#nullable disable
using HarmonyLib;
using MelonLoader;
using Silica;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Queen from the Colossal Spawning Cyst ("!queenb", config "alien_queen_build_max", int, default 2)
        //
        // The game ships a complete ConstructionData_Alien_Queen (non-placeable, own construction site, vanilla
        // 12000 / 300 s) that no structure lists; the only Queen source is the Nest's SpawnOnInit. A vanilla client
        // builds its production buttons from its own copy of the structure's private build list, whose index is
        // also the network protocol byte, so no button can be added from the server.
        //
        // This feature borrows the Colossus button instead:
        //  * The Alien commander types !queenb. The team is ARMED and that commander's client alone receives an
        //    override packet (same format as OverrideManager's own) that sets ResourceCost / BuildUpTime of
        //    ConstructionData_Alien_Colossus to the Queen's values, so the Colossus card shows the Queen's price and
        //    time and the client's own resource check uses the Queen's price. The server's asset is not touched.
        //  * The next Colossus order of that team (Structure.Construct prefix) is taken over: the server runs the
        //    vanilla checks against the QUEEN's ConstructionData (logistics, prerequisites, resources, exit clear),
        //    charges the Queen's cost and starts the game's own non-placeable production with the Queen's data
        //    (private Structure.ConstructNonPlaceable). The Queen is not in the cyst's list, so the game's own RPC
        //    (IndexOf = -1) stays silent; the mod then sends the vanilla RPC for the Colossus slot, so every client
        //    shows a production in progress on that cyst. Progress travels as a 0..1 fraction, so the bar is right
        //    whatever the client believes the total time is.
        //  * GROWING until that construction site ends. Cancel (vanilla button) refunds the Queen's cost through
        //    the game's own CancelCurrent. While growing, further Colossus orders of the team are refused, because
        //    the commander's card still shows the Queen's values. When the site ends (born, cancelled, cyst lost)
        //    the commander's client gets the server's current Colossus values back and the button builds a Colossus
        //    again.
        // Queen cost / build time / tier are the "Queen" unit's cost_mult / build_time_mult / min_tier (read live
        // from the Queen's ConstructionData). At most alien_queen_build_max Queens per team, alive plus growing
        // (0 = the command is off). A second Queen is a second Critical unit: the team is only lost when all die.
        // Limits a server cannot remove: the client decides whether the Colossus button exists and is enabled
        // (its tech tier, cap space for one Colossus), and counts a growing "Colossus" against its own cap display.
        // Hint: when an Alien team's first Colossal Spawning Cyst of the round is finished (its construction site
        // completes) and the feature is on, the commander gets a chat line that names !queenb.
        // =============================================

        private static int _alienQueenBuildMax = 2;                       // config "alien_queen_build_max"
        private const int QueenBuildMaxLimit = 10;
        private const float QueenBuildArmSeconds = 120f;                  // an unused !queenb disarms itself
        private const string QueenInfoName = "ObjectInfo_Alien_Queen";
        private const string ColossusInfoName = "ObjectInfo_Alien_Colossus";

        private enum QueenBuildPhase { Armed, Growing }

        private sealed class QueenBuildState
        {
            public QueenBuildPhase Phase;
            public Team Team;
            public Player Commander;        // the client whose Colossus card shows the Queen's values
            public Structure Cyst;
            public ConstructionSite Site;
            public int Seq;
        }

        private static readonly Dictionary<int, QueenBuildState> _queenBuilds = new Dictionary<int, QueenBuildState>(); // by team index
        private static int _queenBuildSeq;
        private static readonly Dictionary<int, float> _queenBuildLastWarn = new Dictionary<int, float>();

        private static ObjectInfo _qbQueenInfo;
        private static ConstructionData _qbQueenCd;
        private static ConstructionData _qbColossusCd;
        private static MethodInfo _qbConstructNonPlaceable;
        private static MethodInfo _qbRpcConstructNonPlaceable;
        private static MethodInfo _qbSendServerPacket;
        private static bool _qbPatched;
        private static readonly HashSet<int> _queenBuildHinted = new HashSet<int>(); // team index: !queenb hint shown this round

        private static int ClampQueenBuildMax(int n) => n < 2 ? 0 : n > QueenBuildMaxLimit ? QueenBuildMaxLimit : n;
        private static string FmtQueenBuildMax(int n) => n < 2 ? "OFF" : n.ToString(CultureInfo.InvariantCulture);

        /// <summary>Harmony patches of this feature. Called after the other Structure.Construct prefixes are
        /// registered, so the count caps and the per-Nest logistics rule see the Colossus order first.</summary>
        private static void PatchQueenBuild(HarmonyLib.Harmony harmony, MethodInfo construct)
        {
            try
            {
                _qbConstructNonPlaceable = AccessTools.Method(typeof(Structure), "ConstructNonPlaceable", new[] { typeof(ConstructionData) });
                _qbRpcConstructNonPlaceable = AccessTools.Method(typeof(Structure), "RPC_ConstructNonPlaceable", new[] { typeof(ConstructionData) });
                _qbSendServerPacket = typeof(NetworkLayer).GetMethod("SendServerPacket", BindingFlags.Static | BindingFlags.NonPublic);
                var siteEnd = AccessTools.Method(typeof(Structure), "OnConstructionSiteDeinitialized");
                if (construct == null || _qbConstructNonPlaceable == null || _qbRpcConstructNonPlaceable == null || siteEnd == null)
                {
                    MelonLogger.Warning("[QUEENBUILD] game methods not found (Construct / ConstructNonPlaceable / RPC_ConstructNonPlaceable / OnConstructionSiteDeinitialized) — !queenb inactive");
                    return;
                }
                harmony.Patch(construct, prefix: new HarmonyMethod(typeof(Patch_QueenBuild), "Prefix"));
                harmony.Patch(siteEnd, prefix: new HarmonyMethod(typeof(Patch_QueenBuildSiteEnd), "Prefix"));
                if (_sendPlayerOverridesMethod != null)
                    harmony.Patch(_sendPlayerOverridesMethod, postfix: new HarmonyMethod(typeof(Patch_QueenBuildResend), "Postfix"));
                _qbPatched = true;
                var siteDeinit = AccessTools.Method(typeof(ConstructionSite), "Deinit", new[] { typeof(bool) });
                if (siteDeinit != null)
                    harmony.Patch(siteDeinit, prefix: new HarmonyMethod(typeof(Patch_QueenBuildCystBuilt), "Prefix"));
                else
                    MelonLogger.Warning("[QUEENBUILD] ConstructionSite.Deinit not found — no !queenb hint when the first Colossal Spawning Cyst is finished");
                MelonLogger.Msg("Patched Structure.Construct / OnConstructionSiteDeinitialized for !queenb (Queen from the Colossal Spawning Cyst)"
                    + (_qbSendServerPacket == null ? " — NetworkLayer.SendServerPacket not found: the commander's card keeps the Colossus price" : ""));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QUEENBUILD] Failed to patch: {ex.Message}");
            }
        }

        private static bool QueenBuildResolve()
        {
            if (_qbQueenCd != null && _qbColossusCd != null && _qbQueenInfo != null) return true;
            var q = FindObjectInfoByName(QueenInfoName);
            var c = FindObjectInfoByName(ColossusInfoName);
            _qbQueenInfo = q;
            _qbQueenCd = q != null ? q.ConstructionData : null;
            _qbColossusCd = c != null ? c.ConstructionData : null;
            return _qbQueenCd != null && _qbColossusCd != null && q.Prefab != null;
        }

        private static bool QueenBuildIsAlienTeam(Team team)
        {
            ObjectInfo bs = null;
            try { bs = team.BaseStructure; } catch { }
            return bs != null && bs.name == AlienNestInfoName;
        }

        private static Player QueenBuildCommander(Team team)
        {
            try { return (GameMode.CurrentGameMode as GameModeExt)?.GetCommanderForTeam(team); }
            catch { return null; }
        }

        /// <summary>Finished structures of the team that list the Colossus (the Colossal Spawning Cyst).</summary>
        private static int QueenBuildCountCysts(Team team)
        {
            int n = 0;
            var list = team.Structures;
            if (list == null) return 0;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null || s.IsDestroyed) continue;
                var opts = s.ConstructionOptions;
                if (opts != null && opts.Contains(_qbColossusCd)) n++;
            }
            return n;
        }

        private static int QueenBuildCountQueens(Team team, int teamIndex)
        {
            int n = 0;
            try { n = team.GetUnitCount(_qbQueenInfo); } catch { }
            if (_queenBuilds.TryGetValue(teamIndex, out var st) && st.Phase == QueenBuildPhase.Growing) n++;
            return n;
        }

        // ---- the commander's Colossus card ----

        /// <summary>What the commander's client shows on the Colossus card: the Queen's values while a state is
        /// active, else the server's current Colossus values (which include this server's balance overrides).</summary>
        private static void QueenBuildCardValues(bool queen, out int cost, out float buildUp)
        {
            if (!queen)
            {
                cost = _qbColossusCd.ResourceCost;
                buildUp = _qbColossusCd.BuildUpTime;
                return;
            }
            cost = _qbQueenCd.ResourceCost;
            // The client shows (BuildUpTime + FinishedWaitTime + CleanUpTime) x its time multiplier for the Colossus.
            float mult = _qbColossusCd.ConstructionTimeMultiplier;
            if (mult <= 0f) mult = 1f;
            buildUp = Mathf.Max(0.5f, _qbQueenCd.TotalConstructionTime / mult - _qbColossusCd.FinishedWaitTime - _qbColossusCd.CleanUpTime);
        }

        /// <summary>One override packet (the format of NetworkLayer.SendOverride) to ONE player: ResourceCost and
        /// BuildUpTime of the Colossus ConstructionData. The server's own asset stays as it is.</summary>
        private static bool QueenBuildSendCard(Player to, bool queen, string why)
        {
            try
            {
                if (to == null || _qbSendServerPacket == null || _qbColossusCd == null || _qbQueenCd == null) return false;
                if (Player.Players == null || !Player.Players.Contains(to)) return false; // left the server
                QueenBuildCardValues(queen, out int cost, out float buildUp);
                var w = GameByteStreamWriter.GetGameByteStreamWriter(0u, "UnitBalance::QueenBuildCard");
                w.WriteByte((byte)ENetworkPacketType.Overrides);
                w.WriteUInt16(1);
                w.WriteString("A:" + _qbColossusCd.name + ".asset");
                w.WriteUInt16(2);
                w.WriteString("ResourceCost");
                w.WriteByte((byte)OverrideManager.EOverrideDataType.Int);
                w.WriteInt32(cost);
                w.WriteString("BuildUpTime");
                w.WriteByte((byte)OverrideManager.EOverrideDataType.Float);
                w.WriteFloat(buildUp);
                byte[] data = w.GetByteData();
                uint size = (uint)w.GetByteDataSize();
                _qbSendServerPacket.Invoke(null, new object[] { to.PlayerID, data, size, ENetworkPacketSend.Reliable, to.PlayerChannel });
                LogDebug($"[QUEENBUILD] card -> {GetPlayerName(to)}: Colossus cost {cost}, build-up {buildUp:F1} s ({(queen ? "Queen values" : "Colossus values")}, {why})");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QUEENBUILD] sending the card values failed ({why}): {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        // ---- state ----

        private static bool QueenBuildGrowingValid(QueenBuildState st)
        {
            try
            {
                return st.Cyst != null && !st.Cyst.IsDestroyed && st.Site != null && st.Cyst.ProductionConstructionSite == st.Site;
            }
            catch { return false; }
        }

        /// <summary>Leave the Armed / Growing state: the commander's card gets the Colossus values back.</summary>
        private static void QueenBuildEnd(int teamIndex, string why, string chat)
        {
            if (!_queenBuilds.TryGetValue(teamIndex, out var st)) return;
            _queenBuilds.Remove(teamIndex);
            if (_qbColossusCd != null) QueenBuildSendCard(st.Commander, false, why);
            string tn = "team " + teamIndex;
            try { tn = st.Team.TeamShortName; } catch { }
            MelonLogger.Msg($"[QUEENBUILD] {tn}: {why} — Colossus button back to Colossus");
            if (chat == null) return;
            Player now = null;
            try { now = QueenBuildCommander(st.Team); } catch { }
            if (st.Commander != null) SendChatToPlayer(st.Commander, _chatPrefix + chat);
            if (now != null && now != st.Commander) SendChatToPlayer(now, _chatPrefix + chat);
        }

        /// <summary>Round over / new round: clients keep override values across a same-map restart, so every card
        /// that still shows the Queen is put back.</summary>
        private static void QueenBuildResetRound(string why)
        {
            _queenBuildHinted.Clear();
            if (_queenBuilds.Count == 0) return;
            foreach (int key in new List<int>(_queenBuilds.Keys))
            {
                try { QueenBuildEnd(key, why, null); }
                catch (Exception ex) { MelonLogger.Warning($"[QUEENBUILD] reset ({why}) failed: {ex.Message}"); }
            }
            _queenBuilds.Clear();
        }

        private static IEnumerator QueenBuildArmTimeout(int teamIndex, int seq)
        {
            yield return new WaitForSeconds(QueenBuildArmSeconds);
            if (_queenBuilds.TryGetValue(teamIndex, out var st) && st.Seq == seq && st.Phase == QueenBuildPhase.Armed)
                QueenBuildEnd(teamIndex, "!queenb not used within " + QueenBuildArmSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s",
                    "<color=#FFAA00>Queen order expired</color> " + _dimColor + "— the Colossus button builds a Colossus again. Type !queenb to arm it again.</color>");
        }

        private static void QueenBuildWarn(int teamIndex, Team team, string text, bool sound = true)
        {
            float now = Time.realtimeSinceStartup;
            if (_queenBuildLastWarn.TryGetValue(teamIndex, out float last) && now - last < 3f) return;
            _queenBuildLastWarn[teamIndex] = now;
            var commander = QueenBuildCommander(team);
            if (commander == null) return;
            SendChatToPlayer(commander, _chatPrefix + text);
            if (sound) PlaySoundToPlayer(commander, NEST_REACH_SOUND);
        }

        private static string QueenBuildPriceText()
        {
            return _valueColor + _qbQueenCd.ResourceCost.ToString(CultureInfo.InvariantCulture) + "</color>, "
                + _valueColor + _qbQueenCd.TotalConstructionTime.ToString("F0", CultureInfo.InvariantCulture) + " s</color>";
        }

        // ---- "!queenb" (player command: the Alien commander arms / disarms the Queen order) ----

        private static void OnQueenBuildCommand(object playerObj, string args)
        {
            try
            {
                var player = playerObj as Player;
                if (player == null) return;
                if (!_enabled || _alienQueenBuildMax < 2 || !_qbPatched)
                {
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Building a Queen is switched off on this server.</color>");
                    return;
                }
                if (!Game.GetIsServer()) return;
                Team team = player.Team;
                if (team == null || !QueenBuildIsAlienTeam(team) || QueenBuildCommander(team) != player)
                {
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>!queenb is for the Alien commander.</color>");
                    return;
                }
                if (!QueenBuildResolve())
                {
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>The Queen's build data was not found in this game version.</color>");
                    return;
                }
                int ti = Team.Teams.IndexOf(team);
                if (ti < 0) return;

                if (_queenBuilds.TryGetValue(ti, out var st))
                {
                    if (st.Phase == QueenBuildPhase.Growing && QueenBuildGrowingValid(st))
                    {
                        int pct = Mathf.RoundToInt(Mathf.Clamp01(st.Site.ProgressTime01) * 100f);
                        SendChatToPlayer(player, _chatPrefix + "A Queen is growing (" + _valueColor + pct + "%</color>). " + _dimColor
                            + "The Colossus button is back when she is born; cancel the production on that cyst to abort (full refund).</color>");
                        return;
                    }
                    if (st.Phase == QueenBuildPhase.Armed)
                    {
                        QueenBuildEnd(ti, "disarmed by " + GetPlayerName(player),
                            "Queen order cancelled " + _dimColor + "— the Colossus button builds a Colossus again.</color>");
                        return;
                    }
                    QueenBuildEnd(ti, "stale growing state cleared", null);
                }

                int queens = QueenBuildCountQueens(team, ti);
                if (queens >= _alienQueenBuildMax)
                {
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Queen limit reached</color> " + _dimColor + "(" + queens + " of " + _alienQueenBuildMax + ").</color>");
                    return;
                }
                if (QueenBuildCountCysts(team) == 0)
                {
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>A Queen grows in a Colossal Spawning Cyst</color> " + _dimColor + "— build one first.</color>");
                    return;
                }

                st = new QueenBuildState { Phase = QueenBuildPhase.Armed, Team = team, Commander = player, Seq = ++_queenBuildSeq };
                _queenBuilds[ti] = st;
                bool cardSent = QueenBuildSendCard(player, true, "armed");
                MelonCoroutines.Start(QueenBuildArmTimeout(ti, st.Seq));
                MelonLogger.Msg($"[QUEENBUILD] {team.TeamShortName}: armed by {GetPlayerName(player)} ({GetPlayerSteamId(player)}) — Queen cost {_qbQueenCd.ResourceCost}, {_qbQueenCd.TotalConstructionTime:F0} s, Queens {queens} of {_alienQueenBuildMax}");

                SendChatToPlayer(player, _chatPrefix + "<color=#55FF55>Queen order armed:</color> the Colossus button of a Colossal Spawning Cyst now grows a Queen (" + QueenBuildPriceText() + ").");
                string hint = cardSent ? "Select the cyst again to see the Queen's price on the Colossus card. " : "The card still shows the Colossus price; the Queen's price is charged. ";
                if (team.TechnologyTier < _qbColossusCd.MinimumTeamTier)
                    hint += "The Colossus button needs tech tier " + _qbColossusCd.MinimumTeamTier.ToString(CultureInfo.InvariantCulture) + ". ";
                SendChatToPlayer(player, _chatPrefix + _dimColor + hint + "!queenb again cancels; it expires after "
                    + QueenBuildArmSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s.</color>");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QUEENBUILD] !queenb failed: {ex.Message}");
            }
        }

        // ---- the Colossus order of an armed team ----

        private static class Patch_QueenBuild
        {
            public static bool Prefix(Structure __instance, ConstructionData constructionData, bool isClientRequest, ref ProductionActionResult __result)
            {
                try
                {
                    if (_queenBuilds.Count == 0) return true;
                    if (__instance == null || constructionData == null || _qbColossusCd == null || constructionData != _qbColossusCd) return true;
                    if (!Game.GetIsServer()) return true;
                    var team = __instance.Team;
                    if (team == null) return true;
                    int ti = Team.Teams.IndexOf(team);
                    if (!_queenBuilds.TryGetValue(ti, out var st)) return true;
                    return QueenBuildOnColossusOrder(__instance, team, ti, st, isClientRequest, ref __result);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[QUEENBUILD] Construct prefix error: {ex.InnerException?.Message ?? ex.Message}");
                    return true;
                }
            }
        }

        /// <summary>False = handled here (vanilla Construct skipped), true = let the Colossus through.</summary>
        private static bool QueenBuildOnColossusOrder(Structure cyst, Team team, int ti, QueenBuildState st, bool isClientRequest, ref ProductionActionResult result)
        {
            if (st.Phase == QueenBuildPhase.Growing)
            {
                if (!QueenBuildGrowingValid(st))
                {
                    QueenBuildEnd(ti, "growing state was stale", null);
                    return true;
                }
                result = ProductionActionResult.QueueFull;
                int pct = Mathf.RoundToInt(Mathf.Clamp01(st.Site.ProgressTime01) * 100f);
                QueenBuildWarn(ti, team, "<color=#FFAA00>A Queen is growing (" + pct + "%)</color> " + _dimColor
                    + "— the Colossus button is back when she is born. Cancel the production on that cyst to abort.</color>");
                return false;
            }

            // Armed: the vanilla checks of Structure.Construct, against the Queen's data.
            if (cyst.IsDestroyed) { result = ProductionActionResult.UnmetPrerequisite; return false; }
            if (!cyst.ConstructionOptions.Contains(_qbColossusCd)) return true; // not a Colossus producer: vanilla answers InvalidData

            int queens = QueenBuildCountQueens(team, ti);
            if (queens >= _alienQueenBuildMax)
            {
                result = ProductionActionResult.UnitCap;
                QueenBuildEnd(ti, "Queen limit reached before the order",
                    "<color=#FF5555>Queen limit reached</color> " + _dimColor + "(" + queens + " of " + _alienQueenBuildMax + ") — the Colossus button builds a Colossus again.</color>");
                return false;
            }
            if (team.GetNeedConstructionLogistics())
            {
                result = ProductionActionResult.MissingLogistics;
                QueenBuildWarn(ti, team, "<color=#FF5555>No Queen docked</color> " + _dimColor + "— a Queen must sit in a Nest before another can grow.</color>");
                return false;
            }
            if (!team.GetHasPrerequisitesForConstruction(_qbQueenCd))
            {
                result = ProductionActionResult.UnmetPrerequisite;
                string need = _qbQueenCd.MinimumTeamTier > -1 ? " (tech tier " + _qbQueenCd.MinimumTeamTier.ToString(CultureInfo.InvariantCulture) + ")" : "";
                QueenBuildWarn(ti, team, "<color=#FF5555>The Queen's requirements are not met</color>" + _dimColor + need + ".</color>");
                return false;
            }
            int cost = _qbQueenCd.ResourceCost;
            if (cost > 0 && team.TotalResources < cost)
            {
                result = ProductionActionResult.InsufficientResource;
                QueenBuildWarn(ti, team, "<color=#FF5555>Not enough resources for a Queen</color> " + _dimColor + "(" + team.TotalResources + " of " + cost + ").</color>");
                return false;
            }
            if (cyst.ProductionConstructionSite != null || cyst.ProductionQueue.Count > 0)
            {
                result = ProductionActionResult.QueueFull;
                QueenBuildWarn(ti, team, "<color=#FF5555>This cyst is busy</color> " + _dimColor + "— a Queen needs an idle Colossal Spawning Cyst (wait, cancel its production, or use another).</color>");
                return false;
            }
            if (!cyst.GetAreaClearForConstruction(_qbQueenCd))
            {
                result = ProductionActionResult.AreaObstructed;
                QueenBuildWarn(ti, team, "<color=#FF5555>The cyst's exit is blocked</color> " + _dimColor + "— move the units away and order again.</color>");
                return false;
            }

            if (cost > 0) team.RetrieveResource(cost);
            float total = _qbQueenCd.TotalConstructionTime;
            _qbConstructNonPlaceable.Invoke(cyst, new object[] { _qbQueenCd });
            var site = cyst.ProductionConstructionSite;
            if (site == null)
            {
                if (total <= 0f)
                {
                    // build time 0: the game spawned her at once
                    result = ProductionActionResult.Success;
                    QueenBuildEnd(ti, "Queen born at once (build time 0)", "<color=#55FF55>A Queen is born.</color> " + _dimColor + "The Colossus button builds a Colossus again.</color>");
                    return false;
                }
                if (cost > 0) team.StartingResources += cost;
                result = ProductionActionResult.UnspecifiedFailure;
                MelonLogger.Warning($"[QUEENBUILD] {team.TeamShortName}: the game did not start the Queen's construction site — {cost} refunded");
                QueenBuildEnd(ti, "construction site request failed", "<color=#FF5555>The Queen could not be started</color> " + _dimColor + "(refunded). The Colossus button builds a Colossus again.</color>");
                return false;
            }

            // Every client: the vanilla "production started" RPC for the Colossus slot of this cyst.
            try { _qbRpcConstructNonPlaceable.Invoke(cyst, new object[] { _qbColossusCd }); }
            catch (Exception rex) { MelonLogger.Warning($"[QUEENBUILD] production-start RPC failed: {rex.InnerException?.Message ?? rex.Message}"); }

            st.Phase = QueenBuildPhase.Growing;
            st.Cyst = cyst;
            st.Site = site;
            st.Seq = ++_queenBuildSeq; // the arm timeout no longer applies
            result = ProductionActionResult.Success;
            MelonLogger.Msg($"[QUEENBUILD] {team.TeamShortName}: Queen growing in {(cyst.ObjectInfo != null ? cyst.ObjectInfo.DisplayName : "cyst")} at "
                + $"({cyst.transform.position.x:F0}, {cyst.transform.position.z:F0}) — {cost} charged, {total:F0} s{(isClientRequest ? " (client request)" : "")}");
            var commander = QueenBuildCommander(team);
            if (commander != null)
                SendChatToPlayer(commander, _chatPrefix + "<color=#55FF55>A Queen is growing</color> (" + QueenBuildPriceText() + "). " + _dimColor
                    + "The cyst shows it on the Colossus slot; cancelling it refunds the full price.</color>");
            return false;
        }

        // ---- the Queen's construction site ends (born / cancelled / cyst lost) ----

        private static class Patch_QueenBuildSiteEnd
        {
            // Prefix: the card values go out before the game's own "production ended" RPC, which makes the
            // commander's client rebuild the production panel.
            public static void Prefix(Structure __instance, ConstructionSite constructionSite, bool wasCompleted)
            {
                try
                {
                    if (_queenBuilds.Count == 0 || constructionSite == null) return;
                    int found = -1;
                    foreach (var kv in _queenBuilds)
                        if (kv.Value.Phase == QueenBuildPhase.Growing && kv.Value.Site == constructionSite && kv.Value.Cyst == __instance) { found = kv.Key; break; }
                    if (found < 0) return;
                    if (wasCompleted)
                        QueenBuildEnd(found, "Queen born", "<color=#55FF55>A Queen is born.</color> " + _dimColor
                            + "She needs a Nest of her own to dock in. The Colossus button builds a Colossus again.</color>");
                    else
                        QueenBuildEnd(found, "Queen production ended without a Queen (cancelled or cyst lost)", "<color=#FFAA00>The Queen's production ended</color> " + _dimColor
                            + "(cancelled or cyst lost). The Colossus button builds a Colossus again.</color>");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[QUEENBUILD] site-end prefix error: {ex.Message}");
                }
            }
        }

        // ---- hint: the team's first Colossal Spawning Cyst of the round is finished ----

        private static class Patch_QueenBuildCystBuilt
        {
            // ConstructionSite.Deinit(true) is the completion of every construction (placed structure or produced
            // unit); the finished object is already spawned at this point.
            public static void Prefix(ConstructionSite __instance, bool wasCompleted)
            {
                try
                {
                    if (!wasCompleted || __instance == null || __instance.IsDeconstructing) return;
                    if (!_enabled || _alienQueenBuildMax < 2 || !_qbPatched) return;
                    var cd = __instance.ConstructionData;
                    if (cd == null || cd.ObjectInfo == null || cd.ObjectInfo.name != ColossalCystInfoName) return;
                    QueenBuildCystFinished(__instance.Team);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[QUEENBUILD] cyst-built prefix error: {ex.Message}");
                }
            }
        }

        /// <summary>Once per team and round: tell the commander that this cyst can grow a Queen. Without a player
        /// commander at that moment nothing is marked, so the next finished cyst tries again.</summary>
        private static void QueenBuildCystFinished(Team team)
        {
            if (team == null || !Game.GetIsServer() || !QueenBuildIsAlienTeam(team)) return;
            int ti = Team.Teams.IndexOf(team);
            if (ti < 0 || _queenBuildHinted.Contains(ti)) return;
            if (!QueenBuildResolve()) return;
            var commander = QueenBuildCommander(team);
            if (commander == null)
            {
                MelonLogger.Msg($"[QUEENBUILD] {team.TeamShortName}: Colossal Spawning Cyst finished, no player commander — the !queenb hint waits for the next cyst");
                return;
            }
            _queenBuildHinted.Add(ti);
            int queens = QueenBuildCountQueens(team, ti);
            SendChatToPlayer(commander, _chatPrefix + "<color=#55FF55>Colossal Spawning Cyst ready:</color> it can also grow another Queen. Type "
                + _valueColor + "!queenb</color> in chat, then press its Colossus button (" + QueenBuildPriceText() + ").");
            string more = "Queens " + queens + " of " + _alienQueenBuildMax + ". The cyst must be idle; cancelling refunds the full price.";
            if (team.TechnologyTier < _qbColossusCd.MinimumTeamTier)
                more += " The Colossus button appears at tech tier " + _qbColossusCd.MinimumTeamTier.ToString(CultureInfo.InvariantCulture) + ".";
            SendChatToPlayer(commander, _chatPrefix + _dimColor + more + "</color>");
            MelonLogger.Msg($"[QUEENBUILD] {team.TeamShortName}: first Colossal Spawning Cyst finished — !queenb hint sent to {GetPlayerName(commander)}");
        }

        // A full override table sent to the commander (round start, !rebalance, menu save) carries the server's
        // Colossus values: put the Queen's values back on the card while the state lasts.
        private static class Patch_QueenBuildResend
        {
            public static void Postfix(Player __0)
            {
                try
                {
                    if (_inChunkedSend || _queenBuilds.Count == 0 || __0 == null) return;
                    foreach (var st in _queenBuilds.Values)
                        if (st.Commander == __0) { QueenBuildSendCard(__0, true, "after a full override sync"); break; }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[QUEENBUILD] resend postfix error: {ex.Message}");
                }
            }
        }

        // ---- self-test ("debug_queen_build_selftest": true, JSON only) ----
        // For a headless staging run without a commander: 20 s into the round the first Alien team in play gets a
        // Colossal Spawning Cyst next to its Nest (if it has none) and the Queen's price, is armed without a
        // commander, and the Colossus is ordered through Structure.Construct like a client request would be. The
        // log shows the result of every step: order taken over, a second order refused while growing, the site
        // fast-forwarded to its end, the Queen count afterwards, the limit refusing a third Queen. The cyst is built
        // through a real (fast-forwarded) construction site, so the "first cyst finished" hint hook runs as well.

        private static bool _debugQueenBuildSelfTest;   // config "debug_queen_build_selftest"
        private const string ColossalCystInfoName = "ObjectInfo_Alien_ColossalSpawningCyst";

        private static void QueenBuildSelfTestSchedule(string why)
        {
            if (!_debugQueenBuildSelfTest || !_enabled || !_qbPatched) return;
            try { MelonCoroutines.Start(QueenBuildSelfTestRun(why)); }
            catch (Exception ex) { MelonLogger.Warning($"[QUEENBUILD-TEST] could not start: {ex.Message}"); }
        }

        private static Structure QueenBuildFindCyst(Team team)
        {
            var list = team.Structures;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && !s.IsDestroyed && s.ConstructionOptions != null && s.ConstructionOptions.Contains(_qbColossusCd)) return s;
            }
            return null;
        }

        private static IEnumerator QueenBuildSelfTestRun(string why)
        {
            const string T = "[QUEENBUILD-TEST] ";
            yield return new WaitForSeconds(20f);
            Team team = null; Structure nest = null; int ti = -1;
            try
            {
                if (!Game.GetIsServer() || !QueenBuildResolve()) { MelonLogger.Warning(T + "not a server or Queen/Colossus data missing"); yield break; }
                for (int i = 0; i < Team.Teams.Count && team == null; i++)
                {
                    var t = Team.Teams[i];
                    if (t == null || !QueenBuildIsAlienTeam(t)) continue;
                    var n = FindStarterNest(t, t.BaseStructure);
                    if (n != null) { team = t; nest = n; ti = i; }
                }
            }
            catch (Exception ex) { MelonLogger.Warning(T + "setup failed: " + ex.Message); yield break; }
            if (team == null) { MelonLogger.Warning(T + "no Alien team with a Nest in this round"); yield break; }

            MelonLogger.Msg(T + $"start ({why}): {team.TeamShortName}, Queens {QueenBuildCountQueens(team, ti)}, limit {_alienQueenBuildMax}, Queen cost {_qbQueenCd.ResourceCost}, "
                + $"time {_qbQueenCd.TotalConstructionTime:F0} s, Colossus cost {_qbColossusCd.ResourceCost}, time {_qbColossusCd.TotalConstructionTime:F0} s, logistics needed {team.GetNeedConstructionLogistics()}");

            Structure cyst = QueenBuildFindCyst(team);
            if (cyst == null)
            {
                try
                {
                    var info = FindObjectInfoByName(ColossalCystInfoName);
                    Vector3 pos = nest.transform.position + nest.transform.forward * 60f;
                    var terrain = Game.MainTerrain;
                    if (terrain != null) pos.y = terrain.SampleHeight(pos) + terrain.transform.position.y;
                    var cystCd = info != null ? info.ConstructionData : null;
                    var csite = cystCd != null ? cystCd.RequestConstructionSite(nest, pos, nest.transform.rotation, team) : null;
                    if (csite != null) csite.ProgressTime = Mathf.Max(0.1f, csite.TotalConstructionTime - 1f);
                    MelonLogger.Msg(T + (csite != null ? $"Colossal Spawning Cyst site placed at {P(pos)}, fast-forwarded to 1 s before its end" : "could not place a Colossal Spawning Cyst site"));
                }
                catch (Exception ex) { MelonLogger.Warning(T + "cyst site threw: " + ex.Message); }
                yield return new WaitForSeconds(4f);
                cyst = QueenBuildFindCyst(team);
                MelonLogger.Msg(T + (cyst != null ? "the cyst is finished" : "no finished cyst after 4 s") + $"; hint shown to a commander this round: {_queenBuildHinted.Contains(ti)}");
                if (cyst == null) yield break;
            }

            ConstructionSite site = null;
            try
            {
                var alien = cyst as AlienStructure;
                MelonLogger.Msg(T + $"cyst: linked {(alien != null ? alien.IsLinkedToMainBuilding.ToString() : "n/a")}, functional {cyst.IsFunctional}, producing {cyst.IsProducing}, queue {cyst.ProductionQueue.Count}, "
                    + $"options [{string.Join(", ", cyst.ConstructionOptions.ConvertAll(c => c != null ? c.name : "null"))}]");
                int cost = _qbQueenCd.ResourceCost;
                team.StartingResources += cost;
                _queenBuilds[ti] = new QueenBuildState { Phase = QueenBuildPhase.Armed, Team = team, Commander = null, Seq = ++_queenBuildSeq };
                if (cyst.IsProducing || cyst.ProductionQueue.Count > 0)
                {
                    // an AI commander may already use the cyst: a busy cyst must refuse, then it is emptied for the test
                    var r0 = cyst.Construct(_qbColossusCd);
                    MelonLogger.Msg(T + $"order 0 (armed, busy cyst): result {r0} (expected QueueFull), state {(_queenBuilds.ContainsKey(ti) ? "still armed" : "CLEARED")}");
                    cyst.CancelQueuedAll();
                    cyst.CancelCurrent();
                }
                int before = team.TotalResources;
                var r1 = cyst.Construct(_qbColossusCd);
                site = cyst.ProductionConstructionSite;
                bool growing = _queenBuilds.TryGetValue(ti, out var st) && st.Phase == QueenBuildPhase.Growing;
                MelonLogger.Msg(T + $"order 1 (armed): result {r1}, state {(growing ? "Growing" : _queenBuilds.ContainsKey(ti) ? "Armed" : "none")}, site data {(site != null && site.ConstructionData != null ? site.ConstructionData.name : "none")}, "
                    + $"site total {(site != null ? site.TotalConstructionTime : 0f):F0} s, charged {before - team.TotalResources}, Queens (alive + growing) {QueenBuildCountQueens(team, ti)}");
                if (!growing || site == null) { QueenBuildEnd(ti, "self-test: order was not taken over", null); yield break; }
                var r2 = cyst.Construct(_qbColossusCd);
                MelonLogger.Msg(T + $"order 2 (while growing): result {r2} (expected QueueFull), queue {cyst.ProductionQueue.Count} (expected 0)");
                site.ProgressTime = Mathf.Max(0f, site.TotalConstructionTime - 3f);
                MelonLogger.Msg(T + "site fast-forwarded to 3 s before its end");
            }
            catch (Exception ex) { MelonLogger.Warning(T + "order phase threw: " + (ex.InnerException?.Message ?? ex.Message) + "\n" + ex.StackTrace); yield break; }

            yield return new WaitForSeconds(8f);
            try
            {
                int alive = team.GetUnitCount(_qbQueenInfo);
                MelonLogger.Msg(T + $"after the site ended: Queens alive {alive} (expected 2), state {(_queenBuilds.ContainsKey(ti) ? "STILL SET" : "cleared")}, cyst producing {cyst.IsProducing}");
                // limit: a third Queen must be refused at the order
                _queenBuilds[ti] = new QueenBuildState { Phase = QueenBuildPhase.Armed, Team = team, Commander = null, Seq = ++_queenBuildSeq };
                team.StartingResources += _qbQueenCd.ResourceCost;
                int before = team.TotalResources;
                var r3 = cyst.Construct(_qbColossusCd);
                bool atLimit = alive >= _alienQueenBuildMax;
                MelonLogger.Msg(T + $"order 3 (armed, {(atLimit ? "at the limit: expected UnitCap, state cleared, charged 0" : "below the limit " + _alienQueenBuildMax + ": QueueFull on a busy cyst, else taken over")}): "
                    + $"result {r3}, state {(_queenBuilds.ContainsKey(ti) ? "still set" : "cleared")}, charged {before - team.TotalResources}, producing {cyst.IsProducing}");
                if (_queenBuilds.ContainsKey(ti)) QueenBuildEnd(ti, "self-test: state cleared before the unarmed order", null);
                // unarmed: the Colossus slot is vanilla again
                var r4 = cyst.Construct(_qbColossusCd);
                var s4 = cyst.ProductionConstructionSite;
                MelonLogger.Msg(T + $"order 4 (not armed): result {r4}, site data {(s4 != null && s4.ConstructionData != null ? s4.ConstructionData.name : "none")} (a Colossus, or UnmetPrerequisite below its tier; team tier {team.TechnologyTier})");
                MelonLogger.Msg(T + "done");
            }
            catch (Exception ex) { MelonLogger.Warning(T + "check phase threw: " + (ex.InnerException?.Message ?? ex.Message)); }
        }

        // ---- setting: one code path for the chat menu (Strategystyle 8) and "!b queenbuild" ----

        /// <summary>Set the limit, persist it. Immediate. False when it already had that value.</summary>
        private static bool QueenBuildSetMax(object player, int n)
        {
            n = ClampQueenBuildMax(n);
            if (_alienQueenBuildMax == n) return false;
            int old = _alienQueenBuildMax;
            _alienQueenBuildMax = n;
            WriteNumberToJson("alien_queen_build_max", n);
            string pn = GetPlayerName(player), sid = GetPlayerSteamId(player);
            WriteAuditLog(pn, sid, "strategy", "alien_queen_build_max", old.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture));
            MelonLogger.Msg($"[BAL] {pn} ({sid}): alien_queen_build_max -> {n}");
            if (n < 2)
            {
                // switched off: pending orders go, a Queen already growing is finished
                foreach (int key in new List<int>(_queenBuilds.Keys))
                    if (_queenBuilds[key].Phase == QueenBuildPhase.Armed)
                        QueenBuildEnd(key, "switched off", "<color=#FFAA00>Building a Queen was switched off</color> " + _dimColor + "— the Colossus button builds a Colossus again.</color>");
            }
            return true;
        }

        private static string QueenBuildStatusLine()
        {
            int armed = 0, growing = 0;
            foreach (var st in _queenBuilds.Values) { if (st.Phase == QueenBuildPhase.Armed) armed++; else growing++; }
            string v = _alienQueenBuildMax >= 2 ? "<color=#55FF55>" + _alienQueenBuildMax + "</color>" : "<color=#FF5555>OFF</color>";
            return "Queen build limit (!queenb): " + v + " · " + armed + " armed, " + growing + " growing";
        }

        private const string QueenBuildHelp = "The Alien commander types !queenb; the next Colossus order at a Colossal Spawning Cyst grows a Queen instead. "
            + "Limit = Queens per team (alive + growing). Price / time / tier: the Queen unit's cost_mult, build_time_mult, min_tier.";

        // "!b queenbuild [status | off | 2-10]" — stateless, for the ModUI row and chat (AdminMod's !b power applies).
        private static void OnQueenBuildSettingCommand(object player, string args)
        {
            string sub = (args ?? "").Trim().ToLowerInvariant();
            if (sub.Length == 0 || sub == "status")
            {
                SendChatToPlayer(player, _chatPrefix + QueenBuildStatusLine());
                SendChatToPlayer(player, _chatPrefix + _dimColor + QueenBuildHelp + " Set: !b queenbuild off | 2-" + QueenBuildMaxLimit + "</color>");
                return;
            }
            int n;
            if (sub == "off") n = 0;
            else if (!int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0 || n == 1 || n > QueenBuildMaxLimit)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Unknown option: " + sub + ". Use !b queenbuild status, off or 2-" + QueenBuildMaxLimit + "</color>");
                return;
            }
            bool changed = QueenBuildSetMax(player, n);
            SendChatToPlayer(player, _chatPrefix + (changed ? "" : "Already: ") + QueenBuildStatusLine() + " " + _dimColor + "(immediate)</color>");
        }

        // Strategystyle > Queen Build Limit page: ".1 3" sets the limit directly (no confirm step).
        private static void HandleQueenBuildEdit(object player, BalanceMenuState state, int paramIdx, string valueStr)
        {
            if (paramIdx != 1)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Use .1 <n> on this page.</color>");
                return;
            }
            if (!int.TryParse(valueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n == 1 || n > QueenBuildMaxLimit)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid value: " + valueStr + " (0 = off, or 2-" + QueenBuildMaxLimit + ")</color>");
                return;
            }
            bool changed = QueenBuildSetMax(player, n);
            SendChatToPlayer(player, _chatPrefix + (changed ? "<color=#55FF55>Saved:</color> " : "Already: ") + QueenBuildStatusLine() + " " + _dimColor + "(immediate)</color>");
        }
    }
}

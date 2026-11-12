using HarmonyLib;
using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Silica;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Player Cap Exempt (config "player_infantry_ignore_cap", buffer "player_cap_exempt_buffer")
        //
        // Goal: units that players are actively playing (soldiers, lesser creatures) should not use up
        // the team's Secondary unit cap, so the commander can still build when many players are alive.
        //
        // Why not simply subtract them on the server: the cap is checked on the CLIENT first.
        // StrategyMode.Construct runs Structure.Construct locally (isClientRequest:false), which fails
        // HasCapSpaceForUnit from the client's own Team.UnitCapEntries before any RPC is sent, and
        // UnitCapEntry.Current is not networked. The only cap inputs the server can change on a client
        // are the per-unit ObjectInfo.UnitCapValue (an asset member the OverrideManager syncs) and the
        // global unit cap multiplier (one byte for every team and both cap bars).
        //
        // So the exemption is done through UnitCapValue, per team, as a two-state switch:
        //   CLOSED  = the team's player units carry their normal cap value (vanilla behaviour).
        //   OPEN    = their cap value is 0 on server AND clients, so client and server both let
        //             soldier builds through although the standard cap is full.
        // Bookkeeping stays in "true" cap points (the values the units would carry when CLOSED):
        //   S    = standard Secondary cap of the team (CapBase x multiplier)
        //   held = true cap points of the team's player-controlled Secondary units
        //   E    = S + held, the enhanced cap
        //   tc   = true cap points in use (alive + queued + in production), what vanilla would show
        //   CLOSED -> OPEN   when tc >= S - lead (default 5 below the standard cap, so the override is on
        //                    the commander's client before a button greys out) and tc + buffer <= E
        //   OPEN   -> CLOSED when tc + buffer > E (enhanced cap reached) or tc < S - lead - hyst
        //   re-open after a close at the top only once one more point is free (tc + buffer + 1 <= E)
        // With the default buffer 1 that is "open while S - 5 <= tc < E". The server re-validates every
        // build request with its own already-switched values and re-evaluates after each enqueue, so the
        // overshoot past E is at most one cap point (a 2-point unit accepted at E - 1).
        //
        // Client side (7.13.3): UnitCapEntry.Current is a local count the client only refreshes on unit
        // add/remove and on queue RPCs. After a switch it can stay stale (and above Max when players
        // pushed the team over the cap), which keeps the buttons grey although the values arrived. So
        // every switch is followed by a harmless Structure CONSTRUCT_DEQUEUE RPC with index 255 (removes
        // nothing, runs Team.UpdateUnitCapValues on every client), sent at once and again after 0.5 s and
        // 2 s because the override packet and the RPC travel separately. The unit cards cache their cap
        // cost label when they are built (Constructable.CapValue), so a card keeps showing the old cost
        // until the structure is selected again; the cap bar and the button state follow the recount.
        // Evaluated on every Team.UpdateUnitCapValues (unit add/remove, queue changes) and on every
        // Unit.ControlledBy change (player spawns/dies/leaves a unit). Reset at round start/end and
        // before !rebalance, so OM holds the normal values whenever the overrides are (re)applied.
        //
        // Side effects while OPEN: the client's cap panel and the unit cards show the zeroed values
        // (e.g. "4/30" with 40 soldiers alive); the AI commander sees free cap too and builds until the
        // enhanced cap closes the window. Shrimps and other harvesters keep their value at all times.
        // Per-team state keys the ObjectInfo assets of the team's faction, which assumes each faction
        // appears once per match (Sol / Centauri / Alien), as in every vanilla versus mode.
        // Human teams (Sol, Centauri) only: alien teams never open a window.
        //
        // Admin access (7.14.0): chat menu !b > 6 > 2, or the stateless "!b capexempt [status|on|off|buffer N]"
        // that the ModUI admin page uses for its switch, buffer and status rows. Both go through
        // DynCapSetEnabled / DynCapApplyBuffer. Live lines list only teams in play (DynCapTeamInPlay).
        // =============================================

        private static int _playerCapExemptBuffer = 1;     // config "player_cap_exempt_buffer": open while tc + buffer <= E
        private static int _playerCapExemptLead = 5;       // config "player_cap_exempt_lead": open from tc >= S - lead
        private const int DynCapHysteresis = 2;            // bottom close: tc < S - lead - hyst
        private const int DynCapTopHysteresis = 1;         // re-open after a close at the top: one extra free point
        private const int DynCapMaxUnitValue = 2;         // largest Secondary player unit value (Heavy, Shocker, ...)

        private sealed class DynCapTeamState
        {
            public bool Open;
            public bool ClosedAtTop;                                       // last close was "enhanced cap reached"
            public readonly Dictionary<string, int> Originals = new Dictionary<string, int>(); // ObjectInfo.name -> cap value while OPEN
            public bool WarnedNoOM;
            public float LastWhyLog = -999f;                               // throttle for the "stays closed" diagnostic
        }

        private static readonly Dictionary<int, DynCapTeamState> _dynCap = new Dictionary<int, DynCapTeamState>();
        private static bool _inDynCapRecompute; // re-entrancy guard: our own UpdateUnitCapValues calls skip evaluation

        /// <summary>Sol/Centauri: the base structure prefab carries a HumanStructure component.</summary>
        private static bool DynCapIsHumanTeam(Team team)
        {
            try
            {
                return team.BaseStructure != null && team.BaseStructure.Prefab != null
                    && team.BaseStructure.Prefab.GetComponent<HumanStructure>() != null;
            }
            catch { return false; }
        }

        private static DynCapTeamState DynCapState(Team team)
        {
            int key = team.Index;
            if (!_dynCap.TryGetValue(key, out var st))
            {
                st = new DynCapTeamState();
                _dynCap[key] = st;
            }
            return st;
        }

        /// <summary>Cap value the unit carries when the team is CLOSED (i.e. before we zeroed it).</summary>
        private static int DynCapTrueValue(DynCapTeamState st, ObjectInfo info)
        {
            if (st.Open && st.Originals.TryGetValue(info.name, out int orig)) return orig;
            return info.UnitCapValue;
        }

        private static bool DynCapIsSecondary(Team team, ObjectInfo info)
        {
            if (info == null || info.ObjectType != ObjectInfoType.Unit) return false;
            var entry = team.GetUnitTypeCapEntryForUnit(info);
            return entry != null && entry.UnitCapType == UnitCapType.Secondary;
        }

        /// <summary>Player units of the team: Secondary-cap units the commander can build or a player can
        /// spawn as, harvesters excluded. Shared assets of the team's faction.</summary>
        private static List<ObjectInfo> DynCapPlayerUnits(Team team)
        {
            var result = new List<ObjectInfo>();
            var seen = new HashSet<string>();
            void Add(ObjectInfo info)
            {
                if (info == null || info.UnitType == UnitType.Harvester) return;
                if (!DynCapIsSecondary(team, info)) return;
                if (seen.Add(info.name)) result.Add(info);
            }
            try
            {
                if (GameMode.CurrentGameMode is GameModeExt mode && mode.BaseTeamSetups != null)
                {
                    foreach (BaseTeamSetup setup in mode.BaseTeamSetups)
                    {
                        if (setup == null || setup.Team != team) continue;
                        Add(setup.PlayerSpawn);
                        if (setup.PlayerSpawnExt != null)
                            foreach (var info in setup.PlayerSpawnExt) Add(info);
                    }
                }
                var structures = team.Structures;
                for (int i = 0; i < structures.Count; i++)
                {
                    var s = structures[i];
                    if (s == null || s.ConstructionOptions == null) continue;
                    foreach (var cd in s.ConstructionOptions)
                        if (cd != null) Add(cd.ObjectInfo);
                }
                var units = team.Units;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u != null) Add(u.ObjectInfo);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DYNCAP] player unit scan failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>True cap points in use on the Secondary cap (vanilla loop with CLOSED values) and the
        /// part of it that players are controlling.</summary>
        private static void DynCapCount(Team team, DynCapTeamState st, out int trueCurrent, out int held)
        {
            trueCurrent = 0; held = 0;
            var units = team.Units;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.ObjectInfo == null || !DynCapIsSecondary(team, u.ObjectInfo)) continue;
                int v = DynCapTrueValue(st, u.ObjectInfo);
                trueCurrent += v;
                if (u.ControlledBy != null) held += v;
            }
            var structures = team.Structures;
            for (int i = 0; i < structures.Count; i++)
            {
                var s = structures[i];
                if (s == null || s.ProductionQueue == null) continue;
                for (int j = 0; j < s.ProductionQueue.Count; j++)
                {
                    var cd = s.ProductionQueue[j];
                    if (cd == null || cd.ObjectInfo == null || !DynCapIsSecondary(team, cd.ObjectInfo)) continue;
                    trueCurrent += DynCapTrueValue(st, cd.ObjectInfo);
                }
            }
            var sites = ConstructionSite.ConstructionSites;
            for (int i = 0; i < sites.Count; i++)
            {
                var cs = sites[i];
                if (cs == null || cs.Team != team || cs.ObjectInfo == null) continue;
                if (!cs.gameObject.activeSelf || !DynCapIsSecondary(team, cs.ObjectInfo)) continue;
                trueCurrent += DynCapTrueValue(st, cs.ObjectInfo);
            }
        }

        private static bool DynCapSetValue(DynCapTeamState st, ObjectInfo info, int value)
        {
            // Immediate server apply + single-member notify packet to every client (no queue, no
            // full resync). Falls back to a server-only direct set when the OverrideManager is unusable;
            // then only the AI commander and the server-side check see the change.
            if (_omInitialized && _omSetMethod != null)
            {
                try
                {
                    object r = _omSetMethod.Invoke(null, new object[] { _omTypeInt, $"A:{info.name}.asset", "UnitCapValue", value, false, true });
                    if (r is bool ok && ok) return true;
                    MelonLogger.Warning($"[DYNCAP] OM.Set refused UnitCapValue={value} for '{info.name}', setting server-side only");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[DYNCAP] OM.Set UnitCapValue for '{info.name}': {ex.InnerException?.Message ?? ex.Message}");
                }
            }
            else if (!st.WarnedNoOM)
            {
                st.WarnedNoOM = true;
                MelonLogger.Warning("[DYNCAP] OverrideManager not available: cap exemption is server-side only (clients keep vanilla cap values)");
            }
            info.UnitCapValue = value;
            return false;
        }

        private static void DynCapRecompute(Team team)
        {
            _inDynCapRecompute = true;
            try { team.UpdateUnitCapValues(); }
            catch (Exception ex) { MelonLogger.Warning($"[DYNCAP] recompute failed: {ex.Message}"); }
            finally { _inDynCapRecompute = false; }
        }

        private static void DynCapOpen(Team team, DynCapTeamState st, string why)
        {
            var infos = DynCapPlayerUnits(team);
            var sb = new StringBuilder();
            foreach (var info in infos)
            {
                int orig = info.UnitCapValue;
                if (orig <= 0) continue;
                st.Originals[info.name] = orig;
                DynCapSetValue(st, info, 0);
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(info.DisplayName).Append('=').Append(orig);
            }
            st.Open = true;
            st.ClosedAtTop = false;
            DynCapRecompute(team);
            DynCapNudgeAll(team);
            MelonLogger.Msg($"[DYNCAP] {team.TeamShortName}: OPEN ({why}) zeroed {st.Originals.Count} unit(s): {sb}");
        }

        // ---- client recount nudge ----
        private static System.Reflection.MethodInfo _rpcDequeueMethod;
        private static bool _rpcDequeueLooked;

        /// <summary>Make every client recount this team's cap entries: CONSTRUCT_DEQUEUE with an index no
        /// queue has removes nothing and ends in Team.UpdateUnitCapValues on the client.</summary>
        private static bool DynCapNudgeClients(Team team)
        {
            try
            {
                if (!_rpcDequeueLooked)
                {
                    _rpcDequeueLooked = true;
                    _rpcDequeueMethod = AccessTools.Method(typeof(Structure), "RPC_Dequeue", new[] { typeof(int), typeof(bool), typeof(bool) });
                    if (_rpcDequeueMethod == null)
                        MelonLogger.Warning("[DYNCAP] Structure.RPC_Dequeue not found: clients recount only on their next unit/queue change");
                }
                if (_rpcDequeueMethod == null || team == null) return false;
                var structures = team.Structures;
                for (int i = 0; i < structures.Count; i++)
                {
                    var s = structures[i];
                    if (s == null || s.IsDestroyed || s.NetworkComponent == null || !s.gameObject.activeInHierarchy) continue;
                    _rpcDequeueMethod.Invoke(s, new object[] { 255, false, true });
                    return true;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DYNCAP] client recount nudge failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            return false;
        }

        private static void DynCapNudgeAll(Team team)
        {
            if (team == null) return;
            DynCapNudgeClients(team);
            try
            {
                MelonCoroutines.Start(DynCapNudgeLater(team.Index, 0.5f));
                MelonCoroutines.Start(DynCapNudgeLater(team.Index, 2f));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DYNCAP] delayed nudge not scheduled: {ex.Message}");
            }
        }

        private static IEnumerator DynCapNudgeLater(int teamIndex, float delay)
        {
            yield return new WaitForSeconds(delay);
            Team team = null;
            try { team = teamIndex >= 0 && teamIndex < Team.Teams.Count ? Team.Teams[teamIndex] : null; } catch { }
            if (team != null) DynCapNudgeClients(team);
        }

        private static void DynCapClose(Team team, DynCapTeamState st, string why, bool atTop)
        {
            int restored = 0;
            foreach (var kv in st.Originals)
            {
                var info = DynCapFindInfo(team, kv.Key);
                if (info == null) continue;
                DynCapSetValue(st, info, kv.Value);
                restored++;
            }
            st.Originals.Clear();
            st.Open = false;
            st.ClosedAtTop = atTop;
            DynCapRecompute(team);
            DynCapNudgeAll(team);
            MelonLogger.Msg($"[DYNCAP] {team.TeamShortName}: CLOSED ({why}) restored {restored} unit(s)");
        }

        private static ObjectInfo DynCapFindInfo(Team team, string assetName)
        {
            foreach (var info in DynCapPlayerUnits(team))
                if (info.name == assetName) return info;
            // Unit types the team no longer builds/has still need their value back.
            foreach (var info in Resources.FindObjectsOfTypeAll<ObjectInfo>())
                if (info != null && info.name == assetName) return info;
            return null;
        }

        /// <summary>Decide OPEN/CLOSED for one team from the current true counts. Cheap enough to run on
        /// every cap recompute (one pass over units, queues and sites).</summary>
        private static void DynCapEvaluate(Team team)
        {
            if (!_playerInfantryIgnoreCap || team == null || _inDynCapRecompute) return;
            if (!Game.GetIsServer()) return;
            try
            {
                var st = DynCapState(team);
                var entry = team.GetUnitTypeCapEntryForType(UnitCapType.Secondary);
                int S = entry != null ? entry.Max : 0;
                if (S <= 0 || !DynCapIsHumanTeam(team))
                {
                    if (st.Open) DynCapClose(team, st, S <= 0 ? "no standard cap" : "not a human team", false);
                    return;
                }
                DynCapCount(team, st, out int tc, out int held);
                int E = S + held;
                int buffer = Math.Max(1, _playerCapExemptBuffer);
                int lead = Math.Max(0, _playerCapExemptLead);

                if (!st.Open)
                {
                    bool nearCap = tc + lead >= S;
                    int reopenGap = buffer + (st.ClosedAtTop ? DynCapTopHysteresis : 0);
                    if (nearCap && held > 0 && tc + reopenGap <= E)
                        DynCapOpen(team, st, $"tc={tc} S={S} held={held} E={E} lead={lead} buffer={buffer}");
                    else if (nearCap && Time.realtimeSinceStartup - st.LastWhyLog > 30f)
                    {
                        // Near or over the standard cap but not switching: say why, at most every 30 s per team.
                        st.LastWhyLog = Time.realtimeSinceStartup;
                        string why = held <= 0 ? "no player holds a cap unit"
                            : $"needs tc <= {E - reopenGap} (E - {reopenGap}{(st.ClosedAtTop ? ", closed at the top before" : "")})";
                        MelonLogger.Msg($"[DYNCAP] {team.TeamShortName}: stays CLOSED tc={tc} S={S} held={held} E={E} lead={lead} buffer={buffer}: {why}");
                    }
                }
                else
                {
                    if (tc + buffer > E)
                        DynCapClose(team, st, $"enhanced cap reached: tc={tc} S={S} held={held} E={E} buffer={buffer}", true);
                    else if (tc + lead + DynCapHysteresis < S)
                        DynCapClose(team, st, $"below standard cap: tc={tc} S={S} lead={lead}", false);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DYNCAP] evaluate failed for {team.TeamShortName}: {ex.Message}");
            }
        }

        /// <summary>Restore every team's values (round end/start, !rebalance, toggle off).</summary>
        private static void DynCapCloseAll(string why)
        {
            try
            {
                foreach (var kv in _dynCap)
                {
                    if (!kv.Value.Open) continue;
                    Team team = kv.Key >= 0 && kv.Key < Team.Teams.Count ? Team.Teams[kv.Key] : null;
                    if (team == null) { kv.Value.Originals.Clear(); kv.Value.Open = false; continue; }
                    DynCapClose(team, kv.Value, why, false);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DYNCAP] close-all ({why}) failed: {ex.Message}");
            }
        }

        // ---- settings: one code path for the chat menu page and "!b capexempt" ----
        private const int DynCapBufferMin = 1;
        private const int DynCapBufferMax = 10;

        private static void DynCapEvaluateAll()
        {
            try { foreach (var t in Team.Teams) if (t != null) DynCapEvaluate(t); }
            catch (Exception ex) { MelonLogger.Warning($"[DYNCAP] evaluate-all failed: {ex.Message}"); }
        }

        /// <summary>Switch the exemption on or off: config write, close or re-evaluate every team, audit log.
        /// False (and nothing written) when it is already in that state.</summary>
        private static bool DynCapSetEnabled(object player, bool on)
        {
            if (_playerInfantryIgnoreCap == on) return false;
            _playerInfantryIgnoreCap = on;
            WriteBoolToJson("player_infantry_ignore_cap", on);
            if (!on) DynCapCloseAll("toggle off");
            else DynCapEvaluateAll();
            string pn = GetPlayerName(player);
            string sid = GetPlayerSteamId(player);
            WriteAuditLog(pn, sid, "capdecay", "player_infantry_ignore_cap", (!on).ToString(), on.ToString());
            MelonLogger.Msg($"[BAL] {pn} ({sid}): player_infantry_ignore_cap -> {on}");
            return true;
        }

        /// <summary>Set the buffer from chat text (whole number 1..10): config write, re-evaluate while on, audit log.
        /// False when the text is not a valid buffer; nothing is changed then.</summary>
        private static bool DynCapApplyBuffer(object player, string valueStr, out int oldValue, out int newValue)
        {
            oldValue = _playerCapExemptBuffer;
            if (!int.TryParse(valueStr?.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out newValue)
                || newValue < DynCapBufferMin || newValue > DynCapBufferMax)
                return false;
            _playerCapExemptBuffer = newValue;
            WriteNumberToJson("player_cap_exempt_buffer", newValue);
            if (_playerInfantryIgnoreCap) DynCapEvaluateAll();
            string pn = GetPlayerName(player);
            string sid = GetPlayerSteamId(player);
            WriteAuditLog(pn, sid, "capdecay", "player_cap_exempt_buffer", oldValue.ToString(), newValue.ToString());
            MelonLogger.Msg($"[BAL] {pn} ({sid}): player_cap_exempt_buffer {oldValue} -> {newValue}");
            return true;
        }

        // ---- teams in play ----

        /// <summary>True when the team takes part in the current match. Primary test is the game's own:
        /// GameModeExt.GetTeamSetupActive(setup) for the team's BaseTeamSetup (setup enabled and listed for the
        /// current TeamsVersus; MP_StrategyMatchmaking narrows it to its two match teams). Fallback: the team has a
        /// standing structure, for modes or mods that field a team outside its setup. Unused factions and the
        /// *_Allied teams of other versus modes fail both.</summary>
        private static bool DynCapTeamInPlay(Team team)
        {
            if (team == null) return false;
            try
            {
                if (GameMode.CurrentGameMode is GameModeExt mode && mode.BaseTeamSetups != null)
                {
                    foreach (BaseTeamSetup setup in mode.BaseTeamSetups)
                        if (setup != null && setup.Team == team && mode.GetTeamSetupActive(setup)) return true;
                }
            }
            catch { }
            try
            {
                var structures = team.Structures;
                for (int i = 0; i < structures.Count; i++)
                {
                    var s = structures[i];
                    if (s != null && !s.IsDestroyed) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>The numbers behind the switch for one team (true cap points, see the header).</summary>
        private struct DynCapView
        {
            public string Name;
            public bool Open;
            public int S, Tc, Held, E, Buffer, Lead, ReopenGap;
        }

        private static bool DynCapTryView(Team team, out DynCapView v)
        {
            v = default;
            try
            {
                if (team == null || !DynCapIsHumanTeam(team)) return false;
                var entry = team.GetUnitTypeCapEntryForType(UnitCapType.Secondary);
                int S = entry != null ? entry.Max : 0;
                if (S <= 0) return false;
                var st = DynCapState(team);
                DynCapCount(team, st, out int tc, out int held);
                int buffer = Math.Max(1, _playerCapExemptBuffer);
                v = new DynCapView
                {
                    Name = team.TeamShortName, Open = st.Open, S = S, Tc = tc, Held = held, E = S + held,
                    Buffer = buffer, Lead = Math.Max(0, _playerCapExemptLead),
                    ReopenGap = buffer + (st.ClosedAtTop ? DynCapTopHysteresis : 0)
                };
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DYNCAP] live numbers failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Sol/Centauri teams in play in the current match that have a standard cap, in team order.</summary>
        private static List<DynCapView> DynCapTeamsInPlay()
        {
            var list = new List<DynCapView>();
            try
            {
                foreach (var t in Team.Teams)
                    if (DynCapTeamInPlay(t) && DynCapTryView(t, out var v)) list.Add(v);
            }
            catch { }
            return list;
        }

        /// <summary>Chat status for the Cap&amp;Decay overview line.</summary>
        private static string DynCapStatus()
        {
            if (!_playerInfantryIgnoreCap) return "off";
            var sb = new StringBuilder();
            foreach (var v in DynCapTeamsInPlay())
            {
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(v.Name).Append(v.Open ? " active" : " inactive");
            }
            return sb.Length > 0 ? sb.ToString() : "on";
        }

        /// <summary>One line per team for the menu page and the status report. No state part while the switch
        /// is off: the switch row says so already.</summary>
        private static string DynCapLiveLine(DynCapView v)
        {
            string line = v.Name + ": " + v.Tc + "/" + v.S + " cap points in use · players hold " + v.Held;
            if (!_playerInfantryIgnoreCap) return line;
            string state;
            if (v.Open)
                state = "<color=#55FF55>ACTIVE</color>, cap raised to " + v.E + ", closes at " + Math.Max(0, v.E - v.Buffer + 1);
            else if (v.Tc + v.Lead < v.S)
                state = "<color=#FFAA00>inactive</color>, activates at " + Math.Max(0, v.S - v.Lead);
            else if (v.Held <= 0)
                state = "<color=#FFAA00>inactive</color>, no player holds a cap unit";
            else
                state = "<color=#FFAA00>inactive</color>, raised cap " + v.E + " used up, reopens at " + Math.Max(0, v.E - v.ReopenGap);
            return line + " · " + state;
        }

        /// <summary>Second line of a "!b capexempt" reply: every team in play on one line, no colour.</summary>
        private static string DynCapSummary()
        {
            var views = DynCapTeamsInPlay();
            if (views.Count == 0) return "No Sol/Centauri team in play.";
            var sb = new StringBuilder();
            foreach (var v in views)
            {
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(v.Name).Append(' ').Append(v.Tc).Append('/').Append(v.S).Append(", players hold ").Append(v.Held);
                if (!_playerInfantryIgnoreCap) continue;
                if (v.Open) sb.Append(", active (cap ").Append(v.E).Append(')');
                else if (v.Tc + v.Lead < v.S) sb.Append(", activates at ").Append(Math.Max(0, v.S - v.Lead));
                else if (v.Held <= 0) sb.Append(", nothing to exempt");
                else sb.Append(", raised cap ").Append(v.E).Append(" full");
            }
            return sb.ToString();
        }

        private static string DynCapOnOff(bool on) => on ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";

        // ---- "!b capexempt [status | on | off | buffer <1..10>]" ----
        // Stateless admin sub-command for the ModUI panel (switch row, value row, status report) and for chat.
        // Reached through OnBalanceCommand, so AdminMod's power check of !b applies; never touches the caller's
        // menu state. Replies to on/off/buffer stay at two lines so the panel shows them inline.
        private static void OnCapExemptCommand(object player, string args)
        {
            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "Player Cap Exempt</color> " + DynCapOnOff(_playerInfantryIgnoreCap)
                        + " · buffer " + _valueColor + _playerCapExemptBuffer + "</color> · starts " + _playerCapExemptLead + " below the cap");
                    var views = DynCapTeamsInPlay();
                    foreach (var v in views) SendChatToPlayer(player, _chatPrefix + DynCapLiveLine(v));
                    if (views.Count == 0) SendChatToPlayer(player, _chatPrefix + _dimColor + "No Sol/Centauri team in play.</color>");
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Near the cap, units players hold count 0, so the cap rises by what they hold.</color>");
                    return;
                }
                case "on":
                case "off":
                {
                    bool on = sub == "on";
                    if (DynCapSetEnabled(player, on))
                        SendChatToPlayer(player, _chatPrefix + "Player Cap Exempt: " + DynCapOnOff(on) + (on ? " (buffer " + _playerCapExemptBuffer + ")" : "") + ".");
                    else
                        SendChatToPlayer(player, _chatPrefix + "Player Cap Exempt is already " + DynCapOnOff(on) + ".");
                    SendChatToPlayer(player, _chatPrefix + _dimColor + DynCapSummary() + "</color>");
                    return;
                }
                case "buffer":
                {
                    string raw = parts.Length > 1 ? parts[1] : "";
                    if (!DynCapApplyBuffer(player, raw, out int oldValue, out int newValue))
                    {
                        SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Buffer must be a whole number " + DynCapBufferMin + ".." + DynCapBufferMax
                            + (raw.Length > 0 ? " (got " + raw + ")" : "") + ", e.g. !b capexempt buffer 3</color>");
                        return;
                    }
                    SendChatToPlayer(player, _chatPrefix + "Player Cap Exempt buffer: " + _valueColor + oldValue + "</color> -> " + _valueColor + newValue + "</color>"
                        + (_playerInfantryIgnoreCap ? "." : " (switch is OFF)."));
                    SendChatToPlayer(player, _chatPrefix + _dimColor + DynCapSummary() + "</color>");
                    return;
                }
                default:
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Unknown option: " + parts[0] + ". Use !b capexempt status, on, off or buffer 1..10</color>");
                    return;
            }
        }

        // Team.UpdateUnitCapValues postfix: vanilla just recounted with the current values; decide
        // whether the window has to switch.
        private static class Patch_UnitCapPlayerInfantry
        {
            public static void Postfix(Team __instance)
            {
                DynCapEvaluate(__instance);
            }
        }

        // Unit.ControlledBy setter postfix: vanilla does not recount on a control change, and "held"
        // depends on it. The count itself does not change, so no vanilla recompute is needed.
        private static class Patch_UnitControlledBy_CapRefresh
        {
            public static void Postfix(Unit __instance)
            {
                if (!_playerInfantryIgnoreCap || __instance == null) return;
                try
                {
                    var team = __instance.UncontrolledTeam; // the team whose Units list holds this unit
                    if (team != null) DynCapEvaluate(team);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[DYNCAP] control-change evaluate error: {ex.Message}");
                }
            }
        }
    }
}

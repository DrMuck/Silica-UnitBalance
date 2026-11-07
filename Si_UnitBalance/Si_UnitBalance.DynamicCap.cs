using HarmonyLib;
using MelonLoader;
using System;
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
        //   CLOSED -> OPEN   when the standard cap is reached (tc + 2 > S) and tc + buffer <= E
        //   OPEN   -> CLOSED when tc + buffer > E (enhanced cap reached) or tc + 2 + hyst <= S
        //   re-open after a close at the top only once tc + buffer + hyst <= E (no flapping)
        // The buffer keeps a burst of queued builds (client and server do not switch in the same
        // instant) from passing the enhanced cap; the server re-validates every request with its own
        // already-switched values, so the overshoot is bounded by buffer + 2 at worst.
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
        // =============================================

        private static int _playerCapExemptBuffer = 3;     // config "player_cap_exempt_buffer"
        private const int DynCapHysteresis = 2;
        private const int DynCapMaxUnitValue = 2;         // largest Secondary player unit value (Heavy, Shocker, ...)

        private sealed class DynCapTeamState
        {
            public bool Open;
            public bool ClosedAtTop;                                       // last close was "enhanced cap reached"
            public readonly Dictionary<string, int> Originals = new Dictionary<string, int>(); // ObjectInfo.name -> cap value while OPEN
            public bool WarnedNoOM;
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
            MelonLogger.Msg($"[DYNCAP] {team.TeamShortName}: OPEN ({why}) zeroed {st.Originals.Count} unit(s): {sb}");
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
                int buffer = Math.Max(DynCapMaxUnitValue, _playerCapExemptBuffer);

                if (!st.Open)
                {
                    bool standardFull = tc + DynCapMaxUnitValue > S;
                    int reopenGap = buffer + (st.ClosedAtTop ? DynCapHysteresis : 0);
                    if (standardFull && held > 0 && tc + reopenGap <= E)
                        DynCapOpen(team, st, $"tc={tc} S={S} held={held} E={E} buffer={buffer}");
                }
                else
                {
                    if (tc + buffer > E)
                        DynCapClose(team, st, $"enhanced cap reached: tc={tc} S={S} held={held} E={E} buffer={buffer}", true);
                    else if (tc + DynCapMaxUnitValue + DynCapHysteresis <= S)
                        DynCapClose(team, st, $"below standard cap: tc={tc} S={S}", false);
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

        /// <summary>Chat/ModUI status line for the toggle.</summary>
        private static string DynCapStatus()
        {
            if (!_playerInfantryIgnoreCap) return "off";
            var sb = new StringBuilder();
            foreach (var kv in _dynCap)
            {
                if (kv.Key < 0 || kv.Key >= Team.Teams.Count) continue;
                var team = Team.Teams[kv.Key];
                if (team == null || !DynCapIsHumanTeam(team)) continue;
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(team.TeamShortName).Append(kv.Value.Open ? " open" : " closed");
            }
            return sb.Length > 0 ? sb.ToString() : "on";
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

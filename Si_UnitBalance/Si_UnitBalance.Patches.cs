using HarmonyLib;
using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Silica;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Unit-cap exemption for player-controlled infantry (HTP toggle: player_infantry_ignore_cap)
        // Team.UpdateUnitCapValues recomputes each cap entry's Current from scratch every call,
        // adding every unit's ObjectInfo.UnitCapValue with no ControlledBy check. When the toggle is on,
        // we subtract back the contribution of player-controlled infantry so actively-played soldiers
        // don't occupy cap slots. Recompute-from-scratch means no compounding across calls.
        // =============================================

        private static class Patch_UnitCapPlayerInfantry
        {
            public static void Postfix(Team __instance)
            {
                if (!_playerInfantryIgnoreCap || __instance == null) return;
                try
                {
                    var units = __instance.Units;
                    if (units == null) return;
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u == null || u.ObjectInfo == null) continue;
                        if (u.ControlledBy == null) continue;                       // only players actively controlling a soldier
                        if (u.ObjectInfo.UnitType != UnitType.Infantry) continue;   // soldiers only (creatures are Cavalry/Special/etc.)
                        var entry = __instance.GetUnitTypeCapEntryForUnit(u.ObjectInfo);
                        if (entry != null)
                        {
                            entry.Current -= u.ObjectInfo.UnitCapValue;
                            if (entry.Current < 0) entry.Current = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[UnitCap] player-infantry exemption error: {ex.Message}");
                }
            }
        }

        // =============================================
        // VehicleDispenser patch — enforce min_tier on dispensed vehicles (e.g., Hoverbike)
        // =============================================

        private static class Patch_VehicleDispenser
        {
            public static bool Prefix(object __instance, object player)
            {
                try
                {
                    if (_minTierOverrides.Count == 0) return true;

                    // Get VehicleToDispense.DisplayName
                    var vtdField = __instance.GetType().GetField("VehicleToDispense",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (vtdField == null) return true;
                    var objectInfo = vtdField.GetValue(__instance);
                    if (objectInfo == null) return true;

                    var displayNameField = objectInfo.GetType().GetField("DisplayName",
                        BindingFlags.Public | BindingFlags.Instance);
                    string unitName = null;
                    if (displayNameField != null)
                        unitName = displayNameField.GetValue(objectInfo) as string;
                    else
                    {
                        var displayNameProp = objectInfo.GetType().GetProperty("DisplayName",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (displayNameProp != null)
                            unitName = displayNameProp.GetValue(objectInfo) as string;
                    }
                    if (string.IsNullOrEmpty(unitName)) return true;

                    if (!_minTierOverrides.TryGetValue(unitName, out int minTier)) return true;
                    if (minTier < 0) return true;

                    // Get team tier: __instance.Team.TechnologyTier
                    var teamProp = __instance.GetType().GetProperty("Team",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (teamProp == null) return true;
                    var team = teamProp.GetValue(__instance);
                    if (team == null) return true;

                    var tierProp = team.GetType().GetProperty("TechnologyTier",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (tierProp == null) return true;
                    int currentTier = (int)tierProp.GetValue(team);

                    if (currentTier < minTier)
                    {
                        MelonLogger.Msg($"[DISPENSER] Blocked '{unitName}' — team tier {currentTier} < required {minTier}");

                        // Notify the requesting player via chat
                        try
                        {
                            if (_sendChatToPlayerMethod != null && player != null)
                            {
                                string msg = _chatPrefix + $"<color=#FF6666>{unitName}</color> requires tech tier <color=#FFD700>{minTier}</color> (current: {currentTier})";
                                _sendChatToPlayerMethod.Invoke(null, new object[] { player, new string[] { msg } });
                            }
                        }
                        catch { }

                        return false; // Block the spawn
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[DISPENSER] Tier check error: {ex.Message}");
                }
                return true;
            }
        }

        // =============================================
        // keep_production patches — allow unlinked structures to keep producing
        //
        // Strategy: Patch IsFunctional on HumanStructure and AlienStructure to return
        // true when keep_production is enabled (so Construct, HasLogistics, and
        // ConstructionSite.Update all work). Then patch UpdateHealDecay on both to
        // enable decay based on actual unlinked state, since IsFunctional no longer
        // reflects it.
        // =============================================

        /// <summary>Check if a structure belongs to a faction with keep_production enabled.</summary>
        private static bool ShouldKeepProduction(Structure structure)
        {
            if (structure == null) return false;
            bool isHuman = structure is HumanStructure;
            bool isAlien = structure is AlienStructure;
            if (!isHuman && !isAlien) return false;
            var settings = isHuman ? _decayHuman : _decayAlien;
            return settings.KeepProduction;
        }

        // ── keep_production: permanent production for unlinked structures ──
        // UnlinkedStructureStateEnabled=false is set per-team in ApplyDecayTeamSettings (RPC synced).
        // IsFunctional returns true on client+server → production works even without HQ.
        // UpdateHealDecay postfix forces server-side decay on actually-unlinked structures,
        // since the vanilla code won't enable it (IsFunctional=true skips decay).

        private static FieldInfo _humanAnchorField;

        // Perf: avoid per-frame reflection + GetComponent on every structure's UpdateHealDecay.
        // Called per-structure-per-frame by the engine; body cost dominates over Harmony dispatch.
        private static AccessTools.FieldRef<HumanStructure, Structure>? _humanAnchorAccessor;
        // InstanceID -> cached DamageManager (the component never swaps during a structure's lifetime).
        // Cleared on map change / game end (DecayCacheClear) to avoid stale refs from destroyed objects.
        private static readonly Dictionary<int, DamageManager> _dmCacheHuman = new Dictionary<int, DamageManager>();
        private static readonly Dictionary<int, DamageManager> _dmCacheAlien = new Dictionary<int, DamageManager>();

        internal static void DecayCacheClear()
        {
            _dmCacheHuman.Clear();
            _dmCacheAlien.Clear();
        }


        // Patch HumanStructure.UpdateHealDecay — PREFIX that REPLACES vanilla method
        // when keep_production is active. Vanilla would set Decay.enabled = !IsFunctional = false
        // (since IsFunctional=true due to UnlinkedStructureStateEnabled=false), preventing decay.
        // We replace it to use the actual unlinked state instead of IsFunctional.
        private static class Patch_HumanStructure_UpdateHealDecay
        {
            public static bool Prefix(object __instance)
            {
                try
                {
                    if (!_decayHuman.KeepProduction || !_decayHuman.Enabled) return true; // run vanilla

                    var hs = __instance as HumanStructure;
                    if (hs == null) return true;

                    if (_humanAnchorAccessor == null)
                    {
                        if (_humanAnchorField == null)
                            _humanAnchorField = typeof(HumanStructure).GetField("AnchorStructure",
                                BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_humanAnchorField == null) return true;
                        _humanAnchorAccessor = AccessTools.FieldRefAccess<HumanStructure, Structure>(_humanAnchorField);
                    }

                    // Fast field read via compiled accessor (vs. slow FieldInfo.GetValue on Il2Cpp)
                    var anchor = _humanAnchorAccessor(hs);
                    bool actuallyUnlinked = !hs.IsConstructionAnchor && anchor == null;

                    // Cache DamageManager by instance — GetComponent is expensive in Il2Cpp
                    int id = hs.GetInstanceID();
                    if (!_dmCacheHuman.TryGetValue(id, out var dm) || dm == null)
                    {
                        dm = hs.gameObject.GetComponent<DamageManager>();
                        if (dm != null) _dmCacheHuman[id] = dm;
                    }
                    if (dm != null && dm.DisableAutoHeal != actuallyUnlinked)
                        dm.DisableAutoHeal = actuallyUnlinked;

                    if (hs.Decay != null && hs.Decay.enabled != actuallyUnlinked)
                        hs.Decay.enabled = actuallyUnlinked;

                    return false; // skip vanilla method
                }
                catch { return true; }
            }
        }

        // Patch AlienStructure.UpdateHealDecay — same PREFIX replacement for alien
        private static class Patch_AlienStructure_UpdateHealDecay
        {
            public static bool Prefix(object __instance)
            {
                try
                {
                    if (!_decayAlien.KeepProduction || !_decayAlien.Enabled) return true;

                    var alien = __instance as AlienStructure;
                    if (alien == null) return true;

                    bool actuallyUnlinked = !alien.IsLinkedToMainBuilding;

                    int id = alien.GetInstanceID();
                    if (!_dmCacheAlien.TryGetValue(id, out var dm) || dm == null)
                    {
                        dm = alien.gameObject.GetComponent<DamageManager>();
                        if (dm != null) _dmCacheAlien[id] = dm;
                    }
                    if (dm != null && dm.DisableAutoHeal != actuallyUnlinked)
                        dm.DisableAutoHeal = actuallyUnlinked;

                    if (alien.Decay != null && alien.Decay.enabled != actuallyUnlinked)
                        alien.Decay.enabled = actuallyUnlinked;

                    return false; // skip vanilla
                }
                catch { return true; }
            }
        }

        // =============================================
        // Null-safe GameDatabase.GetSpawnablePrefabIndex(string)
        //
        // Beta 0.9.47 leaves null entries in GameDatabase.AllNetworkedPrefabs (see
        // BuildNetworkedPrefabIndex). The vanilla lookup does list[i].name on every entry, so any
        // mod that spawns by name (Si_Resources' reveal scout at the versus vote, Si_MapBalance,
        // AdminMod.SpawnAtLocation callers) throws — and Si_Resources' team loop aborts on the first
        // throw, leaving later teams without their starting-resource adjustment. Same lookup,
        // same result, just skipping nulls. Falls back to vanilla if anything unexpected happens.
        // =============================================
        private static class Patch_GetSpawnablePrefabIndex
        {
            public static bool Prefix(string prefabName, ref int __result)
            {
                try
                {
                    __result = -1;
                    var db = GameDatabase.Database;
                    if (db == null || prefabName == null) return false;
                    var list = db.AllNetworkedPrefabs;
                    if (list == null) return false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var go = list[i];
                        if (go == null) continue;
                        if (string.Equals(go.name, prefabName, StringComparison.OrdinalIgnoreCase)) { __result = i; return false; }
                    }
                    return false;
                }
                catch { return true; }
            }
        }

        // =============================================
        // Absolute unit count caps (config "count_caps": shrimp / harvester; -1 = unlimited)
        //
        // Structure.Construct is the one server-side gate for every production request (client
        // CONSTRUCT_REQUEST RPC, AI commander, alien cysts). We count the team's live units plus
        // everything queued / under construction of the same ObjectInfo — the same three sources
        // Team.UpdateUnitCapValues uses — and refuse with ProductionActionResult.UnitCap once the cap
        // is reached. The client's build button stays enabled (its own cap checks pass), so the
        // team's commander gets a chat warning instead; throttled so click-spam doesn't flood chat.
        // =============================================

        private static readonly Dictionary<string, float> _countCapLastWarn = new Dictionary<string, float>();

        private static string FmtCountCap(int v) => v < 0 ? "unlimited" : v.ToString();

        // Which cap (if any) applies to this ObjectInfo. Asset names are stable across the display
        // names ("Sol Harvester" / "Cent Harvester" / localized variants).
        private static int CountCapFor(ObjectInfo info, out string label)
        {
            label = null;
            if (info == null) return -1;
            string n = info.name ?? "";
            if (n.Equals("ObjectInfo_Alien_Shrimp", StringComparison.OrdinalIgnoreCase)) { label = "Shrimp"; return _countCapShrimp; }
            if (n.IndexOf("Harvester", StringComparison.OrdinalIgnoreCase) >= 0 && !n.Contains("_Intro")) { label = "Harvester"; return _countCapHarvester; }
            return -1;
        }

        private static int CountTeamUnitsOf(Team team, ObjectInfo info)
        {
            int n = 0;
            var units = team.Units;
            if (units != null)
                for (int i = 0; i < units.Count; i++)
                    if (units[i] != null && units[i].ObjectInfo == info) n++;
            var structures = team.Structures;
            if (structures != null)
                for (int i = 0; i < structures.Count; i++)
                {
                    var q = structures[i]?.ProductionQueue;
                    if (q == null) continue;
                    for (int j = 0; j < q.Count; j++)
                        if (q[j] != null && q[j].ObjectInfo == info) n++;
                }
            var sites = ConstructionSite.ConstructionSites;
            if (sites != null)
                for (int i = 0; i < sites.Count; i++)
                {
                    var cs = sites[i];
                    if (cs != null && cs.Team == team && cs.ObjectInfo == info && cs.gameObject.activeSelf) n++;
                }
            return n;
        }

        private static class Patch_CountCap
        {
            public static bool Prefix(Structure __instance, ConstructionData constructionData, bool isClientRequest, ref ProductionActionResult __result)
            {
                try
                {
                    if (__instance == null || constructionData == null || constructionData.ObjectInfo == null) return true;
                    var team = __instance.Team;
                    if (team == null) return true;
                    int cap = CountCapFor(constructionData.ObjectInfo, out string label);
                    if (cap < 0) return true;

                    int have = CountTeamUnitsOf(team, constructionData.ObjectInfo);
                    if (have < cap) return true;

                    __result = ProductionActionResult.UnitCap;
                    LogDebug($"[COUNTCAP] {team.TeamShortName}: {label} {have}/{cap} — refused {constructionData.ObjectInfo.DisplayName}{(isClientRequest ? " (client request)" : "")}");

                    // Warn the commander (throttled per team+cap).
                    string key = team.TeamShortName + "|" + label;
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (!_countCapLastWarn.TryGetValue(key, out float last) || now - last > 3f)
                    {
                        _countCapLastWarn[key] = now;
                        var gm = GameMode.CurrentGameMode as GameModeExt;
                        var commander = gm?.GetCommanderForTeam(team);
                        if (commander != null)
                            SendChatToPlayer(commander, _chatPrefix + "<color=#FF5555>" + label + " cap reached</color> " + _dimColor + "(" + have + "/" + cap + ") — no more can be built.</color>");
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[CountCap] prefix error: {ex.Message}");
                    return true;
                }
            }
        }

        // =============================================
        // Alien nest reach (config "alien_nest_reach_radius", metres; -1 = vanilla)
        //
        // Nest-centric expansion: a Nest reaches out to resources via Nodes / Bio Caches, but only
        // within its reach radius — anything further needs a new Nest first. Same server gate as the
        // count caps (Structure.Construct carries the requested world position for placeable
        // structures). Distance is 2D (XZ) from the placement point to the nearest friendly Nest,
        // built or under construction (vanilla lets you build around construction sites too), like
        // the game's own anchor check. The client's placement preview can't know about the rule, so
        // the commander gets a chat line + a sound (AdminMod AudioHelper) instead of a red ghost.
        // =============================================

        private const string NEST_REACH_SOUND = "sounds/access_denied.wav"; // relative to UserData/
        private static readonly Dictionary<string, float> _nestReachLastWarn = new Dictionary<string, float>();
        private static bool? _nestReachSoundAvailable; // lazily: file exists + AdminMod AudioHelper found

        private static string FmtNestReach(float v) => v < 0f ? "vanilla" : v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m";

        private static bool IsNestReachLimited(ObjectInfo info, out string label)
        {
            label = null;
            if (info == null) return false;
            string n = info.name ?? "";
            if (n.Equals("ObjectInfo_Alien_Node", StringComparison.OrdinalIgnoreCase)) { label = "Node"; return true; }
            if (n.Equals("ObjectInfo_Alien_BioCache", StringComparison.OrdinalIgnoreCase)) { label = "Bio Cache"; return true; }
            return false;
        }

        private static bool IsNestInfo(ObjectInfo info, Team team)
        {
            if (info == null) return false;
            if (team != null && team.BaseStructure != null && info == team.BaseStructure) return true;
            return string.Equals(info.name, "ObjectInfo_Alien_Nest", StringComparison.OrdinalIgnoreCase);
        }

        // Nearest friendly Nest (alive structure or active construction site), 2D distance; +inf if none.
        private static float NearestNestDistance2D(Team team, Vector3 position, out int nests)
        {
            float best = float.MaxValue;
            nests = 0;
            var structures = team.Structures;
            if (structures != null)
                for (int i = 0; i < structures.Count; i++)
                {
                    var st = structures[i];
                    if (st == null || st.IsDestroyed || !IsNestInfo(st.ObjectInfo, team)) continue;
                    nests++;
                    float d = GameMath.Distance2D(st.transform.position, position);
                    if (d < best) best = d;
                }
            var sites = ConstructionSite.ConstructionSites;
            if (sites != null)
                for (int i = 0; i < sites.Count; i++)
                {
                    var cs = sites[i];
                    if (cs == null || cs.Team != team || cs.IsDestroyed || !cs.gameObject.activeSelf || !IsNestInfo(cs.ObjectInfo, team)) continue;
                    nests++;
                    float d = GameMath.Distance2D(cs.transform.position, position);
                    if (d < best) best = d;
                }
            return best;
        }

        private static void PlaySoundToPlayer(object player, string relPath)
        {
            if (player == null || _playSoundFileMethod == null) return;
            if (_nestReachSoundAvailable == null)
            {
                string full = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, relPath);
                _nestReachSoundAvailable = File.Exists(full);
                if (!_nestReachSoundAvailable.Value)
                    MelonLogger.Warning($"[NestReach] sound file missing: {full} — chat warning only");
            }
            if (!_nestReachSoundAvailable.Value) return;
            try
            {
                // async Task — fire and forget; AudioHelper streams the WAV over the voice channel
                _playSoundFileMethod.Invoke(null, new object[] { relPath, player });
            }
            catch (Exception ex)
            {
                LogDebug($"[NestReach] PlaySoundFile failed: {ex.Message}");
            }
        }

        private static class Patch_NestReach
        {
            public static bool Prefix(Structure __instance, ConstructionData constructionData, Vector3 worldPosition, bool isClientRequest, ref ProductionActionResult __result)
            {
                try
                {
                    if (_alienNestReachRadius < 0f) return true;
                    if (__instance == null || constructionData == null || !constructionData.Placeable) return true;
                    if (!IsNestReachLimited(constructionData.ObjectInfo, out string label)) return true;
                    var team = __instance.Team;
                    if (team == null) return true;

                    float dist = NearestNestDistance2D(team, worldPosition, out int nests);
                    if (dist <= _alienNestReachRadius) return true;

                    __result = ProductionActionResult.AreaObstructed;
                    string distStr = nests > 0 ? dist.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m" : "no Nest";
                    LogDebug($"[NESTREACH] {team.TeamShortName}: {label} refused at {worldPosition} — nearest Nest {distStr} > {_alienNestReachRadius:F0} m{(isClientRequest ? " (client request)" : "")}");

                    // Warn the commander (throttled per team) + play the denied sound.
                    string key = team.TeamShortName;
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (!_nestReachLastWarn.TryGetValue(key, out float last) || now - last > 3f)
                    {
                        _nestReachLastWarn[key] = now;
                        var gm = GameMode.CurrentGameMode as GameModeExt;
                        var commander = gm?.GetCommanderForTeam(team);
                        if (commander != null)
                        {
                            SendChatToPlayer(commander, _chatPrefix + "<color=#FF5555>" + label + " out of Nest reach</color> " + _dimColor + "(nearest Nest " + distStr + ", limit " + _alienNestReachRadius.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m) — build closer to a Nest. A Nest is required for further expansion.</color>");
                            PlaySoundToPlayer(commander, NEST_REACH_SOUND);
                        }
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[NestReach] prefix error: {ex.Message}");
                    return true;
                }
            }
        }

        // =============================================
        // Alien nest finished-anchor only (config "alien_nest_finished_anchor_only", bool)
        //
        // Vanilla's placement test (ConstructionPreview.GetWithinBaseStructuresDistanceAndFogOfWar)
        // accepts construction SITES as anchors, so aliens can chain Nest -> Nest -> Nest outward
        // while the previous Nest is still growing. With this on, a Nest placement must be within
        // MaximumBaseStructureDistance of a FINISHED friendly anchor (Structure.Structures with
        // IsConstructionAnchor — i.e. a built Nest, or with nest-only build off also a linked cyst/
        // node); construction sites are ignored. Parallel Nests off a finished Nest stay allowed.
        // Same bounds-to-bounds 2D distance as the game, same server gate + commander feedback as
        // the nest reach rule.
        // =============================================

        private static readonly Dictionary<string, float> _nestAnchorLastWarn = new Dictionary<string, float>();

        // Nearest FINISHED friendly construction anchor for placing `data` at position/rotation,
        // measured like the game does (closest points of the two physical bounds, 2D). +inf if none.
        private static float NearestFinishedAnchorDistance2D(Team team, ConstructionData data, Vector3 position, Quaternion rotation, out Structure anchor)
        {
            anchor = null;
            float best = float.MaxValue;
            Bounds placed = GameFuncs.TransformBounds(data.ObjectInfo.PhysicalBounds, position, rotation);
            bool useRadius = data.MaximumDistanceUseRadius;
            var structures = Structure.Structures;
            if (structures == null) return best;
            for (int i = 0; i < structures.Count; i++)
            {
                var st = structures[i];
                if (st == null || st.Team != team || st.IsDestroyed || !st.IsConstructionAnchor) continue;
                Bounds sb = GameFuncs.TransformBounds(st.PhysicalBounds, st.transform, true);
                Vector3 a = placed.ClosestPoint(st.transform.position);
                Vector3 b = sb.ClosestPoint(a);
                float d = useRadius
                    ? GameMath.Distance2D(st.transform.position, position - a + position)
                    : GameMath.Distance2D(a, b);
                if (d < best) { best = d; anchor = st; }
            }
            return best;
        }

        // First active friendly Nest construction site (for the warning text), or null.
        private static ConstructionSite FindNestUnderConstruction(Team team)
        {
            var sites = ConstructionSite.ConstructionSites;
            if (sites == null) return null;
            for (int i = 0; i < sites.Count; i++)
            {
                var cs = sites[i];
                if (cs == null || cs.Team != team || cs.IsDestroyed || !cs.gameObject.activeSelf || cs.IsDeconstructing) continue;
                if (IsNestInfo(cs.ObjectInfo, team)) return cs;
            }
            return null;
        }

        // =============================================
        // Alien Queen logistics per Nest (config "alien_queen_logistics_per_nest", bool)
        //
        // Vanilla: Structure.Construct and StrategyMode.StartStructurePlacement refuse with
        // MissingLogistics when Team.GetNeedConstructionLogistics() is true — a TEAM-WIDE check over
        // every IsConstructionLogistics compartment (only the Nest's "QueenDock" AlienQueenCompartment
        // has the flag, verified in sharedassets0). Building is allowed as long as ANY Nest on the
        // team has a docked Queen, so the starter Queen leaving blocks the whole team only when no
        // other Queen is docked, and an expansion Queen leaving never blocks anything.
        // With this on, every alien structure belongs to its NEAREST finished Nest (2D distance —
        // the same "reach" notion as alien_nest_reach_radius; a Nest belongs to itself) and may only
        // build / produce while THAT Nest's Queen is docked. The alien link graph is deliberately not
        // used: with chained nodes/cysts the whole base is one connected graph, which would collapse
        // back into the vanilla team-wide rule. Needs alien_queens_max 0/N so expansion Nests have a Queen.
        // =============================================

        private static readonly Dictionary<string, float> _queenLogLastWarn = new Dictionary<string, float>();

        // True if a logistics compartment (Nest Queen dock) on this structure has a docked unit.
        private static bool HasDockedLogisticsUnit(Structure st)
        {
            var comps = st.UnitCompartments;
            if (comps == null) return false;
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (c != null && c.IsConstructionLogistics && c.DriverUnit != null) return true;
            }
            return false;
        }

        // Nearest finished friendly Nest to `origin` (2D). Null if the team has no built Nest.
        private static Structure NearestNest(Team team, Vector3 position, out float dist)
        {
            Structure best = null;
            dist = float.MaxValue;
            var structures = team.Structures;
            if (structures == null) return null;
            for (int i = 0; i < structures.Count; i++)
            {
                var st = structures[i];
                if (st == null || st.IsDestroyed || !IsNestInfo(st.ObjectInfo, team)) continue;
                float d = GameMath.Distance2D(st.transform.position, position);
                if (d < dist) { dist = d; best = st; }
            }
            return best;
        }

        private static class Patch_QueenLogisticsPerNest
        {
            public static bool Prefix(Structure __instance, ConstructionData constructionData, bool isClientRequest, ref ProductionActionResult __result)
            {
                try
                {
                    if (!_alienQueenLogisticsPerNest) return true;
                    if (ConstructionSite.LimitationsDisabled) return true;
                    var alien = __instance as AlienStructure;
                    if (alien == null || constructionData == null) return true;
                    var team = alien.Team;
                    if (team == null) return true;

                    var nest = NearestNest(team, alien.transform.position, out float dist);
                    if (nest == null) return true;              // no built Nest → vanilla rules decide
                    if (HasDockedLogisticsUnit(nest)) return true;

                    __result = ProductionActionResult.MissingLogistics;
                    string what = constructionData.ObjectInfo != null ? constructionData.ObjectInfo.DisplayName : "build";
                    string from = alien.ObjectInfo != null ? alien.ObjectInfo.DisplayName : "structure";
                    bool isSelf = nest == __instance;
                    string distStr = isSelf ? "itself" : dist.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m away";
                    LogDebug($"[QUEENLOG] {team.TeamShortName}: {what} from {from} refused — nearest Nest ({distStr}) has no docked Queen{(isClientRequest ? " (client request)" : "")}");

                    // Warn the commander (throttled per team) + play the denied sound.
                    string key = team.TeamShortName;
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (!_queenLogLastWarn.TryGetValue(key, out float last) || now - last > 3f)
                    {
                        _queenLogLastWarn[key] = now;
                        var gm = GameMode.CurrentGameMode as GameModeExt;
                        var commander = gm?.GetCommanderForTeam(team);
                        if (commander != null)
                        {
                            string where = isSelf ? "this Nest has no docked Queen" : "its Nest (nearest, " + distStr + ") has no docked Queen";
                            SendChatToPlayer(commander, _chatPrefix + "<color=#FF5555>Queen away — " + from + " can't build</color> " + _dimColor + "(" + where + ") — return the Queen to that Nest.</color>");
                            PlaySoundToPlayer(commander, NEST_REACH_SOUND);
                        }
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[QueenLog] prefix error: {ex.Message}");
                    return true;
                }
            }
        }

        private static class Patch_NestFinishedAnchor
        {
            public static bool Prefix(Structure __instance, ConstructionData constructionData, Vector3 worldPosition, Quaternion worldRotation, bool isClientRequest, ref ProductionActionResult __result)
            {
                try
                {
                    if (!_alienNestFinishedAnchorOnly) return true;
                    if (ConstructionSite.LimitationsDisabled) return true;
                    if (__instance == null || constructionData == null || !constructionData.Placeable || constructionData.ObjectInfo == null) return true;
                    var team = __instance.Team;
                    if (team == null || !IsNestInfo(constructionData.ObjectInfo, team)) return true;

                    float maxDist = constructionData.MaximumBaseStructureDistance;
                    float dist = NearestFinishedAnchorDistance2D(team, constructionData, worldPosition, worldRotation, out Structure anchor);
                    if (dist <= maxDist) return true; // a finished anchor is in range — vanilla rule already applies

                    __result = ProductionActionResult.AreaObstructed;
                    var pending = FindNestUnderConstruction(team);
                    string pct = pending != null ? Mathf.RoundToInt(Mathf.Clamp01(pending.ProgressTime01) * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" : null;
                    string distStr = anchor != null ? dist.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m" : "none";
                    LogDebug($"[NESTANCHOR] {team.TeamShortName}: Nest refused at {worldPosition} — nearest finished anchor {distStr} > {maxDist:F0} m{(pct != null ? $", Nest under construction {pct}" : "")}{(isClientRequest ? " (client request)" : "")}");

                    // Warn the commander (throttled per team) + play the denied sound.
                    string key = team.TeamShortName;
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (!_nestAnchorLastWarn.TryGetValue(key, out float last) || now - last > 3f)
                    {
                        _nestAnchorLastWarn[key] = now;
                        var gm = GameMode.CurrentGameMode as GameModeExt;
                        var commander = gm?.GetCommanderForTeam(team);
                        if (commander != null)
                        {
                            string hint = pct != null
                                ? "the Nest under construction (" + pct + ") can't anchor it yet — wait for it to finish or build next to a finished Nest."
                                : "build within " + maxDist.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m of a finished Nest.";
                            SendChatToPlayer(commander, _chatPrefix + "<color=#FF5555>Nest needs a finished anchor</color> " + _dimColor + "(nearest finished anchor " + distStr + ") — " + hint + "</color>");
                            PlaySoundToPlayer(commander, NEST_REACH_SOUND);
                        }
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[NestAnchor] prefix error: {ex.Message}");
                    return true;
                }
            }
        }

        // =============================================
        // Unit cap multiplier per match type (config "unit_cap_mult": two_way / three_way)
        //
        // Vanilla: MP_Strategy.Restart -> UpdateInitialStrategySettings ->
        // GameModeExt.UpdateUnitCapMultFromSetting derives UnitCapMultiplier from the server's
        // "capmult" param (0/1/2 -> x1/x2/x3, default x2; 0 = unlimited). That runs while
        // TeamsVersus is still NONE — the versus vote resolves later and calls SetTeamVersusMode,
        // which never touches the multiplier. UnitCapEntry.Max reads the multiplier live
        // (CapBase * GetUnitCapMultiplier()) and the setter RPC-syncs any change to clients, so
        // re-deriving it in a SetTeamVersusMode postfix is enough: no cap entry has to be rewritten.
        //
        // 2-way = HUMANS_VS_HUMANS, HUMANS_VS_ALIENS. 3-way = HUMANS_VS_HUMANS_VS_ALIENS and
        // HUMANS2_VS_ALIENS (two allied human teams vs aliens — still three teams' worth of units
        // on the map). -1 leaves the vanilla/server value alone (Si_MatchSettings, if loaded, has
        // already written its own value by then and is respected in that case).
        // =============================================

        private static bool _inVanillaCapMultRefresh; // re-entrancy guard for the UpdateUnitCapMultFromSetting postfix

        private static string FmtCapMult(int m) => m < 0 ? "vanilla" : (m == 0 ? "unlimited" : "x" + m);

        private static bool IsThreeWay(GameModeExt.ETeamsVersus mode) =>
            mode == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS ||
            mode == GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS;

        /// <summary>Configured multiplier for the given versus mode; -1 = leave vanilla.</summary>
        private static int GetConfiguredCapMult(GameModeExt.ETeamsVersus mode)
        {
            if (mode == GameModeExt.ETeamsVersus.NONE) return -1;
            return IsThreeWay(mode) ? _unitCapMult3Way : _unitCapMult2Way;
        }

        /// <summary>
        /// Push the configured multiplier for the current versus mode onto the running game mode.
        /// Safe to call any time (versus undecided / no game mode / not configured = no-op).
        /// </summary>
        private static void ApplyUnitCapMultiplier(string reason)
        {
            try
            {
                var gm = GameMode.CurrentGameMode as GameModeExt;
                if (gm == null || Game.GetIsClient()) return;
                var mode = gm.TeamsVersus;
                int wanted = GetConfiguredCapMult(mode);
                if (wanted < 0)
                {
                    // Not configured for this mode: make sure a value we set earlier (e.g. before
                    // the admin reverted the config to -1 and ran !rebalance) doesn't linger.
                    if (mode != GameModeExt.ETeamsVersus.NONE && !_inVanillaCapMultRefresh)
                    {
                        _inVanillaCapMultRefresh = true;
                        try { gm.UpdateUnitCapMultFromSetting(); }
                        finally { _inVanillaCapMultRefresh = false; }
                        LogDebug($"[UNITCAPMULT] {mode}: not configured, vanilla value {gm.UnitCapMultiplier} kept ({reason})");
                    }
                    return;
                }
                int before = gm.UnitCapMultiplier;
                if (before != wanted)
                    gm.SetUnitCapMultiplier(wanted); // setter RPC-syncs to clients
                MelonLogger.Msg($"[UNITCAPMULT] {mode} ({(IsThreeWay(mode) ? "3-way" : "2-way")}): cap multiplier {FmtCapMult(before)} -> {FmtCapMult(wanted)} ({reason})");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[UNITCAPMULT] apply error: {ex.Message}");
            }
        }

        private static class Patch_UnitCapMultVersus
        {
            public static void Postfix(MP_Strategy __instance)
            {
                if (!_enabled || __instance == null) return;
                ApplyUnitCapMultiplier("versus mode set");
            }
        }

        // The game (or Si_MatchSettings) recomputed the multiplier from the capmult setting.
        // If the versus mode is already known, our per-mode value wins; if it is still NONE
        // (round restart), the SetTeamVersusMode postfix will handle it.
        private static class Patch_UnitCapMultSetting
        {
            public static void Postfix(GameModeExt __instance)
            {
                if (!_enabled || __instance == null || _inVanillaCapMultRefresh) return;
                if (__instance.TeamsVersus == GameModeExt.ETeamsVersus.NONE) return;
                ApplyUnitCapMultiplier("capmult refresh");
            }
        }

        // =============================================
        // Chat command: !rebalance — hot-reload config during a running game
        // =============================================

        // Called by AdminMod when an admin types !rebalance or !rebalance default
        private static void OnRebalanceCommand(object player, string args)
        {
            // Check for "!rebalance default" — revert to vanilla (no re-apply)
            bool defaultMode = args != null && args.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0;

            if (defaultMode)
                MelonLogger.Msg("[Rebalance] Reverting to vanilla defaults (admin command)");
            else
                MelonLogger.Msg("[Rebalance] Hot-reload triggered by admin chat command");

            try
            {
                // 1. Revert all current OM overrides (restores originals, notifies clients)
                if (_omInitialized && _overrideManagerType != null)
                {
                    OMRevertAll();
                    _overridesApplied = false;
                    MelonLogger.Msg("[Rebalance] Reverted all overrides");
                }

                // 2. Clear direct-mutation caches
                _originalCreatureAttackAimDist.Clear();
                _originalSpread.Clear();
                _originalProjectileLifetimes.Clear();
                _originalProjectileSpeeds.Clear();
                _originalAimLeadVelocity.Clear();
                _originalAimLeadGravity.Clear();
                _originalAimLeadDrag.Clear();
                _originalMoveSpeeds.Clear();
                _originalMeleeDamage.Clear();
                _originalHealth.Clear();
                _originalAimAngle.Clear();
                _nameCache.Clear();

                if (defaultMode)
                {
                    // Sync the revert to all connected players and stop — vanilla is restored
                    if (_omInitialized)
                        MelonCoroutines.Start(SyncOverridesToAllPlayers(0.5f));
                    _unitCapMult2Way = -1;
                    _unitCapMult3Way = -1;
                    ApplyUnitCapMultiplier("rebalance default");
                    MelonLogger.Msg("[Rebalance] Vanilla defaults restored. Use !rebalance to reload config.");
                    return;
                }

                // 3. Reload config from disk
                LoadConfig();
                MelonLogger.Msg($"[Rebalance] Config reloaded: {_damageMultipliers.Count} damage, {_healthMultipliers.Count} health, " +
                    $"{_costMultipliers.Count} cost, {_buildTimeMultipliers.Count} buildTime, " +
                    $"{_moveSpeedMultipliers.Count} moveSpeed, {_projectileOverrides.Count} projOverrides");

                // 4. Re-apply all overrides
                if (_enabled && _configLoaded)
                {
                    bool omReady = _omInitialized;
                    if (!omReady)
                        omReady = InitOverrideManager();

                    if (omReady && _healthMultEnabled && _healthMultipliers.Count > 0)
                        RegisterDamageManagerDataInOM();

                    // Snapshot vanilla base values before overrides modify them
                    CacheVanillaBaseValues();

                    ApplyConstructionDataOverrides(omReady);
                    if (_healthMultEnabled)
                    {
                        ApplyHealthOverrides(omReady);
                        ReclampLiveHealth();
                    }
                    ApplyProjectileDamageOverrides(omReady);
                    ApplyProjectileTuningOverrides(omReady);
                    ApplyAimCompensationOverrides();
                    ApplyTurretPitchOverrides();
                    ApplyRangeOverrides(omReady);
                    ApplyTargetDistanceOverrides(omReady);
                    ApplyFoWDistanceOverrides(omReady);
                    ApplyJumpSpeedOverrides(omReady);
                    ApplyVisibleEventRadiusOverrides(omReady);
                    ApplyMoveSpeedOverrides(omReady);
                    ApplyStrafeSpeedOverrides(omReady);
                    ApplyTurnRadiusOverrides(omReady);
                    ApplyTeleportOverrides(omReady);
                    ApplyDispenserTimeoutOverrides(omReady);
                    ApplyProximityDetonationOverrides();
                    ApplyDepositRadiusOverrides(omReady);
                    ApplyExtractionRadiusOverrides(omReady);

                    if (_shrimpDisableAim)
                        ApplyShrimpAimDisable();

                    ApplyAlienNestOnlyBuild(omReady);
                    ApplyAlienQueensMax();

                    ApplyUnitCapMultiplier("rebalance");

                    // 4.5 Propagate overrides to already-spawned units in the scene
                    // (OM only modifies prefab data — live instances have independent component copies)
                    try { PropagateToLiveInstances(); }
                    catch (Exception pex) { MelonLogger.Warning($"[Rebalance] Live propagation error: {pex.Message}"); }

                    _overridesApplied = true;
                    FlushPrefabPool("after rebalance");
                    MelonLogger.Msg("[Rebalance] All overrides re-applied");

                    // 5. Sync to all connected players
                    if (omReady)
                        MelonCoroutines.Start(SyncOverridesToAllPlayers(0.5f));

                    // 6. Auto-post to Discord if enabled
                    if (_discordAutoPost && !string.IsNullOrEmpty(_discordWebhookUrl))
                        PostBalanceChangesToDiscord();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Rebalance] Hot-reload failed: {ex.Message}");
            }
        }

        // =============================================
        // SendPlayerOverrides patch — split overrides into per-target packets.
        // The original packs ALL overrides into one packet (>2400 bytes with 186 overrides).
        // We swap the _overrides dict to contain one target at a time, call the original
        // per-target (each produces a small ~100-300 byte packet), then restore.
        // =============================================

        // Process menu input from !b command
        private static void ProcessMenuInput(object player, string input)
        {
            long key = GetPlayerKey(player);

            // Read-only /stats inspector: handled only when the caller is NOT already in the
            // admin editor (_menuStates). The editor takes priority so admins who used /stats
            // before opening !b still get their /1 confirm-save routed to the editor, not
            // hijacked by the stats display.
            if (!_menuStates.ContainsKey(key) && _statsStates.ContainsKey(key))
            {
                string statsCmd = (input ?? "").Trim();
                var sp = statsCmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length > 0) statsCmd = sp[0];
                if (TryHandleStatsNavCommand(player, key, statsCmd)) return;
            }

            if (!_menuStates.TryGetValue(key, out var state)) return;

            string arg = input;
            string arg2 = "";
            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) arg = parts[0];
            if (parts.Length > 1) arg2 = parts[1];

            // Handle pending confirmation: 1/yes/y = save, 2/no/n = cancel, 3 = save + rebalance
            if (state.PendingConfirm)
            {
                bool doSave = arg.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                              arg.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                              arg == "1" || arg == "3";
                bool doCancel = arg.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                                arg.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                                arg == "2";
                bool doRebalance = arg == "3";

                if (doSave)
                {
                    string unitName = state.PendingUnitName ?? _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];
                    bool ok;
                    if (state.PendingTechTierKey != null && state.PendingIsTechCost)
                        ok = WriteTechCostToJson(state.PendingTechTierKey, (int)Math.Round(state.PendingValue));
                    else if (state.PendingTechTierKey != null)
                        ok = WriteTechTierToJson(state.PendingTechTierKey, state.PendingValue);
                    else if (state.PendingParamKey.StartsWith("prox_"))
                        ok = WriteProximityToJson(unitName, state.PendingParamKey.Substring(5), state.PendingValue);
                    else if (state.PendingParamKey.StartsWith("unitcapmult_"))
                    {
                        string capKey = state.PendingParamKey.Substring(12); // "two_way" / "three_way"
                        int capVal = (int)Math.Round(state.PendingValue);
                        ok = WriteUnitCapMultToJson(capKey, capVal);
                        if (ok)
                        {
                            if (capKey == "two_way") _unitCapMult2Way = capVal; else _unitCapMult3Way = capVal;
                            // Takes effect immediately for the running match; no OM re-apply needed.
                            ApplyUnitCapMultiplier("menu edit");
                        }
                    }
                    else if (state.PendingParamKey.StartsWith("countcap_"))
                    {
                        string ccKey = state.PendingParamKey.Substring(9); // "shrimp" / "harvester"
                        int ccVal = (int)Math.Round(state.PendingValue);
                        ok = WriteCountCapToJson(ccKey, ccVal);
                        if (ok)
                        {
                            if (ccKey == "shrimp") _countCapShrimp = ccVal; else _countCapHarvester = ccVal;
                            // Enforced live by the Construct prefix — nothing to re-apply.
                        }
                    }
                    else if (state.PendingParamKey == "queensmax")
                    {
                        int qv = (int)Math.Round(state.PendingValue);
                        ok = WriteNumberToJson("alien_queens_max", qv);
                        if (ok)
                        {
                            _alienQueensMax = qv < 0 ? -1 : qv;
                            var nest = ApplyAlienQueensMax();      // direct prefab mutation — immediate
                            if (nest != null)                      // drop only the Nest's clones made before the change
                                FlushPrefabPool("alien_queens_max edit", new[] { nest });
                        }
                    }
                    else if (state.PendingParamKey == "nestreach")
                    {
                        ok = WriteNumberToJson("alien_nest_reach_radius", state.PendingValue);
                        if (ok)
                            _alienNestReachRadius = state.PendingValue < 1f ? -1f : state.PendingValue;
                        // Enforced live by the Construct prefix — nothing to re-apply.
                    }
                    else if (state.PendingParamKey.StartsWith("decay_"))
                    {
                        // "decay_human_delay" → factionKey="human", paramKey="delay"
                        string decayRest = state.PendingParamKey.Substring(6); // "human_delay"
                        int sep = decayRest.IndexOf('_');
                        string factionKey = decayRest.Substring(0, sep);
                        string dpKey = decayRest.Substring(sep + 1);
                        ok = WriteDecayToJson(factionKey, dpKey, state.PendingValue);
                        // Update in-memory state
                        if (ok)
                        {
                            var ds = factionKey == "human" ? _decayHuman : _decayAlien;
                            switch (dpKey)
                            {
                                case "delay": ds.Delay = state.PendingValue; break;
                                case "tick": ds.Tick = state.PendingValue; break;
                                case "amount_pct": ds.AmountPct = state.PendingValue; break;
                                case "randomize_pct": ds.RandomizePct = state.PendingValue; break;
                            }
                        }
                    }
                    else
                        ok = WriteParamToJson(unitName, state.PendingParamKey, state.PendingValue);
                    // Capture before clearing — display label depends on these flags.
                    bool wasTechTier = state.PendingTechTierKey != null;
                    bool wasTechCost = state.PendingIsTechCost;
                    state.PendingConfirm = false;
                    state.PendingUnitName = null;
                    state.PendingTechTierKey = null;
                    state.PendingIsTechCost = false;

                    if (ok)
                    {
                        string playerName = GetPlayerName(player);
                        string steamId = GetPlayerSteamId(player);
                        string newValStr = state.PendingValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string displayLabel = wasTechTier
                            ? ((wasTechCost ? "tech_cost." : "tech_time.") + state.PendingParamKey)
                            : (unitName + " " + state.PendingParamKey);
                        SendChatToPlayer(player, _chatPrefix + "<color=#55FF55>Saved:</color> " + displayLabel + " " + _valueColor + FmtBoolCompact(state.PendingParamKey, state.PendingOldVal) + "</color> -> " + _valueColor + FmtBoolCompact(state.PendingParamKey, newValStr) + "</color>");
                        WriteAuditLog(playerName, steamId, unitName, state.PendingParamKey, state.PendingOldVal, newValStr);
                        MelonLogger.Msg($"[BAL] {playerName} ({steamId}): {displayLabel} {state.PendingOldVal} -> {newValStr}");

                        if (doRebalance)
                        {
                            SendChatToPlayer(player, _chatPrefix + _dimColor + "Applying rebalance...</color>");
                            OnRebalanceCommand(player, "rebalance");
                        }
                        else
                            SendChatToPlayer(player, _chatPrefix + _dimColor + "Use !rebalance to apply.</color>");
                    }
                    else
                        SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Failed to write config.</color>");
                    return;
                }
                else if (doCancel)
                {
                    state.PendingConfirm = false;
                    state.PendingUnitName = null;
                    state.PendingTechTierKey = null;
                    state.PendingIsTechCost = false;
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Cancelled.</color>");
                    return;
                }
                else
                {
                    state.PendingConfirm = false;
                    state.PendingUnitName = null;
                    state.PendingTechTierKey = null;
                    state.PendingIsTechCost = false;
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Cancelled.</color>");
                    // Fall through to process as normal input
                }
            }

            // Handle JSON pending save name input
            if (state.JsonPendingSave)
            {
                state.JsonPendingSave = false;
                // "1" = save without name, anything else = the name
                string saveName = (arg == "1") ? "" : input.Trim();
                bool ok = SaveConfigAs(saveName);
                if (ok)
                {
                    string ts = DateTime.Now.ToString("yyyyMMddHHmm");
                    string fileName = string.IsNullOrEmpty(saveName) ? ts + ".json" : ts + "_" + SanitizeFileName(saveName) + ".json";
                    SendChatToPlayer(player, _chatPrefix + "<color=#55FF55>Saved:</color> " + fileName);
                    string playerName = GetPlayerName(player);
                    string steamId = GetPlayerSteamId(player);
                    MelonLogger.Msg($"[JSON] {playerName} ({steamId}) saved config as {fileName}");
                }
                else
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Failed to save config.</color>");
                ShowCurrentMenu(player, state);
                return;
            }

            // Handle JSON action confirmation (blank reset or load)
            if (!string.IsNullOrEmpty(state.JsonPendingAction))
            {
                bool doConfirm = arg == "1" || arg.Equals("yes", StringComparison.OrdinalIgnoreCase);
                bool doCancel = arg == "2" || arg.Equals("no", StringComparison.OrdinalIgnoreCase);
                string action = state.JsonPendingAction;
                string file = state.JsonPendingFile;
                state.JsonPendingAction = null;
                state.JsonPendingFile = null;

                if (doConfirm)
                {
                    bool ok = false;
                    if (action == "blank")
                    {
                        ok = ResetToBlankConfig();
                        if (ok)
                        {
                            SendChatToPlayer(player, _chatPrefix + "<color=#55FF55>Config reset to vanilla.</color> Use !rebalance to apply.");
                            string pn = GetPlayerName(player);
                            string sid = GetPlayerSteamId(player);
                            MelonLogger.Msg($"[JSON] {pn} ({sid}) reset config to blank");
                        }
                        else
                            SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Failed to reset config.</color>");
                    }
                    else if (action == "load" && file != null)
                    {
                        ok = LoadSavedConfig(file);
                        if (ok)
                        {
                            SendChatToPlayer(player, _chatPrefix + "<color=#55FF55>Loaded:</color> " + file);
                            SendChatToPlayer(player, _chatPrefix + _dimColor + "Use !rebalance to apply.</color>");
                            string pn = GetPlayerName(player);
                            string sid = GetPlayerSteamId(player);
                            MelonLogger.Msg($"[JSON] {pn} ({sid}) loaded config: {file}");
                        }
                        else
                            SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Failed to load config.</color>");
                    }
                    ShowCurrentMenu(player, state);
                    return;
                }
                else
                {
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Cancelled.</color>");
                    if (!doCancel)
                    {
                        // Fall through to normal processing
                    }
                    else
                    {
                        ShowCurrentMenu(player, state);
                        return;
                    }
                }
            }

            // "exit" → leave menu
            if (arg.Equals("exit", StringComparison.OrdinalIgnoreCase) || arg == "0")
            {
                _menuStates.Remove(key);
                SendChatToPlayer(player, _chatPrefix + _dimColor + "Exited balance editor.</color>");
                return;
            }

            // "back" → go up
            if (arg.Equals("back", StringComparison.OrdinalIgnoreCase))
            {
                // Clear any pending JSON state
                state.JsonPendingSave = false;
                state.JsonPendingAction = null;
                state.JsonPendingFile = null;

                if (state.Level == MenuLevel.Root)
                {
                    _menuStates.Remove(key);
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Exited balance editor.</color>");
                    return;
                }
                // Non-linear branches return to their parent
                if (state.Level == MenuLevel.JsonMenu)
                    state.Level = MenuLevel.Root;
                else if (state.Level == MenuLevel.JsonLoad)
                    state.Level = MenuLevel.JsonMenu;
                else if (state.Level == MenuLevel.HTPMenu)
                    state.Level = MenuLevel.Root;
                else if (state.Level == MenuLevel.HTPHoverbike)
                    state.Level = MenuLevel.HTPMenu;
                else if (state.Level == MenuLevel.HTPHoverbikeParam)
                    state.Level = MenuLevel.HTPHoverbike;
                else if (state.Level == MenuLevel.HTPTier)
                    state.Level = MenuLevel.HTPMenu;
                else if (state.Level == MenuLevel.HTPTierTime)
                    state.Level = MenuLevel.HTPTier;
                else if (state.Level == MenuLevel.HTPTierCost)
                    state.Level = MenuLevel.HTPTier;
                else if (state.Level == MenuLevel.HTPTeleport)
                    state.Level = MenuLevel.HTPMenu;
                else if (state.Level == MenuLevel.CapDecayMenu || state.Level == MenuLevel.StrategyMenu)
                    state.Level = MenuLevel.Root;
                else if (state.Level == MenuLevel.HTPDecay)
                    state.Level = MenuLevel.CapDecayMenu;
                else if (state.Level == MenuLevel.HTPDecayFaction)
                    state.Level = MenuLevel.HTPDecay;
                else if (state.Level == MenuLevel.HTPUnitCap)
                    state.Level = MenuLevel.CapDecayMenu;
                else if (state.Level == MenuLevel.CountCap)
                    state.Level = MenuLevel.CapDecayMenu;
                else if (state.Level == MenuLevel.NestReach || state.Level == MenuLevel.QueensMax)
                    state.Level = MenuLevel.StrategyMenu;
                else
                    state.Level = (MenuLevel)((int)state.Level - 1);
                ShowCurrentMenu(player, state);
                return;
            }

            // Parse number
            if (!int.TryParse(arg, out int selection) || selection < 1)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Use /1-/20, /back, or /0 to exit</color>");
                return;
            }

            // At ParamGroup level with value: "1 1.5"
            if (state.Level == MenuLevel.ParamGroup && !string.IsNullOrEmpty(arg2))
            {
                HandleParamEdit(player, state, selection, arg2);
                return;
            }

            // HTP Hoverbike param edit: same as regular param edit but for "Hover Bike"
            if (state.Level == MenuLevel.HTPHoverbikeParam && !string.IsNullOrEmpty(arg2))
            {
                HandleHTPHoverbikeEdit(player, state, selection, arg2);
                return;
            }

            // HTP Tier > Time: "/1 45" sets tier_1 time to 45 seconds
            if (state.Level == MenuLevel.HTPTierTime && !string.IsNullOrEmpty(arg2))
            {
                HandleHTPTierEdit(player, state, selection, arg2);
                return;
            }

            // HTP Tier > Cost: "/1 120" sets tier_1 cost to 120 (-1 = vanilla)
            if (state.Level == MenuLevel.HTPTierCost && !string.IsNullOrEmpty(arg2))
            {
                HandleHTPTierCostEdit(player, state, selection, arg2);
                return;
            }

            // HTP Teleport edit: "/1 90" sets cooldown, "/2 3" sets duration
            if (state.Level == MenuLevel.HTPTeleport && !string.IsNullOrEmpty(arg2))
            {
                HandleHTPTeleportEdit(player, state, selection, arg2);
                return;
            }

            // HTP Decay faction edit: "/2 30" sets delay=30, "/4 0.05" sets amount_pct=0.05
            if (state.Level == MenuLevel.HTPDecayFaction && !string.IsNullOrEmpty(arg2))
            {
                HandleHTPDecayEdit(player, state, selection, arg2);
                return;
            }

            // Cap&Decay Unit Cap Mult edit: "/1 3" sets the page's mode (2-way or 3-way) to x3
            if (state.Level == MenuLevel.HTPUnitCap && !string.IsNullOrEmpty(arg2))
            {
                HandleHTPUnitCapEdit(player, state, selection, arg2);
                return;
            }

            // Cap&Decay count cap edit: "/1 12" caps the page's unit (Shrimp / Harvester) at 12
            if (state.Level == MenuLevel.CountCap && !string.IsNullOrEmpty(arg2))
            {
                HandleCountCapEdit(player, state, selection, arg2);
                return;
            }

            // Cap&Decay nest reach edit: "/1 250" limits Node/Bio Cache placement to 250 m around a Nest
            if (state.Level == MenuLevel.NestReach && !string.IsNullOrEmpty(arg2))
            {
                HandleNestReachEdit(player, state, selection, arg2);
                return;
            }

            // Cap&Decay queens edit: "/1 0" = every Nest gets a Queen, "/1 3" = at most 3, "/1 -1" = vanilla
            if (state.Level == MenuLevel.QueensMax && !string.IsNullOrEmpty(arg2))
            {
                HandleQueensMaxEdit(player, state, selection, arg2);
                return;
            }

            // Navigate deeper
            switch (state.Level)
            {
                case MenuLevel.Root:
                    if (selection == 4)
                    {
                        state.Level = MenuLevel.JsonMenu;
                        break;
                    }
                    if (selection == 5)
                    {
                        state.Level = MenuLevel.HTPMenu;
                        break;
                    }
                    if (selection == 6)
                    {
                        state.Level = MenuLevel.CapDecayMenu;
                        break;
                    }
                    if (selection == 7)
                    {
                        state.Level = MenuLevel.StrategyMenu;
                        break;
                    }
                    if (selection < 1 || selection > 7) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-7.</color>"); return; }
                    state.FactionIdx = selection - 1;
                    state.Level = MenuLevel.Faction;
                    break;
                case MenuLevel.Faction:
                    var cats = _categoryNames[state.FactionIdx];
                    if (selection < 1 || selection > cats.Length) { SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{cats.Length}.</color>"); return; }
                    state.CategoryIdx = selection - 1;
                    state.Level = MenuLevel.Category;
                    break;
                case MenuLevel.Category:
                    var units = _unitNames[state.FactionIdx][state.CategoryIdx];
                    if (selection < 1 || selection > units.Length) { SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{units.Length}.</color>"); return; }
                    state.UnitIdx = selection - 1;
                    state.Level = MenuLevel.Unit;
                    break;
                case MenuLevel.Unit:
                {
                    string uName = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];
                    GetUnitParamGroups(uName, out string[] dynNames, out string[][] _dk);
                    if (selection < 1 || selection > dynNames.Length) { SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{dynNames.Length}.</color>"); return; }
                    state.ParamGroupIdx = selection - 1;
                    state.Level = MenuLevel.ParamGroup;
                    break;
                }
                case MenuLevel.ParamGroup:
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: /1 1.5 (or /b 1 1.5)</color>");
                    return;

                case MenuLevel.JsonMenu:
                {
                    if (selection < 1 || selection > 3) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-3.</color>"); return; }
                    if (selection == 1)
                    {
                        // Blank reset — ask for confirmation
                        state.JsonPendingAction = "blank";
                        SendChatToPlayer(player, _chatPrefix + _headerColor + "Reset to blank?</color> All current settings will be lost!");
                        SendChatToPlayer(player, _chatPrefix + _itemColor + "/1</color>=Confirm  " + _itemColor + "/2</color>=Cancel");
                        return;
                    }
                    else if (selection == 2)
                    {
                        // Save — ask for name
                        state.JsonPendingSave = true;
                        SendChatToPlayer(player, _chatPrefix + "Enter save name " + _dimColor + "(e.g. /b myconfig)</color> or " + _itemColor + "/1</color> for timestamp only");
                        return;
                    }
                    else // selection == 3
                    {
                        // Load — show file list
                        state.JsonLoadFileList = GetSavedConfigs();
                        state.Level = MenuLevel.JsonLoad;
                        break;
                    }
                }

                case MenuLevel.JsonLoad:
                {
                    if (state.JsonLoadFileList == null || state.JsonLoadFileList.Length == 0)
                    {
                        SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>No saved configs.</color>");
                        return;
                    }
                    if (selection < 1 || selection > state.JsonLoadFileList.Length)
                    {
                        SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{state.JsonLoadFileList.Length}.</color>");
                        return;
                    }
                    string selectedFile = state.JsonLoadFileList[selection - 1];
                    state.JsonPendingAction = "load";
                    state.JsonPendingFile = selectedFile;
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "Load " + selectedFile + "?</color>");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "/1</color>=Confirm  " + _itemColor + "/2</color>=Cancel");
                    return;
                }

                // ── HTP Menu ──────────────────────────────────────────
                case MenuLevel.HTPMenu:
                    if (selection < 1 || selection > 7) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-7.</color>"); return; }
                    if (selection == 1) state.Level = MenuLevel.HTPHoverbike;
                    else if (selection == 2) state.Level = MenuLevel.HTPTier;
                    else if (selection == 3) state.Level = MenuLevel.HTPTeleport;
                    else if (selection == 6) { state.Level = MenuLevel.DiscordMenu; break; }
                    else if (selection == 4)
                    {
                        // Toggle shrimp aim disable
                        _shrimpDisableAim = !_shrimpDisableAim;
                        WriteBoolToJson("shrimp_disable_aim", _shrimpDisableAim);
                        string newStatus = _shrimpDisableAim ? "<color=#FF5555>OFF</color>" : "<color=#55FF55>ON</color>";
                        SendChatToPlayer(player, _chatPrefix + "Shrimp Aim: " + newStatus + " " + _dimColor + "(use !rebalance to apply)</color>");
                        string playerName = GetPlayerName(player);
                        string steamId = GetPlayerSteamId(player);
                        WriteAuditLog(playerName, steamId, "shrimp", "disable_aim", (!_shrimpDisableAim).ToString(), _shrimpDisableAim.ToString());
                        MelonLogger.Msg($"[BAL] {playerName} ({steamId}): shrimp_disable_aim -> {_shrimpDisableAim}");
                    }
                    else if (selection == 5)
                    {
                        // Toggle additional spawn
                        _additionalSpawn = !_additionalSpawn;
                        WriteBoolToJson("additional_spawn", _additionalSpawn);
                        string spawnStatus = _additionalSpawn ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + "Additional Spawn: " + spawnStatus + " " + _dimColor + "(applies next game)</color>");
                        string playerName2 = GetPlayerName(player);
                        string steamId2 = GetPlayerSteamId(player);
                        WriteAuditLog(playerName2, steamId2, "htp", "additional_spawn", (!_additionalSpawn).ToString(), _additionalSpawn.ToString());
                        MelonLogger.Msg($"[BAL] {playerName2} ({steamId2}): additional_spawn -> {_additionalSpawn}");
                    }
                    else if (selection == 7)
                    {
                        // Toggle watchdog
                        _watchdogEnabled = !_watchdogEnabled;
                        WriteBoolToJson("watchdog_enabled", _watchdogEnabled);
                        string wdStatus = _watchdogEnabled ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + "Round Watchdog: " + wdStatus);
                        string playerName3 = GetPlayerName(player);
                        string steamId3 = GetPlayerSteamId(player);
                        WriteAuditLog(playerName3, steamId3, "htp", "watchdog_enabled", (!_watchdogEnabled).ToString(), _watchdogEnabled.ToString());
                        MelonLogger.Msg($"[BAL] {playerName3} ({steamId3}): watchdog_enabled -> {_watchdogEnabled}");
                    }
                    break;

                // ── Cap&Decay Menu ────────────────────────────────────
                case MenuLevel.CapDecayMenu:
                    if (selection < 1 || selection > 6) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-6.</color>"); return; }
                    if (selection == 1)
                    {
                        state.Level = MenuLevel.HTPDecay;
                    }
                    else if (selection == 2)
                    {
                        // Toggle player-controlled infantry cap exemption
                        _playerInfantryIgnoreCap = !_playerInfantryIgnoreCap;
                        WriteBoolToJson("player_infantry_ignore_cap", _playerInfantryIgnoreCap);
                        string capStatus = _playerInfantryIgnoreCap ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + "Player Cap Exempt: " + capStatus + " " + _dimColor + "(applies immediately)</color>");
                        string pn2 = GetPlayerName(player);
                        string sid2 = GetPlayerSteamId(player);
                        WriteAuditLog(pn2, sid2, "capdecay", "player_infantry_ignore_cap", (!_playerInfantryIgnoreCap).ToString(), _playerInfantryIgnoreCap.ToString());
                        MelonLogger.Msg($"[BAL] {pn2} ({sid2}): player_infantry_ignore_cap -> {_playerInfantryIgnoreCap}");
                    }
                    else if (selection == 3 || selection == 4)
                    {
                        state.HTPUnitCapModeIdx = selection - 3; // 0 = 2-way, 1 = 3-way
                        state.Level = MenuLevel.HTPUnitCap;
                    }
                    else if (selection == 5 || selection == 6)
                    {
                        state.CountCapIdx = selection - 5; // 0 = Shrimp, 1 = Harvester
                        state.Level = MenuLevel.CountCap;
                    }
                    break;

                // ── Strategystyle Menu (alien nest-centric build rules) ──
                case MenuLevel.StrategyMenu:
                    if (selection < 1 || selection > 5) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-5.</color>"); return; }
                    if (selection == 1)
                    {
                        // Toggle nest-only alien building (anchor flag off for every alien structure but the Nest)
                        _alienNestOnlyBuild = !_alienNestOnlyBuild;
                        WriteBoolToJson("alien_nest_only_build", _alienNestOnlyBuild);
                        string nestStatus = _alienNestOnlyBuild ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + "Alien Nest-Only Build: " + nestStatus + " " + _dimColor + "(use !rebalance to apply)</color>");
                        string pn1 = GetPlayerName(player);
                        string sid1 = GetPlayerSteamId(player);
                        WriteAuditLog(pn1, sid1, "strategy", "alien_nest_only_build", (!_alienNestOnlyBuild).ToString(), _alienNestOnlyBuild.ToString());
                        MelonLogger.Msg($"[BAL] {pn1} ({sid1}): alien_nest_only_build -> {_alienNestOnlyBuild}");
                    }
                    else if (selection == 2)
                    {
                        state.Level = MenuLevel.NestReach;
                    }
                    else if (selection == 3)
                    {
                        state.Level = MenuLevel.QueensMax;
                    }
                    else if (selection == 4)
                    {
                        // Toggle finished-anchor-only Nest placement (checked live on every Nest placement — no !rebalance needed)
                        _alienNestFinishedAnchorOnly = !_alienNestFinishedAnchorOnly;
                        WriteBoolToJson("alien_nest_finished_anchor_only", _alienNestFinishedAnchorOnly);
                        string nestAnchorStatus = _alienNestFinishedAnchorOnly ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + "Alien Nest Finished-Anchor Only: " + nestAnchorStatus + " " + _dimColor + "(applies immediately)</color>");
                        string pn4 = GetPlayerName(player);
                        string sid4 = GetPlayerSteamId(player);
                        WriteAuditLog(pn4, sid4, "strategy", "alien_nest_finished_anchor_only", (!_alienNestFinishedAnchorOnly).ToString(), _alienNestFinishedAnchorOnly.ToString());
                        MelonLogger.Msg($"[BAL] {pn4} ({sid4}): alien_nest_finished_anchor_only -> {_alienNestFinishedAnchorOnly}");
                    }
                    else if (selection == 5)
                    {
                        // Toggle per-Nest Queen logistics (checked live on every build request — no !rebalance needed)
                        _alienQueenLogisticsPerNest = !_alienQueenLogisticsPerNest;
                        WriteBoolToJson("alien_queen_logistics_per_nest", _alienQueenLogisticsPerNest);
                        string qlStatus = _alienQueenLogisticsPerNest ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + "Queen Logistics per Nest: " + qlStatus + " " + _dimColor + "(applies immediately)</color>");
                        if (_alienQueenLogisticsPerNest && _alienQueensMax == -1)
                            SendChatToPlayer(player, _chatPrefix + "<color=#FFAA00>Note:</color> " + _dimColor + "Alien Queens is vanilla (-1) — expansion Nests get no Queen and could never build. Set 3. Alien Queens to 0.</color>");
                        string pn5 = GetPlayerName(player);
                        string sid5 = GetPlayerSteamId(player);
                        WriteAuditLog(pn5, sid5, "strategy", "alien_queen_logistics_per_nest", (!_alienQueenLogisticsPerNest).ToString(), _alienQueenLogisticsPerNest.ToString());
                        MelonLogger.Msg($"[BAL] {pn5} ({sid5}): alien_queen_logistics_per_nest -> {_alienQueenLogisticsPerNest}");
                    }
                    break;

                case MenuLevel.HTPUnitCap:
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 <mult> (e.g. .1 3 = x3, .1 -1 = server, .1 0 = unlimited)</color>");
                    return;

                case MenuLevel.CountCap:
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 <max> (e.g. .1 12 = at most 12, .1 -1 = unlimited)</color>");
                    return;

                case MenuLevel.NestReach:
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 <metres> (e.g. .1 250 = 250 m around a Nest, .1 -1 = vanilla)</color>");
                    return;

                case MenuLevel.QueensMax:
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 <n> (.1 0 = every Nest gets a Queen, .1 3 = at most 3, .1 -1 = vanilla)</color>");
                    return;

                case MenuLevel.HTPDecay:
                {
                    if (selection < 1 || selection > 2) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-2.</color>"); return; }
                    state.HTPDecayFactionIdx = selection - 1; // 0=Human, 1=Alien
                    state.Level = MenuLevel.HTPDecayFaction;
                    break;
                }

                case MenuLevel.HTPDecayFaction:
                {
                    bool isHumanDF = state.HTPDecayFactionIdx == 0;
                    string factionKeyDF = isHumanDF ? "human" : "alien";
                    var dsDF = isHumanDF ? _decayHuman : _decayAlien;
                    string fLabelDF = isHumanDF ? "Human" : "Alien";

                    if (selection == 1)
                    {
                        // Toggle enabled
                        dsDF.Enabled = !dsDF.Enabled;
                        WriteDecayToJson(factionKeyDF, "enabled", dsDF.Enabled ? 1f : 0f);
                        string enabledStr = dsDF.Enabled ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + fLabelDF + " Decay: " + enabledStr + " " + _dimColor + "(use !rebalance to apply)</color>");
                        string pn = GetPlayerName(player);
                        string sid = GetPlayerSteamId(player);
                        WriteAuditLog(pn, sid, "decay_" + factionKeyDF, "enabled", (!dsDF.Enabled).ToString(), dsDF.Enabled.ToString());
                        MelonLogger.Msg($"[BAL] {pn} ({sid}): decay.{factionKeyDF}.enabled -> {dsDF.Enabled}");
                    }
                    else if (selection == 6)
                    {
                        // Toggle keep_production
                        dsDF.KeepProduction = !dsDF.KeepProduction;
                        WriteDecayToJson(factionKeyDF, "keep_production", dsDF.KeepProduction ? 1f : 0f);
                        string kpStr = dsDF.KeepProduction ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + fLabelDF + " Keep Production: " + kpStr + " " + _dimColor + "(applies immediately via Harmony)</color>");
                        string pn2 = GetPlayerName(player);
                        string sid2 = GetPlayerSteamId(player);
                        WriteAuditLog(pn2, sid2, "decay_" + factionKeyDF, "keep_production", (!dsDF.KeepProduction).ToString(), dsDF.KeepProduction.ToString());
                        MelonLogger.Msg($"[BAL] {pn2} ({sid2}): decay.{factionKeyDF}.keep_production -> {dsDF.KeepProduction}");
                    }
                    else
                    {
                        SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .2 30 (or !b 2 30)</color>");
                    }
                    return;
                }

                case MenuLevel.HTPHoverbike:
                {
                    GetUnitParamGroups("Hover Bike", out string[] hbNames, out string[][] _hk);
                    if (selection < 1 || selection > hbNames.Length) { SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{hbNames.Length}.</color>"); return; }
                    state.HTPParamGroupIdx = selection - 1;
                    state.Level = MenuLevel.HTPHoverbikeParam;
                    break;
                }

                case MenuLevel.HTPHoverbikeParam:
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: /1 1.5 (or /b 1 1.5)</color>");
                    return;

                case MenuLevel.HTPTier:
                {
                    // Picker: 1=Time, 2=Cost
                    if (selection == 1) { state.Level = MenuLevel.HTPTierTime; ShowCurrentMenu(player, state); return; }
                    if (selection == 2) { state.Level = MenuLevel.HTPTierCost; ShowCurrentMenu(player, state); return; }
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1 (Time) or 2 (Cost).</color>");
                    return;
                }

                case MenuLevel.HTPTierTime:
                {
                    if (selection < 1 || selection > 8) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-8.</color>"); return; }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: ." + selection + " <seconds> (or !b " + selection + " <seconds>)</color>");
                    return;
                }

                case MenuLevel.HTPTierCost:
                {
                    if (selection < 1 || selection > 8) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-8.</color>"); return; }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: ." + selection + " <resource>  ·  -1 = vanilla  (or !b " + selection + " <resource>)</color>");
                    return;
                }

                case MenuLevel.HTPTeleport:
                {
                    // 1=cooldown, 2=duration
                    if (selection < 1 || selection > 2) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-2.</color>"); return; }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: ." + selection + " <value> (or !b " + selection + " <value>)</color>");
                    return;
                }

                case MenuLevel.DiscordMenu:
                {
                    if (selection < 1 || selection > 2) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-2.</color>"); return; }
                    if (selection == 1)
                    {
                        _discordAutoPost = !_discordAutoPost;
                        WriteBoolToJson("discord_auto_post", _discordAutoPost);
                        string autoStatus = _discordAutoPost ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";
                        SendChatToPlayer(player, _chatPrefix + "Auto-post on Rebalance: " + autoStatus);
                        string playerName = GetPlayerName(player);
                        string steamId = GetPlayerSteamId(player);
                        WriteAuditLog(playerName, steamId, "discord", "auto_post", (!_discordAutoPost).ToString(), _discordAutoPost.ToString());
                        MelonLogger.Msg($"[BAL] {playerName} ({steamId}): discord_auto_post -> {_discordAutoPost}");
                    }
                    else // selection == 2
                    {
                        SendChatToPlayer(player, _chatPrefix + "Posting balance changes to Discord...");
                        PostBalanceChangesToDiscord();
                    }
                    break;
                }
            }

            ShowCurrentMenu(player, state);
        }

        // Fix vanilla construction health transfer bug:
        // ConstructionSite.SpawnObject transfers Health01 (ratio) to the spawned building.
        // If DamageManagerData.Health changed between Init and completion (e.g., due to OM overrides),
        // HealthAddRemaining was computed with old MaxHealth but Health01 divides by new MaxHealth → ratio < 1.0.
        // Fix: ensure construction site's health is at MaxHealth right before spawn.
        private static class Patch_ConstructionSpawn
        {
            private static MethodInfo _healthSetter;

            public static void Prefix(object __instance)
            {
                return; // DISABLED FOR TESTING — checking if DamageDisabled removal fixes root cause
                try
                {
                    var dm = ((UnityEngine.Component)__instance).GetComponent<DamageManager>();
                    if (dm == null) return;

                    float maxHP = dm.MaxHealth;
                    if (maxHP <= 0) return;
                    float health01 = dm.Health / maxHP;
                    if (health01 >= 0.999f) return; // Already at full — nothing to fix

                    // Force health to max before spawn transfer via reflection (setter is private)
                    if (_healthSetter == null)
                    {
                        var prop = typeof(DamageManager).GetProperty("Health",
                            BindingFlags.Public | BindingFlags.Instance);
                        _healthSetter = prop?.GetSetMethod(true);
                    }
                    if (_healthSetter != null)
                    {
                        _healthSetter.Invoke(dm, new object[] { maxHP });
                        MelonLogger.Msg($"[HEALTH-FIX] Construction site Health01={health01:F3} at completion — forced to 1.0 (MaxHealth={maxHP:F0})");
                    }
                }
                catch { }
            }
        }


        // =============================================
        // THE DEFERRED BASE SPAWN (Beta 0.9.47)
        //
        // Up to 0.9.46, and still on the game's main branch, MP_Strategy.
        // SetTeamVersusMode called SpawnBaseStructures() inline: the base
        // structures existed the moment the versus mode was set, long after our
        // overrides had been applied at level load, so the starter HQ was
        // instantiated from prefabs that already carried the configured values.
        //
        // 0.9.47 split that apart. SetTeamVersusMode now only sets
        // QueueCoopSpawnBaseStructures, and the spawn fires later from
        // OnPlayerChangedTeam — the instant a player picks a team, which is a
        // moment nothing in this mod anticipates and which can fall anywhere in
        // the lobby. The starter HQ kept coming out with the vanilla
        // FogOfWarViewDistance of 600 instead of the configured 700 (DrMuck
        // measured 600 m in game, and [LIVE-FOW] read 600 -> 700 every round:
        // the instance was vanilla until propagation patched it afterwards).
        //
        // So bracket that spawn. The prefix guarantees the prefabs carry our
        // values before anything is instantiated; the postfix propagates to what
        // was just spawned and pushes the overrides to the clients, which render
        // their own copies of these structures.
        //
        // Patched by name: SpawnStructuresAndStartingResources does not exist
        // before 0.9.47, and SpawnBaseStructures is the older entry point, so
        // whichever the running build has is the one that gets bracketed.
        // =============================================
        private static class Patch_BaseSpawn
        {
            public static void Prefix()
            {
                try
                {
                    if (!_enabled || !_configLoaded) return;
                    if (!_overridesApplied)
                    {
                        MelonLogger.Msg("[BASESPAWN] overrides not applied yet at the base spawn — applying now");
                        ApplyOverridesLogic();
                        // ApplyOverridesLogic reverted with notify=true: the clients got the "r"
                        // packet immediately and are vanilla right now, while our re-applied
                        // values sit in OM's queue until the next frame. The base structures are
                        // spawned by the very method we are prefixing, so drain the queue and
                        // push the full set to the clients before that happens. Otherwise the
                        // client builds its HQ from vanilla prefabs and the delayed coroutine
                        // sync (0.5 s later) never reaches that instance.
                        OMDrainQueue();
                        SyncOverridesNow("base spawn");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[BASESPAWN] prefix threw: " + ex.Message); }
            }

            public static void Postfix()
            {
                try
                {
                    if (!_enabled || !_configLoaded) return;
                    MelonLogger.Msg("[BASESPAWN] base structures spawned — propagating and re-syncing");
                    try { PropagateToLiveInstances(); }
                    catch (Exception pex) { MelonLogger.Warning("[BASESPAWN] propagation threw: " + pex.Message); }
                    ProbeLiveFoW("after-base-spawn");
                    try { MelonCoroutines.Start(SyncOverridesToAllPlayers(0.5f)); }
                    catch (Exception sex) { MelonLogger.Warning("[BASESPAWN] sync threw: " + sex.Message); }
                }
                catch (Exception ex) { MelonLogger.Warning("[BASESPAWN] postfix threw: " + ex.Message); }
            }
        }

        private static class Patch_SendPlayerOverrides
        {
            public static bool Prefix(object __0)
            {
                // Re-entry guard: when we call the original from inside, let it through
                if (_inChunkedSend) return true;

                if (_overrideManagerType == null || _sendPlayerOverridesMethod == null)
                    return true; // fallback to original if reflection not ready

                FieldInfo overridesField = _overrideManagerType.GetField("_overrides",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (overridesField == null) return true;

                var realOverrides = overridesField.GetValue(null) as IDictionary;
                if (realOverrides == null || realOverrides.Count == 0) return false;

                // Find which targets have at least one enabled member
                Type moType = _overrideManagerType.GetNestedType("MemberOverride",
                    BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo enabledProp = moType?.GetProperty("Enabled");

                var activeTargets = new List<object>();
                foreach (DictionaryEntry entry in realOverrides)
                {
                    var members = entry.Value as IDictionary;
                    if (members == null || members.Count == 0) continue;

                    bool hasEnabled = false;
                    if (enabledProp != null)
                    {
                        foreach (DictionaryEntry me in members)
                        {
                            if ((bool)enabledProp.GetValue(me.Value))
                            {
                                hasEnabled = true;
                                break;
                            }
                        }
                    }
                    else
                        hasEnabled = true; // can't check, assume enabled

                    if (hasEnabled)
                        activeTargets.Add(entry.Key);
                }

                if (activeTargets.Count == 0) return false;

                MelonLogger.Msg($"[OverrideSync] Chunking {activeTargets.Count} target groups to player");

                Type dictType = realOverrides.GetType();
                _inChunkedSend = true;
                int sent = 0;
                try
                {
                    foreach (var targetKey in activeTargets)
                    {
                        var tempDict = (IDictionary)Activator.CreateInstance(dictType);
                        tempDict.Add(targetKey, realOverrides[targetKey]);

                        overridesField.SetValue(null, tempDict);
                        _sendPlayerOverridesMethod.Invoke(null, new object[] { __0 });
                        sent++;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[OverrideSync] Chunked send error at target #{sent}: {ex.InnerException?.Message ?? ex.Message}");
                }
                finally
                {
                    _inChunkedSend = false;
                    overridesField.SetValue(null, realOverrides); // always restore
                }

                MelonLogger.Msg($"[OverrideSync] Sent {sent}/{activeTargets.Count} target groups");
                return false; // skip original (we already sent everything)
            }
        }
    }
}

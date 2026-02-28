using HarmonyLib;
using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Game lifecycle hooks
        // =============================================

        private static bool _gameStartedRan; // tracks if Harmony hook fired

        private static class Patch_GameStarted
        {
            public static void Postfix()
            {
                _gameStartedRan = true;
                OnGameStartedLogic();
            }
        }

        // =============================================
        // OverrideManager reflection wrapper
        // =============================================

        private static bool InitOverrideManager()
        {
            if (_omInitialized) return _overrideManagerType != null;

            _omInitialized = true;
            try
            {
                _overrideManagerType = typeof(OverrideManager);

                // Get the EOverrideDataType enum
                var dataTypeEnum = _overrideManagerType.GetNestedType("EOverrideDataType");
                if (dataTypeEnum == null)
                {
                    MelonLogger.Warning("[OverrideManager] EOverrideDataType enum not found");
                    return false;
                }
                _omTypeFloat = Enum.Parse(dataTypeEnum, "Float");
                _omTypeInt = Enum.Parse(dataTypeEnum, "Int");

                // Get Set method: Set(EOverrideDataType type, string target, string memberName, object value, bool enqueue, bool notify)
                _omSetMethod = _overrideManagerType.GetMethod("Set",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { dataTypeEnum, typeof(string), typeof(string), typeof(object), typeof(bool), typeof(bool) },
                    null);
                if (_omSetMethod == null)
                {
                    MelonLogger.Warning("[OverrideManager] Set method not found");
                    return false;
                }

                // Get RevertAll method: RevertAll(bool notify, bool enqueue)
                _omRevertAllMethod = _overrideManagerType.GetMethod("RevertAll",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(bool), typeof(bool) },
                    null);
                if (_omRevertAllMethod == null)
                {
                    MelonLogger.Warning("[OverrideManager] RevertAll method not found");
                    return false;
                }

                MelonLogger.Msg("[OverrideManager] Reflection wrapper initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[OverrideManager] Init error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Register DamageManagerData assets in OverrideManager's AssetSourceRegistry
        /// so that health overrides sync to clients via OM.
        /// </summary>
        private static void RegisterDamageManagerDataInOM()
        {
            try
            {
                // Find AssetSourceRegistry type (nested inside OverrideManager)
                Type asrType = typeof(OverrideManager).GetNestedType("AssetSourceRegistry",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (asrType == null)
                {
                    MelonLogger.Warning("[OM-DMD] AssetSourceRegistry type not found");
                    return;
                }

                // Access private _sources dictionary
                var sourcesField = asrType.GetField("_sources", BindingFlags.NonPublic | BindingFlags.Static);
                if (sourcesField == null)
                {
                    MelonLogger.Warning("[OM-DMD] _sources field not found");
                    return;
                }
                var sources = sourcesField.GetValue(null) as System.Collections.IDictionary;
                if (sources == null)
                {
                    MelonLogger.Warning("[OM-DMD] _sources is null");
                    return;
                }

                // CRITICAL: _sources uses lazy initialization — BuildSources() only runs when
                // _sources.Count == 0. If we add our entry to an empty dict, BuildSources() never
                // runs and the built-in types (ConstructionData, ProjectileData, ObjectInfo) are lost.
                // Force BuildSources() to populate the dict first.
                if (sources.Count == 0)
                {
                    var buildSources = asrType.GetMethod("BuildSources",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (buildSources != null)
                    {
                        buildSources.Invoke(null, null);
                        MelonLogger.Msg($"[OM-DMD] Triggered BuildSources() — {sources.Count} built-in types registered");
                    }
                    else
                    {
                        MelonLogger.Warning("[OM-DMD] BuildSources method not found — built-in OM types may be missing");
                    }
                }

                // Collect all unique DamageManagerData assets from ObjectInfo prefabs
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                var dmdAssets = new List<object>();
                var dmdPaths = new List<string>();
                var seen = new HashSet<string>();

                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    var dm = info.Prefab.GetComponent<DamageManager>();
                    if (dm == null) continue;

                    var dataField = dm.GetType().GetField("Data", BindingFlags.Public | BindingFlags.Instance);
                    if (dataField == null) continue;
                    var dataObj = dataField.GetValue(dm) as UnityEngine.Object;
                    if (dataObj == null) continue;

                    string assetName = dataObj.name;
                    if (string.IsNullOrEmpty(assetName) || seen.Contains(assetName)) continue;
                    seen.Add(assetName);

                    // Synthetic path: "DamageManagerData/DamageManagerData_{name}.asset"
                    // GetAssetPrefix extracts prefix before first '_' = "DamageManagerData"
                    // EndsWith matches our query "DamageManagerData_{name}.asset"
                    string syntheticPath = $"DamageManagerData/DamageManagerData_{assetName}.asset";
                    dmdAssets.Add(dataObj);
                    dmdPaths.Add(syntheticPath);
                }

                if (dmdAssets.Count == 0)
                {
                    MelonLogger.Warning("[OM-DMD] No DamageManagerData assets found");
                    return;
                }

                // Get the DamageManagerData type from the first asset
                Type dmdType = dmdAssets[0].GetType();

                // Build typed lists matching the tuple signature: (Type, IList, IList, MethodInfo)
                var assetListType = typeof(List<>).MakeGenericType(dmdType);
                var assetList = (System.Collections.IList)Activator.CreateInstance(assetListType);
                foreach (var a in dmdAssets) assetList.Add(a);

                var pathList = new List<string>(dmdPaths);

                // Add to _sources with key "DamageManagerData"
                // The value is a ValueTuple<Type, IList, IList, MethodInfo>
                var tupleType = sourcesField.FieldType.GetGenericArguments()[1]; // the value type of the dict
                var tuple = Activator.CreateInstance(tupleType, dmdType, assetList, (System.Collections.IList)pathList, (MethodInfo)null);
                sources["DamageManagerData"] = tuple;

                MelonLogger.Msg($"[OM-DMD] Registered {dmdAssets.Count} DamageManagerData assets in AssetSourceRegistry");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[OM-DMD] Registration failed: {ex.Message}");
            }
        }

        private static bool OMSetFloat(string target, string memberName, float value)
        {
            try
            {
                object result = _omSetMethod.Invoke(null, new object[] { _omTypeFloat, target, memberName, value, true, false });
                bool ok = result is bool b && b;
                if (!ok)
                    MelonLogger.Warning($"[OM] Set failed: Float '{target}'.'{memberName}' = {value}");
                return ok;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[OM] Set exception: Float '{target}'.'{memberName}': {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        private static bool OMSetInt(string target, string memberName, int value)
        {
            try
            {
                object result = _omSetMethod.Invoke(null, new object[] { _omTypeInt, target, memberName, value, true, false });
                bool ok = result is bool b && b;
                if (!ok)
                    MelonLogger.Warning($"[OM] Set failed: Int '{target}'.'{memberName}' = {value}");
                return ok;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[OM] Set exception: Int '{target}'.'{memberName}': {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        private static void OMRevertAll()
        {
            try
            {
                _omRevertAllMethod.Invoke(null, new object[] { true, false });
                MelonLogger.Msg("[OverrideManager] RevertAll called (notify=true)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[OverrideManager] RevertAll error: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private static void OnGameStartedLogic()
        {
            try
            {
                _nameCache.Clear();

                if (_dumpFields && !_fieldsDumped)
                {
                    DumpFieldDiscovery();
                    DumpAllUnitsJson();
                    _fieldsDumped = true;
                }

                if (_enabled && _configLoaded)
                {
                    // Initialize OverrideManager reflection wrapper
                    bool omReady = InitOverrideManager();
                    if (!omReady)
                        MelonLogger.Warning("OverrideManager not available — falling back to direct mutation (no client sync)");

                    // Register DamageManagerData in OM so health syncs to clients
                    if (omReady && _healthMultipliers.Count > 0)
                        RegisterDamageManagerDataInOM();

                    ApplyConstructionDataOverrides(omReady);
                    ApplyHealthOverrides(omReady);
                    ApplyProjectileDamageOverrides(omReady);
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

                    if (_shrimpDisableAim)
                        ApplyShrimpAimDisable();

                    MelonLogger.Msg($"Unit Balance active (OverrideManager={omReady}): " +
                        $"{_damageMultipliers.Count} damage, {_healthMultipliers.Count} health, " +
                        $"{_costMultipliers.Count} cost, {_buildTimeMultipliers.Count} buildTime, " +
                        $"{_rangeMultipliers.Count} range, {_moveSpeedMultipliers.Count} moveSpeed, " +
                        $"{_projectileOverrides.Count} projOverrides, " +
                        $"{_minTierOverrides.Count} minTier, {_techTierTimes.Count} techTime");

                    // Subscribe to tier change events for dispenser LocalTimeout reset
                    if (_minTierOverrides.Count > 0)
                    {
                        _dispenserTierResets.Clear();
                        GameEvents.OnTeamTechnologyTierChanged += OnTeamTierChanged;
                        MelonLogger.Msg("[DISPENSER] Subscribed to tier change events for min_tier unlock");
                    }

                    // Spawn additional units if enabled
                    if (_additionalSpawn)
                        MelonCoroutines.Start(SpawnAdditionalUnits(5f));

                    // Send overrides to all connected players who joined before game start
                    // (they missed the overrides because notify=false during bulk setup)
                    if (omReady)
                        MelonCoroutines.Start(SyncOverridesToAllPlayers(2f));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"GameStarted error: {ex.Message}");
            }
        }

        // Send all OM overrides to each connected player after game start.
        // Calls SendPlayerOverrides per-player (our Prefix chunks it per-target automatically).
        private static IEnumerator SyncOverridesToAllPlayers(float initialDelay)
        {
            if (initialDelay > 0)
                yield return new WaitForSeconds(initialDelay);

            // Resolve Player.Players and NetworkGameServer.GetServerPlayer
            IList playersList = null;
            object serverPlayer = null;
            bool initOk = false;

            try
            {
                Type playerType = typeof(Player);
                Type ngsType = typeof(NetworkGameServer);

                if (_sendPlayerOverridesMethod == null)
                {
                    MelonLogger.Warning("[OverrideSync] Cannot sync: missing Player type or SendPlayerOverrides method");
                }
                else
                {
                    // Get Player.Players (try property, then field)
                    var playersProp = playerType.GetProperty("Players", BindingFlags.Public | BindingFlags.Static);
                    if (playersProp != null)
                        playersList = playersProp.GetValue(null) as IList;
                    else
                    {
                        var playersField = playerType.GetField("Players", BindingFlags.Public | BindingFlags.Static);
                        if (playersField != null)
                            playersList = playersField.GetValue(null) as IList;
                    }

                    // Get server player to skip it
                    if (ngsType != null)
                    {
                        var getServerPlayer = ngsType.GetMethod("GetServerPlayer", BindingFlags.Public | BindingFlags.Static);
                        serverPlayer = getServerPlayer?.Invoke(null, null);
                    }

                    initOk = playersList != null && playersList.Count > 0;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[OverrideSync] Init error: {ex.Message}");
            }

            if (!initOk || playersList == null)
            {
                MelonLogger.Warning("[OverrideSync] No players to sync to");
                yield break;
            }

            // Copy player list (it may change during iteration)
            var players = new List<object>();
            foreach (var p in playersList)
            {
                if (p != null && !p.Equals(serverPlayer))
                    players.Add(p);
            }

            MelonLogger.Msg($"[OverrideSync] Syncing overrides to {players.Count} connected player(s)...");

            int synced = 0;
            for (int i = 0; i < players.Count; i++)
            {
                try
                {
                    // This triggers our Prefix which chunks per-target automatically
                    _sendPlayerOverridesMethod.Invoke(null, new object[] { players[i] });
                    synced++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[OverrideSync] Sync to player #{i} failed: {ex.InnerException?.Message ?? ex.Message}");
                }

                // Yield between players to spread network load
                yield return new WaitForSeconds(0.5f);
            }

            MelonLogger.Msg($"[OverrideSync] Sync complete: {synced}/{players.Count} players");
        }

        // Server-only mod: no client fallback needed.
        // The base game's OnReceiveOverrides() handles client-side override application.

        // =============================================
        // Additional spawn — spawn extra transport/utility units at game start
        // =============================================

        private static readonly (string faction, string unitName, int count)[] _additionalSpawnMap =
        {
            ("Sol", "Platoon Hauler", 3),
            ("Centauri", "Squad Transport", 3),
            ("Alien", "Hunter", 1),
        };

        private static IEnumerator SpawnAdditionalUnits(float delay)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            MelonLogger.Msg("[SPAWN] Additional spawn triggered");

            // Cache ObjectInfo lookup
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            var infoByName = new Dictionary<string, ObjectInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string dn = info.DisplayName;
                if (!string.IsNullOrEmpty(dn) && !infoByName.ContainsKey(dn))
                    infoByName[dn] = info;
            }

            int totalSpawned = 0;

            foreach (var team in Team.Teams)
            {
                if (team == null || team.Structures == null || team.Structures.Count == 0) continue;

                // Determine faction from team name
                string teamId = team.TeamShortName ?? team.name ?? "";
                string matchedFaction = null;
                foreach (var entry in _additionalSpawnMap)
                {
                    if (teamId.IndexOf(entry.faction, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matchedFaction = entry.faction;
                        break;
                    }
                }
                if (matchedFaction == null) continue;

                // Find spawn position 150m in front of the HQ
                Structure baseStruct = team.Structures[0];
                if (baseStruct == null) continue;
                Vector3 hqPos = baseStruct.transform.position;
                Quaternion spawnRot = baseStruct.transform.rotation;
                Vector3 spawnPos = hqPos + baseStruct.transform.forward * 150f;

                foreach (var entry in _additionalSpawnMap)
                {
                    if (!string.Equals(entry.faction, matchedFaction, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!infoByName.TryGetValue(entry.unitName, out ObjectInfo unitInfo))
                    {
                        MelonLogger.Warning($"[SPAWN] ObjectInfo not found for '{entry.unitName}'");
                        continue;
                    }

                    for (int i = 0; i < entry.count; i++)
                    {
                        try
                        {
                            // Offset each unit slightly so they don't stack
                            Vector3 offset = new Vector3((i - 1) * 5f, 0f, 3f);
                            Vector3 pos = spawnPos + spawnRot * offset;
                            Game.SpawnPrefab(unitInfo.Prefab, null, team, pos, spawnRot);
                            totalSpawned++;
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[SPAWN] Failed to spawn '{entry.unitName}' for {teamId}: {ex.Message}");
                        }
                    }

                    MelonLogger.Msg($"[SPAWN] Spawned {entry.count}x {entry.unitName} for {teamId}");
                }
            }

            MelonLogger.Msg($"[SPAWN] Additional spawn complete: {totalSpawned} units");
        }

        private static class Patch_GameEnded
        {
            public static void Postfix()
            {
                MelonLogger.Msg("[UnitBalance] Game ended");

                // Unsubscribe tier change events
                GameEvents.OnTeamTechnologyTierChanged -= OnTeamTierChanged;
                _dispenserTierResets.Clear();

                // Clear direct-mutation caches (objects are destroyed on map change)
                _originalCreatureAttackAimDist.Clear();
                _originalSpread.Clear();
                _originalProjectileLifetimes.Clear();
                _originalProjectileSpeeds.Clear();
                _originalMoveSpeeds.Clear();

                // Revert all OverrideManager overrides (restores originals, notifies clients)
                if (_omInitialized && _overrideManagerType != null)
                {
                    OMRevertAll();
                }
            }
        }

        // =============================================
        // Tier change handler — reset dispenser LocalTimeout once when min_tier is reached
        // =============================================

        private static void OnTeamTierChanged(Team team, int oldTier, int newTier)
        {
            try
            {
                if (_minTierOverrides.Count == 0 || newTier <= oldTier) return;

                foreach (var kvp in _minTierOverrides)
                {
                    string unitName = kvp.Key;
                    int minTier = kvp.Value;
                    if (minTier < 0) continue;

                    // Did we just cross the threshold?
                    if (oldTier < minTier && newTier >= minTier)
                    {
                        string resetKey = $"{team.TeamShortName}_{unitName}";
                        if (_dispenserTierResets.Contains(resetKey)) continue;
                        _dispenserTierResets.Add(resetKey);

                        ResetDispenserLocalTimeout(unitName, team);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DISPENSER] Tier change handler error: {ex.Message}");
            }
        }

        private static void ResetDispenserLocalTimeout(string unitName, Team team)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance;
            var privFlags = BindingFlags.NonPublic | BindingFlags.Instance;

            // Find VehicleDispenser type via reflection
            Type vdType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                vdType = asm.GetType("VehicleDispenser");
                if (vdType != null) break;
            }
            if (vdType == null) return;

            // Find all VehicleDispenser instances (scene + prefab)
            var allDisps = Resources.FindObjectsOfTypeAll(vdType);
            int reset = 0;

            foreach (var dispObj in allDisps)
            {
                if (dispObj == null) continue;
                var disp = dispObj as Component;
                if (disp == null) continue;

                // Check VehicleToDispense.DisplayName matches our unit
                var vtdF = disp.GetType().GetField("VehicleToDispense", flags);
                if (vtdF == null) continue;
                var vtdObj = vtdF.GetValue(disp);
                if (vtdObj == null) continue;

                string dn = null;
                var dnF = vtdObj.GetType().GetField("DisplayName", flags);
                if (dnF != null) dn = dnF.GetValue(vtdObj) as string;
                else
                {
                    var dnP = vtdObj.GetType().GetProperty("DisplayName", flags);
                    if (dnP != null) dn = dnP.GetValue(vtdObj) as string;
                }
                if (!string.Equals(dn, unitName, StringComparison.OrdinalIgnoreCase)) continue;

                // Check team match — only reset dispensers for the team that reached the tier
                var teamProp = disp.GetType().GetProperty("Team", flags);
                if (teamProp != null)
                {
                    var dispTeam = teamProp.GetValue(disp) as Team;
                    if (dispTeam != null && dispTeam != team) continue;
                }

                // Reset LocalTimeout to 0 (private field)
                var ltF = disp.GetType().GetField("LocalTimeout", privFlags);
                if (ltF != null && ltF.FieldType == typeof(float))
                {
                    float oldVal = (float)ltF.GetValue(disp);
                    if (oldVal > 0f)
                    {
                        ltF.SetValue(disp, 0f);
                        MelonLogger.Msg($"[DISPENSER] Reset LocalTimeout on '{unitName}' dispenser ({oldVal:F0}s -> 0s)");
                    }
                    reset++;
                }
            }

            MelonLogger.Msg($"[DISPENSER] Team '{team.TeamShortName}' reached tier for '{unitName}' — checked {reset} dispenser(s)");
        }
    }
}

using HarmonyLib;
using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Game lifecycle hooks
        // =============================================

        private static bool _gameStartedRan; // tracks if Harmony hook fired
        private static int _watchdogGeneration; // incremented each game start to cancel stale watchdogs
        private const float WatchdogTimeoutSeconds = 60f; // time after game end before forcing recovery
        private static bool _watchdogEnabled = true;
        private static bool _watchdogFired; // prevents re-triggering after recovery EndRound
        private static string _lastMapName = ""; // tracks current map to detect map changes

        private static string GetCurrentMapName()
        {
            try
            {
                return SceneManager.GetActiveScene().name ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static class Patch_GameInit
        {
            public static void Postfix()
            {
                // Fires on map load — fresh prefabs loaded
                _watchdogGeneration++;
                _watchdogFired = false;
                _lastMapName = GetCurrentMapName();
                // Fresh map = fresh prefabs — clear vanilla cache so it re-reads from clean prefabs
                _overridesApplied = false;
                _vanillaBaseCache.Clear();
                MelonLogger.Msg($"[UnitBalance] Game init ({_lastMapName}) — applying overrides to fresh prefabs");
                ApplyOverridesLogic();
            }
        }

        private static class Patch_GameRestart
        {
            public static void Postfix()
            {
                _watchdogGeneration++;
                _watchdogFired = false;

                // Detect map change by comparing current map name
                string currentMap = GetCurrentMapName();
                bool mapChanged = !string.Equals(currentMap, _lastMapName, StringComparison.OrdinalIgnoreCase);
                if (mapChanged)
                {
                    MelonLogger.Msg($"[UnitBalance] Game restart — map changed ({_lastMapName} → {currentMap}), re-applying overrides");
                    _lastMapName = currentMap;
                    _overridesApplied = false;
                    _vanillaBaseCache.Clear();
                    ApplyOverridesLogic();
                }
                else
                {
                    MelonLogger.Msg("[UnitBalance] Game restart (same map) — prefabs already modified");
                    // Prefabs still modified from previous round, no re-apply needed
                    if (!_overridesApplied)
                        ApplyOverridesLogic();
                }
            }
        }

        private static class Patch_GameStarted
        {
            public static void Postfix()
            {
                _gameStartedRan = true;
                _watchdogGeneration++; // redundant safety — already cancelled in Init
                _watchdogFired = false;
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

        /// <summary>
        /// Apply all overrides to prefab/asset data. Called during voting phase
        /// (OnGameRestart) so overrides are active before starter units spawn.
        /// Safe to call multiple times — skips if already applied.
        /// </summary>
        private static void ApplyOverridesLogic()
        {
            try
            {
                if (!_enabled || !_configLoaded) return;

                if (_overridesApplied)
                {
                    MelonLogger.Msg("[UnitBalance] Overrides already active — skipping re-apply");
                    return;
                }

                _nameCache.Clear();

                if (_dumpFields && !_fieldsDumped)
                {
                    DumpFieldDiscovery();
                    DumpAllUnitsJson();
                    _fieldsDumped = true;
                }

                // Initialize OverrideManager reflection wrapper
                bool omReady = InitOverrideManager();
                if (!omReady)
                    MelonLogger.Warning("OverrideManager not available — falling back to direct mutation (no client sync)");

                // Revert OM overrides to ensure clean state before applying.
                // On map change: game already reverted in LoadAsyncScene, this ensures
                // our OM state is consistent (notify=true cleans up client state too).
                // On same-map restart: ApplyOverridesLogic is skipped (_overridesApplied=true),
                // so this never fires — no compounding risk.
                if (omReady && _overrideManagerType != null)
                    OMRevertAll();

                // Register DamageManagerData in OM (server-only — clients don't have this type registered)
                if (omReady && _healthMultEnabled && _healthMultipliers.Count > 0)
                    RegisterDamageManagerDataInOM();

                // Snapshot vanilla base values from fresh prefabs (OMRevertAll ensures vanilla state).
                // Only re-read when cache is empty (map change clears it, same-map restart preserves it).
                if (_vanillaBaseCache.Count == 0)
                    CacheVanillaBaseValues();

                ApplyConstructionDataOverrides(omReady);
                if (_healthMultEnabled)
                {
                    ApplyHealthOverrides(omReady);
                    ReclampLiveHealth();
                }
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
                ApplyProximityDetonationOverrides();

                if (_shrimpDisableAim)
                    ApplyShrimpAimDisable();

                _overridesApplied = true;

                MelonLogger.Msg($"[UnitBalance] Overrides applied (OverrideManager={omReady}): " +
                    $"{_damageMultipliers.Count} damage, {_healthMultipliers.Count} health, " +
                    $"{_costMultipliers.Count} cost, {_buildTimeMultipliers.Count} buildTime, " +
                    $"{_rangeMultipliers.Count} range, {_moveSpeedMultipliers.Count} moveSpeed, " +
                    $"{_projectileOverrides.Count} projOverrides, {_proximityOverrides.Count} proximity, " +
                    $"{_minTierOverrides.Count} minTier, {_techTierTimes.Count} techTime");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"ApplyOverridesLogic error: {ex.Message}");
            }
        }

        /// <summary>
        /// Game start: apply overrides, propagate to live instances, spawn extra units, sync clients.
        /// </summary>
        private static void OnGameStartedLogic()
        {
            try
            {
                if (!_enabled || !_configLoaded) return;

                // Safety net: detect map change if Patch_GameInit didn't fire
                string currentMap = GetCurrentMapName();
                if (!string.Equals(currentMap, _lastMapName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(currentMap))
                {
                    MelonLogger.Msg($"[UnitBalance] GameStarted detected map change ({_lastMapName} → {currentMap})");
                    _lastMapName = currentMap;
                    _overridesApplied = false;
                    _vanillaBaseCache.Clear();
                }
                // Apply if not already done
                ApplyOverridesLogic();

                bool omReady = _omInitialized;

                // Propagate overrides to already-spawned instances (starter HQs, etc.)
                // OM only modifies prefab data — live instances have independent component copies
                try { PropagateToLiveInstances(); }
                catch (Exception pex) { MelonLogger.Warning($"[LIVE] Propagation error on game start: {pex.Message}"); }

                MelonLogger.Msg($"[UnitBalance] Game started — propagation complete");

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
            ("Sol", "Platoon Hauler", 2),
            ("Sol", "Light Striker", 2),
            ("Sol", "Heavy Quad", 2),
            ("Centauri", "Squad Transport", 2),
            ("Centauri", "Assault Car", 2),
            ("Centauri", "Heavy Raider", 2),
            ("Alien", "Hunter", 3),
            ("Alien", "Squid", 2),
            ("Alien", "Shocker", 4),
            ("Alien", "Wasp", 1),
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

            // Disable damage during spawn to prevent fall damage on spawned units
            DamageManager.DamageDisabled = true;
            try
            {
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

                    Structure baseStruct = team.Structures[0];
                    if (baseStruct == null) continue;
                    Vector3 hqPos = baseStruct.transform.position;
                    Vector3 hqForward = baseStruct.transform.forward;

                    // Flatten spawn list for this faction so we can distribute evenly
                    var spawnList = new List<(string unitName, ObjectInfo info)>();
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
                            spawnList.Add((entry.unitName, unitInfo));
                    }

                    // Distribute all units evenly in a circle around HQ
                    float radius = 120f;
                    int totalForFaction = spawnList.Count;
                    for (int i = 0; i < totalForFaction; i++)
                    {
                        try
                        {
                            float angleDeg = (360f / totalForFaction) * i;
                            Vector3 dir = Quaternion.Euler(0f, angleDeg, 0f) * hqForward;
                            Vector3 pos = hqPos + dir * radius;

                            // Sample terrain height at spawn position
                            float bestY = pos.y;
                            foreach (var terrain in Terrain.activeTerrains)
                            {
                                if (terrain == null) continue;
                                float ty = terrain.SampleHeight(pos) + terrain.transform.position.y;
                                if (ty > bestY) bestY = ty;
                            }
                            pos.y = bestY + 1f;

                            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
                            Game.SpawnPrefab(spawnList[i].info.Prefab, null, team, pos, rot);
                            totalSpawned++;
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[SPAWN] Failed to spawn '{spawnList[i].unitName}' for {teamId}: {ex.Message}");
                        }
                    }

                    MelonLogger.Msg($"[SPAWN] Spawned {totalForFaction} units for {teamId}");
                }
            }
            finally
            {
                // Keep damage disabled for 20s so spawned units can land without fall damage
                MelonLogger.Msg($"[SPAWN] Additional spawn complete: {totalSpawned} units — damage disabled for 20s");
            }

            yield return new WaitForSeconds(20f);
            DamageManager.DamageDisabled = false;
            MelonLogger.Msg("[SPAWN] Damage re-enabled");
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
                _originalHealth.Clear();
                _originalAimAngle.Clear();

                // Don't reset _overridesApplied or call OMRevertAll — prefabs stay modified
                // between rounds so starter structures inherit overrides on same-map restarts.
                // Map changes are detected via _lastMapName in Patch_GameRestart/GameStarted.

                // Start watchdog — if no new game starts within timeout, force recovery (once only)
                if (_watchdogEnabled && !_watchdogFired)
                    MelonCoroutines.Start(RoundTransitionWatchdog(_watchdogGeneration));
            }
        }

        /// <summary>
        /// Watchdog coroutine: waits after game end with countdown notifications,
        /// then forces EndRound if the next game never starts.
        /// The generation token cancels the watchdog if a new game starts normally.
        /// </summary>
        private static IEnumerator RoundTransitionWatchdog(int generation)
        {
            // Wait initial 20s silently — normal transitions complete well within this
            yield return new WaitForSeconds(20f);
            if (generation != _watchdogGeneration) yield break;

            // Remaining countdown with 10s intervals and player notifications
            float remaining = WatchdogTimeoutSeconds - 20f;
            BroadcastChat($"{_chatPrefix}<color=#FFAA00>[Watchdog] Round transition stalled — auto-recovery in {remaining:0}s</color>");
            MelonLogger.Warning($"[Watchdog] Round transition stalled — countdown started ({remaining}s)");

            while (remaining > 0f)
            {
                float wait = remaining > 10f ? 10f : remaining;
                yield return new WaitForSeconds(wait);
                remaining -= wait;

                if (generation != _watchdogGeneration)
                {
                    BroadcastChat($"{_chatPrefix}<color=#55FF55>[Watchdog] New round detected — watchdog cancelled</color>");
                    MelonLogger.Msg("[Watchdog] New round started — watchdog cancelled");
                    yield break;
                }

                if (remaining > 0f)
                {
                    BroadcastChat($"{_chatPrefix}<color=#FFAA00>[Watchdog] Auto-recovery in {remaining:0}s...</color>");
                }
            }

            // Timeout reached — force recovery (set flag to prevent loop)
            _watchdogFired = true;
            BroadcastChat($"{_chatPrefix}<color=#FF5555>[Watchdog] Forcing round end — server recovery</color>");
            MelonLogger.Warning($"[Watchdog] No new game started within {WatchdogTimeoutSeconds}s — forcing EndRound");

            try
            {
                Type gameModeType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    gameModeType = asm.GetType("GameMode");
                    if (gameModeType != null) break;
                }

                if (gameModeType != null)
                {
                    var currentProp = gameModeType.GetProperty("CurrentGameMode", BindingFlags.Public | BindingFlags.Static);
                    var currentMode = currentProp?.GetValue(null);
                    if (currentMode != null)
                    {
                        var endRound = currentMode.GetType().GetMethod("EndRound", BindingFlags.Public | BindingFlags.Instance);
                        if (endRound != null)
                        {
                            endRound.Invoke(currentMode, null);
                            MelonLogger.Msg("[Watchdog] EndRound() called — server should recover");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Watchdog] Recovery failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a chat message to all connected players (skipping server player).
        /// </summary>
        private static void BroadcastChat(string message)
        {
            try
            {
                Type playerType = typeof(Player);
                IList playersList = null;

                var playersProp = playerType.GetProperty("Players", BindingFlags.Public | BindingFlags.Static);
                if (playersProp != null)
                    playersList = playersProp.GetValue(null) as IList;
                else
                {
                    var playersField = playerType.GetField("Players", BindingFlags.Public | BindingFlags.Static);
                    if (playersField != null)
                        playersList = playersField.GetValue(null) as IList;
                }

                if (playersList == null) return;

                // Get server player to skip
                object serverPlayer = null;
                Type ngsType = typeof(NetworkGameServer);
                var getServerPlayer = ngsType.GetMethod("GetServerPlayer", BindingFlags.Public | BindingFlags.Static);
                serverPlayer = getServerPlayer?.Invoke(null, null);

                foreach (var p in playersList)
                {
                    if (p != null && !p.Equals(serverPlayer))
                        SendChatToPlayer(p, message);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Watchdog] BroadcastChat error: {ex.Message}");
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

/*
 Si_UnitBalance - v6.0.0
 Server-only unit and structure balance mod.
 Uses Silica's built-in OverrideManager to push all parameter changes to clients.
 No client mod needed — the base game handles receiving overrides automatically.

 Parameters:
 - damage_mult: Scales ProjectileData damage fields (ImpactDamage, RicochetDamage, etc.) via OverrideManager
 - health_mult: Scales MaxHealth (via OverrideManager → synced to clients)
 - cost_mult: Scales resource cost (via OverrideManager → synced to clients)
 - build_time_mult: Scales build time (via OverrideManager → synced to clients)
 - min_tier: Override MinimumTeamTier (via OverrideManager → synced to clients)
 - tech_time: Per-tier build time for tech-up research (absolute seconds)
 - range_mult: Scales projectile range/lifetime and sensor/aim distances
 - proj_speed_mult: Scales projectile base speed (separate from range)
 - reload_time_mult: Scales VehicleTurret reload time
 - magazine_mult: Scales VehicleTurret magazine size
 - fire_rate_mult: Scales fire rate (divides PrimaryFireInterval)
 - move_speed_mult: Scales unit movement speed
 - turn_radius_mult: Scales VehicleWheeled TurningCircleRadius (lower = tighter turns)
 - accuracy_mult: Scales VehicleTurret muzzle spread
 - build_radius: Override MaximumBaseStructureDistance (absolute meters)
*/

using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_UnitBalance.UnitBalance), "Unit Balance", "7.0.0", "schwe")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("SilicaCore")]

namespace Si_UnitBalance
{
    public class UnitBalance : MelonMod
    {
        private static string _configPath = "";
        private static bool _configLoaded;
        private static bool _fieldsDumped;

        private static bool _enabled = true;
        private static bool _dumpFields = true;
        private static bool _shrimpDisableAim = false;
        private static bool _harmonyApplied; // true when SilicaCore found (server)

        // OverrideManager reflection cache (resolved once in OnGameStartedLogic)
        private static Type _overrideManagerType;
        private static MethodInfo _omSetMethod;
        private static MethodInfo _omRevertAllMethod;
        private static object _omTypeFloat;
        private static object _omTypeInt;
        private static bool _omInitialized;

        // Override sync: chunked SendPlayerOverrides
        private static MethodInfo _sendPlayerOverridesMethod; // original method (Harmony-patched)
        private static bool _inChunkedSend; // re-entry guard for Prefix

        // Chat command: !rebalance
        // _playerAdminLevelProp removed — admin check handled by AdminMod

        // Unit/structure name -> multipliers
        private static readonly Dictionary<string, float> _damageMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _healthMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _costMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _buildTimeMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // Unit/structure name -> min tier override (-1 = no override)
        private static readonly Dictionary<string, int> _minTierOverrides =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _rangeMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _speedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _reloadTimeMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _moveSpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _accuracyMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _magazineMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _fireRateMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _buildRadiusOverrides =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _turnRadiusMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _turboSpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _targetDistanceOverrides =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Tech tier number (1-8) -> build time in seconds
        private static readonly Dictionary<int, float> _techTierTimes = new Dictionary<int, float>();

        // Original move speed values (keyed by unit+component+field) to prevent compounding
        private static readonly Dictionary<string, float> _originalMoveSpeeds =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Cache: Unity instance ID -> display name
        private static readonly Dictionary<int, string> _nameCache = new Dictionary<int, string>();

        // Per-projectile absolute value overrides: unit name -> { projectile name -> { field name -> value } }
        private static readonly Dictionary<string, Dictionary<string, Dictionary<string, float>>> _projectileOverrides =
            new Dictionary<string, Dictionary<string, Dictionary<string, float>>>(StringComparer.OrdinalIgnoreCase);

        public override void OnInitializeMelon()
        {
            _configPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "Si_UnitBalance_Config.json"
            );

            LoadConfig();
            TryApplyHarmonyPatches();
            MelonLogger.Msg($"Unit Balance v7.0.0 initialized. Config: {_configPath}");
            MelonLogger.Msg($"  Enabled: {_enabled} | Damage: {_damageMultipliers.Count} | Health: {_healthMultipliers.Count} | Cost: {_costMultipliers.Count} | BuildTime: {_buildTimeMultipliers.Count} | Range: {_rangeMultipliers.Count} | Speed: {_speedMultipliers.Count} | Reload: {_reloadTimeMultipliers.Count} | MoveSpeed: {_moveSpeedMultipliers.Count} | MinTier: {_minTierOverrides.Count} | TechTime: {_techTierTimes.Count}");
        }

        // =============================================
        // Manual Harmony patching — only when SilicaCore is available (server)
        // =============================================

        private void TryApplyHarmonyPatches()
        {
            try
            {
                Type musicHandler = null;
                Type damageManager = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (musicHandler == null) musicHandler = asm.GetType("MusicJukeboxHandler");
                    if (damageManager == null) damageManager = asm.GetType("DamageManager");
                    if (musicHandler != null && damageManager != null) break;
                }

                if (musicHandler == null || damageManager == null)
                {
                    MelonLogger.Msg("SilicaCore not available — Harmony patches skipped (server-only mod, no-op on client)");
                    return;
                }

                var harmony = HarmonyInstance;

                var gameStarted = AccessTools.Method(musicHandler, "OnGameStarted");
                if (gameStarted != null)
                    harmony.Patch(gameStarted, postfix: new HarmonyMethod(typeof(Patch_GameStarted), "Postfix"));

                var gameEnded = AccessTools.Method(musicHandler, "OnGameEnded");
                if (gameEnded != null)
                    harmony.Patch(gameEnded, postfix: new HarmonyMethod(typeof(Patch_GameEnded), "Postfix"));

                // ApplyDamage Harmony Postfix removed — damage scaling now done via ProjectileData fields
                // which avoids client-server desync (Postfix modified HP after damage was already networked)

                // Patch SendPlayerOverrides to chunk overrides into multiple packets
                // (the original packs ALL overrides into one packet, exceeding Steam's 2400-byte limit)
                Type networkLayerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    networkLayerType = asm.GetType("NetworkLayer");
                    if (networkLayerType != null) break;
                }
                if (networkLayerType != null)
                {
                    var sendPlayerOverrides = networkLayerType.GetMethod("SendPlayerOverrides",
                        BindingFlags.Public | BindingFlags.Static);
                    if (sendPlayerOverrides != null)
                    {
                        _sendPlayerOverridesMethod = sendPlayerOverrides;
                        harmony.Patch(sendPlayerOverrides,
                            prefix: new HarmonyMethod(typeof(Patch_SendPlayerOverrides), "Prefix"));
                        MelonLogger.Msg("Patched SendPlayerOverrides for chunked override sync");
                    }
                    else
                        MelonLogger.Warning("SendPlayerOverrides method not found — override sync may fail for large configs");
                }

                // Subscribe to chat events for !rebalance command
                Type gameEventsType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    gameEventsType = asm.GetType("GameEvents");
                    if (gameEventsType != null) break;
                }
                // Register !rebalance command via AdminMod API (HelperMethods.RegisterAdminCommand)
                try
                {
                    Type helperType = null;
                    Type playerType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (helperType == null)
                            helperType = asm.GetType("SilicaAdminMod.HelperMethods");
                        if (playerType == null)
                            playerType = asm.GetType("Player");
                    }
                    if (helperType != null && playerType != null)
                    {
                        // CommandCallback is delegate void(Player?, string)
                        var callbackType = helperType.GetNestedType("CommandCallback", BindingFlags.Public);
                        if (callbackType == null)
                            callbackType = helperType.Assembly.GetType("SilicaAdminMod.HelperMethods+CommandCallback");

                        if (callbackType != null)
                        {
                            // Build DynamicMethod shim: void(Player, string) -> calls our void(object, string)
                            var ourMethod = typeof(UnitBalance).GetMethod("OnRebalanceCommand", BindingFlags.NonPublic | BindingFlags.Static);
                            var dm = new System.Reflection.Emit.DynamicMethod(
                                "RebalanceShim", typeof(void),
                                new Type[] { playerType, typeof(string) },
                                typeof(UnitBalance).Module, true);
                            var il = dm.GetILGenerator();
                            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                            il.Emit(System.Reflection.Emit.OpCodes.Call, ourMethod);
                            il.Emit(System.Reflection.Emit.OpCodes.Ret);
                            var callback = dm.CreateDelegate(callbackType);

                            // Power enum: find a low-level power value (e.g., Commander or default)
                            var powerType = helperType.Assembly.GetType("SilicaAdminMod.Power");
                            object powerValue = Enum.ToObject(powerType, 0); // Power.None = any admin

                            // RegisterAdminCommand(string, CommandCallback, Power, string?)
                            var registerMethod = helperType.GetMethod("RegisterAdminCommand",
                                BindingFlags.Public | BindingFlags.Static);
                            if (registerMethod != null)
                            {
                                registerMethod.Invoke(null, new object[] { "rebalance", callback, powerValue, "Hot-reload unit balance config" });
                                MelonLogger.Msg("Registered !rebalance admin command via AdminMod");
                            }
                            else
                                MelonLogger.Warning("Could not find RegisterAdminCommand method");
                        }
                        else
                            MelonLogger.Warning("Could not find CommandCallback delegate type in AdminMod");
                    }
                    else
                        MelonLogger.Warning($"AdminMod API not found (helper={helperType != null}, player={playerType != null}). !rebalance command unavailable.");
                }
                catch (Exception chatEx)
                {
                    MelonLogger.Warning($"Failed to register !rebalance command: {chatEx.Message}");
                }

                _harmonyApplied = true;
                MelonLogger.Msg("Harmony patches applied successfully");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Harmony patching failed: {ex.Message}");
            }
        }

        // =============================================
        // Config loading
        // =============================================

        private static void LoadConfig()
        {
            if (!File.Exists(_configPath))
            {
                MelonLogger.Warning("Config not found, creating default...");
                CreateDefaultConfig();
            }

            try
            {
                string json = File.ReadAllText(_configPath);
                var config = JObject.Parse(json);

                _enabled = config["enabled"]?.Value<bool>() ?? true;
                _dumpFields = config["dump_fields"]?.Value<bool>() ?? false;
                _shrimpDisableAim = config["shrimp_disable_aim"]?.Value<bool>() ?? false;

                _damageMultipliers.Clear();
                _healthMultipliers.Clear();
                _costMultipliers.Clear();
                _buildTimeMultipliers.Clear();
                _minTierOverrides.Clear();
                _rangeMultipliers.Clear();
                _speedMultipliers.Clear();
                _reloadTimeMultipliers.Clear();
                _moveSpeedMultipliers.Clear();
                _accuracyMultipliers.Clear();
                _magazineMultipliers.Clear();
                _fireRateMultipliers.Clear();
                _buildRadiusOverrides.Clear();
                _turnRadiusMultipliers.Clear();
                _turboSpeedMultipliers.Clear();
                _targetDistanceOverrides.Clear();
                _projectileOverrides.Clear();
                _techTierTimes.Clear();

                var units = config["units"] as JObject;
                if (units != null)
                {
                    foreach (var kvp in units)
                    {
                        string unitName = kvp.Key;
                        var overrides = kvp.Value as JObject;
                        if (overrides == null) continue;

                        float damageMult = overrides["damage_mult"]?.Value<float>() ?? 1.0f;
                        float healthMult = overrides["health_mult"]?.Value<float>() ?? 1.0f;
                        float costMult = overrides["cost_mult"]?.Value<float>() ?? 1.0f;
                        float buildTimeMult = overrides["build_time_mult"]?.Value<float>() ?? 1.0f;
                        float rangeMult = overrides["range_mult"]?.Value<float>() ?? 1.0f;
                        float speedMult = overrides["proj_speed_mult"]?.Value<float>() ?? 1.0f;
                        float reloadTimeMult = overrides["reload_time_mult"]?.Value<float>() ?? 1.0f;
                        float moveSpeedMult = overrides["move_speed_mult"]?.Value<float>() ?? 1.0f;
                        float accuracyMult = overrides["accuracy_mult"]?.Value<float>() ?? 1.0f;
                        float magazineMult = overrides["magazine_mult"]?.Value<float>() ?? 1.0f;
                        float fireRateMult = overrides["fire_rate_mult"]?.Value<float>() ?? 1.0f;
                        float turnRadiusMult = overrides["turn_radius_mult"]?.Value<float>() ?? 1.0f;
                        float turboSpeedMult = overrides["turbo_speed_mult"]?.Value<float>() ?? 1.0f;
                        float targetDistance = overrides["target_distance"]?.Value<float>() ?? -1f;
                        float buildRadius = overrides["build_radius"]?.Value<float>() ?? -1f;
                        int minTier = overrides["min_tier"]?.Value<int>() ?? -1;

                        if (Math.Abs(damageMult - 1.0f) > 0.001f)
                            _damageMultipliers[unitName] = damageMult;
                        if (Math.Abs(healthMult - 1.0f) > 0.001f)
                            _healthMultipliers[unitName] = healthMult;
                        if (Math.Abs(costMult - 1.0f) > 0.001f)
                            _costMultipliers[unitName] = costMult;
                        if (Math.Abs(buildTimeMult - 1.0f) > 0.001f)
                            _buildTimeMultipliers[unitName] = buildTimeMult;
                        if (Math.Abs(rangeMult - 1.0f) > 0.001f)
                            _rangeMultipliers[unitName] = rangeMult;
                        if (Math.Abs(speedMult - 1.0f) > 0.001f)
                            _speedMultipliers[unitName] = speedMult;
                        if (Math.Abs(reloadTimeMult - 1.0f) > 0.001f)
                            _reloadTimeMultipliers[unitName] = reloadTimeMult;
                        if (Math.Abs(moveSpeedMult - 1.0f) > 0.001f)
                            _moveSpeedMultipliers[unitName] = moveSpeedMult;
                        if (Math.Abs(accuracyMult - 1.0f) > 0.001f)
                            _accuracyMultipliers[unitName] = accuracyMult;
                        if (Math.Abs(magazineMult - 1.0f) > 0.001f)
                            _magazineMultipliers[unitName] = magazineMult;
                        if (Math.Abs(fireRateMult - 1.0f) > 0.001f)
                            _fireRateMultipliers[unitName] = fireRateMult;
                        if (Math.Abs(turnRadiusMult - 1.0f) > 0.001f)
                            _turnRadiusMultipliers[unitName] = turnRadiusMult;
                        if (Math.Abs(turboSpeedMult - 1.0f) > 0.001f)
                            _turboSpeedMultipliers[unitName] = turboSpeedMult;
                        if (targetDistance >= 0)
                            _targetDistanceOverrides[unitName] = targetDistance;
                        if (buildRadius >= 0)
                            _buildRadiusOverrides[unitName] = buildRadius;
                        if (minTier >= 0)
                            _minTierOverrides[unitName] = minTier;

                        // Per-projectile absolute value overrides
                        var projectiles = overrides["projectiles"] as JObject;
                        if (projectiles != null)
                        {
                            var unitProjOverrides = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
                            foreach (var projKvp in projectiles)
                            {
                                string projName = projKvp.Key;
                                var projFields = projKvp.Value as JObject;
                                if (projFields == null) continue;
                                var fieldOverrides = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                                foreach (var fieldKvp in projFields)
                                {
                                    float? val = fieldKvp.Value?.Value<float>();
                                    if (val.HasValue)
                                        fieldOverrides[fieldKvp.Key] = val.Value;
                                }
                                if (fieldOverrides.Count > 0)
                                    unitProjOverrides[projName] = fieldOverrides;
                            }
                            if (unitProjOverrides.Count > 0)
                                _projectileOverrides[unitName] = unitProjOverrides;
                        }
                    }
                }

                // Tech tier build times (absolute seconds per tier)
                var techTime = config["tech_time"] as JObject;
                if (techTime != null)
                {
                    for (int tier = 1; tier <= 8; tier++)
                    {
                        float? time = techTime[$"tier_{tier}"]?.Value<float>();
                        if (time.HasValue)
                            _techTierTimes[tier] = time.Value;
                    }
                }

                _configLoaded = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Failed to load config: {ex.Message}");
                _enabled = false;
            }
        }

        private static void CreateDefaultConfig()
        {
            string defaultJson = @"{
    ""enabled"": true,
    ""dump_fields"": false,
    ""description"": ""Unit balance mod. damage/health_mult = combat scaling, cost/build_time_mult = economy scaling, min_tier = tech requirement override. tech_time = per-tier research time in seconds (default 30s all tiers). Omit or -1 for min_tier = no change."",
    ""tech_time"": {
        ""_note"": ""Build time in seconds for each tech tier research (all factions). Default is 30s for all tiers."",
        ""tier_1"": 30,
        ""tier_2"": 30,
        ""tier_3"": 30,
        ""tier_4"": 30,
        ""tier_5"": 30,
        ""tier_6"": 30,
        ""tier_7"": 30,
        ""tier_8"": 30
    },
    ""units"": {
        ""Gunship"":      { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 5.24 - dominant air unit"" },
        ""Siege Tank"":   { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 4.98 - very strong heavy vehicle"" },
        ""Crimson Tank"": { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 3.69"" },
        ""Railgun Tank"": { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 3.68"" },
        ""Goliath"":      { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 2.70"" },
        ""Firebug"":      { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 3.59"" },
        ""Scorpion"":     { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 3.45"" },
        ""Colossus"":     { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 3.62"" },
        ""Behemoth"":     { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 2.10"" },
        ""Hover Tank"":   { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 2.50"" },

        ""Horned Crab"":  { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.13 - worst combat unit"" },
        ""Great Worm"":   { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.19"" },
        ""Flak Car"":     { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.47 - fails at anti-air"" },
        ""Crab"":         { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.56"" },
        ""Squid"":        { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.45"" },

        ""Rifleman"":     { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.18"" },
        ""Trooper"":      { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.18"" },
        ""Scout"":        { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.20"" },
        ""Militia"":      { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.21"" },
        ""Marksman"":     { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""_note"": ""K/D 0.67"" },
        ""Commando"":     { ""damage_mult"": 1.00, ""health_mult"": 1.00, ""cost_mult"": 1.00, ""build_time_mult"": 1.00, ""move_speed_mult"": 1.00, ""_note"": ""K/D 1.20"" }
    }
}";
            try
            {
                File.WriteAllText(_configPath, defaultJson);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Failed to create default config: {ex.Message}");
            }
        }

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
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _overrideManagerType = asm.GetType("OverrideManager");
                    if (_overrideManagerType != null) break;
                }
                if (_overrideManagerType == null)
                {
                    MelonLogger.Warning("[OverrideManager] Type not found in loaded assemblies");
                    return false;
                }

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
                Type asrType = _overrideManagerType?.GetNestedType("AssetSourceRegistry",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (asrType == null)
                {
                    // Fallback: search all assemblies for top-level type
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        asrType = asm.GetType("AssetSourceRegistry");
                        if (asrType != null) break;
                    }
                }
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
                    ApplyMoveSpeedOverrides(omReady);
                    ApplyTurnRadiusOverrides(omReady);

                    if (_shrimpDisableAim)
                        ApplyShrimpAimDisable();

                    MelonLogger.Msg($"Unit Balance active (OverrideManager={omReady}): " +
                        $"{_damageMultipliers.Count} damage, {_healthMultipliers.Count} health, " +
                        $"{_costMultipliers.Count} cost, {_buildTimeMultipliers.Count} buildTime, " +
                        $"{_rangeMultipliers.Count} range, {_moveSpeedMultipliers.Count} moveSpeed, " +
                        $"{_projectileOverrides.Count} projOverrides, " +
                        $"{_minTierOverrides.Count} minTier, {_techTierTimes.Count} techTime");

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
                Type playerType = null, ngsType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (playerType == null) playerType = asm.GetType("Player");
                    if (ngsType == null) ngsType = asm.GetType("NetworkGameServer");
                    if (playerType != null && ngsType != null) break;
                }

                if (playerType == null || _sendPlayerOverridesMethod == null)
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

        private static class Patch_GameEnded
        {
            public static void Postfix()
            {
                MelonLogger.Msg("[UnitBalance] Game ended");

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
        // Chat command: !rebalance — hot-reload config during a running game
        // =============================================

        // Called by AdminMod when an admin types !rebalance
        private static void OnRebalanceCommand(object player, string args)
        {
            MelonLogger.Msg("[Rebalance] Hot-reload triggered by admin chat command");

            try
            {
                // 1. Revert all current OM overrides (restores originals, notifies clients)
                if (_omInitialized && _overrideManagerType != null)
                {
                    OMRevertAll();
                    MelonLogger.Msg("[Rebalance] Reverted all overrides");
                }

                // 2. Clear direct-mutation caches
                _originalCreatureAttackAimDist.Clear();
                _originalSpread.Clear();
                _originalProjectileLifetimes.Clear();
                _originalProjectileSpeeds.Clear();
                _originalMoveSpeeds.Clear();
                _nameCache.Clear();

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

                    if (omReady && _healthMultipliers.Count > 0)
                        RegisterDamageManagerDataInOM();

                    ApplyConstructionDataOverrides(omReady);
                    ApplyHealthOverrides(omReady);
                    ApplyProjectileDamageOverrides(omReady);
                    ApplyRangeOverrides(omReady);
                    ApplyTargetDistanceOverrides(omReady);
                    ApplyMoveSpeedOverrides(omReady);
                    ApplyTurnRadiusOverrides(omReady);

                    if (_shrimpDisableAim)
                        ApplyShrimpAimDisable();

                    MelonLogger.Msg("[Rebalance] All overrides re-applied");

                    // 5. Sync to all connected players
                    if (omReady)
                        MelonCoroutines.Start(SyncOverridesToAllPlayers(0.5f));
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

        // =============================================
        // Cost & build time: modify ConstructionData via OverrideManager
        // =============================================

        private static void ApplyConstructionDataOverrides(bool useOM)
        {
            bool hasUnitOverrides = _costMultipliers.Count > 0 || _buildTimeMultipliers.Count > 0
                                 || _minTierOverrides.Count > 0 || _buildRadiusOverrides.Count > 0;
            bool hasTechOverrides = _techTierTimes.Count > 0;

            if (!hasUnitOverrides && !hasTechOverrides)
                return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            int techApplied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.ConstructionData == null) continue;

                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                var cd = info.ConstructionData;
                string cdTarget = useOM ? $"A:{cd.name}.asset" : null;

                // Tech tier build time overrides (match by TechnologyTier field)
                if (hasTechOverrides && cd.TechnologyTier > 0)
                {
                    if (_techTierTimes.TryGetValue(cd.TechnologyTier, out float techTime))
                    {
                        float origTime = cd.BuildUpTime;
                        float newTime = Math.Max(1f, techTime);
                        if (useOM)
                            OMSetFloat(cdTarget, "BuildUpTime", newTime);
                        else
                            cd.BuildUpTime = newTime;
                        MelonLogger.Msg($"[TECH] Tier {cd.TechnologyTier} '{name}': {origTime:F0}s -> {newTime:F0}s{(useOM ? " (OM)" : "")}");
                        techApplied++;
                    }
                    continue;
                }

                // Unit/structure overrides
                bool hasCost = _costMultipliers.TryGetValue(name, out float costMult);
                bool hasBuildTime = _buildTimeMultipliers.TryGetValue(name, out float btMult);
                bool hasMinTier = _minTierOverrides.TryGetValue(name, out int minTier);
                bool hasBuildRadius = _buildRadiusOverrides.TryGetValue(name, out float buildRadius);
                if (!hasCost && !hasBuildTime && !hasMinTier && !hasBuildRadius) continue;

                if (hasCost)
                {
                    int origCost = cd.ResourceCost;
                    int newCost = Math.Max(1, (int)Math.Round(origCost * costMult));
                    if (useOM)
                        OMSetInt(cdTarget, "ResourceCost", newCost);
                    else
                        cd.ResourceCost = newCost;
                    MelonLogger.Msg($"[COST] {name}: {origCost} -> {newCost} (x{costMult:F2}){(useOM ? " (OM)" : "")}");
                }

                if (hasBuildTime)
                {
                    float origTime = cd.BuildUpTime;
                    float newTime = Math.Max(0.5f, origTime * btMult);
                    if (useOM)
                        OMSetFloat(cdTarget, "BuildUpTime", newTime);
                    else
                        cd.BuildUpTime = newTime;
                    MelonLogger.Msg($"[BUILD] {name}: {origTime:F1}s -> {newTime:F1}s (x{btMult:F2}){(useOM ? " (OM)" : "")}");
                }

                if (hasMinTier)
                {
                    int origTier = cd.MinimumTeamTier;
                    if (useOM)
                        OMSetInt(cdTarget, "MinimumTeamTier", minTier);
                    else
                        cd.MinimumTeamTier = minTier;
                    MelonLogger.Msg($"[TIER] {name}: min tier {origTier} -> {minTier}{(useOM ? " (OM)" : "")}");
                }

                if (hasBuildRadius)
                {
                    float origDist = cd.MaximumBaseStructureDistance;
                    if (useOM)
                        OMSetFloat(cdTarget, "MaximumBaseStructureDistance", buildRadius);
                    else
                        cd.MaximumBaseStructureDistance = buildRadius;
                    MelonLogger.Msg($"[BUILD_RADIUS] {name}: {origDist:F0} -> {buildRadius:F0}{(useOM ? " (OM)" : "")}");
                }

                applied++;
            }

            if (applied > 0 || techApplied > 0)
                MelonLogger.Msg($"Applied construction overrides: {applied} units/structures, {techApplied} tech tiers");
        }

        // =============================================
        // Health: scale DamageManagerData.Health via OverrideManager
        // DamageManagerData is a ScriptableObject asset (e.g. "Soldier_Juggernaut.asset")
        // with a "Health" field. OverrideManager can target it via "A:{name}.asset".
        // This syncs to clients and avoids necromancy desync.
        // =============================================

        private static void ApplyHealthOverrides(bool useOM)
        {
            if (_healthMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_healthMultipliers.TryGetValue(name, out float hpMult)) continue;

                var dm = info.Prefab.GetComponent<DamageManager>();
                if (dm == null) continue;

                bool set = false;
                try
                {
                    var dataField = dm.GetType().GetField("Data", BindingFlags.Public | BindingFlags.Instance);
                    if (dataField != null)
                    {
                        object dataObj = dataField.GetValue(dm);
                        if (dataObj != null)
                        {
                            float origHealth = GetFloatMember(dataObj, "Health");
                            if (origHealth <= 0) origHealth = GetFloatMember(dataObj, "MaxHealth");
                            if (origHealth <= 0) origHealth = GetFloatMember(dataObj, "m_MaxHealth");
                            if (origHealth > 0)
                            {
                                float newHealth = origHealth * hpMult;
                                string dataName = ((UnityEngine.Object)dataObj).name;

                                if (useOM && !string.IsNullOrEmpty(dataName))
                                {
                                    // Use OverrideManager to set Health on the DamageManagerData asset
                                    // Target uses "DamageManagerData_" prefix to match our registered synthetic paths
                                    string dmdTarget = $"A:DamageManagerData_{dataName}.asset";
                                    set = OMSetFloat(dmdTarget, "Health", newHealth);
                                    if (set)
                                        MelonLogger.Msg($"[HEALTH] {name}: Health {origHealth:F0} -> {newHealth:F0} (x{hpMult:F2}) (OM: {dataName})");
                                    else
                                        MelonLogger.Warning($"[HEALTH] {name}: OM failed for {dmdTarget}, falling back to direct mutation");
                                }

                                if (!set)
                                {
                                    // Fallback: direct mutation (no client sync — may cause desync)
                                    set = SetFloatMember(dataObj, "Health", newHealth);
                                    if (!set) set = SetFloatMember(dataObj, "MaxHealth", newHealth);
                                    if (!set) set = SetFloatMember(dataObj, "m_MaxHealth", newHealth);
                                    if (set)
                                        MelonLogger.Msg($"[HEALTH] {name}: Health {origHealth:F0} -> {newHealth:F0} (x{hpMult:F2}) (direct mutation — no client sync!)");
                                }
                            }
                        }
                    }
                }
                catch { }
                if (set) applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[HEALTH] Applied MaxHealth overrides to {applied} units/structures");
        }

        // =============================================
        // Damage: scale ProjectileData damage fields (replaces broken Harmony ApplyDamage Postfix)
        // Modifying damage at the ProjectileData level ensures server & client see the same values.
        // =============================================

        private static readonly string[] _damageFields = {
            "m_fImpactDamage", "m_fRicochetDamage", "m_fSplashDamageMax", "m_fPenetratingDamage"
        };

        private static void ApplyProjectileDamageOverrides(bool useOM)
        {
            if (_damageMultipliers.Count == 0 && _projectileOverrides.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            var modifiedPD = new HashSet<string>(); // track already-modified ProjectileData assets

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                bool hasDamageMult = _damageMultipliers.TryGetValue(name, out float dmgMult);
                bool hasProjOverrides = _projectileOverrides.TryGetValue(name, out var unitProjOverrides);
                if (!hasDamageMult && !hasProjOverrides) continue;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                bool anyApplied = false;

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    // --- VehicleTurret: find PrimaryProjectileData / SecondaryProjectileData ---
                    if (typeName == "VehicleTurret")
                    {
                        var vtType = comp.GetType();
                        foreach (string projFieldName in new[] { "PrimaryProjectileData", "SecondaryProjectileData" })
                        {
                            var projField = vtType.GetField(projFieldName,
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (projField == null) continue;
                            object pdObj;
                            try { pdObj = projField.GetValue(comp); } catch { continue; }
                            if (pdObj == null) continue;

                            string pdName = "";
                            try { pdName = ((UnityEngine.Object)pdObj).name; } catch { continue; }
                            if (string.IsNullOrEmpty(pdName) || modifiedPD.Contains(pdName)) continue;

                            // Check for per-projectile absolute overrides first
                            if (hasProjOverrides && unitProjOverrides.TryGetValue(pdName, out var fieldOverrides))
                            {
                                string pdTarget = useOM ? $"A:{pdName}.asset" : null;
                                foreach (var kvp in fieldOverrides)
                                {
                                    float origVal = GetFloatMember(pdObj, kvp.Key);
                                    if (useOM)
                                        OMSetFloat(pdTarget, kvp.Key, kvp.Value);
                                    else
                                        SetFloatMember(pdObj, kvp.Key, kvp.Value);
                                    MelonLogger.Msg($"[DMG] {name} -> {pdName}.{kvp.Key}: {origVal:F1} -> {kvp.Value:F1}{(useOM ? " (OM)" : "")}");
                                }
                                modifiedPD.Add(pdName);
                                anyApplied = true;
                            }
                            else if (hasDamageMult)
                            {
                                // Proportional scaling via damage_mult
                                ScaleProjectileDamage(pdObj, pdName, dmgMult, modifiedPD, useOM, name);
                                anyApplied = true;
                            }
                        }
                    }

                    // --- CreatureDecapod: AttackPrimary/Secondary.AttackProjectileData + melee Damage ---
                    if (typeName == "CreatureDecapod")
                    {
                        var compType = comp.GetType();
                        foreach (string attackFieldName in new[] { "AttackPrimary", "AttackSecondary" })
                        {
                            var attackField = compType.GetField(attackFieldName,
                                BindingFlags.Public | BindingFlags.Instance);
                            if (attackField == null) continue;
                            object attackObj;
                            try { attackObj = attackField.GetValue(comp); } catch { continue; }
                            if (attackObj == null) continue;

                            var attackType = attackObj.GetType();

                            // Check for ranged attack (has ProjectileData)
                            var pdField = attackType.GetField("AttackProjectileData",
                                BindingFlags.Public | BindingFlags.Instance);
                            object pdObj = null;
                            if (pdField != null)
                            {
                                try { pdObj = pdField.GetValue(attackObj); } catch { }
                            }

                            if (pdObj != null)
                            {
                                string pdName = "";
                                try { pdName = ((UnityEngine.Object)pdObj).name; } catch { }
                                if (!string.IsNullOrEmpty(pdName) && !modifiedPD.Contains(pdName))
                                {
                                    if (hasProjOverrides && unitProjOverrides.TryGetValue(pdName, out var fieldOverrides))
                                    {
                                        string pdTarget = useOM ? $"A:{pdName}.asset" : null;
                                        foreach (var kvp in fieldOverrides)
                                        {
                                            float origVal = GetFloatMember(pdObj, kvp.Key);
                                            if (useOM)
                                                OMSetFloat(pdTarget, kvp.Key, kvp.Value);
                                            else
                                                SetFloatMember(pdObj, kvp.Key, kvp.Value);
                                            MelonLogger.Msg($"[DMG] {name} -> {pdName}.{kvp.Key}: {origVal:F1} -> {kvp.Value:F1}{(useOM ? " (OM)" : "")}");
                                        }
                                        modifiedPD.Add(pdName);
                                        anyApplied = true;
                                    }
                                    else if (hasDamageMult)
                                    {
                                        ScaleProjectileDamage(pdObj, pdName, dmgMult, modifiedPD, useOM, name);
                                        anyApplied = true;
                                    }
                                }
                            }
                            else if (hasDamageMult)
                            {
                                // Melee attack (no projectile) — scale CreatureAttack.Damage directly
                                float origDmg = GetFloatMember(attackObj, "Damage");
                                if (origDmg > 0)
                                {
                                    float newDmg = origDmg * dmgMult;
                                    SetFloatMember(attackObj, "Damage", newDmg);
                                    MelonLogger.Msg($"[DMG] {name} -> {attackFieldName}.Damage: {origDmg:F0} -> {newDmg:F0} (melee, direct)");
                                    anyApplied = true;
                                }
                            }
                        }
                    }
                }

                if (anyApplied) applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[DMG] Applied projectile/melee damage overrides to {applied} units");
        }

        private static void ScaleProjectileDamage(object pdObj, string pdName, float dmgMult, HashSet<string> modifiedPD, bool useOM, string unitName)
        {
            string pdTarget = useOM ? $"A:{pdName}.asset" : null;
            int scaled = 0;

            foreach (string fieldName in _damageFields)
            {
                float orig = GetFloatMember(pdObj, fieldName);
                if (orig <= 0) continue;
                float newVal = orig * dmgMult;
                if (useOM)
                    OMSetFloat(pdTarget, fieldName, newVal);
                else
                    SetFloatMember(pdObj, fieldName, newVal);
                MelonLogger.Msg($"[DMG] {unitName} -> {pdName}.{fieldName}: {orig:F1} -> {newVal:F1} (x{dmgMult:F2}){(useOM ? " (OM)" : "")}");
                scaled++;
            }

            if (scaled == 0)
                MelonLogger.Warning($"[DMG] {unitName} -> {pdName}: no damage fields found to scale");

            modifiedPD.Add(pdName);
        }

        // =============================================
        // Range/Speed: scale projectiles, sensor, turret, creature attacks
        // =============================================

        // Original-value caches for direct mutation path (CreatureAttack fields)
        private static readonly Dictionary<string, float> _originalCreatureAttackAimDist =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalSpread =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalProjectileLifetimes =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalProjectileSpeeds =
            new Dictionary<string, float>();

        private static void ApplyRangeOverrides(bool useOM)
        {
            if (_rangeMultipliers.Count == 0 && _speedMultipliers.Count == 0 && _reloadTimeMultipliers.Count == 0
                && _accuracyMultipliers.Count == 0 && _magazineMultipliers.Count == 0 && _fireRateMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            var allProjectiles = Resources.FindObjectsOfTypeAll<ProjectileData>();
            int applied = 0;
            var modifiedPD = new HashSet<string>();

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                bool hasRange = _rangeMultipliers.TryGetValue(name, out float rangeMult);
                bool hasSpeed = _speedMultipliers.TryGetValue(name, out float speedMult);
                bool hasReload = _reloadTimeMultipliers.TryGetValue(name, out float reloadMult);
                bool hasAccuracy = _accuracyMultipliers.TryGetValue(name, out float accMult);
                bool hasMagazine = _magazineMultipliers.TryGetValue(name, out float magMult);
                bool hasFireRate = _fireRateMultipliers.TryGetValue(name, out float frMult);
                if (!hasRange && !hasSpeed && !hasReload && !hasAccuracy && !hasMagazine && !hasFireRate) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                string desc = "";
                if (hasRange) desc += $"range x{rangeMult:F4}";
                if (hasSpeed) desc += $"{(desc.Length > 0 ? ", " : "")}speed x{speedMult:F4}";
                if (hasReload) desc += $"{(desc.Length > 0 ? ", " : "")}reload x{reloadMult:F4}";
                if (hasAccuracy) desc += $"{(desc.Length > 0 ? ", " : "")}accuracy x{accMult:F4}";
                if (hasMagazine) desc += $"{(desc.Length > 0 ? ", " : "")}magazine x{magMult:F4}";
                if (hasFireRate) desc += $"{(desc.Length > 0 ? ", " : "")}fire_rate x{frMult:F4}";
                MelonLogger.Msg($"[RANGE] Applying {desc} to '{name}' (internal: {info.name}){(useOM ? " (OM)" : "")}");

                bool foundProjectileOnComponent = false;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    if (hasRange)
                    {
                        // Sensor.TargetingDistance via OverrideManager (prefab component fallback)
                        if (typeName == "Sensor")
                        {
                            var tdField = comp.GetType().GetField("TargetingDistance",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (tdField != null)
                            {
                                float orig = (float)tdField.GetValue(comp);
                                float newVal = orig * rangeMult;
                                if (useOM)
                                    OMSetFloat(oiTarget, "TargetingDistance", newVal);
                                else
                                    tdField.SetValue(comp, newVal);
                                MelonLogger.Msg($"  Sensor.TargetingDistance: {orig} -> {newVal}");
                            }
                        }

                        // VehicleTurret.AimDistance
                        if (typeName == "VehicleTurret")
                        {
                            var adField = comp.GetType().GetField("AimDistance",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (adField != null)
                            {
                                float orig = (float)adField.GetValue(comp);
                                float newVal = orig * rangeMult;
                                if (useOM)
                                    OMSetFloat(oiTarget, "AimDistance", newVal);
                                else
                                    adField.SetValue(comp, newVal);
                                MelonLogger.Msg($"  VehicleTurret.AimDistance: {orig} -> {newVal}");
                            }
                        }

                        // UnitAimAt.AimDistanceMax
                        if (typeName == "UnitAimAt")
                        {
                            var admField = comp.GetType().GetField("AimDistanceMax",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (admField != null)
                            {
                                float orig = (float)admField.GetValue(comp);
                                float newVal = orig * rangeMult;
                                if (useOM)
                                    OMSetFloat(oiTarget, "AimDistanceMax", newVal);
                                else
                                    admField.SetValue(comp, newVal);
                                MelonLogger.Msg($"  UnitAimAt.AimDistanceMax: {orig} -> {newVal}");
                            }
                        }

                        // CreatureDecapod: CreatureAttack fields (ALWAYS direct mutation — not MonoBehaviour)
                        if (typeName == "CreatureDecapod")
                        {
                            var compType = comp.GetType();
                            foreach (string attackFieldName in new[] { "AttackPrimary", "AttackSecondary" })
                            {
                                var attackField = compType.GetField(attackFieldName,
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (attackField == null) continue;
                                object attackObj;
                                try { attackObj = attackField.GetValue(comp); } catch { continue; }
                                if (attackObj == null) continue;

                                var attackType = attackObj.GetType();

                                // Scale AttackProjectileAimDistMax (server-authoritative AI field)
                                var aimDistField = attackType.GetField("AttackProjectileAimDistMax",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (aimDistField != null)
                                {
                                    string key = $"{name}_ca_{attackFieldName}";
                                    float orig;
                                    if (_originalCreatureAttackAimDist.ContainsKey(key))
                                        orig = _originalCreatureAttackAimDist[key];
                                    else
                                    {
                                        orig = (float)aimDistField.GetValue(attackObj);
                                        _originalCreatureAttackAimDist[key] = orig;
                                    }
                                    float newVal = orig * rangeMult;
                                    aimDistField.SetValue(attackObj, newVal);
                                    MelonLogger.Msg($"  CreatureAttack.{attackFieldName}.AimDistMax: {orig} -> {newVal} (direct)");
                                }

                                // Scale AttackProjectileSpread (server-authoritative AI field)
                                if (hasAccuracy)
                                {
                                    var spreadField = attackType.GetField("AttackProjectileSpread",
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (spreadField != null)
                                    {
                                        string key = $"{name}_spread_{attackFieldName}";
                                        float orig;
                                        if (_originalSpread.ContainsKey(key))
                                            orig = _originalSpread[key];
                                        else
                                        {
                                            orig = (float)spreadField.GetValue(attackObj);
                                            _originalSpread[key] = orig;
                                        }
                                        float newVal = orig * accMult;
                                        spreadField.SetValue(attackObj, newVal);
                                        MelonLogger.Msg($"  CreatureAttack.{attackFieldName}.Spread: {orig} -> {newVal} (direct)");
                                    }
                                }

                                // Find ProjectileData referenced inside CreatureAttack
                                var pdField = attackType.GetField("AttackProjectileData",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (pdField != null)
                                {
                                    object pdObj;
                                    try { pdObj = pdField.GetValue(attackObj); } catch { pdObj = null; }
                                    if (pdObj != null)
                                    {
                                        string pdName = "";
                                        try { pdName = ((UnityEngine.Object)pdObj).name; } catch { }
                                        if (!string.IsNullOrEmpty(pdName) && !modifiedPD.Contains(pdName))
                                        {
                                            ScaleProjectileData(pdObj, pdName,
                                                hasRange ? rangeMult : 1f, hasSpeed ? speedMult : 1f, modifiedPD, useOM);
                                            foundProjectileOnComponent = true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // VehicleTurret weapon params via OverrideManager (prefab component fallback)
                    if (typeName == "VehicleTurret")
                    {
                        var vtType = comp.GetType();

                        if (hasReload)
                        {
                            foreach (string prefix in new[] { "Primary", "Secondary" })
                            {
                                var rtField = vtType.GetField($"{prefix}ReloadTime",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (rtField == null) continue;
                                float orig = (float)rtField.GetValue(comp);
                                float newVal = Math.Max(0.1f, orig * reloadMult);
                                if (useOM)
                                    OMSetFloat(oiTarget, $"{prefix}ReloadTime", newVal);
                                else
                                    rtField.SetValue(comp, newVal);
                                MelonLogger.Msg($"  VehicleTurret.{prefix}ReloadTime: {orig:F2} -> {newVal:F2}");
                            }
                        }

                        if (hasFireRate)
                        {
                            foreach (string prefix in new[] { "Primary", "Secondary" })
                            {
                                var fiField = vtType.GetField($"{prefix}FireInterval",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (fiField == null || fiField.FieldType != typeof(float)) continue;
                                float orig = (float)fiField.GetValue(comp);
                                float newVal = Math.Max(0.01f, orig / frMult);
                                if (useOM)
                                    OMSetFloat(oiTarget, $"{prefix}FireInterval", newVal);
                                else
                                    fiField.SetValue(comp, newVal);
                                MelonLogger.Msg($"  VehicleTurret.{prefix}FireInterval: {orig:F4} -> {newVal:F4}");
                            }
                        }

                        if (hasMagazine)
                        {
                            foreach (string prefix in new[] { "Primary", "Secondary" })
                            {
                                var msField = vtType.GetField($"{prefix}MagazineSize",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (msField == null || msField.FieldType != typeof(int)) continue;
                                int orig = (int)msField.GetValue(comp);
                                int newVal = Math.Max(1, (int)Math.Round(orig * magMult));
                                if (useOM)
                                    OMSetInt(oiTarget, $"{prefix}MagazineSize", newVal);
                                else
                                    msField.SetValue(comp, newVal);
                                MelonLogger.Msg($"  VehicleTurret.{prefix}MagazineSize: {orig} -> {newVal}");
                            }
                        }

                        if (hasAccuracy)
                        {
                            foreach (string prefix in new[] { "Primary", "Secondary" })
                            {
                                var spField = vtType.GetField($"{prefix}MuzzleSpread",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (spField == null || spField.FieldType != typeof(float)) continue;
                                float orig = (float)spField.GetValue(comp);
                                float newVal = orig * accMult;
                                if (useOM)
                                    OMSetFloat(oiTarget, $"{prefix}MuzzleSpread", newVal);
                                else
                                    spField.SetValue(comp, newVal);
                                MelonLogger.Msg($"  VehicleTurret.{prefix}MuzzleSpread: {orig:F4} -> {newVal:F4}");
                            }
                        }
                    }

                    // Find ProjectileData references on components
                    if (hasRange || hasSpeed)
                    {
                        var fields = comp.GetType().GetFields(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        foreach (var field in fields)
                        {
                            if (field.FieldType.Name != "ProjectileData") continue;
                            object pdObj;
                            try { pdObj = field.GetValue(comp); } catch { continue; }
                            if (pdObj == null) continue;

                            string pdName = "";
                            try { pdName = ((UnityEngine.Object)pdObj).name; } catch { continue; }
                            if (string.IsNullOrEmpty(pdName) || modifiedPD.Contains(pdName)) continue;

                            ScaleProjectileData(pdObj, pdName, hasRange ? rangeMult : 1f, hasSpeed ? speedMult : 1f, modifiedPD, useOM);
                            foundProjectileOnComponent = true;
                        }
                    }
                }

                // Fallback: search all ProjectileData assets by name pattern
                if (!foundProjectileOnComponent && (hasRange || hasSpeed))
                {
                    MelonLogger.Msg($"  No ProjectileData found on components, searching by name...");
                    int fallbackCount = 0;
                    foreach (var pd in allProjectiles)
                    {
                        if (pd == null || modifiedPD.Contains(pd.name)) continue;
                        if (pd.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        ScaleProjectileData(pd, pd.name, hasRange ? rangeMult : 1f, hasSpeed ? speedMult : 1f, modifiedPD, useOM);
                        fallbackCount++;
                    }
                    if (fallbackCount == 0)
                        MelonLogger.Warning($"  No ProjectileData asset found matching '{name}'");
                }

                applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[RANGE] Applied range overrides to {applied} units");
        }

        // =============================================
        // Move Speed: scale unit movement speed via OverrideManager
        // =============================================

        // Known speed fields per component type (all DeclaredOnly, reachable via OM prefab fallback)
        private static readonly string[] _speedFieldNames = {
            "MoveSpeed",       // CreatureDecapod, VehicleHovered, VehicleWheeled
            "FlyMoveSpeed",    // CreatureDecapod
            "WalkSpeed",       // Soldier (human infantry)
            "RunSpeed",        // Soldier (human infantry)
            "SprintSpeed",     // Soldier (human infantry)
            "ForwardSpeed",    // VehicleAir (Dropship, Shuttle, Gunship)
            "StrafeSpeed",     // VehicleAir
            "TurboSpeed",      // VehicleAir
        };

        // =============================================
        // Target distance: absolute override for Sensor.TargetingDistance (applied after range_mult)
        // =============================================

        private static void ApplyTargetDistanceOverrides(bool useOM)
        {
            if (_targetDistanceOverrides.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_targetDistanceOverrides.TryGetValue(name, out float targetDist)) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null || comp.GetType().Name != "Sensor") continue;

                    var tdField = comp.GetType().GetField("TargetingDistance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (tdField == null || tdField.FieldType != typeof(float)) continue;

                    float orig = (float)tdField.GetValue(comp);
                    if (useOM && OMSetFloat(oiTarget, "TargetingDistance", targetDist))
                    {
                        MelonLogger.Msg($"[TARGETDIST] {name}: Sensor.TargetingDistance {orig} -> {targetDist} (OM)");
                        applied++;
                    }
                    else if (!useOM)
                    {
                        tdField.SetValue(comp, targetDist);
                        MelonLogger.Msg($"[TARGETDIST] {name}: Sensor.TargetingDistance {orig} -> {targetDist} (direct)");
                        applied++;
                    }
                    break;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[TARGETDIST] Applied target distance overrides to {applied} units");
        }

        private static void ApplyMoveSpeedOverrides(bool useOM)
        {
            if (_moveSpeedMultipliers.Count == 0 && _turboSpeedMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                bool hasMoveSpeed = _moveSpeedMultipliers.TryGetValue(name, out float mult);
                bool hasTurboSpeed = _turboSpeedMultipliers.TryGetValue(name, out float turboMult);
                if (!hasMoveSpeed && !hasTurboSpeed) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                if (hasMoveSpeed)
                    MelonLogger.Msg($"[MOVESPEED] Applying move_speed_mult x{mult:F2} to '{name}'{(useOM ? " (OM)" : "")}");
                if (hasTurboSpeed)
                    MelonLogger.Msg($"[MOVESPEED] Applying turbo_speed_mult x{turboMult:F2} to '{name}'{(useOM ? " (OM)" : "")}");

                if (useOM)
                {
                    // Search prefab components for known speed fields and override via OM
                    var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                    int fieldsSet = 0;
                    foreach (var comp in childComps)
                    {
                        if (comp == null) continue;
                        var compType = comp.GetType();
                        foreach (string fieldName in _speedFieldNames)
                        {
                            // TurboSpeed uses turbo_speed_mult; all others use move_speed_mult
                            float fieldMult;
                            if (fieldName == "TurboSpeed")
                            {
                                if (hasTurboSpeed) fieldMult = turboMult;
                                else if (hasMoveSpeed) fieldMult = mult;
                                else continue;
                            }
                            else
                            {
                                if (!hasMoveSpeed) continue;
                                fieldMult = mult;
                            }

                            var field = compType.GetField(fieldName,
                                BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                            if (field == null || field.FieldType != typeof(float)) continue;
                            float orig = (float)field.GetValue(comp);
                            if (orig <= 0) continue;
                            float newVal = orig * fieldMult;
                            if (OMSetFloat(oiTarget, fieldName, newVal))
                            {
                                MelonLogger.Msg($"  {compType.Name}.{fieldName}: {orig} -> {newVal} (OM)");
                                fieldsSet++;
                            }
                        }
                        if (fieldsSet > 0) break; // found the right component
                    }
                    if (fieldsSet > 0)
                        applied++;
                    else
                    {
                        // Fallback: direct mutation via ModifyPrefabSpeeds
                        if (hasMoveSpeed)
                        {
                            int modCount = ModifyPrefabSpeeds(info.Prefab, name, mult);
                            if (modCount > 0) applied++;
                        }
                    }
                }
                else
                {
                    if (hasMoveSpeed)
                    {
                        int modCount = ModifyPrefabSpeeds(info.Prefab, name, mult);
                        if (modCount > 0) applied++;
                    }
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[MOVESPEED] Applied move speed overrides to {applied} units");
        }

        private static void ApplyTurnRadiusOverrides(bool useOM)
        {
            if (_turnRadiusMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_turnRadiusMultipliers.TryGetValue(name, out float mult)) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    if (comp.GetType().Name != "VehicleWheeled") continue;

                    var field = comp.GetType().GetField("TurningCircleRadius",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (field == null || field.FieldType != typeof(float)) continue;

                    float orig = (float)field.GetValue(comp);
                    if (orig <= 0) continue;
                    float newVal = orig * mult;

                    if (useOM && OMSetFloat(oiTarget, "TurningCircleRadius", newVal))
                    {
                        MelonLogger.Msg($"[TURNRADIUS] {name}: TurningCircleRadius {orig:F1} -> {newVal:F1} (OM)");
                        applied++;
                    }
                    else
                    {
                        field.SetValue(comp, newVal);
                        MelonLogger.Msg($"[TURNRADIUS] {name}: TurningCircleRadius {orig:F1} -> {newVal:F1} (direct)");
                        applied++;
                    }
                    break;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[TURNRADIUS] Applied turn radius overrides to {applied} units");
        }

        // Shared helper: scan all components on a prefab for speed-related float fields/properties
        private static int ModifyPrefabSpeeds(GameObject prefab, string unitName, float mult)
        {
            int totalModified = 0;
            var components = prefab.GetComponentsInChildren<Component>(true);

            foreach (var comp in components)
            {
                if (comp == null) continue;
                var compType = comp.GetType();
                string typeName = compType.Name;

                // Skip rendering/physics/UI components — only look at gameplay components
                string lower = typeName.ToLower();
                if (lower == "transform" || lower.Contains("mesh") || lower.Contains("renderer") ||
                    lower.Contains("animator") || lower.Contains("lodgroup") || lower.Contains("particle") ||
                    lower.Contains("collider") || lower.Contains("light") || lower.Contains("audio") ||
                    lower.Contains("canvas") || lower.Contains("cloth") || lower.Contains("rigidbody"))
                    continue;

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // Search fields for speed-related floats
                foreach (var field in compType.GetFields(flags))
                {
                    if (field.FieldType != typeof(float)) continue;
                    string fname = field.Name.ToLower();
                    if (!fname.Contains("speed")) continue;
                    // Exclude non-movement speeds
                    if (fname.Contains("turn") || fname.Contains("rotation") || fname.Contains("projectile") ||
                        fname.Contains("anim") || fname.Contains("reload") || fname.Contains("fire") ||
                        fname.Contains("attack") || fname.Contains("cool") ||
                        fname.Contains("shutter") || fname.Contains("landing")) continue;

                    try
                    {
                        float orig = (float)field.GetValue(comp);
                        if (Math.Abs(orig) < 0.001f) continue;

                        string key = $"{unitName}_{comp.GetInstanceID()}_{field.Name}";
                        if (!_originalMoveSpeeds.ContainsKey(key))
                            _originalMoveSpeeds[key] = orig;
                        else
                            orig = _originalMoveSpeeds[key];

                        float newVal = orig * mult;
                        field.SetValue(comp, newVal);
                        MelonLogger.Msg($"  [{typeName}] field {field.Name}: {orig} -> {newVal}");
                        totalModified++;
                    }
                    catch { }
                }

                // Search properties for speed-related writable floats
                foreach (var prop in compType.GetProperties(flags))
                {
                    if (prop.PropertyType != typeof(float) || !prop.CanRead || !prop.CanWrite) continue;
                    string pname = prop.Name.ToLower();
                    if (!pname.Contains("speed")) continue;
                    if (pname.Contains("turn") || pname.Contains("rotation") || pname.Contains("projectile") ||
                        pname.Contains("anim") || pname.Contains("reload") || pname.Contains("fire") ||
                        pname.Contains("attack") || pname.Contains("cool") ||
                        pname.Contains("shutter") || pname.Contains("landing")) continue;

                    try
                    {
                        float orig = (float)prop.GetValue(comp);
                        if (Math.Abs(orig) < 0.001f) continue;

                        string key = $"{unitName}_{comp.GetInstanceID()}_{prop.Name}";
                        if (!_originalMoveSpeeds.ContainsKey(key))
                            _originalMoveSpeeds[key] = orig;
                        else
                            orig = _originalMoveSpeeds[key];

                        float newVal = orig * mult;
                        prop.SetValue(comp, newVal);
                        MelonLogger.Msg($"  [{typeName}] prop {prop.Name}: {orig} -> {newVal}");
                        totalModified++;
                    }
                    catch { }
                }
            }

            if (totalModified == 0)
            {
                MelonLogger.Warning($"[MOVESPEED] No speed fields found on '{unitName}' — listing gameplay components:");
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string tn = comp.GetType().Name.ToLower();
                    if (tn == "transform" || tn.Contains("mesh") || tn.Contains("renderer") ||
                        tn.Contains("animator") || tn.Contains("lodgroup") || tn.Contains("particle") ||
                        tn.Contains("collider") || tn.Contains("light") || tn.Contains("audio") ||
                        tn.Contains("canvas") || tn.Contains("cloth") || tn.Contains("rigidbody"))
                        continue;
                    MelonLogger.Msg($"  Component: {comp.GetType().Name} on '{comp.gameObject.name}'");
                }
            }

            return totalModified;
        }

        // Helper: get/set float field or property (Il2Cpp exposes fields as properties)
        private static float GetFloatMember(object obj, string name)
        {
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return (float)field.GetValue(obj);
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null) return (float)prop.GetValue(obj);
            return 0f;
        }

        private static bool SetFloatMember(object obj, string name, float value)
        {
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { field.SetValue(obj, value); return true; }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite) { prop.SetValue(obj, value); return true; }
            return false;
        }

        private static void ScaleProjectileData(object pdObj, string pdName, float rangeMult, float speedMult, HashSet<string> modifiedPD, bool useOM = false)
        {
            string pdTarget = useOM ? $"A:{pdName}.asset" : null;
            float origLt = GetFloatMember(pdObj, "m_fLifeTime");
            float origSpeed = GetFloatMember(pdObj, "m_fBaseSpeed");

            // Check if this is an instant-hit (hitscan/ray) projectile
            bool isInstantHit = false;
            try
            {
                var ihField = pdObj.GetType().GetField("m_InstantHit",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ihField != null)
                    isInstantHit = (bool)ihField.GetValue(pdObj);
                else
                {
                    var ihProp = pdObj.GetType().GetProperty("m_InstantHit",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (ihProp != null && ihProp.CanRead)
                        isInstantHit = (bool)ihProp.GetValue(pdObj);
                }
            }
            catch { }

            if (isInstantHit)
            {
                // For instant-hit: m_fBaseSpeed IS the raycast distance, not speed.
                float newSpeed = origSpeed * rangeMult;
                if (Math.Abs(rangeMult - 1f) > 0.001f && origSpeed > 0)
                {
                    if (useOM) OMSetFloat(pdTarget, "m_fBaseSpeed", newSpeed);
                    else SetFloatMember(pdObj, "m_fBaseSpeed", newSpeed);
                }
                if (Math.Abs(speedMult - 1f) > 0.001f && origSpeed > 0)
                {
                    float finalSpeed = newSpeed * speedMult;
                    if (useOM) OMSetFloat(pdTarget, "m_fBaseSpeed", finalSpeed);
                    else SetFloatMember(pdObj, "m_fBaseSpeed", finalSpeed);
                    newSpeed = finalSpeed;
                }
                MelonLogger.Msg($"  ProjectileData '{pdName}' (instant-hit): Range {origSpeed:F0} -> {newSpeed:F0}{(useOM ? " (OM)" : "")}");
            }
            else
            {
                // Normal projectile: range = speed * lifetime
                float newLt = origLt * rangeMult;
                float newSpeed = origSpeed * speedMult;
                float origRange = origSpeed * origLt;
                float newRange = newSpeed * newLt;

                if (Math.Abs(rangeMult - 1f) > 0.001f && origLt > 0)
                {
                    if (useOM) OMSetFloat(pdTarget, "m_fLifeTime", newLt);
                    else SetFloatMember(pdObj, "m_fLifeTime", newLt);
                    MelonLogger.Msg($"  ProjectileData '{pdName}': Lifetime {origLt} -> {newLt:F2}");
                }

                if (Math.Abs(speedMult - 1f) > 0.001f && origSpeed > 0)
                {
                    if (useOM) OMSetFloat(pdTarget, "m_fBaseSpeed", newSpeed);
                    else SetFloatMember(pdObj, "m_fBaseSpeed", newSpeed);
                    MelonLogger.Msg($"  ProjectileData '{pdName}': Speed {origSpeed} -> {newSpeed:F1}");
                }

                MelonLogger.Msg($"  ProjectileData '{pdName}': Range {origRange:F0} -> {newRange:F0}{(useOM ? " (OM)" : "")}");
            }

            // Scale VisibleEventRadius for instant-hit projectiles (beams need VER to match range)
            // Normal projectiles (flames, balls) don't need VER scaling — projectile renders independently
            if (Math.Abs(rangeMult - 1f) > 0.001f && isInstantHit)
            {
                float origVER = GetFloatMember(pdObj, "VisibleEventRadius");
                if (origVER > 0)
                {
                    float newVER = origVER * rangeMult;
                    if (useOM) OMSetFloat(pdTarget, "VisibleEventRadius", newVER);
                    else SetFloatMember(pdObj, "VisibleEventRadius", newVER);
                    MelonLogger.Msg($"  ProjectileData '{pdName}': VisibleEventRadius {origVER:F0} -> {newVER:F0}");
                }
            }

            modifiedPD.Add(pdName);
        }

        // =============================================
        // Shrimp: prevent AI from auto-attacking (player attacks still work)
        // =============================================

        private static void ApplyShrimpAimDisable()
        {
            try
            {
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                foreach (var info in allInfos)
                {
                    if (info == null || info.DisplayName != "Shrimp" || info.Prefab == null)
                        continue;

                    var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                    foreach (var comp in childComps)
                    {
                        if (comp == null) continue;
                        string typeName = comp.GetType().Name;

                        // 1) CreatureDecapod: set AIMeleeDistance = 0 so AI never initiates melee
                        if (typeName == "CreatureDecapod")
                        {
                            var compType = comp.GetType();
                            var meleeDistField = compType.GetField("AIMeleeDistance",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (meleeDistField != null)
                            {
                                float was = (float)meleeDistField.GetValue(comp);
                                meleeDistField.SetValue(comp, 0f);
                                MelonLogger.Msg($"[SHRIMP] AIMeleeDistance: {was} -> 0");
                            }
                        }

                        // 2) Sensor: set TargetingDistance = 0 so AI can't detect enemies
                        //    (FogOfWarViewDistance is separate — vision stays intact)
                        if (typeName == "Sensor")
                        {
                            var targetDistField = comp.GetType().GetField("TargetingDistance",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (targetDistField != null)
                            {
                                float was = (float)targetDistField.GetValue(comp);
                                targetDistField.SetValue(comp, 0f);
                                MelonLogger.Msg($"[SHRIMP] Sensor.TargetingDistance: {was} -> 0");
                            }
                        }

                        // 3) AIAiming: pause aim tracking
                        if (typeName == "AIAiming")
                        {
                            var aimPausedField = comp.GetType().GetField("AimPaused",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (aimPausedField != null)
                            {
                                aimPausedField.SetValue(comp, true);
                                MelonLogger.Msg($"[SHRIMP] AIAiming.AimPaused -> True");
                            }
                        }
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SHRIMP] ApplyShrimpAimDisable error: {ex.Message}");
            }
        }

        // =============================================
        // Name resolution (units + structures)
        // =============================================

        private static string GetCachedName(GameObject obj)
        {
            if (obj == null) return null;

            int id = obj.GetInstanceID();
            if (_nameCache.TryGetValue(id, out string cached))
                return cached;

            string name = null;

            try
            {
                var unit = obj.GetComponent<Unit>();
                if (unit?.ObjectInfo != null)
                    name = unit.ObjectInfo.DisplayName;
            }
            catch { }

            if (name == null)
            {
                try
                {
                    var structure = obj.GetComponent<Structure>();
                    if (structure?.ObjectInfo != null)
                        name = structure.ObjectInfo.DisplayName;
                }
                catch { }
            }

            if (name != null)
                _nameCache[id] = name;

            return name;
        }

        // =============================================
        // JSON dump: export ALL unit/structure data to JSON for spreadsheet
        // =============================================

        private static void DumpAllUnitsJson()
        {
            try
            {
                string outPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "Si_UnitBalance_Dump.json");

                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                MelonLogger.Msg($"[JSON DUMP] Found {allInfos.Length} ObjectInfo entries, writing to {outPath}");

                // Build production tree: structure display name -> list of buildable display names
                var prodTree = new Dictionary<string, List<string>>();
                // Build reverse map: unit name -> which structure produces it
                var builtAt = new Dictionary<string, string>();

                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    var structure = info.Prefab.GetComponent<Structure>();
                    if (structure == null || structure.ConstructionOptions == null) continue;

                    string structName = info.DisplayName;
                    if (string.IsNullOrEmpty(structName)) continue;

                    // Skip tutorial/TD variants
                    if (info.name != null && (info.name.Contains("_TD") || info.name.Contains("_Intro") || info.name.Contains("_Tutorial")))
                        continue;

                    string team = "?";
                    try { if (structure.DefaultTeam != null) team = structure.DefaultTeam.name; } catch { }

                    foreach (var cd in structure.ConstructionOptions)
                    {
                        if (cd == null || cd.ObjectInfo == null) continue;
                        string prodName = cd.ObjectInfo.DisplayName;
                        if (string.IsNullOrEmpty(prodName)) continue;

                        if (!prodTree.ContainsKey(structName))
                            prodTree[structName] = new List<string>();
                        if (!prodTree[structName].Contains(prodName))
                            prodTree[structName].Add(prodName);

                        // First non-tutorial producer wins
                        if (!builtAt.ContainsKey(prodName))
                            builtAt[prodName] = structName;
                    }
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"units\": [");

                var seen = new HashSet<string>();
                bool first = true;

                foreach (var info in allInfos)
                {
                    if (info == null) continue;
                    string name = info.DisplayName;
                    if (string.IsNullOrEmpty(name)) continue;

                    // Skip tutorial/TD variants by internal name
                    string internalName = info.name ?? "";
                    if (internalName.Contains("_TD") || internalName.Contains("_Intro") || internalName.Contains("_Tutorial"))
                        continue;

                    // Skip duplicates by internal name (not display name — Sol/Cent can share display names)
                    if (seen.Contains(internalName)) continue;

                    // Must have either ConstructionData or be a producible unit
                    if (info.ConstructionData == null) continue;
                    seen.Add(internalName);

                    // Basic info
                    var cd = info.ConstructionData;
                    int cost = cd.ResourceCost;
                    float buildTime = cd.BuildUpTime;
                    int minTier = cd.MinimumTeamTier;
                    int maxTier = cd.MaximumTeamTier;
                    float maxDist = cd.MaximumBaseStructureDistance;
                    bool useRadius = cd.MaximumDistanceUseRadius;

                    // Team
                    string team = "?";
                    string faction = "Unknown";

                    // HP
                    int maxHealth = 0;
                    if (info.Prefab != null)
                    {
                        var dm = info.Prefab.GetComponent<DamageManager>();
                        if (dm != null)
                        {
                            try { maxHealth = (int)dm.MaxHealth; } catch { }
                        }
                    }

                    // Determine faction
                    if (info.Prefab != null)
                    {
                        var structure = info.Prefab.GetComponent<Structure>();
                        if (structure != null)
                        {
                            try { if (structure.DefaultTeam != null) team = structure.DefaultTeam.name; } catch { }
                        }
                        var unit = info.Prefab.GetComponent<Unit>();
                        if (unit != null)
                        {
                            try { if (unit.DefaultTeam != null) team = unit.DefaultTeam.name; } catch { }
                        }
                    }
                    if (team.Contains("Sol")) faction = "Sol";
                    else if (team.Contains("Centauri")) faction = "Centauri";
                    else if (team.Contains("Alien")) faction = "Alien";

                    // Type: structure or unit
                    bool isStructure = false;
                    if (info.Prefab != null)
                    {
                        var structure = info.Prefab.GetComponent<Structure>();
                        isStructure = (structure != null);
                    }

                    // Built at
                    string producer = builtAt.ContainsKey(name) ? builtAt[name] : "";

                    // Movement
                    float moveSpeed = 0, flyMoveSpeed = 0;
                    float walkSpeed = 0, runSpeed = 0, jumpSpeed = 0;
                    // Vehicle movement detail
                    string vehicleType = "";  // "Wheeled", "Hovered", or ""
                    float vehAccel = 0;       // WheelMotorAccel (%) or MoveAcceleration (m/s^2)
                    float vehTurnRadius = 0;  // TurningCircleRadius (wheeled)
                    float vehTurnSpeed = 0;   // TurnSpeed (hovered, deg/s)
                    float vehReverseScale = 0; // ReverseSpeedScale (wheeled)

                    // Attack (creature)
                    float atkDamage = 0, atkCooldown = 0, atkRange = 0, atkSpread = 0;
                    string atkProjName = "";
                    float atkDamageSec = 0, atkCooldownSec = 0, atkRangeSec = 0, atkSpreadSec = 0;
                    string atkProjNameSec = "";

                    // Projectile data
                    float projSpeed = 0, projLifetime = 0;
                    bool instantHit = false;
                    float projSpeedSec = 0, projLifetimeSec = 0;
                    bool instantHitSec = false;

                    // Detection
                    float fowView = 0, targetDist = 0;

                    // Vehicle turret
                    float vtFireInterval = 0, vtMuzzleSpread = 0;
                    int vtMagazine = 0, vtShotCount = 0;
                    float vtReloadTime = 0;
                    float vtFireIntervalSec = 0, vtMuzzleSpreadSec = 0, vtReloadTimeSec = 0;
                    int vtMagazineSec = 0, vtShotCountSec = 0;
                    // VT projectile data
                    string vtProjName = "", vtProjName2 = "";
                    float vtImpactDmg = 0, vtRicochetDmg = 0, vtSplashDmg = 0, vtPenDmg = 0;
                    bool vtHasSplash = false, vtHasPen = false, vtInstantHit = false;
                    float vtProjSpeed = 0, vtProjLifetime = 0;
                    float vtImpactDmg2 = 0, vtRicochetDmg2 = 0, vtSplashDmg2 = 0, vtPenDmg2 = 0;
                    bool vtHasSplash2 = false, vtHasPen2 = false, vtInstantHit2 = false;
                    float vtProjSpeed2 = 0, vtProjLifetime2 = 0;
                    // Creature projectile damage
                    float projImpactDmg = 0, projRicochetDmg = 0, projSplashDmg = 0;
                    float projImpactDmg2 = 0, projRicochetDmg2 = 0, projSplashDmg2 = 0;
                    // TeleportUI (on structures)
                    bool hasTeleport = false;
                    float teleportTime = 0, teleportCooldown = 0;
                    float teleportPct = 0;
                    bool teleportSameTeamOnly = false;

                    if (info.Prefab != null)
                    {
                        // Creature movement + attacks
                        var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                        foreach (var comp in childComps)
                        {
                            if (comp == null) continue;
                            var ct = comp.GetType();
                            string tn = ct.Name;

                            // CreatureDecapod: movement + attacks
                            if (tn == "CreatureDecapod")
                            {
                                try { var f = ct.GetField("MoveSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) moveSpeed = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("FlyMoveSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) flyMoveSpeed = (float)f.GetValue(comp); } catch { }

                                // Primary attack
                                try
                                {
                                    object ap = null;
                                    var apField = ct.GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                                    if (apField != null) ap = apField.GetValue(comp);
                                    else { var apProp = ct.GetProperty("AttackPrimary", BindingFlags.Public | BindingFlags.Instance); if (apProp != null) ap = apProp.GetValue(comp); }

                                    if (ap != null)
                                    {
                                        var at = ap.GetType();
                                        try { atkDamage = GetFloatField(at, ap, "Damage"); } catch { }
                                        try { atkCooldown = GetFloatField(at, ap, "CoolDownTime"); } catch { }
                                        try { atkRange = GetFloatField(at, ap, "AttackProjectileAimDistMax"); } catch { }
                                        try { atkSpread = GetFloatField(at, ap, "AttackProjectileSpread"); } catch { }
                                        // Get projectile data name
                                        try
                                        {
                                            object pdObj = null;
                                            var pdfField = at.GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                            if (pdfField != null) pdObj = pdfField.GetValue(ap);
                                            else { var pdfProp = at.GetProperty("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance); if (pdfProp != null) pdObj = pdfProp.GetValue(ap); }
                                            if (pdObj != null)
                                            {
                                                atkProjName = (pdObj as UnityEngine.Object)?.name ?? "";
                                                var pdt = pdObj.GetType();
                                                try { projSpeed = GetFloatField(pdt, pdObj, "m_fBaseSpeed"); } catch { }
                                                try { projLifetime = GetFloatField(pdt, pdObj, "m_fLifeTime"); } catch { }
                                                try
                                                {
                                                    var ihf = pdt.GetField("m_InstantHit", BindingFlags.Public | BindingFlags.Instance);
                                                    if (ihf != null) instantHit = (bool)ihf.GetValue(pdObj);
                                                    else { var ihp = pdt.GetProperty("m_InstantHit", BindingFlags.Public | BindingFlags.Instance); if (ihp != null) instantHit = (bool)ihp.GetValue(pdObj); }
                                                }
                                                catch { }
                                                // Projectile damage fields
                                                try { projImpactDmg = GetFloatField(pdt, pdObj, "m_fImpactDamage"); } catch { }
                                                try { projRicochetDmg = GetFloatField(pdt, pdObj, "m_fRicochetDamage"); } catch { }
                                                try { projSplashDmg = GetFloatField(pdt, pdObj, "m_fSplashDamageMax"); } catch { }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }

                                // Secondary attack
                                try
                                {
                                    object asec = null;
                                    var asField = ct.GetField("AttackSecondary", BindingFlags.Public | BindingFlags.Instance);
                                    if (asField != null) asec = asField.GetValue(comp);
                                    else { var asProp = ct.GetProperty("AttackSecondary", BindingFlags.Public | BindingFlags.Instance); if (asProp != null) asec = asProp.GetValue(comp); }

                                    if (asec != null)
                                    {
                                        var ast = asec.GetType();
                                        try { atkDamageSec = GetFloatField(ast, asec, "Damage"); } catch { }
                                        try { atkCooldownSec = GetFloatField(ast, asec, "CoolDownTime"); } catch { }
                                        try { atkRangeSec = GetFloatField(ast, asec, "AttackProjectileAimDistMax"); } catch { }
                                        try { atkSpreadSec = GetFloatField(ast, asec, "AttackProjectileSpread"); } catch { }
                                        try
                                        {
                                            object pdObj = null;
                                            var pdfField = ast.GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                            if (pdfField != null) pdObj = pdfField.GetValue(asec);
                                            else { var pdfProp = ast.GetProperty("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance); if (pdfProp != null) pdObj = pdfProp.GetValue(asec); }
                                            if (pdObj != null)
                                            {
                                                atkProjNameSec = (pdObj as UnityEngine.Object)?.name ?? "";
                                                var pdt = pdObj.GetType();
                                                try { projSpeedSec = GetFloatField(pdt, pdObj, "m_fBaseSpeed"); } catch { }
                                                try { projLifetimeSec = GetFloatField(pdt, pdObj, "m_fLifeTime"); } catch { }
                                                try
                                                {
                                                    var ihf = pdt.GetField("m_InstantHit", BindingFlags.Public | BindingFlags.Instance);
                                                    if (ihf != null) instantHitSec = (bool)ihf.GetValue(pdObj);
                                                    else { var ihp = pdt.GetProperty("m_InstantHit", BindingFlags.Public | BindingFlags.Instance); if (ihp != null) instantHitSec = (bool)ihp.GetValue(pdObj); }
                                                }
                                                catch { }
                                                // Projectile damage fields
                                                try { projImpactDmg2 = GetFloatField(pdt, pdObj, "m_fImpactDamage"); } catch { }
                                                try { projRicochetDmg2 = GetFloatField(pdt, pdObj, "m_fRicochetDamage"); } catch { }
                                                try { projSplashDmg2 = GetFloatField(pdt, pdObj, "m_fSplashDamageMax"); } catch { }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }

                            // Sensor: detection ranges
                            if (tn == "Sensor")
                            {
                                try { var f = ct.GetField("FogOfWarViewDistance", BindingFlags.Public | BindingFlags.Instance); if (f != null) { float val = (float)f.GetValue(comp); if (val > fowView) fowView = val; } } catch { }
                                try { var f = ct.GetField("TargetingDistance", BindingFlags.Public | BindingFlags.Instance); if (f != null) { float val = (float)f.GetValue(comp); if (val > targetDist) targetDist = val; } } catch { }
                            }

                            // VehicleTurret: weapon data + projectile damage
                            if (tn == "VehicleTurret")
                            {
                                try { var f = ct.GetField("PrimaryFireInterval", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtFireInterval = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("PrimaryMuzzleSpread", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtMuzzleSpread = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("PrimaryMagazineSize", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtMagazine = (int)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("PrimaryReloadTime", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtReloadTime = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("PrimaryShotCount", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtShotCount = (int)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryFireInterval", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtFireIntervalSec = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryMuzzleSpread", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtMuzzleSpreadSec = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryMagazineSize", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtMagazineSec = (int)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryReloadTime", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtReloadTimeSec = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryShotCount", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtShotCountSec = (int)f.GetValue(comp); } catch { }

                                // Read PrimaryProjectileData
                                try
                                {
                                    object pdObj = null;
                                    var pf = ct.GetField("PrimaryProjectileData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (pf != null) pdObj = pf.GetValue(comp);
                                    if (pdObj != null)
                                    {
                                        vtProjName = (pdObj as UnityEngine.Object)?.name ?? "";
                                        var pdt = pdObj.GetType();
                                        try { vtImpactDmg = GetFloatField(pdt, pdObj, "m_fImpactDamage"); } catch { }
                                        try { vtRicochetDmg = GetFloatField(pdt, pdObj, "m_fRicochetDamage"); } catch { }
                                        try { vtSplashDmg = GetFloatField(pdt, pdObj, "m_fSplashDamageMax"); } catch { }
                                        try { vtPenDmg = GetFloatField(pdt, pdObj, "m_fPenetratingDamage"); } catch { }
                                        try { vtProjSpeed = GetFloatField(pdt, pdObj, "m_fBaseSpeed"); } catch { }
                                        try { vtProjLifetime = GetFloatField(pdt, pdObj, "m_fLifeTime"); } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_bSplash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtHasSplash = (bool)ihf.GetValue(pdObj);
                                        } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_bPenetrating", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtHasPen = (bool)ihf.GetValue(pdObj);
                                        } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_InstantHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtInstantHit = (bool)ihf.GetValue(pdObj);
                                            else { var ihp = pdt.GetProperty("m_InstantHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (ihp != null) vtInstantHit = (bool)ihp.GetValue(pdObj); }
                                        } catch { }
                                    }
                                } catch { }

                                // Read SecondaryProjectileData
                                try
                                {
                                    object pdObj = null;
                                    var pf = ct.GetField("SecondaryProjectileData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (pf != null) pdObj = pf.GetValue(comp);
                                    if (pdObj != null)
                                    {
                                        vtProjName2 = (pdObj as UnityEngine.Object)?.name ?? "";
                                        var pdt = pdObj.GetType();
                                        try { vtImpactDmg2 = GetFloatField(pdt, pdObj, "m_fImpactDamage"); } catch { }
                                        try { vtRicochetDmg2 = GetFloatField(pdt, pdObj, "m_fRicochetDamage"); } catch { }
                                        try { vtSplashDmg2 = GetFloatField(pdt, pdObj, "m_fSplashDamageMax"); } catch { }
                                        try { vtPenDmg2 = GetFloatField(pdt, pdObj, "m_fPenetratingDamage"); } catch { }
                                        try { vtProjSpeed2 = GetFloatField(pdt, pdObj, "m_fBaseSpeed"); } catch { }
                                        try { vtProjLifetime2 = GetFloatField(pdt, pdObj, "m_fLifeTime"); } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_bSplash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtHasSplash2 = (bool)ihf.GetValue(pdObj);
                                        } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_bPenetrating", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtHasPen2 = (bool)ihf.GetValue(pdObj);
                                        } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_InstantHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtInstantHit2 = (bool)ihf.GetValue(pdObj);
                                            else { var ihp = pdt.GetProperty("m_InstantHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (ihp != null) vtInstantHit2 = (bool)ihp.GetValue(pdObj); }
                                        } catch { }
                                    }
                                } catch { }
                            }

                            // CreatureTurret: additional turret for creatures
                            if (tn == "CreatureTurret")
                            {
                                // Already captured via CreatureDecapod attacks
                            }

                            // PlayerMovement: infantry walk/run/jump
                            if (tn == "PlayerMovement" || tn == "FPSMovement")
                            {
                                try { var f = ct.GetField("WalkSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) walkSpeed = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("RunSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) runSpeed = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("JumpSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) jumpSpeed = (float)f.GetValue(comp); } catch { }
                            }

                            // VehicleWheeled: ground vehicle movement
                            if (tn == "VehicleWheeled")
                            {
                                vehicleType = "Wheeled";
                                try { var f = ct.GetField("MoveSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) { float val = (float)f.GetValue(comp); if (val > moveSpeed) moveSpeed = val; } } catch { }
                                try { var f = ct.GetField("WheelMotorAccel", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehAccel = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("TurningCircleRadius", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehTurnRadius = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("ReverseSpeedScale", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehReverseScale = (float)f.GetValue(comp); } catch { }
                            }

                            // VehicleHovered: hover/air vehicle movement
                            if (tn == "VehicleHovered")
                            {
                                vehicleType = "Hovered";
                                try { var f = ct.GetField("MoveSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) { float val = (float)f.GetValue(comp); if (val > moveSpeed) moveSpeed = val; } } catch { }
                                try { var f = ct.GetField("MoveAcceleration", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehAccel = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("TurnSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehTurnSpeed = (float)f.GetValue(comp); } catch { }
                            }

                            // TeleportUI: teleportation settings on structures
                            if (tn == "TeleportUI")
                            {
                                hasTeleport = true;
                                try { var f = ct.GetField("TeleportTime", BindingFlags.Public | BindingFlags.Instance); if (f != null) teleportTime = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("TeleportCooldownTime", BindingFlags.Public | BindingFlags.Instance); if (f != null) teleportCooldown = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("TeleportPct", BindingFlags.Public | BindingFlags.Instance); if (f != null) teleportPct = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("OnlyForSameTeamUnit", BindingFlags.Public | BindingFlags.Instance); if (f != null) teleportSameTeamOnly = (bool)f.GetValue(comp); } catch { }
                            }
                        }
                    }

                    // Write JSON entry
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append("    {");
                    sb.Append($"\"name\":\"{EscJson(name)}\",");
                    sb.Append($"\"internal\":\"{EscJson(info.name)}\",");
                    sb.Append($"\"faction\":\"{faction}\",");
                    sb.Append($"\"team\":\"{EscJson(team)}\",");
                    sb.Append($"\"is_structure\":{(isStructure ? "true" : "false")},");
                    sb.Append($"\"built_at\":\"{EscJson(producer)}\",");
                    sb.Append($"\"cost\":{cost},");
                    sb.Append($"\"build_time\":{buildTime:F1},");
                    sb.Append($"\"min_tier\":{minTier},");
                    sb.Append($"\"max_tier\":{maxTier},");
                    sb.Append($"\"hp\":{maxHealth},");
                    sb.Append($"\"max_dist\":{maxDist:F0},");
                    sb.Append($"\"use_radius\":{(useRadius ? "true" : "false")},");
                    // Movement
                    sb.Append($"\"move_speed\":{moveSpeed:F1},");
                    sb.Append($"\"fly_speed\":{flyMoveSpeed:F1},");
                    sb.Append($"\"walk_speed\":{walkSpeed:F1},");
                    sb.Append($"\"run_speed\":{runSpeed:F1},");
                    sb.Append($"\"jump_speed\":{jumpSpeed:F2},");
                    // Vehicle movement detail
                    sb.Append($"\"veh_type\":\"{vehicleType}\",");
                    sb.Append($"\"veh_accel\":{vehAccel:F2},");
                    sb.Append($"\"veh_turn_radius\":{vehTurnRadius:F1},");
                    sb.Append($"\"veh_turn_speed\":{vehTurnSpeed:F1},");
                    sb.Append($"\"veh_reverse_scale\":{vehReverseScale:F2},");
                    // Primary attack
                    sb.Append($"\"atk_damage\":{atkDamage:F0},");
                    sb.Append($"\"atk_cooldown\":{atkCooldown:F2},");
                    sb.Append($"\"atk_range\":{atkRange:F0},");
                    sb.Append($"\"atk_spread\":{atkSpread:F2},");
                    sb.Append($"\"atk_proj\":\"{EscJson(atkProjName)}\",");
                    sb.Append($"\"proj_speed\":{projSpeed:F1},");
                    sb.Append($"\"proj_lifetime\":{projLifetime:F2},");
                    sb.Append($"\"instant_hit\":{(instantHit ? "true" : "false")},");
                    // Secondary attack
                    sb.Append($"\"atk2_damage\":{atkDamageSec:F0},");
                    sb.Append($"\"atk2_cooldown\":{atkCooldownSec:F2},");
                    sb.Append($"\"atk2_range\":{atkRangeSec:F0},");
                    sb.Append($"\"atk2_spread\":{atkSpreadSec:F2},");
                    sb.Append($"\"atk2_proj\":\"{EscJson(atkProjNameSec)}\",");
                    sb.Append($"\"proj2_speed\":{projSpeedSec:F1},");
                    sb.Append($"\"proj2_lifetime\":{projLifetimeSec:F2},");
                    sb.Append($"\"instant_hit2\":{(instantHitSec ? "true" : "false")},");
                    // Detection
                    sb.Append($"\"fow_view\":{fowView:F0},");
                    sb.Append($"\"target_dist\":{targetDist:F0},");
                    // Vehicle turret
                    sb.Append($"\"vt_fire_interval\":{vtFireInterval:F4},");
                    sb.Append($"\"vt_spread\":{vtMuzzleSpread:F3},");
                    sb.Append($"\"vt_magazine\":{vtMagazine},");
                    sb.Append($"\"vt_reload\":{vtReloadTime:F1},");
                    sb.Append($"\"vt_shot_count\":{vtShotCount},");
                    sb.Append($"\"vt2_fire_interval\":{vtFireIntervalSec:F4},");
                    sb.Append($"\"vt2_spread\":{vtMuzzleSpreadSec:F3},");
                    sb.Append($"\"vt2_magazine\":{vtMagazineSec},");
                    sb.Append($"\"vt2_shot_count\":{vtShotCountSec},");
                    sb.Append($"\"vt2_reload\":{vtReloadTimeSec:F1},");
                    // VT primary projectile
                    sb.Append($"\"vt_proj\":\"{EscJson(vtProjName)}\",");
                    sb.Append($"\"vt_impact_dmg\":{vtImpactDmg:F1},");
                    sb.Append($"\"vt_ricochet_dmg\":{vtRicochetDmg:F1},");
                    sb.Append($"\"vt_splash_dmg\":{vtSplashDmg:F1},");
                    sb.Append($"\"vt_has_splash\":{(vtHasSplash ? "true" : "false")},");
                    sb.Append($"\"vt_pen_dmg\":{vtPenDmg:F1},");
                    sb.Append($"\"vt_has_pen\":{(vtHasPen ? "true" : "false")},");
                    sb.Append($"\"vt_instant_hit\":{(vtInstantHit ? "true" : "false")},");
                    sb.Append($"\"vt_proj_speed\":{vtProjSpeed:F1},");
                    sb.Append($"\"vt_proj_lifetime\":{vtProjLifetime:F2},");
                    // VT secondary projectile
                    sb.Append($"\"vt2_proj\":\"{EscJson(vtProjName2)}\",");
                    sb.Append($"\"vt2_impact_dmg\":{vtImpactDmg2:F1},");
                    sb.Append($"\"vt2_ricochet_dmg\":{vtRicochetDmg2:F1},");
                    sb.Append($"\"vt2_splash_dmg\":{vtSplashDmg2:F1},");
                    sb.Append($"\"vt2_has_splash\":{(vtHasSplash2 ? "true" : "false")},");
                    sb.Append($"\"vt2_pen_dmg\":{vtPenDmg2:F1},");
                    sb.Append($"\"vt2_has_pen\":{(vtHasPen2 ? "true" : "false")},");
                    sb.Append($"\"vt2_instant_hit\":{(vtInstantHit2 ? "true" : "false")},");
                    sb.Append($"\"vt2_proj_speed\":{vtProjSpeed2:F1},");
                    sb.Append($"\"vt2_proj_lifetime\":{vtProjLifetime2:F2},");
                    // Creature projectile damage
                    sb.Append($"\"proj_impact_dmg\":{projImpactDmg:F1},");
                    sb.Append($"\"proj_ricochet_dmg\":{projRicochetDmg:F1},");
                    sb.Append($"\"proj_splash_dmg\":{projSplashDmg:F1},");
                    sb.Append($"\"proj2_impact_dmg\":{projImpactDmg2:F1},");
                    sb.Append($"\"proj2_ricochet_dmg\":{projRicochetDmg2:F1},");
                    sb.Append($"\"proj2_splash_dmg\":{projSplashDmg2:F1},");
                    // Teleport
                    sb.Append($"\"has_teleport\":{(hasTeleport ? "true" : "false")},");
                    sb.Append($"\"teleport_time\":{teleportTime:F1},");
                    sb.Append($"\"teleport_cooldown\":{teleportCooldown:F1},");
                    sb.Append($"\"teleport_pct\":{teleportPct:F2},");
                    sb.Append($"\"teleport_same_team\":{(teleportSameTeamOnly ? "true" : "false")}");
                    sb.Append("}");
                }

                sb.AppendLine();
                sb.AppendLine("  ],");

                // Production tree
                sb.AppendLine("  \"production_tree\": {");
                bool firstProd = true;
                foreach (var kv in prodTree)
                {
                    if (!firstProd) sb.AppendLine(",");
                    firstProd = false;
                    sb.Append($"    \"{EscJson(kv.Key)}\": [");
                    sb.Append(string.Join(", ", kv.Value.ConvertAll(n => $"\"{EscJson(n)}\"")));
                    sb.Append("]");
                }
                sb.AppendLine();
                sb.AppendLine("  }");
                sb.AppendLine("}");

                System.IO.File.WriteAllText(outPath, sb.ToString());
                MelonLogger.Msg($"[JSON DUMP] Wrote {seen.Count} entries to {outPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JSON DUMP] Error: {ex}");
            }
        }

        private static float GetFloatField(Type t, object obj, string fieldName)
        {
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f != null)
            {
                object val = f.GetValue(obj);
                if (val is float fv) return fv;
                if (val is int iv) return iv;
            }
            var p = t.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null)
            {
                object val = p.GetValue(obj);
                if (val is float fv) return fv;
                if (val is int iv) return iv;
            }
            return 0;
        }

        private static string EscJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // =============================================
        // Field discovery: dump ObjectInfo, Unit, Structure fields
        // to find cost/build time/etc. field names
        // =============================================

        private static void DumpFieldDiscovery()
        {
            MelonLogger.Msg("========== Field Discovery ==========");
            try
            {
                // Dump ObjectInfo fields (first instance)
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                if (allInfos.Length > 0)
                {
                    DumpTypeFields("ObjectInfo", allInfos[0]);

                    // Find one unit and one structure to dump their fields
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.Prefab == null) continue;

                        var unit = info.Prefab.GetComponent<Unit>();
                        if (unit != null)
                        {
                            MelonLogger.Msg($"--- Unit fields (from '{info.DisplayName}') ---");
                            DumpTypeFields("Unit", unit);

                            // Also dump DamageManager and DamageManagerData fields
                            var dm = info.Prefab.GetComponent<DamageManager>();
                            if (dm != null)
                            {
                                DumpTypeFields("DamageManager", dm);
                                // Dump the underlying DamageManagerData asset
                                var dataField = dm.GetType().GetField("Data",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (dataField != null)
                                {
                                    object dataObj = dataField.GetValue(dm);
                                    if (dataObj != null)
                                        DumpTypeFields("DamageManagerData", dataObj);
                                }
                            }
                            break;
                        }
                    }

                    // Dump ConstructionData fields (from first unit that has one)
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.ConstructionData == null) continue;
                        MelonLogger.Msg($"--- ConstructionData fields (from '{info.DisplayName}') ---");
                        DumpTypeFields("ConstructionData", info.ConstructionData);
                        break;
                    }

                    foreach (var info in allInfos)
                    {
                        if (info == null || info.Prefab == null) continue;

                        var structure = info.Prefab.GetComponent<Structure>();
                        if (structure != null)
                        {
                            MelonLogger.Msg($"--- Structure fields (from '{info.DisplayName}') ---");
                            DumpTypeFields("Structure", structure);
                            break;
                        }
                    }
                }

                // === Deep dump: ALL components on combat units to find weapon/range fields ===
                string[] dumpTargets = { "Shrimp", "Crab", "Behemoth", "Scorpion", "Defiler", "Colossus", "Dragonfly", "Goliath", "Hunter", "Firebug", "Railgun Tank", "Nest", "Interceptor", "Headquarters", "Combat Tank", "Heavy Tank", "Barrage Truck" };
                foreach (string targetName in dumpTargets)
                {
                    MelonLogger.Msg($"--- {targetName.ToUpper()} DEEP COMPONENT DUMP ---");
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.DisplayName != targetName) continue;
                        if (info.Prefab == null) { MelonLogger.Msg($"  {targetName} prefab is null"); break; }

                        // Root components
                        var rootComps = info.Prefab.GetComponents<Component>();
                        MelonLogger.Msg($"  {targetName} has {rootComps.Length} root components:");
                        foreach (var comp in rootComps)
                        {
                            if (comp == null) continue;
                            string cName = comp.GetType().Name;
                            MelonLogger.Msg($"  --- Root Component: {cName} ({comp.GetType().FullName}) ---");
                            DumpTypeFields(cName, comp);

                            // Recursively dump referenced data objects (e.g., CreatureAttack on CreatureDecapod)
                            if (cName == "CreatureDecapod")
                            {
                                var compType = comp.GetType();
                                foreach (var rf in compType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                                {
                                    if (rf.FieldType.Name == "CreatureAttack")
                                    {
                                        object attackObj = rf.GetValue(comp);
                                        if (attackObj != null)
                                        {
                                            MelonLogger.Msg($"  --- Referenced Object: {rf.Name} (type {rf.FieldType.FullName}) ---");
                                            DumpTypeFields($"CreatureAttack:{rf.Name}", attackObj);
                                        }
                                        else
                                        {
                                            MelonLogger.Msg($"  --- Referenced Object: {rf.Name} = NULL ---");
                                        }
                                    }
                                }
                            }

                            // Dump VehicleTurret projectile references
                            if (cName == "VehicleTurret")
                            {
                                var vtType = comp.GetType();
                                foreach (string projField in new[] { "PrimaryProjectile", "SecondaryProjectile" })
                                {
                                    try
                                    {
                                        var pf = vtType.GetField(projField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                        if (pf == null) { var pp = vtType.GetProperty(projField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (pp != null) { object pdObj = pp.GetValue(comp); if (pdObj != null) { MelonLogger.Msg($"  --- VT {projField}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{projField}", pdObj); } } }
                                        else { object pdObj = pf.GetValue(comp); if (pdObj != null) { MelonLogger.Msg($"  --- VT {projField}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{projField}", pdObj); } else { MelonLogger.Msg($"  --- VT {projField}: NULL ---"); } }
                                    }
                                    catch (Exception ex) { MelonLogger.Msg($"  --- VT {projField}: Error: {ex.Message} ---"); }
                                }
                                // Also dump all fields that reference ProjectileData
                                foreach (var rf in vtType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    if (rf.FieldType.Name == "ProjectileData" && rf.Name != "PrimaryProjectile" && rf.Name != "SecondaryProjectile")
                                    {
                                        try { object pdObj = rf.GetValue(comp); if (pdObj != null) { MelonLogger.Msg($"  --- VT {rf.Name}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{rf.Name}", pdObj); } }
                                        catch { }
                                    }
                                }
                            }
                        }

                        // ALL child components (no filter — we need to find weapon/range types)
                        var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                        var seen = new HashSet<int>();
                        foreach (var comp in rootComps) { if (comp != null) seen.Add(comp.GetInstanceID()); }
                        MelonLogger.Msg($"  --- Child components (total {childComps.Length}, skipping root-level) ---");
                        foreach (var comp in childComps)
                        {
                            if (comp == null || seen.Contains(comp.GetInstanceID())) continue;
                            seen.Add(comp.GetInstanceID());
                            string cName = comp.GetType().Name;
                            // Skip noise: Transform, MeshFilter, MeshRenderer, SkinnedMeshRenderer, Animator, LODGroup, ParticleSystem
                            string lower = cName.ToLower();
                            if (lower == "transform" || lower.Contains("mesh") || lower.Contains("renderer") ||
                                lower.Contains("animator") || lower.Contains("lodgroup") || lower.Contains("particle") ||
                                lower.Contains("collider") || lower.Contains("light") || lower.Contains("audio") ||
                                lower.Contains("canvas") || lower.Contains("cloth"))
                                continue;
                            string goName = comp.gameObject != null ? comp.gameObject.name : "?";
                            MelonLogger.Msg($"  --- Child [{goName}] Component: {cName} ({comp.GetType().FullName}) ---");
                            DumpTypeFields(cName, comp);
                        }
                        break;
                    }
                    MelonLogger.Msg($"--- END {targetName.ToUpper()} DUMP ---");
                }

                // Production tree dump: what each structure produces, with faction
                MelonLogger.Msg("--- Production Trees (Structure -> Producible Items) ---");
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    var structure = info.Prefab.GetComponent<Structure>();
                    if (structure == null || structure.ConstructionOptions == null || structure.ConstructionOptions.Count == 0) continue;

                    string team = "?";
                    try { if (structure.DefaultTeam != null) team = structure.DefaultTeam.name; } catch { }

                    string prodCd = "";
                    if (info.ConstructionData != null)
                        prodCd = $" Cost={info.ConstructionData.ResourceCost} Build={info.ConstructionData.BuildUpTime:F0}s MinTier={info.ConstructionData.MinimumTeamTier} MaxDist={info.ConstructionData.MaximumBaseStructureDistance:F0} UseRadius={info.ConstructionData.MaximumDistanceUseRadius}";

                    MelonLogger.Msg($"  PRODUCER: {info.DisplayName} ({info.name}) [{team}]{prodCd}");
                    foreach (var cd in structure.ConstructionOptions)
                    {
                        if (cd == null) continue;
                        string prodName = "(unknown)";
                        string prodInfoName = "";
                        try
                        {
                            if (cd.ObjectInfo != null)
                            {
                                prodName = cd.ObjectInfo.DisplayName ?? cd.ObjectInfo.name;
                                prodInfoName = cd.ObjectInfo.name;
                            }
                        }
                        catch { }
                        MelonLogger.Msg($"    -> {prodName,-25} Cost={cd.ResourceCost,5} Build={cd.BuildUpTime,4:F0}s MinTier={cd.MinimumTeamTier,2} TechTier={cd.TechnologyTier,2} MaxDist={cd.MaximumBaseStructureDistance:F0} UseRadius={cd.MaximumDistanceUseRadius} ({prodInfoName})");
                    }
                }

                // === ProjectileData asset dump: speed, lifetime, damage, gravity ===
                MelonLogger.Msg("--- ProjectileData Asset Dump ---");
                var allProjectiles = Resources.FindObjectsOfTypeAll<ProjectileData>();
                MelonLogger.Msg($"  Found {allProjectiles.Length} ProjectileData assets");
                foreach (var pd in allProjectiles)
                {
                    if (pd == null) continue;
                    MelonLogger.Msg($"  === ProjectileData: {pd.name} ===");
                    DumpTypeFields("ProjectileData", pd);
                }
                MelonLogger.Msg("--- END ProjectileData Dump ---");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"DumpFieldDiscovery error: {ex.Message}");
            }
            MelonLogger.Msg("========== End Field Discovery ==========");
        }

        private static void DumpTypeFields(string typeName, object obj)
        {
            try
            {
                var type = obj.GetType();
                MelonLogger.Msg($"  [{typeName}] Type: {type.FullName}");

                // Public fields
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    try
                    {
                        object val = f.GetValue(obj);
                        string valStr = val?.ToString() ?? "null";
                        if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                        MelonLogger.Msg($"  [{typeName}] field {f.FieldType.Name,-16} {f.Name} = {valStr}");
                    }
                    catch
                    {
                        MelonLogger.Msg($"  [{typeName}] field {f.FieldType.Name,-16} {f.Name} = (error reading)");
                    }
                }

                // Public properties
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var p in props)
                {
                    if (!p.CanRead) continue;
                    try
                    {
                        object val = p.GetValue(obj);
                        string valStr = val?.ToString() ?? "null";
                        if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                        MelonLogger.Msg($"  [{typeName}] prop  {p.PropertyType.Name,-16} {p.Name} = {valStr}");
                    }
                    catch
                    {
                        MelonLogger.Msg($"  [{typeName}] prop  {p.PropertyType.Name,-16} {p.Name} = (error reading)");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"  [{typeName}] Error dumping: {ex.Message}");
            }
        }
    }
}

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
        private static string _configSaveDir = "";  // subfolder for saved configs
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

        // Chat commands: !rebalance, !b (balance editor UI)
        private static MethodInfo _sendChatToPlayerMethod; // HelperMethods.SendChatMessageToPlayer
        private static MethodInfo _sendConsoleToPlayerMethod; // HelperMethods.SendConsoleMessageToPlayer
        private static MethodInfo _registerAdminCmdMethod;
        private static Type _adminPowerType;
        private static Type _adminCallbackType;
        private static Type _adminPlayerType;
        private static FieldInfo _playerNameField;
        private static FieldInfo _playerIdField; // PlayerID (NetworkID struct)

        // Per-player menu state for !b command (keyed by SteamID for stable identity across Il2Cpp wrappers)
        private static readonly Dictionary<long, BalanceMenuState> _menuStates = new Dictionary<long, BalanceMenuState>();

        private enum MenuLevel { Root, Faction, Category, Unit, ParamGroup, JsonMenu, JsonLoad, HTPMenu, HTPHoverbike, HTPHoverbikeParam, HTPTier, HTPTeleport }
        private class BalanceMenuState
        {
            public MenuLevel Level = MenuLevel.Root;
            public int FactionIdx;
            public int CategoryIdx;
            public int UnitIdx;
            public int ParamGroupIdx;
            // HTP state
            public int HTPParamGroupIdx;       // for Hoverbike param groups
            public int HTPTeleportStructIdx;   // for Teleportation structure selection
            // Pending confirmation
            public bool PendingConfirm;
            public string PendingParamKey;
            public float PendingValue;
            public string PendingOldVal;
            public string PendingUnitName;     // set by HTP editors (null = use _unitNames lookup)
            public string PendingTechTierKey;  // e.g. "tier_3" for tech_time edits
            // JSON handling state
            public bool JsonPendingSave;        // waiting for save name input
            public string JsonPendingAction;    // "blank" or "load" for confirm prompts
            public string JsonPendingFile;      // file to load (for confirm)
            public string[] JsonLoadFileList;   // cached file list for load menu
        }

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
        private static readonly Dictionary<string, float> _jumpSpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _fowDistanceOverrides =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _visibleEventRadiusMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _lifetimeMultipliers =
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

        // Teleport overrides (from _teleport pseudo-unit in config)
        private static float _teleportCooldown = -1f;
        private static float _teleportDuration = -1f;

        // Dispenser timeout override (absolute seconds, -1 = no override)
        private static float _dispenseTimeout = -1f;

        public override void OnInitializeMelon()
        {
            var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            _configPath = Path.Combine(modDir, "Si_UnitBalance_Config.json");
            _configSaveDir = Path.Combine(modDir, "Si_UnitBalance_Configs");

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

                // Patch VehicleDispenser.RequestVehicle to block dispensing if team tier < min_tier
                Type vehicleDispenserType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    vehicleDispenserType = asm.GetType("VehicleDispenser");
                    if (vehicleDispenserType != null) break;
                }
                if (vehicleDispenserType != null)
                {
                    var requestVehicle = AccessTools.Method(vehicleDispenserType, "RequestVehicle");
                    if (requestVehicle != null)
                    {
                        harmony.Patch(requestVehicle,
                            prefix: new HarmonyMethod(typeof(Patch_VehicleDispenser), "Prefix"));
                        MelonLogger.Msg("Patched VehicleDispenser.RequestVehicle for min_tier enforcement");
                    }
                }

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

                // Register chat commands via AdminMod API
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
                        // Cache AdminMod types for reuse
                        _adminPlayerType = playerType;
                        _adminCallbackType = helperType.GetNestedType("CommandCallback", BindingFlags.Public);
                        if (_adminCallbackType == null)
                            _adminCallbackType = helperType.Assembly.GetType("SilicaAdminMod.HelperMethods+CommandCallback");
                        _adminPowerType = helperType.Assembly.GetType("SilicaAdminMod.Power");
                        _registerAdminCmdMethod = helperType.GetMethod("RegisterAdminCommand", BindingFlags.Public | BindingFlags.Static);

                        // Cache SendChatMessageToPlayer for UI
                        _sendChatToPlayerMethod = helperType.GetMethod("SendChatMessageToPlayer", BindingFlags.Public | BindingFlags.Static);
                        _sendConsoleToPlayerMethod = helperType.GetMethod("SendConsoleMessageToPlayer", BindingFlags.Public | BindingFlags.Static);

                        // Cache Player.PlayerName and Player.PlayerID for audit log
                        _playerNameField = playerType.GetField("PlayerName", BindingFlags.Public | BindingFlags.Instance);
                        _playerIdField = playerType.GetField("PlayerID", BindingFlags.Public | BindingFlags.Instance);

                        if (_adminCallbackType != null && _registerAdminCmdMethod != null)
                        {
                            // Helper: create a DynamicMethod shim for void(Player, string) -> void(object, string)
                            Delegate MakeShim(string name, string targetMethod)
                            {
                                var method = typeof(UnitBalance).GetMethod(targetMethod, BindingFlags.NonPublic | BindingFlags.Static);
                                var dm = new System.Reflection.Emit.DynamicMethod(
                                    name, typeof(void), new Type[] { playerType, typeof(string) },
                                    typeof(UnitBalance).Module, true);
                                var il = dm.GetILGenerator();
                                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                                il.Emit(System.Reflection.Emit.OpCodes.Call, method);
                                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                                return dm.CreateDelegate(_adminCallbackType);
                            }

                            object powerNone = Enum.ToObject(_adminPowerType, 0);
                            // Power.Cheat (0x80) — allows any admin with Cheat power (standard admins)
                            object powerCheat = Enum.ToObject(_adminPowerType, 0x80);

                            // Register !rebalance (Cheat power — standard admins can use)
                            _registerAdminCmdMethod.Invoke(null, new object[] {
                                "rebalance", MakeShim("RebalanceShim", "OnRebalanceCommand"),
                                powerCheat, "Hot-reload unit balance config" });
                            MelonLogger.Msg("Registered !rebalance admin command via AdminMod");

                            // Register !b (balance editor UI — Cheat power for standard admins)
                            _registerAdminCmdMethod.Invoke(null, new object[] {
                                "b", MakeShim("BalanceShim", "OnBalanceCommand"),
                                powerCheat, "Balance editor UI" });
                            MelonLogger.Msg("Registered !b admin command (balance editor)");

                            // Register shortcut commands: .1 through .9, .0, .back
                            // These let admins navigate the menu without typing "!b " prefix
                            var shortcutShim = MakeShim("MenuShortcutShim", "OnMenuShortcut");
                            string[] shortcuts = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "back" };
                            foreach (var sc in shortcuts)
                            {
                                try
                                {
                                    _registerAdminCmdMethod.Invoke(null, new object[] {
                                        sc, shortcutShim, powerCheat, null });
                                }
                                catch { }
                            }
                            MelonLogger.Msg("Registered .1-.9, .0, .back shortcut commands");
                        }
                        else
                            MelonLogger.Warning("Could not find CommandCallback or RegisterAdminCommand in AdminMod");
                    }
                    else
                        MelonLogger.Warning($"AdminMod API not found (helper={helperType != null}, player={playerType != null}).");
                }
                catch (Exception chatEx)
                {
                    MelonLogger.Warning($"Failed to register chat commands: {chatEx.Message}");
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
                _jumpSpeedMultipliers.Clear();
                _fowDistanceOverrides.Clear();
                _visibleEventRadiusMultipliers.Clear();
                _lifetimeMultipliers.Clear();
                _projectileOverrides.Clear();
                _techTierTimes.Clear();
                _teleportCooldown = -1f;
                _teleportDuration = -1f;
                _dispenseTimeout = -1f;

                var units = config["units"] as JObject;
                if (units != null)
                {
                    // Load teleport overrides from pseudo-unit "_teleport"
                    var tpObj = units["_teleport"] as JObject;
                    if (tpObj != null)
                    {
                        _teleportCooldown = tpObj["cooldown"]?.Value<float>() ?? -1f;
                        _teleportDuration = tpObj["duration"]?.Value<float>() ?? -1f;
                    }

                    foreach (var kvp in units)
                    {
                        string unitName = kvp.Key;
                        if (unitName.StartsWith("_")) continue; // skip pseudo-units
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
                        float jumpSpeedMult = overrides["jump_speed_mult"]?.Value<float>() ?? 1.0f;
                        float fowDistance = overrides["fow_distance"]?.Value<float>() ?? -1f;
                        float verMult = overrides["visible_event_radius_mult"]?.Value<float>() ?? 1.0f;
                        float lifetimeMult = overrides["proj_lifetime_mult"]?.Value<float>() ?? 1.0f;
                        float dispenseTimeout = overrides["dispense_timeout"]?.Value<float>() ?? -1f;

                        // Per-weapon multipliers (pri_/sec_ prefixed)
                        float priDamageMult = overrides["pri_damage_mult"]?.Value<float>() ?? 1.0f;
                        float secDamageMult = overrides["sec_damage_mult"]?.Value<float>() ?? 1.0f;
                        float priRangeMult = overrides["pri_range_mult"]?.Value<float>() ?? 1.0f;
                        float secRangeMult = overrides["sec_range_mult"]?.Value<float>() ?? 1.0f;
                        float priSpeedMult = overrides["pri_proj_speed_mult"]?.Value<float>() ?? 1.0f;
                        float secSpeedMult = overrides["sec_proj_speed_mult"]?.Value<float>() ?? 1.0f;
                        float priLifetimeMult = overrides["pri_proj_lifetime_mult"]?.Value<float>() ?? 1.0f;
                        float secLifetimeMult = overrides["sec_proj_lifetime_mult"]?.Value<float>() ?? 1.0f;
                        float priAccuracyMult = overrides["pri_accuracy_mult"]?.Value<float>() ?? 1.0f;
                        float secAccuracyMult = overrides["sec_accuracy_mult"]?.Value<float>() ?? 1.0f;
                        float priMagazineMult = overrides["pri_magazine_mult"]?.Value<float>() ?? 1.0f;
                        float secMagazineMult = overrides["sec_magazine_mult"]?.Value<float>() ?? 1.0f;
                        float priFireRateMult = overrides["pri_fire_rate_mult"]?.Value<float>() ?? 1.0f;
                        float secFireRateMult = overrides["sec_fire_rate_mult"]?.Value<float>() ?? 1.0f;
                        float priReloadTimeMult = overrides["pri_reload_time_mult"]?.Value<float>() ?? 1.0f;
                        float secReloadTimeMult = overrides["sec_reload_time_mult"]?.Value<float>() ?? 1.0f;

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
                        if (Math.Abs(jumpSpeedMult - 1.0f) > 0.001f)
                            _jumpSpeedMultipliers[unitName] = jumpSpeedMult;
                        if (fowDistance >= 0)
                            _fowDistanceOverrides[unitName] = fowDistance;
                        if (Math.Abs(verMult - 1.0f) > 0.001f)
                            _visibleEventRadiusMultipliers[unitName] = verMult;
                        if (Math.Abs(lifetimeMult - 1.0f) > 0.001f)
                            _lifetimeMultipliers[unitName] = lifetimeMult;
                        if (dispenseTimeout >= 0)
                            _dispenseTimeout = dispenseTimeout; // global — applies to all dispensers of this unit

                        // Store per-weapon multipliers with "pri:"/"sec:" prefix keys
                        if (Math.Abs(priDamageMult - 1.0f) > 0.001f) _damageMultipliers["pri:" + unitName] = priDamageMult;
                        if (Math.Abs(secDamageMult - 1.0f) > 0.001f) _damageMultipliers["sec:" + unitName] = secDamageMult;
                        if (Math.Abs(priRangeMult - 1.0f) > 0.001f) _rangeMultipliers["pri:" + unitName] = priRangeMult;
                        if (Math.Abs(secRangeMult - 1.0f) > 0.001f) _rangeMultipliers["sec:" + unitName] = secRangeMult;
                        if (Math.Abs(priSpeedMult - 1.0f) > 0.001f) _speedMultipliers["pri:" + unitName] = priSpeedMult;
                        if (Math.Abs(secSpeedMult - 1.0f) > 0.001f) _speedMultipliers["sec:" + unitName] = secSpeedMult;
                        if (Math.Abs(priLifetimeMult - 1.0f) > 0.001f) _lifetimeMultipliers["pri:" + unitName] = priLifetimeMult;
                        if (Math.Abs(secLifetimeMult - 1.0f) > 0.001f) _lifetimeMultipliers["sec:" + unitName] = secLifetimeMult;
                        if (Math.Abs(priAccuracyMult - 1.0f) > 0.001f) _accuracyMultipliers["pri:" + unitName] = priAccuracyMult;
                        if (Math.Abs(secAccuracyMult - 1.0f) > 0.001f) _accuracyMultipliers["sec:" + unitName] = secAccuracyMult;
                        if (Math.Abs(priMagazineMult - 1.0f) > 0.001f) _magazineMultipliers["pri:" + unitName] = priMagazineMult;
                        if (Math.Abs(secMagazineMult - 1.0f) > 0.001f) _magazineMultipliers["sec:" + unitName] = secMagazineMult;
                        if (Math.Abs(priFireRateMult - 1.0f) > 0.001f) _fireRateMultipliers["pri:" + unitName] = priFireRateMult;
                        if (Math.Abs(secFireRateMult - 1.0f) > 0.001f) _fireRateMultipliers["sec:" + unitName] = secFireRateMult;
                        if (Math.Abs(priReloadTimeMult - 1.0f) > 0.001f) _reloadTimeMultipliers["pri:" + unitName] = priReloadTimeMult;
                        if (Math.Abs(secReloadTimeMult - 1.0f) > 0.001f) _reloadTimeMultipliers["sec:" + unitName] = secReloadTimeMult;

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
                    ApplyFoWDistanceOverrides(omReady);
                    ApplyJumpSpeedOverrides(omReady);
                    ApplyVisibleEventRadiusOverrides(omReady);
                    ApplyMoveSpeedOverrides(omReady);
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
                    MelonLogger.Msg("[Rebalance] Reverted all overrides");
                }

                // 2. Clear direct-mutation caches
                _originalCreatureAttackAimDist.Clear();
                _originalSpread.Clear();
                _originalProjectileLifetimes.Clear();
                _originalProjectileSpeeds.Clear();
                _originalMoveSpeeds.Clear();
                _nameCache.Clear();

                if (defaultMode)
                {
                    // Sync the revert to all connected players and stop — vanilla is restored
                    if (_omInitialized)
                        MelonCoroutines.Start(SyncOverridesToAllPlayers(0.5f));
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
                    ApplyTurnRadiusOverrides(omReady);
                    ApplyTeleportOverrides(omReady);
                    ApplyDispenserTimeoutOverrides(omReady);

                    if (_shrimpDisableAim)
                        ApplyShrimpAimDisable();

                    // 4.5 Propagate overrides to already-spawned units in the scene
                    // (OM only modifies prefab data — live instances have independent component copies)
                    try { PropagateToLiveInstances(); }
                    catch (Exception pex) { MelonLogger.Warning($"[Rebalance] Live propagation error: {pex.Message}"); }

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

        // Process menu input from !b command
        private static void ProcessMenuInput(object player, string input)
        {
            long key = GetPlayerKey(player);
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
                    if (state.PendingTechTierKey != null)
                        ok = WriteTechTierToJson(state.PendingTechTierKey, state.PendingValue);
                    else
                        ok = WriteParamToJson(unitName, state.PendingParamKey, state.PendingValue);
                    state.PendingConfirm = false;
                    state.PendingUnitName = null;
                    state.PendingTechTierKey = null;

                    if (ok)
                    {
                        string playerName = GetPlayerName(player);
                        string steamId = GetPlayerSteamId(player);
                        string newValStr = state.PendingValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string displayLabel = state.PendingTechTierKey != null ? ("tech_time." + state.PendingParamKey) : (unitName + " " + state.PendingParamKey);
                        SendChatToPlayer(player, _chatPrefix + "<color=#55FF55>Saved:</color> " + displayLabel + " " + _valueColor + state.PendingOldVal + "</color> -> " + _valueColor + newValStr + "</color>");
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
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Cancelled.</color>");
                    return;
                }
                else
                {
                    state.PendingConfirm = false;
                    state.PendingUnitName = null;
                    state.PendingTechTierKey = null;
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
                else if (state.Level == MenuLevel.HTPTeleport)
                    state.Level = MenuLevel.HTPMenu;
                else
                    state.Level = (MenuLevel)((int)state.Level - 1);
                ShowCurrentMenu(player, state);
                return;
            }

            // Parse number
            if (!int.TryParse(arg, out int selection) || selection < 1)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Use .1-.9, .back, or .0 to exit</color>");
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

            // HTP Tier edit: ".1 45" sets tier_1 to 45 seconds
            if (state.Level == MenuLevel.HTPTier && !string.IsNullOrEmpty(arg2))
            {
                HandleHTPTierEdit(player, state, selection, arg2);
                return;
            }

            // HTP Teleport edit: ".1 90" sets cooldown, ".2 3" sets duration
            if (state.Level == MenuLevel.HTPTeleport && !string.IsNullOrEmpty(arg2))
            {
                HandleHTPTeleportEdit(player, state, selection, arg2);
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
                    if (selection < 1 || selection > 5) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-5.</color>"); return; }
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
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 1.5 (or !b 1 1.5)</color>");
                    return;

                case MenuLevel.JsonMenu:
                {
                    if (selection < 1 || selection > 3) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-3.</color>"); return; }
                    if (selection == 1)
                    {
                        // Blank reset — ask for confirmation
                        state.JsonPendingAction = "blank";
                        SendChatToPlayer(player, _chatPrefix + _headerColor + "Reset to blank?</color> All current settings will be lost!");
                        SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Confirm  " + _itemColor + ".2</color>=Cancel");
                        return;
                    }
                    else if (selection == 2)
                    {
                        // Save — ask for name
                        state.JsonPendingSave = true;
                        SendChatToPlayer(player, _chatPrefix + "Enter save name " + _dimColor + "(e.g. !b myconfig)</color> or " + _itemColor + ".1</color> for timestamp only");
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
                    SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Confirm  " + _itemColor + ".2</color>=Cancel");
                    return;
                }

                // ── HTP Menu ──────────────────────────────────────────
                case MenuLevel.HTPMenu:
                    if (selection < 1 || selection > 4) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-4.</color>"); return; }
                    if (selection == 1) state.Level = MenuLevel.HTPHoverbike;
                    else if (selection == 2) state.Level = MenuLevel.HTPTier;
                    else if (selection == 3) state.Level = MenuLevel.HTPTeleport;
                    else
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
                    break;

                case MenuLevel.HTPHoverbike:
                {
                    GetUnitParamGroups("Hover Bike", out string[] hbNames, out string[][] _hk);
                    if (selection < 1 || selection > hbNames.Length) { SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{hbNames.Length}.</color>"); return; }
                    state.HTPParamGroupIdx = selection - 1;
                    state.Level = MenuLevel.HTPHoverbikeParam;
                    break;
                }

                case MenuLevel.HTPHoverbikeParam:
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 1.5 (or !b 1 1.5)</color>");
                    return;

                case MenuLevel.HTPTier:
                {
                    // Tier 1-8 editable
                    if (selection < 1 || selection > 8) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-8.</color>"); return; }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: ." + selection + " <seconds> (or !b " + selection + " <seconds>)</color>");
                    return;
                }

                case MenuLevel.HTPTeleport:
                {
                    // 1=cooldown, 2=duration
                    if (selection < 1 || selection > 2) { SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-2.</color>"); return; }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: ." + selection + " <value> (or !b " + selection + " <value>)</color>");
                    return;
                }
            }

            ShowCurrentMenu(player, state);
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

                bool hasDamageMult = HasAnyWeaponMult(_damageMultipliers, name);
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
                                // Resolve per-weapon damage mult (pri_/sec_ override, then shared fallback)
                                string weapon = projFieldName.StartsWith("Primary") ? "pri" : "sec";
                                float effectiveDmgMult = GetWeaponMult(_damageMultipliers, name, weapon);
                                if (Math.Abs(effectiveDmgMult - 1f) > 0.001f)
                                {
                                    ScaleProjectileDamage(pdObj, pdName, effectiveDmgMult, modifiedPD, useOM, name);
                                    anyApplied = true;
                                }
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
                                        string weapon = attackFieldName == "AttackPrimary" ? "pri" : "sec";
                                        float effectiveDmgMult = GetWeaponMult(_damageMultipliers, name, weapon);
                                        if (Math.Abs(effectiveDmgMult - 1f) > 0.001f)
                                        {
                                            ScaleProjectileDamage(pdObj, pdName, effectiveDmgMult, modifiedPD, useOM, name);
                                            anyApplied = true;
                                        }
                                    }
                                }
                            }
                            else if (hasDamageMult)
                            {
                                // Melee attack (no projectile) — scale CreatureAttack.Damage directly
                                string weapon = attackFieldName == "AttackPrimary" ? "pri" : "sec";
                                float effectiveDmgMult = GetWeaponMult(_damageMultipliers, name, weapon);
                                if (Math.Abs(effectiveDmgMult - 1f) > 0.001f)
                                {
                                    float origDmg = GetFloatMember(attackObj, "Damage");
                                    if (origDmg > 0)
                                    {
                                        float newDmg = origDmg * effectiveDmgMult;
                                        SetFloatMember(attackObj, "Damage", newDmg);
                                        MelonLogger.Msg($"[DMG] {name} -> {attackFieldName}.Damage: {origDmg:F0} -> {newDmg:F0} (melee, direct)");
                                        anyApplied = true;
                                    }
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
                && _accuracyMultipliers.Count == 0 && _magazineMultipliers.Count == 0 && _fireRateMultipliers.Count == 0
                && _lifetimeMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            var allProjectiles = Resources.FindObjectsOfTypeAll<ProjectileData>();
            int applied = 0;
            var modifiedPD = new HashSet<string>();

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                bool hasRange = HasAnyWeaponMult(_rangeMultipliers, name);
                bool hasSpeed = HasAnyWeaponMult(_speedMultipliers, name);
                bool hasLifetime = HasAnyWeaponMult(_lifetimeMultipliers, name);
                bool hasReload = HasAnyWeaponMult(_reloadTimeMultipliers, name);
                bool hasAccuracy = HasAnyWeaponMult(_accuracyMultipliers, name);
                bool hasMagazine = HasAnyWeaponMult(_magazineMultipliers, name);
                bool hasFireRate = HasAnyWeaponMult(_fireRateMultipliers, name);
                if (!hasRange && !hasSpeed && !hasLifetime && !hasReload && !hasAccuracy && !hasMagazine && !hasFireRate) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                MelonLogger.Msg($"[RANGE] Applying weapon overrides to '{name}' (internal: {info.name}){(useOM ? " (OM)" : "")}");

                bool foundProjectileOnComponent = false;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    if (hasRange)
                    {
                        // NOTE: Sensor.TargetingDistance is NOT scaled by range_mult.
                        // Use the explicit "target_distance" config param to set it.

                        // VehicleTurret.AimDistance (shared field — use primary range mult)
                        if (typeName == "VehicleTurret")
                        {
                            float vtRangeMult = GetWeaponMult(_rangeMultipliers, name, "pri");
                            if (Math.Abs(vtRangeMult - 1f) > 0.001f)
                            {
                                var adField = comp.GetType().GetField("AimDistance",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (adField != null)
                                {
                                    float orig = (float)adField.GetValue(comp);
                                    float newVal = orig * vtRangeMult;
                                    if (useOM)
                                        OMSetFloat(oiTarget, "AimDistance", newVal);
                                    else
                                        adField.SetValue(comp, newVal);
                                    MelonLogger.Msg($"  VehicleTurret.AimDistance: {orig} -> {newVal}");
                                }
                            }
                        }

                        // UnitAimAt.AimDistanceMax (shared — use primary range mult)
                        if (typeName == "UnitAimAt")
                        {
                            float uaRangeMult = GetWeaponMult(_rangeMultipliers, name, "pri");
                            if (Math.Abs(uaRangeMult - 1f) > 0.001f)
                            {
                                var admField = comp.GetType().GetField("AimDistanceMax",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (admField != null)
                                {
                                    float orig = (float)admField.GetValue(comp);
                                    float newVal = orig * uaRangeMult;
                                    if (useOM)
                                        OMSetFloat(oiTarget, "AimDistanceMax", newVal);
                                    else
                                        admField.SetValue(comp, newVal);
                                    MelonLogger.Msg($"  UnitAimAt.AimDistanceMax: {orig} -> {newVal}");
                                }
                            }
                        }

                        // CreatureDecapod: per-weapon range/accuracy/speed (direct mutation)
                        if (typeName == "CreatureDecapod")
                        {
                            var compType = comp.GetType();
                            foreach (string attackFieldName in new[] { "AttackPrimary", "AttackSecondary" })
                            {
                                string weapon = attackFieldName == "AttackPrimary" ? "pri" : "sec";
                                float atkRangeMult = GetWeaponMult(_rangeMultipliers, name, weapon);
                                float atkAccMult = GetWeaponMult(_accuracyMultipliers, name, weapon);
                                float atkSpeedMult = GetWeaponMult(_speedMultipliers, name, weapon);

                                var attackField = compType.GetField(attackFieldName,
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (attackField == null) continue;
                                object attackObj;
                                try { attackObj = attackField.GetValue(comp); } catch { continue; }
                                if (attackObj == null) continue;

                                var attackType = attackObj.GetType();

                                // Scale AttackProjectileAimDistMax (server-authoritative AI field)
                                if (Math.Abs(atkRangeMult - 1f) > 0.001f)
                                {
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
                                        float newVal = orig * atkRangeMult;
                                        aimDistField.SetValue(attackObj, newVal);
                                        MelonLogger.Msg($"  CreatureAttack.{attackFieldName}.AimDistMax: {orig} -> {newVal} (direct)");
                                    }
                                }

                                // Scale AttackProjectileSpread (server-authoritative AI field)
                                if (Math.Abs(atkAccMult - 1f) > 0.001f)
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
                                        float newVal = orig * atkAccMult;
                                        spreadField.SetValue(attackObj, newVal);
                                        MelonLogger.Msg($"  CreatureAttack.{attackFieldName}.Spread: {orig} -> {newVal} (direct)");
                                    }
                                }

                                // Find ProjectileData referenced inside CreatureAttack
                                float atkLifetimeMult = GetWeaponMult(_lifetimeMultipliers, name, weapon);
                                bool hasAtkRange = Math.Abs(atkRangeMult - 1f) > 0.001f;
                                bool hasAtkSpeed = Math.Abs(atkSpeedMult - 1f) > 0.001f;
                                bool hasAtkLifetime = Math.Abs(atkLifetimeMult - 1f) > 0.001f;
                                if (hasAtkRange || hasAtkSpeed || hasAtkLifetime)
                                {
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
                                                    hasAtkRange ? atkRangeMult : 1f, hasAtkSpeed ? atkSpeedMult : 1f, modifiedPD, useOM,
                                                    hasAtkLifetime ? atkLifetimeMult : 1f);
                                                foundProjectileOnComponent = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // VehicleTurret weapon params via OverrideManager (per-weapon multiplier resolution)
                    if (typeName == "VehicleTurret")
                    {
                        var vtType = comp.GetType();

                        foreach (string prefix in new[] { "Primary", "Secondary" })
                        {
                            string weapon = prefix == "Primary" ? "pri" : "sec";

                            if (hasReload)
                            {
                                float effReload = GetWeaponMult(_reloadTimeMultipliers, name, weapon);
                                if (Math.Abs(effReload - 1f) > 0.001f)
                                {
                                    var rtField = vtType.GetField($"{prefix}ReloadTime",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (rtField != null)
                                    {
                                        float orig = (float)rtField.GetValue(comp);
                                        float newVal = Math.Max(0.1f, orig * effReload);
                                        if (useOM) OMSetFloat(oiTarget, $"{prefix}ReloadTime", newVal);
                                        else rtField.SetValue(comp, newVal);
                                        MelonLogger.Msg($"  VehicleTurret.{prefix}ReloadTime: {orig:F2} -> {newVal:F2}");
                                    }
                                }
                            }

                            if (hasFireRate)
                            {
                                float effFR = GetWeaponMult(_fireRateMultipliers, name, weapon);
                                if (Math.Abs(effFR - 1f) > 0.001f)
                                {
                                    var fiField = vtType.GetField($"{prefix}FireInterval",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (fiField != null && fiField.FieldType == typeof(float))
                                    {
                                        float orig = (float)fiField.GetValue(comp);
                                        float newVal = Math.Max(0.01f, orig / effFR);
                                        if (useOM) OMSetFloat(oiTarget, $"{prefix}FireInterval", newVal);
                                        else fiField.SetValue(comp, newVal);
                                        MelonLogger.Msg($"  VehicleTurret.{prefix}FireInterval: {orig:F4} -> {newVal:F4}");
                                    }
                                }
                            }

                            if (hasMagazine)
                            {
                                float effMag = GetWeaponMult(_magazineMultipliers, name, weapon);
                                if (Math.Abs(effMag - 1f) > 0.001f)
                                {
                                    var msField = vtType.GetField($"{prefix}MagazineSize",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (msField != null && msField.FieldType == typeof(int))
                                    {
                                        int orig = (int)msField.GetValue(comp);
                                        int newVal = Math.Max(1, (int)Math.Round(orig * effMag));
                                        if (useOM) OMSetInt(oiTarget, $"{prefix}MagazineSize", newVal);
                                        else msField.SetValue(comp, newVal);
                                        MelonLogger.Msg($"  VehicleTurret.{prefix}MagazineSize: {orig} -> {newVal}");
                                    }
                                }
                            }

                            if (hasAccuracy)
                            {
                                float effAcc = GetWeaponMult(_accuracyMultipliers, name, weapon);
                                if (Math.Abs(effAcc - 1f) > 0.001f)
                                {
                                    var spField = vtType.GetField($"{prefix}MuzzleSpread",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (spField != null && spField.FieldType == typeof(float))
                                    {
                                        float orig = (float)spField.GetValue(comp);
                                        float newVal = orig * effAcc;
                                        if (useOM) OMSetFloat(oiTarget, $"{prefix}MuzzleSpread", newVal);
                                        else spField.SetValue(comp, newVal);
                                        MelonLogger.Msg($"  VehicleTurret.{prefix}MuzzleSpread: {orig:F4} -> {newVal:F4}");
                                    }
                                }
                            }
                        }
                    }

                    // Find ProjectileData references on components (per-weapon resolution by field name)
                    if (hasRange || hasSpeed || hasLifetime)
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

                            // Determine weapon slot from field name (Secondary* → sec, else pri)
                            string weapon = field.Name.StartsWith("Secondary") ? "sec" : "pri";
                            float effRange = GetWeaponMult(_rangeMultipliers, name, weapon);
                            float effSpeed = GetWeaponMult(_speedMultipliers, name, weapon);
                            float effLifetime = GetWeaponMult(_lifetimeMultipliers, name, weapon);
                            bool effHasRange = Math.Abs(effRange - 1f) > 0.001f;
                            bool effHasSpeed = Math.Abs(effSpeed - 1f) > 0.001f;
                            bool effHasLifetime = Math.Abs(effLifetime - 1f) > 0.001f;
                            if (!effHasRange && !effHasSpeed && !effHasLifetime) continue;

                            ScaleProjectileData(pdObj, pdName, effHasRange ? effRange : 1f, effHasSpeed ? effSpeed : 1f, modifiedPD, useOM,
                                effHasLifetime ? effLifetime : 1f);
                            foundProjectileOnComponent = true;
                        }
                    }
                }

                // Fallback: search all ProjectileData assets by name pattern (uses primary weapon mult)
                if (!foundProjectileOnComponent && (hasRange || hasSpeed || hasLifetime))
                {
                    float fbRange = GetWeaponMult(_rangeMultipliers, name, "pri");
                    float fbSpeed = GetWeaponMult(_speedMultipliers, name, "pri");
                    float fbLifetime = GetWeaponMult(_lifetimeMultipliers, name, "pri");
                    bool fbHasRange = Math.Abs(fbRange - 1f) > 0.001f;
                    bool fbHasSpeed = Math.Abs(fbSpeed - 1f) > 0.001f;
                    bool fbHasLifetime = Math.Abs(fbLifetime - 1f) > 0.001f;
                    MelonLogger.Msg($"  No ProjectileData found on components, searching by name...");
                    int fallbackCount = 0;
                    if (fbHasRange || fbHasSpeed || fbHasLifetime)
                    {
                        foreach (var pd in allProjectiles)
                        {
                            if (pd == null || modifiedPD.Contains(pd.name)) continue;
                            if (pd.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            ScaleProjectileData(pd, pd.name, fbHasRange ? fbRange : 1f, fbHasSpeed ? fbSpeed : 1f, modifiedPD, useOM,
                                fbHasLifetime ? fbLifetime : 1f);
                            fallbackCount++;
                        }
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

        // =============================================
        // FoW Distance: absolute override for Sensor.FogOfWarViewDistance
        // =============================================
        private static void ApplyFoWDistanceOverrides(bool useOM)
        {
            if (_fowDistanceOverrides.Count == 0) return;
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_fowDistanceOverrides.TryGetValue(name, out float fowDist)) continue;
                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null || comp.GetType().Name != "Sensor") continue;
                    var fowField = comp.GetType().GetField("FogOfWarViewDistance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fowField == null || fowField.FieldType != typeof(float)) continue;
                    float orig = (float)fowField.GetValue(comp);
                    if (useOM && OMSetFloat(oiTarget, "FogOfWarViewDistance", fowDist))
                    {
                        MelonLogger.Msg($"[FOW] {name}: FogOfWarViewDistance {orig} -> {fowDist} (OM)");
                        applied++;
                    }
                    else if (!useOM)
                    {
                        fowField.SetValue(comp, fowDist);
                        MelonLogger.Msg($"[FOW] {name}: FogOfWarViewDistance {orig} -> {fowDist} (direct)");
                        applied++;
                    }
                    break;
                }
            }
            if (applied > 0)
                MelonLogger.Msg($"[FOW] Applied FoW distance overrides to {applied} units");
        }

        // =============================================
        // Jump Speed: scale Soldier.JumpSpeed via OverrideManager
        // =============================================
        private static void ApplyJumpSpeedOverrides(bool useOM)
        {
            if (_jumpSpeedMultipliers.Count == 0) return;
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_jumpSpeedMultipliers.TryGetValue(name, out float mult)) continue;
                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    if (typeName != "Soldier" && typeName != "PlayerMovement" && typeName != "FPSMovement") continue;
                    var jsField = comp.GetType().GetField("JumpSpeed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (jsField == null || jsField.FieldType != typeof(float)) continue;
                    float orig = (float)jsField.GetValue(comp);
                    float newVal = orig * mult;
                    if (useOM && OMSetFloat(oiTarget, "JumpSpeed", newVal))
                    {
                        MelonLogger.Msg($"[JUMP] {name}: JumpSpeed {orig:F1} -> {newVal:F1} x{mult:F2} (OM)");
                        applied++;
                    }
                    else if (!useOM)
                    {
                        jsField.SetValue(comp, newVal);
                        MelonLogger.Msg($"[JUMP] {name}: JumpSpeed {orig:F1} -> {newVal:F1} x{mult:F2} (direct)");
                        applied++;
                    }
                }
            }
            if (applied > 0)
                MelonLogger.Msg($"[JUMP] Applied jump speed overrides to {applied} units");
        }

        // =============================================
        // Visible Event Radius: scale ProjectileData.VisibleEventRadius
        // =============================================
        private static void ApplyVisibleEventRadiusOverrides(bool useOM)
        {
            if (_visibleEventRadiusMultipliers.Count == 0) return;
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_visibleEventRadiusMultipliers.TryGetValue(name, out float mult)) continue;
                // Find all ProjectileData referenced by this unit's turrets/weapons
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    if (typeName != "VehicleTurret") continue;
                    // Check PrimaryProjectile and SecondaryProjectile fields
                    foreach (string pdFieldName in new[] { "PrimaryProjectile", "SecondaryProjectile" })
                    {
                        var pdField = comp.GetType().GetField(pdFieldName,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (pdField == null) continue;
                        var pdObj = pdField.GetValue(comp);
                        if (pdObj == null) continue;
                        float origVER = GetFloatMember(pdObj, "VisibleEventRadius");
                        if (origVER <= 0) continue;
                        float newVER = origVER * mult;
                        string pdName = (pdObj as UnityEngine.Object)?.name ?? pdObj.ToString();
                        string pdTarget = useOM ? $"A:{pdName}.asset" : null;
                        if (useOM) OMSetFloat(pdTarget, "VisibleEventRadius", newVER);
                        else SetFloatMember(pdObj, "VisibleEventRadius", newVER);
                        MelonLogger.Msg($"[VER] {name}: {pdName} VisibleEventRadius {origVER:F0} -> {newVER:F0} x{mult:F2}");
                        applied++;
                    }
                }
            }
            if (applied > 0)
                MelonLogger.Msg($"[VER] Applied VisibleEventRadius overrides to {applied} projectiles");
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

        private static void ApplyTeleportOverrides(bool useOM)
        {
            if (_teleportCooldown < 0 && _teleportDuration < 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            var flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    if (comp.GetType().Name != "TeleportUI") continue;

                    string name = info.DisplayName;
                    string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                    if (_teleportCooldown >= 0)
                    {
                        var f = comp.GetType().GetField("TeleportCooldownTime", flags);
                        if (f != null && f.FieldType == typeof(float))
                        {
                            float orig = (float)f.GetValue(comp);
                            if (useOM && OMSetFloat(oiTarget, "TeleportCooldownTime", _teleportCooldown))
                            {
                                MelonLogger.Msg($"[TELEPORT] {name}: CooldownTime {orig:F1} -> {_teleportCooldown:F1} (OM)");
                                applied++;
                            }
                            else
                            {
                                f.SetValue(comp, _teleportCooldown);
                                MelonLogger.Msg($"[TELEPORT] {name}: CooldownTime {orig:F1} -> {_teleportCooldown:F1} (direct)");
                                applied++;
                            }
                        }
                    }
                    if (_teleportDuration >= 0)
                    {
                        var f = comp.GetType().GetField("TeleportTime", flags);
                        if (f != null && f.FieldType == typeof(float))
                        {
                            float orig = (float)f.GetValue(comp);
                            if (useOM && OMSetFloat(oiTarget, "TeleportTime", _teleportDuration))
                            {
                                MelonLogger.Msg($"[TELEPORT] {name}: TeleportTime {orig:F1} -> {_teleportDuration:F1} (OM)");
                                applied++;
                            }
                            else
                            {
                                f.SetValue(comp, _teleportDuration);
                                MelonLogger.Msg($"[TELEPORT] {name}: TeleportTime {orig:F1} -> {_teleportDuration:F1} (direct)");
                                applied++;
                            }
                        }
                    }
                    break;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[TELEPORT] Applied teleport overrides ({applied} fields)");
        }

        private static void ApplyDispenserTimeoutOverrides(bool useOM)
        {
            if (_dispenseTimeout < 0) return;

            int applied = 0;
            var flags = BindingFlags.Public | BindingFlags.Instance;

            // Find all VehicleDispenser components in all ObjectInfo prefabs
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    if (comp.GetType().Name != "VehicleDispenser") continue;

                    string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                    var f = comp.GetType().GetField("DispenseTimeout", flags);
                    if (f != null && f.FieldType == typeof(float))
                    {
                        float orig = (float)f.GetValue(comp);
                        if (useOM && OMSetFloat(oiTarget, "DispenseTimeout", _dispenseTimeout))
                        {
                            MelonLogger.Msg($"[DISPENSER] {info.DisplayName}: DispenseTimeout {orig:F1} -> {_dispenseTimeout:F1} (OM)");
                            applied++;
                        }
                        else
                        {
                            f.SetValue(comp, _dispenseTimeout);
                            MelonLogger.Msg($"[DISPENSER] {info.DisplayName}: DispenseTimeout {orig:F1} -> {_dispenseTimeout:F1} (direct)");
                            applied++;
                        }
                    }
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[DISPENSER] Applied dispense timeout overrides ({applied} dispensers)");
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

        private static void ScaleProjectileData(object pdObj, string pdName, float rangeMult, float speedMult,
            HashSet<string> modifiedPD, bool useOM = false, float lifetimeMult = 1f)
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
                float newLt = origLt * rangeMult * lifetimeMult;
                float newSpeed = origSpeed * speedMult;
                float origRange = origSpeed * origLt;
                float newRange = newSpeed * newLt;

                bool ltChanged = Math.Abs(rangeMult * lifetimeMult - 1f) > 0.001f;
                if (ltChanged && origLt > 0)
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

            // For instant-hit: lifetime mult also applies (visual beam duration)
            if (isInstantHit && Math.Abs(lifetimeMult - 1f) > 0.001f && origLt > 0)
            {
                float newLt = origLt * lifetimeMult;
                if (useOM) OMSetFloat(pdTarget, "m_fLifeTime", newLt);
                else SetFloatMember(pdObj, "m_fLifeTime", newLt);
                MelonLogger.Msg($"  ProjectileData '{pdName}' (instant-hit): Lifetime {origLt} -> {newLt:F2}");
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
        // Propagate overrides to live scene instances
        // =============================================
        // OM modifies prefab/asset data only — live instances have independent
        // MonoBehaviour component copies from spawn time. After all Apply* methods
        // run, this method finds every spawned unit in the scene and applies the
        // same overrides via direct field mutation.

        private static void PropagateToLiveInstances()
        {
            int liveCount = 0, totalUpdated = 0;
            var processedGOs = new HashSet<int>();

            // Collect live scene objects via Unit and Structure MonoBehaviour components
            // (ObjectInfo is a ScriptableObject — can't enumerate scene instances from it)
            var liveUnits = UnityEngine.Object.FindObjectsOfType<Unit>();
            var liveStructures = UnityEngine.Object.FindObjectsOfType<Structure>();

            // Process units
            foreach (var unit in liveUnits)
            {
                if (unit == null || unit.ObjectInfo == null || unit.ObjectInfo.Prefab == null) continue;
                PropagateToInstance(unit.gameObject, unit.ObjectInfo, processedGOs, ref liveCount, ref totalUpdated);
            }

            // Process structures
            foreach (var structure in liveStructures)
            {
                if (structure == null || structure.ObjectInfo == null || structure.ObjectInfo.Prefab == null) continue;
                PropagateToInstance(structure.gameObject, structure.ObjectInfo, processedGOs, ref liveCount, ref totalUpdated);
            }

            if (liveCount > 0 || totalUpdated > 0)
                MelonLogger.Msg($"[LIVE] Propagated overrides to {totalUpdated}/{liveCount} live instances");
        }

        private static void PropagateToInstance(GameObject liveGO, ObjectInfo info, HashSet<int> processedGOs,
            ref int liveCount, ref int totalUpdated)
        {
            int goId = liveGO.GetInstanceID();
            if (processedGOs.Contains(goId)) return;
            processedGOs.Add(goId);
            liveCount++;

            string name = info.DisplayName;
            if (string.IsNullOrEmpty(name)) return;

            // Gather all overrides for this unit name (weapon params use HasAnyWeaponMult for per-weapon keys)
            bool hasRange = HasAnyWeaponMult(_rangeMultipliers, name);
            bool hasReload = HasAnyWeaponMult(_reloadTimeMultipliers, name);
            bool hasAccuracy = HasAnyWeaponMult(_accuracyMultipliers, name);
            bool hasMagazine = HasAnyWeaponMult(_magazineMultipliers, name);
            bool hasFireRate = HasAnyWeaponMult(_fireRateMultipliers, name);
            bool hasMoveSpeed = _moveSpeedMultipliers.TryGetValue(name, out float moveMult);
            bool hasTurboSpeed = _turboSpeedMultipliers.TryGetValue(name, out float turboMult);
            bool hasTurnRadius = _turnRadiusMultipliers.TryGetValue(name, out float turnMult);
            bool hasTargetDist = _targetDistanceOverrides.TryGetValue(name, out float targetDist);
            bool hasFoW = _fowDistanceOverrides.TryGetValue(name, out float fowDist);
            bool hasJump = _jumpSpeedMultipliers.TryGetValue(name, out float jumpMult);

            if (!hasRange && !hasReload && !hasAccuracy && !hasMagazine && !hasFireRate
                && !hasMoveSpeed && !hasTurboSpeed && !hasTurnRadius
                && !hasTargetDist && !hasFoW && !hasJump) return;

            // Build prefab component lookup by type (for reading vanilla values)
            var prefabComps = info.Prefab.GetComponentsInChildren<Component>(true);
            var prefabByType = new Dictionary<string, Component>();
            foreach (var pc in prefabComps)
            {
                if (pc == null) continue;
                string tn = pc.GetType().Name;
                if (!prefabByType.ContainsKey(tn))
                    prefabByType[tn] = pc;
            }

            // Iterate live instance components and apply overrides
            var liveComps = liveGO.GetComponentsInChildren<Component>(true);
            int fieldsSet = 0;
            bool logDetail = (fieldsSet == 0); // log first few units for debugging

            foreach (var liveComp in liveComps)
            {
                if (liveComp == null) continue;
                string typeName = liveComp.GetType().Name;
                var compType = liveComp.GetType();
                prefabByType.TryGetValue(typeName, out Component prefabComp);

                // --- VehicleTurret: AimDistance, per-weapon reload/fire rate/magazine/accuracy ---
                if (typeName == "VehicleTurret" && prefabComp != null)
                {
                    if (hasRange)
                    {
                        float vtRange = GetWeaponMult(_rangeMultipliers, name, "pri");
                        if (Math.Abs(vtRange - 1f) > 0.001f)
                            fieldsSet += LiveScaleFieldLog(prefabComp, liveComp, "AimDistance", vtRange, name);
                    }

                    foreach (string prefix in new[] { "Primary", "Secondary" })
                    {
                        string weapon = prefix == "Primary" ? "pri" : "sec";
                        if (hasReload)
                        {
                            float effReload = GetWeaponMult(_reloadTimeMultipliers, name, weapon);
                            if (Math.Abs(effReload - 1f) > 0.001f)
                                fieldsSet += LiveScaleField(prefabComp, liveComp, $"{prefix}ReloadTime", effReload, 0.1f);
                        }
                        if (hasFireRate)
                        {
                            float effFR = GetWeaponMult(_fireRateMultipliers, name, weapon);
                            if (Math.Abs(effFR - 1f) > 0.001f)
                                fieldsSet += LiveScaleField(prefabComp, liveComp, $"{prefix}FireInterval", 1f / effFR, 0.01f);
                        }
                        if (hasMagazine)
                        {
                            float effMag = GetWeaponMult(_magazineMultipliers, name, weapon);
                            if (Math.Abs(effMag - 1f) > 0.001f)
                                fieldsSet += LiveScaleIntField(prefabComp, liveComp, $"{prefix}MagazineSize", effMag);
                        }
                        if (hasAccuracy)
                        {
                            float effAcc = GetWeaponMult(_accuracyMultipliers, name, weapon);
                            if (Math.Abs(effAcc - 1f) > 0.001f)
                                fieldsSet += LiveScaleField(prefabComp, liveComp, $"{prefix}MuzzleSpread", effAcc);
                        }
                    }
                }

                // --- UnitAimAt: AimDistanceMax (shared — uses primary range mult) ---
                if (typeName == "UnitAimAt" && prefabComp != null && hasRange)
                {
                    float uaRange = GetWeaponMult(_rangeMultipliers, name, "pri");
                    if (Math.Abs(uaRange - 1f) > 0.001f)
                        fieldsSet += LiveScaleFieldLog(prefabComp, liveComp, "AimDistanceMax", uaRange, name);
                }

                // --- Sensor: TargetingDistance, FoW ---
                if (typeName == "Sensor")
                {
                    if (hasTargetDist)
                        fieldsSet += LiveSetAbsolute(liveComp, "TargetingDistance", targetDist);
                    if (hasFoW)
                        fieldsSet += LiveSetAbsolute(liveComp, "FogOfWarViewDistance", fowDist);
                }

                // --- Soldier / movement: JumpSpeed + speed fields ---
                if (typeName == "Soldier" || typeName == "PlayerMovement" || typeName == "FPSMovement")
                {
                    if (hasJump && prefabComp != null)
                        fieldsSet += LiveScaleFieldLog(prefabComp, liveComp, "JumpSpeed", jumpMult, name);
                }

                // --- VehicleWheeled: TurningCircleRadius ---
                if (typeName == "VehicleWheeled" && prefabComp != null && hasTurnRadius)
                    fieldsSet += LiveScaleFieldLog(prefabComp, liveComp, "TurningCircleRadius", turnMult, name);

                // --- CreatureDecapod: propagate per-weapon attack fields from prefab ---
                if (typeName == "CreatureDecapod" && prefabComp != null)
                {
                    foreach (string atkName in new[] { "AttackPrimary", "AttackSecondary" })
                    {
                        string weapon = atkName == "AttackPrimary" ? "pri" : "sec";
                        var atkField = compType.GetField(atkName, BindingFlags.Public | BindingFlags.Instance);
                        if (atkField == null) continue;
                        object liveAtk = null, prefabAtk = null;
                        try { liveAtk = atkField.GetValue(liveComp); } catch { }
                        try { prefabAtk = atkField.GetValue(prefabComp); } catch { }
                        if (liveAtk == null || prefabAtk == null) continue;

                        var atkType = liveAtk.GetType();

                        // Copy AimDistMax from prefab (already overridden by ApplyRangeOverrides)
                        float atkRange = GetWeaponMult(_rangeMultipliers, name, weapon);
                        if (Math.Abs(atkRange - 1f) > 0.001f)
                        {
                            var f = atkType.GetField("AttackProjectileAimDistMax", BindingFlags.Public | BindingFlags.Instance);
                            if (f != null)
                            {
                                f.SetValue(liveAtk, f.GetValue(prefabAtk));
                                fieldsSet++;
                            }
                        }
                        // Copy Spread from prefab
                        float atkAcc = GetWeaponMult(_accuracyMultipliers, name, weapon);
                        if (Math.Abs(atkAcc - 1f) > 0.001f)
                        {
                            var f = atkType.GetField("AttackProjectileSpread", BindingFlags.Public | BindingFlags.Instance);
                            if (f != null)
                            {
                                f.SetValue(liveAtk, f.GetValue(prefabAtk));
                                fieldsSet++;
                            }
                        }
                    }
                }

                // --- Any component with speed fields (VehicleHovered, VehicleAir, CreatureDecapod, etc.) ---
                if ((hasMoveSpeed || hasTurboSpeed) && prefabComp != null)
                {
                    foreach (string fn in _speedFieldNames)
                    {
                        float fieldMult;
                        if (fn == "TurboSpeed")
                        {
                            if (hasTurboSpeed) fieldMult = turboMult;
                            else if (hasMoveSpeed) fieldMult = moveMult;
                            else continue;
                        }
                        else
                        {
                            if (!hasMoveSpeed) continue;
                            fieldMult = moveMult;
                        }
                        fieldsSet += LiveScaleFieldDeclaredOnlyLog(prefabComp, liveComp, fn, fieldMult, name);
                    }
                }
            }

            if (fieldsSet > 0)
                totalUpdated++;
        }

        // Read vanilla float from prefab component, multiply, write to live component
        private static int LiveScaleField(Component prefab, Component live, string fieldName, float multiplier, float minVal = 0f)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(float)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                float vanilla = (float)pfField.GetValue(prefab);
                float newVal = vanilla * multiplier;
                if (minVal > 0 && newVal < minVal) newVal = minVal;
                liveField.SetValue(live, newVal);
                return 1;
            }
            catch { return 0; }
        }

        // Same but DeclaredOnly (for speed fields that need type-specific binding)
        private static int LiveScaleFieldDeclaredOnly(Component prefab, Component live, string fieldName, float multiplier)
        {
            try
            {
                var flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(float)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                float vanilla = (float)pfField.GetValue(prefab);
                if (vanilla <= 0) return 0;
                liveField.SetValue(live, vanilla * multiplier);
                return 1;
            }
            catch { return 0; }
        }

        // Read vanilla int from prefab, multiply, write to live
        private static int LiveScaleIntField(Component prefab, Component live, string fieldName, float multiplier)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(int)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                int vanilla = (int)pfField.GetValue(prefab);
                int newVal = Math.Max(1, (int)Math.Round(vanilla * multiplier));
                liveField.SetValue(live, newVal);
                return 1;
            }
            catch { return 0; }
        }

        // Logging version: read vanilla, multiply, write, and log the before/after
        private static int LiveScaleFieldLog(Component prefab, Component live, string fieldName, float multiplier, string unitName)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(float)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                float vanilla = (float)pfField.GetValue(prefab);
                float oldLive = (float)liveField.GetValue(live);
                float newVal = vanilla * multiplier;
                liveField.SetValue(live, newVal);
                float verify = (float)liveField.GetValue(live);
                MelonLogger.Msg($"[LIVE-DBG] {unitName}: {fieldName} prefab={vanilla:F2} old={oldLive:F2} -> new={newVal:F2} verify={verify:F2}");
                return 1;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[LIVE-DBG] {unitName}: {fieldName} error: {ex.Message}");
                return 0;
            }
        }

        // Logging version for DeclaredOnly speed fields
        private static int LiveScaleFieldDeclaredOnlyLog(Component prefab, Component live, string fieldName, float multiplier, string unitName)
        {
            try
            {
                var flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(float)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                float vanilla = (float)pfField.GetValue(prefab);
                if (vanilla <= 0) return 0;
                float oldLive = (float)liveField.GetValue(live);
                float newVal = vanilla * multiplier;
                liveField.SetValue(live, newVal);
                float verify = (float)liveField.GetValue(live);
                MelonLogger.Msg($"[LIVE-DBG] {unitName}: {fieldName} prefab={vanilla:F2} old={oldLive:F2} -> new={newVal:F2} verify={verify:F2}");
                return 1;
            }
            catch { return 0; }
        }

        // Set absolute float value on live component
        private static int LiveSetAbsolute(Component live, string fieldName, float value)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var field = live.GetType().GetField(fieldName, flags);
                if (field == null || field.FieldType != typeof(float)) return 0;
                field.SetValue(live, value);
                return 1;
            }
            catch { return 0; }
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

        // =============================================
        // Balance Editor UI — chat-based menu system (!b command)
        // =============================================

        private static readonly string[][] _factionNames = {
            new[] { "Sol" },
            new[] { "Centauri" },
            new[] { "Alien" },
        };

        // Faction -> Category names (matching in-game production buildings)
        private static readonly string[][] _categoryNames = {
            // Sol
            new[] { "Barracks", "Light Factory", "Heavy Factory", "Ultra Heavy Factory", "Air Factory", "Structures" },
            // Centauri
            new[] { "Barracks", "Light Factory", "Heavy Factory", "Ultra Heavy Factory", "Air Factory", "Structures" },
            // Alien
            new[] { "Lesser Spawning Cyst", "Greater Spawning Cyst", "Grand Spawning Cyst", "Colossal Spawning Cyst", "Nest", "Structures" },
        };

        // Faction -> Category -> Unit names (matching in-game unit tree)
        private static readonly string[][][] _unitNames = {
            // Sol
            new[] {
                new[] { "Scout", "Rifleman", "Sniper", "Heavy", "Commando" },
                new[] { "Light Quad", "Platoon Hauler", "Heavy Quad", "Light Striker", "Heavy Striker", "AA Truck" },
                new[] { "Hover Tank", "Barrage Truck", "Railgun Tank", "Pulse Truck" },
                new[] { "Harvester", "Siege Tank" },
                new[] { "Gunship", "Dropship", "Fighter", "Bomber" },
                new[] { "Headquarters", "Refinery", "Barracks", "Air Factory", "Heavy Factory", "Ultra Heavy Factory", "Turret", "Heavy Turret", "Anti-Air Rocket Turret" },
            },
            // Centauri
            new[] {
                new[] { "Militia", "Trooper", "Marksman", "Juggernaut", "Templar" },
                new[] { "Light Raider", "Squad Transport", "Heavy Raider", "Assault Car", "Strike Tank", "Flak Car" },
                new[] { "Combat Tank", "Rocket Tank", "Heavy Tank", "Pyro Tank" },
                new[] { "Harvester", "Crimson Tank" },
                new[] { "Shuttle", "Dreadnought", "Interceptor", "Freighter" },
                new[] { "Headquarters", "Refinery", "Barracks", "Air Factory", "Heavy Factory", "Ultra Heavy Factory", "Turret", "Heavy Turret", "Anti-Air Rocket Turret" },
            },
            // Alien
            new[] {
                new[] { "Crab", "Shrimp", "Shocker", "Wasp", "Dragonfly", "Squid" },
                new[] { "Horned Crab", "Hunter", "Behemoth", "Scorpion", "Firebug" },
                new[] { "Goliath" },
                new[] { "Defiler", "Colossus" },
                new[] { "Queen" },
                new[] { "Nest", "Node", "Bio Cache", "Lesser Spawning Cyst", "Greater Spawning Cyst", "Grand Spawning Cyst", "Colossal Spawning Cyst", "Quantum Cortex", "Hive Spire", "Thorn Spire" },
            },
        };

        // Parameter group names and their config keys
        private static readonly string[] _paramGroupNames = {
            "Health & Production", "Damage & Weapons", "Movement", "Vision & Sense"
        };

        private static readonly string[][] _paramGroupKeys = {
            // Health & Production
            new[] { "health_mult", "cost_mult", "build_time_mult", "min_tier", "build_radius" },
            // Damage & Weapons (legacy fallback — dynamic groups use pri_/sec_ keys)
            new[] { "damage_mult", "range_mult", "proj_speed_mult", "accuracy_mult", "magazine_mult", "fire_rate_mult", "reload_time_mult" },
            // Movement
            new[] { "move_speed_mult", "jump_speed_mult", "turbo_speed_mult", "turn_radius_mult" },
            // Vision & Sense
            new[] { "target_distance", "fow_distance", "visible_event_radius_mult" },
        };

        // Per-weapon param keys (without pri_/sec_ prefix)
        private static readonly string[] _weaponParamKeys = {
            "damage_mult", "proj_speed_mult", "proj_lifetime_mult", "accuracy_mult", "magazine_mult", "fire_rate_mult", "reload_time_mult"
        };

        // Per-weapon multiplier resolution: checks "pri:"/"sec:" prefixed key, falls back to shared
        private static float GetWeaponMult(Dictionary<string, float> dict, string unitName, string weapon)
        {
            if (dict.TryGetValue(weapon + ":" + unitName, out float specific))
                return specific;
            if (dict.TryGetValue(unitName, out float shared))
                return shared;
            return 1f;
        }

        private static bool HasAnyWeaponMult(Dictionary<string, float> dict, string unitName)
        {
            return dict.ContainsKey(unitName) || dict.ContainsKey("pri:" + unitName) || dict.ContainsKey("sec:" + unitName);
        }

        private static string CleanWeaponName(string pdName)
        {
            if (string.IsNullOrEmpty(pdName)) return "";
            if (pdName.StartsWith("ProjectileData_")) return pdName.Substring(15);
            if (pdName.StartsWith("Projectile_")) return pdName.Substring(11);
            return pdName;
        }

        private static void GetUnitWeaponInfo(string unitName, out bool hasPrimary, out bool hasSecondary,
            out string priName, out string secName)
        {
            hasPrimary = false;
            hasSecondary = false;
            priName = "";
            secName = "";
            try
            {
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                ObjectInfo matchedInfo = null;
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    if (info.DisplayName == unitName) { matchedInfo = info; break; }
                }
                if (matchedInfo == null) return;

                var childComps = matchedInfo.Prefab.GetComponentsInChildren<Component>(true);
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // Collect all unique ProjectileData refs across ALL VehicleTurret components.
                // Units may have multiple turrets (e.g., main gun turret + coax MG turret),
                // where each turret has its own PrimaryProjectileData.
                var foundWeapons = new List<string>(); // ProjectileData names in discovery order
                bool isVehicle = false;

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    if (typeName == "VehicleTurret")
                    {
                        isVehicle = true;
                        var vtType = comp.GetType();
                        // Check both Primary and Secondary on each VehicleTurret
                        foreach (string projPrefix in new[] { "Primary", "Secondary" })
                        {
                            foreach (string suffix in new[] { "Projectile", "ProjectileData" })
                            {
                                var f = vtType.GetField(projPrefix + suffix, flags);
                                if (f == null) continue;
                                var pd = f.GetValue(comp);
                                if (pd == null) continue;
                                string pdName = "";
                                try { pdName = ((UnityEngine.Object)pd).name; } catch { }
                                if (!string.IsNullOrEmpty(pdName) && !foundWeapons.Contains(pdName))
                                    foundWeapons.Add(pdName);
                                break; // found for this prefix, move to next
                            }
                        }
                    }
                }

                if (isVehicle)
                {
                    if (foundWeapons.Count >= 1)
                    {
                        hasPrimary = true;
                        priName = CleanWeaponName(foundWeapons[0]);
                    }
                    if (foundWeapons.Count >= 2)
                    {
                        hasSecondary = true;
                        secName = CleanWeaponName(foundWeapons[1]);
                    }
                    return;
                }

                // Creature detection (CreatureDecapod with AttackPrimary/AttackSecondary)
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    if (comp.GetType().Name != "CreatureDecapod") continue;

                    var compType = comp.GetType();
                    var priAtkField = compType.GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                    if (priAtkField != null)
                    {
                        var atk = priAtkField.GetValue(comp);
                        if (atk != null)
                        {
                            float dmg = GetFloatMember(atk, "Damage");
                            var pdField = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                            object pd = null;
                            if (pdField != null) try { pd = pdField.GetValue(atk); } catch { }
                            if (dmg > 0 || pd != null)
                            {
                                hasPrimary = true;
                                if (pd != null) try { priName = CleanWeaponName(((UnityEngine.Object)pd).name); } catch { }
                                else priName = "Melee";
                            }
                        }
                    }
                    var secAtkField = compType.GetField("AttackSecondary", BindingFlags.Public | BindingFlags.Instance);
                    if (secAtkField != null)
                    {
                        var atk = secAtkField.GetValue(comp);
                        if (atk != null)
                        {
                            float dmg = GetFloatMember(atk, "Damage");
                            var pdField = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                            object pd = null;
                            if (pdField != null) try { pd = pdField.GetValue(atk); } catch { }
                            if (dmg > 0 || pd != null)
                            {
                                hasSecondary = true;
                                if (pd != null) try { secName = CleanWeaponName(((UnityEngine.Object)pd).name); } catch { }
                                else secName = "Melee";
                            }
                        }
                    }
                    return;
                }
            }
            catch { }
        }

        // Build dynamic param groups for a unit based on its weapons
        private static void GetUnitParamGroups(string unitName, out string[] groupNames, out string[][] groupKeys)
        {
            GetUnitWeaponInfo(unitName, out bool hasPri, out bool hasSec, out string priName, out string secName);

            var names = new List<string>();
            var keys = new List<string[]>();

            // Health & Production (always)
            names.Add("Health & Production");
            if (string.Equals(unitName, "Hover Bike", StringComparison.OrdinalIgnoreCase))
                keys.Add(new[] { "health_mult", "cost_mult", "build_time_mult", "min_tier", "build_radius", "dispense_timeout" });
            else
                keys.Add(new[] { "health_mult", "cost_mult", "build_time_mult", "min_tier", "build_radius" });

            if (hasPri)
            {
                string label = "Primary Weapon";
                if (!string.IsNullOrEmpty(priName)) label += " (" + priName + ")";
                names.Add(label);
                var wk = new string[_weaponParamKeys.Length];
                for (int i = 0; i < _weaponParamKeys.Length; i++) wk[i] = "pri_" + _weaponParamKeys[i];
                keys.Add(wk);
            }

            if (hasSec)
            {
                string label = "Secondary Weapon";
                if (!string.IsNullOrEmpty(secName)) label += " (" + secName + ")";
                names.Add(label);
                var wk = new string[_weaponParamKeys.Length];
                for (int i = 0; i < _weaponParamKeys.Length; i++) wk[i] = "sec_" + _weaponParamKeys[i];
                keys.Add(wk);
            }

            // Fallback: no weapons detected, show unified group for backwards compat
            if (!hasPri && !hasSec)
            {
                names.Add("Damage & Weapons");
                keys.Add(new[] { "damage_mult", "range_mult", "proj_speed_mult", "accuracy_mult", "magazine_mult", "fire_rate_mult", "reload_time_mult" });
            }

            names.Add("Movement");
            keys.Add(new[] { "move_speed_mult", "jump_speed_mult", "turbo_speed_mult", "turn_radius_mult" });

            names.Add("Vision & Sense");
            keys.Add(new[] { "target_distance", "fow_distance", "visible_event_radius_mult" });

            groupNames = names.ToArray();
            groupKeys = keys.ToArray();
        }

        private static readonly string _chatPrefix = "<b><color=#DDE98C>[</color><color=#7DD4FF>BAL</color><color=#DDE98C>]</color></b> ";
        private static readonly string _headerColor = "<color=#FFD700>";
        private static readonly string _itemColor = "<color=#AAFFAA>";
        private static readonly string _valueColor = "<color=#FFB86C>";
        private static readonly string _dimColor = "<color=#888888>";
        private static string _auditLogPath = "";

        private static void SendChatToPlayer(object player, string message)
        {
            if (_sendChatToPlayerMethod == null || player == null) return;
            try
            {
                _sendChatToPlayerMethod.Invoke(null, new object[] { player, new string[] { message } });
            }
            catch { }
        }

        private static void SendConsoleToPlayer(object player, string message)
        {
            if (_sendConsoleToPlayerMethod == null || player == null) return;
            try
            {
                _sendConsoleToPlayerMethod.Invoke(null, new object[] { player, new string[] { message } });
            }
            catch { }
        }

        private static long GetPlayerKey(object player)
        {
            // Use NetworkID.m_ID (which is the SteamID ulong) as stable key
            try
            {
                if (_playerIdField != null)
                {
                    var netId = _playerIdField.GetValue(player); // NetworkID struct
                    if (netId != null)
                    {
                        // NetworkID has public ulong m_ID field
                        var midField = netId.GetType().GetField("m_ID", BindingFlags.Public | BindingFlags.Instance);
                        if (midField != null)
                        {
                            var val = midField.GetValue(netId);
                            if (val is ulong u) return (long)u;
                            if (val is long l) return l;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        private static string GetPlayerName(object player)
        {
            try
            {
                if (_playerNameField != null)
                    return _playerNameField.GetValue(player)?.ToString() ?? "Unknown";
            }
            catch { }
            return "Unknown";
        }

        private static string GetPlayerSteamId(object player)
        {
            try
            {
                if (_playerIdField != null)
                {
                    var netId = _playerIdField.GetValue(player);
                    if (netId != null)
                    {
                        var midField = netId.GetType().GetField("m_ID", BindingFlags.Public | BindingFlags.Instance);
                        if (midField != null)
                            return midField.GetValue(netId)?.ToString() ?? "0";
                    }
                }
            }
            catch { }
            return "0";
        }

        // Shortcut handler for .1-.9, .0, .back commands
        // Only active when player is in the balance menu
        private static void OnMenuShortcut(object player, string args)
        {
            long key = GetPlayerKey(player);
            if (!_menuStates.ContainsKey(key)) return; // Not in menu, silently ignore

            // args is full text like "!1" or ".1 1.5" or "!back"
            // Extract the command and optional extra args
            string text = args?.Trim() ?? "";
            // Strip prefix char (!, /, .)
            if (text.Length > 0 && (text[0] == '!' || text[0] == '/' || text[0] == '.'))
                text = text.Substring(1);

            ProcessMenuInput(player, text);
        }

        // Entry point via AdminMod: !b toggles balance editor on/off.
        // If already in menu with args, route through ProcessMenuInput.
        private static void OnBalanceCommand(object player, string args)
        {
            long key = GetPlayerKey(player);

            // Parse args after "!b": e.g. "!b exit", "!b 1", or just "!b"
            string extraArgs = "";
            if (args != null)
            {
                var parts = args.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1) extraArgs = parts[1].Trim();
            }

            // "!b exit" → close
            if (extraArgs.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                _menuStates.Remove(key);
                SendChatToPlayer(player, _chatPrefix + _dimColor + "Exited balance editor.</color>");
                return;
            }

            // Already in menu?
            if (_menuStates.ContainsKey(key))
            {
                if (string.IsNullOrEmpty(extraArgs))
                {
                    // "!b" with no args while in menu → exit
                    _menuStates.Remove(key);
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Exited balance editor.</color>");
                    return;
                }
                // "!b <something>" while in menu → route to ProcessMenuInput
                ProcessMenuInput(player, extraArgs);
                return;
            }

            // Not in menu → enter menu mode
            var state = new BalanceMenuState();
            _menuStates[key] = state;

            if (!string.IsNullOrEmpty(extraArgs))
            {
                // "!b 1" → enter and immediately select
                ProcessMenuInput(player, extraArgs);
            }
            else
            {
                SendChatToPlayer(player, _chatPrefix + _headerColor + "=== Balance Editor ===</color> " + _dimColor + "(.1 .2 .. or !b 1)</color>");
                ShowCurrentMenu(player, state);
            }
        }

        // Look up vanilla (unmodified) base values for a unit's parameters.
        // Reads from prefab components which retain vanilla values (OM stores overrides separately).
        private static Dictionary<string, string> GetBaseValues(string unitName, string[] paramKeys)
        {
            var result = new Dictionary<string, string>();
            try
            {
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                ObjectInfo matchedInfo = null;
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    if (info.DisplayName == unitName) { matchedInfo = info; break; }
                }
                if (matchedInfo == null) return result;

                var prefab = matchedInfo.Prefab;
                var childComps = prefab.GetComponentsInChildren<Component>(true);

                // Cache component lookups by type
                Component vtComp = null, sensorComp = null, soldierComp = null;
                Component wheeledComp = null, decapodComp = null;
                var allVtComps = new List<Component>();
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string tn = comp.GetType().Name;
                    if (tn == "VehicleTurret") { allVtComps.Add(comp); if (vtComp == null) vtComp = comp; }
                    else if (tn == "Sensor" && sensorComp == null) sensorComp = comp;
                    else if ((tn == "Soldier" || tn == "PlayerMovement" || tn == "FPSMovement") && soldierComp == null) soldierComp = comp;
                    else if (tn == "VehicleWheeled" && wheeledComp == null) wheeledComp = comp;
                    else if (tn == "CreatureDecapod" && decapodComp == null) decapodComp = comp;
                }
                // Build pri/sec weapon → (VehicleTurret, fieldPrefix, ProjectileData) mapping
                // Mirrors GetUnitWeaponInfo: iterate all VTs, first unique PD = pri, second = sec
                Component priVtComp = null, secVtComp = null;
                string priVtPrefix = "Primary", secVtPrefix = "Secondary";
                object priPD = null, secPD = null;
                var seenPDNames = new HashSet<string>();
                foreach (var vt in allVtComps)
                {
                    var vtType = vt.GetType();
                    foreach (string pfx in new[] { "Primary", "Secondary" })
                    {
                        object pd = null;
                        foreach (string suffix in new[] { "Projectile", "ProjectileData" })
                        {
                            var pdF = vtType.GetField(pfx + suffix, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (pdF == null) continue;
                            try { pd = pdF.GetValue(vt); } catch { }
                            if (pd != null) break;
                        }
                        if (pd == null) continue;
                        string pdName = "";
                        try { pdName = ((UnityEngine.Object)pd).name; } catch { }
                        if (string.IsNullOrEmpty(pdName) || seenPDNames.Contains(pdName)) continue;
                        seenPDNames.Add(pdName);
                        if (priPD == null) { priPD = pd; priVtComp = vt; priVtPrefix = pfx; }
                        else if (secPD == null) { secPD = pd; secVtComp = vt; secVtPrefix = pfx; }
                    }
                }

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                foreach (string key in paramKeys)
                {
                    try
                    {
                        string val = null;
                        switch (key)
                        {
                            case "health_mult":
                            {
                                var dm = prefab.GetComponent<DamageManager>();
                                if (dm != null)
                                {
                                    var dataField = dm.GetType().GetField("Data", flags);
                                    if (dataField != null)
                                    {
                                        object dataObj = dataField.GetValue(dm);
                                        if (dataObj != null)
                                        {
                                            float hp = GetFloatMember(dataObj, "Health");
                                            if (hp <= 0) hp = GetFloatMember(dataObj, "MaxHealth");
                                            if (hp > 0) val = hp.ToString("F0");
                                        }
                                    }
                                }
                                break;
                            }
                            case "cost_mult":
                                if (matchedInfo.ConstructionData != null)
                                    val = matchedInfo.ConstructionData.ResourceCost.ToString();
                                break;
                            case "build_time_mult":
                                if (matchedInfo.ConstructionData != null)
                                    val = matchedInfo.ConstructionData.BuildUpTime.ToString("F1") + "s";
                                break;
                            case "min_tier":
                                if (matchedInfo.ConstructionData != null)
                                    val = matchedInfo.ConstructionData.MinimumTeamTier.ToString();
                                break;
                            case "build_radius":
                                if (matchedInfo.ConstructionData != null)
                                    val = matchedInfo.ConstructionData.MaximumBaseStructureDistance.ToString("F0");
                                break;
                            case "damage_mult":
                            {
                                // Show primary projectile impact damage
                                if (vtComp != null)
                                {
                                    var pdField = vtComp.GetType().GetField("PrimaryProjectile", flags);
                                    if (pdField != null)
                                    {
                                        var pd = pdField.GetValue(vtComp);
                                        if (pd != null)
                                        {
                                            float dmg = GetFloatMember(pd, "m_fImpactDamage");
                                            if (dmg > 0) val = dmg.ToString("F0");
                                        }
                                    }
                                }
                                // Creature attack damage
                                if (val == null && decapodComp != null)
                                {
                                    var atkField = decapodComp.GetType().GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                                    if (atkField != null)
                                    {
                                        var atk = atkField.GetValue(decapodComp);
                                        if (atk != null)
                                        {
                                            float dmg = GetFloatMember(atk, "Damage");
                                            if (dmg > 0) val = dmg.ToString("F0");
                                            else
                                            {
                                                var pdField2 = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                                if (pdField2 != null)
                                                {
                                                    var pd = pdField2.GetValue(atk);
                                                    if (pd != null)
                                                    {
                                                        dmg = GetFloatMember(pd, "m_fImpactDamage");
                                                        if (dmg > 0) val = dmg.ToString("F0");
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                            }
                            case "range_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("AimDistance", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(vtComp)).ToString("F0");
                                }
                                break;
                            case "proj_speed_mult":
                                if (vtComp != null)
                                {
                                    var pdField = vtComp.GetType().GetField("PrimaryProjectile", flags);
                                    if (pdField != null)
                                    {
                                        var pd = pdField.GetValue(vtComp);
                                        if (pd != null)
                                        {
                                            float spd = GetFloatMember(pd, "m_fBaseSpeed");
                                            if (spd > 0) val = spd.ToString("F0");
                                        }
                                    }
                                }
                                break;
                            case "accuracy_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("PrimaryMuzzleSpread", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(vtComp)).ToString("F4");
                                }
                                break;
                            case "magazine_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("PrimaryMagazineSize", flags);
                                    if (f != null && f.FieldType == typeof(int))
                                        val = ((int)f.GetValue(vtComp)).ToString();
                                }
                                break;
                            case "fire_rate_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("PrimaryFireInterval", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(vtComp)).ToString("F3") + "s";
                                }
                                if (val == null && decapodComp != null)
                                {
                                    var af = decapodComp.GetType().GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                                    if (af != null)
                                    {
                                        var atk = af.GetValue(decapodComp);
                                        if (atk != null)
                                        {
                                            float cd = GetFloatMember(atk, "CoolDownTime");
                                            if (cd > 0) val = cd.ToString("F3") + "s";
                                        }
                                    }
                                }
                                break;
                            case "reload_time_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("PrimaryReloadTime", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(vtComp)).ToString("F1") + "s";
                                }
                                break;
                            case "move_speed_mult":
                                foreach (var comp in childComps)
                                {
                                    if (comp == null || val != null) continue;
                                    foreach (string fn in _speedFieldNames)
                                    {
                                        if (fn == "TurboSpeed") continue;
                                        var f = comp.GetType().GetField(fn, BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                                        if (f != null && f.FieldType == typeof(float))
                                        {
                                            float v = (float)f.GetValue(comp);
                                            if (v > 0) { val = v.ToString("F1"); break; }
                                        }
                                    }
                                }
                                break;
                            case "jump_speed_mult":
                                if (soldierComp != null)
                                {
                                    var f = soldierComp.GetType().GetField("JumpSpeed", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(soldierComp)).ToString("F1");
                                }
                                break;
                            case "turbo_speed_mult":
                                foreach (var comp in childComps)
                                {
                                    if (comp == null || val != null) continue;
                                    var f = comp.GetType().GetField("TurboSpeed", BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                                    if (f != null && f.FieldType == typeof(float))
                                    {
                                        float v = (float)f.GetValue(comp);
                                        if (v > 0) val = v.ToString("F1");
                                    }
                                }
                                break;
                            case "turn_radius_mult":
                                if (wheeledComp != null)
                                {
                                    var f = wheeledComp.GetType().GetField("TurningCircleRadius", BindingFlags.Public | BindingFlags.Instance);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(wheeledComp)).ToString("F1");
                                }
                                break;
                            case "target_distance":
                                if (sensorComp != null)
                                {
                                    var f = sensorComp.GetType().GetField("TargetingDistance", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(sensorComp)).ToString("F0");
                                }
                                break;
                            case "fow_distance":
                                if (sensorComp != null)
                                {
                                    var f = sensorComp.GetType().GetField("FogOfWarViewDistance", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(sensorComp)).ToString("F0");
                                }
                                break;
                            case "visible_event_radius_mult":
                                if (vtComp != null)
                                {
                                    var pdField = vtComp.GetType().GetField("PrimaryProjectile", flags);
                                    if (pdField != null)
                                    {
                                        var pd = pdField.GetValue(vtComp);
                                        if (pd != null)
                                        {
                                            float ver = GetFloatMember(pd, "VisibleEventRadius");
                                            if (ver > 0) val = ver.ToString("F0");
                                        }
                                    }
                                }
                                break;

                            case "dispense_timeout":
                            {
                                // Find VehicleDispenser that dispenses this unit
                                foreach (var info in allInfos)
                                {
                                    if (info == null || info.Prefab == null || val != null) continue;
                                    var disComps = info.Prefab.GetComponentsInChildren<Component>(true);
                                    foreach (var dc in disComps)
                                    {
                                        if (dc == null || dc.GetType().Name != "VehicleDispenser") continue;
                                        var vtdF = dc.GetType().GetField("VehicleToDispense", BindingFlags.Public | BindingFlags.Instance);
                                        if (vtdF == null) continue;
                                        var vtdObj = vtdF.GetValue(dc);
                                        if (vtdObj == null) continue;
                                        string dn = null;
                                        var dnF = vtdObj.GetType().GetField("DisplayName", BindingFlags.Public | BindingFlags.Instance);
                                        if (dnF != null) dn = dnF.GetValue(vtdObj) as string;
                                        else { var dnP = vtdObj.GetType().GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance); if (dnP != null) dn = dnP.GetValue(vtdObj) as string; }
                                        if (!string.Equals(dn, unitName, StringComparison.OrdinalIgnoreCase)) continue;
                                        var dtF = dc.GetType().GetField("DispenseTimeout", BindingFlags.Public | BindingFlags.Instance);
                                        if (dtF != null && dtF.FieldType == typeof(float))
                                            val = ((float)dtF.GetValue(dc)).ToString("F1") + "s";
                                        else
                                        {
                                            var dtP = dc.GetType().GetProperty("DispenseTimeout", BindingFlags.Public | BindingFlags.Instance);
                                            if (dtP != null && dtP.PropertyType == typeof(float))
                                                val = ((float)dtP.GetValue(dc)).ToString("F1") + "s";
                                        }
                                    }
                                }
                                break;
                            }

                            // Per-weapon keys: pri_X or sec_X → route to correct weapon slot
                            default:
                            {
                                bool isPri = key.StartsWith("pri_");
                                bool isSec = key.StartsWith("sec_");
                                if (!isPri && !isSec) break;
                                string baseKey = key.Substring(4); // strip "pri_" or "sec_"
                                string atkField = isPri ? "AttackPrimary" : "AttackSecondary";

                                // Use pre-computed weapon mapping (correct VT + field prefix)
                                object weaponPD = isPri ? priPD : secPD;
                                Component weaponVt = isPri ? priVtComp : secVtComp;
                                string weaponVtPfx = isPri ? priVtPrefix : secVtPrefix;

                                switch (baseKey)
                                {
                                    case "damage_mult":
                                        if (weaponPD != null)
                                        {
                                            float dmg = GetFloatMember(weaponPD, "m_fImpactDamage");
                                            if (dmg > 0) val = dmg.ToString("F0");
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    float dmg = GetFloatMember(atk, "Damage");
                                                    if (dmg > 0) val = dmg.ToString("F0");
                                                    else
                                                    {
                                                        var pdF2 = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                                        if (pdF2 != null)
                                                        {
                                                            var pd = pdF2.GetValue(atk);
                                                            if (pd != null)
                                                            {
                                                                dmg = GetFloatMember(pd, "m_fImpactDamage");
                                                                if (dmg > 0) val = dmg.ToString("F0");
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case "proj_speed_mult":
                                        if (weaponPD != null)
                                        {
                                            float spd = GetFloatMember(weaponPD, "m_fBaseSpeed");
                                            if (spd > 0) val = spd.ToString("F0");
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    var pdF2 = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                                    if (pdF2 != null)
                                                    {
                                                        var pd = pdF2.GetValue(atk);
                                                        if (pd != null)
                                                        {
                                                            float spd = GetFloatMember(pd, "m_fBaseSpeed");
                                                            if (spd > 0) val = spd.ToString("F0");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case "proj_lifetime_mult":
                                        if (weaponPD != null)
                                        {
                                            float lt = GetFloatMember(weaponPD, "m_fLifeTime");
                                            if (lt > 0) val = lt.ToString("F2") + "s";
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    var pdF2 = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                                    if (pdF2 != null)
                                                    {
                                                        var pd = pdF2.GetValue(atk);
                                                        if (pd != null)
                                                        {
                                                            float lt = GetFloatMember(pd, "m_fLifeTime");
                                                            if (lt > 0) val = lt.ToString("F2") + "s";
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case "accuracy_mult":
                                        if (weaponVt != null)
                                        {
                                            var f = weaponVt.GetType().GetField(weaponVtPfx + "MuzzleSpread", flags);
                                            if (f != null && f.FieldType == typeof(float))
                                                val = ((float)f.GetValue(weaponVt)).ToString("F4");
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    float spread = GetFloatMember(atk, "AttackProjectileSpread");
                                                    if (spread >= 0) val = spread.ToString("F4");
                                                }
                                            }
                                        }
                                        break;
                                    case "magazine_mult":
                                        if (weaponVt != null)
                                        {
                                            var f = weaponVt.GetType().GetField(weaponVtPfx + "MagazineSize", flags);
                                            if (f != null && f.FieldType == typeof(int))
                                                val = ((int)f.GetValue(weaponVt)).ToString();
                                        }
                                        break;
                                    case "fire_rate_mult":
                                        if (weaponVt != null)
                                        {
                                            var f = weaponVt.GetType().GetField(weaponVtPfx + "FireInterval", flags);
                                            if (f != null && f.FieldType == typeof(float))
                                                val = ((float)f.GetValue(weaponVt)).ToString("F3") + "s";
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    float cd = GetFloatMember(atk, "CoolDownTime");
                                                    if (cd > 0) val = cd.ToString("F3") + "s";
                                                }
                                            }
                                        }
                                        break;
                                    case "reload_time_mult":
                                        if (weaponVt != null)
                                        {
                                            var f = weaponVt.GetType().GetField(weaponVtPfx + "ReloadTime", flags);
                                            if (f != null && f.FieldType == typeof(float))
                                                val = ((float)f.GetValue(weaponVt)).ToString("F1") + "s";
                                        }
                                        break;
                                }
                                break;
                            }
                        }
                        if (val != null)
                            result[key] = val;
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static void ShowCurrentMenu(object player, BalanceMenuState state)
        {
            switch (state.Level)
            {
                case MenuLevel.Root:
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "1.</color> Sol  " + _itemColor + "2.</color> Centauri  " + _itemColor + "3.</color> Alien  " + _itemColor + "4.</color> JSON  " + _itemColor + "5.</color> HTP");
                    break;

                case MenuLevel.Faction:
                {
                    string factionName = new[] { "Sol", "Centauri", "Alien" }[state.FactionIdx];
                    SendChatToPlayer(player, _chatPrefix + _headerColor + factionName + "</color>");
                    var cats = _categoryNames[state.FactionIdx];
                    string line = "";
                    for (int i = 0; i < cats.Length; i++)
                    {
                        if (i > 0) line += "  ";
                        line += _itemColor + (i + 1) + ".</color> " + cats[i];
                    }
                    SendChatToPlayer(player, _chatPrefix + line);
                    break;
                }

                case MenuLevel.Category:
                {
                    string factionName = new[] { "Sol", "Centauri", "Alien" }[state.FactionIdx];
                    string catName = _categoryNames[state.FactionIdx][state.CategoryIdx];
                    SendChatToPlayer(player, _chatPrefix + _headerColor + factionName + " > " + catName + "</color>");
                    var units = _unitNames[state.FactionIdx][state.CategoryIdx];
                    for (int i = 0; i < units.Length; i += 3)
                    {
                        string line = "";
                        for (int j = i; j < Math.Min(i + 3, units.Length); j++)
                        {
                            if (j > i) line += "  ";
                            line += _itemColor + (j + 1) + ".</color> " + units[j];
                        }
                        SendChatToPlayer(player, _chatPrefix + line);
                    }
                    break;
                }

                case MenuLevel.Unit:
                {
                    string unitName = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];
                    GetUnitParamGroups(unitName, out string[] dynGroupNames, out string[][] _dkeys);
                    SendChatToPlayer(player, _chatPrefix + _headerColor + unitName + "</color>");
                    for (int i = 0; i < dynGroupNames.Length; i++)
                        SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + dynGroupNames[i]);
                    break;
                }

                case MenuLevel.ParamGroup:
                {
                    string unitName = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];
                    GetUnitParamGroups(unitName, out string[] dynGroupNames, out string[][] dynGroupKeys);
                    if (state.ParamGroupIdx >= dynGroupNames.Length) { state.Level = MenuLevel.Unit; ShowCurrentMenu(player, state); return; }
                    string groupName = dynGroupNames[state.ParamGroupIdx];
                    SendChatToPlayer(player, _chatPrefix + _headerColor + unitName + " > " + groupName + "</color>");

                    var keys = dynGroupKeys[state.ParamGroupIdx];
                    var unitConfig = ReadUnitConfigFromJson(unitName);
                    var baseVals = GetBaseValues(unitName, keys);

                    for (int i = 0; i < keys.Length; i++)
                    {
                        string val = "-";
                        if (unitConfig != null && unitConfig.ContainsKey(keys[i]))
                            val = unitConfig[keys[i]].ToString();
                        string baseSuffix = "";
                        if (baseVals.TryGetValue(keys[i], out string bv))
                            baseSuffix = " " + _dimColor + "(base: " + bv + ")</color>";
                        // Strip pri_/sec_ prefix for display: "pri_damage_mult" → "damage_mult"
                        string displayKey = keys[i];
                        if (displayKey.StartsWith("pri_") || displayKey.StartsWith("sec_"))
                            displayKey = displayKey.Substring(4);
                        SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + displayKey + " = " + _valueColor + val + "</color>" + baseSuffix);
                    }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 1.5 (or !b 1 1.5)</color>");
                    break;
                }

                case MenuLevel.JsonMenu:
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "JSON Config</color>");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "1.</color> Reset to Blank " + _dimColor + "(vanilla settings)</color>");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "2.</color> Save Current Config");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "3.</color> Load Saved Config");
                    break;

                case MenuLevel.JsonLoad:
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "Load Saved Config</color>");
                    if (state.JsonLoadFileList == null || state.JsonLoadFileList.Length == 0)
                    {
                        SendChatToPlayer(player, _chatPrefix + _dimColor + "No saved configs found.</color>");
                        SendChatToPlayer(player, _chatPrefix + _dimColor + "Use .back to return.</color>");
                    }
                    else
                    {
                        for (int i = 0; i < state.JsonLoadFileList.Length; i++)
                            SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + state.JsonLoadFileList[i]);
                        SendChatToPlayer(player, _chatPrefix + _dimColor + "Pick a number to load.</color>");
                    }
                    break;
                }

                // ── HTP (Hover · Tier · Teleportation) ──────────────
                case MenuLevel.HTPMenu:
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "HTP</color> " + _dimColor + "(Hover · Tier · Teleportation)</color>");
                    string shrimpStatus = _shrimpDisableAim ? "<color=#FF5555>OFF</color>" : "<color=#55FF55>ON</color>";
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "1.</color> Hoverbike  " + _itemColor + "2.</color> Tier  " + _itemColor + "3.</color> Teleportation  " + _itemColor + "4.</color> Shrimp Aim [" + shrimpStatus + "]");
                    break;
                }

                case MenuLevel.HTPHoverbike:
                {
                    GetUnitParamGroups("Hover Bike", out string[] hbNames, out string[][] _hk);
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "HTP > Hoverbike</color>");
                    for (int i = 0; i < hbNames.Length; i++)
                        SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + hbNames[i]);
                    break;
                }

                case MenuLevel.HTPHoverbikeParam:
                {
                    GetUnitParamGroups("Hover Bike", out string[] hbNames, out string[][] hbKeys);
                    if (state.HTPParamGroupIdx >= hbNames.Length) { state.Level = MenuLevel.HTPHoverbike; ShowCurrentMenu(player, state); return; }
                    string groupName = hbNames[state.HTPParamGroupIdx];
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "Hover Bike > " + groupName + "</color>");

                    var keys = hbKeys[state.HTPParamGroupIdx];
                    var unitConfig = ReadUnitConfigFromJson("Hover Bike");
                    var baseVals = GetBaseValues("Hover Bike", keys);

                    for (int i = 0; i < keys.Length; i++)
                    {
                        string val = "-";
                        if (unitConfig != null && unitConfig.ContainsKey(keys[i]))
                            val = unitConfig[keys[i]].ToString();
                        string baseSuffix = "";
                        if (baseVals.TryGetValue(keys[i], out string bv))
                            baseSuffix = " " + _dimColor + "(base: " + bv + ")</color>";
                        string displayKey = keys[i];
                        if (displayKey.StartsWith("pri_") || displayKey.StartsWith("sec_"))
                            displayKey = displayKey.Substring(4);
                        SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + displayKey + " = " + _valueColor + val + "</color>" + baseSuffix);
                    }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 1.5 (or !b 1 1.5)</color>");
                    break;
                }

                case MenuLevel.HTPTier:
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "HTP > Tier</color> " + _dimColor + "(tech-up time per tier, in seconds)</color>");
                    // Read current tech_time from JSON
                    JObject techTime = null;
                    try
                    {
                        if (File.Exists(_configPath))
                        {
                            var root = JObject.Parse(File.ReadAllText(_configPath));
                            techTime = root["tech_time"] as JObject;
                        }
                    }
                    catch { }

                    for (int tier = 1; tier <= 8; tier++)
                    {
                        string val = "-";
                        if (techTime != null)
                        {
                            float? t = techTime[$"tier_{tier}"]?.Value<float>();
                            if (t.HasValue) val = t.Value.ToString("F0") + "s";
                        }
                        SendChatToPlayer(player, _chatPrefix + _itemColor + tier + ".</color> Tier " + tier + " = " + _valueColor + val + "</color>" + " " + _dimColor + "(default: 30s)</color>");
                    }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 45 (or !b 1 45)</color>");
                    break;
                }

                case MenuLevel.HTPTeleport:
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "HTP > Teleportation</color>");
                    // Read current teleport values from JSON (stored under units > "_teleport")
                    var tpConfig = ReadUnitConfigFromJson("_teleport");
                    string cdVal = "-", durVal = "-";
                    if (tpConfig != null)
                    {
                        if (tpConfig.ContainsKey("cooldown")) cdVal = tpConfig["cooldown"].ToString() + "s";
                        if (tpConfig.ContainsKey("duration")) durVal = tpConfig["duration"].ToString() + "s";
                    }
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "1.</color> Cooldown = " + _valueColor + cdVal + "</color> " + _dimColor + "(base: 120s)</color>");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "2.</color> Duration = " + _valueColor + durVal + "</color> " + _dimColor + "(base: 5s)</color>");
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 90 (or !b 1 90)</color>");
                    break;
                }
            }
        }

        private static void HandleParamEdit(object player, BalanceMenuState state, int paramIdx, string valueStr)
        {
            string unitNameForGroups = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];
            GetUnitParamGroups(unitNameForGroups, out string[] _dn, out string[][] dynGroupKeys);
            if (state.ParamGroupIdx >= dynGroupKeys.Length) return;
            var keys = dynGroupKeys[state.ParamGroupIdx];
            if (paramIdx < 1 || paramIdx > keys.Length)
            {
                SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{keys.Length}.</color>");
                return;
            }

            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float newValue))
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid number: " + valueStr + "</color>");
                return;
            }

            string paramKey = keys[paramIdx - 1];
            string unitName = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];

            // Read current value
            var unitConfig = ReadUnitConfigFromJson(unitName);
            string oldVal = "-";
            if (unitConfig != null && unitConfig.ContainsKey(paramKey))
                oldVal = unitConfig[paramKey].ToString();

            string newValStr = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Store pending change and ask for confirmation
            state.PendingConfirm = true;
            state.PendingParamKey = paramKey;
            state.PendingValue = newValue;
            state.PendingOldVal = oldVal;

            SendChatToPlayer(player, _chatPrefix + _headerColor + "Confirm:</color> " + unitName + " " + paramKey + " " + _valueColor + oldVal + "</color> -> " + _valueColor + newValStr + "</color>");
            SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Save  " + _itemColor + ".2</color>=Cancel  " + _itemColor + ".3</color>=Save+Rebalance");
        }

        // ── HTP Edit Handlers ────────────────────────────────────────

        private static void HandleHTPHoverbikeEdit(object player, BalanceMenuState state, int paramIdx, string valueStr)
        {
            GetUnitParamGroups("Hover Bike", out string[] _dn, out string[][] hbKeys);
            if (state.HTPParamGroupIdx >= hbKeys.Length) return;
            var keys = hbKeys[state.HTPParamGroupIdx];
            if (paramIdx < 1 || paramIdx > keys.Length)
            {
                SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{keys.Length}.</color>");
                return;
            }
            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float newValue))
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid number: " + valueStr + "</color>");
                return;
            }

            string paramKey = keys[paramIdx - 1];
            var unitConfig = ReadUnitConfigFromJson("Hover Bike");
            string oldVal = "-";
            if (unitConfig != null && unitConfig.ContainsKey(paramKey))
                oldVal = unitConfig[paramKey].ToString();

            string newValStr = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            state.PendingConfirm = true;
            state.PendingParamKey = paramKey;
            state.PendingValue = newValue;
            state.PendingOldVal = oldVal;
            state.PendingUnitName = "Hover Bike";

            SendChatToPlayer(player, _chatPrefix + _headerColor + "Confirm:</color> Hover Bike " + paramKey + " " + _valueColor + oldVal + "</color> -> " + _valueColor + newValStr + "</color>");
            SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Save  " + _itemColor + ".2</color>=Cancel  " + _itemColor + ".3</color>=Save+Rebalance");
        }

        private static void HandleHTPTierEdit(object player, BalanceMenuState state, int tierNum, string valueStr)
        {
            if (tierNum < 1 || tierNum > 8)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick tier 1-8.</color>");
                return;
            }
            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float newValue))
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid number: " + valueStr + "</color>");
                return;
            }

            string tierKey = $"tier_{tierNum}";
            // Read current value
            string oldVal = "-";
            try
            {
                if (File.Exists(_configPath))
                {
                    var root = JObject.Parse(File.ReadAllText(_configPath));
                    var tt = root["tech_time"] as JObject;
                    if (tt != null)
                    {
                        float? t = tt[tierKey]?.Value<float>();
                        if (t.HasValue) oldVal = t.Value.ToString("F0");
                    }
                }
            }
            catch { }

            string newValStr = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            state.PendingConfirm = true;
            state.PendingParamKey = tierKey;
            state.PendingValue = newValue;
            state.PendingOldVal = oldVal;
            state.PendingTechTierKey = tierKey;

            SendChatToPlayer(player, _chatPrefix + _headerColor + "Confirm:</color> Tier " + tierNum + " tech time " + _valueColor + oldVal + "</color> -> " + _valueColor + newValStr + "s</color>");
            SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Save  " + _itemColor + ".2</color>=Cancel  " + _itemColor + ".3</color>=Save+Rebalance");
        }

        private static void HandleHTPTeleportEdit(object player, BalanceMenuState state, int paramIdx, string valueStr)
        {
            if (paramIdx < 1 || paramIdx > 2)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-2.</color>");
                return;
            }
            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float newValue))
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid number: " + valueStr + "</color>");
                return;
            }

            string paramKey = paramIdx == 1 ? "cooldown" : "duration";
            string label = paramIdx == 1 ? "Cooldown" : "Duration";

            var tpConfig = ReadUnitConfigFromJson("_teleport");
            string oldVal = "-";
            if (tpConfig != null && tpConfig.ContainsKey(paramKey))
                oldVal = tpConfig[paramKey].ToString();

            string newValStr = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            state.PendingConfirm = true;
            state.PendingParamKey = paramKey;
            state.PendingValue = newValue;
            state.PendingOldVal = oldVal;
            state.PendingUnitName = "_teleport";

            SendChatToPlayer(player, _chatPrefix + _headerColor + "Confirm:</color> Teleport " + label + " " + _valueColor + oldVal + "</color> -> " + _valueColor + newValStr + "s</color>");
            SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Save  " + _itemColor + ".2</color>=Cancel  " + _itemColor + ".3</color>=Save+Rebalance");
        }

        private static bool WriteTechTierToJson(string tierKey, float value)
        {
            try
            {
                if (!File.Exists(_configPath)) return false;
                string json = File.ReadAllText(_configPath);
                var root = JObject.Parse(json);
                var tt = root["tech_time"] as JObject;
                if (tt == null)
                {
                    tt = new JObject();
                    root["tech_time"] = tt;
                }
                tt[tierKey] = (int)Math.Round(value);
                File.WriteAllText(_configPath, root.ToString());
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"WriteTechTierToJson: {ex.Message}");
                return false;
            }
        }

        private static bool WriteBoolToJson(string key, bool value)
        {
            try
            {
                if (!File.Exists(_configPath)) return false;
                string json = File.ReadAllText(_configPath);
                var root = JObject.Parse(json);
                root[key] = value;
                File.WriteAllText(_configPath, root.ToString());
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"WriteBoolToJson: {ex.Message}");
                return false;
            }
        }

        // Read a unit's config values from JSON on disk
        private static JObject ReadUnitConfigFromJson(string unitName)
        {
            try
            {
                if (!File.Exists(_configPath)) return null;
                string json = File.ReadAllText(_configPath);
                var root = JObject.Parse(json);
                var units = root["units"] as JObject;
                if (units == null) return null;
                return units[unitName] as JObject;
            }
            catch { return null; }
        }

        // Write a parameter value into the JSON config file using regex-based modification
        // (avoids Newtonsoft.Json write methods which are missing on the server)
        private static bool WriteParamToJson(string unitName, string paramKey, float value)
        {
            try
            {
                if (!File.Exists(_configPath)) return false;
                string json = File.ReadAllText(_configPath);

                string valueStr;
                if (paramKey == "min_tier" || paramKey == "build_radius" || paramKey == "target_distance" || paramKey == "fow_distance")
                    valueStr = ((int)value).ToString();
                else
                    valueStr = Math.Round(value, 4).ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Find the unit block in JSON and update/insert the param
                // Strategy: find "UnitName": { ... } block, then find/insert the key
                var root = JObject.Parse(json);
                var units = root["units"] as JObject;
                if (units == null) return false;
                var unitObj = units[unitName] as JObject;

                // Check if unit block exists in the raw JSON
                // Use string-based approach: find the unit, modify/add the key
                int unitStart = FindJsonKey(json, unitName);
                if (unitStart < 0)
                {
                    // Unit doesn't exist — insert before closing "}" of "units" block
                    int unitsEnd = FindClosingBrace(json, FindJsonKey(json, "units"));
                    if (unitsEnd < 0) return false;
                    // Insert new unit entry before the closing brace
                    string indent = "        ";
                    string newEntry = ",\n" + indent + "\"" + EscapeJsonString(unitName) + "\": { " +
                        "\"" + EscapeJsonString(paramKey) + "\": " + valueStr +
                        ", \"_note\": \"modified via !b\" }";
                    json = json.Insert(unitsEnd, newEntry);
                }
                else
                {
                    // Unit exists — find its object block
                    int braceStart = json.IndexOf('{', unitStart);
                    if (braceStart < 0) return false;
                    int braceEnd = FindClosingBrace(json, braceStart);
                    if (braceEnd < 0) return false;

                    string unitBlock = json.Substring(braceStart, braceEnd - braceStart + 1);

                    // Check if key already exists
                    int keyPos = FindJsonKey(unitBlock, paramKey);
                    if (keyPos >= 0)
                    {
                        // Replace existing value
                        int colonPos = unitBlock.IndexOf(':', keyPos);
                        if (colonPos < 0) return false;
                        // Find start and end of the value
                        int valStart = colonPos + 1;
                        while (valStart < unitBlock.Length && unitBlock[valStart] == ' ') valStart++;
                        int valEnd = valStart;
                        // Value ends at , or } or whitespace-then-comma
                        bool inString = false;
                        if (valEnd < unitBlock.Length && unitBlock[valEnd] == '"') inString = true;
                        while (valEnd < unitBlock.Length)
                        {
                            char c = unitBlock[valEnd];
                            if (inString)
                            {
                                if (c == '"' && valEnd > valStart) { valEnd++; break; }
                            }
                            else
                            {
                                if (c == ',' || c == '}' || c == '\n') break;
                            }
                            valEnd++;
                        }
                        string oldValRaw = unitBlock.Substring(valStart, valEnd - valStart).TrimEnd();
                        unitBlock = unitBlock.Substring(0, valStart) + " " + valueStr +
                                    unitBlock.Substring(valStart + oldValRaw.Length);
                    }
                    else
                    {
                        // Insert new key before closing brace
                        int lastBrace = unitBlock.LastIndexOf('}');
                        string insertion = ", \"" + EscapeJsonString(paramKey) + "\": " + valueStr;
                        unitBlock = unitBlock.Insert(lastBrace, insertion);
                    }

                    // Also update/insert _note
                    int notePos = FindJsonKey(unitBlock, "_note");
                    if (notePos >= 0)
                    {
                        int colonPos = unitBlock.IndexOf(':', notePos);
                        if (colonPos >= 0)
                        {
                            int valStart = colonPos + 1;
                            while (valStart < unitBlock.Length && unitBlock[valStart] == ' ') valStart++;
                            int valEnd = valStart;
                            if (valEnd < unitBlock.Length && unitBlock[valEnd] == '"')
                            {
                                valEnd++;
                                while (valEnd < unitBlock.Length && unitBlock[valEnd] != '"') valEnd++;
                                if (valEnd < unitBlock.Length) valEnd++;
                            }
                            unitBlock = unitBlock.Substring(0, valStart) + " \"modified via !b\"" +
                                        unitBlock.Substring(valEnd);
                        }
                    }

                    json = json.Substring(0, braceStart) + unitBlock + json.Substring(braceEnd + 1);
                }

                File.WriteAllText(_configPath, json);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BAL] Failed to write JSON: {ex.Message}");
                return false;
            }
        }

        // Find the position of a JSON key ("keyName":) in a string, returns index of the opening quote
        private static int FindJsonKey(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int pos = 0;
            while (pos < json.Length)
            {
                int found = json.IndexOf(pattern, pos, StringComparison.Ordinal);
                if (found < 0) return -1;
                // Verify it's followed by : (with optional whitespace)
                int afterQuote = found + pattern.Length;
                while (afterQuote < json.Length && (json[afterQuote] == ' ' || json[afterQuote] == '\t')) afterQuote++;
                if (afterQuote < json.Length && json[afterQuote] == ':')
                    return found;
                pos = found + 1;
            }
            return -1;
        }

        // Find the matching closing brace for an opening brace at position pos
        private static int FindClosingBrace(string json, int startPos)
        {
            if (startPos < 0) return -1;
            int bracePos = json.IndexOf('{', startPos);
            if (bracePos < 0) return -1;
            int depth = 0;
            bool inStr = false;
            for (int i = bracePos; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static string EscapeJsonString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void WriteAuditLog(string playerName, string steamId, string unitName, string paramKey, string oldVal, string newVal)
        {
            try
            {
                if (string.IsNullOrEmpty(_auditLogPath))
                    _auditLogPath = Path.Combine(Path.GetDirectoryName(_configPath)!, "Si_UnitBalance_Audit.log");
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {playerName} ({steamId}): {unitName} {paramKey} {oldVal} -> {newVal}";
                File.AppendAllText(_auditLogPath, line + Environment.NewLine);
            }
            catch { }
        }

        // =============================================
        // JSON Config Management (save/load/reset)
        // =============================================

        /// <summary>Sanitize a user-provided filename: only allow alphanumeric, underscore, hyphen, period.</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
            }
            string sanitized = sb.ToString().Trim('.');
            // Prevent reserved Windows names
            string upper = sanitized.ToUpperInvariant();
            foreach (string reserved in new[] { "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" })
            {
                if (upper == reserved || upper.StartsWith(reserved + "."))
                    return "_" + sanitized;
            }
            if (sanitized.Length > 60) sanitized = sanitized.Substring(0, 60);
            return sanitized;
        }

        /// <summary>Ensure the save directory exists.</summary>
        private static bool EnsureSaveDir()
        {
            try
            {
                if (!Directory.Exists(_configSaveDir))
                    Directory.CreateDirectory(_configSaveDir);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JSON] Failed to create save dir: {ex.Message}");
                return false;
            }
        }

        /// <summary>Get list of saved .json config files in the save directory, sorted newest first.</summary>
        private static string[] GetSavedConfigs()
        {
            try
            {
                if (!Directory.Exists(_configSaveDir)) return new string[0];
                var files = Directory.GetFiles(_configSaveDir, "*.json");
                // Security: verify each file is really inside the save dir
                var canonical = Path.GetFullPath(_configSaveDir);
                var safe = new List<string>();
                foreach (var f in files)
                {
                    string full = Path.GetFullPath(f);
                    if (!full.StartsWith(canonical + Path.DirectorySeparatorChar) &&
                        !full.StartsWith(canonical + Path.AltDirectorySeparatorChar) &&
                        full != canonical)
                        continue;
                    safe.Add(Path.GetFileName(f));
                }
                safe.Sort((a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase)); // newest first
                return safe.ToArray();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JSON] Failed to list saved configs: {ex.Message}");
                return new string[0];
            }
        }

        /// <summary>Save current active config to the save folder with timestamp + optional name.</summary>
        private static bool SaveConfigAs(string optionalName)
        {
            if (!EnsureSaveDir()) return false;
            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMddHHmm");
                string fileName;
                if (!string.IsNullOrEmpty(optionalName))
                {
                    string safe = SanitizeFileName(optionalName);
                    if (string.IsNullOrEmpty(safe)) safe = "config";
                    fileName = timestamp + "_" + safe + ".json";
                }
                else
                    fileName = timestamp + ".json";

                string destPath = Path.Combine(_configSaveDir, fileName);
                // Verify destination is inside save dir
                if (!Path.GetFullPath(destPath).StartsWith(Path.GetFullPath(_configSaveDir)))
                {
                    MelonLogger.Error("[JSON] Path traversal attempt blocked in save.");
                    return false;
                }

                if (!File.Exists(_configPath))
                {
                    MelonLogger.Error("[JSON] Active config not found, nothing to save.");
                    return false;
                }

                File.Copy(_configPath, destPath, overwrite: false);
                MelonLogger.Msg($"[JSON] Saved config as: {fileName}");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JSON] Failed to save config: {ex.Message}");
                return false;
            }
        }

        /// <summary>Load a saved config from the save folder, replacing the active config.</summary>
        private static bool LoadSavedConfig(string fileName)
        {
            try
            {
                // Security: sanitize and validate
                string safeName = Path.GetFileName(fileName); // strip any path components
                if (!safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    MelonLogger.Error($"[JSON] Rejected non-JSON file: {safeName}");
                    return false;
                }
                string srcPath = Path.Combine(_configSaveDir, safeName);
                string fullSrc = Path.GetFullPath(srcPath);
                string fullDir = Path.GetFullPath(_configSaveDir);
                // Must be inside save dir
                if (!fullSrc.StartsWith(fullDir + Path.DirectorySeparatorChar) &&
                    !fullSrc.StartsWith(fullDir + Path.AltDirectorySeparatorChar))
                {
                    MelonLogger.Error($"[JSON] Path traversal attempt blocked in load: {safeName}");
                    return false;
                }
                if (!File.Exists(srcPath))
                {
                    MelonLogger.Error($"[JSON] Saved config not found: {safeName}");
                    return false;
                }
                // Validate it's parseable JSON before overwriting active config
                try
                {
                    string content = File.ReadAllText(srcPath);
                    JObject.Parse(content); // throws if invalid
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[JSON] Invalid JSON in {safeName}: {ex.Message}");
                    return false;
                }
                File.Copy(srcPath, _configPath, overwrite: true);
                MelonLogger.Msg($"[JSON] Loaded config from: {safeName}");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JSON] Failed to load config: {ex.Message}");
                return false;
            }
        }

        /// <summary>Reset active config to blank (vanilla settings).</summary>
        private static bool ResetToBlankConfig()
        {
            try
            {
                string blankJson = @"{
    ""enabled"": true,
    ""dump_fields"": false,
    ""description"": ""Blank config with vanilla settings. All multipliers at defaults."",
    ""tech_time"": {
        ""tier_1"": 30,
        ""tier_2"": 30,
        ""tier_3"": 30,
        ""tier_4"": 30,
        ""tier_5"": 30,
        ""tier_6"": 30,
        ""tier_7"": 30,
        ""tier_8"": 30
    },
    ""units"": {}
}";
                File.WriteAllText(_configPath, blankJson);
                MelonLogger.Msg("[JSON] Config reset to blank (vanilla settings).");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JSON] Failed to reset config: {ex.Message}");
                return false;
            }
        }
    }
}

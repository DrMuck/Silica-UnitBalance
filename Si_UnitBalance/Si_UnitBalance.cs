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
using MelonLoader.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_UnitBalance.UnitBalance), "Unit Balance", "7.0.0", "schwe")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod")]

namespace Si_UnitBalance
{
    public partial class UnitBalance : MelonMod
    {
        private static string _configPath = "";
        private static string _configSaveDir = "";  // subfolder for saved configs
        private static bool _configLoaded;
        private static bool _fieldsDumped;

        private static bool _enabled = true;
        private static bool _dumpFields = true;
        private static bool _shrimpDisableAim = false;
        private static bool _additionalSpawn = false;
        // _revertOnRoundEnd removed — always false (reverting breaks starter structure overrides)
        private static bool _healthMultEnabled = true; // always enabled — client mod handles sync
        private static bool _overridesApplied; // tracks if OM overrides are currently active (for no-revert mode)
        private static bool _discordAutoPost = false;
        private static string _discordWebhookUrl = "";
        private static bool _harmonyApplied; // true when SilicaCore found (server)
        private static bool _debugLogging = false; // read from Admin_EnableDebugLogging MelonPreference

        /// <summary>Log only when debug logging is enabled (Admin_EnableDebugLogging in MelonPreferences).</summary>
        internal static void LogDebug(string msg) { if (_debugLogging) MelonLogger.Msg(msg); }

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
        private static FieldInfo _playerNameField;
        private static FieldInfo _playerIdField; // PlayerID (NetworkID struct)

        // Per-player menu state for !b command (keyed by SteamID for stable identity across Il2Cpp wrappers)
        private static readonly Dictionary<long, BalanceMenuState> _menuStates = new Dictionary<long, BalanceMenuState>();

        private enum MenuLevel { Root, Faction, Category, Unit, ParamGroup, JsonMenu, JsonLoad, HTPMenu, HTPHoverbike, HTPHoverbikeParam, HTPTier, HTPTeleport, DiscordMenu }
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
        // Tracks which team+unit combos have had their dispenser LocalTimeout reset (once per game)
        private static readonly HashSet<string> _dispenserTierResets = new HashSet<string>();
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
        private static readonly Dictionary<string, float> _strafeSpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _flySpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _runSpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _sprintSpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Splash radius multipliers: composite keys like "max:unitName", "pri:max:unitName"
        private static readonly Dictionary<string, float> _splashRadiusMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Tech tier number (1-8) -> build time in seconds
        private static readonly Dictionary<int, float> _techTierTimes = new Dictionary<int, float>();

        // Original health values (keyed by DamageManagerData asset name) to prevent compounding
        private static readonly Dictionary<string, float> _originalHealth =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Original aim angle values (keyed by unit_attackField) to prevent compounding
        private static readonly Dictionary<string, float> _originalAimAngle =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Original melee damage values (keyed by unit_attackField) to prevent compounding
        private static readonly Dictionary<string, float> _originalMeleeDamage =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Original move speed values (keyed by unit+component+field) to prevent compounding
        private static readonly Dictionary<string, float> _originalMoveSpeeds =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Cache: Unity instance ID -> display name
        private static readonly Dictionary<int, string> _nameCache = new Dictionary<int, string>();

        // Per-projectile absolute value overrides: unit name -> { projectile name -> { field name -> value } }
        private static readonly Dictionary<string, Dictionary<string, Dictionary<string, float>>> _projectileOverrides =
            new Dictionary<string, Dictionary<string, Dictionary<string, float>>>(StringComparer.OrdinalIgnoreCase);

        // Per-projectile proximity detonation overrides: projectile name -> { field name -> value }
        // Fields: MinimumTime (float), SplashRadiusScale (float), FlyingUnits (0/1), GroundUnits (0/1), Structures (0/1)
        private static readonly Dictionary<string, Dictionary<string, float>> _proximityOverrides =
            new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);

        // Teleport overrides (from _teleport pseudo-unit in config)
        private static float _teleportCooldown = -1f;
        private static float _teleportDuration = -1f;

        // Dispenser timeout override (absolute seconds, -1 = no override)
        private static float _dispenseTimeout = -1f;

        // Resolve display name to config key — handles units with duplicate DisplayNames
        // (e.g., both Sol and Centauri have DisplayName="Harvester" and "Headquarters")
        private static string ResolveConfigName(string displayName, string internalName)
        {
            if (!string.IsNullOrEmpty(internalName))
            {
                if (string.Equals(displayName, "Harvester", StringComparison.OrdinalIgnoreCase))
                {
                    if (internalName.IndexOf("_Sol_", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "Sol Harvester";
                    if (internalName.IndexOf("_Cent_", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "Cent Harvester";
                }
                else if (string.Equals(displayName, "Headquarters", StringComparison.OrdinalIgnoreCase))
                {
                    if (internalName.IndexOf("_Sol_", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "Sol Headquarters";
                    if (internalName.IndexOf("_Cent_", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "Cent Headquarters";
                }
            }
            return displayName;
        }

        public override void OnInitializeMelon()
        {
            var cfgDir = Path.Combine(MelonEnvironment.UserDataDirectory, "UnitBalance_cfg");
            if (!Directory.Exists(cfgDir))
                Directory.CreateDirectory(cfgDir);
            _configPath = Path.Combine(cfgDir, "Si_UnitBalance_Config.json");
            _configSaveDir = Path.Combine(cfgDir, "Saved_Configs");

            LoadConfig();
            TryApplyHarmonyPatches();
            MelonLogger.Msg($"Unit Balance v7.2.0 initialized. Config: {_configPath}");
            MelonLogger.Msg($"  Enabled: {_enabled} | Damage: {_damageMultipliers.Count} | Health: {_healthMultipliers.Count} | Cost: {_costMultipliers.Count} | BuildTime: {_buildTimeMultipliers.Count} | Range: {_rangeMultipliers.Count} | Speed: {_speedMultipliers.Count} | Reload: {_reloadTimeMultipliers.Count} | MoveSpeed: {_moveSpeedMultipliers.Count} | MinTier: {_minTierOverrides.Count} | TechTime: {_techTierTimes.Count}");
        }

        // =============================================
        // Manual Harmony patching — only when SilicaCore is available (server)
        // =============================================

        private void TryApplyHarmonyPatches()
        {
            try
            {
                Type musicHandler = typeof(MusicJukeboxHandler);
                Type damageManager = typeof(DamageManager);

                var harmony = HarmonyInstance;

                var gameInit = AccessTools.Method(musicHandler, "OnGameInit");
                if (gameInit != null)
                    harmony.Patch(gameInit, postfix: new HarmonyMethod(typeof(Patch_GameInit), "Postfix"));

                var gameRestart = AccessTools.Method(musicHandler, "OnGameRestart");
                if (gameRestart != null)
                    harmony.Patch(gameRestart, postfix: new HarmonyMethod(typeof(Patch_GameRestart), "Postfix"));

                var gameStarted = AccessTools.Method(musicHandler, "OnGameStarted");
                if (gameStarted != null)
                    harmony.Patch(gameStarted, postfix: new HarmonyMethod(typeof(Patch_GameStarted), "Postfix"));

                var gameEnded = AccessTools.Method(musicHandler, "OnGameEnded");
                if (gameEnded != null)
                    harmony.Patch(gameEnded, postfix: new HarmonyMethod(typeof(Patch_GameEnded), "Postfix"));

                // ApplyDamage Harmony Postfix removed — damage scaling now done via ProjectileData fields
                // which avoids client-server desync (Postfix modified HP after damage was already networked)

                // Patch VehicleDispenser.RequestVehicle to block dispensing if team tier < min_tier
                var requestVehicle = AccessTools.Method(typeof(VehicleDispenser), "RequestVehicle");
                if (requestVehicle != null)
                {
                    harmony.Patch(requestVehicle,
                        prefix: new HarmonyMethod(typeof(Patch_VehicleDispenser), "Prefix"));
                    MelonLogger.Msg("Patched VehicleDispenser.RequestVehicle for min_tier enforcement");
                }

                // Patch SendPlayerOverrides to chunk overrides into multiple packets
                // (the original packs ALL overrides into one packet, exceeding Steam's 2400-byte limit)
                var sendPlayerOverrides = typeof(NetworkLayer).GetMethod("SendPlayerOverrides",
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

                // Patch ConstructionSite.SpawnObject to ensure full health on building completion
                // Fixes vanilla timing bug where first-built structures get partial health due to
                // HealthAddRemaining being computed against a different MaxHealth than final Health01 ratio.
                var spawnObject = typeof(ConstructionSite).GetMethod("SpawnObject",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (spawnObject != null)
                {
                    harmony.Patch(spawnObject,
                        prefix: new HarmonyMethod(typeof(Patch_ConstructionSpawn), "Prefix"));
                    MelonLogger.Msg("Patched ConstructionSite.SpawnObject for construction health fix");
                }

                // Register chat commands via AdminMod API
                try
                {
                    // AdminMod is optional — find HelperMethods via reflection
                    Type helperType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        helperType = asm.GetType("SilicaAdminMod.HelperMethods");
                        if (helperType != null) break;
                    }
                    if (helperType != null)
                    {
                        _adminCallbackType = helperType.GetNestedType("CommandCallback", BindingFlags.Public);
                        if (_adminCallbackType == null)
                            _adminCallbackType = helperType.Assembly.GetType("SilicaAdminMod.HelperMethods+CommandCallback");
                        _adminPowerType = helperType.Assembly.GetType("SilicaAdminMod.Power");
                        _registerAdminCmdMethod = helperType.GetMethod("RegisterAdminCommand", BindingFlags.Public | BindingFlags.Static);

                        // Cache SendChatMessageToPlayer for UI
                        _sendChatToPlayerMethod = helperType.GetMethod("SendChatMessageToPlayer", BindingFlags.Public | BindingFlags.Static);
                        _sendConsoleToPlayerMethod = helperType.GetMethod("SendConsoleMessageToPlayer", BindingFlags.Public | BindingFlags.Static);

                        // Cache Player.PlayerName and Player.PlayerID for audit log
                        _playerNameField = typeof(Player).GetField("PlayerName", BindingFlags.Public | BindingFlags.Instance);
                        _playerIdField = typeof(Player).GetField("PlayerID", BindingFlags.Public | BindingFlags.Instance);

                        if (_adminCallbackType != null && _registerAdminCmdMethod != null)
                        {
                            // Helper: create a DynamicMethod shim for void(Player, string) -> void(object, string)
                            Delegate MakeShim(string name, string targetMethod)
                            {
                                var method = typeof(UnitBalance).GetMethod(targetMethod, BindingFlags.NonPublic | BindingFlags.Static);
                                var dm = new System.Reflection.Emit.DynamicMethod(
                                    name, typeof(void), new Type[] { typeof(Player), typeof(string) },
                                    typeof(UnitBalance).Module, true);
                                var il = dm.GetILGenerator();
                                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                                il.Emit(System.Reflection.Emit.OpCodes.Call, method);
                                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                                return dm.CreateDelegate(_adminCallbackType);
                            }

                            object powerNone = Enum.ToObject(_adminPowerType, 0);
                            // Power.Generic (0x4000) — all admins (root and non-root) have this
                            object powerGeneric = Enum.ToObject(_adminPowerType, 0x4000);

                            // Register !rebalance (Generic power — all admins can use)
                            _registerAdminCmdMethod.Invoke(null, new object[] {
                                "rebalance", MakeShim("RebalanceShim", "OnRebalanceCommand"),
                                powerGeneric, "Hot-reload unit balance config" });
                            MelonLogger.Msg("Registered !rebalance admin command via AdminMod");

                            // Register !b (balance editor UI — Generic power for all admins)
                            _registerAdminCmdMethod.Invoke(null, new object[] {
                                "b", MakeShim("BalanceShim", "OnBalanceCommand"),
                                powerGeneric, "Balance editor UI" });
                            MelonLogger.Msg("Registered !b admin command (balance editor)");

                            // Register shortcut commands: /1 through /9, /0, /back
                            // These let admins navigate the menu without typing "/b " prefix
                            // Using / prefix hides chat from other players (AdminMod feature)
                            var shortcutShim = MakeShim("MenuShortcutShim", "OnMenuShortcut");
                            string[] shortcuts = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "0", "back" };
                            foreach (var sc in shortcuts)
                            {
                                try
                                {
                                    _registerAdminCmdMethod.Invoke(null, new object[] {
                                        sc, shortcutShim, powerGeneric, null });
                                }
                                catch { }
                            }
                            MelonLogger.Msg("Registered /1-/20, /0, /back shortcut commands");
                        }
                        else
                            MelonLogger.Warning("Could not find CommandCallback or RegisterAdminCommand in AdminMod");
                    }
                    else
                        MelonLogger.Warning("AdminMod not found — chat commands (!rebalance, !b) disabled");
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
                _additionalSpawn = config["additional_spawn"]?.Value<bool>() ?? false;
                _discordAutoPost = config["discord_auto_post"]?.Value<bool>() ?? false;
                _discordWebhookUrl = config["discord_webhook_url"]?.Value<string>() ?? "";
                _watchdogEnabled = config["watchdog_enabled"]?.Value<bool>() ?? true;
                // _revertOnRoundEnd removed — always false (reverting breaks starter structure overrides)
                _healthMultEnabled = config["health_mult_enabled"]?.Value<bool>() ?? true;

                // Read debug logging from Admin mod's MelonPreference
                try
                {
                    var cat = MelonLoader.MelonPreferences.GetCategory("Silica");
                    var entry = cat?.GetEntry<bool>("Admin_EnableDebugLogging");
                    _debugLogging = entry?.Value ?? false;
                }
                catch { _debugLogging = false; }
                MelonLogger.Msg($"[CONFIG] Debug logging: {_debugLogging}");

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
                _strafeSpeedMultipliers.Clear();
                _flySpeedMultipliers.Clear();
                _runSpeedMultipliers.Clear();
                _sprintSpeedMultipliers.Clear();
                _splashRadiusMultipliers.Clear();
                _projectileOverrides.Clear();
                _proximityOverrides.Clear();
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
                        float strafeSpeedMult = overrides["strafe_speed_mult"]?.Value<float>() ?? 1.0f;
                        float flySpeedMult = overrides["fly_speed_mult"]?.Value<float>() ?? 1.0f;
                        float runSpeedMult = overrides["run_speed_mult"]?.Value<float>() ?? 1.0f;
                        float sprintSpeedMult = overrides["sprint_speed_mult"]?.Value<float>() ?? 1.0f;
                        float dispenseTimeout = overrides["dispense_timeout"]?.Value<float>() ?? -1f;

                        // Per-damage-subtype multipliers (4 sub-types × 3 scopes)
                        float impactDmgMult = overrides["impact_damage_mult"]?.Value<float>() ?? 1.0f;
                        float splashDmgMult = overrides["splash_damage_mult"]?.Value<float>() ?? 1.0f;
                        float penDmgMult = overrides["penetrating_damage_mult"]?.Value<float>() ?? 1.0f;
                        float ricochetDmgMult = overrides["ricochet_damage_mult"]?.Value<float>() ?? 1.0f;
                        float priImpactDmgMult = overrides["pri_impact_damage_mult"]?.Value<float>() ?? 1.0f;
                        float priSplashDmgMult = overrides["pri_splash_damage_mult"]?.Value<float>() ?? 1.0f;
                        float priPenDmgMult = overrides["pri_penetrating_damage_mult"]?.Value<float>() ?? 1.0f;
                        float priRicochetDmgMult = overrides["pri_ricochet_damage_mult"]?.Value<float>() ?? 1.0f;
                        float secImpactDmgMult = overrides["sec_impact_damage_mult"]?.Value<float>() ?? 1.0f;
                        float secSplashDmgMult = overrides["sec_splash_damage_mult"]?.Value<float>() ?? 1.0f;
                        float secPenDmgMult = overrides["sec_penetrating_damage_mult"]?.Value<float>() ?? 1.0f;
                        float secRicochetDmgMult = overrides["sec_ricochet_damage_mult"]?.Value<float>() ?? 1.0f;

                        // Splash radius multipliers (3 fields × 3 scopes)
                        float splashRadMaxMult = overrides["splash_radius_max_mult"]?.Value<float>() ?? 1.0f;
                        float splashRadMinMult = overrides["splash_radius_min_mult"]?.Value<float>() ?? 1.0f;
                        float splashRadPowMult = overrides["splash_radius_pow_mult"]?.Value<float>() ?? 1.0f;
                        float priSplashRadMaxMult = overrides["pri_splash_radius_max_mult"]?.Value<float>() ?? 1.0f;
                        float priSplashRadMinMult = overrides["pri_splash_radius_min_mult"]?.Value<float>() ?? 1.0f;
                        float priSplashRadPowMult = overrides["pri_splash_radius_pow_mult"]?.Value<float>() ?? 1.0f;
                        float secSplashRadMaxMult = overrides["sec_splash_radius_max_mult"]?.Value<float>() ?? 1.0f;
                        float secSplashRadMinMult = overrides["sec_splash_radius_min_mult"]?.Value<float>() ?? 1.0f;
                        float secSplashRadPowMult = overrides["sec_splash_radius_pow_mult"]?.Value<float>() ?? 1.0f;

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
                        if (Math.Abs(strafeSpeedMult - 1.0f) > 0.001f)
                            _strafeSpeedMultipliers[unitName] = strafeSpeedMult;
                        if (Math.Abs(flySpeedMult - 1.0f) > 0.001f)
                            _flySpeedMultipliers[unitName] = flySpeedMult;
                        if (Math.Abs(runSpeedMult - 1.0f) > 0.001f)
                            _runSpeedMultipliers[unitName] = runSpeedMult;
                        if (Math.Abs(sprintSpeedMult - 1.0f) > 0.001f)
                            _sprintSpeedMultipliers[unitName] = sprintSpeedMult;
                        if (dispenseTimeout >= 0)
                            _dispenseTimeout = dispenseTimeout; // global — applies to all dispensers of this unit

                        // Store per-weapon multipliers with "pri:"/"sec:" prefix keys
                        if (Math.Abs(priDamageMult - 1.0f) > 0.001f) _damageMultipliers["pri:" + unitName] = priDamageMult;
                        if (Math.Abs(secDamageMult - 1.0f) > 0.001f) _damageMultipliers["sec:" + unitName] = secDamageMult;
                        // Per-damage-subtype composite keys: "subtype:unitName" and "weapon:subtype:unitName"
                        if (Math.Abs(impactDmgMult - 1.0f) > 0.001f) _damageMultipliers["impact:" + unitName] = impactDmgMult;
                        if (Math.Abs(splashDmgMult - 1.0f) > 0.001f) _damageMultipliers["splash:" + unitName] = splashDmgMult;
                        if (Math.Abs(penDmgMult - 1.0f) > 0.001f) _damageMultipliers["penetrating:" + unitName] = penDmgMult;
                        if (Math.Abs(ricochetDmgMult - 1.0f) > 0.001f) _damageMultipliers["ricochet:" + unitName] = ricochetDmgMult;
                        if (Math.Abs(priImpactDmgMult - 1.0f) > 0.001f) _damageMultipliers["pri:impact:" + unitName] = priImpactDmgMult;
                        if (Math.Abs(priSplashDmgMult - 1.0f) > 0.001f) _damageMultipliers["pri:splash:" + unitName] = priSplashDmgMult;
                        if (Math.Abs(priPenDmgMult - 1.0f) > 0.001f) _damageMultipliers["pri:penetrating:" + unitName] = priPenDmgMult;
                        if (Math.Abs(priRicochetDmgMult - 1.0f) > 0.001f) _damageMultipliers["pri:ricochet:" + unitName] = priRicochetDmgMult;
                        if (Math.Abs(secImpactDmgMult - 1.0f) > 0.001f) _damageMultipliers["sec:impact:" + unitName] = secImpactDmgMult;
                        if (Math.Abs(secSplashDmgMult - 1.0f) > 0.001f) _damageMultipliers["sec:splash:" + unitName] = secSplashDmgMult;
                        if (Math.Abs(secPenDmgMult - 1.0f) > 0.001f) _damageMultipliers["sec:penetrating:" + unitName] = secPenDmgMult;
                        if (Math.Abs(secRicochetDmgMult - 1.0f) > 0.001f) _damageMultipliers["sec:ricochet:" + unitName] = secRicochetDmgMult;
                        // Splash radius composite keys: "field:unitName" and "weapon:field:unitName"
                        if (Math.Abs(splashRadMaxMult - 1.0f) > 0.001f) _splashRadiusMultipliers["max:" + unitName] = splashRadMaxMult;
                        if (Math.Abs(splashRadMinMult - 1.0f) > 0.001f) _splashRadiusMultipliers["min:" + unitName] = splashRadMinMult;
                        if (Math.Abs(splashRadPowMult - 1.0f) > 0.001f) _splashRadiusMultipliers["pow:" + unitName] = splashRadPowMult;
                        if (Math.Abs(priSplashRadMaxMult - 1.0f) > 0.001f) _splashRadiusMultipliers["pri:max:" + unitName] = priSplashRadMaxMult;
                        if (Math.Abs(priSplashRadMinMult - 1.0f) > 0.001f) _splashRadiusMultipliers["pri:min:" + unitName] = priSplashRadMinMult;
                        if (Math.Abs(priSplashRadPowMult - 1.0f) > 0.001f) _splashRadiusMultipliers["pri:pow:" + unitName] = priSplashRadPowMult;
                        if (Math.Abs(secSplashRadMaxMult - 1.0f) > 0.001f) _splashRadiusMultipliers["sec:max:" + unitName] = secSplashRadMaxMult;
                        if (Math.Abs(secSplashRadMinMult - 1.0f) > 0.001f) _splashRadiusMultipliers["sec:min:" + unitName] = secSplashRadMinMult;
                        if (Math.Abs(secSplashRadPowMult - 1.0f) > 0.001f) _splashRadiusMultipliers["sec:pow:" + unitName] = secSplashRadPowMult;
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
                                    // Parse "proximity" sub-object for ProjectileProximityDetonation overrides
                                    if (string.Equals(fieldKvp.Key, "proximity", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var proxObj = fieldKvp.Value as JObject;
                                        if (proxObj != null)
                                        {
                                            var proxFields = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                                            foreach (var pKvp in proxObj)
                                            {
                                                if (pKvp.Value == null) continue;
                                                // Booleans stored as 0/1 floats
                                                if (pKvp.Value.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                                                    proxFields[pKvp.Key] = pKvp.Value.Value<bool>() ? 1f : 0f;
                                                else
                                                {
                                                    float? pVal = pKvp.Value.Value<float>();
                                                    if (pVal.HasValue)
                                                        proxFields[pKvp.Key] = pVal.Value;
                                                }
                                            }
                                            if (proxFields.Count > 0)
                                                _proximityOverrides[projName] = proxFields;
                                        }
                                        continue;
                                    }
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
            try
            {
                // Try to copy from Si_UnitBalance_Config_Default.json in config dir or Mods dir
                string defaultPath = Path.Combine(Path.GetDirectoryName(_configPath)!, "Si_UnitBalance_Config_Default.json");
                if (!File.Exists(defaultPath))
                {
                    // Fallback: check Mods directory for legacy placement
                    string modsDefault = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Si_UnitBalance_Config_Default.json");
                    if (File.Exists(modsDefault)) defaultPath = modsDefault;
                }
                if (File.Exists(defaultPath))
                {
                    File.Copy(defaultPath, _configPath);
                    MelonLogger.Msg($"Created config from default template: {defaultPath}");
                    return;
                }

                // Fallback: minimal inline default
                string defaultJson = @"{
    ""enabled"": true,
    ""dump_fields"": false,
    ""shrimp_disable_aim"": false,
    ""description"": ""Vanilla base config. All multipliers at 1.00 = no change. See Si_UnitBalance_Config_Default.json for comprehensive template with all units."",
    ""tech_time"": { ""tier_1"": 30, ""tier_2"": 30, ""tier_3"": 30, ""tier_4"": 30, ""tier_5"": 30, ""tier_6"": 30, ""tier_7"": 30, ""tier_8"": 30 },
    ""units"": {}
}";
                File.WriteAllText(_configPath, defaultJson);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Failed to create default config: {ex.Message}");
            }
        }
    }
}

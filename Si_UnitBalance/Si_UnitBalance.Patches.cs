using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
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
                    ApplyStrafeSpeedOverrides(omReady);
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
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using MelonLoader;
using Newtonsoft.Json.Linq;

namespace Si_UnitBalance
{
    // =============================================
    // Player-facing read-only stats inspector — "/stats"
    // Any player (not just admins) can type /stats to inspect the live parameters of the unit they
    // currently control (or their soldier when on foot). Navigation: /1../N pick a category, /back
    // to category list, /0 (or /stats again) to close.
    //
    // Hook model: /stats and the /1-/20 /back /0 navigation are registered via AdminMod's
    // PlayerMethods.RegisterPlayerCommand (anyone can use, hidden from public chat). The nav
    // commands share the SAME handler as the admin !b editor (OnMenuShortcut -> ProcessMenuInput);
    // ProcessMenuInput gates write access by checking which menu state the caller is in:
    //   - _menuStates (entered via admin-only !b) -> full edit
    //   - _statsStates (entered via /stats)       -> read-only display only (this file)
    //   - no state                                 -> silently ignored
    // =============================================
    public partial class UnitBalance
    {
        private class StatsViewState
        {
            public string UnitName;        // locked config name of the controlled unit
            public string[] GroupNames;    // cached param-group names
            public string[][] GroupKeys;   // cached param-group keys
            public int Level;              // 0 = category list, 1 = viewing a group
            public int GroupIdx;
        }

        private static readonly Dictionary<long, StatsViewState> _statsStates = new Dictionary<long, StatsViewState>();

        // Player-command callback for "/stats" (signature matches the shim's (object player, string args)).
        internal static void OnStatsCommand(object player, string args)
        {
            if (player == null) return;
            try
            {
                long key = GetPlayerKey(player);
                OpenPlayerStats(player, key);
            }
            catch (Exception ex) { MelonLogger.Warning($"[Stats] /stats handler error: {ex.Message}"); }
        }

        // Called from ProcessMenuInput when the player is in _statsStates.
        // Returns true if the command was recognized + handled as stats navigation.
        internal static bool TryHandleStatsNavCommand(object player, long key, string cmd)
        {
            if (!_statsStates.TryGetValue(key, out var state)) return false;
            if (cmd == null) return false;
            cmd = cmd.Trim();
            if (cmd.Length == 0) return false;

            if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase) || cmd == "0")
            {
                _statsStates.Remove(key);
                SendChatToPlayer(player, _chatPrefix + _dimColor + "Closed unit stats.</color>");
                return true;
            }

            if (cmd.Equals("back", StringComparison.OrdinalIgnoreCase))
            {
                if (state.Level == 1) { state.Level = 0; ShowStatsCategories(player, state); }
                else { _statsStates.Remove(key); SendChatToPlayer(player, _chatPrefix + _dimColor + "Closed unit stats.</color>"); }
                return true;
            }

            if (int.TryParse(cmd, out int sel) && sel >= 1)
            {
                if (sel - 1 < state.GroupNames.Length)
                {
                    state.Level = 1;
                    state.GroupIdx = sel - 1;
                    ShowStatsGroup(player, state);
                }
                return true;
            }

            return false;
        }

        private static void OpenPlayerStats(object player, long key)
        {
            string unitName = ResolveControlledUnitName(player);
            if (unitName == null)
            {
                _statsStates.Remove(key);
                SendChatToPlayer(player, _chatPrefix + _dimColor + "Take control of a unit first, then type /stats.</color>");
                return;
            }

            GetUnitParamGroups(unitName, out string[] groupNames, out string[][] groupKeys);
            if (groupNames == null || groupNames.Length == 0)
            {
                _statsStates.Remove(key);
                SendChatToPlayer(player, _chatPrefix + _dimColor + "No inspectable parameters for " + unitName + ".</color>");
                return;
            }

            var state = new StatsViewState
            {
                UnitName = unitName,
                GroupNames = groupNames,
                GroupKeys = groupKeys,
                Level = 0,
                GroupIdx = 0,
            };
            _statsStates[key] = state;
            ShowStatsCategories(player, state);
        }

        // Player.ControlledUnit -> ObjectInfo -> config name (works for vehicles, creatures, and on-foot soldiers).
        private static string ResolveControlledUnitName(object playerObj)
        {
            try
            {
                var player = playerObj as Player;
                if (player == null) return null;
                Unit unit = player.ControlledUnit;
                if (unit == null) return null;
                ObjectInfo info = unit.ObjectInfo;
                if (info == null) return null;
                string resolved = ResolveConfigName(info.DisplayName, info.name);
                return string.IsNullOrEmpty(resolved) ? null : resolved;
            }
            catch { return null; }
        }

        private static void ShowStatsCategories(object player, StatsViewState state)
        {
            SendChatToPlayer(player, _chatPrefix + _headerColor + state.UnitName + " — Stats</color>");
            for (int i = 0; i < state.GroupNames.Length; i++)
                SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + state.GroupNames[i]);
            SendChatToPlayer(player, _chatPrefix + _dimColor + "/1../" + state.GroupNames.Length + " to view · /0 to close</color>");
        }

        private static void ShowStatsGroup(object player, StatsViewState state)
        {
            if (state.GroupIdx < 0 || state.GroupIdx >= state.GroupNames.Length)
            {
                state.Level = 0; ShowStatsCategories(player, state); return;
            }

            string unitName = state.UnitName;
            string groupName = state.GroupNames[state.GroupIdx];
            string[] keys = state.GroupKeys[state.GroupIdx];

            SendChatToPlayer(player, _chatPrefix + _headerColor + unitName + " > " + groupName + "</color>");

            var baseVals = GetBaseValues(unitName, keys);
            // Supplement any keys missing from the vanilla cache with a live prefab read,
            // so the "(vanilla X)" figure shows up reliably next to changed multipliers.
            try
            {
                var missing = new List<string>();
                foreach (string k in keys) if (!baseVals.ContainsKey(k)) missing.Add(k);
                if (missing.Count > 0)
                {
                    var live = GetBaseValuesLive(unitName, missing.ToArray());
                    foreach (var kv in live) if (!baseVals.ContainsKey(kv.Key)) baseVals[kv.Key] = kv.Value;
                }
            }
            catch { }
            JObject unitConfig = null;
            try { unitConfig = ReadUnitConfigFromJson(unitName); } catch { }

            int shown = 0;
            foreach (string key in keys)
            {
                string line = FormatStatLine(key, baseVals, unitConfig);
                if (line == null) continue;
                SendChatToPlayer(player, _chatPrefix + line);
                shown++;
            }
            if (shown == 0)
                SendChatToPlayer(player, _chatPrefix + _dimColor + "(no readable values)</color>");
            SendChatToPlayer(player, _chatPrefix + _dimColor + "/back to categories · /0 to close</color>");
        }

        // Builds one display line: "Damage: 1000 (vanilla 800, ×1.25)" / "Reload: 3 (unchanged)" / absolute overrides.
        private static string FormatStatLine(string key, Dictionary<string, string> baseVals, JObject unitConfig)
        {
            bool isMult = key.EndsWith("_mult", StringComparison.OrdinalIgnoreCase);
            string display = PrettyStatKey(key);

            string baseStr = null;
            bool hasBase = baseVals != null && baseVals.TryGetValue(key, out baseStr);

            bool hasCfg = unitConfig != null && unitConfig[key] != null;

            if (isMult)
            {
                float mult = 1f;
                if (hasCfg) { try { mult = unitConfig[key].Value<float>(); } catch { mult = 1f; } }

                if (hasBase && float.TryParse(baseStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float baseF))
                {
                    if (Math.Abs(mult - 1f) < 0.0001f)
                        return _itemColor + display + ":</color> " + _valueColor + Fmt(baseF) + "</color> " + _dimColor + "(unchanged)</color>";
                    float cur = baseF * mult;
                    return _itemColor + display + ":</color> " + _valueColor + Fmt(cur) + "</color> " +
                           _dimColor + "(vanilla " + Fmt(baseF) + ", ×" + Fmt(mult) + ")</color>";
                }
                // No numeric base — show the multiplier only if it changes something.
                if (Math.Abs(mult - 1f) < 0.0001f) return null;
                return _itemColor + display + ":</color> " + _valueColor + "×" + Fmt(mult) + "</color>";
            }

            // Absolute override (min_tier, build_radius, target_distance, unit_cap_value, ...)
            if (hasCfg)
            {
                string absVal = unitConfig[key].ToString();
                if (hasBase && !string.Equals(absVal, baseStr, StringComparison.OrdinalIgnoreCase))
                    return _itemColor + display + ":</color> " + _valueColor + absVal + "</color> " +
                           _dimColor + "(vanilla " + baseStr + ")</color>";
                return _itemColor + display + ":</color> " + _valueColor + absVal + "</color>";
            }
            if (hasBase)
                return _itemColor + display + ":</color> " + _valueColor + baseStr + "</color> " + _dimColor + "(unchanged)</color>";

            return null;
        }

        private static string PrettyStatKey(string key)
        {
            string k = key;
            if (k.StartsWith("pri_") || k.StartsWith("sec_")) k = k.Substring(4);
            else if (k.StartsWith("prox_")) k = k.Substring(5);
            if (k.EndsWith("_mult", StringComparison.OrdinalIgnoreCase)) k = k.Substring(0, k.Length - 5);
            k = k.Replace("_", " ").Trim();
            if (k.Length == 0) return key;
            // Title-case each word
            var parts = k.Split(' ');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0)
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + (parts[i].Length > 1 ? parts[i].Substring(1) : "");
            return string.Join(" ", parts);
        }

        private static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

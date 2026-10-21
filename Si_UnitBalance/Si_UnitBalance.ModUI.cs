using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Si_UnitBalance
{
    // =============================================
    // Si_ModUI export — the whole balance as one browsable document
    //
    // The ModUI server calls the public static ModUIData() on every frame build (every few seconds)
    // and embeds the JSON as the mod's `data`; the ModUI client renders {"browse":...} as a three-level
    // browser: config section (SOL — Barracks, ...) > unit > stat groups, with a "changes only" filter.
    // Every player gets it, so it replaces the chat-based /stats walk for ModUI users.
    //
    // Document: {"browse":{"title":"Unit balance","sections":[{"n":"Sol — Barracks","u":[
    //   {"n":"Rifleman","c":1,"g":[{"n":"Health & Production","r":[["Health","150"],["Build Time","15","10","x1.5"]]}]}]}]}}
    // Row = [label, current, vanilla, note]; vanilla/note only when the value differs from vanilla.
    //
    // Cost: the build walks every unit's prefab (same work as CacheVanillaBaseValues) and is cached
    // until the config file changes; the periodic calls return the cached string.
    // =============================================
    public partial class UnitBalance
    {
        private static string _hudDoc;
        private static string _hudDocKey = "";

        public static string ModUIData()
        {
            try
            {
                if (!_configLoaded || string.IsNullOrEmpty(_configPath)) return _hudDoc;
                string key;
                try { key = File.GetLastWriteTimeUtc(_configPath).Ticks.ToString(CultureInfo.InvariantCulture) + "|" + (_enabled ? 1 : 0); }
                catch { key = "nofile|" + (_enabled ? 1 : 0); }
                if (_hudDoc != null && key == _hudDocKey) return _hudDoc;
                // Base values come from the vanilla snapshot taken before the overrides; without it (before the
                // first apply, or right after a map change) keep whatever was built last.
                if (_vanillaBaseCache.Count == 0) return _hudDoc;
                var started = DateTime.UtcNow;
                string doc = BuildHudDocument();
                if (doc == null) return _hudDoc;
                _hudDoc = doc; _hudDocKey = key;
                MelonLogger.Msg($"[HUD] Balance browser document built: {doc.Length / 1024} KB in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
                return _hudDoc;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[HUD] ModUIData failed: " + ex.Message);
                return _hudDoc;
            }
        }

        // Absolute keys where a negative config value means "no override".
        private static readonly HashSet<string> _hudOptionalAbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "min_tier", "unit_cap_value", "build_radius", "deposit_radius", "extraction_radius", "target_distance", "fow_distance",
            "dispense_timeout", "aim_compensation", "pitch_max_up", "pitch_max_down", "ai_lead_velocity", "ai_lead_gravity", "ai_lead_drag"
        };

        private static string BuildHudDocument()
        {
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(_configPath)); }
            catch (Exception ex) { MelonLogger.Warning("[HUD] Config unreadable: " + ex.Message); return null; }
            var units = root["units"] as JObject;
            if (units == null) return null;

            bool releaseInfos = _sharedAllInfos == null;
            if (releaseInfos) _sharedAllInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            try
            {
                var sections = new JArray();
                var global = BuildGlobalSection(root);
                if (global != null) sections.Add(global);

                JArray current = null; string currentName = null;
                foreach (var prop in units.Properties())
                {
                    if (prop.Name.StartsWith("_comment", StringComparison.OrdinalIgnoreCase))
                    {
                        if (current != null && current.Count > 0) sections.Add(new JObject { ["n"] = currentName, ["u"] = current });
                        currentName = SectionTitle(prop.Value?.ToString() ?? prop.Name);
                        current = new JArray();
                        continue;
                    }
                    if (prop.Name.StartsWith("_")) continue;
                    var unitConfig = prop.Value as JObject;
                    if (current == null) { current = new JArray(); currentName = "Units"; }
                    var entry = BuildUnitEntry(prop.Name, _enabled ? unitConfig : null);
                    if (entry != null) current.Add(entry);
                }
                if (current != null && current.Count > 0) sections.Add(new JObject { ["n"] = currentName, ["u"] = current });
                if (sections.Count == 0) return null;

                var browse = new JObject
                {
                    ["title"] = _enabled ? "Unit balance" : "Unit balance (mod disabled: vanilla values)",
                    ["sections"] = sections
                };
                // Top-level commands: decorate the registry entries and add the "default" form the registry cannot see.
                // The !b editing menu stays a proxied chat menu by decision (2026-10-20).
                var commands = new JArray
                {
                    new JObject { ["t"] = "/stats", ["l"] = "Stats of my unit", ["d"] = "Health, speed and weapon values of the unit you control, with the vanilla value next to every change.", ["a"] = false },
                    new JObject { ["t"] = "!rebalance", ["l"] = "Reapply balance", ["d"] = "Re-reads the config and applies all overrides to the running match.", ["a"] = true },
                    new JObject { ["t"] = "!rebalance default", ["l"] = "Revert to vanilla", ["d"] = "Removes every override and restores vanilla values for the running match.", ["a"] = true },
                    new JObject { ["t"] = "!b", ["l"] = "Balance editor (chat menu)", ["d"] = "The numbered chat menu that edits the config live.", ["a"] = true }
                };
                return JsonConvert.SerializeObject(new JObject { ["enabled"] = _enabled, ["browse"] = browse, ["commands"] = commands }, Formatting.None);
            }
            finally { if (releaseInfos) _sharedAllInfos = null; }
        }

        // "========== SOL — Barracks ==========" -> "Sol - Barracks". The config file on disk carries the em dash
        // double-encoded (UTF-8 bytes re-encoded as UTF-8 by an earlier writer), so both spellings are accepted and the
        // output uses a plain hyphen, which every font has.
        private static string SectionTitle(string marker)
        {
            string t = marker.Replace("\u00e2\u20ac\u201d", "\u2014").Replace("\u00e2\u20ac\u201c", "\u2014").Trim().Trim('=').Trim();
            if (t.Length == 0) return "Units";
            var parts = t.Split(new[] { "\u2014", " - " }, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                string faction = parts[0].Trim();
                faction = string.Join("/", faction.Split('/').Select(f => f.Length > 1 ? char.ToUpperInvariant(f[0]) + f.Substring(1).ToLowerInvariant() : f));
                return faction + " - " + parts[1].Trim();
            }
            return t;
        }

        private static JObject BuildUnitEntry(string unitName, JObject unitConfig)
        {
            try
            {
                GetUnitParamGroups(unitName, out string[] groupNames, out string[][] groupKeys);
                if (groupNames == null || groupNames.Length == 0) return null;
                // pri_/sec_range_mult are read by the loader for every unit but are not in the chat menu's key lists.
                for (int g = 0; g < groupNames.Length; g++)
                {
                    if (groupNames[g].StartsWith("Primary Weapon", StringComparison.Ordinal) && !groupKeys[g].Contains("pri_range_mult"))
                        groupKeys[g] = groupKeys[g].Concat(new[] { "pri_range_mult" }).ToArray();
                    else if (groupNames[g].StartsWith("Secondary Weapon", StringComparison.Ordinal) && !groupKeys[g].Contains("sec_range_mult"))
                        groupKeys[g] = groupKeys[g].Concat(new[] { "sec_range_mult" }).ToArray();
                }
                var allKeys = groupKeys.SelectMany(k => k).ToArray();
                var baseVals = GetBaseValues(unitName, allKeys);
                // keys the vanilla snapshot lacks: live prefab read (prefabs keep vanilla values, OM stores overrides apart)
                try
                {
                    var missing = allKeys.Where(k => !baseVals.ContainsKey(k)).ToArray();
                    if (missing.Length > 0)
                        foreach (var kv in GetBaseValuesLive(unitName, missing)) if (!baseVals.ContainsKey(kv.Key)) baseVals[kv.Key] = kv.Value;
                }
                catch { }
                var groups = new JArray();
                int changes = 0;
                for (int g = 0; g < groupNames.Length; g++)
                {
                    var rows = new JArray();
                    foreach (string key in groupKeys[g])
                    {
                        var row = StatRow(key, baseVals, unitConfig);
                        if (row == null) continue;
                        if (row.Count > 2) changes++;
                        rows.Add(row);
                    }
                    if (rows.Count > 0) groups.Add(new JObject { ["n"] = groupNames[g], ["r"] = rows });
                }
                // Per-projectile absolute overrides (projectiles.<name>.<Field>); no vanilla value is known for them.
                if (unitConfig?["projectiles"] is JObject projs)
                {
                    foreach (var pp in projs.Properties())
                    {
                        if (!(pp.Value is JObject fields)) continue;
                        var rows = new JArray();
                        foreach (var fp in fields.Properties())
                        {
                            if (fp.Name.Equals("proximity", StringComparison.OrdinalIgnoreCase) || fp.Value.Type == JTokenType.Null) continue;
                            rows.Add(new JArray(fp.Name, fp.Value.ToString(), "n/a", "absolute"));
                            changes++;
                        }
                        if (rows.Count > 0) groups.Add(new JObject { ["n"] = "Projectile " + pp.Name + " (absolute)", ["r"] = rows });
                    }
                }
                if (groups.Count == 0) return null;
                return new JObject { ["n"] = unitName, ["c"] = changes, ["g"] = groups };
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HUD] {unitName}: {ex.Message}");
                return null;
            }
        }

        // Mirrors FormatStatLine (PlayerStats.cs) as data: [label, current] or [label, current, vanilla, note].
        private static JArray StatRow(string key, Dictionary<string, string> baseVals, JObject unitConfig)
        {
            bool isMult = key.EndsWith("_mult", StringComparison.OrdinalIgnoreCase);
            string label = PrettyStatKey(key);
            string baseStr = null;
            bool hasBase = baseVals != null && baseVals.TryGetValue(key, out baseStr);
            var cfgToken = CfgToken(unitConfig, key);
            bool hasCfg = cfgToken != null && cfgToken.Type != JTokenType.Null;

            if (isMult)
            {
                float mult = 1f;
                if (hasCfg) { try { mult = cfgToken.Value<float>(); } catch { mult = 1f; } }
                bool changed = Math.Abs(mult - 1f) >= 0.0001f;
                if (hasBase && SplitNumber(baseStr, out float baseF, out string unit))
                {
                    if (!changed) return new JArray(label, Fmt(baseF) + unit);
                    return new JArray(label, Fmt(baseF * mult) + unit, Fmt(baseF) + unit, "x" + Fmt(mult));
                }
                if (!changed) return null;
                return new JArray(label, "x" + Fmt(mult), "x1");
            }

            if (hasCfg)
            {
                string absVal = cfgToken.ToString();
                bool noOverride = _hudOptionalAbs.Contains(key) && float.TryParse(absVal, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f < 0;
                if (noOverride) return hasBase ? new JArray(label, Tier(key, baseStr)) : null;
                if (hasBase && !SameNumber(absVal, baseStr)) return new JArray(label, Tier(key, absVal), Tier(key, baseStr));
                return new JArray(label, Tier(key, absVal));
            }
            return hasBase ? new JArray(label, Tier(key, baseStr)) : null;
        }

        // "prox_<Field>" rows are stored as projectiles.<name>.proximity.<Field> in the config (any projectile of the unit).
        private static JToken CfgToken(JObject unitConfig, string key)
        {
            if (unitConfig == null) return null;
            if (!key.StartsWith("prox_", StringComparison.OrdinalIgnoreCase)) return unitConfig[key];
            string field = key.Substring(5);
            if (!(unitConfig["projectiles"] is JObject projs)) return null;
            foreach (var pp in projs.Properties())
                if (pp.Value is JObject fields && fields["proximity"] is JObject prox)
                    foreach (var fp in prox.Properties())
                        if (fp.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) return fp.Value;
            return null;
        }

        // The prefab reports no tier requirement as -1; the game treats that as tier 0.
        private static string Tier(string key, string value) => key == "min_tier" && value == "-1" ? "0" : value;

        // "15.0s" -> 15, "s"; "100" -> 100, ""
        private static bool SplitNumber(string text, out float value, out string unit)
        {
            value = 0; unit = "";
            if (string.IsNullOrEmpty(text)) return false;
            int end = text.Length;
            while (end > 0 && !(char.IsDigit(text[end - 1]) || text[end - 1] == '.')) end--;
            unit = text.Substring(end);
            return float.TryParse(text.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool SameNumber(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out float fa) && float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out float fb))
                return Math.Abs(fa - fb) < 0.0005f;
            return false;
        }

        // Server-wide settings as one entry "Server settings" in a leading section.
        private static JObject BuildGlobalSection(JObject root)
        {
            try
            {
                var groups = new JArray();
                int changes = 0;
                JArray Row(string label, string value, string vanilla = null, string note = null)
                {
                    var r = new JArray(label, value);
                    if (vanilla != null && !SameNumber(value, vanilla)) { r.Add(vanilla); if (note != null) r.Add(note); changes++; }
                    return r;
                }
                void Group(string name, JArray rows) { if (rows.Count > 0) groups.Add(new JObject { ["n"] = name, ["r"] = rows }); }
                string Num(JToken t) => t == null || t.Type == JTokenType.Null ? "" : t.Type == JTokenType.Boolean ? ((bool)t ? "on" : "off") : t.ToString();

                if (root["tech_time"] is JObject tech)
                {
                    var rows = new JArray();
                    for (int tier = 1; tier <= 8; tier++)
                    {
                        var t = tech["tier_" + tier]; if (t == null) continue;
                        rows.Add(Row("Tier " + tier + " research (s)", Num(t), "30"));
                    }
                    Group("Tech research time", rows);
                }
                if (root["tech_cost"] is JObject techCost)
                {
                    var rows = new JArray();
                    for (int tier = 1; tier <= 8; tier++)
                    {
                        var t = techCost["tier_" + tier]; if (t == null || Num(t) == "-1") continue;
                        rows.Add(Row("Tier " + tier + " research cost", Num(t)));
                    }
                    Group("Tech research cost", rows);
                }
                if (root["health_mult_enabled"] != null)
                {
                    var rows = new JArray();
                    rows.Add(Row("Health multipliers active", Num(root["health_mult_enabled"]), "on"));
                    Group("Health", rows);
                }
                if (root["units"]?["_teleport"] is JObject tp)
                {
                    var rows = new JArray();
                    if (tp["cooldown"] != null) rows.Add(Row("Teleport cooldown (s)", Num(tp["cooldown"]), "120"));
                    if (tp["duration"] != null) rows.Add(Row("Teleport duration (s)", Num(tp["duration"]), "5"));
                    Group("Teleport", rows);
                }
                {
                    var rows = new JArray();
                    if (root["unit_cap_mult"] is JObject ucm)
                    {
                        foreach (string k in new[] { "two_way", "three_way" })
                        {
                            var t = ucm[k]; if (t == null) continue;
                            string v = Num(t);
                            rows.Add(v == "-1" ? Row(("Unit cap multiplier " + k).Replace("_", " "), "server setting") : Row(("Unit cap multiplier " + k).Replace("_", " "), v == "0" ? "unlimited" : "x" + v, "server setting"));
                        }
                    }
                    if (root["count_caps"] is JObject caps)
                        foreach (var p in caps.Properties()) if (!p.Name.StartsWith("_")) rows.Add(Row("Max " + p.Name + " per team", Num(p.Value)));
                    if (root["player_infantry_ignore_cap"] != null) rows.Add(Row("Player infantry ignores cap", Num(root["player_infantry_ignore_cap"]), "off"));
                    Group("Caps", rows);
                }
                if (root["decay"] is JObject decay)
                {
                    var rows = new JArray();
                    foreach (string side in new[] { "human", "alien" })
                    {
                        if (!(decay[side] is JObject d)) continue;
                        string label = char.ToUpperInvariant(side[0]) + side.Substring(1);
                        if (d["enabled"] != null) rows.Add(Row(label + " decay", Num(d["enabled"]), "on"));
                        if (d["delay"] != null && Num(d["delay"]) != "-1") rows.Add(Row(label + " decay delay (s)", Num(d["delay"]), "10"));
                        if (d["tick"] != null && Num(d["tick"]) != "-1") rows.Add(Row(label + " decay tick (s)", Num(d["tick"]), "5"));
                        if (d["amount_pct"] != null && Num(d["amount_pct"]) != "-1") rows.Add(Row(label + " decay per tick", Num(d["amount_pct"]), "0.01"));
                        if (d["randomize_pct"] != null && Num(d["randomize_pct"]) != "-1") rows.Add(Row(label + " decay randomisation", Num(d["randomize_pct"]), "0"));
                        if (d["keep_production"] != null) rows.Add(Row(label + " unlinked structures produce", Num(d["keep_production"]), "off"));
                    }
                    Group("Structure decay", rows);
                }
                {
                    var rows = new JArray();
                    if (root["alien_nest_only_build"] != null) rows.Add(Row("Build only around Nests", Num(root["alien_nest_only_build"]), "off"));
                    if (root["alien_nest_reach_radius"] != null) rows.Add(Row("Nest build radius", Num(root["alien_nest_reach_radius"])));
                    if (root["alien_nest_finished_anchor_only"] != null) rows.Add(Row("Only finished Nests anchor", Num(root["alien_nest_finished_anchor_only"]), "off"));
                    if (root["alien_queens_max"] != null) rows.Add(Row("Max Queens", Num(root["alien_queens_max"]) == "-1" ? "vanilla" : Num(root["alien_queens_max"]) == "0" ? "one per Nest" : Num(root["alien_queens_max"])));
                    if (root["alien_queen_logistics_per_nest"] != null) rows.Add(Row("Queen logistics per Nest", Num(root["alien_queen_logistics_per_nest"]), "off"));
                    if (root["shrimp_disable_aim"] != null) rows.Add(Row("Shrimps do not shoot", Num(root["shrimp_disable_aim"]), "off"));
                    Group("Alien rules", rows);
                }
                {
                    var rows = new JArray();
                    if (root["additional_spawn"] != null) rows.Add(Row("Extra starting transports", Num(root["additional_spawn"]), "off"));
                    if (root["game_version"] != null) rows.Add(Row("Config calibrated for", Num(root["game_version"])));
                    Group("Other", rows);
                }
                if (groups.Count == 0) return null;
                var entry = new JObject { ["n"] = "Server settings", ["c"] = changes, ["g"] = groups };
                return new JObject { ["n"] = "Global", ["u"] = new JArray(entry) };
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[HUD] Global section: " + ex.Message);
                return null;
            }
        }
    }
}

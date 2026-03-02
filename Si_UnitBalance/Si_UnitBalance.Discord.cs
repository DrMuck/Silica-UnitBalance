using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static List<string> _discordMessageIds = new List<string>();

        // Section ordering for Discord output — maps _comment keys to display names
        private static readonly (string commentKey, string displayName)[] _discordSections =
        {
            ("_comment_sol_barracks",   "Sol -- Barracks"),
            ("_comment_sol_lf",         "Sol -- Light Factory"),
            ("_comment_sol_hf",         "Sol -- Heavy Factory"),
            ("_comment_sol_uhf",        "Sol -- Ultra Heavy Factory"),
            ("_comment_sol_air",        "Sol -- Air Factory"),
            ("_comment_struct",         "Structures (Sol/Centauri)"),
            ("_comment_cen_barracks",   "Centauri -- Barracks"),
            ("_comment_cen_lf",         "Centauri -- Light Factory"),
            ("_comment_cen_hf",         "Centauri -- Heavy Factory"),
            ("_comment_cen_uhf",        "Centauri -- Ultra Heavy Factory"),
            ("_comment_cen_air",        "Centauri -- Air Factory"),
            ("_comment_htp",            "Hover Bike"),
            ("_comment_alien_lesser",   "Alien -- Lesser Spawning Cyst"),
            ("_comment_alien_greater",  "Alien -- Greater Spawning Cyst"),
            ("_comment_alien_grand",    "Alien -- Grand Spawning Cyst"),
            ("_comment_alien_colossal", "Alien -- Colossal Spawning Cyst"),
            ("_comment_alien_nest",     "Alien -- Nest"),
            ("_comment_alien_struct",   "Alien -- Structures"),
        };

        // Parameters to skip when comparing (annotations, not real values)
        private static readonly HashSet<string> _skipParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_base", "_pri_weapon", "_sec_weapon", "_base_speed", "_base_sense",
            "_weapon", "_base_movement", "_note"
        };

        /// <summary>
        /// Orchestrates building and posting balance changes to Discord.
        /// </summary>
        private static void PostBalanceChangesToDiscord()
        {
            if (string.IsNullOrEmpty(_discordWebhookUrl))
            {
                MelonLogger.Warning("[Discord] No webhook URL configured. Set 'discord_webhook_url' in config.");
                return;
            }

            try
            {
                LoadMessageIds();
                string serverName = GetServerName();
                var changes = BuildBalanceChangeSummary();
                if (changes.Count == 0)
                {
                    MelonLogger.Msg("[Discord] No balance changes vs vanilla.");
                    // Delete any existing messages since balance is vanilla now
                    if (_discordMessageIds.Count > 0)
                    {
                        DeleteOldMessages(_discordMessageIds);
                        _discordMessageIds.Clear();
                        SaveMessageIds();
                        MelonLogger.Msg("[Discord] Cleaned up old messages (balance reverted to vanilla).");
                    }
                    return;
                }

                var embeds = FormatDiscordEmbeds(changes, serverName);
                PostEmbedsToDiscord(embeds);
                SavePushSnapshot();
                MelonLogger.Msg($"[Discord] Posted {changes.Count} balance changes to Discord.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Discord] Failed to post: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the server name via reflection on NetworkGameServer.
        /// </summary>
        private static string GetServerName()
        {
            try
            {
                Type ngsType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    ngsType = asm.GetType("NetworkGameServer");
                    if (ngsType != null) break;
                }
                if (ngsType != null)
                {
                    var method = ngsType.GetMethod("GetServerName", BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        var result = method.Invoke(null, null);
                        if (result is string name && !string.IsNullOrEmpty(name))
                            return name;
                    }
                }
            }
            catch { }
            return "Silica Server";
        }

        /// <summary>
        /// Compares active config vs default config. Returns list of changes grouped by section.
        /// Each change has: sectionName, unitName, paramName, defaultVal, activeVal, isNew (vs last push).
        /// </summary>
        private static List<BalanceChange> BuildBalanceChangeSummary()
        {
            string configDir = Path.GetDirectoryName(_configPath)!;
            string defaultPath = Path.Combine(configDir, "Si_UnitBalance_Config_Default.json");
            string lastPushPath = Path.Combine(configDir, "Si_UnitBalance_LastPush.json");

            if (!File.Exists(defaultPath) || !File.Exists(_configPath))
                return new List<BalanceChange>();

            var defaultCfg = JObject.Parse(File.ReadAllText(defaultPath));
            var activeCfg = JObject.Parse(File.ReadAllText(_configPath));
            JObject lastPushCfg = null;
            if (File.Exists(lastPushPath))
                lastPushCfg = JObject.Parse(File.ReadAllText(lastPushPath));

            var changes = new List<BalanceChange>();

            // --- Global settings ---
            CompareGlobalSettings(defaultCfg, activeCfg, lastPushCfg, changes);

            // --- Per-unit changes ---
            var defaultUnits = defaultCfg["units"] as JObject;
            var activeUnits = activeCfg["units"] as JObject;
            var lastPushUnits = lastPushCfg?["units"] as JObject;

            if (defaultUnits == null || activeUnits == null)
                return changes;

            // Build section map: unit name -> section display name
            var unitSectionMap = BuildUnitSectionMap(defaultUnits);

            // Compare _teleport pseudo-unit
            ComparePseudoUnit("_teleport", "Global Settings", defaultUnits, activeUnits, lastPushUnits, changes);

            // Compare real units
            foreach (var kvp in activeUnits)
            {
                string unitName = kvp.Key;
                if (unitName.StartsWith("_")) continue;

                var activeUnit = kvp.Value as JObject;
                var defaultUnit = defaultUnits[unitName] as JObject;
                if (activeUnit == null) continue;

                string section = unitSectionMap.ContainsKey(unitName) ? unitSectionMap[unitName] : "Other";

                // Extract vanilla base info from default config
                string baseInfo = defaultUnit?["_base"]?.Value<string>();

                if (defaultUnit == null)
                {
                    // Unit exists in active but not default — all params are changes
                    foreach (var param in activeUnit)
                    {
                        if (param.Key.StartsWith("_") || _skipParams.Contains(param.Key)) continue;
                        bool isNew = IsParamNew(unitName, param.Key, param.Value, lastPushUnits);
                        changes.Add(new BalanceChange(section, unitName, param.Key, "-", param.Value.ToString(), isNew, baseInfo));
                    }
                    continue;
                }

                foreach (var param in activeUnit)
                {
                    if (param.Key.StartsWith("_") || _skipParams.Contains(param.Key)) continue;
                    var defaultVal = defaultUnit[param.Key];
                    if (defaultVal == null) continue;

                    if (!TokensEqual(param.Value, defaultVal))
                    {
                        bool isNew = IsParamNew(unitName, param.Key, param.Value, lastPushUnits);
                        changes.Add(new BalanceChange(section, unitName, param.Key,
                            FormatValue(defaultVal), FormatValue(param.Value), isNew, baseInfo));
                    }
                }
            }

            return changes;
        }

        private static void CompareGlobalSettings(JObject defaultCfg, JObject activeCfg, JObject lastPushCfg, List<BalanceChange> changes)
        {
            // Tech time
            var defaultTech = defaultCfg["tech_time"] as JObject;
            var activeTech = activeCfg["tech_time"] as JObject;
            var lastPushTech = lastPushCfg?["tech_time"] as JObject;

            if (defaultTech != null && activeTech != null)
            {
                for (int tier = 1; tier <= 8; tier++)
                {
                    string key = $"tier_{tier}";
                    var dv = defaultTech[key];
                    var av = activeTech[key];
                    if (dv != null && av != null && !TokensEqual(dv, av))
                    {
                        bool isNew = lastPushTech == null || !TokensEqual(av, lastPushTech[key]);
                        changes.Add(new BalanceChange("Global Settings", "Tech Time", key,
                            FormatValue(dv), FormatValue(av), isNew));
                    }
                }
            }

            // Bool toggles
            CompareBoolSetting("additional_spawn", "Additional Spawn", defaultCfg, activeCfg, lastPushCfg, changes);
            CompareBoolSetting("shrimp_disable_aim", "Shrimp Disable Aim", defaultCfg, activeCfg, lastPushCfg, changes);
        }

        private static void CompareBoolSetting(string key, string displayName, JObject defaultCfg, JObject activeCfg, JObject lastPushCfg, List<BalanceChange> changes)
        {
            bool dv = defaultCfg[key]?.Value<bool>() ?? false;
            bool av = activeCfg[key]?.Value<bool>() ?? false;
            if (dv != av)
            {
                bool lastVal = lastPushCfg?[key]?.Value<bool>() ?? dv;
                bool isNew = av != lastVal;
                changes.Add(new BalanceChange("Global Settings", displayName, "",
                    dv.ToString(), av.ToString(), isNew));
            }
        }

        private static void ComparePseudoUnit(string pseudoName, string section, JObject defaultUnits, JObject activeUnits, JObject lastPushUnits, List<BalanceChange> changes)
        {
            var activeObj = activeUnits?[pseudoName] as JObject;
            var defaultObj = defaultUnits?[pseudoName] as JObject;
            if (activeObj == null) return;

            foreach (var param in activeObj)
            {
                if (param.Key.StartsWith("_")) continue;
                var defaultVal = defaultObj?[param.Key];
                string dv = defaultVal != null ? FormatValue(defaultVal) : "-";
                string av = FormatValue(param.Value);
                if (dv != av)
                {
                    bool isNew = IsParamNew(pseudoName, param.Key, param.Value, lastPushUnits);
                    string displayName = pseudoName.TrimStart('_');
                    displayName = char.ToUpper(displayName[0]) + displayName.Substring(1);
                    changes.Add(new BalanceChange(section, displayName, param.Key, dv, av, isNew));
                }
            }
        }

        /// <summary>
        /// Builds a map from unit name to section display name based on comment ordering in default config.
        /// </summary>
        private static Dictionary<string, string> BuildUnitSectionMap(JObject defaultUnits)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string currentSection = "Other";
            int sectionIdx = 0;

            foreach (var kvp in defaultUnits)
            {
                if (kvp.Key.StartsWith("_comment"))
                {
                    // Find matching display name
                    foreach (var s in _discordSections)
                    {
                        if (s.commentKey == kvp.Key)
                        {
                            currentSection = s.displayName;
                            break;
                        }
                    }
                }
                else if (!kvp.Key.StartsWith("_"))
                {
                    map[kvp.Key] = currentSection;
                }
            }

            return map;
        }

        private static bool IsParamNew(string unitName, string paramName, JToken activeVal, JObject lastPushUnits)
        {
            if (lastPushUnits == null) return true; // No previous push — everything is new
            var lastUnit = lastPushUnits[unitName] as JObject;
            if (lastUnit == null) return true;
            var lastVal = lastUnit[paramName];
            if (lastVal == null) return true;
            return !TokensEqual(activeVal, lastVal);
        }

        /// <summary>
        /// Numeric-aware equality: treats 1 (int) and 1.0 (float) as equal.
        /// </summary>
        private static bool TokensEqual(JToken a, JToken b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            // If both are numeric, compare as double
            if ((a.Type == JTokenType.Integer || a.Type == JTokenType.Float) &&
                (b.Type == JTokenType.Integer || b.Type == JTokenType.Float))
            {
                return Math.Abs(a.Value<double>() - b.Value<double>()) < 0.0001;
            }
            return JToken.DeepEquals(a, b);
        }

        private static string FormatValue(JToken token)
        {
            if (token.Type == JTokenType.Float)
            {
                float f = token.Value<float>();
                return f.ToString("G");
            }
            return token.ToString();
        }

        /// <summary>
        /// Formats changes into Discord embed JSON objects, splitting by section to respect 4096 char limit.
        /// </summary>
        private static List<JObject> FormatDiscordEmbeds(List<BalanceChange> changes, string serverName)
        {
            var embeds = new List<JObject>();

            // Group changes by section, preserving order from _discordSections
            var sectionOrder = new List<string>();
            var sectionChanges = new Dictionary<string, List<BalanceChange>>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in changes)
            {
                if (!sectionChanges.ContainsKey(c.Section))
                {
                    sectionOrder.Add(c.Section);
                    sectionChanges[c.Section] = new List<BalanceChange>();
                }
                sectionChanges[c.Section].Add(c);
            }

            // Build content blocks per section
            var blocks = new List<string>();
            foreach (var section in sectionOrder)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# {section}");
                sb.AppendLine("```diff");

                // Group by unit within section to show base info once per unit
                string lastUnit = null;
                foreach (var c in sectionChanges[section])
                {
                    // Show vanilla base info header when entering a new unit
                    if (c.UnitName != lastUnit && !string.IsNullOrEmpty(c.BaseInfo))
                    {
                        sb.AppendLine($"  [{c.UnitName}] ({c.BaseInfo})");
                        lastUnit = c.UnitName;
                    }
                    else if (c.UnitName != lastUnit)
                    {
                        lastUnit = c.UnitName;
                    }

                    string prefix = c.IsNew ? "+ " : "  ";
                    string paramPart = string.IsNullOrEmpty(c.ParamName) ? "" : $".{c.ParamName}";
                    string line = $"{prefix}{c.UnitName}{paramPart}: {c.DefaultVal} > {c.ActiveVal}";
                    sb.AppendLine(line);
                }

                sb.AppendLine("```");
                blocks.Add(sb.ToString());
            }

            // Combine blocks into embeds, respecting 4096 char limit per embed description
            const int maxDesc = 3900;
            var currentDesc = new StringBuilder();
            bool isFirst = true;

            foreach (var block in blocks)
            {
                // If a single block exceeds the limit, split it by lines
                if (block.Length > maxDesc)
                {
                    // Flush anything accumulated so far
                    if (currentDesc.Length > 0)
                    {
                        embeds.Add(CreateEmbed(
                            isFirst ? $"{serverName} -- Balance Changes" : null,
                            currentDesc.ToString(), isFirst));
                        currentDesc.Clear();
                        isFirst = false;
                    }

                    // Split the oversized block into sub-embeds
                    var lines = block.Split('\n');
                    var chunk = new StringBuilder();
                    bool inCodeBlock = false;
                    foreach (var line in lines)
                    {
                        if (line.TrimStart().StartsWith("```"))
                            inCodeBlock = !inCodeBlock;

                        if (chunk.Length + line.Length + 1 > maxDesc && chunk.Length > 0)
                        {
                            // Close open code block before flushing
                            if (inCodeBlock)
                                chunk.AppendLine("```");
                            embeds.Add(CreateEmbed(
                                isFirst ? $"{serverName} -- Balance Changes" : null,
                                chunk.ToString(), isFirst));
                            chunk.Clear();
                            isFirst = false;
                            // Re-open code block in new chunk
                            if (inCodeBlock)
                                chunk.AppendLine("```diff");
                        }
                        chunk.AppendLine(line);
                    }
                    if (chunk.Length > 0)
                        currentDesc.Append(chunk);
                    continue;
                }

                // If adding this block would exceed limit, flush current embed
                if (currentDesc.Length + block.Length > maxDesc && currentDesc.Length > 0)
                {
                    embeds.Add(CreateEmbed(
                        isFirst ? $"{serverName} -- Balance Changes" : null,
                        currentDesc.ToString(),
                        isFirst));
                    currentDesc.Clear();
                    isFirst = false;
                }
                currentDesc.Append(block);
            }

            // Flush remaining
            if (currentDesc.Length > 0)
            {
                embeds.Add(CreateEmbed(
                    isFirst ? $"{serverName} -- Balance Changes" : null,
                    currentDesc.ToString(),
                    isFirst));
            }

            return embeds;
        }

        private static JObject CreateEmbed(string title, string description, bool includeFooter)
        {
            var embed = new JObject();
            if (title != null)
                embed["title"] = title;
            embed["description"] = description;
            embed["color"] = 3447003; // blue
            if (includeFooter)
            {
                embed["footer"] = new JObject { ["text"] = "Si_UnitBalance | + = new change" };
                embed["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }
            return embed;
        }

        /// <summary>
        /// Posts or edits embed(s) to the Discord webhook URL.
        /// Edits existing messages in-place if IDs are stored; creates new ones otherwise.
        /// Deletes leftover messages if the new set is smaller.
        /// </summary>
        private static void PostEmbedsToDiscord(List<JObject> embeds)
        {
            var oldIds = new List<string>(_discordMessageIds);

            new Thread(() =>
            {
                var newIds = new List<string>();

                for (int i = 0; i < embeds.Count; i++)
                {
                    try
                    {
                        var payload = new JObject
                        {
                            ["embeds"] = new JArray { embeds[i] }
                        };
                        var jsonContent = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

                        string messageId = null;

                        // Try to edit existing message
                        if (i < oldIds.Count)
                        {
                            messageId = TryEditMessage(oldIds[i], jsonContent);
                        }

                        // If no existing message (or edit failed), post a new one
                        if (messageId == null)
                        {
                            messageId = PostNewMessage(jsonContent, i + 1, embeds.Count);
                        }
                        else
                        {
                            MelonLogger.Msg($"[Discord] Embed {i + 1}/{embeds.Count} edited.");
                        }

                        if (messageId != null)
                            newIds.Add(messageId);

                        if (i < embeds.Count - 1)
                            Thread.Sleep(1000);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Error($"[Discord] Failed for embed {i + 1}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }

                // Delete extra old messages that are no longer needed
                if (oldIds.Count > embeds.Count)
                {
                    for (int i = embeds.Count; i < oldIds.Count; i++)
                    {
                        try
                        {
                            var delUrl = $"{_discordWebhookUrl}/messages/{oldIds[i]}";
                            var resp = _httpClient.DeleteAsync(delUrl).Result;
                            if (resp.IsSuccessStatusCode)
                                MelonLogger.Msg($"[Discord] Deleted old message {i + 1}.");
                            Thread.Sleep(1000);
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[Discord] Failed to delete old message: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                }

                _discordMessageIds = newIds;
                SaveMessageIds();
            })
            { IsBackground = true }.Start();
        }

        /// <summary>
        /// Tries to PATCH an existing Discord message. Returns the message ID on success, null on failure.
        /// </summary>
        private static string TryEditMessage(string messageId, StringContent jsonContent)
        {
            try
            {
                var patchUrl = $"{_discordWebhookUrl}/messages/{messageId}";
                var request = new HttpRequestMessage(new HttpMethod("PATCH"), patchUrl) { Content = jsonContent };
                var response = _httpClient.SendAsync(request).Result;

                if (response.IsSuccessStatusCode)
                    return messageId;

                int status = (int)response.StatusCode;
                if (status == 404)
                {
                    MelonLogger.Msg($"[Discord] Message {messageId} was deleted — will create new.");
                }
                else
                {
                    string body = "";
                    try { body = response.Content.ReadAsStringAsync().Result; } catch { }
                    MelonLogger.Warning($"[Discord] PATCH returned {status}: {body}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Discord] PATCH failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Posts a new Discord message via webhook with ?wait=true to get the message ID back.
        /// </summary>
        private static string PostNewMessage(StringContent jsonContent, int index, int total)
        {
            try
            {
                var postUrl = _discordWebhookUrl + "?wait=true";
                var response = _httpClient.PostAsync(postUrl, jsonContent).Result;

                if (!response.IsSuccessStatusCode)
                {
                    string body = "";
                    try { body = response.Content.ReadAsStringAsync().Result; } catch { }
                    MelonLogger.Warning($"[Discord] POST returned {(int)response.StatusCode}: {body}");
                    return null;
                }

                MelonLogger.Msg($"[Discord] Embed {index}/{total} posted.");

                // Parse response to get message ID
                string respBody = response.Content.ReadAsStringAsync().Result;
                var respJson = JObject.Parse(respBody);
                return respJson["id"]?.Value<string>();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Discord] POST failed: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Deletes a list of Discord messages by their IDs (background thread).
        /// </summary>
        private static void DeleteOldMessages(List<string> messageIds)
        {
            new Thread(() =>
            {
                foreach (var id in messageIds)
                {
                    try
                    {
                        var delUrl = $"{_discordWebhookUrl}/messages/{id}";
                        var _ = _httpClient.DeleteAsync(delUrl).Result;
                        Thread.Sleep(1000);
                    }
                    catch { }
                }
            })
            { IsBackground = true }.Start();
        }

        private static string GetMessageIdsPath()
        {
            string configDir = Path.GetDirectoryName(_configPath)!;
            return Path.Combine(configDir, "Si_UnitBalance_DiscordMsgIds.json");
        }

        private static void LoadMessageIds()
        {
            try
            {
                string path = GetMessageIdsPath();
                if (!File.Exists(path))
                {
                    _discordMessageIds = new List<string>();
                    return;
                }
                var arr = JArray.Parse(File.ReadAllText(path));
                _discordMessageIds = new List<string>();
                foreach (var token in arr)
                {
                    string id = token.Value<string>();
                    if (!string.IsNullOrEmpty(id))
                        _discordMessageIds.Add(id);
                }
                MelonLogger.Msg($"[Discord] Loaded {_discordMessageIds.Count} stored message ID(s).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Discord] Failed to load message IDs: {ex.Message}");
                _discordMessageIds = new List<string>();
            }
        }

        private static void SaveMessageIds()
        {
            try
            {
                string path = GetMessageIdsPath();
                var arr = new JArray();
                foreach (var id in _discordMessageIds)
                    arr.Add(id);
                File.WriteAllText(path, arr.ToString());
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Discord] Failed to save message IDs: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves a snapshot of the current active config as the "last pushed" reference.
        /// </summary>
        private static void SavePushSnapshot()
        {
            try
            {
                string configDir = Path.GetDirectoryName(_configPath)!;
                string lastPushPath = Path.Combine(configDir, "Si_UnitBalance_LastPush.json");
                File.Copy(_configPath, lastPushPath, overwrite: true);
                MelonLogger.Msg($"[Discord] Saved push snapshot to {lastPushPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Discord] Failed to save push snapshot: {ex.Message}");
            }
        }

        private struct BalanceChange
        {
            public string Section;
            public string UnitName;
            public string ParamName;
            public string DefaultVal;
            public string ActiveVal;
            public bool IsNew;
            public string BaseInfo;

            public BalanceChange(string section, string unitName, string paramName, string defaultVal, string activeVal, bool isNew, string baseInfo = null)
            {
                Section = section;
                UnitName = unitName;
                ParamName = paramName;
                DefaultVal = defaultVal;
                ActiveVal = activeVal;
                IsNew = isNew;
                BaseInfo = baseInfo;
            }
        }
    }
}

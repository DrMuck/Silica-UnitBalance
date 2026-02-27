using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
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

using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_UnitBalance
{
    // =============================================
    // Per-player respawn cooldown ("spawn_cooldown", seconds, per unit).
    //
    // Respawning is free, and the respawn menu offers the team's tech-gated player units
    // (BaseTeamSetup.PlayerSpawnExt: Dragonfly for aliens, Commando for Sol, ...), so a player
    // can take a high-tier unit on every death. After spawning as a unit with a cooldown, the
    // same player cannot spawn as it again until the time is up; other units stay available.
    //
    // Enforced on the server only. A vanilla client builds the menu itself, so the unit stays
    // listed and a refused request just leaves the player in the menu, with a chat message.
    // Commander-built units and /buy purchases do not pass through here and are not limited.
    // =============================================
    public partial class UnitBalance
    {
        private static readonly Dictionary<string, float> _spawnCooldowns =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // (player, unit) -> Time.time of the last spawn. Cleared at round start.
        private static readonly Dictionary<(long player, string unit), float> _lastPlayerSpawn =
            new Dictionary<(long, string), float>();

        private static string SpawnUnitKey(ObjectInfo info) => ResolveConfigName(info.DisplayName, info.name).ToLowerInvariant();

        private static void SetSpawnCooldown(string unitName, float seconds)
        {
            if (seconds > 0f) _spawnCooldowns[unitName.ToLowerInvariant()] = seconds;
            else _spawnCooldowns.Remove(unitName.ToLowerInvariant());
        }

        private static float GetSpawnCooldownLeft(Player player, ObjectInfo info)
        {
            string unit = SpawnUnitKey(info);
            if (!_spawnCooldowns.TryGetValue(unit, out float cooldown)) return 0f;
            if (!_lastPlayerSpawn.TryGetValue((GetPlayerKey(player), unit), out float spawnedAt)) return 0f;
            return Math.Max(0f, spawnedAt + cooldown - Time.time);
        }

        private static string FmtSpawnCooldown(float seconds)
        {
            int total = Mathf.CeilToInt(seconds);
            return $"{total / 60}:{total % 60:00}";
        }

        /// <summary>True if the respawn menu can offer this unit, which is when spawn_cooldown has an effect.</summary>
        private static bool IsPlayerSpawnOption(string unitName)
        {
            if (!(GameMode.CurrentGameMode is GameModeExt mode) || mode.BaseTeamSetups == null) return false;
            foreach (BaseTeamSetup setup in mode.BaseTeamSetups)
            {
                if (setup?.PlayerSpawnExt == null) continue;
                foreach (ObjectInfo info in setup.PlayerSpawnExt)
                {
                    if (info != null && string.Equals(ResolveConfigName(info.DisplayName, info.name), unitName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        // The respawn menu request.
        private static class Patch_SpawnCooldownRequest
        {
            public static bool Prefix(Player player, ObjectInfo unitInfo, ref Unit __result)
            {
                try
                {
                    if (_spawnCooldowns.Count == 0 || player == null || unitInfo == null) return true;
                    float left = GetSpawnCooldownLeft(player, unitInfo);
                    if (left <= 0f) return true;

                    string unitName = ResolveConfigName(unitInfo.DisplayName, unitInfo.name);
                    MelonLogger.Msg($"[SPAWNCD] Blocked {player.PlayerName}: {unitName}, {left:F0}s left");
                    SendChatToPlayer(player, _chatPrefix + $"<color=#FF6666>{unitName}</color> is on cooldown for you: <color=#FFD700>{FmtSpawnCooldown(left)}</color>. Pick another unit.");
                    __result = null;
                    return false;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[SPAWNCD] Check failed: {ex.Message}");
                    return true;
                }
            }
        }

        // Spawns without a menu pick (a mod moving a player to another team) take the highest
        // unlocked unit, so without this they would hand out the unit on cooldown.
        private static class Patch_SpawnCooldownDefaultUnit
        {
            public static void Postfix(MP_Strategy __instance, Player player, ref GameObject __result)
            {
                try
                {
                    if (_spawnCooldowns.Count == 0 || player == null || __result == null) return;
                    BaseTeamSetup setup = __instance.GetTeamSetup(player.Team);
                    if (setup?.PlayerSpawnExt == null) return;

                    bool onCooldown = false;
                    for (int i = setup.PlayerSpawnExt.Count - 1; i >= 0; i--)
                    {
                        ObjectInfo info = setup.PlayerSpawnExt[i];
                        if (info == null || info.Prefab == null) continue;
                        if (!onCooldown)
                        {
                            if (info.Prefab != __result) continue;
                            if (GetSpawnCooldownLeft(player, info) <= 0f) return;
                            onCooldown = true;
                        }
                        else if (setup.Team.GetHasPrerequisitesForConstruction(info.ConstructionData)
                            && setup.Team.GetHasStructureForConstruction(info.ConstructionData)
                            && GetSpawnCooldownLeft(player, info) <= 0f)
                        {
                            __result = info.Prefab;
                            return;
                        }
                    }
                    if (onCooldown && setup.PlayerSpawn != null) __result = setup.PlayerSpawn.Prefab;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[SPAWNCD] Default unit check failed: {ex.Message}");
                }
            }
        }

        // Every player spawn ends here, so a spawn by any route starts the cooldown.
        private static class Patch_SpawnCooldownRecord
        {
            public static void Postfix(Player player, Unit __result)
            {
                try
                {
                    if (_spawnCooldowns.Count == 0 || player == null || __result == null || __result.ObjectInfo == null) return;
                    string unit = SpawnUnitKey(__result.ObjectInfo);
                    if (_spawnCooldowns.ContainsKey(unit))
                        _lastPlayerSpawn[(GetPlayerKey(player), unit)] = Time.time;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[SPAWNCD] Record failed: {ex.Message}");
                }
            }
        }

        private static void PatchSpawnCooldown(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(
                    AccessTools.Method(typeof(GameMode), "TrySpawnPlayerAtSpawnPoint", new[] { typeof(Player), typeof(ObjectInfo), typeof(Vector3) }),
                    prefix: new HarmonyMethod(typeof(Patch_SpawnCooldownRequest), "Prefix"));
                harmony.Patch(
                    AccessTools.Method(typeof(MP_Strategy), "GetUnitPrefabForPlayer"),
                    postfix: new HarmonyMethod(typeof(Patch_SpawnCooldownDefaultUnit), "Postfix"));
                harmony.Patch(
                    AccessTools.Method(typeof(GameMode), "SpawnUnitForPlayer", new[] { typeof(Player), typeof(GameObject), typeof(Vector3), typeof(Quaternion) }),
                    postfix: new HarmonyMethod(typeof(Patch_SpawnCooldownRecord), "Postfix"));
                MelonLogger.Msg("Patched player spawn for spawn_cooldown");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SPAWNCD] Failed to patch player spawn: {ex.Message}");
            }
        }
    }
}

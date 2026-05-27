using System;
using System.Collections;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace Si_UnitBalance
{
    // =============================================
    // HQ-killed decay warning (event-based, via GameEvents.OnStructureDestroyed).
    // When a team's base structure (HQ / Nest) is destroyed, tell that team in chat which
    // compass direction it was in and when their buildings will start to decay.
    // The decay countdown is derived from the faction's decay config (human delay=180 -> "3 min").
    // =============================================
    public partial class UnitBalance
    {
        private static bool _hqDecayWarningHooked = false;
        private const float VanillaDecayDelaySeconds = 10f; // vanilla DecayData.Delay
        private const float HQDirectionDeadzoneM = 600f;    // within this of map center on an axis = neutral

        // (Re)install the structure-destroyed hook. GameEvents.* is cleared on scene transitions,
        // so this is called again from OnGameStartedLogic after each load.
        internal static void EnsureHQDecayWarningHook()
        {
            try
            {
                GameEvents.OnStructureDestroyed -= OnStructureDestroyed_HQWarning;
                GameEvents.OnStructureDestroyed += OnStructureDestroyed_HQWarning;
                if (!_hqDecayWarningHooked)
                {
                    _hqDecayWarningHooked = true;
                    MelonLogger.Msg("[HQWarn] HQ-killed decay warning hooked (GameEvents.OnStructureDestroyed)");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HQWarn] Failed to hook OnStructureDestroyed: {ex.Message}");
            }
        }

        private static void OnStructureDestroyed_HQWarning(Structure structure, GameObject instigator)
        {
            try
            {
                if (structure == null || structure.Team == null) return;

                // Only the team's base structure (HQ for humans, Nest for aliens) triggers the unlink/decay cascade.
                ObjectInfo hqInfo = structure.Team.BaseStructure;
                if (hqInfo == null || structure.ObjectInfo != hqInfo) return;

                // Decay settings for this structure's faction. If decay is disabled, no warning.
                DecaySettings ds = (structure is AlienStructure) ? _decayAlien : _decayHuman;
                if (ds == null || !ds.Enabled) return;

                float delaySec = ds.Delay >= 0f ? ds.Delay : VanillaDecayDelaySeconds;

                string dir = GetCompassDirection(structure.transform.position);
                string when = FormatDelay(delaySec);

                string msg = _chatPrefix + "<color=#FFAA00>Your HQ (" + dir + ") was destroyed — building decay starts in " + when + ".</color>";
                BroadcastChatToTeam(structure.Team, msg);

                MelonLogger.Msg($"[HQWarn] HQ destroyed for team {structure.Team.TeamShortName} ({dir}) — decay in {when}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HQWarn] handler error: {ex.Message}");
            }
        }

        // Compass direction of a world position relative to map center (world origin — Silica maps are origin-centered).
        // Unity convention: +Z = north, +X = east.
        private static string GetCompassDirection(Vector3 pos)
        {
            float dx = pos.x;
            float dz = pos.z;
            string ns = dz > HQDirectionDeadzoneM ? "north" : (dz < -HQDirectionDeadzoneM ? "south" : "");
            string ew = dx > HQDirectionDeadzoneM ? "east" : (dx < -HQDirectionDeadzoneM ? "west" : "");
            if (ns.Length > 0 && ew.Length > 0) return ns + "-" + ew;
            if (ns.Length > 0) return ns;
            if (ew.Length > 0) return ew;
            return "center";
        }

        private static string FormatDelay(float seconds)
        {
            if (seconds >= 60f)
            {
                float mins = seconds / 60f;
                // whole minutes when clean, else one decimal
                if (Math.Abs(mins - Mathf.Round(mins)) < 0.05f)
                    return Mathf.RoundToInt(mins) + " min";
                return mins.ToString("0.#") + " min";
            }
            return Mathf.RoundToInt(seconds) + " sec";
        }

        // Send a chat message only to players on the given team.
        private static void BroadcastChatToTeam(Team team, string message)
        {
            try
            {
                if (team == null) return;
                IList playersList = null;
                var playersProp = typeof(Player).GetProperty("Players", BindingFlags.Public | BindingFlags.Static);
                if (playersProp != null) playersList = playersProp.GetValue(null) as IList;
                else
                {
                    var playersField = typeof(Player).GetField("Players", BindingFlags.Public | BindingFlags.Static);
                    if (playersField != null) playersList = playersField.GetValue(null) as IList;
                }
                if (playersList == null) return;

                object serverPlayer = typeof(NetworkGameServer)
                    .GetMethod("GetServerPlayer", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

                foreach (var p in playersList)
                {
                    if (p == null || p.Equals(serverPlayer)) continue;
                    var player = p as Player;
                    if (player == null || player.Team != team) continue;
                    SendChatToPlayer(p, message);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HQWarn] team broadcast error: {ex.Message}");
            }
        }
    }
}

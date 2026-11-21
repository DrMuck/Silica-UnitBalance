using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Si_UnitBalance
{
    // =============================================
    // Turret rotation speed (7.20.0): turret_yaw_speed_mult, turret_pitch_speed_mult
    //
    // Per-unit multipliers (1 = vanilla) on the public float fields YawSpeed / PitchSpeed (deg/s) of
    // VehicleTurret (vehicles, aircraft) and TurretRotator (defence structures).
    //
    // Who uses the value: every machine turns the turret itself toward the networked aim point
    // (VehicleTurret.OrderedUpdate -> GameMath.RotateGameSubsystemToPoint(..., YawSpeed, ..., PitchSpeed)),
    // with the value of ITS OWN component copy. A server-only write therefore changes AI turrets only; a
    // player's own turret follows the player's client.
    //
    // How it reaches clients: OverrideManager resolves "A:<ObjectInfo>.asset" + member name on the ObjectInfo
    // first and then on the FIRST MonoBehaviour of ObjectInfo.Prefab whose type declares that member
    // (DeclaredOnly), and syncs the override to every client. So:
    //   - the first turret component of a prefab goes through OverrideManager (synced, players included);
    //   - every further turret of the same prefab (multi-turret units) cannot be addressed by OverrideManager
    //     and gets a tracked direct prefab write (server-only -> AI turrets only).
    // Vanilla values are remembered on first sight per prefab component, so nothing can compound. Live
    // instances on the server are set from the prefab values after each apply; a client's already spawned
    // units keep their old speed until they respawn (OverrideManager only changes the prefab there).
    // =============================================
    public partial class UnitBalance
    {
        internal const string TurretYawSpeedKey = "turret_yaw_speed_mult";
        internal const string TurretPitchSpeedKey = "turret_pitch_speed_mult";
        private const string TurretYawMember = "YawSpeed";
        private const string TurretPitchMember = "PitchSpeed";
        private const float TurretSpeedMax = 36000f; // the fields' [Range] upper end

        private static readonly Dictionary<string, float> _turretYawSpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _turretPitchSpeedMultipliers =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // "<ObjectInfo name>|<turret index>|<member>" -> vanilla deg/s, first value seen (prefabs live for the process).
        private static readonly Dictionary<string, float> _turretSpeedVanilla =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // ObjectInfo names this mod wrote at the last apply (a removed param resets their live instances too).
        private static readonly HashSet<string> _turretSpeedTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Config parse helper: stores a turret speed multiplier unless it is 1 (vanilla) or not positive.</summary>
        private static void AddTurretSpeedMult(Dictionary<string, float> dict, string unitName, string key, float mult)
        {
            if (Math.Abs(mult - 1.0f) <= 0.001f) return;
            if (!(mult > 0f) || float.IsInfinity(mult) || float.IsNaN(mult))
            {
                MelonLogger.Warning($"[TURRETSPEED] {unitName}: {key} = {mult} ignored (must be > 0)");
                return;
            }
            dict[unitName] = mult;
        }

        private static bool IsTurretSpeedComponent(Component comp)
        {
            if (comp == null) return false;
            string tn = comp.GetType().Name;
            return tn == "VehicleTurret" || tn == "TurretRotator";
        }

        /// <summary>The turret components of a prefab or live object, in OverrideManager's search order.</summary>
        private static List<Component> TurretSpeedComponents(GameObject root)
        {
            var list = new List<Component>();
            if (root == null) return list;
            foreach (var comp in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (IsTurretSpeedComponent(comp)) list.Add(comp);
            return list;
        }

        private static FieldInfo? TurretSpeedField(Component comp, string member)
        {
            var f = comp.GetType().GetField(member, BindingFlags.Public | BindingFlags.Instance);
            return (f != null && f.FieldType == typeof(float)) ? f : null;
        }

        private static void ApplyTurretSpeedOverrides(bool useOM)
        {
            if (_turretYawSpeedMultipliers.Count == 0 && _turretPitchSpeedMultipliers.Count == 0 && _turretSpeedTouched.Count == 0)
                return;

            var nowTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int synced = 0, serverOnly = 0;

            foreach (var info in AllObjectInfos())
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                bool hasYaw = _turretYawSpeedMultipliers.TryGetValue(name, out float yawMult);
                bool hasPitch = _turretPitchSpeedMultipliers.TryGetValue(name, out float pitchMult);
                if (!hasYaw && !hasPitch) continue;

                var turrets = TurretSpeedComponents(info.Prefab);
                if (turrets.Count == 0) continue;
                string oiTarget = $"A:{info.name}.asset";

                for (int pass = 0; pass < 2; pass++)
                {
                    string member = pass == 0 ? TurretYawMember : TurretPitchMember;
                    if (pass == 0 ? !hasYaw : !hasPitch) continue;
                    float mult = pass == 0 ? yawMult : pitchMult;

                    for (int i = 0; i < turrets.Count; i++)
                    {
                        var comp = turrets[i];
                        var f = TurretSpeedField(comp, member);
                        if (f == null) continue;

                        string vKey = info.name + "|" + i + "|" + member;
                        if (!_turretSpeedVanilla.TryGetValue(vKey, out float vanilla))
                        {
                            try { vanilla = (float)f.GetValue(comp); } catch { continue; }
                            _turretSpeedVanilla[vKey] = vanilla;
                        }
                        if (vanilla <= 0f) continue; // 0 = the axis does not turn at all
                        float newVal = Mathf.Clamp(vanilla * mult, 0.01f, TurretSpeedMax);
                        string goName = comp.gameObject != null ? comp.gameObject.name : "?";

                        // OverrideManager can only address the first component that declares the member.
                        if (i == 0 && useOM && OMSetFloat(oiTarget, member, newVal))
                        {
                            synced++;
                            LogDebug($"[TURRETSPEED] {name} -> {goName}.{member}: {vanilla:0.#} -> {newVal:0.#} deg/s (OM, synced)");
                        }
                        else
                        {
                            try
                            {
                                WriteFloatTracked(comp, f, newVal, $"{name}.{goName}.{member}", comp);
                                serverOnly++;
                                LogDebug($"[TURRETSPEED] {name} -> {goName}.{member}: {vanilla:0.#} -> {newVal:0.#} deg/s (direct, server-only)");
                            }
                            catch (Exception ex) { MelonLogger.Warning($"[TURRETSPEED] {name}/{goName}.{member} set failed: {ex.Message}"); }
                        }
                    }
                }
                nowTouched.Add(info.name);
            }

            if (synced > 0 || serverOnly > 0)
                MelonLogger.Msg($"[UnitBalance] turret speed: {synced} field(s) synced via OverrideManager, {serverOnly} server-only (AI turrets)");

            // Live instances on this machine: copy the prefab's values (new ones, or vanilla again after a
            // revert) onto every spawned object whose prefab is or was overridden.
            if (nowTouched.Count > 0 || _turretSpeedTouched.Count > 0)
            {
                int live = 0;
                try
                {
                    foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(BaseGameObject)))
                    {
                        var bgo = obj as BaseGameObject;
                        if (bgo == null || bgo.ObjectInfo == null || bgo.ObjectInfo.Prefab == null) continue;
                        string infoName = bgo.ObjectInfo.name;
                        if (!nowTouched.Contains(infoName) && !_turretSpeedTouched.Contains(infoName)) continue;

                        var prefabTurrets = TurretSpeedComponents(bgo.ObjectInfo.Prefab);
                        var liveTurrets = TurretSpeedComponents(bgo.gameObject);
                        if (prefabTurrets.Count == 0 || prefabTurrets.Count != liveTurrets.Count) continue;
                        for (int i = 0; i < liveTurrets.Count; i++)
                        {
                            live += CopyTurretSpeed(prefabTurrets[i], liveTurrets[i], TurretYawMember);
                            live += CopyTurretSpeed(prefabTurrets[i], liveTurrets[i], TurretPitchMember);
                        }
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[TURRETSPEED] live pass failed: {ex.Message}"); }
                if (live > 0) MelonLogger.Msg($"[UnitBalance] turret speed: set {live} LIVE field(s) in scene");
            }

            _turretSpeedTouched.Clear();
            foreach (var n in nowTouched) _turretSpeedTouched.Add(n);
        }

        private static int CopyTurretSpeed(Component prefab, Component live, string member)
        {
            if (prefab.GetType() != live.GetType()) return 0;
            var f = TurretSpeedField(prefab, member);
            if (f == null) return 0;
            try
            {
                float want = (float)f.GetValue(prefab);
                float cur = (float)f.GetValue(live);
                if (Math.Abs(want - cur) < 0.01f) return 0;
                f.SetValue(live, want);
                return 1;
            }
            catch { return 0; }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace Si_UnitBalance
{
    // =============================================
    // Infantry weapon params: pri_fire_rate_mult, pri_magazine_mult, pri_reload_time_mult,
    // pri_accuracy_mult. Infantry guns live on HumanHandsAnimator, not VehicleTurret.
    //
    // OverrideManager reaches these fields through the ObjectInfo prefab fallback (first
    // MonoBehaviour declaring the member), the same way it reaches VehicleTurret fields,
    // and syncs them to clients. That matters because a player's soldier fires on the
    // player's own machine.
    //
    // OM writes the value onto the prefab, so a prefab read after the first apply is not
    // vanilla. Vanilla values are snapshotted here once per process and everything is
    // computed from the snapshot, which keeps re-applies and live propagation from compounding.
    // =============================================
    public partial class UnitBalance
    {
        private sealed class HandsWeaponStats
        {
            public float FireDelay, ReloadTime, SpreadMin, SpreadMax;
            public int Magazine;

            public bool SameAs(HandsWeaponStats o) =>
                FireDelay == o.FireDelay && ReloadTime == o.ReloadTime && SpreadMin == o.SpreadMin
                && SpreadMax == o.SpreadMax && Magazine == o.Magazine;
        }

        private const float HandsMinFireDelay = 0.01f;
        private const float HandsMinReloadTime = 0.1f;

        // Keyed by ObjectInfo.name. Never cleared: asset values do not change within a process.
        private static readonly Dictionary<string, HandsWeaponStats> _handsVanilla =
            new Dictionary<string, HandsWeaponStats>();
        private static readonly Dictionary<string, string> _handsLastLogged =
            new Dictionary<string, string>();

        private static readonly FieldInfo _handsFiredRounds = typeof(HumanHandsAnimator).GetField(
            "NumFiredRounds", BindingFlags.NonPublic | BindingFlags.Instance);

        private static bool IsMultSet(float mult) => Math.Abs(mult - 1f) > 0.001f;

        /// <summary>Vanilla stats scaled by the unit's config multipliers.</summary>
        private static HandsWeaponStats GetHandsTarget(string unitName, HandsWeaponStats vanilla)
        {
            float fireRate = GetWeaponMult(_fireRateMultipliers, unitName, "pri");
            float reload = GetWeaponMult(_reloadTimeMultipliers, unitName, "pri");
            float accuracy = GetWeaponMult(_accuracyMultipliers, unitName, "pri");
            float magazine = GetWeaponMult(_magazineMultipliers, unitName, "pri");

            return new HandsWeaponStats
            {
                FireDelay = IsMultSet(fireRate) && fireRate > 0f
                    ? Math.Max(HandsMinFireDelay, vanilla.FireDelay / fireRate) : vanilla.FireDelay,
                ReloadTime = IsMultSet(reload)
                    ? Math.Max(HandsMinReloadTime, vanilla.ReloadTime * reload) : vanilla.ReloadTime,
                SpreadMin = IsMultSet(accuracy) ? vanilla.SpreadMin * accuracy : vanilla.SpreadMin,
                SpreadMax = IsMultSet(accuracy) ? vanilla.SpreadMax * accuracy : vanilla.SpreadMax,
                // MagazineSize 0 means infinite ammo, so it stays 0 and nothing scales into it.
                Magazine = IsMultSet(magazine) && vanilla.Magazine > 0
                    ? Math.Max(1, (int)Math.Round(vanilla.Magazine * magazine)) : vanilla.Magazine,
            };
        }

        /// <summary>Called from ApplyRangeOverrides, after OM has been reverted to vanilla.</summary>
        private static void ApplyHandsWeaponOverrides(ObjectInfo info, string unitName, string oiTarget, bool useOM)
        {
            var hands = info.Prefab.GetComponentInChildren<HumanHandsAnimator>(true);
            if (hands == null) return;

            if (!_handsVanilla.TryGetValue(info.name, out HandsWeaponStats vanilla))
            {
                vanilla = new HandsWeaponStats
                {
                    FireDelay = hands.FireDelay,
                    ReloadTime = hands.ReloadTime,
                    SpreadMin = hands.MuzzleSpreadMin,
                    SpreadMax = hands.MuzzleSpreadMax,
                    Magazine = hands.MagazineSize,
                };
                _handsVanilla[info.name] = vanilla;
            }

            HandsWeaponStats target = GetHandsTarget(unitName, vanilla);
            if (target.SameAs(vanilla)) return;

            if (useOM)
            {
                if (target.FireDelay != vanilla.FireDelay) OMSetFloat(oiTarget, "FireDelay", target.FireDelay);
                if (target.ReloadTime != vanilla.ReloadTime) OMSetFloat(oiTarget, "ReloadTime", target.ReloadTime);
                if (target.SpreadMin != vanilla.SpreadMin) OMSetFloat(oiTarget, "MuzzleSpreadMin", target.SpreadMin);
                if (target.SpreadMax != vanilla.SpreadMax) OMSetFloat(oiTarget, "MuzzleSpreadMax", target.SpreadMax);
                if (target.Magazine != vanilla.Magazine) OMSetInt(oiTarget, "MagazineSize", target.Magazine);
            }
            else
            {
                WriteHandsStats(hands, target);
            }
        }

        /// <summary>
        /// Brings a live soldier in line with the config, including back to vanilla when a
        /// multiplier was removed. Only touches units whose vanilla stats were snapshotted.
        /// </summary>
        private static void PropagateHandsWeapon(GameObject liveGO, ObjectInfo info, string unitName)
        {
            if (!_handsVanilla.TryGetValue(info.name, out HandsWeaponStats vanilla)) return;
            var live = liveGO.GetComponentInChildren<HumanHandsAnimator>(true);
            if (live == null) return;

            HandsWeaponStats target = GetHandsTarget(unitName, vanilla);
            WriteHandsStats(live, target);

            // Above the new magazine ShotsRemaining goes negative and never hits the 0 that
            // starts a reload, leaving the soldier unable to fire.
            if (_handsFiredRounds != null && (int)_handsFiredRounds.GetValue(live) > target.Magazine)
                _handsFiredRounds.SetValue(live, target.Magazine);
        }

        private static void WriteHandsStats(HumanHandsAnimator hands, HandsWeaponStats stats)
        {
            hands.FireDelay = stats.FireDelay;
            hands.ReloadTime = stats.ReloadTime;
            hands.MuzzleSpreadMin = stats.SpreadMin;
            hands.MuzzleSpreadMax = stats.SpreadMax;
            hands.MagazineSize = stats.Magazine;
        }

        // Prefab values are logged next to the targets: a mismatch means OM did not reach
        // HumanHandsAnimator, which would leave players (who spawn from the prefab) on old values.
        private static IEnumerator LogHandsResultsAfter(float delay)
        {
            // OM.Set is queued, so the prefabs only carry the new values a few frames later.
            yield return new WaitForSeconds(delay);
            LogHandsResults();
        }

        private static void LogHandsResults()
        {
            if (_handsVanilla.Count == 0) return;
            foreach (var info in Resources.FindObjectsOfTypeAll<ObjectInfo>())
            {
                if (info == null || info.Prefab == null) continue;
                if (!_handsVanilla.TryGetValue(info.name, out HandsWeaponStats vanilla)) continue;
                var prefab = info.Prefab.GetComponentInChildren<HumanHandsAnimator>(true);
                if (prefab == null) continue;

                string unitName = ResolveConfigName(info.DisplayName, info.name);
                HandsWeaponStats target = GetHandsTarget(unitName, vanilla);
                string line = $"fire delay {target.FireDelay:F3} (prefab {prefab.FireDelay:F3}), " +
                              $"magazine {target.Magazine} (prefab {prefab.MagazineSize}), " +
                              $"reload {target.ReloadTime:F2} (prefab {prefab.ReloadTime:F2}), " +
                              $"spread {target.SpreadMin:F3}-{target.SpreadMax:F3} " +
                              $"(prefab {prefab.MuzzleSpreadMin:F3}-{prefab.MuzzleSpreadMax:F3})";
                if (_handsLastLogged.TryGetValue(info.name, out string last) && last == line) continue;
                _handsLastLogged[info.name] = line;
                MelonLogger.Msg($"[HANDS] {unitName}: {line}");
            }
        }
    }
}

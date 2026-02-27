using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Cost & build time: modify ConstructionData via OverrideManager
        // =============================================

        private static void ApplyConstructionDataOverrides(bool useOM)
        {
            bool hasUnitOverrides = _costMultipliers.Count > 0 || _buildTimeMultipliers.Count > 0
                                 || _minTierOverrides.Count > 0 || _buildRadiusOverrides.Count > 0;
            bool hasTechOverrides = _techTierTimes.Count > 0;

            if (!hasUnitOverrides && !hasTechOverrides)
                return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            int techApplied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.ConstructionData == null) continue;

                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                var cd = info.ConstructionData;
                string cdTarget = useOM ? $"A:{cd.name}.asset" : null;

                // Tech tier build time overrides (match by TechnologyTier field)
                if (hasTechOverrides && cd.TechnologyTier > 0)
                {
                    if (_techTierTimes.TryGetValue(cd.TechnologyTier, out float techTime))
                    {
                        float origTime = cd.BuildUpTime;
                        float newTime = Math.Max(1f, techTime);
                        if (useOM)
                            OMSetFloat(cdTarget, "BuildUpTime", newTime);
                        else
                            cd.BuildUpTime = newTime;
                        MelonLogger.Msg($"[TECH] Tier {cd.TechnologyTier} '{name}': {origTime:F0}s -> {newTime:F0}s{(useOM ? " (OM)" : "")}");
                        techApplied++;
                    }
                    continue;
                }

                // Unit/structure overrides
                bool hasCost = _costMultipliers.TryGetValue(name, out float costMult);
                bool hasBuildTime = _buildTimeMultipliers.TryGetValue(name, out float btMult);
                bool hasMinTier = _minTierOverrides.TryGetValue(name, out int minTier);
                bool hasBuildRadius = _buildRadiusOverrides.TryGetValue(name, out float buildRadius);
                if (!hasCost && !hasBuildTime && !hasMinTier && !hasBuildRadius) continue;

                if (hasCost)
                {
                    int origCost = cd.ResourceCost;
                    int newCost = Math.Max(1, (int)Math.Round(origCost * costMult));
                    if (useOM)
                        OMSetInt(cdTarget, "ResourceCost", newCost);
                    else
                        cd.ResourceCost = newCost;
                    MelonLogger.Msg($"[COST] {name}: {origCost} -> {newCost} (x{costMult:F2}){(useOM ? " (OM)" : "")}");
                }

                if (hasBuildTime)
                {
                    float origTime = cd.BuildUpTime;
                    float newTime = Math.Max(0.5f, origTime * btMult);
                    if (useOM)
                        OMSetFloat(cdTarget, "BuildUpTime", newTime);
                    else
                        cd.BuildUpTime = newTime;
                    MelonLogger.Msg($"[BUILD] {name}: {origTime:F1}s -> {newTime:F1}s (x{btMult:F2}){(useOM ? " (OM)" : "")}");
                }

                if (hasMinTier)
                {
                    int origTier = cd.MinimumTeamTier;
                    if (useOM)
                        OMSetInt(cdTarget, "MinimumTeamTier", minTier);
                    else
                        cd.MinimumTeamTier = minTier;
                    MelonLogger.Msg($"[TIER] {name}: min tier {origTier} -> {minTier}{(useOM ? " (OM)" : "")}");
                }

                if (hasBuildRadius)
                {
                    float origDist = cd.MaximumBaseStructureDistance;
                    if (useOM)
                        OMSetFloat(cdTarget, "MaximumBaseStructureDistance", buildRadius);
                    else
                        cd.MaximumBaseStructureDistance = buildRadius;
                    MelonLogger.Msg($"[BUILD_RADIUS] {name}: {origDist:F0} -> {buildRadius:F0}{(useOM ? " (OM)" : "")}");
                }

                applied++;
            }

            if (applied > 0 || techApplied > 0)
                MelonLogger.Msg($"Applied construction overrides: {applied} units/structures, {techApplied} tech tiers");
        }

        // =============================================
        // Health: scale DamageManagerData.Health via OverrideManager
        // DamageManagerData is a ScriptableObject asset (e.g. "Soldier_Juggernaut.asset")
        // with a "Health" field. OverrideManager can target it via "A:{name}.asset".
        // This syncs to clients and avoids necromancy desync.
        // =============================================

        private static void ApplyHealthOverrides(bool useOM)
        {
            if (_healthMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_healthMultipliers.TryGetValue(name, out float hpMult)) continue;

                var dm = info.Prefab.GetComponent<DamageManager>();
                if (dm == null) continue;

                bool set = false;
                try
                {
                    var dataField = dm.GetType().GetField("Data", BindingFlags.Public | BindingFlags.Instance);
                    if (dataField != null)
                    {
                        object dataObj = dataField.GetValue(dm);
                        if (dataObj != null)
                        {
                            float origHealth = GetFloatMember(dataObj, "Health");
                            if (origHealth <= 0) origHealth = GetFloatMember(dataObj, "MaxHealth");
                            if (origHealth <= 0) origHealth = GetFloatMember(dataObj, "m_MaxHealth");
                            if (origHealth > 0)
                            {
                                float newHealth = origHealth * hpMult;
                                string dataName = ((UnityEngine.Object)dataObj).name;

                                if (useOM && !string.IsNullOrEmpty(dataName))
                                {
                                    // Use OverrideManager to set Health on the DamageManagerData asset
                                    // Target uses "DamageManagerData_" prefix to match our registered synthetic paths
                                    string dmdTarget = $"A:DamageManagerData_{dataName}.asset";
                                    set = OMSetFloat(dmdTarget, "Health", newHealth);
                                    if (set)
                                        MelonLogger.Msg($"[HEALTH] {name}: Health {origHealth:F0} -> {newHealth:F0} (x{hpMult:F2}) (OM: {dataName})");
                                    else
                                        MelonLogger.Warning($"[HEALTH] {name}: OM failed for {dmdTarget}, falling back to direct mutation");
                                }

                                if (!set)
                                {
                                    // Fallback: direct mutation (no client sync — may cause desync)
                                    set = SetFloatMember(dataObj, "Health", newHealth);
                                    if (!set) set = SetFloatMember(dataObj, "MaxHealth", newHealth);
                                    if (!set) set = SetFloatMember(dataObj, "m_MaxHealth", newHealth);
                                    if (set)
                                        MelonLogger.Msg($"[HEALTH] {name}: Health {origHealth:F0} -> {newHealth:F0} (x{hpMult:F2}) (direct mutation — no client sync!)");
                                }
                            }
                        }
                    }
                }
                catch { }
                if (set) applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[HEALTH] Applied MaxHealth overrides to {applied} units/structures");
        }

        // =============================================
        // Damage: scale ProjectileData damage fields (replaces broken Harmony ApplyDamage Postfix)
        // Modifying damage at the ProjectileData level ensures server & client see the same values.
        // =============================================

        private static readonly string[] _damageFields = {
            "m_fImpactDamage", "m_fRicochetDamage", "m_fSplashDamageMax", "m_fPenetratingDamage"
        };

        private static void ApplyProjectileDamageOverrides(bool useOM)
        {
            if (_damageMultipliers.Count == 0 && _projectileOverrides.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            var modifiedPD = new HashSet<string>(); // track already-modified ProjectileData assets

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                bool hasDamageMult = HasAnyWeaponMult(_damageMultipliers, name);
                bool hasProjOverrides = _projectileOverrides.TryGetValue(name, out var unitProjOverrides);
                if (!hasDamageMult && !hasProjOverrides) continue;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                bool anyApplied = false;

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    // --- VehicleTurret: find PrimaryProjectileData / SecondaryProjectileData ---
                    if (typeName == "VehicleTurret")
                    {
                        var vtType = comp.GetType();
                        foreach (string projFieldName in new[] { "PrimaryProjectileData", "SecondaryProjectileData" })
                        {
                            var projField = vtType.GetField(projFieldName,
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (projField == null) continue;
                            object pdObj;
                            try { pdObj = projField.GetValue(comp); } catch { continue; }
                            if (pdObj == null) continue;

                            string pdName = "";
                            try { pdName = ((UnityEngine.Object)pdObj).name; } catch { continue; }
                            if (string.IsNullOrEmpty(pdName) || modifiedPD.Contains(pdName)) continue;

                            // Check for per-projectile absolute overrides first
                            if (hasProjOverrides && unitProjOverrides.TryGetValue(pdName, out var fieldOverrides))
                            {
                                string pdTarget = useOM ? $"A:{pdName}.asset" : null;
                                foreach (var kvp in fieldOverrides)
                                {
                                    float origVal = GetFloatMember(pdObj, kvp.Key);
                                    if (useOM)
                                        OMSetFloat(pdTarget, kvp.Key, kvp.Value);
                                    else
                                        SetFloatMember(pdObj, kvp.Key, kvp.Value);
                                    MelonLogger.Msg($"[DMG] {name} -> {pdName}.{kvp.Key}: {origVal:F1} -> {kvp.Value:F1}{(useOM ? " (OM)" : "")}");
                                }
                                modifiedPD.Add(pdName);
                                anyApplied = true;
                            }
                            else if (hasDamageMult)
                            {
                                // Resolve per-weapon damage mult (pri_/sec_ override, then shared fallback)
                                string weapon = projFieldName.StartsWith("Primary") ? "pri" : "sec";
                                float effectiveDmgMult = GetWeaponMult(_damageMultipliers, name, weapon);
                                if (Math.Abs(effectiveDmgMult - 1f) > 0.001f)
                                {
                                    ScaleProjectileDamage(pdObj, pdName, effectiveDmgMult, modifiedPD, useOM, name);
                                    anyApplied = true;
                                }
                            }
                        }
                    }

                    // --- CreatureDecapod: AttackPrimary/Secondary.AttackProjectileData + melee Damage ---
                    if (typeName == "CreatureDecapod")
                    {
                        var compType = comp.GetType();
                        foreach (string attackFieldName in new[] { "AttackPrimary", "AttackSecondary" })
                        {
                            var attackField = compType.GetField(attackFieldName,
                                BindingFlags.Public | BindingFlags.Instance);
                            if (attackField == null) continue;
                            object attackObj;
                            try { attackObj = attackField.GetValue(comp); } catch { continue; }
                            if (attackObj == null) continue;

                            var attackType = attackObj.GetType();

                            // Check for ranged attack (has ProjectileData)
                            var pdField = attackType.GetField("AttackProjectileData",
                                BindingFlags.Public | BindingFlags.Instance);
                            object pdObj = null;
                            if (pdField != null)
                            {
                                try { pdObj = pdField.GetValue(attackObj); } catch { }
                            }

                            if (pdObj != null)
                            {
                                string pdName = "";
                                try { pdName = ((UnityEngine.Object)pdObj).name; } catch { }
                                if (!string.IsNullOrEmpty(pdName) && !modifiedPD.Contains(pdName))
                                {
                                    if (hasProjOverrides && unitProjOverrides.TryGetValue(pdName, out var fieldOverrides))
                                    {
                                        string pdTarget = useOM ? $"A:{pdName}.asset" : null;
                                        foreach (var kvp in fieldOverrides)
                                        {
                                            float origVal = GetFloatMember(pdObj, kvp.Key);
                                            if (useOM)
                                                OMSetFloat(pdTarget, kvp.Key, kvp.Value);
                                            else
                                                SetFloatMember(pdObj, kvp.Key, kvp.Value);
                                            MelonLogger.Msg($"[DMG] {name} -> {pdName}.{kvp.Key}: {origVal:F1} -> {kvp.Value:F1}{(useOM ? " (OM)" : "")}");
                                        }
                                        modifiedPD.Add(pdName);
                                        anyApplied = true;
                                    }
                                    else if (hasDamageMult)
                                    {
                                        string weapon = attackFieldName == "AttackPrimary" ? "pri" : "sec";
                                        float effectiveDmgMult = GetWeaponMult(_damageMultipliers, name, weapon);
                                        if (Math.Abs(effectiveDmgMult - 1f) > 0.001f)
                                        {
                                            ScaleProjectileDamage(pdObj, pdName, effectiveDmgMult, modifiedPD, useOM, name);
                                            anyApplied = true;
                                        }
                                    }
                                }
                            }
                            else if (hasDamageMult)
                            {
                                // Melee attack (no projectile) — scale CreatureAttack.Damage directly
                                string weapon = attackFieldName == "AttackPrimary" ? "pri" : "sec";
                                float effectiveDmgMult = GetWeaponMult(_damageMultipliers, name, weapon);
                                if (Math.Abs(effectiveDmgMult - 1f) > 0.001f)
                                {
                                    float origDmg = GetFloatMember(attackObj, "Damage");
                                    if (origDmg > 0)
                                    {
                                        float newDmg = origDmg * effectiveDmgMult;
                                        SetFloatMember(attackObj, "Damage", newDmg);
                                        MelonLogger.Msg($"[DMG] {name} -> {attackFieldName}.Damage: {origDmg:F0} -> {newDmg:F0} (melee, direct)");
                                        anyApplied = true;
                                    }
                                }
                            }
                        }
                    }
                }

                if (anyApplied) applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[DMG] Applied projectile/melee damage overrides to {applied} units");
        }

        private static void ScaleProjectileDamage(object pdObj, string pdName, float dmgMult, HashSet<string> modifiedPD, bool useOM, string unitName)
        {
            string pdTarget = useOM ? $"A:{pdName}.asset" : null;
            int scaled = 0;

            foreach (string fieldName in _damageFields)
            {
                float orig = GetFloatMember(pdObj, fieldName);
                if (orig <= 0) continue;
                float newVal = orig * dmgMult;
                if (useOM)
                    OMSetFloat(pdTarget, fieldName, newVal);
                else
                    SetFloatMember(pdObj, fieldName, newVal);
                MelonLogger.Msg($"[DMG] {unitName} -> {pdName}.{fieldName}: {orig:F1} -> {newVal:F1} (x{dmgMult:F2}){(useOM ? " (OM)" : "")}");
                scaled++;
            }

            if (scaled == 0)
                MelonLogger.Warning($"[DMG] {unitName} -> {pdName}: no damage fields found to scale");

            modifiedPD.Add(pdName);
        }

        // =============================================
        // Range/Speed: scale projectiles, sensor, turret, creature attacks
        // =============================================

        // Original-value caches for direct mutation path (CreatureAttack fields)
        private static readonly Dictionary<string, float> _originalCreatureAttackAimDist =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalSpread =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalProjectileLifetimes =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalProjectileSpeeds =
            new Dictionary<string, float>();

        private static void ApplyRangeOverrides(bool useOM)
        {
            if (_rangeMultipliers.Count == 0 && _speedMultipliers.Count == 0 && _reloadTimeMultipliers.Count == 0
                && _accuracyMultipliers.Count == 0 && _magazineMultipliers.Count == 0 && _fireRateMultipliers.Count == 0
                && _lifetimeMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            var allProjectiles = Resources.FindObjectsOfTypeAll<ProjectileData>();
            int applied = 0;
            var modifiedPD = new HashSet<string>();

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                bool hasRange = HasAnyWeaponMult(_rangeMultipliers, name);
                bool hasSpeed = HasAnyWeaponMult(_speedMultipliers, name);
                bool hasLifetime = HasAnyWeaponMult(_lifetimeMultipliers, name);
                bool hasReload = HasAnyWeaponMult(_reloadTimeMultipliers, name);
                bool hasAccuracy = HasAnyWeaponMult(_accuracyMultipliers, name);
                bool hasMagazine = HasAnyWeaponMult(_magazineMultipliers, name);
                bool hasFireRate = HasAnyWeaponMult(_fireRateMultipliers, name);
                if (!hasRange && !hasSpeed && !hasLifetime && !hasReload && !hasAccuracy && !hasMagazine && !hasFireRate) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                MelonLogger.Msg($"[RANGE] Applying weapon overrides to '{name}' (internal: {info.name}){(useOM ? " (OM)" : "")}");

                bool foundProjectileOnComponent = false;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    if (hasRange)
                    {
                        // NOTE: Sensor.TargetingDistance is NOT scaled by range_mult.
                        // Use the explicit "target_distance" config param to set it.

                        // VehicleTurret.AimDistance (shared field — use primary range mult)
                        if (typeName == "VehicleTurret")
                        {
                            float vtRangeMult = GetWeaponMult(_rangeMultipliers, name, "pri");
                            if (Math.Abs(vtRangeMult - 1f) > 0.001f)
                            {
                                var adField = comp.GetType().GetField("AimDistance",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (adField != null)
                                {
                                    float orig = (float)adField.GetValue(comp);
                                    float newVal = orig * vtRangeMult;
                                    if (useOM)
                                        OMSetFloat(oiTarget, "AimDistance", newVal);
                                    else
                                        adField.SetValue(comp, newVal);
                                    MelonLogger.Msg($"  VehicleTurret.AimDistance: {orig} -> {newVal}");
                                }
                            }
                        }

                        // UnitAimAt.AimDistanceMax (shared — use primary range mult)
                        if (typeName == "UnitAimAt")
                        {
                            float uaRangeMult = GetWeaponMult(_rangeMultipliers, name, "pri");
                            if (Math.Abs(uaRangeMult - 1f) > 0.001f)
                            {
                                var admField = comp.GetType().GetField("AimDistanceMax",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (admField != null)
                                {
                                    float orig = (float)admField.GetValue(comp);
                                    float newVal = orig * uaRangeMult;
                                    if (useOM)
                                        OMSetFloat(oiTarget, "AimDistanceMax", newVal);
                                    else
                                        admField.SetValue(comp, newVal);
                                    MelonLogger.Msg($"  UnitAimAt.AimDistanceMax: {orig} -> {newVal}");
                                }
                            }
                        }

                        // CreatureDecapod: per-weapon range/accuracy/speed (direct mutation)
                        if (typeName == "CreatureDecapod")
                        {
                            var compType = comp.GetType();
                            foreach (string attackFieldName in new[] { "AttackPrimary", "AttackSecondary" })
                            {
                                string weapon = attackFieldName == "AttackPrimary" ? "pri" : "sec";
                                float atkRangeMult = GetWeaponMult(_rangeMultipliers, name, weapon);
                                float atkAccMult = GetWeaponMult(_accuracyMultipliers, name, weapon);
                                float atkSpeedMult = GetWeaponMult(_speedMultipliers, name, weapon);

                                var attackField = compType.GetField(attackFieldName,
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (attackField == null) continue;
                                object attackObj;
                                try { attackObj = attackField.GetValue(comp); } catch { continue; }
                                if (attackObj == null) continue;

                                var attackType = attackObj.GetType();

                                // Scale AttackProjectileAimDistMax (server-authoritative AI field)
                                if (Math.Abs(atkRangeMult - 1f) > 0.001f)
                                {
                                    var aimDistField = attackType.GetField("AttackProjectileAimDistMax",
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (aimDistField != null)
                                    {
                                        string key = $"{name}_ca_{attackFieldName}";
                                        float orig;
                                        if (_originalCreatureAttackAimDist.ContainsKey(key))
                                            orig = _originalCreatureAttackAimDist[key];
                                        else
                                        {
                                            orig = (float)aimDistField.GetValue(attackObj);
                                            _originalCreatureAttackAimDist[key] = orig;
                                        }
                                        float newVal = orig * atkRangeMult;
                                        aimDistField.SetValue(attackObj, newVal);
                                        MelonLogger.Msg($"  CreatureAttack.{attackFieldName}.AimDistMax: {orig} -> {newVal} (direct)");
                                    }
                                }

                                // Scale AttackProjectileSpread (server-authoritative AI field)
                                if (Math.Abs(atkAccMult - 1f) > 0.001f)
                                {
                                    var spreadField = attackType.GetField("AttackProjectileSpread",
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (spreadField != null)
                                    {
                                        string key = $"{name}_spread_{attackFieldName}";
                                        float orig;
                                        if (_originalSpread.ContainsKey(key))
                                            orig = _originalSpread[key];
                                        else
                                        {
                                            orig = (float)spreadField.GetValue(attackObj);
                                            _originalSpread[key] = orig;
                                        }
                                        float newVal = orig * atkAccMult;
                                        spreadField.SetValue(attackObj, newVal);
                                        MelonLogger.Msg($"  CreatureAttack.{attackFieldName}.Spread: {orig} -> {newVal} (direct)");
                                    }
                                }

                                // Find ProjectileData referenced inside CreatureAttack
                                float atkLifetimeMult = GetWeaponMult(_lifetimeMultipliers, name, weapon);
                                bool hasAtkRange = Math.Abs(atkRangeMult - 1f) > 0.001f;
                                bool hasAtkSpeed = Math.Abs(atkSpeedMult - 1f) > 0.001f;
                                bool hasAtkLifetime = Math.Abs(atkLifetimeMult - 1f) > 0.001f;
                                if (hasAtkRange || hasAtkSpeed || hasAtkLifetime)
                                {
                                    var pdField = attackType.GetField("AttackProjectileData",
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (pdField != null)
                                    {
                                        object pdObj;
                                        try { pdObj = pdField.GetValue(attackObj); } catch { pdObj = null; }
                                        if (pdObj != null)
                                        {
                                            string pdName = "";
                                            try { pdName = ((UnityEngine.Object)pdObj).name; } catch { }
                                            if (!string.IsNullOrEmpty(pdName) && !modifiedPD.Contains(pdName))
                                            {
                                                ScaleProjectileData(pdObj, pdName,
                                                    hasAtkRange ? atkRangeMult : 1f, hasAtkSpeed ? atkSpeedMult : 1f, modifiedPD, useOM,
                                                    hasAtkLifetime ? atkLifetimeMult : 1f);
                                                foundProjectileOnComponent = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // VehicleTurret weapon params via OverrideManager (per-weapon multiplier resolution)
                    if (typeName == "VehicleTurret")
                    {
                        var vtType = comp.GetType();

                        foreach (string prefix in new[] { "Primary", "Secondary" })
                        {
                            string weapon = prefix == "Primary" ? "pri" : "sec";

                            if (hasReload)
                            {
                                float effReload = GetWeaponMult(_reloadTimeMultipliers, name, weapon);
                                if (Math.Abs(effReload - 1f) > 0.001f)
                                {
                                    var rtField = vtType.GetField($"{prefix}ReloadTime",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (rtField != null)
                                    {
                                        float orig = (float)rtField.GetValue(comp);
                                        float newVal = Math.Max(0.1f, orig * effReload);
                                        if (useOM) OMSetFloat(oiTarget, $"{prefix}ReloadTime", newVal);
                                        else rtField.SetValue(comp, newVal);
                                        MelonLogger.Msg($"  VehicleTurret.{prefix}ReloadTime: {orig:F2} -> {newVal:F2}");
                                    }
                                }
                            }

                            if (hasFireRate)
                            {
                                float effFR = GetWeaponMult(_fireRateMultipliers, name, weapon);
                                if (Math.Abs(effFR - 1f) > 0.001f)
                                {
                                    var fiField = vtType.GetField($"{prefix}FireInterval",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (fiField != null && fiField.FieldType == typeof(float))
                                    {
                                        float orig = (float)fiField.GetValue(comp);
                                        float newVal = Math.Max(0.01f, orig / effFR);
                                        if (useOM) OMSetFloat(oiTarget, $"{prefix}FireInterval", newVal);
                                        else fiField.SetValue(comp, newVal);
                                        MelonLogger.Msg($"  VehicleTurret.{prefix}FireInterval: {orig:F4} -> {newVal:F4}");
                                    }
                                }
                            }

                            if (hasMagazine)
                            {
                                float effMag = GetWeaponMult(_magazineMultipliers, name, weapon);
                                if (Math.Abs(effMag - 1f) > 0.001f)
                                {
                                    var msField = vtType.GetField($"{prefix}MagazineSize",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (msField != null && msField.FieldType == typeof(int))
                                    {
                                        int orig = (int)msField.GetValue(comp);
                                        int newVal = Math.Max(1, (int)Math.Round(orig * effMag));
                                        if (useOM) OMSetInt(oiTarget, $"{prefix}MagazineSize", newVal);
                                        else msField.SetValue(comp, newVal);
                                        MelonLogger.Msg($"  VehicleTurret.{prefix}MagazineSize: {orig} -> {newVal}");
                                    }
                                }
                            }

                            if (hasAccuracy)
                            {
                                float effAcc = GetWeaponMult(_accuracyMultipliers, name, weapon);
                                if (Math.Abs(effAcc - 1f) > 0.001f)
                                {
                                    var spField = vtType.GetField($"{prefix}MuzzleSpread",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (spField != null && spField.FieldType == typeof(float))
                                    {
                                        float orig = (float)spField.GetValue(comp);
                                        float newVal = orig * effAcc;
                                        if (useOM) OMSetFloat(oiTarget, $"{prefix}MuzzleSpread", newVal);
                                        else spField.SetValue(comp, newVal);
                                        MelonLogger.Msg($"  VehicleTurret.{prefix}MuzzleSpread: {orig:F4} -> {newVal:F4}");
                                    }
                                }
                            }
                        }
                    }

                    // Find ProjectileData references on components (per-weapon resolution by field name)
                    if (hasRange || hasSpeed || hasLifetime)
                    {
                        var fields = comp.GetType().GetFields(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        foreach (var field in fields)
                        {
                            if (field.FieldType.Name != "ProjectileData") continue;
                            object pdObj;
                            try { pdObj = field.GetValue(comp); } catch { continue; }
                            if (pdObj == null) continue;

                            string pdName = "";
                            try { pdName = ((UnityEngine.Object)pdObj).name; } catch { continue; }
                            if (string.IsNullOrEmpty(pdName) || modifiedPD.Contains(pdName)) continue;

                            // Determine weapon slot from field name (Secondary* -> sec, else pri)
                            string weapon = field.Name.StartsWith("Secondary") ? "sec" : "pri";
                            float effRange = GetWeaponMult(_rangeMultipliers, name, weapon);
                            float effSpeed = GetWeaponMult(_speedMultipliers, name, weapon);
                            float effLifetime = GetWeaponMult(_lifetimeMultipliers, name, weapon);
                            bool effHasRange = Math.Abs(effRange - 1f) > 0.001f;
                            bool effHasSpeed = Math.Abs(effSpeed - 1f) > 0.001f;
                            bool effHasLifetime = Math.Abs(effLifetime - 1f) > 0.001f;
                            if (!effHasRange && !effHasSpeed && !effHasLifetime) continue;

                            ScaleProjectileData(pdObj, pdName, effHasRange ? effRange : 1f, effHasSpeed ? effSpeed : 1f, modifiedPD, useOM,
                                effHasLifetime ? effLifetime : 1f);
                            foundProjectileOnComponent = true;
                        }
                    }
                }

                // Fallback: search all ProjectileData assets by name pattern (uses primary weapon mult)
                if (!foundProjectileOnComponent && (hasRange || hasSpeed || hasLifetime))
                {
                    float fbRange = GetWeaponMult(_rangeMultipliers, name, "pri");
                    float fbSpeed = GetWeaponMult(_speedMultipliers, name, "pri");
                    float fbLifetime = GetWeaponMult(_lifetimeMultipliers, name, "pri");
                    bool fbHasRange = Math.Abs(fbRange - 1f) > 0.001f;
                    bool fbHasSpeed = Math.Abs(fbSpeed - 1f) > 0.001f;
                    bool fbHasLifetime = Math.Abs(fbLifetime - 1f) > 0.001f;
                    MelonLogger.Msg($"  No ProjectileData found on components, searching by name...");
                    int fallbackCount = 0;
                    if (fbHasRange || fbHasSpeed || fbHasLifetime)
                    {
                        foreach (var pd in allProjectiles)
                        {
                            if (pd == null || modifiedPD.Contains(pd.name)) continue;
                            if (pd.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            ScaleProjectileData(pd, pd.name, fbHasRange ? fbRange : 1f, fbHasSpeed ? fbSpeed : 1f, modifiedPD, useOM,
                                fbHasLifetime ? fbLifetime : 1f);
                            fallbackCount++;
                        }
                    }
                    if (fallbackCount == 0)
                        MelonLogger.Warning($"  No ProjectileData asset found matching '{name}'");
                }

                applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[RANGE] Applied range overrides to {applied} units");
        }

        // =============================================
        // Move Speed: scale unit movement speed via OverrideManager
        // =============================================

        // Known speed fields per component type (all DeclaredOnly, reachable via OM prefab fallback)
        private static readonly string[] _speedFieldNames = {
            "MoveSpeed",       // CreatureDecapod, VehicleHovered, VehicleWheeled
            "FlyMoveSpeed",    // CreatureDecapod
            "WalkSpeed",       // Soldier (human infantry)
            "RunSpeed",        // Soldier (human infantry)
            "SprintSpeed",     // Soldier (human infantry)
            "ForwardSpeed",    // VehicleAir (Dropship, Shuttle, Gunship)
            "StrafeSpeed",     // VehicleAir
            "TurboSpeed",      // VehicleAir
        };

        // =============================================
        // Target distance: absolute override for Sensor.TargetingDistance (applied after range_mult)
        // =============================================

        private static void ApplyTargetDistanceOverrides(bool useOM)
        {
            if (_targetDistanceOverrides.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_targetDistanceOverrides.TryGetValue(name, out float targetDist)) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null || comp.GetType().Name != "Sensor") continue;

                    var tdField = comp.GetType().GetField("TargetingDistance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (tdField == null || tdField.FieldType != typeof(float)) continue;

                    float orig = (float)tdField.GetValue(comp);
                    if (useOM && OMSetFloat(oiTarget, "TargetingDistance", targetDist))
                    {
                        MelonLogger.Msg($"[TARGETDIST] {name}: Sensor.TargetingDistance {orig} -> {targetDist} (OM)");
                        applied++;
                    }
                    else if (!useOM)
                    {
                        tdField.SetValue(comp, targetDist);
                        MelonLogger.Msg($"[TARGETDIST] {name}: Sensor.TargetingDistance {orig} -> {targetDist} (direct)");
                        applied++;
                    }
                    break;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[TARGETDIST] Applied target distance overrides to {applied} units");
        }

        // =============================================
        // FoW Distance: absolute override for Sensor.FogOfWarViewDistance
        // =============================================
        private static void ApplyFoWDistanceOverrides(bool useOM)
        {
            if (_fowDistanceOverrides.Count == 0) return;
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_fowDistanceOverrides.TryGetValue(name, out float fowDist)) continue;
                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null || comp.GetType().Name != "Sensor") continue;
                    var fowField = comp.GetType().GetField("FogOfWarViewDistance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fowField == null || fowField.FieldType != typeof(float)) continue;
                    float orig = (float)fowField.GetValue(comp);
                    if (useOM && OMSetFloat(oiTarget, "FogOfWarViewDistance", fowDist))
                    {
                        MelonLogger.Msg($"[FOW] {name}: FogOfWarViewDistance {orig} -> {fowDist} (OM)");
                        applied++;
                    }
                    else if (!useOM)
                    {
                        fowField.SetValue(comp, fowDist);
                        MelonLogger.Msg($"[FOW] {name}: FogOfWarViewDistance {orig} -> {fowDist} (direct)");
                        applied++;
                    }
                    break;
                }
            }
            if (applied > 0)
                MelonLogger.Msg($"[FOW] Applied FoW distance overrides to {applied} units");
        }

        // =============================================
        // Jump Speed: scale Soldier.JumpSpeed via OverrideManager
        // =============================================
        private static void ApplyJumpSpeedOverrides(bool useOM)
        {
            if (_jumpSpeedMultipliers.Count == 0) return;
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_jumpSpeedMultipliers.TryGetValue(name, out float mult)) continue;
                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    if (typeName != "Soldier" && typeName != "PlayerMovement" && typeName != "FPSMovement") continue;
                    var jsField = comp.GetType().GetField("JumpSpeed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (jsField == null || jsField.FieldType != typeof(float)) continue;
                    float orig = (float)jsField.GetValue(comp);
                    float newVal = orig * mult;
                    if (useOM && OMSetFloat(oiTarget, "JumpSpeed", newVal))
                    {
                        MelonLogger.Msg($"[JUMP] {name}: JumpSpeed {orig:F1} -> {newVal:F1} x{mult:F2} (OM)");
                        applied++;
                    }
                    else if (!useOM)
                    {
                        jsField.SetValue(comp, newVal);
                        MelonLogger.Msg($"[JUMP] {name}: JumpSpeed {orig:F1} -> {newVal:F1} x{mult:F2} (direct)");
                        applied++;
                    }
                }
            }
            if (applied > 0)
                MelonLogger.Msg($"[JUMP] Applied jump speed overrides to {applied} units");
        }

        // =============================================
        // Visible Event Radius: scale ProjectileData.VisibleEventRadius
        // =============================================
        private static void ApplyVisibleEventRadiusOverrides(bool useOM)
        {
            if (_visibleEventRadiusMultipliers.Count == 0) return;
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_visibleEventRadiusMultipliers.TryGetValue(name, out float mult)) continue;
                // Find all ProjectileData referenced by this unit's turrets/weapons
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    if (typeName != "VehicleTurret") continue;
                    // Check PrimaryProjectile and SecondaryProjectile fields
                    foreach (string pdFieldName in new[] { "PrimaryProjectile", "SecondaryProjectile" })
                    {
                        var pdField = comp.GetType().GetField(pdFieldName,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (pdField == null) continue;
                        var pdObj = pdField.GetValue(comp);
                        if (pdObj == null) continue;
                        float origVER = GetFloatMember(pdObj, "VisibleEventRadius");
                        if (origVER <= 0) continue;
                        float newVER = origVER * mult;
                        string pdName = (pdObj as UnityEngine.Object)?.name ?? pdObj.ToString();
                        string pdTarget = useOM ? $"A:{pdName}.asset" : null;
                        if (useOM) OMSetFloat(pdTarget, "VisibleEventRadius", newVER);
                        else SetFloatMember(pdObj, "VisibleEventRadius", newVER);
                        MelonLogger.Msg($"[VER] {name}: {pdName} VisibleEventRadius {origVER:F0} -> {newVER:F0} x{mult:F2}");
                        applied++;
                    }
                }
            }
            if (applied > 0)
                MelonLogger.Msg($"[VER] Applied VisibleEventRadius overrides to {applied} projectiles");
        }

        private static void ApplyMoveSpeedOverrides(bool useOM)
        {
            if (_moveSpeedMultipliers.Count == 0 && _turboSpeedMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                bool hasMoveSpeed = _moveSpeedMultipliers.TryGetValue(name, out float mult);
                bool hasTurboSpeed = _turboSpeedMultipliers.TryGetValue(name, out float turboMult);
                if (!hasMoveSpeed && !hasTurboSpeed) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                if (hasMoveSpeed)
                    MelonLogger.Msg($"[MOVESPEED] Applying move_speed_mult x{mult:F2} to '{name}'{(useOM ? " (OM)" : "")}");
                if (hasTurboSpeed)
                    MelonLogger.Msg($"[MOVESPEED] Applying turbo_speed_mult x{turboMult:F2} to '{name}'{(useOM ? " (OM)" : "")}");

                if (useOM)
                {
                    // Search prefab components for known speed fields and override via OM
                    var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                    int fieldsSet = 0;
                    foreach (var comp in childComps)
                    {
                        if (comp == null) continue;
                        var compType = comp.GetType();
                        foreach (string fieldName in _speedFieldNames)
                        {
                            // TurboSpeed uses turbo_speed_mult; all others use move_speed_mult
                            float fieldMult;
                            if (fieldName == "TurboSpeed")
                            {
                                if (hasTurboSpeed) fieldMult = turboMult;
                                else if (hasMoveSpeed) fieldMult = mult;
                                else continue;
                            }
                            else
                            {
                                if (!hasMoveSpeed) continue;
                                fieldMult = mult;
                            }

                            var field = compType.GetField(fieldName,
                                BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                            if (field == null || field.FieldType != typeof(float)) continue;
                            float orig = (float)field.GetValue(comp);
                            if (orig <= 0) continue;
                            float newVal = orig * fieldMult;
                            if (OMSetFloat(oiTarget, fieldName, newVal))
                            {
                                MelonLogger.Msg($"  {compType.Name}.{fieldName}: {orig} -> {newVal} (OM)");
                                fieldsSet++;
                            }
                        }
                        if (fieldsSet > 0) break; // found the right component
                    }
                    if (fieldsSet > 0)
                        applied++;
                    else
                    {
                        // Fallback: direct mutation via ModifyPrefabSpeeds
                        if (hasMoveSpeed)
                        {
                            int modCount = ModifyPrefabSpeeds(info.Prefab, name, mult);
                            if (modCount > 0) applied++;
                        }
                    }
                }
                else
                {
                    if (hasMoveSpeed)
                    {
                        int modCount = ModifyPrefabSpeeds(info.Prefab, name, mult);
                        if (modCount > 0) applied++;
                    }
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[MOVESPEED] Applied move speed overrides to {applied} units");
        }

        private static void ApplyTurnRadiusOverrides(bool useOM)
        {
            if (_turnRadiusMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = info.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (!_turnRadiusMultipliers.TryGetValue(name, out float mult)) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    if (comp.GetType().Name != "VehicleWheeled") continue;

                    var field = comp.GetType().GetField("TurningCircleRadius",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (field == null || field.FieldType != typeof(float)) continue;

                    float orig = (float)field.GetValue(comp);
                    if (orig <= 0) continue;
                    float newVal = orig * mult;

                    if (useOM && OMSetFloat(oiTarget, "TurningCircleRadius", newVal))
                    {
                        MelonLogger.Msg($"[TURNRADIUS] {name}: TurningCircleRadius {orig:F1} -> {newVal:F1} (OM)");
                        applied++;
                    }
                    else
                    {
                        field.SetValue(comp, newVal);
                        MelonLogger.Msg($"[TURNRADIUS] {name}: TurningCircleRadius {orig:F1} -> {newVal:F1} (direct)");
                        applied++;
                    }
                    break;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[TURNRADIUS] Applied turn radius overrides to {applied} units");
        }

        private static void ApplyTeleportOverrides(bool useOM)
        {
            if (_teleportCooldown < 0 && _teleportDuration < 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            var flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    if (comp.GetType().Name != "TeleportUI") continue;

                    string name = info.DisplayName;
                    string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                    if (_teleportCooldown >= 0)
                    {
                        var f = comp.GetType().GetField("TeleportCooldownTime", flags);
                        if (f != null && f.FieldType == typeof(float))
                        {
                            float orig = (float)f.GetValue(comp);
                            if (useOM && OMSetFloat(oiTarget, "TeleportCooldownTime", _teleportCooldown))
                            {
                                MelonLogger.Msg($"[TELEPORT] {name}: CooldownTime {orig:F1} -> {_teleportCooldown:F1} (OM)");
                                applied++;
                            }
                            else
                            {
                                f.SetValue(comp, _teleportCooldown);
                                MelonLogger.Msg($"[TELEPORT] {name}: CooldownTime {orig:F1} -> {_teleportCooldown:F1} (direct)");
                                applied++;
                            }
                        }
                    }
                    if (_teleportDuration >= 0)
                    {
                        var f = comp.GetType().GetField("TeleportTime", flags);
                        if (f != null && f.FieldType == typeof(float))
                        {
                            float orig = (float)f.GetValue(comp);
                            if (useOM && OMSetFloat(oiTarget, "TeleportTime", _teleportDuration))
                            {
                                MelonLogger.Msg($"[TELEPORT] {name}: TeleportTime {orig:F1} -> {_teleportDuration:F1} (OM)");
                                applied++;
                            }
                            else
                            {
                                f.SetValue(comp, _teleportDuration);
                                MelonLogger.Msg($"[TELEPORT] {name}: TeleportTime {orig:F1} -> {_teleportDuration:F1} (direct)");
                                applied++;
                            }
                        }
                    }
                    break;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[TELEPORT] Applied teleport overrides ({applied} fields)");
        }

        private static void ApplyDispenserTimeoutOverrides(bool useOM)
        {
            if (_dispenseTimeout < 0) return;

            int applied = 0;
            var flags = BindingFlags.Public | BindingFlags.Instance;

            // Find all VehicleDispenser components in all ObjectInfo prefabs
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    if (comp.GetType().Name != "VehicleDispenser") continue;

                    string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                    var f = comp.GetType().GetField("DispenseTimeout", flags);
                    if (f != null && f.FieldType == typeof(float))
                    {
                        float orig = (float)f.GetValue(comp);
                        if (useOM && OMSetFloat(oiTarget, "DispenseTimeout", _dispenseTimeout))
                        {
                            MelonLogger.Msg($"[DISPENSER] {info.DisplayName}: DispenseTimeout {orig:F1} -> {_dispenseTimeout:F1} (OM)");
                            applied++;
                        }
                        else
                        {
                            f.SetValue(comp, _dispenseTimeout);
                            MelonLogger.Msg($"[DISPENSER] {info.DisplayName}: DispenseTimeout {orig:F1} -> {_dispenseTimeout:F1} (direct)");
                            applied++;
                        }
                    }
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[DISPENSER] Applied dispense timeout overrides ({applied} dispensers)");
        }

        // Shared helper: scan all components on a prefab for speed-related float fields/properties
        private static int ModifyPrefabSpeeds(GameObject prefab, string unitName, float mult)
        {
            int totalModified = 0;
            var components = prefab.GetComponentsInChildren<Component>(true);

            foreach (var comp in components)
            {
                if (comp == null) continue;
                var compType = comp.GetType();
                string typeName = compType.Name;

                // Skip rendering/physics/UI components — only look at gameplay components
                string lower = typeName.ToLower();
                if (lower == "transform" || lower.Contains("mesh") || lower.Contains("renderer") ||
                    lower.Contains("animator") || lower.Contains("lodgroup") || lower.Contains("particle") ||
                    lower.Contains("collider") || lower.Contains("light") || lower.Contains("audio") ||
                    lower.Contains("canvas") || lower.Contains("cloth") || lower.Contains("rigidbody"))
                    continue;

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // Search fields for speed-related floats
                foreach (var field in compType.GetFields(flags))
                {
                    if (field.FieldType != typeof(float)) continue;
                    string fname = field.Name.ToLower();
                    if (!fname.Contains("speed")) continue;
                    // Exclude non-movement speeds
                    if (fname.Contains("turn") || fname.Contains("rotation") || fname.Contains("projectile") ||
                        fname.Contains("anim") || fname.Contains("reload") || fname.Contains("fire") ||
                        fname.Contains("attack") || fname.Contains("cool") ||
                        fname.Contains("shutter") || fname.Contains("landing")) continue;

                    try
                    {
                        float orig = (float)field.GetValue(comp);
                        if (Math.Abs(orig) < 0.001f) continue;

                        string key = $"{unitName}_{comp.GetInstanceID()}_{field.Name}";
                        if (!_originalMoveSpeeds.ContainsKey(key))
                            _originalMoveSpeeds[key] = orig;
                        else
                            orig = _originalMoveSpeeds[key];

                        float newVal = orig * mult;
                        field.SetValue(comp, newVal);
                        MelonLogger.Msg($"  [{typeName}] field {field.Name}: {orig} -> {newVal}");
                        totalModified++;
                    }
                    catch { }
                }

                // Search properties for speed-related writable floats
                foreach (var prop in compType.GetProperties(flags))
                {
                    if (prop.PropertyType != typeof(float) || !prop.CanRead || !prop.CanWrite) continue;
                    string pname = prop.Name.ToLower();
                    if (!pname.Contains("speed")) continue;
                    if (pname.Contains("turn") || pname.Contains("rotation") || pname.Contains("projectile") ||
                        pname.Contains("anim") || pname.Contains("reload") || pname.Contains("fire") ||
                        pname.Contains("attack") || pname.Contains("cool") ||
                        pname.Contains("shutter") || pname.Contains("landing")) continue;

                    try
                    {
                        float orig = (float)prop.GetValue(comp);
                        if (Math.Abs(orig) < 0.001f) continue;

                        string key = $"{unitName}_{comp.GetInstanceID()}_{prop.Name}";
                        if (!_originalMoveSpeeds.ContainsKey(key))
                            _originalMoveSpeeds[key] = orig;
                        else
                            orig = _originalMoveSpeeds[key];

                        float newVal = orig * mult;
                        prop.SetValue(comp, newVal);
                        MelonLogger.Msg($"  [{typeName}] prop {prop.Name}: {orig} -> {newVal}");
                        totalModified++;
                    }
                    catch { }
                }
            }

            if (totalModified == 0)
            {
                MelonLogger.Warning($"[MOVESPEED] No speed fields found on '{unitName}' — listing gameplay components:");
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string tn = comp.GetType().Name.ToLower();
                    if (tn == "transform" || tn.Contains("mesh") || tn.Contains("renderer") ||
                        tn.Contains("animator") || tn.Contains("lodgroup") || tn.Contains("particle") ||
                        tn.Contains("collider") || tn.Contains("light") || tn.Contains("audio") ||
                        tn.Contains("canvas") || tn.Contains("cloth") || tn.Contains("rigidbody"))
                        continue;
                    MelonLogger.Msg($"  Component: {comp.GetType().Name} on '{comp.gameObject.name}'");
                }
            }

            return totalModified;
        }

        // Helper: get/set float field or property (Il2Cpp exposes fields as properties)
        private static float GetFloatMember(object obj, string name)
        {
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return (float)field.GetValue(obj);
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null) return (float)prop.GetValue(obj);
            return 0f;
        }

        private static bool SetFloatMember(object obj, string name, float value)
        {
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { field.SetValue(obj, value); return true; }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite) { prop.SetValue(obj, value); return true; }
            return false;
        }

        private static void ScaleProjectileData(object pdObj, string pdName, float rangeMult, float speedMult,
            HashSet<string> modifiedPD, bool useOM = false, float lifetimeMult = 1f)
        {
            string pdTarget = useOM ? $"A:{pdName}.asset" : null;
            float origLt = GetFloatMember(pdObj, "m_fLifeTime");
            float origSpeed = GetFloatMember(pdObj, "m_fBaseSpeed");

            // Check if this is an instant-hit (hitscan/ray) projectile
            bool isInstantHit = false;
            try
            {
                var ihField = pdObj.GetType().GetField("m_InstantHit",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ihField != null)
                    isInstantHit = (bool)ihField.GetValue(pdObj);
                else
                {
                    var ihProp = pdObj.GetType().GetProperty("m_InstantHit",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (ihProp != null && ihProp.CanRead)
                        isInstantHit = (bool)ihProp.GetValue(pdObj);
                }
            }
            catch { }

            if (isInstantHit)
            {
                // For instant-hit: m_fBaseSpeed IS the raycast distance, not speed.
                float newSpeed = origSpeed * rangeMult;
                if (Math.Abs(rangeMult - 1f) > 0.001f && origSpeed > 0)
                {
                    if (useOM) OMSetFloat(pdTarget, "m_fBaseSpeed", newSpeed);
                    else SetFloatMember(pdObj, "m_fBaseSpeed", newSpeed);
                }
                if (Math.Abs(speedMult - 1f) > 0.001f && origSpeed > 0)
                {
                    float finalSpeed = newSpeed * speedMult;
                    if (useOM) OMSetFloat(pdTarget, "m_fBaseSpeed", finalSpeed);
                    else SetFloatMember(pdObj, "m_fBaseSpeed", finalSpeed);
                    newSpeed = finalSpeed;
                }
                MelonLogger.Msg($"  ProjectileData '{pdName}' (instant-hit): Range {origSpeed:F0} -> {newSpeed:F0}{(useOM ? " (OM)" : "")}");
            }
            else
            {
                // Normal projectile: range = speed * lifetime
                float newLt = origLt * rangeMult * lifetimeMult;
                float newSpeed = origSpeed * speedMult;
                float origRange = origSpeed * origLt;
                float newRange = newSpeed * newLt;

                bool ltChanged = Math.Abs(rangeMult * lifetimeMult - 1f) > 0.001f;
                if (ltChanged && origLt > 0)
                {
                    if (useOM) OMSetFloat(pdTarget, "m_fLifeTime", newLt);
                    else SetFloatMember(pdObj, "m_fLifeTime", newLt);
                    MelonLogger.Msg($"  ProjectileData '{pdName}': Lifetime {origLt} -> {newLt:F2}");
                }

                if (Math.Abs(speedMult - 1f) > 0.001f && origSpeed > 0)
                {
                    if (useOM) OMSetFloat(pdTarget, "m_fBaseSpeed", newSpeed);
                    else SetFloatMember(pdObj, "m_fBaseSpeed", newSpeed);
                    MelonLogger.Msg($"  ProjectileData '{pdName}': Speed {origSpeed} -> {newSpeed:F1}");
                }

                MelonLogger.Msg($"  ProjectileData '{pdName}': Range {origRange:F0} -> {newRange:F0}{(useOM ? " (OM)" : "")}");
            }

            // For instant-hit: lifetime mult also applies (visual beam duration)
            if (isInstantHit && Math.Abs(lifetimeMult - 1f) > 0.001f && origLt > 0)
            {
                float newLt = origLt * lifetimeMult;
                if (useOM) OMSetFloat(pdTarget, "m_fLifeTime", newLt);
                else SetFloatMember(pdObj, "m_fLifeTime", newLt);
                MelonLogger.Msg($"  ProjectileData '{pdName}' (instant-hit): Lifetime {origLt} -> {newLt:F2}");
            }

            // Scale VisibleEventRadius for instant-hit projectiles (beams need VER to match range)
            // Normal projectiles (flames, balls) don't need VER scaling — projectile renders independently
            if (Math.Abs(rangeMult - 1f) > 0.001f && isInstantHit)
            {
                float origVER = GetFloatMember(pdObj, "VisibleEventRadius");
                if (origVER > 0)
                {
                    float newVER = origVER * rangeMult;
                    if (useOM) OMSetFloat(pdTarget, "VisibleEventRadius", newVER);
                    else SetFloatMember(pdObj, "VisibleEventRadius", newVER);
                    MelonLogger.Msg($"  ProjectileData '{pdName}': VisibleEventRadius {origVER:F0} -> {newVER:F0}");
                }
            }

            modifiedPD.Add(pdName);
        }

        // =============================================
        // Shrimp: prevent AI from auto-attacking (player attacks still work)
        // =============================================

        private static void ApplyShrimpAimDisable()
        {
            try
            {
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                foreach (var info in allInfos)
                {
                    if (info == null || info.DisplayName != "Shrimp" || info.Prefab == null)
                        continue;

                    var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                    foreach (var comp in childComps)
                    {
                        if (comp == null) continue;
                        string typeName = comp.GetType().Name;

                        // 1) CreatureDecapod: set AIMeleeDistance = 0 so AI never initiates melee
                        if (typeName == "CreatureDecapod")
                        {
                            var compType = comp.GetType();
                            var meleeDistField = compType.GetField("AIMeleeDistance",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (meleeDistField != null)
                            {
                                float was = (float)meleeDistField.GetValue(comp);
                                meleeDistField.SetValue(comp, 0f);
                                MelonLogger.Msg($"[SHRIMP] AIMeleeDistance: {was} -> 0");
                            }
                        }

                        // 2) Sensor: set TargetingDistance = 0 so AI can't detect enemies
                        //    (FogOfWarViewDistance is separate — vision stays intact)
                        if (typeName == "Sensor")
                        {
                            var targetDistField = comp.GetType().GetField("TargetingDistance",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (targetDistField != null)
                            {
                                float was = (float)targetDistField.GetValue(comp);
                                targetDistField.SetValue(comp, 0f);
                                MelonLogger.Msg($"[SHRIMP] Sensor.TargetingDistance: {was} -> 0");
                            }
                        }

                        // 3) AIAiming: pause aim tracking
                        if (typeName == "AIAiming")
                        {
                            var aimPausedField = comp.GetType().GetField("AimPaused",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (aimPausedField != null)
                            {
                                aimPausedField.SetValue(comp, true);
                                MelonLogger.Msg($"[SHRIMP] AIAiming.AimPaused -> True");
                            }
                        }
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SHRIMP] ApplyShrimpAimDisable error: {ex.Message}");
            }
        }
    }
}

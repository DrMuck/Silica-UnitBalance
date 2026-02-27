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
        // Propagate overrides to live scene instances
        // =============================================
        // OM modifies prefab/asset data only — live instances have independent
        // MonoBehaviour component copies from spawn time. After all Apply* methods
        // run, this method finds every spawned unit in the scene and applies the
        // same overrides via direct field mutation.

        private static void PropagateToLiveInstances()
        {
            int liveCount = 0, totalUpdated = 0;
            var processedGOs = new HashSet<int>();

            // Collect live scene objects via Unit and Structure MonoBehaviour components
            // (ObjectInfo is a ScriptableObject — can't enumerate scene instances from it)
            var liveUnits = UnityEngine.Object.FindObjectsOfType<Unit>();
            var liveStructures = UnityEngine.Object.FindObjectsOfType<Structure>();

            // Process units
            foreach (var unit in liveUnits)
            {
                if (unit == null || unit.ObjectInfo == null || unit.ObjectInfo.Prefab == null) continue;
                PropagateToInstance(unit.gameObject, unit.ObjectInfo, processedGOs, ref liveCount, ref totalUpdated);
            }

            // Process structures
            foreach (var structure in liveStructures)
            {
                if (structure == null || structure.ObjectInfo == null || structure.ObjectInfo.Prefab == null) continue;
                PropagateToInstance(structure.gameObject, structure.ObjectInfo, processedGOs, ref liveCount, ref totalUpdated);
            }

            if (liveCount > 0 || totalUpdated > 0)
                MelonLogger.Msg($"[LIVE] Propagated overrides to {totalUpdated}/{liveCount} live instances");
        }

        private static void PropagateToInstance(GameObject liveGO, ObjectInfo info, HashSet<int> processedGOs,
            ref int liveCount, ref int totalUpdated)
        {
            int goId = liveGO.GetInstanceID();
            if (processedGOs.Contains(goId)) return;
            processedGOs.Add(goId);
            liveCount++;

            string name = info.DisplayName;
            if (string.IsNullOrEmpty(name)) return;

            // Gather all overrides for this unit name (weapon params use HasAnyWeaponMult for per-weapon keys)
            bool hasRange = HasAnyWeaponMult(_rangeMultipliers, name);
            bool hasReload = HasAnyWeaponMult(_reloadTimeMultipliers, name);
            bool hasAccuracy = HasAnyWeaponMult(_accuracyMultipliers, name);
            bool hasMagazine = HasAnyWeaponMult(_magazineMultipliers, name);
            bool hasFireRate = HasAnyWeaponMult(_fireRateMultipliers, name);
            bool hasMoveSpeed = _moveSpeedMultipliers.TryGetValue(name, out float moveMult);
            bool hasTurboSpeed = _turboSpeedMultipliers.TryGetValue(name, out float turboMult);
            bool hasStrafeSpeed = _strafeSpeedMultipliers.TryGetValue(name, out float strafeMult);
            bool hasTurnRadius = _turnRadiusMultipliers.TryGetValue(name, out float turnMult);
            bool hasTargetDist = _targetDistanceOverrides.TryGetValue(name, out float targetDist);
            bool hasFoW = _fowDistanceOverrides.TryGetValue(name, out float fowDist);
            bool hasJump = _jumpSpeedMultipliers.TryGetValue(name, out float jumpMult);

            if (!hasRange && !hasReload && !hasAccuracy && !hasMagazine && !hasFireRate
                && !hasMoveSpeed && !hasTurboSpeed && !hasStrafeSpeed && !hasTurnRadius
                && !hasTargetDist && !hasFoW && !hasJump) return;

            // Build prefab component lookup by type (for reading vanilla values)
            var prefabComps = info.Prefab.GetComponentsInChildren<Component>(true);
            var prefabByType = new Dictionary<string, Component>();
            foreach (var pc in prefabComps)
            {
                if (pc == null) continue;
                string tn = pc.GetType().Name;
                if (!prefabByType.ContainsKey(tn))
                    prefabByType[tn] = pc;
            }

            // Iterate live instance components and apply overrides
            var liveComps = liveGO.GetComponentsInChildren<Component>(true);
            int fieldsSet = 0;
            bool logDetail = (fieldsSet == 0); // log first few units for debugging

            foreach (var liveComp in liveComps)
            {
                if (liveComp == null) continue;
                string typeName = liveComp.GetType().Name;
                var compType = liveComp.GetType();
                prefabByType.TryGetValue(typeName, out Component prefabComp);

                // --- VehicleTurret: AimDistance, per-weapon reload/fire rate/magazine/accuracy ---
                if (typeName == "VehicleTurret" && prefabComp != null)
                {
                    if (hasRange)
                    {
                        float vtRange = GetWeaponMult(_rangeMultipliers, name, "pri");
                        if (Math.Abs(vtRange - 1f) > 0.001f)
                            fieldsSet += LiveScaleFieldLog(prefabComp, liveComp, "AimDistance", vtRange, name);
                    }

                    foreach (string prefix in new[] { "Primary", "Secondary" })
                    {
                        string weapon = prefix == "Primary" ? "pri" : "sec";
                        if (hasReload)
                        {
                            float effReload = GetWeaponMult(_reloadTimeMultipliers, name, weapon);
                            if (Math.Abs(effReload - 1f) > 0.001f)
                                fieldsSet += LiveScaleField(prefabComp, liveComp, $"{prefix}ReloadTime", effReload, 0.1f);
                        }
                        if (hasFireRate)
                        {
                            float effFR = GetWeaponMult(_fireRateMultipliers, name, weapon);
                            if (Math.Abs(effFR - 1f) > 0.001f)
                                fieldsSet += LiveScaleField(prefabComp, liveComp, $"{prefix}FireInterval", 1f / effFR, 0.01f);
                        }
                        if (hasMagazine)
                        {
                            float effMag = GetWeaponMult(_magazineMultipliers, name, weapon);
                            if (Math.Abs(effMag - 1f) > 0.001f)
                                fieldsSet += LiveScaleIntField(prefabComp, liveComp, $"{prefix}MagazineSize", effMag);
                        }
                        if (hasAccuracy)
                        {
                            float effAcc = GetWeaponMult(_accuracyMultipliers, name, weapon);
                            if (Math.Abs(effAcc - 1f) > 0.001f)
                                fieldsSet += LiveScaleField(prefabComp, liveComp, $"{prefix}MuzzleSpread", effAcc);
                        }
                    }
                }

                // --- UnitAimAt: AimDistanceMax (shared — uses primary range mult) ---
                if (typeName == "UnitAimAt" && prefabComp != null && hasRange)
                {
                    float uaRange = GetWeaponMult(_rangeMultipliers, name, "pri");
                    if (Math.Abs(uaRange - 1f) > 0.001f)
                        fieldsSet += LiveScaleFieldLog(prefabComp, liveComp, "AimDistanceMax", uaRange, name);
                }

                // --- Sensor: TargetingDistance, FoW ---
                if (typeName == "Sensor")
                {
                    if (hasTargetDist)
                        fieldsSet += LiveSetAbsolute(liveComp, "TargetingDistance", targetDist);
                    if (hasFoW)
                        fieldsSet += LiveSetAbsolute(liveComp, "FogOfWarViewDistance", fowDist);
                }

                // --- Soldier / movement: JumpSpeed + speed fields ---
                if (typeName == "Soldier" || typeName == "PlayerMovement" || typeName == "FPSMovement")
                {
                    if (hasJump && prefabComp != null)
                        fieldsSet += LiveScaleFieldLog(prefabComp, liveComp, "JumpSpeed", jumpMult, name);
                }

                // --- VehicleWheeled: TurningCircleRadius ---
                if (typeName == "VehicleWheeled" && prefabComp != null && hasTurnRadius)
                    fieldsSet += LiveScaleFieldLog(prefabComp, liveComp, "TurningCircleRadius", turnMult, name);

                // --- CreatureDecapod: propagate per-weapon attack fields from prefab ---
                if (typeName == "CreatureDecapod" && prefabComp != null)
                {
                    foreach (string atkName in new[] { "AttackPrimary", "AttackSecondary" })
                    {
                        string weapon = atkName == "AttackPrimary" ? "pri" : "sec";
                        var atkField = compType.GetField(atkName, BindingFlags.Public | BindingFlags.Instance);
                        if (atkField == null) continue;
                        object liveAtk = null, prefabAtk = null;
                        try { liveAtk = atkField.GetValue(liveComp); } catch { }
                        try { prefabAtk = atkField.GetValue(prefabComp); } catch { }
                        if (liveAtk == null || prefabAtk == null) continue;

                        var atkType = liveAtk.GetType();

                        // Copy AimDistMax from prefab (already overridden by ApplyRangeOverrides)
                        float atkRange = GetWeaponMult(_rangeMultipliers, name, weapon);
                        if (Math.Abs(atkRange - 1f) > 0.001f)
                        {
                            var f = atkType.GetField("AttackProjectileAimDistMax", BindingFlags.Public | BindingFlags.Instance);
                            if (f != null)
                            {
                                f.SetValue(liveAtk, f.GetValue(prefabAtk));
                                fieldsSet++;
                            }
                        }
                        // Copy Spread from prefab
                        float atkAcc = GetWeaponMult(_accuracyMultipliers, name, weapon);
                        if (Math.Abs(atkAcc - 1f) > 0.001f)
                        {
                            var f = atkType.GetField("AttackProjectileSpread", BindingFlags.Public | BindingFlags.Instance);
                            if (f != null)
                            {
                                f.SetValue(liveAtk, f.GetValue(prefabAtk));
                                fieldsSet++;
                            }
                        }
                    }
                }

                // --- Any component with speed fields (VehicleHovered, VehicleAir, CreatureDecapod, etc.) ---
                if ((hasMoveSpeed || hasTurboSpeed) && prefabComp != null)
                {
                    foreach (string fn in _speedFieldNames)
                    {
                        float fieldMult;
                        if (fn == "TurboSpeed")
                        {
                            if (hasTurboSpeed) fieldMult = turboMult;
                            else if (hasMoveSpeed) fieldMult = moveMult;
                            else continue;
                        }
                        else
                        {
                            if (!hasMoveSpeed) continue;
                            fieldMult = moveMult;
                        }
                        fieldsSet += LiveScaleFieldDeclaredOnlyLog(prefabComp, liveComp, fn, fieldMult, name);
                    }
                }

                // --- Strafe speed: FlyMoveScaleSide (creature) or StrafeSpeed (VehicleAir) ---
                if (hasStrafeSpeed && prefabComp != null)
                {
                    if (typeName == "CreatureDecapod")
                        fieldsSet += LiveScaleField(prefabComp, liveComp, "FlyMoveScaleSide", strafeMult);
                    else if (typeName == "VehicleAir")
                        fieldsSet += LiveScaleFieldDeclaredOnly(prefabComp, liveComp, "StrafeSpeed", strafeMult);
                }
            }

            if (fieldsSet > 0)
                totalUpdated++;
        }

        // Read vanilla float from prefab component, multiply, write to live component
        private static int LiveScaleField(Component prefab, Component live, string fieldName, float multiplier, float minVal = 0f)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(float)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                float vanilla = (float)pfField.GetValue(prefab);
                float newVal = vanilla * multiplier;
                if (minVal > 0 && newVal < minVal) newVal = minVal;
                liveField.SetValue(live, newVal);
                return 1;
            }
            catch { return 0; }
        }

        // Same but DeclaredOnly (for speed fields that need type-specific binding)
        private static int LiveScaleFieldDeclaredOnly(Component prefab, Component live, string fieldName, float multiplier)
        {
            try
            {
                var flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(float)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                float vanilla = (float)pfField.GetValue(prefab);
                if (vanilla <= 0) return 0;
                liveField.SetValue(live, vanilla * multiplier);
                return 1;
            }
            catch { return 0; }
        }

        // Read vanilla int from prefab, multiply, write to live
        private static int LiveScaleIntField(Component prefab, Component live, string fieldName, float multiplier)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(int)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                int vanilla = (int)pfField.GetValue(prefab);
                int newVal = Math.Max(1, (int)Math.Round(vanilla * multiplier));
                liveField.SetValue(live, newVal);
                return 1;
            }
            catch { return 0; }
        }

        // Logging version: read vanilla, multiply, write, and log the before/after
        private static int LiveScaleFieldLog(Component prefab, Component live, string fieldName, float multiplier, string unitName)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(float)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                float vanilla = (float)pfField.GetValue(prefab);
                float oldLive = (float)liveField.GetValue(live);
                float newVal = vanilla * multiplier;
                liveField.SetValue(live, newVal);
                float verify = (float)liveField.GetValue(live);
                MelonLogger.Msg($"[LIVE-DBG] {unitName}: {fieldName} prefab={vanilla:F2} old={oldLive:F2} -> new={newVal:F2} verify={verify:F2}");
                return 1;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[LIVE-DBG] {unitName}: {fieldName} error: {ex.Message}");
                return 0;
            }
        }

        // Logging version for DeclaredOnly speed fields
        private static int LiveScaleFieldDeclaredOnlyLog(Component prefab, Component live, string fieldName, float multiplier, string unitName)
        {
            try
            {
                var flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
                var pfField = prefab.GetType().GetField(fieldName, flags);
                if (pfField == null || pfField.FieldType != typeof(float)) return 0;
                var liveField = live.GetType().GetField(fieldName, flags);
                if (liveField == null) return 0;
                float vanilla = (float)pfField.GetValue(prefab);
                if (vanilla <= 0) return 0;
                float oldLive = (float)liveField.GetValue(live);
                float newVal = vanilla * multiplier;
                liveField.SetValue(live, newVal);
                float verify = (float)liveField.GetValue(live);
                MelonLogger.Msg($"[LIVE-DBG] {unitName}: {fieldName} prefab={vanilla:F2} old={oldLive:F2} -> new={newVal:F2} verify={verify:F2}");
                return 1;
            }
            catch { return 0; }
        }

        // Set absolute float value on live component
        private static int LiveSetAbsolute(Component live, string fieldName, float value)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var field = live.GetType().GetField(fieldName, flags);
                if (field == null || field.FieldType != typeof(float)) return 0;
                field.SetValue(live, value);
                return 1;
            }
            catch { return 0; }
        }

        // =============================================
        // Name resolution (units + structures)
        // =============================================

        private static string GetCachedName(GameObject obj)
        {
            if (obj == null) return null;

            int id = obj.GetInstanceID();
            if (_nameCache.TryGetValue(id, out string cached))
                return cached;

            string name = null;

            try
            {
                var unit = obj.GetComponent<Unit>();
                if (unit?.ObjectInfo != null)
                    name = unit.ObjectInfo.DisplayName;
            }
            catch { }

            if (name == null)
            {
                try
                {
                    var structure = obj.GetComponent<Structure>();
                    if (structure?.ObjectInfo != null)
                        name = structure.ObjectInfo.DisplayName;
                }
                catch { }
            }

            if (name != null)
                _nameCache[id] = name;

            return name;
        }

        // =============================================
        // JSON dump: export ALL unit/structure data to JSON for spreadsheet
        // =============================================

        private static void DumpAllUnitsJson()
        {
            try
            {
                string outPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_configPath),
                    "Si_UnitBalance_Dump.json");

                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                MelonLogger.Msg($"[JSON DUMP] Found {allInfos.Length} ObjectInfo entries, writing to {outPath}");

                // Build production tree: structure display name -> list of buildable display names
                var prodTree = new Dictionary<string, List<string>>();
                // Build reverse map: unit name -> which structure produces it
                var builtAt = new Dictionary<string, string>();

                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    var structure = info.Prefab.GetComponent<Structure>();
                    if (structure == null || structure.ConstructionOptions == null) continue;

                    string structName = info.DisplayName;
                    if (string.IsNullOrEmpty(structName)) continue;

                    // Skip tutorial/TD variants
                    if (info.name != null && (info.name.Contains("_TD") || info.name.Contains("_Intro") || info.name.Contains("_Tutorial")))
                        continue;

                    string team = "?";
                    try { if (structure.DefaultTeam != null) team = structure.DefaultTeam.name; } catch { }

                    foreach (var cd in structure.ConstructionOptions)
                    {
                        if (cd == null || cd.ObjectInfo == null) continue;
                        string prodName = cd.ObjectInfo.DisplayName;
                        if (string.IsNullOrEmpty(prodName)) continue;

                        if (!prodTree.ContainsKey(structName))
                            prodTree[structName] = new List<string>();
                        if (!prodTree[structName].Contains(prodName))
                            prodTree[structName].Add(prodName);

                        // First non-tutorial producer wins
                        if (!builtAt.ContainsKey(prodName))
                            builtAt[prodName] = structName;
                    }
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"units\": [");

                var seen = new HashSet<string>();
                bool first = true;

                foreach (var info in allInfos)
                {
                    if (info == null) continue;
                    string name = info.DisplayName;
                    if (string.IsNullOrEmpty(name)) continue;

                    // Skip tutorial/TD variants by internal name
                    string internalName = info.name ?? "";
                    if (internalName.Contains("_TD") || internalName.Contains("_Intro") || internalName.Contains("_Tutorial"))
                        continue;

                    // Skip duplicates by internal name (not display name — Sol/Cent can share display names)
                    if (seen.Contains(internalName)) continue;

                    // Must have either ConstructionData or be a producible unit
                    if (info.ConstructionData == null) continue;
                    seen.Add(internalName);

                    // Basic info
                    var cd = info.ConstructionData;
                    int cost = cd.ResourceCost;
                    float buildTime = cd.BuildUpTime;
                    int minTier = cd.MinimumTeamTier;
                    int maxTier = cd.MaximumTeamTier;
                    float maxDist = cd.MaximumBaseStructureDistance;
                    bool useRadius = cd.MaximumDistanceUseRadius;

                    // Team
                    string team = "?";
                    string faction = "Unknown";

                    // HP
                    int maxHealth = 0;
                    if (info.Prefab != null)
                    {
                        var dm = info.Prefab.GetComponent<DamageManager>();
                        if (dm != null)
                        {
                            try { maxHealth = (int)dm.MaxHealth; } catch { }
                        }
                    }

                    // Determine faction
                    if (info.Prefab != null)
                    {
                        var structure = info.Prefab.GetComponent<Structure>();
                        if (structure != null)
                        {
                            try { if (structure.DefaultTeam != null) team = structure.DefaultTeam.name; } catch { }
                        }
                        var unit = info.Prefab.GetComponent<Unit>();
                        if (unit != null)
                        {
                            try { if (unit.DefaultTeam != null) team = unit.DefaultTeam.name; } catch { }
                        }
                    }
                    if (team.Contains("Sol")) faction = "Sol";
                    else if (team.Contains("Centauri")) faction = "Centauri";
                    else if (team.Contains("Alien")) faction = "Alien";

                    // Type: structure or unit
                    bool isStructure = false;
                    if (info.Prefab != null)
                    {
                        var structure = info.Prefab.GetComponent<Structure>();
                        isStructure = (structure != null);
                    }

                    // Built at
                    string producer = builtAt.ContainsKey(name) ? builtAt[name] : "";

                    // Movement
                    float moveSpeed = 0, flyMoveSpeed = 0;
                    float walkSpeed = 0, runSpeed = 0, jumpSpeed = 0;
                    // Vehicle movement detail
                    string vehicleType = "";  // "Wheeled", "Hovered", or ""
                    float vehAccel = 0;       // WheelMotorAccel (%) or MoveAcceleration (m/s^2)
                    float vehTurnRadius = 0;  // TurningCircleRadius (wheeled)
                    float vehTurnSpeed = 0;   // TurnSpeed (hovered, deg/s)
                    float vehReverseScale = 0; // ReverseSpeedScale (wheeled)

                    // Attack (creature)
                    float atkDamage = 0, atkCooldown = 0, atkRange = 0, atkSpread = 0;
                    string atkProjName = "";
                    float atkDamageSec = 0, atkCooldownSec = 0, atkRangeSec = 0, atkSpreadSec = 0;
                    string atkProjNameSec = "";

                    // Projectile data
                    float projSpeed = 0, projLifetime = 0;
                    bool instantHit = false;
                    float projSpeedSec = 0, projLifetimeSec = 0;
                    bool instantHitSec = false;

                    // Detection
                    float fowView = 0, targetDist = 0;

                    // Vehicle turret
                    float vtFireInterval = 0, vtMuzzleSpread = 0;
                    int vtMagazine = 0, vtShotCount = 0;
                    float vtReloadTime = 0;
                    float vtFireIntervalSec = 0, vtMuzzleSpreadSec = 0, vtReloadTimeSec = 0;
                    int vtMagazineSec = 0, vtShotCountSec = 0;
                    // VT projectile data
                    string vtProjName = "", vtProjName2 = "";
                    float vtImpactDmg = 0, vtRicochetDmg = 0, vtSplashDmg = 0, vtPenDmg = 0;
                    bool vtHasSplash = false, vtHasPen = false, vtInstantHit = false;
                    float vtProjSpeed = 0, vtProjLifetime = 0;
                    float vtImpactDmg2 = 0, vtRicochetDmg2 = 0, vtSplashDmg2 = 0, vtPenDmg2 = 0;
                    bool vtHasSplash2 = false, vtHasPen2 = false, vtInstantHit2 = false;
                    float vtProjSpeed2 = 0, vtProjLifetime2 = 0;
                    // Creature projectile damage
                    float projImpactDmg = 0, projRicochetDmg = 0, projSplashDmg = 0;
                    float projImpactDmg2 = 0, projRicochetDmg2 = 0, projSplashDmg2 = 0;
                    // TeleportUI (on structures)
                    bool hasTeleport = false;
                    float teleportTime = 0, teleportCooldown = 0;
                    float teleportPct = 0;
                    bool teleportSameTeamOnly = false;

                    if (info.Prefab != null)
                    {
                        // Creature movement + attacks
                        var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                        foreach (var comp in childComps)
                        {
                            if (comp == null) continue;
                            var ct = comp.GetType();
                            string tn = ct.Name;

                            // CreatureDecapod: movement + attacks
                            if (tn == "CreatureDecapod")
                            {
                                try { var f = ct.GetField("MoveSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) moveSpeed = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("FlyMoveSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) flyMoveSpeed = (float)f.GetValue(comp); } catch { }

                                // Primary attack
                                try
                                {
                                    object ap = null;
                                    var apField = ct.GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                                    if (apField != null) ap = apField.GetValue(comp);
                                    else { var apProp = ct.GetProperty("AttackPrimary", BindingFlags.Public | BindingFlags.Instance); if (apProp != null) ap = apProp.GetValue(comp); }

                                    if (ap != null)
                                    {
                                        var at = ap.GetType();
                                        try { atkDamage = GetFloatField(at, ap, "Damage"); } catch { }
                                        try { atkCooldown = GetFloatField(at, ap, "CoolDownTime"); } catch { }
                                        try { atkRange = GetFloatField(at, ap, "AttackProjectileAimDistMax"); } catch { }
                                        try { atkSpread = GetFloatField(at, ap, "AttackProjectileSpread"); } catch { }
                                        // Get projectile data name
                                        try
                                        {
                                            object pdObj = null;
                                            var pdfField = at.GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                            if (pdfField != null) pdObj = pdfField.GetValue(ap);
                                            else { var pdfProp = at.GetProperty("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance); if (pdfProp != null) pdObj = pdfProp.GetValue(ap); }
                                            if (pdObj != null)
                                            {
                                                atkProjName = (pdObj as UnityEngine.Object)?.name ?? "";
                                                var pdt = pdObj.GetType();
                                                try { projSpeed = GetFloatField(pdt, pdObj, "m_fBaseSpeed"); } catch { }
                                                try { projLifetime = GetFloatField(pdt, pdObj, "m_fLifeTime"); } catch { }
                                                try
                                                {
                                                    var ihf = pdt.GetField("m_InstantHit", BindingFlags.Public | BindingFlags.Instance);
                                                    if (ihf != null) instantHit = (bool)ihf.GetValue(pdObj);
                                                    else { var ihp = pdt.GetProperty("m_InstantHit", BindingFlags.Public | BindingFlags.Instance); if (ihp != null) instantHit = (bool)ihp.GetValue(pdObj); }
                                                }
                                                catch { }
                                                // Projectile damage fields
                                                try { projImpactDmg = GetFloatField(pdt, pdObj, "m_fImpactDamage"); } catch { }
                                                try { projRicochetDmg = GetFloatField(pdt, pdObj, "m_fRicochetDamage"); } catch { }
                                                try { projSplashDmg = GetFloatField(pdt, pdObj, "m_fSplashDamageMax"); } catch { }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }

                                // Secondary attack
                                try
                                {
                                    object asec = null;
                                    var asField = ct.GetField("AttackSecondary", BindingFlags.Public | BindingFlags.Instance);
                                    if (asField != null) asec = asField.GetValue(comp);
                                    else { var asProp = ct.GetProperty("AttackSecondary", BindingFlags.Public | BindingFlags.Instance); if (asProp != null) asec = asProp.GetValue(comp); }

                                    if (asec != null)
                                    {
                                        var ast = asec.GetType();
                                        try { atkDamageSec = GetFloatField(ast, asec, "Damage"); } catch { }
                                        try { atkCooldownSec = GetFloatField(ast, asec, "CoolDownTime"); } catch { }
                                        try { atkRangeSec = GetFloatField(ast, asec, "AttackProjectileAimDistMax"); } catch { }
                                        try { atkSpreadSec = GetFloatField(ast, asec, "AttackProjectileSpread"); } catch { }
                                        try
                                        {
                                            object pdObj = null;
                                            var pdfField = ast.GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                            if (pdfField != null) pdObj = pdfField.GetValue(asec);
                                            else { var pdfProp = ast.GetProperty("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance); if (pdfProp != null) pdObj = pdfProp.GetValue(asec); }
                                            if (pdObj != null)
                                            {
                                                atkProjNameSec = (pdObj as UnityEngine.Object)?.name ?? "";
                                                var pdt = pdObj.GetType();
                                                try { projSpeedSec = GetFloatField(pdt, pdObj, "m_fBaseSpeed"); } catch { }
                                                try { projLifetimeSec = GetFloatField(pdt, pdObj, "m_fLifeTime"); } catch { }
                                                try
                                                {
                                                    var ihf = pdt.GetField("m_InstantHit", BindingFlags.Public | BindingFlags.Instance);
                                                    if (ihf != null) instantHitSec = (bool)ihf.GetValue(pdObj);
                                                    else { var ihp = pdt.GetProperty("m_InstantHit", BindingFlags.Public | BindingFlags.Instance); if (ihp != null) instantHitSec = (bool)ihp.GetValue(pdObj); }
                                                }
                                                catch { }
                                                // Projectile damage fields
                                                try { projImpactDmg2 = GetFloatField(pdt, pdObj, "m_fImpactDamage"); } catch { }
                                                try { projRicochetDmg2 = GetFloatField(pdt, pdObj, "m_fRicochetDamage"); } catch { }
                                                try { projSplashDmg2 = GetFloatField(pdt, pdObj, "m_fSplashDamageMax"); } catch { }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }

                            // Sensor: detection ranges
                            if (tn == "Sensor")
                            {
                                try { var f = ct.GetField("FogOfWarViewDistance", BindingFlags.Public | BindingFlags.Instance); if (f != null) { float val = (float)f.GetValue(comp); if (val > fowView) fowView = val; } } catch { }
                                try { var f = ct.GetField("TargetingDistance", BindingFlags.Public | BindingFlags.Instance); if (f != null) { float val = (float)f.GetValue(comp); if (val > targetDist) targetDist = val; } } catch { }
                            }

                            // VehicleTurret: weapon data + projectile damage
                            if (tn == "VehicleTurret")
                            {
                                try { var f = ct.GetField("PrimaryFireInterval", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtFireInterval = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("PrimaryMuzzleSpread", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtMuzzleSpread = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("PrimaryMagazineSize", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtMagazine = (int)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("PrimaryReloadTime", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtReloadTime = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("PrimaryShotCount", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtShotCount = (int)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryFireInterval", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtFireIntervalSec = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryMuzzleSpread", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtMuzzleSpreadSec = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryMagazineSize", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtMagazineSec = (int)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryReloadTime", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtReloadTimeSec = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("SecondaryShotCount", BindingFlags.Public | BindingFlags.Instance); if (f != null) vtShotCountSec = (int)f.GetValue(comp); } catch { }

                                // Read PrimaryProjectileData
                                try
                                {
                                    object pdObj = null;
                                    var pf = ct.GetField("PrimaryProjectileData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (pf != null) pdObj = pf.GetValue(comp);
                                    if (pdObj != null)
                                    {
                                        vtProjName = (pdObj as UnityEngine.Object)?.name ?? "";
                                        var pdt = pdObj.GetType();
                                        try { vtImpactDmg = GetFloatField(pdt, pdObj, "m_fImpactDamage"); } catch { }
                                        try { vtRicochetDmg = GetFloatField(pdt, pdObj, "m_fRicochetDamage"); } catch { }
                                        try { vtSplashDmg = GetFloatField(pdt, pdObj, "m_fSplashDamageMax"); } catch { }
                                        try { vtPenDmg = GetFloatField(pdt, pdObj, "m_fPenetratingDamage"); } catch { }
                                        try { vtProjSpeed = GetFloatField(pdt, pdObj, "m_fBaseSpeed"); } catch { }
                                        try { vtProjLifetime = GetFloatField(pdt, pdObj, "m_fLifeTime"); } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_bSplash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtHasSplash = (bool)ihf.GetValue(pdObj);
                                        } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_bPenetrating", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtHasPen = (bool)ihf.GetValue(pdObj);
                                        } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_InstantHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtInstantHit = (bool)ihf.GetValue(pdObj);
                                            else { var ihp = pdt.GetProperty("m_InstantHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (ihp != null) vtInstantHit = (bool)ihp.GetValue(pdObj); }
                                        } catch { }
                                    }
                                } catch { }

                                // Read SecondaryProjectileData
                                try
                                {
                                    object pdObj = null;
                                    var pf = ct.GetField("SecondaryProjectileData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (pf != null) pdObj = pf.GetValue(comp);
                                    if (pdObj != null)
                                    {
                                        vtProjName2 = (pdObj as UnityEngine.Object)?.name ?? "";
                                        var pdt = pdObj.GetType();
                                        try { vtImpactDmg2 = GetFloatField(pdt, pdObj, "m_fImpactDamage"); } catch { }
                                        try { vtRicochetDmg2 = GetFloatField(pdt, pdObj, "m_fRicochetDamage"); } catch { }
                                        try { vtSplashDmg2 = GetFloatField(pdt, pdObj, "m_fSplashDamageMax"); } catch { }
                                        try { vtPenDmg2 = GetFloatField(pdt, pdObj, "m_fPenetratingDamage"); } catch { }
                                        try { vtProjSpeed2 = GetFloatField(pdt, pdObj, "m_fBaseSpeed"); } catch { }
                                        try { vtProjLifetime2 = GetFloatField(pdt, pdObj, "m_fLifeTime"); } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_bSplash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtHasSplash2 = (bool)ihf.GetValue(pdObj);
                                        } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_bPenetrating", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtHasPen2 = (bool)ihf.GetValue(pdObj);
                                        } catch { }
                                        try
                                        {
                                            var ihf = pdt.GetField("m_InstantHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihf != null) vtInstantHit2 = (bool)ihf.GetValue(pdObj);
                                            else { var ihp = pdt.GetProperty("m_InstantHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (ihp != null) vtInstantHit2 = (bool)ihp.GetValue(pdObj); }
                                        } catch { }
                                    }
                                } catch { }
                            }

                            // CreatureTurret: additional turret for creatures
                            if (tn == "CreatureTurret")
                            {
                                // Already captured via CreatureDecapod attacks
                            }

                            // PlayerMovement: infantry walk/run/jump
                            if (tn == "PlayerMovement" || tn == "FPSMovement")
                            {
                                try { var f = ct.GetField("WalkSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) walkSpeed = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("RunSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) runSpeed = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("JumpSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) jumpSpeed = (float)f.GetValue(comp); } catch { }
                            }

                            // VehicleWheeled: ground vehicle movement
                            if (tn == "VehicleWheeled")
                            {
                                vehicleType = "Wheeled";
                                try { var f = ct.GetField("MoveSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) { float val = (float)f.GetValue(comp); if (val > moveSpeed) moveSpeed = val; } } catch { }
                                try { var f = ct.GetField("WheelMotorAccel", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehAccel = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("TurningCircleRadius", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehTurnRadius = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("ReverseSpeedScale", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehReverseScale = (float)f.GetValue(comp); } catch { }
                            }

                            // VehicleHovered: hover/air vehicle movement
                            if (tn == "VehicleHovered")
                            {
                                vehicleType = "Hovered";
                                try { var f = ct.GetField("MoveSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) { float val = (float)f.GetValue(comp); if (val > moveSpeed) moveSpeed = val; } } catch { }
                                try { var f = ct.GetField("MoveAcceleration", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehAccel = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("TurnSpeed", BindingFlags.Public | BindingFlags.Instance); if (f != null) vehTurnSpeed = (float)f.GetValue(comp); } catch { }
                            }

                            // TeleportUI: teleportation settings on structures
                            if (tn == "TeleportUI")
                            {
                                hasTeleport = true;
                                try { var f = ct.GetField("TeleportTime", BindingFlags.Public | BindingFlags.Instance); if (f != null) teleportTime = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("TeleportCooldownTime", BindingFlags.Public | BindingFlags.Instance); if (f != null) teleportCooldown = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("TeleportPct", BindingFlags.Public | BindingFlags.Instance); if (f != null) teleportPct = (float)f.GetValue(comp); } catch { }
                                try { var f = ct.GetField("OnlyForSameTeamUnit", BindingFlags.Public | BindingFlags.Instance); if (f != null) teleportSameTeamOnly = (bool)f.GetValue(comp); } catch { }
                            }
                        }
                    }

                    // Write JSON entry
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append("    {");
                    sb.Append($"\"name\":\"{EscJson(name)}\",");
                    sb.Append($"\"internal\":\"{EscJson(info.name)}\",");
                    sb.Append($"\"faction\":\"{faction}\",");
                    sb.Append($"\"team\":\"{EscJson(team)}\",");
                    sb.Append($"\"is_structure\":{(isStructure ? "true" : "false")},");
                    sb.Append($"\"built_at\":\"{EscJson(producer)}\",");
                    sb.Append($"\"cost\":{cost},");
                    sb.Append($"\"build_time\":{buildTime:F1},");
                    sb.Append($"\"min_tier\":{minTier},");
                    sb.Append($"\"max_tier\":{maxTier},");
                    sb.Append($"\"hp\":{maxHealth},");
                    sb.Append($"\"max_dist\":{maxDist:F0},");
                    sb.Append($"\"use_radius\":{(useRadius ? "true" : "false")},");
                    // Movement
                    sb.Append($"\"move_speed\":{moveSpeed:F1},");
                    sb.Append($"\"fly_speed\":{flyMoveSpeed:F1},");
                    sb.Append($"\"walk_speed\":{walkSpeed:F1},");
                    sb.Append($"\"run_speed\":{runSpeed:F1},");
                    sb.Append($"\"jump_speed\":{jumpSpeed:F2},");
                    // Vehicle movement detail
                    sb.Append($"\"veh_type\":\"{vehicleType}\",");
                    sb.Append($"\"veh_accel\":{vehAccel:F2},");
                    sb.Append($"\"veh_turn_radius\":{vehTurnRadius:F1},");
                    sb.Append($"\"veh_turn_speed\":{vehTurnSpeed:F1},");
                    sb.Append($"\"veh_reverse_scale\":{vehReverseScale:F2},");
                    // Primary attack
                    sb.Append($"\"atk_damage\":{atkDamage:F0},");
                    sb.Append($"\"atk_cooldown\":{atkCooldown:F2},");
                    sb.Append($"\"atk_range\":{atkRange:F0},");
                    sb.Append($"\"atk_spread\":{atkSpread:F2},");
                    sb.Append($"\"atk_proj\":\"{EscJson(atkProjName)}\",");
                    sb.Append($"\"proj_speed\":{projSpeed:F1},");
                    sb.Append($"\"proj_lifetime\":{projLifetime:F2},");
                    sb.Append($"\"instant_hit\":{(instantHit ? "true" : "false")},");
                    // Secondary attack
                    sb.Append($"\"atk2_damage\":{atkDamageSec:F0},");
                    sb.Append($"\"atk2_cooldown\":{atkCooldownSec:F2},");
                    sb.Append($"\"atk2_range\":{atkRangeSec:F0},");
                    sb.Append($"\"atk2_spread\":{atkSpreadSec:F2},");
                    sb.Append($"\"atk2_proj\":\"{EscJson(atkProjNameSec)}\",");
                    sb.Append($"\"proj2_speed\":{projSpeedSec:F1},");
                    sb.Append($"\"proj2_lifetime\":{projLifetimeSec:F2},");
                    sb.Append($"\"instant_hit2\":{(instantHitSec ? "true" : "false")},");
                    // Detection
                    sb.Append($"\"fow_view\":{fowView:F0},");
                    sb.Append($"\"target_dist\":{targetDist:F0},");
                    // Vehicle turret
                    sb.Append($"\"vt_fire_interval\":{vtFireInterval:F4},");
                    sb.Append($"\"vt_spread\":{vtMuzzleSpread:F3},");
                    sb.Append($"\"vt_magazine\":{vtMagazine},");
                    sb.Append($"\"vt_reload\":{vtReloadTime:F1},");
                    sb.Append($"\"vt_shot_count\":{vtShotCount},");
                    sb.Append($"\"vt2_fire_interval\":{vtFireIntervalSec:F4},");
                    sb.Append($"\"vt2_spread\":{vtMuzzleSpreadSec:F3},");
                    sb.Append($"\"vt2_magazine\":{vtMagazineSec},");
                    sb.Append($"\"vt2_shot_count\":{vtShotCountSec},");
                    sb.Append($"\"vt2_reload\":{vtReloadTimeSec:F1},");
                    // VT primary projectile
                    sb.Append($"\"vt_proj\":\"{EscJson(vtProjName)}\",");
                    sb.Append($"\"vt_impact_dmg\":{vtImpactDmg:F1},");
                    sb.Append($"\"vt_ricochet_dmg\":{vtRicochetDmg:F1},");
                    sb.Append($"\"vt_splash_dmg\":{vtSplashDmg:F1},");
                    sb.Append($"\"vt_has_splash\":{(vtHasSplash ? "true" : "false")},");
                    sb.Append($"\"vt_pen_dmg\":{vtPenDmg:F1},");
                    sb.Append($"\"vt_has_pen\":{(vtHasPen ? "true" : "false")},");
                    sb.Append($"\"vt_instant_hit\":{(vtInstantHit ? "true" : "false")},");
                    sb.Append($"\"vt_proj_speed\":{vtProjSpeed:F1},");
                    sb.Append($"\"vt_proj_lifetime\":{vtProjLifetime:F2},");
                    // VT secondary projectile
                    sb.Append($"\"vt2_proj\":\"{EscJson(vtProjName2)}\",");
                    sb.Append($"\"vt2_impact_dmg\":{vtImpactDmg2:F1},");
                    sb.Append($"\"vt2_ricochet_dmg\":{vtRicochetDmg2:F1},");
                    sb.Append($"\"vt2_splash_dmg\":{vtSplashDmg2:F1},");
                    sb.Append($"\"vt2_has_splash\":{(vtHasSplash2 ? "true" : "false")},");
                    sb.Append($"\"vt2_pen_dmg\":{vtPenDmg2:F1},");
                    sb.Append($"\"vt2_has_pen\":{(vtHasPen2 ? "true" : "false")},");
                    sb.Append($"\"vt2_instant_hit\":{(vtInstantHit2 ? "true" : "false")},");
                    sb.Append($"\"vt2_proj_speed\":{vtProjSpeed2:F1},");
                    sb.Append($"\"vt2_proj_lifetime\":{vtProjLifetime2:F2},");
                    // Creature projectile damage
                    sb.Append($"\"proj_impact_dmg\":{projImpactDmg:F1},");
                    sb.Append($"\"proj_ricochet_dmg\":{projRicochetDmg:F1},");
                    sb.Append($"\"proj_splash_dmg\":{projSplashDmg:F1},");
                    sb.Append($"\"proj2_impact_dmg\":{projImpactDmg2:F1},");
                    sb.Append($"\"proj2_ricochet_dmg\":{projRicochetDmg2:F1},");
                    sb.Append($"\"proj2_splash_dmg\":{projSplashDmg2:F1},");
                    // Teleport
                    sb.Append($"\"has_teleport\":{(hasTeleport ? "true" : "false")},");
                    sb.Append($"\"teleport_time\":{teleportTime:F1},");
                    sb.Append($"\"teleport_cooldown\":{teleportCooldown:F1},");
                    sb.Append($"\"teleport_pct\":{teleportPct:F2},");
                    sb.Append($"\"teleport_same_team\":{(teleportSameTeamOnly ? "true" : "false")}");
                    sb.Append("}");
                }

                sb.AppendLine();
                sb.AppendLine("  ],");

                // Production tree
                sb.AppendLine("  \"production_tree\": {");
                bool firstProd = true;
                foreach (var kv in prodTree)
                {
                    if (!firstProd) sb.AppendLine(",");
                    firstProd = false;
                    sb.Append($"    \"{EscJson(kv.Key)}\": [");
                    sb.Append(string.Join(", ", kv.Value.ConvertAll(n => $"\"{EscJson(n)}\"")));
                    sb.Append("]");
                }
                sb.AppendLine();
                sb.AppendLine("  }");
                sb.AppendLine("}");

                System.IO.File.WriteAllText(outPath, sb.ToString());
                MelonLogger.Msg($"[JSON DUMP] Wrote {seen.Count} entries to {outPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JSON DUMP] Error: {ex}");
            }
        }

        private static float GetFloatField(Type t, object obj, string fieldName)
        {
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f != null)
            {
                object val = f.GetValue(obj);
                if (val is float fv) return fv;
                if (val is int iv) return iv;
            }
            var p = t.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null)
            {
                object val = p.GetValue(obj);
                if (val is float fv) return fv;
                if (val is int iv) return iv;
            }
            return 0;
        }

        private static string EscJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}

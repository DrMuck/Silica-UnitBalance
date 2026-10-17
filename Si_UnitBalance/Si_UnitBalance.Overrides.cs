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
            bool hasTechOverrides = _techTierTimes.Count > 0 || _techTierCosts.Count > 0;

            if (!hasUnitOverrides && !hasTechOverrides)
                return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            int techApplied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.ConstructionData == null) continue;

                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;

                var cd = info.ConstructionData;
                string cdTarget = useOM ? $"A:{cd.name}.asset" : null;

                // Tech tier build time overrides (match by TechnologyTier field)
                if (hasTechOverrides && cd.TechnologyTier > 0)
                {
                    if (_techTierTimes.TryGetValue(cd.TechnologyTier, out float techTime))
                    {
                        // techTime = desired TOTAL time (BuildUpTime + FinishedWaitTime + CleanUpTime)
                        // Distribute proportionally across all three fields
                        float origBU = cd.BuildUpTime;
                        float origFwt = 0, origCut = 0;
                        try
                        {
                            var cdType = cd.GetType();
                            var fwtField = cdType.GetField("FinishedWaitTime", BindingFlags.Public | BindingFlags.Instance);
                            var cutField = cdType.GetField("CleanUpTime", BindingFlags.Public | BindingFlags.Instance);
                            if (fwtField != null) origFwt = (float)fwtField.GetValue(cd);
                            if (cutField != null) origCut = (float)cutField.GetValue(cd);
                        }
                        catch { }
                        float origTotal = origBU + origFwt + origCut;
                        float desiredTotal = Math.Max(1f, techTime);
                        float ratio = origTotal > 0 ? desiredTotal / origTotal : 1f;

                        float newBU = Math.Max(0.5f, origBU * ratio);
                        bool omOk = false;
                        if (useOM)
                        {
                            omOk = OMSetFloat(cdTarget, "BuildUpTime", newBU);
                            if (!omOk)
                            {
                                MelonLogger.Warning($"[TECH] OM.Set failed for '{cdTarget}' BuildUpTime={newBU}, falling back to direct");
                                cd.BuildUpTime = newBU;
                            }
                        }
                        else
                            cd.BuildUpTime = newBU;

                        // Scale FWT/CUT by same ratio
                        try
                        {
                            var cdType = cd.GetType();
                            if (origFwt > 0)
                            {
                                float newFwt = origFwt * ratio;
                                var fwtField = cdType.GetField("FinishedWaitTime", BindingFlags.Public | BindingFlags.Instance);
                                if (fwtField != null)
                                {
                                    if (useOM) OMSetFloat(cdTarget, "FinishedWaitTime", newFwt);
                                    else fwtField.SetValue(cd, newFwt);
                                }
                            }
                            if (origCut > 0)
                            {
                                float newCut = origCut * ratio;
                                var cutField = cdType.GetField("CleanUpTime", BindingFlags.Public | BindingFlags.Instance);
                                if (cutField != null)
                                {
                                    if (useOM) OMSetFloat(cdTarget, "CleanUpTime", newCut);
                                    else cutField.SetValue(cd, newCut);
                                }
                            }
                        }
                        catch { }

                        LogDebug($"[TECH] Tier {cd.TechnologyTier} '{name}' (CD={cd.name}): total {origTotal:F0}s -> {desiredTotal:F0}s (BU:{origBU:F0}->{newBU:F0} FWT:{origFwt:F0} CUT:{origCut:F0}){(omOk ? " (OM)" : "")}");
                        techApplied++;
                    }

                    // Tech tier cost override (absolute int per tier). Independent of the time override above.
                    if (_techTierCosts.TryGetValue(cd.TechnologyTier, out int techCost))
                    {
                        int origCost = cd.ResourceCost;
                        bool costOmOk = false;
                        if (useOM)
                        {
                            costOmOk = OMSetInt(cdTarget, "ResourceCost", techCost);
                            if (!costOmOk)
                            {
                                MelonLogger.Warning($"[TECH-COST] OM.Set failed for '{cdTarget}' ResourceCost={techCost}, falling back to direct");
                                cd.ResourceCost = techCost;
                            }
                        }
                        else
                        {
                            cd.ResourceCost = techCost;
                        }
                        LogDebug($"[TECH-COST] Tier {cd.TechnologyTier} '{name}' (CD={cd.name}): cost {origCost} -> {techCost}{(costOmOk ? " (OM)" : "")}");
                        techApplied++;
                    }

                    if (!_techTierTimes.ContainsKey(cd.TechnologyTier) && !_techTierCosts.ContainsKey(cd.TechnologyTier))
                    {
                        LogDebug($"[TECH-DIAG] Tier {cd.TechnologyTier} '{name}' (CD={cd.name}): no config for this tier, BuildUpTime={cd.BuildUpTime:F0}s cost={cd.ResourceCost}");
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
                    LogDebug($"[COST] {name}: {origCost} -> {newCost} (x{costMult:F2}){(useOM ? " (OM)" : "")}");
                }

                if (hasBuildTime)
                {
                    float origTime = cd.BuildUpTime;
                    float newTime = Math.Max(0.5f, origTime * btMult);
                    if (useOM)
                        OMSetFloat(cdTarget, "BuildUpTime", newTime);
                    else
                        cd.BuildUpTime = newTime;
                    LogDebug($"[BUILD] {name}: BuildUpTime {origTime:F1}s -> {newTime:F1}s (x{btMult:F2}){(useOM ? " (OM)" : "")}");

                    // Also scale FinishedWaitTime and CleanUpTime so the total build time
                    // shown in-game scales proportionally (game displays BuildUpTime + FinishedWaitTime + CleanUpTime)
                    try
                    {
                        var cdType = cd.GetType();
                        var fwtField = cdType.GetField("FinishedWaitTime", BindingFlags.Public | BindingFlags.Instance);
                        var cutField = cdType.GetField("CleanUpTime", BindingFlags.Public | BindingFlags.Instance);
                        if (fwtField != null)
                        {
                            float origFwt = (float)fwtField.GetValue(cd);
                            if (origFwt > 0)
                            {
                                float newFwt = origFwt * btMult;
                                if (useOM) OMSetFloat(cdTarget, "FinishedWaitTime", newFwt);
                                else fwtField.SetValue(cd, newFwt);
                                LogDebug($"[BUILD] {name}: FinishedWaitTime {origFwt:F1}s -> {newFwt:F1}s");
                            }
                        }
                        if (cutField != null)
                        {
                            float origCut = (float)cutField.GetValue(cd);
                            if (origCut > 0)
                            {
                                float newCut = origCut * btMult;
                                if (useOM) OMSetFloat(cdTarget, "CleanUpTime", newCut);
                                else cutField.SetValue(cd, newCut);
                                LogDebug($"[BUILD] {name}: CleanUpTime {origCut:F1}s -> {newCut:F1}s");
                            }
                        }
                    }
                    catch { }
                }

                if (hasMinTier)
                {
                    int origTier = cd.MinimumTeamTier;
                    if (useOM)
                        OMSetInt(cdTarget, "MinimumTeamTier", minTier);
                    else
                        cd.MinimumTeamTier = minTier;
                    LogDebug($"[TIER] {name}: min tier {origTier} -> {minTier}{(useOM ? " (OM)" : "")}");
                }

                if (hasBuildRadius)
                {
                    float origDist = cd.MaximumBaseStructureDistance;
                    if (useOM)
                        OMSetFloat(cdTarget, "MaximumBaseStructureDistance", buildRadius);
                    else
                        cd.MaximumBaseStructureDistance = buildRadius;
                    LogDebug($"[BUILD_RADIUS] {name}: {origDist:F0} -> {buildRadius:F0}{(useOM ? " (OM)" : "")}");
                }

                applied++;
            }

            if (applied > 0 || techApplied > 0)
                MelonLogger.Msg($"Applied construction overrides: {applied} units/structures, {techApplied} tech tiers");
        }

        // =============================================
        // Deposit Radius: override ResourceDepositPoint.DepositRadius (direct prefab mutation)
        // ResourceDepositPoint is a child MonoBehaviour, not an OM-registered asset.
        // Deposit check (GetPointWithinDepositBounds) runs server-side only.
        // =============================================

        private static void ApplyDepositRadiusOverrides(bool useOM)
        {
            if (_depositRadiusOverrides.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                if (!_depositRadiusOverrides.TryGetValue(name, out float depositRadius)) continue;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null || comp.GetType().Name != "ResourceDepositPoint") continue;

                    var drField = comp.GetType().GetField("DepositRadius",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (drField == null || drField.FieldType != typeof(float)) continue;

                    float orig = (float)drField.GetValue(comp);
                    drField.SetValue(comp, depositRadius);
                    LogDebug($"[DEPOSIT_RADIUS] {name}: ResourceDepositPoint.DepositRadius {orig:F0} -> {depositRadius:F0} (direct)");
                    applied++;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[DEPOSIT_RADIUS] Applied deposit radius overrides to {applied} deposit points");
        }

        // =============================================
        // Extraction Radius: override MiningArmsBase.ExtractionRadius (direct prefab mutation)
        // MiningArmsBase is a child MonoBehaviour on harvester units (Shrimp).
        // Extraction logic runs server-side only.
        // =============================================

        private static void ApplyExtractionRadiusOverrides(bool useOM)
        {
            if (_extractionRadiusOverrides.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                if (!_extractionRadiusOverrides.TryGetValue(name, out float extractionRadius)) continue;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;

                    var erField = comp.GetType().GetField("ExtractionRadius",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (erField == null || erField.FieldType != typeof(float)) continue;

                    float orig = (float)erField.GetValue(comp);
                    erField.SetValue(comp, extractionRadius);
                    LogDebug($"[EXTRACTION_RADIUS] {name}: MiningArmsBase.ExtractionRadius {orig:F0} -> {extractionRadius:F0} (direct)");
                    applied++;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[EXTRACTION_RADIUS] Applied extraction radius overrides to {applied} harvester(s)");
        }

        // =============================================
        // Unit Cap: set UnitCapValue on ObjectInfo via OverrideManager
        // =============================================

        private static void ApplyUnitCapOverrides(bool useOM)
        {
            if (_unitCapOverrides.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                if (!_unitCapOverrides.TryGetValue(name, out int newCap)) continue;

                int origCap = info.UnitCapValue;
                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                if (useOM)
                    OMSetInt(oiTarget, "UnitCapValue", newCap);
                else
                    info.UnitCapValue = newCap;
                LogDebug($"[UNITCAP] {name}: {origCap} -> {newCap}{(useOM ? " (OM)" : "")}");
                applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[UNITCAP] Applied unit cap overrides to {applied} units");
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
            var modifiedDMD = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Diagnostic: log all DMD asset sharing to help debug unexpected health changes
            var dmdUsers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                var dmCheck = info.Prefab.GetComponent<DamageManager>();
                if (dmCheck == null) continue;
                try
                {
                    var dataFieldCheck = dmCheck.GetType().GetField("Data", BindingFlags.Public | BindingFlags.Instance);
                    if (dataFieldCheck == null) continue;
                    var dataObjCheck = dataFieldCheck.GetValue(dmCheck) as UnityEngine.Object;
                    if (dataObjCheck == null) continue;
                    string dmdName = dataObjCheck.name;
                    string uName = ResolveConfigName(info.DisplayName, info.name);
                    if (string.IsNullOrEmpty(dmdName) || string.IsNullOrEmpty(uName)) continue;
                    if (!dmdUsers.ContainsKey(dmdName))
                        dmdUsers[dmdName] = new List<string>();
                    dmdUsers[dmdName].Add(uName);
                }
                catch { }
            }
            // Warn about shared DMD assets among units that have health overrides
            foreach (var kvp in dmdUsers)
            {
                if (kvp.Value.Count > 1)
                {
                    bool anyHasOverride = false;
                    foreach (string u in kvp.Value)
                        if (_healthMultipliers.ContainsKey(u)) { anyHasOverride = true; break; }
                    if (anyHasOverride)
                        MelonLogger.Warning($"[HEALTH-DIAG] DamageManagerData '{kvp.Key}' shared by: {string.Join(", ", kvp.Value)}");
                }
            }

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
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
                            string dataName = ((UnityEngine.Object)dataObj).name;

                            // Skip if this DMD asset was already modified by another unit
                            if (!string.IsNullOrEmpty(dataName) && modifiedDMD.Contains(dataName))
                            {
                                MelonLogger.Warning($"[HEALTH] {name}: DamageManagerData '{dataName}' already modified — skipping to prevent compounding");
                                continue;
                            }

                            // Anti-compounding: cache first-seen vanilla value
                            float currentHealth = GetFloatMember(dataObj, "Health");
                            if (currentHealth <= 0) currentHealth = GetFloatMember(dataObj, "MaxHealth");
                            if (currentHealth <= 0) currentHealth = GetFloatMember(dataObj, "m_MaxHealth");

                            float origHealth;
                            string cacheKey = dataName ?? name;
                            if (_originalHealth.ContainsKey(cacheKey))
                                origHealth = _originalHealth[cacheKey];
                            else
                            {
                                origHealth = currentHealth;
                                _originalHealth[cacheKey] = origHealth;
                            }

                            if (origHealth > 0)
                            {
                                float newHealth = origHealth * hpMult;

                                LogDebug($"[HEALTH-DIAG] {name}: DMD='{dataName}', vanilla={origHealth:F0}, current={currentHealth:F0}, target={newHealth:F0} (x{hpMult:F2})");

                                if (useOM && !string.IsNullOrEmpty(dataName))
                                {
                                    // Use OverrideManager to set Health on the DamageManagerData asset
                                    // Target uses "DamageManagerData_" prefix to match our registered synthetic paths
                                    string dmdTarget = $"A:DamageManagerData_{dataName}.asset";
                                    set = OMSetFloat(dmdTarget, "Health", newHealth);
                                    if (set)
                                        LogDebug($"[HEALTH] {name}: Health {origHealth:F0} -> {newHealth:F0} (x{hpMult:F2}) (OM: {dataName})");
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
                                        LogDebug($"[HEALTH] {name}: Health {origHealth:F0} -> {newHealth:F0} (x{hpMult:F2}) (direct mutation — no client sync!)");
                                }

                                if (set && !string.IsNullOrEmpty(dataName))
                                    modifiedDMD.Add(dataName);
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

        /// <summary>
        /// Rescale HealthInternal on all live DamageManager instances whose Data.Health was modified.
        /// Maintains health percentage (e.g., 50% HP before → 50% HP after).
        /// Called after ApplyHealthOverrides to fix existing units spawned with old MaxHealth.
        /// </summary>
        private static void ReclampLiveHealth()
        {
            if (_healthMultipliers.Count == 0) return;

            var healthProp = typeof(DamageManager).GetProperty("Health",
                BindingFlags.Public | BindingFlags.Instance);
            if (healthProp == null || healthProp.GetSetMethod(true) == null)
            {
                MelonLogger.Warning("[HEALTH] Cannot find Health setter — skip live rescale");
                return;
            }
            var healthSetter = healthProp.GetSetMethod(true);

            // Build lookup: DamageManagerData asset name → (oldHealth, newHealth) ratio
            // oldHealth = vanilla value from _originalHealth cache
            // newHealth = current Data.Health (after override applied)
            var dataNameToRatio = new Dictionary<string, float>();
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                if (!_healthMultipliers.TryGetValue(name, out float hpMult)) continue;

                var dm = info.Prefab.GetComponent<DamageManager>();
                if (dm == null || dm.Data == null) continue;
                string dataName = ((UnityEngine.Object)dm.Data).name;
                if (string.IsNullOrEmpty(dataName)) continue;
                if (dataNameToRatio.ContainsKey(dataName)) continue;

                // Ratio = new / old. Use _originalHealth for vanilla baseline.
                string cacheKey = dataName;
                if (_originalHealth.TryGetValue(cacheKey, out float origHP) && origHP > 0)
                    dataNameToRatio[dataName] = hpMult;
                else
                    dataNameToRatio[dataName] = 1f;
            }

            if (dataNameToRatio.Count == 0) return;

            // Find all live DamageManager instances and rescale to maintain health percentage
            var liveDMs = UnityEngine.Object.FindObjectsOfType<DamageManager>();
            int rescaled = 0;
            foreach (var dm in liveDMs)
            {
                if (dm == null || dm.Data == null) continue;
                string dataName = ((UnityEngine.Object)dm.Data).name;
                if (!dataNameToRatio.TryGetValue(dataName, out float ratio)) continue;

                float currentHP = dm.Health;
                float newMax = dm.MaxHealth;

                if (currentHP <= 0 || newMax <= 0) continue;

                // Maintain health percentage: if unit was at X% before, keep at X%
                // oldMax = newMax / ratio. oldPct = currentHP / oldMax. newHP = oldPct * newMax = currentHP * ratio.
                float newHP;
                if (Math.Abs(ratio - 1f) < 0.001f)
                {
                    // No multiplier change, but unit might have been spawned before DMD was modified
                    // by a *different* unit sharing the same DMD. Set to full if at or above old max.
                    if (_originalHealth.TryGetValue(dataName, out float origMax) && origMax > 0)
                    {
                        if (currentHP >= origMax - 1f)
                            newHP = newMax; // Was at full health → stay at full
                        else
                            newHP = (currentHP / origMax) * newMax; // Maintain percentage
                    }
                    else
                        continue;
                }
                else
                {
                    // Scale by ratio to maintain percentage
                    newHP = currentHP * ratio;
                }

                newHP = Math.Min(newHP, newMax);
                if (Math.Abs(newHP - currentHP) < 1f) continue;

                healthSetter.Invoke(dm, new object[] { newHP });
                LogDebug($"[HEALTH] Rescaled live {dataName}: {currentHP:F0} -> {newHP:F0} (max={newMax:F0})");
                rescaled++;
            }

            if (rescaled > 0)
                LogDebug($"[HEALTH] Rescaled {rescaled} live unit(s) to maintain health percentage");
        }

        // =============================================
        // Damage: scale ProjectileData damage fields (replaces broken Harmony ApplyDamage Postfix)
        // Modifying damage at the ProjectileData level ensures server & client see the same values.
        // =============================================

        private static readonly string[] _damageFields = {
            "m_fImpactDamage", "m_fRicochetDamage", "m_fSplashDamageMax", "m_fPenetratingDamage"
        };

        // Mapping from ProjectileData field name to sub-type key prefix for composite key lookup
        private static readonly Dictionary<string, string> _damageFieldToSubtype = new Dictionary<string, string> {
            { "m_fImpactDamage",      "impact:" },
            { "m_fRicochetDamage",    "ricochet:" },
            { "m_fSplashDamageMax",   "splash:" },
            { "m_fPenetratingDamage", "penetrating:" }
        };

        // Units where the primary weapon is NOT on the first VehicleTurret (vtIndex 0).
        // Maps unit name → vtIndex of the primary weapon's VehicleTurret.
        // Default (not listed) = 0 (first VT is primary).
        private static readonly Dictionary<string, int> _vtPriIndex = new Dictionary<string, int> {
            { "Bomber", 1 },         // cannon on 2nd VT, bombs on 1st
            { "Freighter", 1 },      // cannon on 2nd VT, bomb on 1st
            { "Shuttle", 1 },        // cannon on 2nd VT (only weapon)
            { "Gunship", 1 },        // gun on 2nd VT
            { "Platoon Hauler", 1 }, // weapon on 2nd VT
            // Fighter: gun (HMG_StealthFighter) is on VT[0] — NOT in this list
            // Interceptor: gun (Shell_Interceptor) is on VT[0] — NOT in this list
        };

        /// <summary>
        /// Resolve the effective damage multiplier for a specific damage field.
        /// Priority: pri_impact_damage_mult > pri_damage_mult > impact_damage_mult > damage_mult > 1.0
        /// Weapon-specific keys always take precedence over shared keys to prevent cross-contamination.
        /// </summary>
        private static float GetDamageFieldMult(string unitName, string weapon, string fieldName)
        {
            string subtype = _damageFieldToSubtype[fieldName];

            // 1. Most specific: weapon + subtype (e.g., "pri:impact:Hover Tank")
            if (!string.IsNullOrEmpty(weapon) && _damageMultipliers.TryGetValue(weapon + ":" + subtype + unitName, out float v1))
                return v1;
            // 2. Weapon blanket (e.g., "pri:Hover Tank") — before shared subtype to prevent cross-weapon contamination
            if (!string.IsNullOrEmpty(weapon) && _damageMultipliers.TryGetValue(weapon + ":" + unitName, out float v3))
                return v3;
            // 3. Subtype only — shared across all weapons (e.g., "impact:Hover Tank")
            if (_damageMultipliers.TryGetValue(subtype + unitName, out float v2))
                return v2;
            // 4. Unit blanket (e.g., "Hover Tank")
            if (_damageMultipliers.TryGetValue(unitName, out float v4))
                return v4;
            return 1f;
        }

        /// <summary>
        /// Checks if any damage multiplier (blanket or sub-type) or splash radius multiplier is set for a unit.
        /// </summary>
        private static bool HasAnyDamageMult(string unitName)
        {
            string[] dmgPrefixes = { "", "pri:", "sec:", "impact:", "ricochet:", "splash:", "penetrating:",
                                  "pri:impact:", "pri:ricochet:", "pri:splash:", "pri:penetrating:",
                                  "sec:impact:", "sec:ricochet:", "sec:splash:", "sec:penetrating:" };
            foreach (string prefix in dmgPrefixes)
            {
                if (_damageMultipliers.ContainsKey(prefix + unitName))
                    return true;
            }
            string[] splashPrefixes = { "max:", "min:", "pow:", "pri:max:", "pri:min:", "pri:pow:",
                                        "sec:max:", "sec:min:", "sec:pow:" };
            foreach (string prefix in splashPrefixes)
            {
                if (_splashRadiusMultipliers.ContainsKey(prefix + unitName))
                    return true;
            }
            return false;
        }

        // Splash radius fields to scale on ProjectileData (only when splash damage exists)
        private static readonly string[] _splashRadiusFields = {
            "m_fSplashDamageRadiusMax", "m_fSplashDamageRadiusMin", "m_fSplashDamageRadiusPow"
        };

        private static readonly Dictionary<string, string> _splashRadiusFieldToKey = new Dictionary<string, string> {
            { "m_fSplashDamageRadiusMax", "max:" },
            { "m_fSplashDamageRadiusMin", "min:" },
            { "m_fSplashDamageRadiusPow", "pow:" }
        };

        /// <summary>
        /// Resolve the effective splash radius multiplier for a specific field.
        /// Priority: pri_splash_radius_max_mult > splash_radius_max_mult > 1.0
        /// </summary>
        private static float GetSplashRadiusMult(string unitName, string weapon, string fieldName)
        {
            string key = _splashRadiusFieldToKey[fieldName];
            // 1. Weapon-specific (e.g., "pri:max:Hover Tank")
            if (!string.IsNullOrEmpty(weapon) && _splashRadiusMultipliers.TryGetValue(weapon + ":" + key + unitName, out float v1))
                return v1;
            // 2. Shared (e.g., "max:Hover Tank")
            if (_splashRadiusMultipliers.TryGetValue(key + unitName, out float v2))
                return v2;
            return 1f;
        }

        private static void ApplyProjectileDamageOverrides(bool useOM)
        {
            if (_damageMultipliers.Count == 0 && _splashRadiusMultipliers.Count == 0 && _projectileOverrides.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            var modifiedPD = new HashSet<string>(); // track already-modified ProjectileData assets

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;

                bool hasDamageMult = HasAnyDamageMult(name);
                bool hasProjOverrides = _projectileOverrides.TryGetValue(name, out var unitProjOverrides);
                if (!hasDamageMult && !hasProjOverrides) continue;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                bool anyApplied = false;
                int vtIndex = 0; // Track VehicleTurret index for multi-turret units

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
                                    LogDebug($"[DMG] {name} -> {pdName}.{kvp.Key}: {origVal:F1} -> {kvp.Value:F1}{(useOM ? " (OM)" : "")}");
                                }
                                modifiedPD.Add(pdName);
                                anyApplied = true;
                            }
                            else if (hasDamageMult)
                            {
                                // Determine weapon slot based on VT index and turret mapping
                                int priIdx = 0;
                                _vtPriIndex.TryGetValue(name, out priIdx);
                                string weapon;
                                if (vtIndex == priIdx)
                                    weapon = projFieldName.StartsWith("Primary") ? "pri" : "sec";
                                else
                                    weapon = "sec";
                                ScaleProjectileDamage(pdObj, pdName, name, weapon, modifiedPD, useOM);
                                anyApplied = true;
                            }
                        }
                        vtIndex++;
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
                                            LogDebug($"[DMG] {name} -> {pdName}.{kvp.Key}: {origVal:F1} -> {kvp.Value:F1}{(useOM ? " (OM)" : "")}");
                                        }
                                        modifiedPD.Add(pdName);
                                        anyApplied = true;
                                    }
                                    else if (hasDamageMult)
                                    {
                                        string weapon = attackFieldName == "AttackPrimary" ? "pri" : "sec";
                                        ScaleProjectileDamage(pdObj, pdName, name, weapon, modifiedPD, useOM);
                                        anyApplied = true;
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
                                    string cacheKey = $"{name}_{attackFieldName}";
                                    float origDmg;
                                    if (_originalMeleeDamage.ContainsKey(cacheKey))
                                        origDmg = _originalMeleeDamage[cacheKey];
                                    else
                                    {
                                        origDmg = GetFloatMember(attackObj, "Damage");
                                        _originalMeleeDamage[cacheKey] = origDmg;
                                    }
                                    if (origDmg > 0)
                                    {
                                        float newDmg = origDmg * effectiveDmgMult;
                                        SetFloatMember(attackObj, "Damage", newDmg);
                                        LogDebug($"[DMG] {name} -> {attackFieldName}.Damage: {origDmg:F0} -> {newDmg:F0} (melee, direct)");
                                        anyApplied = true;
                                    }
                                }
                            }
                        }
                    }

                    // --- Generic ProjectileData field scan (catches infantry/HumanHandsAnimator weapons) ---
                    if (typeName != "VehicleTurret" && typeName != "CreatureDecapod" && hasDamageMult)
                    {
                        var compType = comp.GetType();
                        foreach (var field in compType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (field.FieldType.Name != "ProjectileData") continue;
                            object pdObj;
                            try { pdObj = field.GetValue(comp); } catch { continue; }
                            if (pdObj == null) continue;
                            string pdName = "";
                            try { pdName = ((UnityEngine.Object)pdObj).name; } catch { continue; }
                            if (string.IsNullOrEmpty(pdName) || modifiedPD.Contains(pdName)) continue;
                            ScaleProjectileDamage(pdObj, pdName, name, "pri", modifiedPD, useOM);
                            anyApplied = true;
                        }
                    }
                }

                if (anyApplied) applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[DMG] Applied projectile/melee damage overrides to {applied} units");
        }

        private static void ScaleProjectileDamage(object pdObj, string pdName, string unitName, string weapon, HashSet<string> modifiedPD, bool useOM)
        {
            string pdTarget = useOM ? $"A:{pdName}.asset" : null;
            int scaled = 0;
            bool hasSplash = GetBoolMember(pdObj, "m_bSplash");
            bool hasPen = GetBoolMember(pdObj, "m_bPenetrating");

            foreach (string fieldName in _damageFields)
            {
                // Skip splash damage field if m_bSplash is disabled (splash not active even if value > 0)
                if (!hasSplash && fieldName == "m_fSplashDamageMax") continue;
                // Skip penetrating damage field if m_bPenetrating is disabled
                if (!hasPen && fieldName == "m_fPenetratingDamage") continue;
                float orig = GetFloatMember(pdObj, fieldName);
                if (orig <= 0) continue;
                float fieldMult = GetDamageFieldMult(unitName, weapon, fieldName);
                if (Math.Abs(fieldMult - 1f) <= 0.001f) continue;
                float newVal = orig * fieldMult;
                if (useOM)
                    OMSetFloat(pdTarget, fieldName, newVal);
                else
                    SetFloatMember(pdObj, fieldName, newVal);
                LogDebug($"[DMG] {unitName} -> {pdName}.{fieldName}: {orig:F1} -> {newVal:F1} (x{fieldMult:F2}){(useOM ? " (OM)" : "")}");
                scaled++;
            }

            // Scale splash radius fields (only if m_bSplash is enabled and has splash damage)
            float splashDmg = GetFloatMember(pdObj, "m_fSplashDamageMax");
            if (hasSplash && splashDmg > 0 && _splashRadiusMultipliers.Count > 0)
            {
                foreach (string srField in _splashRadiusFields)
                {
                    float origSR = GetFloatMember(pdObj, srField);
                    if (origSR <= 0) continue;
                    float srMult = GetSplashRadiusMult(unitName, weapon, srField);
                    if (Math.Abs(srMult - 1f) <= 0.001f) continue;
                    float newSR = origSR * srMult;
                    if (useOM)
                        OMSetFloat(pdTarget, srField, newSR);
                    else
                        SetFloatMember(pdObj, srField, newSR);
                    LogDebug($"[DMG] {unitName} -> {pdName}.{srField}: {origSR:F1} -> {newSR:F1} (x{srMult:F2}){(useOM ? " (OM)" : "")}");
                    scaled++;
                }
            }

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
        private static readonly Dictionary<string, float> _originalAimLeadVelocity =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalAimLeadGravity =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalAimLeadDrag =
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
                string name = ResolveConfigName(info.DisplayName, info.name);
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
                LogDebug($"[RANGE] Applying weapon overrides to '{name}' (internal: {info.name}){(useOM ? " (OM)" : "")}");

                bool foundProjectileOnComponent = false;
                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                int vtIndex = 0; // Track VehicleTurret index for multi-turret units

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    if (hasRange)
                    {
                        // NOTE: Sensor.TargetingDistance is NOT scaled by range_mult.
                        // Use the explicit "target_distance" config param to set it.

                        // VehicleTurret.AimDistance (per-turret — use weapon mapping)
                        if (typeName == "VehicleTurret")
                        {
                            int priIdx = 0;
                            _vtPriIndex.TryGetValue(name, out priIdx);
                            string vtWeapon = (vtIndex == priIdx) ? "pri" : "sec";
                            float vtRangeMult = GetWeaponMult(_rangeMultipliers, name, vtWeapon);
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
                                    LogDebug($"  VehicleTurret.AimDistance: {orig} -> {newVal}");
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
                                    LogDebug($"  UnitAimAt.AimDistanceMax: {orig} -> {newVal}");
                                }
                            }
                        }
                    }

                    // CreatureDecapod: per-weapon range/accuracy/speed (independent of hasRange)
                    if (typeName == "CreatureDecapod")
                        {
                            var compType = comp.GetType();
                            foreach (string attackFieldName in new[] { "AttackPrimary", "AttackSecondary" })
                            {
                                string weapon = attackFieldName == "AttackPrimary" ? "pri" : "sec";
                                float atkRangeMult = GetWeaponMult(_rangeMultipliers, name, weapon);
                                float atkAccMult = GetWeaponMult(_accuracyMultipliers, name, weapon);
                                float atkSpeedMult = GetWeaponMult(_speedMultipliers, name, weapon);

                                // Il2Cpp: serialized fields may be properties — try field first, then property
                                object attackObj = null;
                                var attackField = compType.GetField(attackFieldName,
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (attackField != null)
                                    try { attackObj = attackField.GetValue(comp); } catch { }
                                if (attackObj == null)
                                {
                                    var attackProp = compType.GetProperty(attackFieldName,
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (attackProp != null)
                                        try { attackObj = attackProp.GetValue(comp); } catch { }
                                }
                                if (attackObj == null)
                                    continue;

                                var attackType = attackObj.GetType();

                                // Get ProjectileData early (needed for instant-hit detection + scaling)
                                object pdObj = null;
                                {
                                    var pdField = attackType.GetField("AttackProjectileData",
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (pdField != null)
                                        try { pdObj = pdField.GetValue(attackObj); } catch { }
                                    if (pdObj == null)
                                    {
                                        var pdProp = attackType.GetProperty("AttackProjectileData",
                                            BindingFlags.Public | BindingFlags.Instance);
                                        if (pdProp != null)
                                            try { pdObj = pdProp.GetValue(attackObj); } catch { }
                                    }
                                }

                                // Detect instant-hit: for these projectiles, speed mult IS range
                                bool isInstantHitAtk = false;
                                if (pdObj != null)
                                {
                                    try
                                    {
                                        var ihField = pdObj.GetType().GetField("m_InstantHit",
                                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                        if (ihField != null)
                                            isInstantHitAtk = (bool)ihField.GetValue(pdObj);
                                        else
                                        {
                                            var ihProp = pdObj.GetType().GetProperty("m_InstantHit",
                                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (ihProp != null && ihProp.CanRead)
                                                isInstantHitAtk = (bool)ihProp.GetValue(pdObj);
                                        }
                                    }
                                    catch { }
                                }

                                // For instant-hit, speed mult effectively scales range
                                float effectiveAimDistMult = atkRangeMult *
                                    (isInstantHitAtk ? atkSpeedMult : 1f);

                                // Scale AttackProjectileAimDistMax (server-authoritative AI field)
                                if (Math.Abs(effectiveAimDistMult - 1f) > 0.001f)
                                {
                                    float aimDist = GetFloatMember(attackObj, "AttackProjectileAimDistMax");
                                    if (!float.IsNaN(aimDist))
                                    {
                                        string key = $"{name}_ca_{attackFieldName}";
                                        float orig;
                                        if (_originalCreatureAttackAimDist.ContainsKey(key))
                                            orig = _originalCreatureAttackAimDist[key];
                                        else
                                        {
                                            orig = aimDist;
                                            _originalCreatureAttackAimDist[key] = orig;
                                        }
                                        float newVal = orig * effectiveAimDistMult;
                                        SetFloatMember(attackObj, "AttackProjectileAimDistMax", newVal);
                                        LogDebug($"  CreatureAttack.{attackFieldName}.AimDistMax: {orig} -> {newVal} (direct{(isInstantHitAtk ? ", instant-hit" : "")})");
                                    }
                                }

                                // Scale AttackProjectileSpread (server-authoritative AI field)
                                if (Math.Abs(atkAccMult - 1f) > 0.001f)
                                {
                                    float spread = GetFloatMember(attackObj, "AttackProjectileSpread");
                                    if (!float.IsNaN(spread))
                                    {
                                        string key = $"{name}_spread_{attackFieldName}";
                                        float orig;
                                        if (_originalSpread.ContainsKey(key))
                                            orig = _originalSpread[key];
                                        else
                                        {
                                            orig = spread;
                                            _originalSpread[key] = orig;
                                        }
                                        float newVal = orig * atkAccMult;
                                        SetFloatMember(attackObj, "AttackProjectileSpread", newVal);
                                        LogDebug($"  CreatureAttack.{attackFieldName}.Spread: {orig} -> {newVal} (direct)");
                                    }

                                    // For instant-hit (ray) weapons, AttackProjectileSpread has no effect —
                                    // the ray goes straight to target. Scale AttackProjectileAimAngle instead,
                                    // which controls the AI's aiming cone (how precisely it aims).
                                    // NOTE: This must be OUTSIDE the spread NaN check — instant-hit weapons
                                    // may not have a readable spread field at all.
                                    if (isInstantHitAtk)
                                    {
                                        float aimAngle = GetFloatMember(attackObj, "AttackProjectileAimAngle");
                                        if (!float.IsNaN(aimAngle) && aimAngle > 0)
                                        {
                                            string aaKey = $"{name}_aimangle_{attackFieldName}";
                                            float origAA;
                                            if (_originalAimAngle.ContainsKey(aaKey))
                                                origAA = _originalAimAngle[aaKey];
                                            else
                                            {
                                                origAA = aimAngle;
                                                _originalAimAngle[aaKey] = origAA;
                                            }
                                            float newAA = origAA * atkAccMult;
                                            SetFloatMember(attackObj, "AttackProjectileAimAngle", newAA);
                                            LogDebug($"  CreatureAttack.{attackFieldName}.AimAngle: {origAA:F2} -> {newAA:F2} (direct, instant-hit accuracy)");
                                        }
                                    }
                                }

                                // Scale ProjectileData (using already-retrieved pdObj)
                                float atkLifetimeMult = GetWeaponMult(_lifetimeMultipliers, name, weapon);
                                bool hasAtkRange = Math.Abs(atkRangeMult - 1f) > 0.001f;
                                bool hasAtkSpeed = Math.Abs(atkSpeedMult - 1f) > 0.001f;
                                bool hasAtkLifetime = Math.Abs(atkLifetimeMult - 1f) > 0.001f;
                                if ((hasAtkRange || hasAtkSpeed || hasAtkLifetime) && pdObj != null)
                                {
                                    string pdName = "";
                                    try { pdName = ((UnityEngine.Object)pdObj).name; } catch { }
                                    if (!string.IsNullOrEmpty(pdName) && !modifiedPD.Contains(pdName))
                                    {
                                        ScaleProjectileData(pdObj, pdName,
                                            hasAtkRange ? atkRangeMult : 1f, hasAtkSpeed ? atkSpeedMult : 1f, modifiedPD, useOM,
                                            hasAtkLifetime ? atkLifetimeMult : 1f, name);
                                        foundProjectileOnComponent = true;
                                    }
                                }
                            }
                        }

                    // VehicleTurret weapon params via OverrideManager (per-weapon multiplier resolution)
                    // NOTE: OM.Set always targets the first VehicleTurret on the prefab.
                    // _vtPriIndex is NOT used here — weapon params always map VT[0].Primary → "pri".
                    // _vtPriIndex is only used for damage scaling (which targets ProjectileData assets).
                    if (typeName == "VehicleTurret")
                    {
                        var vtType = comp.GetType();

                        foreach (string prefix in new[] { "Primary", "Secondary" })
                        {
                            string weapon;
                            if (vtIndex == 0)
                                weapon = prefix == "Primary" ? "pri" : "sec";
                            else
                                weapon = "sec";

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
                                        LogDebug($"  VT[{vtIndex}].{prefix}ReloadTime: {orig:F2} -> {newVal:F2}{(useOM ? " (OM)" : " (direct)")}");
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
                                        LogDebug($"  VehicleTurret.{prefix}FireInterval: {orig:F4} -> {newVal:F4}");
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
                                        LogDebug($"  VT[{vtIndex}].{prefix}MagazineSize: {orig} -> {newVal}{(useOM ? " (OM)" : " (direct)")}");

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
                                        LogDebug($"  VehicleTurret.{prefix}MuzzleSpread: {orig:F4} -> {newVal:F4}");
                                    }
                                }
                            }
                        }
                        vtIndex++;
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

                            // Determine weapon slot from turret mapping and field name
                            string weapon;
                            if (typeName == "VehicleTurret")
                            {
                                int priIdx = 0;
                                _vtPriIndex.TryGetValue(name, out priIdx);
                                int currentVtIdx = vtIndex - 1; // vtIndex already incremented
                                if (currentVtIdx == priIdx)
                                    weapon = field.Name.StartsWith("Secondary") ? "sec" : "pri";
                                else
                                    weapon = "sec";
                            }
                            else
                            {
                                weapon = field.Name.StartsWith("Secondary") ? "sec" : "pri";
                            }
                            float effRange = GetWeaponMult(_rangeMultipliers, name, weapon);
                            float effSpeed = GetWeaponMult(_speedMultipliers, name, weapon);
                            float effLifetime = GetWeaponMult(_lifetimeMultipliers, name, weapon);
                            bool effHasRange = Math.Abs(effRange - 1f) > 0.001f;
                            bool effHasSpeed = Math.Abs(effSpeed - 1f) > 0.001f;
                            bool effHasLifetime = Math.Abs(effLifetime - 1f) > 0.001f;
                            if (!effHasRange && !effHasSpeed && !effHasLifetime) continue;

                            ScaleProjectileData(pdObj, pdName, effHasRange ? effRange : 1f, effHasSpeed ? effSpeed : 1f, modifiedPD, useOM,
                                effHasLifetime ? effLifetime : 1f, name);
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
                    LogDebug($"  No ProjectileData found on components, searching by name...");
                    int fallbackCount = 0;
                    if (fbHasRange || fbHasSpeed || fbHasLifetime)
                    {
                        foreach (var pd in allProjectiles)
                        {
                            if (pd == null || modifiedPD.Contains(pd.name)) continue;
                            if (pd.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            ScaleProjectileData(pd, pd.name, fbHasRange ? fbRange : 1f, fbHasSpeed ? fbSpeed : 1f, modifiedPD, useOM,
                                fbHasLifetime ? fbLifetime : 1f, name);
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
        // Networked prefab twins
        //
        // Beta 0.9.47 split the prefab registries: the GameObjects in
        // GameDatabase.AllNetworkedPrefabs are no longer the same instances as
        // ObjectInfo.Prefab. Anything spawned through HelperMethods.SpawnAtLocation
        // comes from the networked list — including the starter HQ, which Si_MapBalance
        // places itself — so mutating only ObjectInfo.Prefab left those spawns on
        // vanilla values: on 0.9.47 the starter HQ spawned with FogOfWarViewDistance
        // 600 instead of the configured 700 and was only corrected afterwards by live
        // propagation. On 0.9.46 both lists held the same object, so one mutation did.
        //
        // The list also holds null entries on 0.9.47 — the NRE thrown out of
        // GameDatabase.GetSpawnablePrefabIndex(string) when another mod spawns by name —
        // so it is indexed defensively here.
        // =============================================
        private static readonly Dictionary<string, GameObject> _networkedPrefabs =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Prefabs the FoW sweep mutated this pass, to tell whether the base-structure prefab is a distinct object.</summary>
        private static readonly HashSet<int> _fowPrefabsTouched = new HashSet<int>();

        private static void BuildNetworkedPrefabIndex()
        {
            _networkedPrefabs.Clear();
            try
            {
                var db = GameDatabase.Database;
                if (db == null) return;
                var list = db.AllNetworkedPrefabs;
                if (list == null) return;
                int nulls = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var go = list[i];
                    if (go == null) { nulls++; continue; }
                    _networkedPrefabs[go.name] = go;
                }
                if (nulls > 0)
                    MelonLogger.Warning($"[PREFAB] GameDatabase.AllNetworkedPrefabs holds {nulls} null entries — GetSpawnablePrefabIndex(string) throws on those");
                LogDebug($"[PREFAB] indexed {_networkedPrefabs.Count} networked prefabs");
            }
            catch (Exception ex) { MelonLogger.Warning("[PREFAB] could not index networked prefabs: " + ex.Message); }
        }

        /// <summary>The networked-list twin of a prefab: null when it is the same object (0.9.46) or absent.</summary>
        private static GameObject NetworkedTwin(GameObject prefab)
        {
            if (prefab == null) return null;
            if (!_networkedPrefabs.TryGetValue(prefab.name, out var twin) || twin == null) return null;
            return ReferenceEquals(twin, prefab) ? null : twin;
        }

        /// <summary>Sets a float field on the first Sensor under a prefab. Returns the previous value, or -1 when not found.</summary>
        private static float SetSensorFloat(GameObject prefab, string field, float value)
        {
            if (prefab == null) return -1f;
            var comps = prefab.GetComponentsInChildren<Component>(true);
            foreach (var comp in comps)
            {
                if (comp == null || comp.GetType().Name != "Sensor") continue;
                var f = comp.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null || f.FieldType != typeof(float)) continue;
                float orig = (float)f.GetValue(comp);
                f.SetValue(comp, value);
                return orig;
            }
            return -1f;
        }

        // =============================================
        // Target distance: absolute override for Sensor.TargetingDistance (applied after range_mult)
        // =============================================

        private static void ApplyTargetDistanceOverrides(bool useOM)
        {
            if (_targetDistanceOverrides.Count == 0) return;

            BuildNetworkedPrefabIndex();
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0, twins = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
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
                    tdField.SetValue(comp, targetDist);
                    if (useOM) OMSetFloat(oiTarget, "TargetingDistance", targetDist);
                    // 0.9.47: also the networked-list twin, or spawns by name stay vanilla
                    var tdTwin = NetworkedTwin(info.Prefab);
                    if (tdTwin != null && SetSensorFloat(tdTwin, "TargetingDistance", targetDist) >= 0f) twins++;
                    LogDebug($"[TARGETDIST] {name}: Sensor.TargetingDistance {orig} -> {targetDist}{(useOM ? " (OM+direct)" : " (direct)")}");
                    applied++;
                    break;
                }
            }

            if (applied > 0)
                MelonLogger.Msg($"[TARGETDIST] Applied target distance overrides to {applied} units ({twins} networked prefab twins)");
        }

        // =============================================
        // FoW Distance: absolute override for Sensor.FogOfWarViewDistance
        // =============================================
        private static void ApplyFoWDistanceOverrides(bool useOM)
        {
            if (_fowDistanceOverrides.Count == 0) return;
            BuildNetworkedPrefabIndex();
            _fowPrefabsTouched.Clear();
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0, twins = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
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
                    // Always mutate prefab directly so starters inherit the value at spawn time.
                    // Also set via OM to sync to clients.
                    fowField.SetValue(comp, fowDist);
                    if (useOM) OMSetFloat(oiTarget, "FogOfWarViewDistance", fowDist);
                    // 0.9.47: also the networked-list twin, or spawns by name stay vanilla
                    _fowPrefabsTouched.Add(info.Prefab.GetInstanceID());
                    var fowTwin = NetworkedTwin(info.Prefab);
                    if (fowTwin != null && SetSensorFloat(fowTwin, "FogOfWarViewDistance", fowDist) >= 0f)
                    {
                        _fowPrefabsTouched.Add(fowTwin.GetInstanceID());
                        twins++;
                    }
                    LogDebug($"[FOW] {name}: FogOfWarViewDistance {orig} -> {fowDist}{(useOM ? " (OM+direct)" : " (direct)")}");
                    applied++;
                    break;
                }
            }
            ApplyFoWToBaseStructures(useOM);

            if (applied > 0)
                MelonLogger.Msg($"[FOW] Applied FoW distance overrides to {applied} units ({twins} networked prefab twins)");
        }


        /// <summary>
        /// WHAT THE LIVE BUILDINGS ACTUALLY CARRY, READ BACK AFTER ROUND START.
        ///
        /// The prefab reads 700 and the starter HQ still reveals 600 (DrMuck
        /// measured 600 m in game on 2026-09-08), and the first Radar Station
        /// built uncovers less than the second built from the same prefab. Both
        /// say the value is not being read from where we write it, so stop
        /// arguing from the prefab and read the instances.
        ///
        /// For every live structure that has a fow_distance override this logs
        /// each Sensor on the object (there may be more than one — the game's fog
        /// uses BaseGameObject.FogOfWarViewDistance, which is the MAX over them,
        /// while we only ever wrote the first), the computed value the fog
        /// actually uses, and the team's CurrentSensorScale, which multiplies it.
        /// The index disambiguates the first Radar Station from the second.
        /// </summary>

        // =============================================
        // THE PREFAB POOL, WHICH HANDS OUT PRE-OVERRIDE CLONES
        //
        // Game.SpawnPrefab does not instantiate the prefab. It calls
        // PrefabPool.Instantiate, and PrefabPool keeps
        // Dictionary<GameObject, Stack<GameObject>> Pools of clones made ahead of
        // time by WarmUpAsync (UnityEngine.Object.InstantiateAsync). A clone is a
        // frozen copy: mutating the prefab afterwards does not reach back into
        // clones that already exist.
        //
        // That is the whole mystery. The overrides are applied before the round,
        // and the client does receive them, but the starter HQ and the FIRST
        // Radar Station are served from clones warmed BEFORE the override, so
        // they carry vanilla values (600 and 200). The SECOND Radar Station finds
        // the pool empty, falls through to a real Instantiate of the prefab we
        // mutated, and comes out right (800). Verified on 2026-09-08: every
        // prefab and networked twin read the configured value at the exact moment
        // the HQ spawned vanilla.
        //
        // So drop the stale clones once the overrides are on the prefabs. The
        // pool refills itself on demand, and everything it makes from now on is
        // cloned from an overridden prefab. This costs the warm-up benefit once
        // per apply and buys correctness for every component-level override, not
        // just fog of war.
        // =============================================
        // 1.0.1 made PrefabPool.Drop(prefab) and PrefabPool.ClearAll() public, and OverrideManager
        // now drops the pool entry of every prefab it reaches through an asset's Prefab fallback
        // itself (MarkPoolDirty / FlushPoolDrops at the end of OverrideManager.Update). That covers
        // OM-set members. It does NOT cover what this mod writes onto prefabs directly — networked
        // twin Sensor values, the Nest SpawnOnInit.MaxPerTeam for alien_queens_max, ... — so the
        // flush stays, but goes through the public API when the build has it:
        //   Drop(prefab)  bumps the prefab's pool generation, so clones that are still being
        //                 instantiated asynchronously are discarded on arrival instead of being
        //                 pushed into the pool, and it refills from the mutated prefab right away.
        //   ClearAll()    the blanket variant for "we touched many prefabs": also resets InFlight
        //                 and the poolable cache.
        // The reflection sweep over the private Pools dictionary is kept only for builds without
        // that API (it cannot discard in-flight refills, which is why it was never fully reliable).
        private static MethodInfo _poolDropMethod, _poolClearAllMethod;
        private static bool _poolApiResolved;

        private static void ResolvePoolApi()
        {
            if (_poolApiResolved) return;
            _poolApiResolved = true;
            try
            {
                var poolType = typeof(PrefabPool);
                _poolDropMethod = poolType.GetMethod("Drop", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
                _poolClearAllMethod = poolType.GetMethod("ClearAll", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (_poolDropMethod != null && _poolDropMethod.ReturnType != typeof(int)) _poolDropMethod = null;
                if (_poolClearAllMethod != null && _poolClearAllMethod.ReturnType != typeof(int)) _poolClearAllMethod = null;
            }
            catch (Exception ex) { MelonLogger.Warning("[POOL] API lookup threw: " + ex.Message); }
        }

        /// <summary>Blanket flush: drop every pooled clone (after a full override apply / rebalance).</summary>
        internal static void FlushPrefabPool(string reason)
        {
            ResolvePoolApi();
            if (_poolClearAllMethod != null)
            {
                try
                {
                    int clones = (int)_poolClearAllMethod.Invoke(null, null);
                    MelonLogger.Msg($"[POOL] {reason}: dropped {clones} pre-override clone(s) via PrefabPool.ClearAll() — the pool refills from the overridden prefabs");
                    return;
                }
                catch (Exception ex) { MelonLogger.Warning("[POOL] ClearAll threw, falling back to reflection: " + ex.Message); }
            }
            FlushPrefabPoolReflection(reason);
        }

        /// <summary>
        /// Targeted flush for prefabs this mod mutated directly (outside OverrideManager). Falls back
        /// to the blanket flush when the public Drop API is missing.
        /// </summary>
        internal static void FlushPrefabPool(string reason, IEnumerable<GameObject> prefabs)
        {
            ResolvePoolApi();
            if (_poolDropMethod == null || prefabs == null) { FlushPrefabPool(reason); return; }
            try
            {
                int clones = 0, count = 0;
                var seen = new HashSet<GameObject>();
                foreach (var prefab in prefabs)
                {
                    if (prefab == null || !seen.Add(prefab)) continue;
                    count++;
                    clones += (int)_poolDropMethod.Invoke(null, new object[] { prefab });
                }
                MelonLogger.Msg($"[POOL] {reason}: dropped {clones} pre-override clone(s) across {count} prefab(s) via PrefabPool.Drop — in-flight refills discarded, the pool refills from the overridden prefabs");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[POOL] Drop threw, falling back to blanket flush: " + ex.Message);
                FlushPrefabPool(reason);
            }
        }

        private static void FlushPrefabPoolReflection(string reason)
        {
            try
            {
                var poolType = typeof(PrefabPool);
                var poolsField = poolType.GetField("Pools", BindingFlags.NonPublic | BindingFlags.Static)
                              ?? poolType.GetField("Pools", BindingFlags.Public | BindingFlags.Static);
                if (poolsField == null)
                {
                    MelonLogger.Warning("[POOL] PrefabPool.Pools not found — stale clones cannot be dropped");
                    return;
                }
                var pools = poolsField.GetValue(null) as System.Collections.IDictionary;
                if (pools == null) { MelonLogger.Warning("[POOL] PrefabPool.Pools is not a dictionary"); return; }

                int prefabs = 0, clones = 0;
                var stacks = new List<System.Collections.IEnumerable>();
                foreach (System.Collections.DictionaryEntry e in pools)
                {
                    if (e.Value is System.Collections.IEnumerable en) stacks.Add(en);
                }
                foreach (var st in stacks)
                {
                    prefabs++;
                    foreach (var o in st)
                    {
                        var go = o as GameObject;
                        if (go == null) continue;
                        clones++;
                        try { UnityEngine.Object.Destroy(go); } catch { }
                    }
                }
                pools.Clear();
                // Refills that are still in flight will land in the pool as pre-override clones;
                // nothing to do about that without the 1.0.1 generation counter.
                try
                {
                    var inFlight = poolType.GetField("InFlight", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as System.Collections.IDictionary;
                    inFlight?.Clear();
                }
                catch { }
                MelonLogger.Msg($"[POOL] {reason}: dropped {clones} pre-override clone(s) across {prefabs} prefab(s) (reflection) — the pool refills from the overridden prefabs");
            }
            catch (Exception ex) { MelonLogger.Warning("[POOL] flush threw: " + ex.Message); }
        }

        internal static void ProbeLiveFoW(string tag)
        {
            try
            {
                ProbeFoWPrefabs(tag);
                var teams = Team.Teams;
                if (teams == null) return;
                for (int t = 0; t < teams.Count; t++)
                {
                    var team = teams[t];
                    if (team == null) continue;
                    float scale = 1f;
                    try
                    {
                        var sp = team.GetType().GetProperty("CurrentSensorScale", BindingFlags.Public | BindingFlags.Instance);
                        if (sp != null) scale = (float)sp.GetValue(team);
                    }
                    catch { }

                    var structures = team.Structures;
                    if (structures == null) continue;
                    var seen = new Dictionary<string, int>();
                    for (int i = 0; i < structures.Count; i++)
                    {
                        var s = structures[i];
                        if (s == null) continue;
                        string disp = "?", intern = "";
                        try { disp = s.ObjectInfo?.DisplayName ?? "?"; } catch { }
                        try { intern = s.ObjectInfo?.name ?? ""; } catch { }
                        // The internal asset name is what separates "Sol Headquarters"
                        // from "Cent Headquarters": both carry the DisplayName
                        // "Headquarters", so passing the display name twice resolved to
                        // a key no override uses and the first probe logged nothing.
                        string cfg = ResolveConfigName(disp, intern);
                        if (string.IsNullOrEmpty(cfg)) cfg = disp;
                        if (!_fowDistanceOverrides.TryGetValue(cfg, out float want)) want = -1f;

                        seen.TryGetValue(cfg, out int idx);
                        seen[cfg] = idx + 1;

                        string vals = "";
                        int n = 0;
                        try
                        {
                            var comps = s.GetComponentsInChildren<Component>(true);
                            foreach (var c in comps)
                            {
                                if (c == null || c.GetType().Name != "Sensor") continue;
                                var f = c.GetType().GetField("FogOfWarViewDistance",
                                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (f == null || f.FieldType != typeof(float)) continue;
                                bool en = true;
                                try { en = ((Behaviour)c).enabled; } catch { }
                                vals += (n > 0 ? "," : "") + ((float)f.GetValue(c)).ToString("F0") + (en ? "" : "(off)");
                                n++;
                            }
                        }
                        catch { }

                        float computed = -1f;
                        try
                        {
                            var p = s.GetType().GetProperty("FogOfWarViewDistance", BindingFlags.Public | BindingFlags.Instance);
                            if (p != null) computed = (float)p.GetValue(s);
                        }
                        catch { }

                        Vector3 at = Vector3.zero;
                        try { at = s.transform.position; } catch { }

                        if (n == 0) continue;   // no Sensor on this building, nothing to say
                        MelonLogger.Msg(
                            $"[FOW/PROBE {tag}] {team.name} {cfg}#{idx} at=({at.x:F0},{at.z:F0}) " +
                            $"sensors=[{vals}] n={n} computed={computed:F0} want={want:F0} sensorScale={scale:F3}");
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[FOW/PROBE] threw: " + ex.Message); }
        }

        /// <summary>
        /// THE PREFAB SIDE, READ AT THE SAME MOMENT AS THE INSTANCES.
        ///
        /// Two Radar Stations built four seconds apart on 2026-09-08 came out
        /// permanently different — the first with the vanilla 200, the second
        /// with the configured 800 — from what is supposed to be one prefab. So
        /// print what the assets actually hold while those instances are alive:
        /// every ObjectInfo that resolves to a name with a fow_distance override,
        /// its prefab, the prefab's Sensors, and the networked twin. If the
        /// prefab reads 800 the first radar was built from something else; if it
        /// reads 200 the sweep never reached it and the second radar's 800 comes
        /// from the OverrideManager overlay instead.
        /// </summary>
        static void ProbeFoWPrefabs(string tag)
        {
            try
            {
                BuildNetworkedPrefabIndex();
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    string cfg = ResolveConfigName(info.DisplayName, info.name);
                    if (string.IsNullOrEmpty(cfg)) continue;
                    if (!_fowDistanceOverrides.TryGetValue(cfg, out float want)) continue;

                    string vals = SensorValues(info.Prefab);
                    var twin = NetworkedTwin(info.Prefab);
                    string twinTxt = twin == null ? "none" : $"#{twin.GetInstanceID()} [{SensorValues(twin)}]";
                    MelonLogger.Msg(
                        $"[FOW/PREFAB {tag}] {cfg} asset={info.name} prefab#{info.Prefab.GetInstanceID()} " +
                        $"sensors=[{vals}] want={want:F0} twin={twinTxt}");
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[FOW/PREFAB] threw: " + ex.Message); }
        }

        /// <summary>Comma separated FogOfWarViewDistance of every Sensor under a GameObject.</summary>
        static string SensorValues(GameObject go)
        {
            string vals = "";
            int n = 0;
            try
            {
                var comps = go.GetComponentsInChildren<Component>(true);
                foreach (var c in comps)
                {
                    if (c == null || c.GetType().Name != "Sensor") continue;
                    var f = c.GetType().GetField("FogOfWarViewDistance",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f == null || f.FieldType != typeof(float)) continue;
                    vals += (n > 0 ? "," : "") + ((float)f.GetValue(c)).ToString("F0");
                    n++;
                }
            }
            catch { }
            return n == 0 ? "none" : vals;
        }

        /// <summary>
        /// THE STARTER HQ, WHICH IS SPAWNED FROM A PREFAB WE MAY NEVER SEE.
        ///
        /// The base structures are not placed through the construction system: the
        /// game (and Si_MapBalance's SpawnBaseStructures prefix) spawns
        /// team.BaseStructure.Prefab directly. On 0.9.47 that ObjectInfo is resolved
        /// through GameDatabase.GetObjectInfoByIndex, and the asset graph changed
        /// under us this build — 37 of 115 prefabs now have a distinct twin in
        /// AllNetworkedPrefabs where previously there was one shared object. The
        /// starter HQ kept spawning with the vanilla FogOfWarViewDistance of 600
        /// while every later-built structure carried the configured value, so the
        /// sweep over Resources.FindObjectsOfTypeAll&lt;ObjectInfo&gt;() evidently does
        /// not reach the prefab the base spawn uses.
        ///
        /// This walks the teams and mutates that prefab by the same route, and logs
        /// the identity of what it found: when "covered by sweep" is false, or the
        /// value read back was vanilla, this is a distinct object and this pass is
        /// what fixes it.
        /// </summary>
        private static void ApplyFoWToBaseStructures(bool useOM)
        {
            try
            {
                var teams = Team.Teams;
                if (teams == null) return;
                for (int i = 0; i < teams.Count; i++)
                {
                    var team = teams[i];
                    if (team == null) continue;
                    ObjectInfo bs = null;
                    try { bs = team.BaseStructure; } catch { }
                    if (bs == null || bs.Prefab == null) continue;

                    string name = ResolveConfigName(bs.DisplayName, bs.name);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!_fowDistanceOverrides.TryGetValue(name, out float fowDist)) continue;

                    int prefabId = bs.Prefab.GetInstanceID();
                    bool covered = _fowPrefabsTouched.Contains(prefabId);
                    float orig = SetSensorFloat(bs.Prefab, "FogOfWarViewDistance", fowDist);
                    // The direct mutation is server-side only. The client renders its own
                    // instance, so the override has to reach this asset by name as well or
                    // the commander keeps seeing the vanilla radius around the starter HQ.
                    if (useOM) OMSetFloat($"A:{bs.name}.asset", "FogOfWarViewDistance", fowDist);
                    MelonLogger.Msg($"[FOW/BASE] {team.name} BaseStructure {name}: prefab#{prefabId} sensor {orig} -> {fowDist} (covered by sweep: {covered}, om: {useOM})");

                    var twin = NetworkedTwin(bs.Prefab);
                    if (twin != null)
                    {
                        float twinOrig = SetSensorFloat(twin, "FogOfWarViewDistance", fowDist);
                        MelonLogger.Msg($"[FOW/BASE] {team.name} BaseStructure {name}: networked twin#{twin.GetInstanceID()} sensor {twinOrig} -> {fowDist}");
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[FOW/BASE] threw: " + ex.Message); }
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
                string name = ResolveConfigName(info.DisplayName, info.name);
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
                        LogDebug($"[JUMP] {name}: JumpSpeed {orig:F1} -> {newVal:F1} x{mult:F2} (OM)");
                        applied++;
                    }
                    else if (!useOM)
                    {
                        jsField.SetValue(comp, newVal);
                        LogDebug($"[JUMP] {name}: JumpSpeed {orig:F1} -> {newVal:F1} x{mult:F2} (direct)");
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
            var modifiedPD = new HashSet<string>();
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                if (!_visibleEventRadiusMultipliers.TryGetValue(name, out float mult)) continue;

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                bool anyApplied = false;

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    // VehicleTurret: PrimaryProjectile/SecondaryProjectile
                    if (typeName == "VehicleTurret")
                    {
                        foreach (string pdFieldName in new[] { "PrimaryProjectile", "SecondaryProjectile" })
                        {
                            var pdField = comp.GetType().GetField(pdFieldName,
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (pdField == null) continue;
                            var pdObj = pdField.GetValue(comp);
                            if (pdObj == null) continue;
                            string pdName = (pdObj as UnityEngine.Object)?.name ?? "";
                            if (string.IsNullOrEmpty(pdName) || modifiedPD.Contains(pdName)) continue;
                            float origVER = GetFloatMember(pdObj, "VisibleEventRadius");
                            if (origVER <= 0) continue;
                            float newVER = origVER * mult;
                            string pdTarget = useOM ? $"A:{pdName}.asset" : null;
                            if (useOM) OMSetFloat(pdTarget, "VisibleEventRadius", newVER);
                            else SetFloatMember(pdObj, "VisibleEventRadius", newVER);
                            LogDebug($"[VER] {name}: {pdName} VisibleEventRadius {origVER:F0} -> {newVER:F0} x{mult:F2}");
                            modifiedPD.Add(pdName);
                            anyApplied = true;
                        }
                    }

                    // CreatureDecapod: AttackPrimary/AttackSecondary → AttackProjectileData
                    if (typeName == "CreatureDecapod")
                    {
                        foreach (string attackFieldName in new[] { "AttackPrimary", "AttackSecondary" })
                        {
                            var attackField = comp.GetType().GetField(attackFieldName,
                                BindingFlags.Public | BindingFlags.Instance);
                            if (attackField == null) continue;
                            object attackObj;
                            try { attackObj = attackField.GetValue(comp); } catch { continue; }
                            if (attackObj == null) continue;
                            var pdField = attackObj.GetType().GetField("AttackProjectileData",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (pdField == null) continue;
                            object pdObj;
                            try { pdObj = pdField.GetValue(attackObj); } catch { continue; }
                            if (pdObj == null) continue;
                            string pdName = "";
                            try { pdName = ((UnityEngine.Object)pdObj).name; } catch { continue; }
                            if (string.IsNullOrEmpty(pdName) || modifiedPD.Contains(pdName)) continue;
                            float origVER = GetFloatMember(pdObj, "VisibleEventRadius");
                            if (origVER <= 0) continue;
                            float newVER = origVER * mult;
                            string pdTarget = useOM ? $"A:{pdName}.asset" : null;
                            if (useOM) OMSetFloat(pdTarget, "VisibleEventRadius", newVER);
                            else SetFloatMember(pdObj, "VisibleEventRadius", newVER);
                            LogDebug($"[VER] {name}: {pdName} VisibleEventRadius {origVER:F0} -> {newVER:F0} x{mult:F2}");
                            modifiedPD.Add(pdName);
                            anyApplied = true;
                        }
                    }

                    // Generic: any component field of type ProjectileData (catches infantry CharacterAttachment)
                    var fields = comp.GetType().GetFields(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var field in fields)
                    {
                        if (field.FieldType.Name != "ProjectileData") continue;
                        object pdObj2;
                        try { pdObj2 = field.GetValue(comp); } catch { continue; }
                        if (pdObj2 == null) continue;
                        string pdName2 = "";
                        try { pdName2 = ((UnityEngine.Object)pdObj2).name; } catch { continue; }
                        if (string.IsNullOrEmpty(pdName2) || modifiedPD.Contains(pdName2)) continue;
                        float origVER = GetFloatMember(pdObj2, "VisibleEventRadius");
                        if (origVER <= 0) continue;
                        float newVER = origVER * mult;
                        string pdTarget = useOM ? $"A:{pdName2}.asset" : null;
                        if (useOM) OMSetFloat(pdTarget, "VisibleEventRadius", newVER);
                        else SetFloatMember(pdObj2, "VisibleEventRadius", newVER);
                        LogDebug($"[VER] {name}: {pdName2} VisibleEventRadius {origVER:F0} -> {newVER:F0} x{mult:F2}");
                        modifiedPD.Add(pdName2);
                        anyApplied = true;
                    }
                }

                if (anyApplied) applied++;
            }
            if (applied > 0)
                MelonLogger.Msg($"[VER] Applied VisibleEventRadius overrides to {applied} units");
        }

        private static void ApplyMoveSpeedOverrides(bool useOM)
        {
            if (_moveSpeedMultipliers.Count == 0 && _turboSpeedMultipliers.Count == 0 && _flySpeedMultipliers.Count == 0
                && _runSpeedMultipliers.Count == 0 && _sprintSpeedMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;

                bool hasMoveSpeed = _moveSpeedMultipliers.TryGetValue(name, out float mult);
                bool hasTurboSpeed = _turboSpeedMultipliers.TryGetValue(name, out float turboMult);
                bool hasFlySpeed = _flySpeedMultipliers.TryGetValue(name, out float flyMult);
                bool hasRunSpeed = _runSpeedMultipliers.TryGetValue(name, out float runMult);
                bool hasSprintSpeed = _sprintSpeedMultipliers.TryGetValue(name, out float sprintMult);
                if (!hasMoveSpeed && !hasTurboSpeed && !hasFlySpeed && !hasRunSpeed && !hasSprintSpeed) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                if (hasMoveSpeed)
                    LogDebug($"[MOVESPEED] Applying move_speed_mult x{mult:F2} to '{name}'{(useOM ? " (OM)" : "")}");
                if (hasFlySpeed)
                    LogDebug($"[MOVESPEED] Applying fly_speed_mult x{flyMult:F2} to '{name}'{(useOM ? " (OM)" : "")}");
                if (hasTurboSpeed)
                    LogDebug($"[MOVESPEED] Applying turbo_speed_mult x{turboMult:F2} to '{name}'{(useOM ? " (OM)" : "")}");
                if (hasRunSpeed)
                    LogDebug($"[MOVESPEED] Applying run_speed_mult x{runMult:F2} to '{name}'{(useOM ? " (OM)" : "")}");
                if (hasSprintSpeed)
                    LogDebug($"[MOVESPEED] Applying sprint_speed_mult x{sprintMult:F2} to '{name}'{(useOM ? " (OM)" : "")}");

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
                            // Each speed field uses its specific mult if set, else falls back to move_speed_mult
                            float fieldMult;
                            if (fieldName == "FlyMoveSpeed")
                            {
                                if (hasFlySpeed) fieldMult = flyMult;
                                else if (hasMoveSpeed) fieldMult = mult;
                                else continue;
                            }
                            else if (fieldName == "TurboSpeed")
                            {
                                if (hasTurboSpeed) fieldMult = turboMult;
                                else if (hasMoveSpeed) fieldMult = mult;
                                else continue;
                            }
                            else if (fieldName == "RunSpeed")
                            {
                                if (hasRunSpeed) fieldMult = runMult;
                                else if (hasMoveSpeed) fieldMult = mult;
                                else continue;
                            }
                            else if (fieldName == "SprintSpeed")
                            {
                                if (hasSprintSpeed) fieldMult = sprintMult;
                                else if (hasMoveSpeed) fieldMult = mult;
                                else continue;
                            }
                            else
                            {
                                if (!hasMoveSpeed) continue;
                                fieldMult = mult;
                            }

                            // Il2Cpp: serialized fields may be properties — try field first, then property
                            float orig;
                            var field = compType.GetField(fieldName,
                                BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                            if (field != null && field.FieldType == typeof(float))
                            {
                                orig = (float)field.GetValue(comp);
                            }
                            else
                            {
                                var prop = compType.GetProperty(fieldName,
                                    BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                                if (prop == null || prop.PropertyType != typeof(float)) continue;
                                orig = (float)prop.GetValue(comp);
                            }
                            if (orig <= 0) continue;
                            // Anti-compounding: cache the first-seen value as the true original
                            string cacheKey = $"{name}_{compType.Name}_{fieldName}";
                            if (!_originalMoveSpeeds.ContainsKey(cacheKey))
                                _originalMoveSpeeds[cacheKey] = orig;
                            else
                                orig = _originalMoveSpeeds[cacheKey];
                            float newVal = orig * fieldMult;
                            if (OMSetFloat(oiTarget, fieldName, newVal))
                            {
                                LogDebug($"  {compType.Name}.{fieldName}: {orig} -> {newVal} (OM)");
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

        private static void ApplyStrafeSpeedOverrides(bool useOM)
        {
            if (_strafeSpeedMultipliers.Count == 0) return;
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                if (!_strafeSpeedMultipliers.TryGetValue(name, out float mult)) continue;

                string oiTarget = useOM ? $"A:{info.name}.asset" : null;
                LogDebug($"[STRAFE] Applying strafe_speed_mult x{mult:F2} to '{name}'{(useOM ? " (OM)" : "")}");

                var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                bool done = false;
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    var ct = comp.GetType();
                    string tn = ct.Name;

                    // CreatureDecapod → scale FlyMoveScaleSide
                    if (tn == "CreatureDecapod")
                    {
                        // Il2Cpp: try field first, then property
                        float orig;
                        bool found = false;
                        var f = ct.GetField("FlyMoveScaleSide", BindingFlags.Public | BindingFlags.Instance);
                        if (f != null && f.FieldType == typeof(float))
                        {
                            orig = (float)f.GetValue(comp);
                            found = true;
                        }
                        else
                        {
                            var p = ct.GetProperty("FlyMoveScaleSide", BindingFlags.Public | BindingFlags.Instance);
                            if (p != null && p.PropertyType == typeof(float))
                            {
                                orig = (float)p.GetValue(comp);
                                found = true;
                            }
                            else orig = 0;
                        }
                        if (found)
                        {
                            float newVal = orig * mult;
                            if (useOM && OMSetFloat(oiTarget, "FlyMoveScaleSide", newVal))
                                LogDebug($"  CreatureDecapod.FlyMoveScaleSide: {orig} -> {newVal} (OM)");
                            else
                            {
                                if (f != null) f.SetValue(comp, newVal);
                                else { var p = ct.GetProperty("FlyMoveScaleSide", BindingFlags.Public | BindingFlags.Instance); p?.SetValue(comp, newVal); }
                                LogDebug($"  CreatureDecapod.FlyMoveScaleSide: {orig} -> {newVal} (direct)");
                            }
                            done = true;
                        }
                        break;
                    }
                    // VehicleAir → scale StrafeSpeed
                    if (tn == "VehicleAir")
                    {
                        // Il2Cpp: try field first, then property
                        float orig;
                        bool found = false;
                        var f = ct.GetField("StrafeSpeed", BindingFlags.Public | BindingFlags.Instance);
                        if (f != null && f.FieldType == typeof(float))
                        {
                            orig = (float)f.GetValue(comp);
                            found = true;
                        }
                        else
                        {
                            var p = ct.GetProperty("StrafeSpeed", BindingFlags.Public | BindingFlags.Instance);
                            if (p != null && p.PropertyType == typeof(float))
                            {
                                orig = (float)p.GetValue(comp);
                                found = true;
                            }
                            else orig = 0;
                        }
                        if (found)
                        {
                            float newVal = orig * mult;
                            if (useOM && OMSetFloat(oiTarget, "StrafeSpeed", newVal))
                                LogDebug($"  VehicleAir.StrafeSpeed: {orig} -> {newVal} (OM)");
                            else
                            {
                                if (f != null) f.SetValue(comp, newVal);
                                else { var p = ct.GetProperty("StrafeSpeed", BindingFlags.Public | BindingFlags.Instance); p?.SetValue(comp, newVal); }
                                LogDebug($"  VehicleAir.StrafeSpeed: {orig} -> {newVal} (direct)");
                            }
                            done = true;
                        }
                        break;
                    }
                }
                if (done) applied++;
            }
            if (applied > 0)
                MelonLogger.Msg($"[STRAFE] Applied strafe speed overrides to {applied} units");
        }

        private static void ApplyTurnRadiusOverrides(bool useOM)
        {
            if (_turnRadiusMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int applied = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
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
                        LogDebug($"[TURNRADIUS] {name}: TurningCircleRadius {orig:F1} -> {newVal:F1} (OM)");
                        applied++;
                    }
                    else
                    {
                        field.SetValue(comp, newVal);
                        LogDebug($"[TURNRADIUS] {name}: TurningCircleRadius {orig:F1} -> {newVal:F1} (direct)");
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

                    string name = ResolveConfigName(info.DisplayName, info.name);
                    string oiTarget = useOM ? $"A:{info.name}.asset" : null;

                    if (_teleportCooldown >= 0)
                    {
                        var f = comp.GetType().GetField("TeleportCooldownTime", flags);
                        if (f != null && f.FieldType == typeof(float))
                        {
                            float orig = (float)f.GetValue(comp);
                            if (useOM && OMSetFloat(oiTarget, "TeleportCooldownTime", _teleportCooldown))
                            {
                                LogDebug($"[TELEPORT] {name}: CooldownTime {orig:F1} -> {_teleportCooldown:F1} (OM)");
                                applied++;
                            }
                            else
                            {
                                f.SetValue(comp, _teleportCooldown);
                                LogDebug($"[TELEPORT] {name}: CooldownTime {orig:F1} -> {_teleportCooldown:F1} (direct)");
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
                                LogDebug($"[TELEPORT] {name}: TeleportTime {orig:F1} -> {_teleportDuration:F1} (OM)");
                                applied++;
                            }
                            else
                            {
                                f.SetValue(comp, _teleportDuration);
                                LogDebug($"[TELEPORT] {name}: TeleportTime {orig:F1} -> {_teleportDuration:F1} (direct)");
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
                            LogDebug($"[DISPENSER] {info.DisplayName}: DispenseTimeout {orig:F1} -> {_dispenseTimeout:F1} (OM)");
                            applied++;
                        }
                        else
                        {
                            f.SetValue(comp, _dispenseTimeout);
                            LogDebug($"[DISPENSER] {info.DisplayName}: DispenseTimeout {orig:F1} -> {_dispenseTimeout:F1} (direct)");
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
                        LogDebug($"  [{typeName}] field {field.Name}: {orig} -> {newVal}");
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
                        LogDebug($"  [{typeName}] prop {prop.Name}: {orig} -> {newVal}");
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
                    LogDebug($"  Component: {comp.GetType().Name} on '{comp.gameObject.name}'");
                }
            }

            return totalModified;
        }

        // Helper: get bool field or property (Il2Cpp exposes fields as properties)
        private static bool GetBoolMember(object obj, string name)
        {
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return (bool)field.GetValue(obj);
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null) return (bool)prop.GetValue(obj);
            return false;
        }

        private static void SetBoolMember(object obj, string name, bool value)
        {
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { field.SetValue(obj, value); return; }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite) prop.SetValue(obj, value);
        }

        // Apply per-unit projectile tuning to every ProjectileData a unit fires:
        //   impact_scale_by_speed  -> m_bImpactScaleBySpeed (bool)
        //   drag_mult              -> m_fDragCoefficient *= mult
        //   ai_lead_drag/vel/grav  -> m_AIAimLeadFactor_{Drag,Velocity,Gravity} (absolute)
        // Covers direct ProjectileData fields (VehicleTurret.Primary/SecondaryProjectileData) —
        // every vehicle weapon, which is the case tanks (Crimson/Siege) use.
        // Handles impact_scale_by_speed + drag_mult. The AI aim-lead multipliers
        // (ai_lead_*) are applied in ScaleAimLeadFactors instead, so they compose with
        // the speed auto-compensation (final = vanilla × 1/speedMult × mult).
        private static void ApplyProjectileTuningOverrides(bool useOM)
        {
            if (_impactScaleBySpeedOverrides.Count == 0 && _dragMultipliers.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            var done = new HashSet<string>();
            int units = 0;

            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;

                bool hasScale = _impactScaleBySpeedOverrides.TryGetValue(name, out bool wantScale);
                bool hasDrag  = _dragMultipliers.TryGetValue(name, out float dragMult);
                if (!hasScale && !hasDrag) continue;
                units++;

                foreach (var comp in info.Prefab.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    var ct = comp.GetType();
                    foreach (var pf in ct.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (pf.FieldType.Name != "ProjectileData") continue;
                        object pdObj;
                        try { pdObj = pf.GetValue(comp); } catch { continue; }
                        if (pdObj == null) continue;
                        string pdName;
                        try { pdName = ((UnityEngine.Object)pdObj).name; } catch { continue; }
                        if (string.IsNullOrEmpty(pdName) || done.Contains(pdName)) continue;
                        done.Add(pdName);

                        string target = useOM ? $"A:{pdName}.asset" : null;

                        if (hasScale)
                        {
                            bool cur = GetBoolMember(pdObj, "m_bImpactScaleBySpeed");
                            if (cur != wantScale)
                            {
                                if (useOM) OMSetBool(target, "m_bImpactScaleBySpeed", wantScale);
                                else SetBoolMember(pdObj, "m_bImpactScaleBySpeed", wantScale);
                                LogDebug($"[PROJTUNE] {name} -> {pdName}.m_bImpactScaleBySpeed: {cur} -> {wantScale}");
                            }
                        }
                        if (hasDrag)
                        {
                            float cur = GetFloatMember(pdObj, "m_fDragCoefficient");
                            float nv = cur * dragMult;
                            if (useOM) OMSetFloat(target, "m_fDragCoefficient", nv);
                            else SetFloatMember(pdObj, "m_fDragCoefficient", nv);
                            LogDebug($"[PROJTUNE] {name} -> {pdName}.m_fDragCoefficient: {cur:F3} -> {nv:F3} (x{dragMult})");
                        }
                    }
                }
            }
            if (done.Count > 0)
                MelonLogger.Msg($"[UnitBalance] projectile tuning: adjusted {done.Count} projectile(s) across {units} unit(s)");
        }

        // Force each unit's VehicleTurret.AimCompensation mode. Unlike the projectile
        // fields this lives on the prefab MonoBehaviour (not a ScriptableObject asset),
        // so it can't go through OverrideManager — we set it directly and cache the
        // vanilla value so a removed override restores it. Enables PLAYER aim
        // compensation on turrets the game ships as None (most tanks).
        private static void ApplyAimCompensationOverrides()
        {
            // Nothing to do until we've either got an override or previously touched a turret.
            if (_aimCompensationOverrides.Count == 0 && _aimCompVanilla.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int changed = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                bool hasOv = _aimCompensationOverrides.TryGetValue(name, out int ov);

                foreach (var comp in info.Prefab.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null || comp.GetType().Name != "VehicleTurret") continue;
                    var acf = comp.GetType().GetField("AimCompensation", BindingFlags.Public | BindingFlags.Instance);
                    if (acf == null) continue;

                    int cur;
                    try { cur = Convert.ToInt32(acf.GetValue(comp)); } catch { continue; }

                    string goName = comp.gameObject != null ? comp.gameObject.name : "?";
                    string cacheKey = name + "|" + goName;
                    if (!_aimCompVanilla.ContainsKey(cacheKey)) _aimCompVanilla[cacheKey] = cur; // remember vanilla
                    int vanilla = _aimCompVanilla[cacheKey];

                    // Only turrets we have (or had) an override for get driven; others are
                    // left exactly as the game set them.
                    if (!hasOv && vanilla == cur) continue;
                    int target = hasOv ? ov : vanilla; // override, or restore vanilla when removed
                    if (target == cur) continue;

                    try
                    {
                        acf.SetValue(comp, Enum.ToObject(acf.FieldType, target));
                        changed++;
                        LogDebug($"[AIMCOMP] {name} -> {goName}.AimCompensation: {cur} -> {target}");
                    }
                    catch (Exception ex) { MelonLogger.Warning($"[AIMCOMP] {name}/{goName} set failed: {ex.Message}"); }
                }
            }
            if (changed > 0) MelonLogger.Msg($"[UnitBalance] aim_compensation: set {changed} turret(s) [prefab]");

            // Prefab edits only affect FUTURE spawns — Unity copies MonoBehaviour fields at
            // instantiation. To make a mid-round !b edit hit the tank the player is already
            // driving, also set every LIVE turret in the scene. (Forces overrides only; the
            // vanilla restore for a removed override happens on the unit's next spawn.)
            if (_aimCompensationOverrides.Count == 0) return;
            int live = 0;
            try
            {
                var units = UnityEngine.Object.FindObjectsOfType(typeof(Unit));
                foreach (var uObj in units)
                {
                    var unit = uObj as Unit;
                    if (unit == null || unit.ObjectInfo == null) continue;
                    string name = ResolveConfigName(unit.ObjectInfo.DisplayName, unit.ObjectInfo.name);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!_aimCompensationOverrides.TryGetValue(name, out int ov)) continue;

                    foreach (var comp in unit.GetComponentsInChildren<Component>(true))
                    {
                        if (comp == null || comp.GetType().Name != "VehicleTurret") continue;
                        var acf = comp.GetType().GetField("AimCompensation", BindingFlags.Public | BindingFlags.Instance);
                        if (acf == null) continue;
                        int cur;
                        try { cur = Convert.ToInt32(acf.GetValue(comp)); } catch { continue; }
                        if (cur == ov) continue;
                        try
                        {
                            acf.SetValue(comp, Enum.ToObject(acf.FieldType, ov));
                            live++;
                            MelonLogger.Msg($"[AIMCOMP-LIVE] {name}: AimCompensation {cur} -> {ov}");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[AIMCOMP] live pass failed: {ex.Message}"); }
            if (live > 0) MelonLogger.Msg($"[UnitBalance] aim_compensation: set {live} LIVE turret(s) in scene");
        }

        // Force VehicleTurret.PitchMax{Up,Down} (degrees) per unit — prefab + live instances,
        // vanilla cached for restore. Direct-write (server-side): works for AI-controlled
        // turrets; a human player's own turret is likely still clamped by their client's copy
        // (same networking caveat as aim_compensation; see CHANGELOG). The proper player-facing
        // path is an OM networked ("N:") override — TODO once the target format is confirmed.
        private static void ApplyTurretPitchOverrides()
        {
            if (_pitchMaxUpOverrides.Count == 0 && _pitchMaxDownOverrides.Count == 0 && _pitchVanilla.Count == 0) return;

            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            int changed = 0;
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var comp in info.Prefab.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null || comp.GetType().Name != "VehicleTurret") continue;
                    changed += SetTurretPitchField(comp, name, "PitchMaxUp", _pitchMaxUpOverrides);
                    changed += SetTurretPitchField(comp, name, "PitchMaxDown", _pitchMaxDownOverrides);
                }
            }
            if (changed > 0) MelonLogger.Msg($"[UnitBalance] pitch clamp: set {changed} field(s) [prefab]");

            if (_pitchMaxUpOverrides.Count == 0 && _pitchMaxDownOverrides.Count == 0) return;
            int live = 0;
            try
            {
                foreach (var uObj in UnityEngine.Object.FindObjectsOfType(typeof(Unit)))
                {
                    var unit = uObj as Unit;
                    if (unit == null || unit.ObjectInfo == null) continue;
                    string name = ResolveConfigName(unit.ObjectInfo.DisplayName, unit.ObjectInfo.name);
                    if (string.IsNullOrEmpty(name)) continue;
                    bool hasUp = _pitchMaxUpOverrides.TryGetValue(name, out float ovUp);
                    bool hasDown = _pitchMaxDownOverrides.TryGetValue(name, out float ovDown);
                    if (!hasUp && !hasDown) continue;
                    foreach (var comp in unit.GetComponentsInChildren<Component>(true))
                    {
                        if (comp == null || comp.GetType().Name != "VehicleTurret") continue;
                        if (hasUp) live += SetTurretPitchLive(comp, "PitchMaxUp", ovUp);
                        if (hasDown) live += SetTurretPitchLive(comp, "PitchMaxDown", ovDown);
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[PITCH] live pass failed: {ex.Message}"); }
            if (live > 0) MelonLogger.Msg($"[UnitBalance] pitch clamp: set {live} LIVE field(s) in scene");
        }

        private static int SetTurretPitchField(Component comp, string unitName, string field, Dictionary<string, float> dict)
        {
            var f = comp.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) return 0;
            float cur; try { cur = (float)f.GetValue(comp); } catch { return 0; }
            string goName = comp.gameObject != null ? comp.gameObject.name : "?";
            string key = unitName + "|" + goName + "|" + field;
            if (!_pitchVanilla.ContainsKey(key)) _pitchVanilla[key] = cur;
            float vanilla = _pitchVanilla[key];
            bool hasOv = dict.TryGetValue(unitName, out float ov);
            if (!hasOv && Math.Abs(vanilla - cur) < 0.01f) return 0;
            float target = hasOv ? ov : vanilla;
            if (Math.Abs(target - cur) < 0.01f) return 0;
            try { f.SetValue(comp, target); LogDebug($"[PITCH] {unitName} -> {goName}.{field}: {cur:F0} -> {target:F0}"); return 1; }
            catch (Exception ex) { MelonLogger.Warning($"[PITCH] {unitName}/{goName}.{field} set failed: {ex.Message}"); return 0; }
        }

        private static int SetTurretPitchLive(Component comp, string field, float ov)
        {
            var f = comp.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) return 0;
            float cur; try { cur = (float)f.GetValue(comp); } catch { return 0; }
            if (Math.Abs(cur - ov) < 0.01f) return 0;
            try { f.SetValue(comp, ov); MelonLogger.Msg($"[PITCH-LIVE] {field} {cur:F0} -> {ov:F0}"); return 1; } catch { return 0; }
        }

        private static bool HasAnyLeadMult(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return _aiLeadDragOverrides.ContainsKey(name)
                || _aiLeadVelocityOverrides.ContainsKey(name)
                || _aiLeadGravityOverrides.ContainsKey(name);
        }

        // Helper: get/set float field or property (Il2Cpp exposes fields as properties)
        /// <summary>
        /// Get DefaultTeam via reflection (field or property) to survive field→property changes across game versions.
        /// </summary>
        private static Team GetDefaultTeam(BaseGameObject obj)
        {
            var type = obj.GetType();
            // Walk up hierarchy — DefaultTeam may be declared on BaseGameObject, not the concrete type
            while (type != null)
            {
                var field = type.GetField("DefaultTeam", BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return field.GetValue(obj) as Team;
                var prop = type.GetProperty("DefaultTeam", BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(obj) as Team;
                type = type.BaseType;
            }
            return null;
        }

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
            HashSet<string> modifiedPD, bool useOM = false, float lifetimeMult = 1f, string unitName = null)
        {
            string pdTarget = useOM ? $"A:{pdName}.asset" : null;

            // Anti-compounding: cache first-seen values as true originals
            float origLt = GetFloatMember(pdObj, "m_fLifeTime");
            string ltKey = $"{pdName}_lifetime";
            if (!_originalProjectileLifetimes.ContainsKey(ltKey))
                _originalProjectileLifetimes[ltKey] = origLt;
            else
                origLt = _originalProjectileLifetimes[ltKey];

            float origSpeed = GetFloatMember(pdObj, "m_fBaseSpeed");
            string spdKey = $"{pdName}_speed";
            if (!_originalProjectileSpeeds.ContainsKey(spdKey))
                _originalProjectileSpeeds[spdKey] = origSpeed;
            else
                origSpeed = _originalProjectileSpeeds[spdKey];

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
                LogDebug($"  ProjectileData '{pdName}' (instant-hit): Range {origSpeed:F0} -> {newSpeed:F0}{(useOM ? " (OM)" : "")}");
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
                    LogDebug($"  ProjectileData '{pdName}': Lifetime {origLt} -> {newLt:F2}");
                }

                if (Math.Abs(speedMult - 1f) > 0.001f && origSpeed > 0)
                {
                    if (useOM) OMSetFloat(pdTarget, "m_fBaseSpeed", newSpeed);
                    else SetFloatMember(pdObj, "m_fBaseSpeed", newSpeed);
                    LogDebug($"  ProjectileData '{pdName}': Speed {origSpeed} -> {newSpeed:F1}");
                }

                LogDebug($"  ProjectileData '{pdName}': Range {origRange:F0} -> {newRange:F0}{(useOM ? " (OM)" : "")}");

                // AI aim lead compensation: speed change auto-scales lead by 1/speedMult
                // (faster projectile needs less lead), and any per-unit ai_lead_* multiplier
                // is layered on top: final = vanilla × (1/speedMult) × mult. Runs when there's
                // a speed change OR a lead multiplier — otherwise it's a no-op we skip.
                bool speedChanged = Math.Abs(speedMult - 1f) > 0.001f;
                if (speedChanged || HasAnyLeadMult(unitName))
                {
                    float invSpeed = speedChanged ? 1f / speedMult : 1f;
                    ScaleAimLeadFactors(pdObj, pdName, pdTarget, invSpeed, useOM, unitName);
                }
            }

            // For instant-hit: lifetime mult also applies (visual beam duration)
            if (isInstantHit && Math.Abs(lifetimeMult - 1f) > 0.001f && origLt > 0)
            {
                float newLt = origLt * lifetimeMult;
                if (useOM) OMSetFloat(pdTarget, "m_fLifeTime", newLt);
                else SetFloatMember(pdObj, "m_fLifeTime", newLt);
                LogDebug($"  ProjectileData '{pdName}' (instant-hit): Lifetime {origLt} -> {newLt:F2}");
            }

            // Scale VisibleEventRadius for instant-hit projectiles (beams need VER to match range)
            // Normal projectiles (flames, balls) don't need VER scaling — projectile renders independently
            // VER is also available as a manual UI setting (visible_event_radius_mult) — only auto-scale here for range_mult
            if (Math.Abs(rangeMult - 1f) > 0.001f && isInstantHit)
            {
                float origVER = GetFloatMember(pdObj, "VisibleEventRadius");
                if (origVER > 0)
                {
                    float newVER = origVER * rangeMult;
                    if (useOM) OMSetFloat(pdTarget, "VisibleEventRadius", newVER);
                    else SetFloatMember(pdObj, "VisibleEventRadius", newVER);
                    LogDebug($"  ProjectileData '{pdName}': VisibleEventRadius {origVER:F0} -> {newVER:F0}");
                }
            }

            modifiedPD.Add(pdName);
        }

        /// <summary>
        /// Sets the AI aim-lead factors on a ProjectileData to vanilla × speedScale × userMult.
        /// speedScale (1/speedMult) auto-compensates for a projectile speed change; the per-unit
        /// ai_lead_{velocity,gravity,drag} multipliers layer a manual correction on top (e.g. to
        /// fix Crimson under-leading its draggy shell). Anchored to the cached vanilla value so
        /// re-applies can't compound. A field whose final value equals vanilla is left unwritten
        /// (no wasted override).
        /// </summary>
        private static void ScaleAimLeadFactors(object pdObj, string pdName, string pdTarget, float scale, bool useOM, string unitName)
        {
            string[] fields = { "m_AIAimLeadFactor_Velocity", "m_AIAimLeadFactor_Gravity", "m_AIAimLeadFactor_Drag" };
            Dictionary<string, float>[] caches = { _originalAimLeadVelocity, _originalAimLeadGravity, _originalAimLeadDrag };
            Dictionary<string, float>[] userMults = { _aiLeadVelocityOverrides, _aiLeadGravityOverrides, _aiLeadDragOverrides };

            for (int i = 0; i < fields.Length; i++)
            {
                string field = fields[i];
                string cacheKey = $"{pdName}_{field}";
                float orig = GetFloatMember(pdObj, field);
                if (float.IsNaN(orig)) continue;

                if (!caches[i].ContainsKey(cacheKey))
                    caches[i][cacheKey] = orig;
                else
                    orig = caches[i][cacheKey];

                float userMult = 1f;
                if (unitName != null) userMults[i].TryGetValue(unitName, out userMult);
                if (userMult == 0f) userMult = 1f; // guard against an unset/0 slipping through

                float newVal = orig * scale * userMult;
                // Skip writing a value that lands back on vanilla — saves an override slot.
                if (Math.Abs(newVal - orig) < 0.0001f) continue;

                if (useOM) OMSetFloat(pdTarget, field, newVal);
                else SetFloatMember(pdObj, field, newVal);
                // Always log when the user has actively tuned this factor, so the applied
                // value is visible without turning on full debug logging.
                if (Math.Abs(userMult - 1f) > 0.001f)
                    MelonLogger.Msg($"[LEADFACTOR] {unitName} '{pdName}': {field} {orig:F3} -> {newVal:F3} (speed x{scale:F3}, user x{userMult:F3})");
                else
                    LogDebug($"  ProjectileData '{pdName}': {field} {orig:F3} -> {newVal:F3} (x{scale:F3} speed, x{userMult:F3} user)");
            }
        }

        // =============================================
        // Proximity Detonation: modify ProjectileProximityDetonation on projectile prefabs
        // Fields: MinimumTime (arming delay), SplashRadiusScale, FlyingUnits, GroundUnits, Structures
        // =============================================

        private static void ApplyProximityDetonationOverrides()
        {
            if (_proximityOverrides.Count == 0) return;

            var allProjectiles = Resources.FindObjectsOfTypeAll<ProjectileData>();
            int applied = 0;

            foreach (var pd in allProjectiles)
            {
                if (pd == null) continue;
                string pdName = pd.name;
                if (string.IsNullOrEmpty(pdName)) continue;
                if (!_proximityOverrides.TryGetValue(pdName, out var proxFields)) continue;

                // Get the projectile prefab GameObject from ProjectileData
                object prefabObj = null;
                try
                {
                    var prefabField = pd.GetType().GetField("m_ProjectilePrefab",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prefabField != null)
                        prefabObj = prefabField.GetValue(pd);
                    if (prefabObj == null)
                    {
                        var prefabProp = pd.GetType().GetProperty("m_ProjectilePrefab",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (prefabProp != null)
                            prefabObj = prefabProp.GetValue(pd);
                    }
                }
                catch { }

                if (prefabObj == null)
                {
                    MelonLogger.Warning($"[PROXIMITY] {pdName}: m_ProjectilePrefab not found");
                    continue;
                }

                // The prefab could be a GameObject or a Component (ProjectileBasic) — get the GameObject
                GameObject prefabGO = null;
                if (prefabObj is GameObject go)
                    prefabGO = go;
                else if (prefabObj is Component c)
                    prefabGO = c.gameObject;

                if (prefabGO == null)
                {
                    MelonLogger.Warning($"[PROXIMITY] {pdName}: Could not resolve prefab GameObject (type: {prefabObj.GetType().Name})");
                    continue;
                }

                // Find ProjectileProximityDetonation component on the prefab
                Component proxComp = null;
                foreach (var comp in prefabGO.GetComponentsInChildren<Component>(true))
                {
                    if (comp != null && comp.GetType().Name == "ProjectileProximityDetonation")
                    {
                        proxComp = comp;
                        break;
                    }
                }

                if (proxComp == null)
                {
                    MelonLogger.Warning($"[PROXIMITY] {pdName}: No ProjectileProximityDetonation component on prefab '{prefabGO.name}'");
                    continue;
                }

                var proxType = proxComp.GetType();
                LogDebug($"[PROXIMITY] Applying overrides to '{pdName}' (prefab '{prefabGO.name}'):");

                foreach (var kvp in proxFields)
                {
                    string fieldName = kvp.Key;
                    float newVal = kvp.Value;

                    // Handle bool fields (FlyingUnits, GroundUnits, Structures) — stored as 0/1
                    bool isBoolField = fieldName == "FlyingUnits" || fieldName == "GroundUnits" || fieldName == "Structures";

                    if (isBoolField)
                    {
                        bool boolVal = newVal >= 0.5f;
                        bool origBool = GetBoolMember(proxComp, fieldName);
                        var bField = proxType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                        if (bField != null)
                        {
                            bField.SetValue(proxComp, boolVal);
                            LogDebug($"  {fieldName}: {origBool} -> {boolVal}");
                        }
                        else
                        {
                            var bProp = proxType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
                            if (bProp != null && bProp.CanWrite)
                            {
                                bProp.SetValue(proxComp, boolVal);
                                LogDebug($"  {fieldName}: {origBool} -> {boolVal}");
                            }
                            else
                                MelonLogger.Warning($"  {fieldName}: field/property not found or read-only");
                        }
                    }
                    else
                    {
                        float origVal = GetFloatMember(proxComp, fieldName);
                        bool ok = SetFloatMember(proxComp, fieldName, newVal);
                        if (ok)
                            LogDebug($"  {fieldName}: {origVal:F3} -> {newVal:F3}");
                        else
                            MelonLogger.Warning($"  {fieldName}: SetFloatMember failed");
                    }
                }
                applied++;
            }

            if (applied > 0)
                MelonLogger.Msg($"[PROXIMITY] Applied proximity detonation overrides to {applied} projectile(s)");
        }

        // =============================================
        // Shrimp: prevent AI from auto-attacking (player attacks still work)
        // =============================================

        // =============================================
        // Nest-only alien building ("alien_nest_only_build")
        //
        // ObjectInfo.StructureConstructionAnchor decides which structures new buildings may be
        // placed around (ConstructionData.GetWithinBaseStructuresDistanceAndFogOfWar only measures
        // against IsConstructionAnchor structures) and which existing structures a freshly spawned
        // AlienStructure links to (AlienStructure.InitLink filters on CanBeConstructionAnchor).
        // Vanilla has it true on every alien structure, so cysts/nodes/caches creep the build area
        // outward. With the flag off on everything but the Nest, aliens can only build within
        // build_radius of a Nest and expand by building new Nests.
        //
        // The game reads the flag live from the ObjectInfo asset, so the OM overlay is enough (no
        // prefab-baked copy, no PrefabPool concern) and it syncs to clients, keeping the client-side
        // placement preview in agreement with the server. Structures already placed keep the links
        // they made at spawn; only new placements/spawns see the change.
        // =============================================

        private static readonly HashSet<string> _nestOnlyMutated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void ApplyAlienNestOnlyBuild(bool useOM)
        {
            // Toggle off: OM path is already clean (RevertAll ran before this apply); the direct
            // path restores whatever we flipped.
            if (!_alienNestOnlyBuild)
            {
                if (!useOM && _nestOnlyMutated.Count > 0)
                {
                    foreach (var info in Resources.FindObjectsOfTypeAll<ObjectInfo>())
                        if (info != null && _nestOnlyMutated.Contains(info.name))
                            info.StructureConstructionAnchor = true;
                    _nestOnlyMutated.Clear();
                }
                return;
            }

            try
            {
                int applied = 0;
                var names = new List<string>();
                foreach (var info in Resources.FindObjectsOfTypeAll<ObjectInfo>())
                {
                    if (info == null || info.Prefab == null) continue;
                    if (!info.name.StartsWith("ObjectInfo_Alien_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (info.name.Equals("ObjectInfo_Alien_Nest", StringComparison.OrdinalIgnoreCase)) continue;
                    if (info.Prefab.GetComponent<AlienStructure>() == null) continue; // creatures/tech tiers aren't anchors anyway
                    if (!info.StructureConstructionAnchor) continue;

                    bool ok;
                    if (useOM)
                        ok = OMSetBool($"A:{info.name}.asset", "StructureConstructionAnchor", false);
                    else
                    {
                        info.StructureConstructionAnchor = false;
                        _nestOnlyMutated.Add(info.name);
                        ok = true;
                    }
                    if (ok) { applied++; names.Add(info.DisplayName); }
                }
                MelonLogger.Msg($"[NESTONLY] Alien nest-only build ON: anchor flag off on {applied} structures ({string.Join(", ", names)}){(useOM ? " (OM)" : "")}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NESTONLY] Failed: {ex.Message}");
            }
        }

        // =============================================
        // Alien queens per team ("alien_queens_max")
        //
        // The Nest prefab carries a SpawnOnInit whose SpawnObject is the Queen and whose
        // MoveToCompartment is the Nest's AlienQueenCompartment. SpawnOnInit.Start runs on the server
        // for every Nest instance (built ones included) and skips the spawn once
        // Team.GetUnitCount(Queen) >= MaxPerTeam — vanilla 1, hence only the starting Nest has a
        // Queen. Raising MaxPerTeam (0 = no limit) gives expansion Nests their own docked Queen.
        // Server-side only (clients never run Start's spawn), so a direct prefab mutation is enough;
        // applies to Nests spawned after the change.
        // =============================================

        private static int? _originalQueenMaxPerTeam; // never cleared: prefab survives map changes and we always re-apply

        private static string FmtQueensMax(int v) => v < 0 ? "vanilla" : v == 0 ? "every Nest" : v.ToString();

        /// <summary>Returns the Nest prefab whose SpawnOnInit value changed (null when nothing changed).</summary>
        private static GameObject ApplyAlienQueensMax()
        {
            try
            {
                SpawnOnInit target = null;
                GameObject nestPrefab = null;
                foreach (var info in Resources.FindObjectsOfTypeAll<ObjectInfo>())
                {
                    if (info == null || info.Prefab == null) continue;
                    if (!info.name.Equals("ObjectInfo_Alien_Nest", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var soi in info.Prefab.GetComponentsInChildren<SpawnOnInit>(true))
                    {
                        if (soi == null || soi.SpawnObject == null) continue;
                        if (soi.SpawnObject.name.IndexOf("Queen", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        target = soi;
                        nestPrefab = info.Prefab;
                        break;
                    }
                    if (target != null) break;
                }
                if (target == null)
                {
                    if (_alienQueensMax >= 0)
                        MelonLogger.Warning("[QUEENS] Nest prefab has no SpawnOnInit for the Queen — alien_queens_max inactive");
                    return null;
                }

                if (_originalQueenMaxPerTeam == null) _originalQueenMaxPerTeam = target.MaxPerTeam;
                int wanted = _alienQueensMax < 0 ? _originalQueenMaxPerTeam.Value : _alienQueensMax;
                int before = target.MaxPerTeam;
                target.MaxPerTeam = wanted;
                if (before != wanted || _alienQueensMax >= 0)
                    MelonLogger.Msg($"[QUEENS] Nest SpawnOnInit.MaxPerTeam {before} -> {wanted} (vanilla {_originalQueenMaxPerTeam.Value}; config {FmtQueensMax(_alienQueensMax)})");
                return before != wanted ? nestPrefab : null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QUEENS] Failed: {ex.Message}");
                return null;
            }
        }

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
                                LogDebug($"[SHRIMP] AIMeleeDistance: {was} -> 0");
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
                                LogDebug($"[SHRIMP] Sensor.TargetingDistance: {was} -> 0");
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
                                LogDebug($"[SHRIMP] AIAiming.AimPaused -> True");
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

        // =============================================
        // Decay: per-faction enable/disable + custom DecayData params
        // =============================================

        // Cloned DecayData assets for per-faction overrides (avoids modifying shared vanilla asset)
        private static ScriptableObject _decayDataHuman;
        private static ScriptableObject _decayDataAlien;

        /// <summary>
        /// Apply per-faction decay settings.
        /// - Enable/disable: sets Team.UnlinkedStructureStateEnabled (built-in RPC sync).
        /// - Custom params: clones DecayData per faction, swaps the private Data field on
        ///   structure prefab Decay components. Server-only, no Harmony needed.
        /// Called from ApplyOverridesLogic (prefab-level) and OnGameStartedLogic (team-level).
        /// </summary>
        private static void ApplyDecayOverrides()
        {
            if (_decayHuman.IsVanilla && _decayAlien.IsVanilla) return;

            try
            {
                // --- Phase 1: Custom DecayData per faction (prefab mutation) ---
                if (_decayHuman.HasCustomParams || _decayAlien.HasCustomParams)
                {
                    // Find all Decay components on structure prefabs
                    var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                    var decayType = typeof(Decay);
                    var dataField = decayType.GetField("Data", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (dataField == null)
                    {
                        MelonLogger.Warning("[DECAY] Decay.Data field not found via reflection");
                        return;
                    }

                    // Collect unique DecayData assets used by each faction
                    ScriptableObject vanillaDecayData = null;
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.Prefab == null) continue;
                        var decay = info.Prefab.GetComponent<Decay>();
                        if (decay == null) continue;
                        var dd = dataField.GetValue(decay) as ScriptableObject;
                        if (dd != null) { vanillaDecayData = dd; break; }
                    }

                    if (vanillaDecayData == null)
                    {
                        MelonLogger.Warning("[DECAY] No DecayData asset found on any structure prefab");
                        return;
                    }

                    var ddType = vanillaDecayData.GetType();
                    var tickField = ddType.GetField("Tick", BindingFlags.Public | BindingFlags.Instance);
                    var amountField = ddType.GetField("AmountPct", BindingFlags.Public | BindingFlags.Instance);
                    var randomizeField = ddType.GetField("RandomizePct", BindingFlags.Public | BindingFlags.Instance);
                    var delayField = ddType.GetField("Delay", BindingFlags.Public | BindingFlags.Instance);

                    // Read vanilla values
                    float vanillaTick = tickField != null ? (float)tickField.GetValue(vanillaDecayData) : 5f;
                    float vanillaAmount = amountField != null ? (float)amountField.GetValue(vanillaDecayData) : 0.01f;
                    float vanillaRandomize = randomizeField != null ? (float)randomizeField.GetValue(vanillaDecayData) : 0.2f;
                    float vanillaDelay = delayField != null ? (float)delayField.GetValue(vanillaDecayData) : 10f;

                    // Create cloned DecayData for each faction that has custom params
                    if (_decayHuman.HasCustomParams && _decayDataHuman == null)
                    {
                        _decayDataHuman = ScriptableObject.CreateInstance(ddType) as ScriptableObject;
                        _decayDataHuman.name = "DecayData_Human_Override";
                    }
                    if (_decayAlien.HasCustomParams && _decayDataAlien == null)
                    {
                        _decayDataAlien = ScriptableObject.CreateInstance(ddType) as ScriptableObject;
                        _decayDataAlien.name = "DecayData_Alien_Override";
                    }

                    // Populate cloned assets
                    foreach (var (settings, clone) in new[] { (_decayHuman, _decayDataHuman), (_decayAlien, _decayDataAlien) })
                    {
                        if (clone == null || !settings.HasCustomParams) continue;
                        if (tickField != null) tickField.SetValue(clone, settings.Tick >= 0 ? settings.Tick : vanillaTick);
                        if (amountField != null) amountField.SetValue(clone, settings.AmountPct >= 0 ? settings.AmountPct : vanillaAmount);
                        if (randomizeField != null) randomizeField.SetValue(clone, settings.RandomizePct >= 0 ? settings.RandomizePct : vanillaRandomize);
                        if (delayField != null) delayField.SetValue(clone, settings.Delay >= 0 ? settings.Delay : vanillaDelay);
                    }

                    // Swap Data field on each structure prefab's Decay component
                    int swapped = 0;
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.Prefab == null) continue;
                        var decay = info.Prefab.GetComponent<Decay>();
                        if (decay == null) continue;

                        // Determine faction: check for HumanStructure vs AlienStructure component
                        bool isHuman = info.Prefab.GetComponent<HumanStructure>() != null;
                        bool isAlien = info.Prefab.GetComponent<AlienStructure>() != null;

                        ScriptableObject targetDD = null;
                        if (isHuman && _decayHuman.HasCustomParams) targetDD = _decayDataHuman;
                        else if (isAlien && _decayAlien.HasCustomParams) targetDD = _decayDataAlien;

                        if (targetDD != null)
                        {
                            dataField.SetValue(decay, targetDD);
                            swapped++;
                        }
                        else if (!isHuman && !isAlien)
                        {
                            // Unknown structure type — leave vanilla
                        }
                    }

                    if (swapped > 0)
                        MelonLogger.Msg($"[DECAY] Swapped DecayData on {swapped} structure prefabs");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[DECAY] ApplyDecayOverrides error: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply per-faction decay enable/disable via Team.UnlinkedStructureStateEnabled.
        /// Must run AFTER teams are initialized (OnGameStartedLogic).
        /// </summary>
        private static void ApplyDecayTeamSettings()
        {
            if (_decayHuman.IsVanilla && _decayAlien.IsVanilla) return;

            try
            {
                var teamsProperty = typeof(Team).GetProperty("Teams", BindingFlags.Public | BindingFlags.Static);
                if (teamsProperty == null)
                {
                    MelonLogger.Warning("[DECAY] Team.Teams property not found");
                    return;
                }
                var teams = teamsProperty.GetValue(null) as System.Collections.IList;
                if (teams == null) return;

                var unlinkedProp = typeof(Team).GetProperty("UnlinkedStructureStateEnabled", BindingFlags.Public | BindingFlags.Instance);
                if (unlinkedProp == null)
                {
                    MelonLogger.Warning("[DECAY] Team.UnlinkedStructureStateEnabled property not found");
                    return;
                }

                foreach (var teamObj in teams)
                {
                    var team = teamObj as Team;
                    if (team == null) continue;

                    // Determine human vs alien: check if BaseStructure prefab has HumanStructure component
                    bool isHuman = false;
                    if (team.BaseStructure != null && team.BaseStructure.Prefab != null)
                        isHuman = team.BaseStructure.Prefab.GetComponent<HumanStructure>() != null;

                    DecaySettings settings = isHuman ? _decayHuman : _decayAlien;
                    if (!settings.Enabled || settings.KeepProduction)
                    {
                        // Set UnlinkedStructureStateEnabled=false → IsFunctional returns true on client
                        // → production works, no "missing logistics" on client UI.
                        // If keep_production + decay enabled: UpdateHealDecay Harmony postfix handles
                        // forcing decay on server based on actual unlinked state.
                        unlinkedProp.SetValue(team, false);
                        string reason = !settings.Enabled ? "decay disabled" : "keep_production";
                        LogDebug($"[DECAY] UnlinkedStructureStateEnabled=false for team {team.TeamShortName} (IsHuman={isHuman}, reason={reason})");
                    }
                    // If enabled and no keep_production, leave vanilla value (true) — don't force it,
                    // in case the server has DisableStructureDecay set globally.
                }

                if (!_decayHuman.Enabled || !_decayAlien.Enabled)
                    MelonLogger.Msg($"[DECAY] Team decay — Human: {(_decayHuman.Enabled ? "ON" : "OFF")}, Alien: {(_decayAlien.Enabled ? "ON" : "OFF")}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[DECAY] ApplyDecayTeamSettings error: {ex.Message}");
            }
        }
    }
}

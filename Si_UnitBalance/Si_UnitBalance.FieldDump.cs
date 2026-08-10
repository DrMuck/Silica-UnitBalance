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
        // Field discovery: dump ObjectInfo, Unit, Structure fields
        // to find cost/build time/etc. field names
        // =============================================

        private static void DumpFieldDiscovery()
        {
            LogDebug("========== Field Discovery ==========");
            try
            {
                // Dump ObjectInfo fields (first instance)
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                if (allInfos.Length > 0)
                {
                    DumpTypeFields("ObjectInfo", allInfos[0]);

                    // Find one unit and one structure to dump their fields
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.Prefab == null) continue;

                        var unit = info.Prefab.GetComponent<Unit>();
                        if (unit != null)
                        {
                            LogDebug($"--- Unit fields (from '{info.DisplayName}') ---");
                            DumpTypeFields("Unit", unit);

                            // Also dump DamageManager and DamageManagerData fields
                            var dm = info.Prefab.GetComponent<DamageManager>();
                            if (dm != null)
                            {
                                DumpTypeFields("DamageManager", dm);
                                // Dump the underlying DamageManagerData asset
                                var dataField = dm.GetType().GetField("Data",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (dataField != null)
                                {
                                    object dataObj = dataField.GetValue(dm);
                                    if (dataObj != null)
                                        DumpTypeFields("DamageManagerData", dataObj);
                                }
                            }
                            break;
                        }
                    }

                    // Dump ConstructionData fields (from first unit that has one)
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.ConstructionData == null) continue;
                        LogDebug($"--- ConstructionData fields (from '{info.DisplayName}') ---");
                        DumpTypeFields("ConstructionData", info.ConstructionData);
                        break;
                    }

                    foreach (var info in allInfos)
                    {
                        if (info == null || info.Prefab == null) continue;

                        var structure = info.Prefab.GetComponent<Structure>();
                        if (structure != null)
                        {
                            LogDebug($"--- Structure fields (from '{info.DisplayName}') ---");
                            DumpTypeFields("Structure", structure);
                            break;
                        }
                    }
                }

                // === Deep dump: ALL components on combat units to find weapon/range fields ===
                string[] dumpTargets = { "Shrimp", "Crab", "Behemoth", "Scorpion", "Defiler", "Colossus", "Dragonfly", "Goliath", "Hunter", "Firebug", "Railgun Tank", "Nest", "Interceptor", "Headquarters", "Combat Tank", "Heavy Tank", "Barrage Truck", "Siege Tank", "Harvester", "Bio Cache" };
                foreach (string targetName in dumpTargets)
                {
                    LogDebug($"--- {targetName.ToUpper()} DEEP COMPONENT DUMP ---");
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.DisplayName != targetName) continue;
                        if (info.Prefab == null) { LogDebug($"  {targetName} prefab is null"); break; }

                        // Root components
                        var rootComps = info.Prefab.GetComponents<Component>();
                        LogDebug($"  {targetName} has {rootComps.Length} root components:");
                        foreach (var comp in rootComps)
                        {
                            if (comp == null) continue;
                            string cName = comp.GetType().Name;
                            LogDebug($"  --- Root Component: {cName} ({comp.GetType().FullName}) ---");
                            DumpTypeFields(cName, comp);

                            // Recursively dump referenced data objects (e.g., CreatureAttack on CreatureDecapod)
                            if (cName == "CreatureDecapod")
                            {
                                var compType = comp.GetType();
                                foreach (var rf in compType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                                {
                                    if (rf.FieldType.Name == "CreatureAttack")
                                    {
                                        object attackObj = rf.GetValue(comp);
                                        if (attackObj != null)
                                        {
                                            LogDebug($"  --- Referenced Object: {rf.Name} (type {rf.FieldType.FullName}) ---");
                                            DumpTypeFields($"CreatureAttack:{rf.Name}", attackObj);
                                        }
                                        else
                                        {
                                            LogDebug($"  --- Referenced Object: {rf.Name} = NULL ---");
                                        }
                                    }
                                }
                            }

                            // Dump VehicleTurret projectile references
                            if (cName == "VehicleTurret")
                            {
                                var vtType = comp.GetType();
                                foreach (string projField in new[] { "PrimaryProjectile", "SecondaryProjectile" })
                                {
                                    try
                                    {
                                        var pf = vtType.GetField(projField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                        if (pf == null) { var pp = vtType.GetProperty(projField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (pp != null) { object pdObj = pp.GetValue(comp); if (pdObj != null) { LogDebug($"  --- VT {projField}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{projField}", pdObj); } } }
                                        else { object pdObj = pf.GetValue(comp); if (pdObj != null) { LogDebug($"  --- VT {projField}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{projField}", pdObj); } else { LogDebug($"  --- VT {projField}: NULL ---"); } }
                                    }
                                    catch (Exception ex) { LogDebug($"  --- VT {projField}: Error: {ex.Message} ---"); }
                                }
                                // Also dump all fields that reference ProjectileData
                                foreach (var rf in vtType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    if (rf.FieldType.Name == "ProjectileData" && rf.Name != "PrimaryProjectile" && rf.Name != "SecondaryProjectile")
                                    {
                                        try { object pdObj = rf.GetValue(comp); if (pdObj != null) { LogDebug($"  --- VT {rf.Name}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{rf.Name}", pdObj); } }
                                        catch { }
                                    }
                                }
                            }
                        }

                        // ALL child components (no filter — we need to find weapon/range types)
                        var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                        var seen = new HashSet<int>();
                        foreach (var comp in rootComps) { if (comp != null) seen.Add(comp.GetInstanceID()); }
                        LogDebug($"  --- Child components (total {childComps.Length}, skipping root-level) ---");
                        foreach (var comp in childComps)
                        {
                            if (comp == null || seen.Contains(comp.GetInstanceID())) continue;
                            seen.Add(comp.GetInstanceID());
                            string cName = comp.GetType().Name;
                            // Skip noise: Transform, MeshFilter, MeshRenderer, SkinnedMeshRenderer, Animator, LODGroup, ParticleSystem
                            string lower = cName.ToLower();
                            if (lower == "transform" || lower.Contains("mesh") || lower.Contains("renderer") ||
                                lower.Contains("animator") || lower.Contains("lodgroup") || lower.Contains("particle") ||
                                lower.Contains("collider") || lower.Contains("light") || lower.Contains("audio") ||
                                lower.Contains("canvas") || lower.Contains("cloth"))
                                continue;
                            string goName = comp.gameObject != null ? comp.gameObject.name : "?";
                            LogDebug($"  --- Child [{goName}] Component: {cName} ({comp.GetType().FullName}) ---");
                            DumpTypeFields(cName, comp);
                        }
                        break;
                    }
                    LogDebug($"--- END {targetName.ToUpper()} DUMP ---");
                }

                // Production tree dump: what each structure produces, with faction
                LogDebug("--- Production Trees (Structure -> Producible Items) ---");
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    var structure = info.Prefab.GetComponent<Structure>();
                    if (structure == null || structure.ConstructionOptions == null || structure.ConstructionOptions.Count == 0) continue;

                    string team = "?";
                    try { var dt = GetDefaultTeam(structure); if (dt != null) team = dt.name; } catch { }

                    string prodCd = "";
                    if (info.ConstructionData != null)
                        prodCd = $" Cost={info.ConstructionData.ResourceCost} Build={info.ConstructionData.BuildUpTime:F0}s MinTier={info.ConstructionData.MinimumTeamTier} MaxDist={info.ConstructionData.MaximumBaseStructureDistance:F0} UseRadius={info.ConstructionData.MaximumDistanceUseRadius}";

                    LogDebug($"  PRODUCER: {info.DisplayName} ({info.name}) [{team}]{prodCd}");
                    foreach (var cd in structure.ConstructionOptions)
                    {
                        if (cd == null) continue;
                        string prodName = "(unknown)";
                        string prodInfoName = "";
                        try
                        {
                            if (cd.ObjectInfo != null)
                            {
                                prodName = cd.ObjectInfo.DisplayName ?? cd.ObjectInfo.name;
                                prodInfoName = cd.ObjectInfo.name;
                            }
                        }
                        catch { }
                        LogDebug($"    -> {prodName,-25} Cost={cd.ResourceCost,5} Build={cd.BuildUpTime,4:F0}s MinTier={cd.MinimumTeamTier,2} TechTier={cd.TechnologyTier,2} MaxDist={cd.MaximumBaseStructureDistance:F0} UseRadius={cd.MaximumDistanceUseRadius} ({prodInfoName})");
                    }
                }

                // === ProjectileData asset dump: speed, lifetime, damage, gravity ===
                LogDebug("--- ProjectileData Asset Dump ---");
                var allProjectiles = Resources.FindObjectsOfTypeAll<ProjectileData>();
                LogDebug($"  Found {allProjectiles.Length} ProjectileData assets");
                foreach (var pd in allProjectiles)
                {
                    if (pd == null) continue;
                    LogDebug($"  === ProjectileData: {pd.name} ===");
                    DumpTypeFields("ProjectileData", pd);
                }
                LogDebug("--- END ProjectileData Dump ---");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"DumpFieldDiscovery error: {ex.Message}");
            }
            LogDebug("========== End Field Discovery ==========");
        }

        // Focused projectile dump: one clean CSV row per ProjectileData asset with the
        // fields that govern damage drop-off and AI aim. Written unconditionally (no
        // debug flag) to UserData/UnitBalance_cfg/ProjectileData_Dump.csv, from the
        // same load-time point as the unit JSON dump — i.e. BEFORE OverrideManager
        // applies, so the values are vanilla.
        //
        // Drop-off primer: a projectile only loses damage with range when
        // m_bImpactScaleBySpeed is TRUE (impact damage then scales with the shell's
        // current speed, which drag bleeds off over distance). If it is FALSE the shot
        // hits for full m_fImpactDamage at any range regardless of drag. So the
        // Siege Tank's plasma (no drop-off) should read ImpactScaleBySpeed=False and
        // the Crimson Tank's shell (drop-off) should read True.
        private static float RF(Type t, object o, string name, float def = 0f)
        {
            try { var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                  if (f != null) return Convert.ToSingle(f.GetValue(o)); } catch { }
            return def;
        }
        private static bool RB(Type t, object o, string name, bool def = false)
        {
            try { var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                  if (f != null) return (bool)f.GetValue(o); } catch { }
            return def;
        }
        private static string RS(Type t, object o, string name)
        {
            try { var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                  if (f != null) return f.GetValue(o)?.ToString() ?? ""; } catch { }
            return "";
        }

        internal static void DumpProjectileDataCsv()
        {
            try
            {
                string outPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_configPath), "ProjectileData_Dump.csv");

                var all = Resources.FindObjectsOfTypeAll<ProjectileData>();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("name,instant_hit,impact_scale_by_speed,impact_damage,impact_dmg_type," +
                    "base_speed,min_speed_pct,lifetime,drag_coeff,air_drag_mult,gravity_scale,mass,diameter," +
                    "penetrating,penetrating_damage,splash,splash_dmg_max,splash_radius_min,splash_radius_max,splash_radius_pow," +
                    "ai_lead_drag,ai_lead_velocity,ai_lead_gravity");

                int n = 0;
                foreach (var pd in all)
                {
                    if (pd == null) continue;
                    var t = pd.GetType();
                    string name = (pd.name ?? "").Replace(",", " ");
                    sb.Append(name).Append(',')
                      .Append(RB(t, pd, "m_InstantHit")).Append(',')
                      .Append(RB(t, pd, "m_bImpactScaleBySpeed")).Append(',')
                      .Append(RF(t, pd, "m_fImpactDamage").ToString("0.##")).Append(',')
                      .Append(RS(t, pd, "m_ImpactDamageType")).Append(',')
                      .Append(RF(t, pd, "m_fBaseSpeed").ToString("0.##")).Append(',')
                      .Append(RF(t, pd, "m_fMinSpeedPct").ToString("0.###")).Append(',')
                      .Append(RF(t, pd, "m_fLifeTime").ToString("0.##")).Append(',')
                      .Append(RF(t, pd, "m_fDragCoefficient").ToString("0.####")).Append(',')
                      .Append(RF(t, pd, "AirDragForceMultiplier").ToString("0.####")).Append(',')
                      .Append(RF(t, pd, "m_fGravityScale").ToString("0.###")).Append(',')
                      .Append(RF(t, pd, "m_fMass").ToString("0.###")).Append(',')
                      .Append(RF(t, pd, "m_fDiameter").ToString("0.###")).Append(',')
                      .Append(RB(t, pd, "m_bPenetrating")).Append(',')
                      .Append(RF(t, pd, "m_fPenetratingDamage").ToString("0.##")).Append(',')
                      .Append(RB(t, pd, "m_bSplash")).Append(',')
                      .Append(RF(t, pd, "m_fSplashDamageMax").ToString("0.##")).Append(',')
                      .Append(RF(t, pd, "m_fSplashDamageRadiusMin").ToString("0.##")).Append(',')
                      .Append(RF(t, pd, "m_fSplashDamageRadiusMax").ToString("0.##")).Append(',')
                      .Append(RF(t, pd, "m_fSplashDamageRadiusPow").ToString("0.###")).Append(',')
                      .Append(RF(t, pd, "m_AIAimLeadFactor_Drag").ToString("0.###")).Append(',')
                      .Append(RF(t, pd, "m_AIAimLeadFactor_Velocity").ToString("0.###")).Append(',')
                      .Append(RF(t, pd, "m_AIAimLeadFactor_Gravity").ToString("0.###"))
                      .AppendLine();
                    n++;
                }
                System.IO.File.WriteAllText(outPath, sb.ToString());
                MelonLogger.Msg($"[UnitBalance] Wrote {n} projectiles to {outPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"DumpProjectileDataCsv error: {ex.Message}");
            }
        }

        // Dump each unit's VehicleTurret.AimCompensation. This enum gates whether a
        // PLAYER-controlled turret gets ballistic aim compensation at all: None = no
        // player compensation (the ai_lead_* factors then only affect AI units);
        // Primary/Secondary_VerticalOnly = gravity drop only; Primary/Secondary_Full =
        // full drag+gravity+velocity. Written to UserData/UnitBalance_cfg/TurretAimComp_Dump.csv.
        internal static void DumpTurretAimCompCsv()
        {
            try
            {
                string outPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_configPath), "TurretAimComp_Dump.csv");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("unit,turret_go,aim_compensation,pitch_max_up,pitch_max_down,pitch_up_yawcurve,pitch_down_yawcurve,pri_projectile,sec_projectile");

                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                int n = 0;
                var seen = new HashSet<string>();
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    string disp = info.DisplayName ?? info.name ?? "";
                    if (string.IsNullOrEmpty(disp)) continue;

                    foreach (var comp in info.Prefab.GetComponentsInChildren<Component>(true))
                    {
                        if (comp == null || comp.GetType().Name != "VehicleTurret") continue;
                        var ct = comp.GetType();
                        var acf = ct.GetField("AimCompensation", BindingFlags.Public | BindingFlags.Instance);
                        if (acf == null) continue;
                        string ac = "?";
                        try { ac = acf.GetValue(comp)?.ToString() ?? "?"; } catch { }

                        float pmu = TurretFloat(ct, comp, "PitchMaxUp");
                        float pmd = TurretFloat(ct, comp, "PitchMaxDown");
                        bool puc = TurretBool(ct, comp, "PitchUpClampByYawCurve");
                        bool pdc = TurretBool(ct, comp, "PitchDownClampByYawCurve");
                        string pri = ProjName(ct, comp, "PrimaryProjectileData");
                        string sec = ProjName(ct, comp, "SecondaryProjectileData");
                        string goName = comp.gameObject != null ? comp.gameObject.name : "?";
                        string keyd = disp + "|" + goName + "|" + ac;
                        if (!seen.Add(keyd)) continue;

                        sb.Append(disp.Replace(",", " ")).Append(',')
                          .Append(goName.Replace(",", " ")).Append(',')
                          .Append(ac).Append(',')
                          .Append(pmu.ToString("0.#")).Append(',')
                          .Append(pmd.ToString("0.#")).Append(',')
                          .Append(puc).Append(',')
                          .Append(pdc).Append(',')
                          .Append(pri).Append(',')
                          .Append(sec).AppendLine();
                        n++;
                    }
                }
                System.IO.File.WriteAllText(outPath, sb.ToString());
                MelonLogger.Msg($"[UnitBalance] Wrote {n} turret aim-compensation rows to {outPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"DumpTurretAimCompCsv error: {ex.Message}");
            }
        }

        private static float TurretFloat(Type ct, object comp, string field)
        {
            try { var f = ct.GetField(field, BindingFlags.Public | BindingFlags.Instance); if (f != null) return (float)f.GetValue(comp); } catch { }
            return -1f;
        }
        private static bool TurretBool(Type ct, object comp, string field)
        {
            try { var f = ct.GetField(field, BindingFlags.Public | BindingFlags.Instance); if (f != null) return (bool)f.GetValue(comp); } catch { }
            return false;
        }

        private static string ProjName(Type ct, object comp, string field)
        {
            try
            {
                var f = ct.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return "";
                var pd = f.GetValue(comp);
                if (pd == null) return "";
                return (((UnityEngine.Object)pd).name ?? "").Replace(",", " ");
            }
            catch { return ""; }
        }

        private static void DumpTypeFields(string typeName, object obj)
        {
            try
            {
                var type = obj.GetType();
                LogDebug($"  [{typeName}] Type: {type.FullName}");

                // Public fields
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    try
                    {
                        object val = f.GetValue(obj);
                        string valStr = val?.ToString() ?? "null";
                        if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                        LogDebug($"  [{typeName}] field {f.FieldType.Name,-16} {f.Name} = {valStr}");
                    }
                    catch
                    {
                        LogDebug($"  [{typeName}] field {f.FieldType.Name,-16} {f.Name} = (error reading)");
                    }
                }

                // Public properties
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var p in props)
                {
                    if (!p.CanRead) continue;
                    try
                    {
                        object val = p.GetValue(obj);
                        string valStr = val?.ToString() ?? "null";
                        if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                        LogDebug($"  [{typeName}] prop  {p.PropertyType.Name,-16} {p.Name} = {valStr}");
                    }
                    catch
                    {
                        LogDebug($"  [{typeName}] prop  {p.PropertyType.Name,-16} {p.Name} = (error reading)");
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"  [{typeName}] Error dumping: {ex.Message}");
            }
        }
    }
}

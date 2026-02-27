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
            MelonLogger.Msg("========== Field Discovery ==========");
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
                            MelonLogger.Msg($"--- Unit fields (from '{info.DisplayName}') ---");
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
                        MelonLogger.Msg($"--- ConstructionData fields (from '{info.DisplayName}') ---");
                        DumpTypeFields("ConstructionData", info.ConstructionData);
                        break;
                    }

                    foreach (var info in allInfos)
                    {
                        if (info == null || info.Prefab == null) continue;

                        var structure = info.Prefab.GetComponent<Structure>();
                        if (structure != null)
                        {
                            MelonLogger.Msg($"--- Structure fields (from '{info.DisplayName}') ---");
                            DumpTypeFields("Structure", structure);
                            break;
                        }
                    }
                }

                // === Deep dump: ALL components on combat units to find weapon/range fields ===
                string[] dumpTargets = { "Shrimp", "Crab", "Behemoth", "Scorpion", "Defiler", "Colossus", "Dragonfly", "Goliath", "Hunter", "Firebug", "Railgun Tank", "Nest", "Interceptor", "Headquarters", "Combat Tank", "Heavy Tank", "Barrage Truck" };
                foreach (string targetName in dumpTargets)
                {
                    MelonLogger.Msg($"--- {targetName.ToUpper()} DEEP COMPONENT DUMP ---");
                    foreach (var info in allInfos)
                    {
                        if (info == null || info.DisplayName != targetName) continue;
                        if (info.Prefab == null) { MelonLogger.Msg($"  {targetName} prefab is null"); break; }

                        // Root components
                        var rootComps = info.Prefab.GetComponents<Component>();
                        MelonLogger.Msg($"  {targetName} has {rootComps.Length} root components:");
                        foreach (var comp in rootComps)
                        {
                            if (comp == null) continue;
                            string cName = comp.GetType().Name;
                            MelonLogger.Msg($"  --- Root Component: {cName} ({comp.GetType().FullName}) ---");
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
                                            MelonLogger.Msg($"  --- Referenced Object: {rf.Name} (type {rf.FieldType.FullName}) ---");
                                            DumpTypeFields($"CreatureAttack:{rf.Name}", attackObj);
                                        }
                                        else
                                        {
                                            MelonLogger.Msg($"  --- Referenced Object: {rf.Name} = NULL ---");
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
                                        if (pf == null) { var pp = vtType.GetProperty(projField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (pp != null) { object pdObj = pp.GetValue(comp); if (pdObj != null) { MelonLogger.Msg($"  --- VT {projField}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{projField}", pdObj); } } }
                                        else { object pdObj = pf.GetValue(comp); if (pdObj != null) { MelonLogger.Msg($"  --- VT {projField}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{projField}", pdObj); } else { MelonLogger.Msg($"  --- VT {projField}: NULL ---"); } }
                                    }
                                    catch (Exception ex) { MelonLogger.Msg($"  --- VT {projField}: Error: {ex.Message} ---"); }
                                }
                                // Also dump all fields that reference ProjectileData
                                foreach (var rf in vtType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    if (rf.FieldType.Name == "ProjectileData" && rf.Name != "PrimaryProjectile" && rf.Name != "SecondaryProjectile")
                                    {
                                        try { object pdObj = rf.GetValue(comp); if (pdObj != null) { MelonLogger.Msg($"  --- VT {rf.Name}: {((UnityEngine.Object)pdObj).name} ---"); DumpTypeFields($"VT_ProjectileData:{rf.Name}", pdObj); } }
                                        catch { }
                                    }
                                }
                            }
                        }

                        // ALL child components (no filter — we need to find weapon/range types)
                        var childComps = info.Prefab.GetComponentsInChildren<Component>(true);
                        var seen = new HashSet<int>();
                        foreach (var comp in rootComps) { if (comp != null) seen.Add(comp.GetInstanceID()); }
                        MelonLogger.Msg($"  --- Child components (total {childComps.Length}, skipping root-level) ---");
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
                            MelonLogger.Msg($"  --- Child [{goName}] Component: {cName} ({comp.GetType().FullName}) ---");
                            DumpTypeFields(cName, comp);
                        }
                        break;
                    }
                    MelonLogger.Msg($"--- END {targetName.ToUpper()} DUMP ---");
                }

                // Production tree dump: what each structure produces, with faction
                MelonLogger.Msg("--- Production Trees (Structure -> Producible Items) ---");
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    var structure = info.Prefab.GetComponent<Structure>();
                    if (structure == null || structure.ConstructionOptions == null || structure.ConstructionOptions.Count == 0) continue;

                    string team = "?";
                    try { if (structure.DefaultTeam != null) team = structure.DefaultTeam.name; } catch { }

                    string prodCd = "";
                    if (info.ConstructionData != null)
                        prodCd = $" Cost={info.ConstructionData.ResourceCost} Build={info.ConstructionData.BuildUpTime:F0}s MinTier={info.ConstructionData.MinimumTeamTier} MaxDist={info.ConstructionData.MaximumBaseStructureDistance:F0} UseRadius={info.ConstructionData.MaximumDistanceUseRadius}";

                    MelonLogger.Msg($"  PRODUCER: {info.DisplayName} ({info.name}) [{team}]{prodCd}");
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
                        MelonLogger.Msg($"    -> {prodName,-25} Cost={cd.ResourceCost,5} Build={cd.BuildUpTime,4:F0}s MinTier={cd.MinimumTeamTier,2} TechTier={cd.TechnologyTier,2} MaxDist={cd.MaximumBaseStructureDistance:F0} UseRadius={cd.MaximumDistanceUseRadius} ({prodInfoName})");
                    }
                }

                // === ProjectileData asset dump: speed, lifetime, damage, gravity ===
                MelonLogger.Msg("--- ProjectileData Asset Dump ---");
                var allProjectiles = Resources.FindObjectsOfTypeAll<ProjectileData>();
                MelonLogger.Msg($"  Found {allProjectiles.Length} ProjectileData assets");
                foreach (var pd in allProjectiles)
                {
                    if (pd == null) continue;
                    MelonLogger.Msg($"  === ProjectileData: {pd.name} ===");
                    DumpTypeFields("ProjectileData", pd);
                }
                MelonLogger.Msg("--- END ProjectileData Dump ---");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"DumpFieldDiscovery error: {ex.Message}");
            }
            MelonLogger.Msg("========== End Field Discovery ==========");
        }

        private static void DumpTypeFields(string typeName, object obj)
        {
            try
            {
                var type = obj.GetType();
                MelonLogger.Msg($"  [{typeName}] Type: {type.FullName}");

                // Public fields
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    try
                    {
                        object val = f.GetValue(obj);
                        string valStr = val?.ToString() ?? "null";
                        if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                        MelonLogger.Msg($"  [{typeName}] field {f.FieldType.Name,-16} {f.Name} = {valStr}");
                    }
                    catch
                    {
                        MelonLogger.Msg($"  [{typeName}] field {f.FieldType.Name,-16} {f.Name} = (error reading)");
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
                        MelonLogger.Msg($"  [{typeName}] prop  {p.PropertyType.Name,-16} {p.Name} = {valStr}");
                    }
                    catch
                    {
                        MelonLogger.Msg($"  [{typeName}] prop  {p.PropertyType.Name,-16} {p.Name} = (error reading)");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"  [{typeName}] Error dumping: {ex.Message}");
            }
        }
    }
}

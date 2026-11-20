using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Si_UnitBalance
{
    // =============================================
    // Gatherer params (7.19.0): harvest_rate_mult, cargo_capacity_mult, deposit_rate_mult
    //
    // The game fields (SilicaCore, identical in 0.9.47 and 1.0.17):
    //   MiningClaws.ExtractAmount    int  Shrimp: resource per extraction bite (CreatureAttack hit)
    //   MiningClaws.DepositAmount    int  Shrimp: resource unloaded per MiningClaws.Tick (1 s)
    //   MiningArmsBase.ExtractAmount int  Sol/Cent Harvester (MiningArmsLaser / MiningArmsRoller): per ExtractionTick,
    //                                     scaled by the arm spin-up 0..1
    //   MiningArmsBase.DepositAmount int  Sol/Cent Harvester: resource unloaded per ExtractionTick
    //   ResourceHolder.MaxAmount     int  cargo capacity (the same component also holds a refinery's / silo's storage;
    //                                     only the ResourceHolder next to a MiningClaws/MiningArmsBase is touched here)
    // One field per unit, no per-resource split: a gatherer has exactly one ResourceHolder with one ResourceType
    // (Shrimp: Biotics, harvesters: Balterium).
    //
    // Extraction and deposit ticks run on the server only (MiningArmsBase: !Game.GetIsClient(); MiningClaws deposit:
    // returns on the client before RetrieveResource; MiningClaws extraction: the client only predicts, the server's
    // ResourceHolder RPC overwrites AmountStored). So a server-side value is authoritative for AI and player-piloted
    // gatherers alike. Clients read the fields only for the HUD (Crosshair cargo bar = AmountStored / MaxAmount) and
    // the Shrimp's local bite prediction.
    //
    // Mechanism per member:
    //   - OverrideManager (synced to vanilla clients) when OM resolves "A:<ObjectInfo>.asset".<member> to exactly
    //     this component. OM looks the member up DeclaredOnly on the prefab's MonoBehaviours, so it reaches
    //     ResourceHolder.MaxAmount and MiningClaws.ExtractAmount/DepositAmount, but NOT the harvesters'
    //     ExtractAmount/DepositAmount (declared on MiningArmsBase, the component is a MiningArmsLaser/Roller).
    //   - otherwise a tracked direct prefab write (WriteIntTracked -> RestoreDirectWrites puts vanilla back before
    //     every re-apply). Server-only, which is enough for those two members (clients never read them).
    // Values are always computed from a per-process vanilla snapshot (first value seen at an apply, right after
    // OMRevertAll + RestoreDirectWrites), never from the current prefab value, so nothing can compound; live
    // instances get absolute values from the same snapshot.
    // =============================================
    public partial class UnitBalance
    {
        internal const string GatherHarvestKey = "harvest_rate_mult";
        internal const string GatherCapacityKey = "cargo_capacity_mult";
        internal const string GatherDepositKey = "deposit_rate_mult";
        internal static readonly string[] _gathererParamKeys = { GatherHarvestKey, GatherCapacityKey, GatherDepositKey };

        private const string GatherExtractMember = "ExtractAmount";
        private const string GatherDepositMember = "DepositAmount";
        private const string GatherCapacityMember = "MaxAmount";
        private const int GatherValueMax = 1000000; // ResourceHolder.MaxAmount's [Range] upper end; keeps float tracking exact

        // "<ObjectInfo name>|<member>" -> vanilla int, first value seen at an apply (prefabs live for the process).
        private static readonly Dictionary<string, int> _gatherVanilla = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Unit names whose live instances this mod wrote last time (so a removed param resets them to vanilla).
        private static readonly HashSet<string> _gatherLiveTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _gatherWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Config parse helper: stores a gatherer multiplier unless it is 1 (vanilla) or not positive.</summary>
        private static void AddGathererMult(Dictionary<string, float> dict, string unitName, string key, float mult)
        {
            if (Math.Abs(mult - 1.0f) <= 0.001f) return;
            if (!(mult > 0f) || float.IsInfinity(mult) || float.IsNaN(mult))
            {
                MelonLogger.Warning($"[GATHER] {unitName}: {key} = {mult} ignored (must be > 0)");
                return;
            }
            dict[unitName] = mult;
        }

        private static bool HasAnyGathererParam(string name) =>
            _harvestRateMultipliers.ContainsKey(name) || _cargoCapacityMultipliers.ContainsKey(name) || _depositRateMultipliers.ContainsKey(name);

        private static float GathererMult(string name, string member)
        {
            var dict = member == GatherExtractMember ? _harvestRateMultipliers
                     : member == GatherDepositMember ? _depositRateMultipliers
                     : _cargoCapacityMultipliers;
            return dict.TryGetValue(name, out float m) ? m : 1f;
        }

        private static void GatherWarnOnce(string key, string msg)
        {
            if (_gatherWarned.Add(key)) MelonLogger.Warning(msg);
        }

        /// <summary>The gatherer components of a unit (prefab or live): its MiningClaws or MiningArmsBase (incl.
        /// MiningArmsLaser/Roller) and the ResourceHolder on the same GameObject, as the game pairs them
        /// (MiningClaws/MiningArmsBase.OnEnable: GetComponent&lt;ResourceHolder&gt;()).</summary>
        private static bool TryGetGatherer(GameObject go, out Component? miner, out ResourceHolder? holder)
        {
            miner = null; holder = null;
            if (go == null) return false;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (mb is MiningClaws || mb is MiningArmsBase) { miner = mb; break; }
            }
            if (miner == null) return false;
            holder = miner.gameObject.GetComponent<ResourceHolder>();
            return true;
        }

        /// <summary>The int member of that name (field first, then a writable property), or null.</summary>
        private static MemberInfo? ResolveIntMember(object obj, string name)
        {
            var t = obj.GetType();
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.FieldType == typeof(int) ? f : null;
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(int)) return p;
            return null;
        }

        private static int ReadIntMember(MemberInfo m, object obj) =>
            m is FieldInfo f ? (int)f.GetValue(obj) : (int)((PropertyInfo)m).GetValue(obj);

        private static void WriteIntMember(MemberInfo m, object obj, int v)
        {
            if (m is FieldInfo f) f.SetValue(obj, v);
            else ((PropertyInfo)m).SetValue(obj, v);
        }

        private static int ScaleGatherValue(int vanilla, float mult)
        {
            double v = Math.Round(vanilla * (double)mult);
            if (v < 1) v = 1;
            if (v > GatherValueMax) v = GatherValueMax;
            return (int)v;
        }

        /// <summary>The (component, member) a gatherer member name lives on, or null when the unit lacks it.</summary>
        private static Component? GathererComponentFor(string member, Component? miner, ResourceHolder? holder) =>
            member == GatherCapacityMember ? holder : miner;

        /// <summary>True when OverrideManager.Set("A:info.asset", member) would bind exactly this component's member:
        /// mirrors OverrideManager.GetOrCreateMemberOverride (asset type first, then the first MonoBehaviour under
        /// ObjectInfo.Prefab that DECLARES the member; DeclaredOnly, so inherited members are not found).</summary>
        private static bool OMReachesPrefabMember(ObjectInfo info, string member, Component comp)
        {
            try
            {
                const BindingFlags decl = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
                if (info.GetType().GetMember(member, decl).Length > 0) return false;
                if (info.Prefab == null) return false;
                foreach (var mb in info.Prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var found = mb.GetType().GetMember(member, decl);
                    if (found.Length == 0) continue;
                    if (!ReferenceEquals(mb, comp)) return false;
                    var mi = found[0];
                    if (mi is FieldInfo fi) return fi.FieldType == typeof(int) && !fi.IsInitOnly && !fi.IsLiteral && fi.GetCustomAttribute<NonSerializedAttribute>() == null;
                    if (mi is PropertyInfo pi) return pi.PropertyType == typeof(int) && pi.CanWrite && pi.SetMethod != null && pi.SetMethod.IsPublic;
                    return false;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Vanilla value of a gatherer member of this ObjectInfo's prefab: the per-process snapshot, or
        /// (capture == true, only from the apply step, which runs right after the revert) the current prefab value,
        /// which is then recorded. Null when the unit has no such member.</summary>
        private static int? GathererVanilla(ObjectInfo info, string member, bool capture)
        {
            if (info == null || info.Prefab == null) return null;
            string key = info.name + "|" + member;
            if (!TryGetGatherer(info.Prefab, out var miner, out var holder)) return null;
            var comp = GathererComponentFor(member, miner, holder);
            if (comp == null) return null;
            var m = ResolveIntMember(comp, member);
            if (m == null) return null;
            int cur;
            try { cur = ReadIntMember(m, comp); } catch { return null; }
            if (_gatherVanilla.TryGetValue(key, out int snap))
            {
                if (capture && cur != snap)
                    GatherWarnOnce("drift|" + key, $"[GATHER] {info.name}.{comp.GetType().Name}.{member} is {cur} after the revert, vanilla snapshot {snap} — using the snapshot (another mod writes this field?)");
                return snap;
            }
            if (!capture) return cur;
            _gatherVanilla[key] = cur;
            return cur;
        }

        // =============================================
        // Apply step (ApplyOverrideSteps)
        // =============================================

        private static void ApplyGathererOverrides(bool useOM)
        {
            if (_harvestRateMultipliers.Count == 0 && _cargoCapacityMultipliers.Count == 0 && _depositRateMultipliers.Count == 0) return;

            int units = 0, viaOM = 0, direct = 0;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in AllObjectInfos())
            {
                if (info == null || info.Prefab == null) continue;
                string name = ResolveConfigName(info.DisplayName, info.name);
                if (string.IsNullOrEmpty(name) || !HasAnyGathererParam(name)) continue;
                seenNames.Add(name);
                if (!TryGetGatherer(info.Prefab, out var miner, out var holder))
                {
                    GatherWarnOnce("nocomp|" + info.name, $"[GATHER] {name} ({info.name}): gatherer params set, but the prefab has no MiningClaws/MiningArmsBase — ignored");
                    continue;
                }
                string oiTarget = $"A:{info.name}.asset";
                bool any = false;
                foreach (string member in new[] { GatherExtractMember, GatherCapacityMember, GatherDepositMember })
                {
                    float mult = GathererMult(name, member);
                    if (Math.Abs(mult - 1f) <= 0.001f) continue;
                    var comp = GathererComponentFor(member, miner, holder);
                    if (comp == null)
                    {
                        GatherWarnOnce("nohold|" + info.name, $"[GATHER] {name} ({info.name}): no ResourceHolder next to {miner!.GetType().Name} — cargo_capacity_mult ignored");
                        continue;
                    }
                    var m = ResolveIntMember(comp, member);
                    if (m == null)
                    {
                        GatherWarnOnce("nomem|" + info.name + member, $"[GATHER] {name}: {comp.GetType().Name}.{member} (int) not found on this game build — ignored");
                        continue;
                    }
                    int? vanilla = GathererVanilla(info, member, capture: true);
                    if (!vanilla.HasValue) continue;
                    int target = ScaleGatherValue(vanilla.Value, mult);
                    string label = $"{name}.{comp.GetType().Name}.{member}";
                    try
                    {
                        bool om = useOM && OMReachesPrefabMember(info, member, comp) && OMSetInt(oiTarget, member, target);
                        if (om) viaOM++;
                        else { WriteIntTracked(comp, m, target, label, comp); direct++; }
                        LogDebug($"[GATHER] {label}: {vanilla.Value} -> {target} (x{mult:0.###}, {(om ? "OM" : "direct")})");
                        any = true;
                    }
                    catch (Exception ex) { MelonLogger.Warning($"[GATHER] {label}: write failed: {ex.Message}"); }
                }
                if (any) units++;
            }

            foreach (var dict in new[] { _harvestRateMultipliers, _cargoCapacityMultipliers, _depositRateMultipliers })
                foreach (var n in dict.Keys)
                    if (!seenNames.Contains(n))
                        GatherWarnOnce("noinfo|" + n, $"[GATHER] {n}: gatherer params set, but no ObjectInfo has that name — ignored");

            if (units > 0)
                MelonLogger.Msg($"[GATHER] Applied gatherer overrides to {units} unit prefab(s): {viaOM} via OverrideManager (synced), {direct} direct (server-only, tracked)");
        }

        // =============================================
        // Live instances (called from PropagateToLiveInstances)
        // =============================================

        /// <summary>Writes the gatherer values onto already-spawned gatherers: absolute values from the vanilla
        /// snapshot x the current multiplier (vanilla where the multiplier was removed), never prefab x mult, so a
        /// second propagation cannot square anything. Server-only: the fields are not networked; a client keeps the
        /// MaxAmount its own copy of that unit spawned with (only its HUD bar reads it).</summary>
        private static void PropagateGatherersLive(Unit[] liveUnits)
        {
            var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in _harvestRateMultipliers.Keys) want.Add(n);
            foreach (var n in _cargoCapacityMultipliers.Keys) want.Add(n);
            foreach (var n in _depositRateMultipliers.Keys) want.Add(n);
            if (want.Count == 0 && _gatherLiveTouched.Count == 0) return;

            int units = 0, fields = 0, clamped = 0;
            try
            {
                foreach (var unit in liveUnits)
                {
                    if (unit == null || unit.ObjectInfo == null) continue;
                    var info = unit.ObjectInfo;
                    string name = ResolveConfigName(info.DisplayName, info.name);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!want.Contains(name) && !_gatherLiveTouched.Contains(name)) continue;
                    if (!TryGetGatherer(unit.gameObject, out var miner, out var holder)) continue;

                    int set = 0;
                    bool capacityChanged = false;
                    foreach (string member in new[] { GatherExtractMember, GatherCapacityMember, GatherDepositMember })
                    {
                        // No snapshot = this mod never wrote that member's prefab in this process: the live value is vanilla.
                        if (!_gatherVanilla.TryGetValue(info.name + "|" + member, out int vanilla)) continue;
                        var comp = GathererComponentFor(member, miner, holder);
                        if (comp == null) continue;
                        var m = ResolveIntMember(comp, member);
                        if (m == null) continue;
                        int target = ScaleGatherValue(vanilla, GathererMult(name, member));
                        try
                        {
                            if (ReadIntMember(m, comp) == target) continue;
                            WriteIntMember(m, comp, target);
                            set++;
                            if (member == GatherCapacityMember) capacityChanged = true;
                        }
                        catch (Exception ex) { LogDebug($"[GATHER-LIVE] {name}.{member}: {ex.Message}"); }
                    }
                    // A lowered capacity below the cargo on board would leave RemainingSpace < 0; the AI waits for
                    // RemainingSpace == 0 before it heads home, so clamp the cargo (SetResource clamps and RPCs it).
                    if (capacityChanged && holder != null && holder.AmountStored > holder.MaxAmount)
                    {
                        try { holder.SetResource(holder.MaxAmount); clamped++; }
                        catch (Exception ex) { LogDebug($"[GATHER-LIVE] {name}: cargo clamp failed: {ex.Message}"); }
                    }
                    if (set > 0) { units++; fields += set; }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[GATHER-LIVE] propagation failed: {ex.Message}"); }

            _gatherLiveTouched.Clear();
            foreach (var n in want) _gatherLiveTouched.Add(n);
            if (units > 0 || clamped > 0)
                MelonLogger.Msg($"[GATHER-LIVE] {fields} field(s) on {units} live gatherer(s) set from the vanilla snapshot" + (clamped > 0 ? $", cargo clamped on {clamped}" : ""));
        }

        // =============================================
        // Menu / ModUI base values, dump
        // =============================================

        /// <summary>Base value text for the menu, /stats and the ModUI: the vanilla int (snapshot when this mod
        /// already changed the prefab, else the prefab, which is vanilla then). Null when the unit has no such member.</summary>
        private static string? GathererBaseText(ObjectInfo info, string paramKey)
        {
            string member = paramKey == GatherHarvestKey ? GatherExtractMember
                          : paramKey == GatherDepositKey ? GatherDepositMember
                          : paramKey == GatherCapacityKey ? GatherCapacityMember : "";
            if (member.Length == 0) return null;
            int? v = GathererVanilla(info, member, capture: false);
            return v.HasValue ? v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        /// <summary>Gatherer fields for Si_UnitBalance_Dump.json (written in vanilla state), as a JSON fragment
        /// ending in a comma, or "" for a unit without MiningClaws/MiningArmsBase.</summary>
        private static string GathererDumpJson(ObjectInfo info)
        {
            try
            {
                if (info == null || info.Prefab == null) return "";
                if (!TryGetGatherer(info.Prefab, out var miner, out var holder) || miner == null) return "";
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string kind = miner is MiningClaws ? "claws" : "arms";
                int? ext = GathererVanilla(info, GatherExtractMember, capture: false);
                int? dep = GathererVanilla(info, GatherDepositMember, capture: false);
                int? cap = GathererVanilla(info, GatherCapacityMember, capture: false);
                float tick = 0f;
                var tickF = miner.GetType().GetField(miner is MiningClaws ? "Tick" : "ExtractionTick", BindingFlags.Public | BindingFlags.Instance);
                if (tickF != null && tickF.FieldType == typeof(float)) tick = (float)tickF.GetValue(miner);
                string res = "";
                try { if (holder != null && holder.ResourceType != null) res = holder.ResourceType.name; } catch { }
                var sb = new System.Text.StringBuilder();
                sb.Append($"\"gather_component\":\"{miner.GetType().Name}\",");
                sb.Append($"\"gather_kind\":\"{kind}\",");
                if (res.Length > 0) sb.Append($"\"gather_resource\":\"{EscJson(res)}\",");
                if (cap.HasValue) sb.Append($"\"gather_capacity\":{cap.Value.ToString(inv)},");
                if (ext.HasValue) sb.Append($"\"gather_extract_amount\":{ext.Value.ToString(inv)},");
                if (dep.HasValue) sb.Append($"\"gather_deposit_amount\":{dep.Value.ToString(inv)},");
                sb.Append($"\"gather_tick\":{tick.ToString("0.###", inv)},");
                return sb.ToString();
            }
            catch { return ""; }
        }
    }
}

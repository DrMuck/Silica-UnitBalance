using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // [OMVERIFY] — read-back of the override state, LOGGING ONLY
        //
        // Groundwork for skipping the per-round revert + re-apply one day: before every re-apply,
        // prove from the live server that what the last apply left behind is still in place.
        //
        //  * After every completed apply, once OverrideManager's queue has drained, a snapshot records
        //    every Enabled OverrideManager member (target, member, the REAL field/property value read
        //    through MemberInfo — never MemberOverride.GetValue(), which overwrites _currentValue), a set
        //    of direct-write sentinels (values this mod writes onto prefabs outside OverrideManager) and
        //    a few anchor objects (instance ids) that would change if the assets were reloaded.
        //  * Before every re-apply ("pre-apply", always on) and, with "debug_om_probe": true in the
        //    config, at every lifecycle hook, the current values are compared with the snapshot and one
        //    summary line is logged (details for the first mismatches).
        //
        // The check only iterates the snapshot and the OM table (no FindObjectsOfTypeAll), never
        // writes anything and never throws into the game.
        //
        // Player Cap Exempt (DynCap) legitimately zeroes UnitCapValue while a team's window is open.
        // Those members are skipped (counted as dyncap=N) while their team is OPEN; once it closes the
        // value is back to the snapshot value and is compared again. Members DynCap creates itself are
        // never in the snapshot unless they existed when it was taken.
        // =============================================

        private static bool _debugOmProbe; // config "debug_om_probe" (JSON only, default false)

        private sealed class OMSnapEntry
        {
            public string Target = "";
            public string Name = "";
            public OverrideManager.MemberOverride Mo = null!;
            public MemberInfo Member = null!;
            public object? Obj;
            public object? Expected;
        }

        private sealed class OMSentinel
        {
            public string Label = "";
            public object Target = null!;
            public MemberInfo Member = null!;
            public UnityEngine.Object? Owner;
            public object? Expected;
            public float? Vanilla; // recorded pre-mod value (tracked direct writes only)
        }

        private sealed class OMAnchor
        {
            public string Label = "";
            public UnityEngine.Object Obj = null!;
            public int Id;
            public string? TeamName; // set for a base structure: that team's BaseStructure must still be this object
        }

        private static readonly List<OMSnapEntry> _omSnap = new List<OMSnapEntry>();
        private static List<OMSentinel> _omSentinels = new List<OMSentinel>();
        private static List<OMAnchor> _omAnchors = new List<OMAnchor>();
        private static List<OMSentinel>? _omPendingSentinels;
        private static List<OMAnchor>? _omPendingAnchors;
        private static string? _omPendingSrc;   // an apply finished, its member snapshot is not taken yet
        private static int _omPendingGen;
        private static bool _omSnapValid;
        private static string _omSnapSrc = "";
        private static string _omNoSnapReason = "no completed apply yet";
        private static float _omSnapTime;
        private static int _omBlockedTotal, _omBlockedSinceSnap; // game RevertAll calls blocked by Patch_OMRevertAll

        private static bool _omQueueReflected;
        private static FieldInfo? _omApplyQueueField, _omRevertQueueField;
        private static PropertyInfo? _omApplyQueueEmpty, _omRevertQueueEmpty;

        private const int OMVerifyMaxDetail = 10;

        // ---------- helpers ----------

        /// <summary>True when OverrideManager has nothing queued, null when that cannot be read.</summary>
        private static bool? OMQueuesEmpty()
        {
            try
            {
                if (!_omQueueReflected)
                {
                    _omQueueReflected = true;
                    var t = typeof(OverrideManager);
                    _omApplyQueueField = t.GetField("_applyQueue", BindingFlags.NonPublic | BindingFlags.Static);
                    _omRevertQueueField = t.GetField("_revertQueue", BindingFlags.NonPublic | BindingFlags.Static);
                    _omApplyQueueEmpty = _omApplyQueueField?.FieldType.GetProperty("IsEmpty");
                    _omRevertQueueEmpty = _omRevertQueueField?.FieldType.GetProperty("IsEmpty");
                }
                if (_omApplyQueueField == null || _omApplyQueueEmpty == null) return null;
                bool applyEmpty = (bool)_omApplyQueueEmpty.GetValue(_omApplyQueueField.GetValue(null));
                bool revertEmpty = _omRevertQueueField == null || _omRevertQueueEmpty == null
                    || (bool)_omRevertQueueEmpty.GetValue(_omRevertQueueField.GetValue(null));
                return applyEmpty && revertEmpty;
            }
            catch { return null; }
        }

        /// <summary>The real value of a field/property, read without side effects on OverrideManager.</summary>
        private static object? OMReadMember(MemberInfo m, object? target)
        {
            if (m is FieldInfo f) return f.GetValue(f.IsStatic ? null : target);
            if (m is PropertyInfo p) return p.GetValue(target);
            return null;
        }

        private static bool OMValuesEqual(object? real, object? expected)
        {
            if (real is float a && expected is float b)
                return a == b || (float.IsNaN(a) && float.IsNaN(b)) || Math.Abs(a - b) <= 1e-4f * Math.Max(1f, Math.Abs(b));
            if (ReferenceEquals(real, null) || ReferenceEquals(expected, null)) return ReferenceEquals(real, expected);
            return real.Equals(expected);
        }

        private static string OMFmt(object? v)
        {
            try
            {
                if (ReferenceEquals(v, null)) return "null";
                if (v is float f) return f.ToString("0.####", CultureInfo.InvariantCulture);
                if (v is UnityEngine.Object uo) return uo == null ? "destroyed" : uo.name;
                if (v is IFormattable fm) return fm.ToString(null, CultureInfo.InvariantCulture);
                return v.ToString() ?? "";
            }
            catch { return "?"; }
        }

        private static string OMShortStack()
        {
            try
            {
                // skip this method and its caller (the logging helper)
                var st = new StackTrace(2, false);
                var sb = new StringBuilder();
                int shown = 0;
                for (int i = 0; i < st.FrameCount && shown < 8; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null) continue;
                    if (sb.Length > 0) sb.Append(" < ");
                    sb.Append(m.DeclaringType?.Name ?? "?").Append('.').Append(m.Name);
                    shown++;
                }
                return sb.Length > 0 ? sb.ToString() : "?";
            }
            catch { return "?"; }
        }

        /// <summary>First Sensor under a GameObject that has a float field of that name (the one SetSensorFloat writes).</summary>
        private static bool OMFindSensorField(GameObject go, string field, out Component? comp, out FieldInfo? fi)
        {
            comp = null; fi = null;
            if (go == null) return false;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().Name != "Sensor") continue;
                var f = c.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null || f.FieldType != typeof(float)) continue;
                comp = c; fi = f;
                return true;
            }
            return false;
        }

        // ---------- snapshot ----------

        /// <summary>Called at the end of every completed apply (inside its scan scope). Records the
        /// direct-write sentinels now and the OM members as soon as OverrideManager's queue drained.</summary>
        private static void OMVerifyAfterApply(string src)
        {
            try
            {
                _omSnapValid = false; // the previous snapshot describes the state before this apply
                OMVerifyCaptureSentinels(out var sentinels, out var anchors);
                _omPendingSentinels = sentinels;
                _omPendingAnchors = anchors;
                _omPendingSrc = src;
                int gen = ++_omPendingGen;
                if (OMQueuesEmpty() == true) { OMVerifyTakeSnapshot(""); return; }
                MelonCoroutines.Start(OMVerifySnapshotWhenDrained(gen));
            }
            catch (Exception ex) { MelonLogger.Warning($"[OMVERIFY] after-apply ({src}) failed: {ex.Message}"); }
        }

        /// <summary>Base-spawn prefix, right after OMDrainQueue: take the pending snapshot now.</summary>
        private static void OMVerifySnapshotIfPending()
        {
            try
            {
                if (_omPendingSrc == null) return;
                OMVerifyTakeSnapshot(OMQueuesEmpty() == false ? " queue=not-empty" : "");
            }
            catch (Exception ex) { MelonLogger.Warning($"[OMVERIFY] snapshot failed: {ex.Message}"); }
        }

        private static IEnumerator OMVerifySnapshotWhenDrained(int gen)
        {
            for (int i = 0; i < 120; i++)
            {
                yield return null;
                if (gen != _omPendingGen || _omPendingSrc == null) yield break; // taken already, or superseded
                bool? empty = OMQueuesEmpty();
                if (empty == true || (empty == null && i >= 1))
                {
                    OMVerifyTakeSnapshot(empty == null ? " queue=unknown" : "");
                    yield break;
                }
            }
            if (gen == _omPendingGen && _omPendingSrc != null)
                OMVerifyTakeSnapshot(" queue=not-empty");
        }

        private static void OMVerifyTakeSnapshot(string note)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                _omSnap.Clear();
                int members = 0, unreadable = 0;
                var table = OverrideManager.GetOverrides();
                foreach (var t in table)
                {
                    if (t.Value == null) continue;
                    foreach (var kv in t.Value)
                    {
                        members++;
                        var mo = kv.Value;
                        if (mo == null || !mo.Enabled || mo.Member == null) continue;
                        object? val;
                        try { val = OMReadMember(mo.Member, mo.Target); }
                        catch { unreadable++; continue; }
                        _omSnap.Add(new OMSnapEntry { Target = t.Key, Name = kv.Key, Mo = mo, Member = mo.Member, Obj = mo.Target, Expected = val });
                    }
                }
                _omSentinels = _omPendingSentinels ?? new List<OMSentinel>();
                int sentUnreadable = 0;
                foreach (var sen in _omSentinels)
                {
                    try { sen.Expected = OMReadMember(sen.Member, sen.Target); }
                    catch { sen.Expected = null; sentUnreadable++; }
                }
                _omAnchors = _omPendingAnchors ?? new List<OMAnchor>();
                _omPendingSentinels = null;
                _omPendingAnchors = null;
                _omSnapSrc = _omPendingSrc ?? "?";
                _omPendingSrc = null;
                _omSnapValid = true;
                _omSnapTime = Time.realtimeSinceStartup;
                _omBlockedSinceSnap = 0;
                sw.Stop();
                MelonLogger.Msg($"[OMVERIFY] snapshot src={_omSnapSrc}: members={members} enabled={_omSnap.Count} " +
                    $"sentinels={_omSentinels.Count} anchors={_omAnchors.Count}{(unreadable + sentUnreadable > 0 ? $" unreadable={unreadable + sentUnreadable}" : "")}{note} " +
                    $"({sw.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms)");
                OMVerifyLogValues();
            }
            catch (Exception ex)
            {
                _omSnapValid = false;
                _omPendingSrc = null;
                _omNoSnapReason = "last snapshot failed";
                MelonLogger.Warning($"[OMVERIFY] snapshot failed: {ex.Message}");
            }
        }

        /// <summary>Direct-write sentinels and anchors, read right after an apply (inside its scan scope).</summary>
        private static void OMVerifyCaptureSentinels(out List<OMSentinel> sentinels, out List<OMAnchor> anchors)
        {
            var sl = new List<OMSentinel>();
            var al = new List<OMAnchor>();
            sentinels = sl;
            anchors = al;

            // Only WHAT to watch is recorded here; the expected values are read in OMVerifyTakeSnapshot,
            // after OverrideManager's queue drained (an OM set of the same field lands only then).
            void Add(string label, object target, MemberInfo m, UnityEngine.Object? owner, float? vanilla = null)
            {
                if (target != null && m != null)
                    sl.Add(new OMSentinel { Label = label, Target = target, Member = m, Owner = owner, Vanilla = vanilla });
            }
            void AddSensor(string label, GameObject go, string field)
            {
                if (go != null && OMFindSensorField(go, field, out var c, out var f) && c != null && f != null)
                    Add(label, c, f, c);
            }
            void AddAnchor(string label, UnityEngine.Object o, string? teamName)
            {
                if (o != null) al.Add(new OMAnchor { Label = label, Obj = o, Id = o.GetInstanceID(), TeamName = teamName });
            }

            // 1. Base structures: anchors, and FogOfWarViewDistance on the prefab and its networked twin.
            try
            {
                var teams = Team.Teams;
                var seenInfos = new HashSet<int>();
                if (teams != null)
                {
                    for (int i = 0; i < teams.Count; i++)
                    {
                        var team = teams[i];
                        if (team == null) continue;
                        ObjectInfo? bs = null;
                        try { bs = team.BaseStructure; } catch { }
                        if (bs == null || bs.Prefab == null) continue;
                        if (!seenInfos.Add(bs.GetInstanceID())) continue;
                        string name = ResolveConfigName(bs.DisplayName, bs.name);
                        AddAnchor($"base:{team.name}:{bs.name}", bs, team.name);
                        AddAnchor($"base:{team.name}:prefab", bs.Prefab, null);
                        if (string.IsNullOrEmpty(name) || !_fowDistanceOverrides.ContainsKey(name)) continue;
                        AddSensor($"fow:{name}:prefab", bs.Prefab, "FogOfWarViewDistance");
                        var twin = NetworkedTwin(bs.Prefab);
                        if (twin != null) AddSensor($"fow:{name}:twin", twin, "FogOfWarViewDistance");
                    }
                }
            }
            catch (Exception ex) { LogDebug("[OMVERIFY] base sentinels: " + ex.Message); }

            // 2. One TargetingDistance (the first unit with a target_distance override), prefab + twin.
            // 3. Nest SpawnOnInit.MaxPerTeam (alien_queens_max; recorded even when vanilla).
            try
            {
                bool tdDone = _targetDistanceOverrides.Count == 0, nestDone = false;
                foreach (var info in AllObjectInfos())
                {
                    if (tdDone && nestDone) break;
                    if (info == null || info.Prefab == null) continue;
                    if (!tdDone)
                    {
                        string name = ResolveConfigName(info.DisplayName, info.name);
                        if (!string.IsNullOrEmpty(name) && _targetDistanceOverrides.ContainsKey(name)
                            && OMFindSensorField(info.Prefab, "TargetingDistance", out _, out _))
                        {
                            AddSensor($"td:{name}:prefab", info.Prefab, "TargetingDistance");
                            var twin = NetworkedTwin(info.Prefab);
                            if (twin != null) AddSensor($"td:{name}:twin", twin, "TargetingDistance");
                            tdDone = true;
                        }
                    }
                    if (!nestDone && info.name.Equals("ObjectInfo_Alien_Nest", StringComparison.OrdinalIgnoreCase))
                    {
                        nestDone = true;
                        AddAnchor("nest:ObjectInfo_Alien_Nest", info, null);
                        var mpt = typeof(SpawnOnInit).GetField("MaxPerTeam", BindingFlags.Public | BindingFlags.Instance);
                        if (mpt == null) continue;
                        foreach (var soi in info.Prefab.GetComponentsInChildren<SpawnOnInit>(true))
                        {
                            if (soi == null || soi.SpawnObject == null) continue;
                            if (soi.SpawnObject.name.IndexOf("Queen", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            Add("nest:SpawnOnInit.MaxPerTeam", soi, mpt, soi);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { LogDebug("[OMVERIFY] td/nest sentinels: " + ex.Message); }

            // 4. Every recorded direct prefab write (CreatureAttack values the config scales, speed fallback, ...).
            try
            {
                foreach (var dw in _directWrites)
                    Add("direct:" + dw.Label, dw.Target, dw.Member, dw.Owner, dw.Vanilla);
            }
            catch (Exception ex) { LogDebug("[OMVERIFY] direct sentinels: " + ex.Message); }
        }

        /// <summary>The mod itself reverted everything without re-applying (!rebalance default): the
        /// snapshot no longer describes the intended state.</summary>
        private static void OMVerifyInvalidate(string why)
        {
            _omSnapValid = false;
            _omPendingSrc = null;
            _omPendingSentinels = null;
            _omPendingAnchors = null;
            _omNoSnapReason = why;
        }

        /// <summary>A mod edit outside an apply changed a sentinel on purpose (e.g. alien_queens_max from
        /// the menu): take the new value as expected.</summary>
        private static void OMVerifyRecaptureSentinels(string why)
        {
            try
            {
                int n = 0;
                foreach (var s in _omSentinels)
                {
                    try { s.Expected = OMReadMember(s.Member, s.Target); n++; } catch { }
                }
                if (_debugOmProbe) MelonLogger.Msg($"[OMVERIFY] sentinels re-read after {why} ({n})");
            }
            catch { }
        }

        // ---------- absolute values ----------

        private const int OMVerifyMaxValues = 60;

        /// <summary>Invariant culture, at most 3 decimals, no thousands separators, never '=' or ';'.</summary>
        private static string OMValueText(object? v)
        {
            if (ReferenceEquals(v, null)) return "?";
            if (v is float f) return float.IsNaN(f) ? "NaN" : f.ToString("0.###", CultureInfo.InvariantCulture);
            if (v is double d) return d.ToString("0.###", CultureInfo.InvariantCulture);
            if (v is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            return OMFmt(v).Replace(";", ",").Replace("=", ":");
        }

        /// <summary>One always-on line per snapshot with the ABSOLUTE sentinel values, so a log shows
        /// whether every apply produced the same numbers (the pre-apply check only compares with the
        /// previous snapshot). Format (an analyser parses it):
        ///   [OMVERIFY] values src=SRC: LABEL=VALUE; LABEL=VALUE(vVANILLA); ...[; +N more]
        /// "(vVANILLA)" only on tracked direct writes. Labels never contain '=' or ';'.</summary>
        private static void OMVerifyLogValues()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("[OMVERIFY] values src=").Append(_omSnapSrc).Append(": ");
                int shown = 0;
                foreach (var sen in _omSentinels)
                {
                    if (shown == OMVerifyMaxValues) break;
                    if (shown > 0) sb.Append("; ");
                    sb.Append(sen.Label.Replace("=", "_").Replace(";", "_")).Append('=').Append(OMValueText(sen.Expected));
                    if (sen.Vanilla.HasValue) sb.Append("(v").Append(OMValueText(sen.Vanilla.Value)).Append(')');
                    shown++;
                }
                if (shown == 0) sb.Append("none");
                if (_omSentinels.Count > shown) sb.Append("; +").Append(_omSentinels.Count - shown).Append(" more");
                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception ex) { MelonLogger.Warning($"[OMVERIFY] values line failed: {ex.Message}"); }
        }

        // ---------- live-instance check (probe only) ----------

        private const int OMVerifyMaxLiveChecks = 8;

        /// <summary>Reads a float/int field or property with the given binding (field first, as the live
        /// propagation does). Null when absent or not numeric.</summary>
        private static float? OMReadNumber(object obj, string member, BindingFlags flags)
        {
            var t = obj.GetType();
            var f = t.GetField(member, flags);
            if (f != null)
            {
                if (f.FieldType == typeof(float)) return (float)f.GetValue(obj);
                if (f.FieldType == typeof(int)) return (int)f.GetValue(obj);
                return null;
            }
            var p = t.GetProperty(member, flags);
            if (p != null && p.CanRead)
            {
                if (p.PropertyType == typeof(float)) return (float)p.GetValue(obj);
                if (p.PropertyType == typeof(int)) return (int)p.GetValue(obj);
            }
            return null;
        }

        private static float OMVerifySpeedMult(string name, string field)
        {
            float v;
            if (field == "FlyMoveSpeed" && _flySpeedMultipliers.TryGetValue(name, out v)) return v;
            if (field == "TurboSpeed" && _turboSpeedMultipliers.TryGetValue(name, out v)) return v;
            if (field == "RunSpeed" && _runSpeedMultipliers.TryGetValue(name, out v)) return v;
            if (field == "SprintSpeed" && _sprintSpeedMultipliers.TryGetValue(name, out v)) return v;
            return _moveSpeedMultipliers.TryGetValue(name, out v) ? v : 1f;
        }

        /// <summary>Candidate members of one kind for a unit name, with the multiplier
        /// PropagateToLiveInstances applies: 0 speed field, 1 strafe, 2 turn radius, 3 turret reload,
        /// 4 magazine, 5 spread, 6 jump, 7 gatherer amounts. comp "*" = any component (speed fields, DeclaredOnly).</summary>
        private static List<(string comp, string field, BindingFlags flags, float mult)> OMVerifyLiveCandidates(int kind, string name)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var list = new List<(string, string, BindingFlags, float)>();
            float m;
            switch (kind)
            {
                case 0:
                    foreach (var fn in _speedFieldNames)
                    {
                        if (!_moveSpeedMultipliers.ContainsKey(name) && !_flySpeedMultipliers.ContainsKey(name)
                            && !_turboSpeedMultipliers.ContainsKey(name) && !_runSpeedMultipliers.ContainsKey(name)
                            && !_sprintSpeedMultipliers.ContainsKey(name)) break;
                        m = OMVerifySpeedMult(name, fn);
                        if (Math.Abs(m - 1f) > 0.001f) list.Add(("*", fn, declared, m));
                    }
                    break;
                case 1:
                    if (_strafeSpeedMultipliers.TryGetValue(name, out m))
                    {
                        list.Add(("CreatureDecapod", "FlyMoveScaleSide", any, m));
                        list.Add(("VehicleAir", "StrafeSpeed", declared, m));
                    }
                    break;
                case 2:
                    if (_turnRadiusMultipliers.TryGetValue(name, out m)) list.Add(("VehicleWheeled", "TurningCircleRadius", any, m));
                    break;
                case 3:
                    m = GetWeaponMult(_reloadTimeMultipliers, name, "pri");
                    if (Math.Abs(m - 1f) > 0.001f) list.Add(("VehicleTurret", "PrimaryReloadTime", any, m));
                    break;
                case 4:
                    m = GetWeaponMult(_magazineMultipliers, name, "pri");
                    if (Math.Abs(m - 1f) > 0.001f) list.Add(("VehicleTurret", "PrimaryMagazineSize", any, m));
                    break;
                case 5:
                    m = GetWeaponMult(_accuracyMultipliers, name, "pri");
                    if (Math.Abs(m - 1f) > 0.001f) list.Add(("VehicleTurret", "PrimaryMuzzleSpread", any, m));
                    break;
                case 6:
                    if (_jumpSpeedMultipliers.TryGetValue(name, out m))
                    {
                        list.Add(("Soldier", "JumpSpeed", any, m));
                        list.Add(("PlayerMovement", "JumpSpeed", any, m));
                        list.Add(("FPSMovement", "JumpSpeed", any, m));
                    }
                    break;
                case 7:
                    // Gatherers: live must equal the prefab (both = vanilla snapshot x mult, see Gatherers.cs)
                    if (_harvestRateMultipliers.TryGetValue(name, out m))
                        foreach (var comp in new[] { "MiningClaws", "MiningArmsLaser", "MiningArmsRoller" })
                            list.Add((comp, "ExtractAmount", any, m));
                    if (_depositRateMultipliers.TryGetValue(name, out m))
                        foreach (var comp in new[] { "MiningClaws", "MiningArmsLaser", "MiningArmsRoller" })
                            list.Add((comp, "DepositAmount", any, m));
                    if (_cargoCapacityMultipliers.TryGetValue(name, out m))
                        list.Add(("ResourceHolder", "MaxAmount", any, m));
                    break;
            }
            return list;
        }

        /// <summary>Probe only, after the GameStarted propagation: do already-spawned units carry the
        /// prefab value (vanilla x multiplier once OverrideManager has written the prefab) or one that was
        /// multiplied again? Up to 8 live units/structures, one per kind first (speed field, strafe, turn
        /// radius, turret reload, magazine, spread, jump, gatherer amounts), then more of the same kinds. Read-only;
        /// iterates the teams' Units/Structures only.</summary>
        private static void OMVerifyLiveCheck(string tag)
        {
            if (!_debugOmProbe) return;
            int checkedN = 0, diff = 0;
            try
            {
                // live objects: units first (starting Queen, harvesters, starter vehicles), then structures
                var live = new List<(GameObject go, ObjectInfo info)>();
                var seen = new HashSet<int>();
                var teams = Team.Teams;
                if (teams != null)
                {
                    foreach (var team in teams)
                    {
                        if (team == null || team.Units == null) continue;
                        foreach (var u in team.Units)
                            if (u != null && u.ObjectInfo != null && u.ObjectInfo.Prefab != null && seen.Add(u.gameObject.GetInstanceID()))
                                live.Add((u.gameObject, u.ObjectInfo));
                    }
                    foreach (var team in teams)
                    {
                        if (team == null || team.Structures == null) continue;
                        foreach (var st in team.Structures)
                            if (st != null && st.ObjectInfo != null && st.ObjectInfo.Prefab != null && seen.Add(st.gameObject.GetInstanceID()))
                                live.Add((st.gameObject, st.ObjectInfo));
                    }
                }

                var used = new HashSet<string>();
                for (int pass = 0; pass < 2 && checkedN < OMVerifyMaxLiveChecks; pass++)
                {
                    for (int kind = 0; kind <= 7 && checkedN < OMVerifyMaxLiveChecks; kind++)
                    {
                        bool found = false;
                        foreach (var entry in live)
                        {
                            if (found || checkedN >= OMVerifyMaxLiveChecks) break;
                            var go = entry.go;
                            var info = entry.info;
                            if (go == null || info == null || info.Prefab == null) continue;
                            string name = ResolveConfigName(info.DisplayName, info.name);
                            if (string.IsNullOrEmpty(name)) continue;
                            foreach (var c in OMVerifyLiveCandidates(kind, name))
                            {
                                Component? liveComp = null;
                                float? liveV = null, prefabV = null;
                                try
                                {
                                    foreach (var lc in go.GetComponentsInChildren<Component>(true))
                                    {
                                        if (lc == null || (c.comp != "*" && lc.GetType().Name != c.comp)) continue;
                                        var v = OMReadNumber(lc, c.field, c.flags);
                                        if (v == null) continue;
                                        liveComp = lc; liveV = v;
                                        break;
                                    }
                                    if (liveComp == null) continue;
                                    string tn = liveComp.GetType().Name;
                                    foreach (var pc in info.Prefab.GetComponentsInChildren<Component>(true))
                                    {
                                        if (pc == null || pc.GetType().Name != tn) continue;
                                        prefabV = OMReadNumber(pc, c.field, c.flags); // first of the type, as the propagation reads it
                                        break;
                                    }
                                }
                                catch { continue; }
                                if (liveComp == null || liveV == null || prefabV == null) continue;
                                if (!used.Add(info.name + "|" + liveComp.GetType().Name + "|" + c.field)) continue;
                                float lv = liveV.Value, pv = prefabV.Value;
                                bool ok = lv == pv || Math.Abs(lv - pv) <= 0.001f * Math.Abs(pv);
                                checkedN++;
                                if (!ok) diff++;
                                string line = $"[OMVERIFY] {tag} live-check unit={info.name} comp={liveComp.GetType().Name} field={c.field} " +
                                    $"live={OMValueText(lv)} prefab={OMValueText(pv)} mult={OMValueText(c.mult)} {(ok ? "ok" : "DIFF")}";
                                if (ok) MelonLogger.Msg(line); else MelonLogger.Warning(line);
                                found = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[OMVERIFY] {tag} live-check failed: {ex.Message}"); }
            try { MelonLogger.Msg($"[OMVERIFY] {tag} live-check: checked={checkedN} diff={diff}"); } catch { }
        }

        // ---------- check ----------

        /// <summary>Probe-mode check at a lifecycle hook (only with "debug_om_probe": true).</summary>
        private static void OMVerifyProbe(string tag, bool liveBaseFoW = false)
        {
            if (!_debugOmProbe) return;
            OMVerifyCheck(tag, null, liveBaseFoW);
        }

        /// <summary>Compares the current real values with the snapshot and logs one summary line
        /// (plus up to 10 detail lines per kind). Read-only, cheap, never throws.</summary>
        private static void OMVerifyCheck(string tag, string? src, bool liveBaseFoW = false)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                string srcTxt = src != null ? $" src={src}" : "";
                if (!_omSnapValid)
                {
                    string why = _omPendingSrc != null ? $"pending after {_omPendingSrc}" : _omNoSnapReason;
                    MelonLogger.Msg($"[OMVERIFY] {tag}: no snapshot ({why}){srcTxt} blocked={_omBlockedSinceSnap}/{_omBlockedTotal}");
                    if (liveBaseFoW) OMVerifyLiveBaseFoW(tag);
                    return;
                }

                int members = 0, enabled = 0;
                var table = OverrideManager.GetOverrides();
                foreach (var t in table)
                {
                    if (t.Value == null) continue;
                    foreach (var kv in t.Value)
                    {
                        members++;
                        if (kv.Value != null && kv.Value.Enabled) enabled++;
                    }
                }

                // Player Cap Exempt: UnitCapValue of the units of an OPEN team is ours to change.
                HashSet<string>? dynTargets = null;
                foreach (var kv in _dynCap)
                {
                    if (kv.Value == null || !kv.Value.Open) continue;
                    foreach (var n in kv.Value.Originals.Keys)
                        (dynTargets ??= new HashSet<string>()).Add($"A:{n}.asset");
                }

                var details = new List<string>();
                int mismatches = 0, missing = 0, dyncap = 0, shown = 0;
                foreach (var e in _omSnap)
                {
                    if (dynTargets != null && e.Name == "UnitCapValue" && dynTargets.Contains(e.Target)) { dyncap++; continue; }
                    string? why = null;
                    object? real = null;
                    if (!table.TryGetValue(e.Target, out var byName) || byName == null
                        || !byName.TryGetValue(e.Name, out var mo) || !ReferenceEquals(mo, e.Mo))
                    {
                        why = "missing"; // the OM table no longer holds this member (ClearAll?)
                        missing++;
                    }
                    else if (!mo.Enabled) why = "disabled";
                    else if (e.Obj is UnityEngine.Object uo && uo == null) why = "destroyed";
                    else
                    {
                        try
                        {
                            real = OMReadMember(e.Member, e.Obj);
                            if (!OMValuesEqual(real, e.Expected)) why = "value";
                        }
                        catch (Exception rex) { why = "unreadable:" + rex.GetType().Name; }
                    }
                    if (why == null) continue;
                    mismatches++;
                    if (shown++ < OMVerifyMaxDetail)
                        details.Add($"[OMVERIFY]   {tag} mismatch {e.Target}.{e.Name} real={(why == "value" ? OMFmt(real) : "-")} expected={OMFmt(e.Expected)} ({why})");
                }

                int sentOk = 0, sShown = 0;
                foreach (var s in _omSentinels)
                {
                    string? why = null;
                    object? real = null;
                    if (!ReferenceEquals(s.Owner, null) && s.Owner == null) why = "destroyed";
                    else
                    {
                        try
                        {
                            real = OMReadMember(s.Member, s.Target);
                            if (!OMValuesEqual(real, s.Expected)) why = "value";
                        }
                        catch (Exception rex) { why = "unreadable:" + rex.GetType().Name; }
                    }
                    if (why == null) { sentOk++; continue; }
                    if (sShown++ < OMVerifyMaxDetail)
                        details.Add($"[OMVERIFY]   {tag} sentinel {s.Label} real={(why == "value" ? OMFmt(real) : "-")} expected={OMFmt(s.Expected)} ({why})");
                }

                int anchorsBad = 0;
                List<Team>? teams = null;
                try { teams = Team.Teams; } catch { }
                foreach (var a in _omAnchors)
                {
                    string? why = null;
                    if (a.Obj == null) why = "destroyed";
                    else if (a.TeamName != null && teams != null)
                    {
                        foreach (var team in teams)
                        {
                            if (team == null || team.name != a.TeamName) continue;
                            ObjectInfo? cur = null;
                            try { cur = team.BaseStructure; } catch { }
                            if (cur != null && cur.GetInstanceID() != a.Id) why = $"replaced:{cur.GetInstanceID()}";
                            break;
                        }
                    }
                    if (why == null) continue;
                    anchorsBad++;
                    if (anchorsBad <= OMVerifyMaxDetail)
                        details.Add($"[OMVERIFY]   {tag} anchor {a.Label} id={a.Id} ({why})");
                }

                sw.Stop();
                bool sentAllOk = sentOk == _omSentinels.Count;
                bool bad = mismatches > 0 || !sentAllOk || anchorsBad > 0;
                float age = Time.realtimeSinceStartup - _omSnapTime;
                string line = $"[OMVERIFY] {tag}: members={members} enabled={enabled} snap={_omSnap.Count} mismatches={mismatches} " +
                    $"missing={missing} dyncap={dyncap} sentinels={sentOk}/{_omSentinels.Count} {(sentAllOk ? "ok" : "FAIL")} " +
                    $"ids={(anchorsBad == 0 ? "ok" : $"changed:{anchorsBad}/{_omAnchors.Count}")} blocked={_omBlockedSinceSnap}/{_omBlockedTotal}" +
                    $"{srcTxt} snapsrc={_omSnapSrc} age={age.ToString("F0", CultureInfo.InvariantCulture)}s " +
                    $"({sw.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms)";
                if (bad) MelonLogger.Warning(line); else MelonLogger.Msg(line);
                foreach (var d in details) MelonLogger.Warning(d);
                if (liveBaseFoW) OMVerifyLiveBaseFoW(tag);
            }
            catch (Exception ex) { MelonLogger.Warning($"[OMVERIFY] {tag}: check failed: {ex.Message}"); }
        }

        /// <summary>Probe at the base-spawn postfix: FogOfWarViewDistance on the LIVE base structures,
        /// read before propagation touches them (what the spawn produced).</summary>
        private static void OMVerifyLiveBaseFoW(string tag)
        {
            try
            {
                var teams = Team.Teams;
                if (teams == null) return;
                int n = 0;
                for (int i = 0; i < teams.Count; i++)
                {
                    var team = teams[i];
                    if (team == null) continue;
                    ObjectInfo? bs = null;
                    try { bs = team.BaseStructure; } catch { }
                    if (bs == null) continue;
                    string name = ResolveConfigName(bs.DisplayName, bs.name);
                    bool hasWant = !string.IsNullOrEmpty(name) && _fowDistanceOverrides.TryGetValue(name, out _);
                    float want = hasWant ? _fowDistanceOverrides[name] : -1f;
                    var structures = team.Structures;
                    if (structures == null) continue;
                    for (int j = 0; j < structures.Count; j++)
                    {
                        var s = structures[j];
                        if (s == null || s.ObjectInfo != bs) continue;
                        string val = "none";
                        bool ok = !hasWant;
                        if (OMFindSensorField(s.gameObject, "FogOfWarViewDistance", out var c, out var f) && c != null && f != null)
                        {
                            float v = (float)f.GetValue(c);
                            val = v.ToString("0.##", CultureInfo.InvariantCulture);
                            if (hasWant) ok = Math.Abs(v - want) < 0.01f;
                        }
                        string line = $"[OMVERIFY] {tag} live-fow team={team.name} base={name} sensor={val} " +
                            $"want={(hasWant ? want.ToString("0.##", CultureInfo.InvariantCulture) : "-")} {(hasWant ? (ok ? "ok" : "MISMATCH") : "n/a")}";
                        if (ok) MelonLogger.Msg(line); else MelonLogger.Warning(line);
                        n++;
                    }
                }
                if (n == 0) MelonLogger.Msg($"[OMVERIFY] {tag} live-fow: no live base structure");
            }
            catch (Exception ex) { MelonLogger.Warning($"[OMVERIFY] {tag} live-fow failed: {ex.Message}"); }
        }

        // ---------- RevertAll / ClearAll ----------

        /// <summary>Probe only: a non-blocked RevertAll or a ClearAll, with a short stack.</summary>
        private static void OMVerifyLogCall(string what)
        {
            if (!_debugOmProbe) return;
            try { MelonLogger.Msg($"[OMVERIFY] call OverrideManager.{what} stack={OMShortStack()}"); }
            catch { }
        }

        /// <summary>Every game RevertAll the prefix blocks: always counted (blocked=N/M in every check
        /// line), with the probe also checked and logged with a short stack.</summary>
        private static void OMVerifyNoteBlockedRevertAll(bool notify, bool enqueue)
        {
            try
            {
                _omBlockedTotal++;
                _omBlockedSinceSnap++;
                if (!_debugOmProbe) return;
                OMVerifyCheck("revertall-blocked", null);
                MelonLogger.Msg($"[OMVERIFY] revertall-blocked n={_omBlockedTotal} notify={notify} enqueue={enqueue} stack={OMShortStack()}");
            }
            catch { }
        }
    }
}

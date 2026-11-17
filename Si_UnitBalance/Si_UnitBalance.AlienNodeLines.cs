using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Alien starter node lines ("alien_starter_node_lines", int 0-6, default 0 = off;
        // "alien_starter_node_lines_max_m", JSON only, default 600)
        //
        // Human teams can put a refinery next to their Balterium at round start; an Alien commander first has
        // to chain Nodes from the Nest to the Biotics. With N > 0 every Alien team in play gets finished Nodes
        // from its starter Nest towards its N closest Biotics patches, so a Bio Cache (and a Lesser Spawning
        // Cyst next to it) can be placed at those patches from the start.
        //
        // Game facts this relies on (decompile 1.0.17, same on 0.9.47 where noted in the plan):
        //  * vanilla finishes a construction with Game.SpawnPrefab(ObjectToBuild, null, Team, pos, rot)
        //    (ConstructionSite.SpawnObject), so a directly spawned Node is the same object a finished build is.
        //  * AlienStructure.InitLink (OnPostInit / first update) links a new alien structure both ways to every
        //    same-team structure whose ObjectInfo has StructureConstructionAnchor, if
        //    Distance2D(anchorCentre, 2*pos - ownBounds.ClosestPoint(anchorBounds.ClosestPoint(pos))) <= own
        //    MaximumBaseStructureDistance. Linked = construction anchor, functional, no decay.
        //  * the server's own area test is GetObstructed + GetGroundSlopeClear of the ConstructionPreview
        //    (Structure.GetAreaClearForConstruction); positions are grounded the way vanilla placement does it
        //    (ray from 1500 m, SnapToGrid, GetSnappedRotation per 90 deg yaw, y += adjustY).
        //
        // Per team: patches nearest first (2D Nest -> patch bounds); each patch attaches to the nearest member
        // of the network laid so far (Nest or a starter node), so lines share trunks. A patch is "tapped" when a
        // member is a legal Bio Cache anchor for the spot just outside the patch edge; a tapped patch needs no
        // node and still counts. Hops are Rn - 2*rn long and checked exactly against the hop's anchor; blocked
        // hops sidestep laterally or shorten; a line that cannot continue ends there. The last node stays clear
        // of the Bio Cache spot. Laid once per round, 2 s after the round started (after Si_MapBalance's
        // round-start work, before Si_StarterUnits' +5 s spawn). Never re-laid by !rebalance, never removed:
        // the nodes are ordinary structures afterwards.
        // =============================================

        private static int _alienStarterNodeLines = 0;                   // config "alien_starter_node_lines"
        private const float NodeLinesMaxMDefault = 600f;
        private static float _alienStarterNodeLinesMaxM = NodeLinesMaxMDefault; // config "alien_starter_node_lines_max_m"
        private const int NodeLinesMax = 6;
        private const string AlienNodeInfoName = "ObjectInfo_Alien_Node";
        private const float NodeLinesDelaySeconds = 2f;
        private const float NodeLinesMinProgressM = 20f;
        private const float NodeLinesOvershootM = 30f;          // aim this far inside the tapping distance
        private const float NodeLinesRangeMarginM = 3f;
        private const float NodeLinesTapMarginM = 10f;
        private static readonly float[] NodeLinesLaterals = { 0f, 15f, -15f, 30f, -30f, 45f, -45f, 60f, -60f };
        private static readonly float[] NodeLinesHopScales = { 1f, 0.7f, 0.5f };

        private static int ClampNodeLines(int n) => n < 0 ? 0 : n > NodeLinesMax ? NodeLinesMax : n;

        private static int _nodeLinesGen;          // bumped per round: stale coroutines stop
        private static int _nodeLinesScheduledGen = -1;
        private static readonly Dictionary<int, List<Structure>> _nodeLinesSpawned = new Dictionary<int, List<Structure>>();

        private static void NodeLinesResetRound()
        {
            _nodeLinesGen++;
            _nodeLinesSpawned.Clear();
        }

        /// <summary>Round start (OnGameStarted): lay the lines once this round, a moment later.</summary>
        private static void NodeLinesScheduleRound(string why)
        {
            try
            {
                if (!_enabled || _alienStarterNodeLines <= 0) return;
                if (!Game.GetIsServer()) return;
                if (_nodeLinesScheduledGen == _nodeLinesGen) return; // once per round
                _nodeLinesScheduledGen = _nodeLinesGen;
                MelonCoroutines.Start(NodeLinesRun(_nodeLinesGen, _alienStarterNodeLines, why));
            }
            catch (Exception ex) { MelonLogger.Warning($"[NODELINES] schedule ({why}) failed: {ex.Message}"); }
        }

        private static IEnumerator NodeLinesRun(int gen, int n, string why)
        {
            yield return new WaitForSeconds(NodeLinesDelaySeconds);
            if (gen != _nodeLinesGen) yield break;

            if (_alienNestOnlyBuild)
            {
                MelonLogger.Msg($"[NODELINES] skipped this round: alien_nest_only_build is on (Nodes are no construction anchors and could not link to each other)");
                yield break;
            }

            var layers = new List<NodeLineLayer>();
            try
            {
                var teams = Team.Teams;
                for (int i = 0; teams != null && i < teams.Count; i++)
                {
                    var team = teams[i];
                    if (team == null) continue;
                    ObjectInfo? bs = null;
                    try { bs = team.BaseStructure; } catch { }
                    if (bs == null || bs.name != AlienNestInfoName) continue;
                    var nest = FindStarterNest(team, bs);
                    if (nest == null) continue; // team not in play this round
                    NodeLineLayer? layer = null;
                    try { layer = NodeLineLayer.Create(i, team, nest, n); }
                    catch (Exception ex) { MelonLogger.Warning($"[NODELINES] {team.TeamShortName}: preparing failed, nothing laid: {ex.Message}"); }
                    if (layer != null) layers.Add(layer);
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[NODELINES] team scan failed ({why}): {ex.Message}"); }

            foreach (var layer in layers)
            {
                while (true)
                {
                    bool more = layer.Next(); // one node at most per call; exception-safe inside
                    if (!more) break;
                    yield return null;       // let the new node register before the next obstruction test
                    if (gen != _nodeLinesGen) yield break;
                    if (!IsAlive(layer.Nest)) { layer.Abort("starter Nest destroyed while laying"); break; }
                }
                layer.LogSummary();
            }
            if (layers.Count == 0) yield break;

            yield return new WaitForSeconds(5f);
            if (gen != _nodeLinesGen) yield break;
            foreach (var layer in layers) layer.ReadBack("t+5s", probeBioCache: true);

            yield return new WaitForSeconds(55f);
            if (gen != _nodeLinesGen) yield break;
            foreach (var layer in layers) layer.ReadBack("t+60s", probeBioCache: false);
        }

        // ---- geometry helpers ----

        private static float Dist2D(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        private static float HalfExtent2D(Bounds b) => Mathf.Max(b.extents.x, b.extents.z) + new Vector2(b.center.x, b.center.z).magnitude;

        /// <summary>The anchor distance the game uses for a structure with bounds <paramref name="localBounds"/>
        /// placed at pos/rot against an anchor: the larger of the placement form (GetAnchorDistance, radius mode)
        /// and the link form (AlienStructure.InitLink).</summary>
        private static float AnchorDistance(Bounds localBounds, Vector3 pos, Quaternion rot, Structure anchor)
        {
            Bounds own = GameFuncs.TransformBounds(localBounds, pos, rot);
            Vector3 ac = anchor.transform.position;
            float place = GameMath.Distance2D(ac, pos - own.ClosestPoint(ac) + pos);
            Bounds ab = GameFuncs.TransformBounds(anchor.PhysicalBounds, anchor.transform, true);
            Vector3 v = own.ClosestPoint(ab.ClosestPoint(pos));
            float link = GameMath.Distance2D(ac, pos - v + pos);
            return Mathf.Max(place, link);
        }

        /// <summary>Ground a structure like vanilla placement does and run the server's own area test.
        /// treatUnitsAsClear: a unit standing there does not count (for the read-back probe only).</summary>
        private static bool NodeLinesTryGround(ConstructionPreview preview, ConstructionData cd, Vector3 xz, float yawPref,
            bool treatUnitsAsClear, out Vector3 pos, out Quaternion rot, out string why)
        {
            pos = Vector3.zero; rot = Quaternion.identity; why = "";
            if (!Physics.Raycast(new Vector3(xz.x, xz.y + 1500f, xz.z), Vector3.down, out var hit, 3000f,
                    GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC, QueryTriggerInteraction.Ignore))
            { why = "no ground"; return false; }
            Vector3 p = GameMath.SnapToGrid(hit.point, cd.GridSnapXZ, cd.GridSnapY);
            try
            {
                if (FogOfWar.Instance != null && !FogOfWar.Instance.Bounds.Contains(p)) { why = "outside the map"; return false; }
            }
            catch { }
            for (int k = 0; k < 4; k++)
            {
                Quaternion r = preview.GetSnappedRotation(p, Quaternion.Euler(0f, yawPref + k * 90f, 0f), cd.GridSnapY);
                Vector3 q = p;
                if (!preview.GetGroundSlopeClear(q, r, out float adjustY)) { why = "ground/slope"; continue; }
                q.y += adjustY;
                var ob = preview.GetObstructed(q, r, out bool byUnits);
                if (ob != EObstructedType.None && !(treatUnitsAsClear && ob == EObstructedType.Unit)) { why = "obstructed (" + ob + ")"; continue; }
                pos = q; rot = r;
                return true;
            }
            return false;
        }

        private static string P(Vector3 v) => "(" + v.x.ToString("F0", CultureInfo.InvariantCulture) + ", " + v.z.ToString("F0", CultureInfo.InvariantCulture) + ")";

        // ---- one team's lay ----

        private sealed class NodeLineLayer
        {
            private sealed class Patch
            {
                public ResourceArea Area = null!;
                public int Rank;            // 1 = closest
                public float NestEdgeDist;
                public Vector3 Spot;        // Bio Cache spot just outside the patch edge
                public bool HasSpot;
                public string Result = "";
                public bool Tapped;
                public int Nodes;
            }

            public readonly int TeamIndex;
            public readonly Team Team;
            public readonly Structure Nest;
            private readonly int _n;
            private readonly string _tn;
            private ObjectInfo _nodeInfo = null!, _bcInfo = null!;
            private ConstructionData _nodeCd = null!, _bcCd = null!;
            private ConstructionPreview _nodePreview = null!, _bcPreview = null!;
            private GameObject _nodePrefab = null!;
            private float _rn, _Rn, _rb, _Rb, _hop, _dTap, _keepFree;
            private readonly List<Structure> _members = new List<Structure>();
            private readonly List<Vector3> _enemyBases = new List<Vector3>();
            private readonly List<Patch> _patches = new List<Patch>();
            private int _p = -1;              // current patch
            private Structure? _tip;
            private bool _walking, _done;
            private float _totalLen;
            private int _spawned;
            private string _abort = "";

            private NodeLineLayer(int teamIndex, Team team, Structure nest, int n)
            {
                TeamIndex = teamIndex; Team = team; Nest = nest; _n = n;
                _tn = team.TeamShortName;
            }

            /// <summary>Null when the team must not get lines (one warning is logged).</summary>
            public static NodeLineLayer? Create(int teamIndex, Team team, Structure nest, int n)
            {
                var l = new NodeLineLayer(teamIndex, team, nest, n);
                return l.Prepare() ? l : null;
            }

            private bool Prepare()
            {
                _nodeInfo = FindObjectInfoByName(AlienNodeInfoName)!;
                _bcInfo = FindObjectInfoByName(AlienBioCacheInfoName)!;
                _nodeCd = _nodeInfo != null ? _nodeInfo.ConstructionData : null!;
                _bcCd = _bcInfo != null ? _bcInfo.ConstructionData : null!;
                try { _nodePreview = _nodeCd != null ? _nodeCd.ObjectPreviewSetup : null!; } catch { _nodePreview = null!; }
                try { _bcPreview = _bcCd != null ? _bcCd.ObjectPreviewSetup : null!; } catch { _bcPreview = null!; }
                if (_nodeInfo == null || _bcInfo == null || _nodeCd == null || _bcCd == null || _nodePreview == null || _bcPreview == null || _nodeInfo.Prefab == null)
                {
                    MelonLogger.Warning($"[NODELINES] {_tn}: Node / Bio Cache data or placement preview not found (node info {_nodeInfo != null}, cd {_nodeCd != null}, preview {_nodePreview != null}; " +
                        $"bio cache info {_bcInfo != null}, cd {_bcCd != null}, preview {_bcPreview != null}) — nothing laid");
                    return false;
                }
                _nodePrefab = _nodeInfo.Prefab;
                if (!GameDatabase.GetPrefabSpawnable(_nodePrefab))
                {
                    BuildNetworkedPrefabIndex();
                    var twin = NetworkedTwin(_nodePrefab);
                    if (twin != null && GameDatabase.GetPrefabSpawnable(twin)) _nodePrefab = twin;
                }

                _Rn = _nodeCd.MaximumBaseStructureDistance;
                _Rb = _bcCd.MaximumBaseStructureDistance;
                _rn = HalfExtent2D(_nodeInfo.PhysicalBounds);
                _rb = HalfExtent2D(_bcInfo.PhysicalBounds);
                _hop = _Rn - 2f * _rn;
                _dTap = _Rb - _rb - NodeLinesTapMarginM;
                _keepFree = _rb + _rn + 5f;
                if (_hop < NodeLinesMinProgressM || _dTap <= 0f)
                {
                    MelonLogger.Warning($"[NODELINES] {_tn}: reach too small (Node {_Rn:F0}/r{_rn:F1} -> hop {_hop:F0}, Bio Cache {_Rb:F0}/r{_rb:F1} -> tap {_dTap:F0}) — nothing laid");
                    return false;
                }

                _members.Add(Nest);
                Vector3 np = Nest.transform.position;

                // other teams' base structures (for the "patch is on their side" rule)
                var all = Structure.Structures;
                for (int i = 0; all != null && i < all.Count; i++)
                {
                    var s = all[i];
                    if (s == null || s.IsDestroyed || s.Team == null || s.Team == Team) continue;
                    ObjectInfo? obs = null;
                    try { obs = s.Team.BaseStructure; } catch { }
                    if (obs != null && s.ObjectInfo == obs) _enemyBases.Add(s.transform.position);
                }

                // the N closest Biotics patches in play (fog of war ignored on purpose)
                var cands = new List<Patch>();
                var areas = ResourceArea.ResourceAreas;
                Resource res = Team.UsableResource;
                Bounds? map = null;
                try { if (FogOfWar.Instance != null) map = FogOfWar.Instance.Bounds; } catch { }
                for (int i = 0; areas != null && i < areas.Count; i++)
                {
                    var a = areas[i];
                    if (a == null || a.ResourceType != res || a.ResourceAmountCurrent <= 0) continue;
                    Vector3 c = a.Bounds.center;
                    if (map.HasValue && (c.x < map.Value.min.x || c.x > map.Value.max.x || c.z < map.Value.min.z || c.z > map.Value.max.z)) continue;
                    cands.Add(new Patch { Area = a, NestEdgeDist = EdgeDist(a.Bounds, np) });
                }
                cands.Sort((x, y) => x.NestEdgeDist.CompareTo(y.NestEdgeDist));
                for (int i = 0; i < cands.Count && i < _n; i++) { cands[i].Rank = i + 1; _patches.Add(cands[i]); }
                if (_patches.Count == 0)
                    MelonLogger.Msg($"[NODELINES] {_tn}: no Biotics patch with resources found — nothing to connect");
                return true;
            }

            private static float EdgeDist(Bounds b, Vector3 p)
            {
                float dx = Mathf.Max(b.min.x - p.x, 0f, p.x - b.max.x);
                float dz = Mathf.Max(b.min.z - p.z, 0f, p.z - b.max.z);
                return Mathf.Sqrt(dx * dx + dz * dz);
            }

            /// <summary>Bio Cache spot for a member: the patch edge point nearest to it, pushed outward.</summary>
            private Vector3 SpotFor(Bounds b, Vector3 m)
            {
                Vector3 e = new Vector3(Mathf.Clamp(m.x, b.min.x, b.max.x), m.y, Mathf.Clamp(m.z, b.min.z, b.max.z));
                Vector3 d = new Vector3(m.x - e.x, 0f, m.z - e.z);
                if (d.sqrMagnitude < 0.01f) d = new Vector3(m.x - b.center.x, 0f, m.z - b.center.z); // member inside the box
                if (d.sqrMagnitude < 0.01f) d = Vector3.forward;
                return e + d.normalized * (_rb + 5f);
            }

            public void Abort(string why)
            {
                _abort = why;
                _done = true;
                if (_p >= 0 && _p < _patches.Count && _walking && string.IsNullOrEmpty(_patches[_p].Result))
                    _patches[_p].Result = "stopped: " + why;
            }

            /// <summary>Advance by at most one spawned node. False when this team is finished.</summary>
            public bool Next()
            {
                while (!_done)
                {
                    try
                    {
                        if (!_walking)
                        {
                            _p++;
                            if (_p >= _patches.Count) { _done = true; break; }
                            BeginPatch(_patches[_p]);
                            continue;
                        }
                        if (Hop(_patches[_p])) return true; // a node was spawned
                    }
                    catch (Exception ex)
                    {
                        // one bad patch must not stop the others
                        if (_p >= 0 && _p < _patches.Count) _patches[_p].Result = "error: " + ex.Message;
                        MelonLogger.Warning($"[NODELINES] {_tn}: patch #{_p + 1} failed: {ex.Message}");
                        _walking = false;
                    }
                }
                return false;
            }

            private void BeginPatch(Patch pt)
            {
                _walking = false;
                Bounds b = pt.Area.Bounds;
                Structure? best = null; Vector3 bestSpot = Vector3.zero; float bestD = float.MaxValue;
                foreach (var m in _members)
                {
                    if (!IsAlive(m)) continue;
                    Vector3 s = SpotFor(b, m.transform.position);
                    float d = Dist2D(m.transform.position, s);
                    if (d < bestD) { bestD = d; best = m; bestSpot = s; }
                }
                if (best == null) { pt.Result = "skipped: no live network member"; return; }
                pt.Spot = bestSpot; pt.HasSpot = true;

                if (bestD <= _dTap) { pt.Tapped = true; pt.Result = "in reach of " + Label(best); return; }

                Vector3 np = Nest.transform.position;
                float nestDist = Dist2D(np, bestSpot);
                if (nestDist > _alienStarterNodeLinesMaxM) { pt.Result = $"skipped: {nestDist:F0} m from the Nest > max {_alienStarterNodeLinesMaxM:F0} m"; return; }
                if (_alienNestReachRadius > 0f && nestDist > _alienNestReachRadius) { pt.Result = $"skipped: {nestDist:F0} m from the Nest > alien_nest_reach_radius {_alienNestReachRadius:F0} m"; return; }
                foreach (var eb in _enemyBases)
                    if (Dist2D(eb, bestSpot) < nestDist) { pt.Result = $"skipped: nearer to another team's base ({Dist2D(eb, bestSpot):F0} m) than to the Nest ({nestDist:F0} m)"; return; }

                _tip = best;
                _walking = true;
            }

            private string Label(Structure s) => s == Nest ? "the Nest" : "node " + P(s.transform.position);

            /// <summary>One hop for the current patch. True when a node was spawned.</summary>
            private bool Hop(Patch pt)
            {
                var tip = _tip;
                if (!IsAlive(tip)) { pt.Result = $"stopped after {pt.Nodes} node(s): line tip gone"; _walking = false; return false; }
                Vector3 tp = tip!.transform.position;
                Vector3 S = pt.Spot;
                float d = Dist2D(tp, S);
                if (d <= _dTap) { pt.Tapped = true; pt.Result = pt.Nodes + " node(s)" + (pt.Nodes == 0 ? ", in reach of " + Label(tip) : ""); _walking = false; return false; }

                int cap = Mathf.CeilToInt(_alienStarterNodeLinesMaxM / _hop) + 2;
                if (pt.Nodes >= cap) { pt.Result = $"stopped after {pt.Nodes} node(s): node cap {cap} for one patch"; _walking = false; return false; }

                Vector3 dir = new Vector3(S.x - tp.x, 0f, S.z - tp.z) / d;
                Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
                // Aim a little past the tapping distance (grid snap moves a spot up to ~3.5 m), so the hop that
                // reaches it really taps the patch and never lands a few metres short (first staging run: a 21 m
                // hop for 2 m, and a line "blocked" 3 m short because a 20 m step could not show 20 m of progress).
                float toGoal = d - _dTap;
                float step = Mathf.Min(_hop, toGoal + NodeLinesOvershootM);
                float yawPref = Mathf.Round(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg / 90f) * 90f;

                string lastWhy = "no candidate";
                foreach (float scale in NodeLinesHopScales)
                {
                    float st = Mathf.Max(NodeLinesMinProgressM, step * scale);
                    foreach (float lat in NodeLinesLaterals)
                    {
                        Vector3 c = new Vector3(tp.x + dir.x * st + perp.x * lat, tp.y, tp.z + dir.z * st + perp.z * lat);
                        if (!NodeLinesTryGround(_nodePreview, _nodeCd, c, yawPref, false, out Vector3 pos, out Quaternion rot, out string why)) { lastWhy = why; continue; }
                        float dS = Dist2D(pos, S);
                        if (dS > d - NodeLinesMinProgressM) { lastWhy = "no progress"; continue; }
                        if (dS < _keepFree) { lastWhy = "too close to the Bio Cache spot"; continue; }
                        float ad = AnchorDistance(_nodeInfo.PhysicalBounds, pos, rot, tip);
                        if (ad > _Rn - NodeLinesRangeMarginM) { lastWhy = $"out of reach ({ad:F0} m)"; continue; }

                        GameObject? go = null;
                        try { go = Game.SpawnPrefab(_nodePrefab, null, Team, pos, rot); }
                        catch (Exception sex) { lastWhy = "spawn threw: " + sex.Message; }
                        var node = go != null ? go.GetComponent<Structure>() : null;
                        if (node == null)
                        {
                            pt.Result = $"stopped after {pt.Nodes} node(s): spawn failed at {P(pos)} ({lastWhy})";
                            _walking = false;
                            return false;
                        }
                        float hopLen = Dist2D(tp, pos);
                        _totalLen += hopLen;
                        _spawned++;
                        pt.Nodes++;
                        _members.Add(node);
                        if (!_nodeLinesSpawned.TryGetValue(TeamIndex, out var list)) { list = new List<Structure>(); _nodeLinesSpawned[TeamIndex] = list; }
                        list.Add(node);
                        _tip = node;
                        MelonLogger.Msg($"[NODELINES] {_tn}: node {_spawned} at ({pos.x:F0}, {pos.y:F0}, {pos.z:F0}) yaw {rot.eulerAngles.y:F0} for patch #{pt.Rank}, " +
                            $"{hopLen:F0} m from {Label(tip)} (anchor distance {ad:F0}/{_Rn:F0}), {Dist2D(pos, S):F0} m to the Bio Cache spot");
                        return true;
                    }
                }
                pt.Result = $"blocked after {pt.Nodes} node(s) at {P(tp)}, {d:F0} m from the Bio Cache spot (last reason: {lastWhy})";
                _walking = false;
                return false;
            }

            public void LogSummary()
            {
                try
                {
                    int tapped = 0;
                    var parts = new List<string>();
                    foreach (var pt in _patches)
                    {
                        if (pt.Tapped) tapped++;
                        Vector3 c = pt.Area != null ? pt.Area.Bounds.center : Vector3.zero;
                        parts.Add($"#{pt.Rank} {P(c)} {pt.NestEdgeDist:F0} m: {(string.IsNullOrEmpty(pt.Result) ? "not reached" : pt.Result)}");
                    }
                    MelonLogger.Msg($"[NODELINES] {_tn}: N={_n}, tapped {tapped}/{_patches.Count}, {_spawned} nodes, {_totalLen:F0} m " +
                        $"(Node reach {_Rn:F0}/r{_rn:F1}, Bio Cache reach {_Rb:F0}/r{_rb:F1}, hop {_hop:F0}, tap {_dTap:F0})" +
                        (string.IsNullOrEmpty(_abort) ? "" : " ABORTED: " + _abort));
                    foreach (var s in parts) MelonLogger.Msg($"[NODELINES] {_tn}:   {s}");
                }
                catch (Exception ex) { MelonLogger.Warning($"[NODELINES] {_tn}: summary failed: {ex.Message}"); }
            }

            /// <summary>Prove the nodes are alive, linked and anchors; optionally probe a legal Bio Cache spot per tapped patch.</summary>
            public void ReadBack(string when, bool probeBioCache)
            {
                try
                {
                    _nodeLinesSpawned.TryGetValue(TeamIndex, out var list);
                    int total = list?.Count ?? 0, alive = 0, linked = 0, anchors = 0;
                    var bad = new List<string>();
                    if (list != null)
                        foreach (var s in list)
                        {
                            if (!IsAlive(s)) { bad.Add("gone"); continue; }
                            alive++;
                            var al = s as AlienStructure;
                            bool lk = al != null && al.IsLinkedToMainBuilding;
                            if (lk) linked++; else bad.Add("unlinked " + P(s.transform.position));
                            if (s.IsConstructionAnchor) anchors++;
                        }
                    string probe = "";
                    if (probeBioCache)
                    {
                        int ok = 0, tried = 0;
                        var miss = new List<string>();
                        foreach (var pt in _patches)
                        {
                            if (!pt.Tapped || !pt.HasSpot) continue;
                            tried++;
                            if (ProbeBioCache(pt, out string where)) { ok++; LogDebug($"[NODELINES] {_tn}: patch #{pt.Rank} Bio Cache placeable at {where}"); }
                            else miss.Add($"#{pt.Rank} ({where})");
                        }
                        probe = $"; Bio Cache placeable for {ok}/{tried} tapped patches" + (miss.Count > 0 ? " — not for " + string.Join(", ", miss) : "");
                    }
                    MelonLogger.Msg($"[NODELINES] {_tn} read-back {when}: {alive}/{total} alive, {linked}/{total} linked to the Nest, {anchors}/{total} anchors{probe}" +
                        (bad.Count > 0 ? " — " + string.Join(", ", bad) : ""));
                }
                catch (Exception ex) { MelonLogger.Warning($"[NODELINES] {_tn}: read-back {when} failed: {ex.Message}"); }
            }

            /// <summary>Server area test of a Bio Cache at the spot (or next to it), with a live linked anchor in range.</summary>
            private bool ProbeBioCache(Patch pt, out string where)
            {
                where = P(pt.Spot);
                string why = "no anchor in range";
                for (int ring = 0; ring <= 2; ring++)
                {
                    for (int k = 0; k < (ring == 0 ? 1 : 8); k++)
                    {
                        float a = k * 45f * Mathf.Deg2Rad, r = ring * 15f;
                        Vector3 c = pt.Spot + new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r);
                        if (!NodeLinesTryGround(_bcPreview, _bcCd, c, 0f, true, out Vector3 pos, out Quaternion rot, out string w)) { why = w; continue; }
                        foreach (var m in _members)
                        {
                            if (!IsAlive(m)) continue;
                            var al = m as AlienStructure;
                            if (al != null && !al.IsConstructionAnchor) continue;
                            float ad = AnchorDistance(_bcInfo.PhysicalBounds, pos, rot, m);
                            if (ad <= _Rb) { where = P(pos) + " from " + Label(m) + $" ({ad:F0}/{_Rb:F0})"; return true; }
                        }
                        why = "no anchor in range";
                    }
                }
                where = P(pt.Spot) + ": " + why;
                return false;
            }
        }

        // ---- setting: one code path for the chat menu (HTP 9) and "!b nodelines" ----

        private static string FmtNodeLines(int n) => n <= 0 ? "OFF" : n.ToString(CultureInfo.InvariantCulture);

        /// <summary>Set the count, persist it. Applies from the next round. False when it already had that value.</summary>
        private static bool NodeLinesSetCount(object player, int n)
        {
            n = ClampNodeLines(n);
            if (_alienStarterNodeLines == n) return false;
            int old = _alienStarterNodeLines;
            _alienStarterNodeLines = n;
            WriteNumberToJson("alien_starter_node_lines", n);
            string pn = GetPlayerName(player), sid = GetPlayerSteamId(player);
            WriteAuditLog(pn, sid, "htp", "alien_starter_node_lines", old.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture));
            MelonLogger.Msg($"[BAL] {pn} ({sid}): alien_starter_node_lines -> {n} (from the next round)");
            return true;
        }

        private static string NodeLinesStatusLine()
        {
            int laid = 0;
            foreach (var l in _nodeLinesSpawned.Values) laid += l.Count;
            string v = _alienStarterNodeLines > 0 ? "<color=#55FF55>" + _alienStarterNodeLines + "</color>" : "<color=#FF5555>OFF</color>";
            return "Alien Starter Node Lines: " + v + " · " + laid + " node(s) laid this round";
        }

        // "!b nodelines [status | off | 0-6]" — stateless, for the ModUI row and chat (AdminMod's !b power applies).
        private static void OnNodeLinesCommand(object player, string args)
        {
            string sub = (args ?? "").Trim().ToLowerInvariant();
            if (sub.Length == 0 || sub == "status")
            {
                SendChatToPlayer(player, _chatPrefix + NodeLinesStatusLine());
                SendChatToPlayer(player, _chatPrefix + _dimColor + "Finished Nodes from each Alien starter Nest to its N closest Biotics (max "
                    + _alienStarterNodeLinesMaxM.ToString("F0", CultureInfo.InvariantCulture) + " m), so a Bio Cache fits there from the start. Set: !b nodelines 0-" + NodeLinesMax + "</color>");
                return;
            }
            int n;
            if (sub == "off") n = 0;
            else if (!int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0 || n > NodeLinesMax)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Unknown option: " + sub + ". Use !b nodelines status, off or 0-" + NodeLinesMax + "</color>");
                return;
            }
            bool changed = NodeLinesSetCount(player, n);
            SendChatToPlayer(player, _chatPrefix + (changed ? "" : "Already: ") + NodeLinesStatusLine() + " " + _dimColor + "(applies from the next round; this round's lines stay as they are)</color>");
        }

        // HTP > Alien Starter Node Lines page: ".1 3" sets the count directly (no confirm step: it only acts at the next round start).
        private static void HandleNodeLinesEdit(object player, BalanceMenuState state, int paramIdx, string valueStr)
        {
            if (paramIdx != 1)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Use .1 <n> on this page.</color>");
                return;
            }
            if (!int.TryParse(valueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > NodeLinesMax)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid value: " + valueStr + " (integer 0-" + NodeLinesMax + "; 0 = off)</color>");
                return;
            }
            bool changed = NodeLinesSetCount(player, n);
            SendChatToPlayer(player, _chatPrefix + (changed ? "<color=#55FF55>Saved:</color> " : "Already: ") + NodeLinesStatusLine() + " " + _dimColor + "(applies from the next round)</color>");
        }
    }
}

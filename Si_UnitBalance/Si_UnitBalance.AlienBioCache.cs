using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Alien starter Bio Cache ("alien_starter_biocache", bool, default false)
        //
        // ConstructionData_Alien_LesserSpawningCyst.MustHaveObjects = [ObjectInfo_Alien_BioCache], checked by
        // Team.GetHasPrerequisitesForConstruction -> Team.GetHasStructure (any non-destroyed entry of
        // Team.Structures), and the client's build menu runs the same check on its own copy. A real, networked
        // Bio Cache in the team's structure list satisfies both, so with the switch on every Alien team that has
        // a starter Nest gets one finished Bio Cache spawned AlienBioCacheDepth straight below that Nest at round
        // start. It exists for the prerequisite only; on the server it is neutralised per instance:
        //  * its ResourceDepositPoints list is emptied. The Nest has no deposit point of its own and a Bio Cache's
        //    deposit point has no AI entry points (always "usable"), so otherwise every Shrimp would be sent to
        //    drop off at a point 50 m underground (Team.GetNearestResourceDepositPoint), and the vanilla AI
        //    commander would count it as a working economy (GetNumUsableResourceDepositPoints > 0) and never build
        //    its first Bio Cache. Every server-side lookup iterates that list.
        //  * Target.SetDetectable(false) (the game's own switch, used for sandbox spawn points): enemy sensors
        //    never detect it, so nothing targets it and no enemy client is told about it.
        //  * its storage (ResourceHolder, 4000) is kept: it is a Bio Cache, and the capacity then matches on
        //    server and clients. Stored resources go to the team bank when this mod removes it.
        // At most one per team is alive; base spawn / game start / !rebalance never spawn a second one in a round
        // (not even after it was destroyed); only the admin switch (off -> on) spawns a replacement. It is removed
        // when the switch goes off, on "!rebalance default", and when the starter Nest above it is gone.
        // =============================================

        private static bool _alienStarterBioCache = false;   // config "alien_starter_biocache"
        private const float AlienBioCacheDepth = 50f;        // metres straight below the starter Nest
        private const string AlienNestInfoName = "ObjectInfo_Alien_Nest";
        private const string AlienBioCacheInfoName = "ObjectInfo_Alien_BioCache";
        private const float AlienBioCacheWatchSeconds = 5f;

        private sealed class StarterCacheState
        {
            public Structure? Nest;
            public Structure? Cache;
            public bool SpawnedThisRound;
        }

        private static readonly Dictionary<int, StarterCacheState> _starterCaches = new Dictionary<int, StarterCacheState>();
        private static int _starterCacheGen; // bumped per round: stale watchers stop

        /// <summary>New round (game init / restart): forget last round's caches (the round's structures go with it).</summary>
        private static void StarterCacheResetRound()
        {
            _starterCacheGen++;
            _starterCaches.Clear();
        }

        private static bool IsAlive(Structure? s) => s != null && !s.IsDestroyed;

        private static Structure? FindStarterNest(Team team, ObjectInfo nestInfo)
        {
            var list = team.Structures;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && !s.IsDestroyed && s.ObjectInfo == nestInfo) return s;
            }
            return null;
        }

        /// <summary>A live Bio Cache of this team sitting straight below the Nest (e.g. left over from a restart).</summary>
        private static Structure? FindBuriedCache(Team team, Structure nest)
        {
            var list = team.Structures;
            if (list == null) return null;
            Vector3 np = nest.transform.position;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null || s.IsDestroyed || s.ObjectInfo == null || s.ObjectInfo.name != AlienBioCacheInfoName) continue;
                Vector3 p = s.transform.position;
                float dx = p.x - np.x, dz = p.z - np.z;
                if (dx * dx + dz * dz < 4f && p.y < np.y - AlienBioCacheDepth * 0.5f) return s;
            }
            return null;
        }

        private static ObjectInfo? FindObjectInfoByName(string name)
        {
            try
            {
                var all = GameDatabase.Database?.AllObjectData;
                if (all == null) return null;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null && all[i].name == name) return all[i];
            }
            catch { }
            return null;
        }

        /// <summary>Server-side neutralisation of the hidden cache (see the header). Returns the number of
        /// deposit points taken out of the server's lookups.</summary>
        private static int NeutraliseStarterCache(Structure cache)
        {
            int dps = 0;
            try { dps = cache.ResourceDepositPoints.Count; cache.ResourceDepositPoints.Clear(); }
            catch (Exception ex) { MelonLogger.Warning($"[ALIENCACHE] could not disable the deposit points: {ex.Message}"); }
            try { cache.Target?.SetDetectable(false); }
            catch (Exception ex) { MelonLogger.Warning($"[ALIENCACHE] could not make it undetectable: {ex.Message}"); }
            return dps;
        }

        /// <summary>Spawn the hidden cache for every Alien team that has a living starter Nest and none yet.
        /// allowRespawn: an admin switch-on may replace one already spawned (and lost) this round.</summary>
        private static void StarterCacheEnsureAll(string why, bool allowRespawn = false)
        {
            if (!_alienStarterBioCache || !_enabled) return;
            try
            {
                if (!Game.GetIsServer()) return;
                var teams = Team.Teams;
                if (teams == null) return;
                ObjectInfo? cacheInfo = null;
                for (int i = 0; i < teams.Count; i++)
                {
                    var team = teams[i];
                    if (team == null) continue;
                    ObjectInfo? bs = null;
                    try { bs = team.BaseStructure; } catch { }
                    if (bs == null || bs.name != AlienNestInfoName) continue;
                    var nest = FindStarterNest(team, bs);
                    if (nest == null) continue; // team not in play (no Nest spawned), or its Nest is gone

                    if (!_starterCaches.TryGetValue(i, out var st)) { st = new StarterCacheState(); _starterCaches[i] = st; }
                    if (IsAlive(st.Cache)) continue;                         // exactly one
                    if (st.SpawnedThisRound && !allowRespawn)
                    {
                        LogDebug($"[ALIENCACHE] {team.TeamShortName}: starter Bio Cache already spawned this round, not respawned ({why})");
                        continue;
                    }

                    var leftover = FindBuriedCache(team, nest);
                    if (leftover != null)
                    {
                        int dpl = NeutraliseStarterCache(leftover);
                        st.Nest = nest; st.Cache = leftover; st.SpawnedThisRound = true;
                        MelonLogger.Msg($"[ALIENCACHE] {team.TeamShortName}: adopted the Bio Cache already below the Nest ({why}), deposit points disabled: {dpl}");
                        MelonCoroutines.Start(StarterCacheWatch(i, _starterCacheGen));
                        continue;
                    }

                    if (cacheInfo == null) cacheInfo = FindObjectInfoByName(AlienBioCacheInfoName);
                    if (cacheInfo == null || cacheInfo.Prefab == null)
                    {
                        MelonLogger.Warning($"[ALIENCACHE] {AlienBioCacheInfoName} not found — nothing spawned ({why})");
                        return;
                    }
                    // 1.0.x spawns ObjectInfo.Prefab; 0.9.47 only knows the networked-list twin.
                    GameObject prefab = cacheInfo.Prefab;
                    if (!GameDatabase.GetPrefabSpawnable(prefab))
                    {
                        BuildNetworkedPrefabIndex();
                        var twin = NetworkedTwin(prefab);
                        if (twin != null && GameDatabase.GetPrefabSpawnable(twin)) prefab = twin;
                    }

                    Vector3 pos = nest.transform.position + Vector3.down * AlienBioCacheDepth;
                    GameObject? go = null;
                    try { go = Game.SpawnPrefab(prefab, null, team, pos, nest.transform.rotation); }
                    catch (Exception sex) { MelonLogger.Warning($"[ALIENCACHE] {team.TeamShortName}: spawn threw: {sex.Message}"); }
                    var cache = go != null ? go.GetComponent<Structure>() : null;
                    if (cache == null)
                    {
                        MelonLogger.Warning($"[ALIENCACHE] {team.TeamShortName}: spawning the starter Bio Cache failed ({why})");
                        st.SpawnedThisRound = true; // do not retry every hook this round
                        continue;
                    }
                    int dp = NeutraliseStarterCache(cache);
                    st.Nest = nest; st.Cache = cache; st.SpawnedThisRound = true;
                    int cap = 0;
                    try { cap = cache.GetResourceCapacity(team.UsableResource); } catch { }
                    MelonLogger.Msg($"[ALIENCACHE] {team.TeamShortName}: starter Bio Cache spawned {AlienBioCacheDepth:F0} m below the Nest at " +
                        $"({pos.x:F0}, {pos.y:F0}, {pos.z:F0}) ({why}) — deposit points disabled: {dp}, undetectable, storage {cap} kept");
                    MelonCoroutines.Start(StarterCacheWatch(i, _starterCacheGen));
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[ALIENCACHE] ensure ({why}) failed: {ex.Message}"); }
        }

        /// <summary>Remove the cache this mod spawned (never a player-built one): stored resources go to the
        /// team bank (StartingResources), then it is destroyed through the networked health path (the game's own
        /// "destroy" context option does the same).</summary>
        private static void StarterCacheRemove(int teamIndex, string why)
        {
            try
            {
                if (!_starterCaches.TryGetValue(teamIndex, out var st)) return;
                var cache = st.Cache;
                st.Cache = null;
                if (!IsAlive(cache)) return;
                Team? team = teamIndex >= 0 && teamIndex < Team.Teams.Count ? Team.Teams[teamIndex] : null;
                int moved = 0;
                try
                {
                    if (team != null && team.UsableResource != null)
                    {
                        int stored = cache!.GetStoredResources(team.UsableResource);
                        if (stored > 0)
                        {
                            moved = cache.RetrieveResource(team.UsableResource, stored);
                            if (moved > 0) team.StartingResources += moved;
                        }
                    }
                }
                catch (Exception rex) { MelonLogger.Warning($"[ALIENCACHE] moving the stored resources failed: {rex.Message}"); }
                try { cache!.DamageManager?.SetHealth(0f); }
                catch (Exception dex) { MelonLogger.Warning($"[ALIENCACHE] destroying the starter Bio Cache failed: {dex.Message}"); }
                MelonLogger.Msg($"[ALIENCACHE] {team?.TeamShortName ?? ("team " + teamIndex)}: starter Bio Cache removed ({why}), {moved} stored resources moved to the team bank");
            }
            catch (Exception ex) { MelonLogger.Warning($"[ALIENCACHE] remove ({why}) failed: {ex.Message}"); }
        }

        private static void StarterCacheRemoveAll(string why)
        {
            foreach (var key in new List<int>(_starterCaches.Keys)) StarterCacheRemove(key, why);
        }

        /// <summary>Every few seconds while the cache lives: gone -> stop; starter Nest gone -> remove it.</summary>
        private static IEnumerator StarterCacheWatch(int teamIndex, int gen)
        {
            while (true)
            {
                yield return new WaitForSeconds(AlienBioCacheWatchSeconds);
                if (gen != _starterCacheGen) yield break;
                if (!_starterCaches.TryGetValue(teamIndex, out var st) || st.Cache == null) yield break;
                bool cacheAlive = false, nestAlive = false;
                try { cacheAlive = IsAlive(st.Cache); nestAlive = IsAlive(st.Nest); } catch { }
                if (!cacheAlive)
                {
                    string tn = "team " + teamIndex;
                    try { tn = Team.Teams[teamIndex].TeamShortName; } catch { }
                    MelonLogger.Msg($"[ALIENCACHE] {tn}: starter Bio Cache was destroyed (not respawned this round)");
                    st.Cache = null;
                    yield break;
                }
                if (!nestAlive)
                {
                    StarterCacheRemove(teamIndex, "starter Nest destroyed");
                    yield break;
                }
            }
        }

        // ---- switch: one code path for the chat menu (HTP 8) and "!b biocache on|off" ----

        /// <summary>Set the switch, persist it, apply it to the running round. False when it already had that value.</summary>
        private static bool AlienCacheSetEnabled(object player, bool on)
        {
            if (_alienStarterBioCache == on) return false;
            _alienStarterBioCache = on;
            WriteBoolToJson("alien_starter_biocache", on);
            string pn = GetPlayerName(player), sid = GetPlayerSteamId(player);
            WriteAuditLog(pn, sid, "htp", "alien_starter_biocache", (!on).ToString(), on.ToString());
            MelonLogger.Msg($"[BAL] {pn} ({sid}): alien_starter_biocache -> {on}");
            if (on) StarterCacheEnsureAll("switched on", allowRespawn: true);
            else StarterCacheRemoveAll("switched off");
            return true;
        }

        private static string AlienCacheStatusLine()
        {
            int alive = 0;
            foreach (var st in _starterCaches.Values) if (IsAlive(st.Cache)) alive++;
            return "Alien Starter Bio Cache: " + (_alienStarterBioCache ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>")
                + " · " + alive + " in play";
        }

        // "!b biocache [status | on | off]" — stateless, for the ModUI switch row and chat (AdminMod's !b power applies).
        private static void OnAlienCacheCommand(object player, string args)
        {
            string sub = (args ?? "").Trim().ToLowerInvariant();
            if (sub.Length == 0) sub = "status";
            switch (sub)
            {
                case "status":
                    SendChatToPlayer(player, _chatPrefix + AlienCacheStatusLine());
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "A hidden Bio Cache 50 m under each Alien starter Nest: Lesser Spawning Cyst can be built from the start.</color>");
                    return;
                case "on":
                case "off":
                {
                    bool on = sub == "on";
                    bool changed = AlienCacheSetEnabled(player, on);
                    SendChatToPlayer(player, _chatPrefix + (changed ? "" : "Already: ") + AlienCacheStatusLine());
                    return;
                }
                default:
                    SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Unknown option: " + sub + ". Use !b biocache status, on or off</color>");
                    return;
            }
        }
    }
}

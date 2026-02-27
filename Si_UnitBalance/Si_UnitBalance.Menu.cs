using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Si_UnitBalance
{
    public partial class UnitBalance
    {
        // =============================================
        // Balance Editor UI — chat-based menu system (!b command)
        // =============================================

        private static readonly string[][] _factionNames = {
            new[] { "Sol" },
            new[] { "Centauri" },
            new[] { "Alien" },
        };

        // Faction -> Category names (matching in-game production buildings)
        private static readonly string[][] _categoryNames = {
            // Sol
            new[] { "Barracks", "Light Factory", "Heavy Factory", "Ultra Heavy Factory", "Air Factory", "Structures" },
            // Centauri
            new[] { "Barracks", "Light Factory", "Heavy Factory", "Ultra Heavy Factory", "Air Factory", "Structures" },
            // Alien
            new[] { "Lesser Spawning Cyst", "Greater Spawning Cyst", "Grand Spawning Cyst", "Colossal Spawning Cyst", "Nest", "Structures" },
        };

        // Faction -> Category -> Unit names (matching in-game unit tree)
        private static readonly string[][][] _unitNames = {
            // Sol
            new[] {
                new[] { "Scout", "Rifleman", "Sniper", "Heavy", "Commando" },
                new[] { "Light Quad", "Platoon Hauler", "Heavy Quad", "Light Striker", "Heavy Striker", "AA Truck" },
                new[] { "Hover Tank", "Barrage Truck", "Railgun Tank", "Pulse Truck" },
                new[] { "Harvester", "Siege Tank" },
                new[] { "Gunship", "Dropship", "Fighter", "Bomber" },
                new[] { "Headquarters", "Refinery", "Barracks", "Air Factory", "Heavy Factory", "Ultra Heavy Factory", "Turret", "Heavy Turret", "Anti-Air Rocket Turret" },
            },
            // Centauri
            new[] {
                new[] { "Militia", "Trooper", "Marksman", "Juggernaut", "Templar" },
                new[] { "Light Raider", "Squad Transport", "Heavy Raider", "Assault Car", "Strike Tank", "Flak Car" },
                new[] { "Combat Tank", "Rocket Tank", "Heavy Tank", "Pyro Tank" },
                new[] { "Harvester", "Crimson Tank" },
                new[] { "Shuttle", "Dreadnought", "Interceptor", "Freighter" },
                new[] { "Headquarters", "Refinery", "Barracks", "Air Factory", "Heavy Factory", "Ultra Heavy Factory", "Turret", "Heavy Turret", "Anti-Air Rocket Turret" },
            },
            // Alien
            new[] {
                new[] { "Crab", "Shrimp", "Shocker", "Wasp", "Dragonfly", "Squid" },
                new[] { "Horned Crab", "Hunter", "Behemoth", "Scorpion", "Firebug" },
                new[] { "Goliath" },
                new[] { "Defiler", "Colossus" },
                new[] { "Queen" },
                new[] { "Nest", "Node", "Bio Cache", "Lesser Spawning Cyst", "Greater Spawning Cyst", "Grand Spawning Cyst", "Colossal Spawning Cyst", "Quantum Cortex", "Hive Spire", "Thorn Spire" },
            },
        };

        // Parameter group names and their config keys
        private static readonly string[] _paramGroupNames = {
            "Health & Production", "Damage & Weapons", "Movement", "Vision & Sense"
        };

        private static readonly string[][] _paramGroupKeys = {
            // Health & Production
            new[] { "health_mult", "cost_mult", "build_time_mult", "min_tier", "build_radius" },
            // Damage & Weapons (legacy fallback — dynamic groups use pri_/sec_ keys)
            new[] { "damage_mult", "range_mult", "proj_speed_mult", "accuracy_mult", "magazine_mult", "fire_rate_mult", "reload_time_mult" },
            // Movement
            new[] { "move_speed_mult", "jump_speed_mult", "turbo_speed_mult", "turn_radius_mult" },
            // Vision & Sense
            new[] { "target_distance", "fow_distance", "visible_event_radius_mult" },
        };

        // Per-weapon param keys (without pri_/sec_ prefix)
        // Weapon param keys per unit type (must match gen_default_config.py)
        // Vehicles (VehicleTurret): all 7 params
        private static readonly string[] _weaponParamKeys = {
            "damage_mult", "proj_speed_mult", "proj_lifetime_mult", "accuracy_mult", "magazine_mult", "fire_rate_mult", "reload_time_mult"
        };
        // Infantry (CharacterAttachment): only 3 functional params (accuracy/magazine/fire_rate/reload not overridable)
        private static readonly string[] _weaponParamKeysInfantry = {
            "damage_mult", "proj_speed_mult", "proj_lifetime_mult"
        };
        // Creature ranged (CreatureDecapod ranged): 4 functional params (magazine/fire_rate/reload not applicable)
        private static readonly string[] _weaponParamKeysCreatureRanged = {
            "damage_mult", "proj_speed_mult", "proj_lifetime_mult", "accuracy_mult"
        };
        // Creature melee (CreatureDecapod melee): only damage
        private static readonly string[] _weaponParamKeysCreatureMelee = {
            "damage_mult"
        };

        // Per-weapon multiplier resolution: checks "pri:"/"sec:" prefixed key, falls back to shared
        private static float GetWeaponMult(Dictionary<string, float> dict, string unitName, string weapon)
        {
            if (dict.TryGetValue(weapon + ":" + unitName, out float specific))
                return specific;
            if (dict.TryGetValue(unitName, out float shared))
                return shared;
            return 1f;
        }

        private static bool HasAnyWeaponMult(Dictionary<string, float> dict, string unitName)
        {
            return dict.ContainsKey(unitName) || dict.ContainsKey("pri:" + unitName) || dict.ContainsKey("sec:" + unitName);
        }

        private static string CleanWeaponName(string pdName)
        {
            if (string.IsNullOrEmpty(pdName)) return "";
            if (pdName.StartsWith("ProjectileData_")) return pdName.Substring(15);
            if (pdName.StartsWith("Projectile_")) return pdName.Substring(11);
            return pdName;
        }

        private static void GetUnitWeaponInfo(string unitName, out bool hasPrimary, out bool hasSecondary,
            out string priName, out string secName)
        {
            hasPrimary = false;
            hasSecondary = false;
            priName = "";
            secName = "";
            try
            {
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                ObjectInfo matchedInfo = null;
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    if (info.DisplayName == unitName) { matchedInfo = info; break; }
                }
                if (matchedInfo == null) return;

                var childComps = matchedInfo.Prefab.GetComponentsInChildren<Component>(true);
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // Collect all unique ProjectileData refs across ALL VehicleTurret components.
                // Units may have multiple turrets (e.g., main gun turret + coax MG turret),
                // where each turret has its own PrimaryProjectileData.
                var foundWeapons = new List<string>(); // ProjectileData names in discovery order
                bool isVehicle = false;

                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    if (typeName == "VehicleTurret")
                    {
                        isVehicle = true;
                        var vtType = comp.GetType();
                        // Check both Primary and Secondary on each VehicleTurret
                        foreach (string projPrefix in new[] { "Primary", "Secondary" })
                        {
                            foreach (string suffix in new[] { "Projectile", "ProjectileData" })
                            {
                                var f = vtType.GetField(projPrefix + suffix, flags);
                                if (f == null) continue;
                                var pd = f.GetValue(comp);
                                if (pd == null) continue;
                                string pdName = "";
                                try { pdName = ((UnityEngine.Object)pd).name; } catch { }
                                if (!string.IsNullOrEmpty(pdName) && !foundWeapons.Contains(pdName))
                                    foundWeapons.Add(pdName);
                                break; // found for this prefix, move to next
                            }
                        }
                    }
                }

                if (isVehicle)
                {
                    if (foundWeapons.Count >= 1)
                    {
                        hasPrimary = true;
                        priName = CleanWeaponName(foundWeapons[0]);
                    }
                    if (foundWeapons.Count >= 2)
                    {
                        hasSecondary = true;
                        secName = CleanWeaponName(foundWeapons[1]);
                    }
                    return;
                }

                // Creature detection (CreatureDecapod with AttackPrimary/AttackSecondary)
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    if (comp.GetType().Name != "CreatureDecapod") continue;

                    var compType = comp.GetType();
                    var priAtkField = compType.GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                    if (priAtkField != null)
                    {
                        var atk = priAtkField.GetValue(comp);
                        if (atk != null)
                        {
                            float dmg = GetFloatMember(atk, "Damage");
                            var pdField = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                            object pd = null;
                            if (pdField != null) try { pd = pdField.GetValue(atk); } catch { }
                            if (dmg > 0 || pd != null)
                            {
                                hasPrimary = true;
                                if (pd != null) try { priName = CleanWeaponName(((UnityEngine.Object)pd).name); } catch { }
                                else priName = "Melee";
                            }
                        }
                    }
                    var secAtkField = compType.GetField("AttackSecondary", BindingFlags.Public | BindingFlags.Instance);
                    if (secAtkField != null)
                    {
                        var atk = secAtkField.GetValue(comp);
                        if (atk != null)
                        {
                            float dmg = GetFloatMember(atk, "Damage");
                            var pdField = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                            object pd = null;
                            if (pdField != null) try { pd = pdField.GetValue(atk); } catch { }
                            if (dmg > 0 || pd != null)
                            {
                                hasSecondary = true;
                                if (pd != null) try { secName = CleanWeaponName(((UnityEngine.Object)pd).name); } catch { }
                                else secName = "Melee";
                            }
                        }
                    }
                    return;
                }
            }
            catch { }
        }

        // Build dynamic param groups for a unit based on its weapons
        private static void GetUnitParamGroups(string unitName, out string[] groupNames, out string[][] groupKeys)
        {
            GetUnitWeaponInfo(unitName, out bool hasPri, out bool hasSec, out string priName, out string secName);

            // Detect component types on the prefab for dynamic param selection
            bool hasSoldier = false, hasVT = false, hasWheeled = false, hasHovered = false, hasAir = false, hasDecapod = false;
            try
            {
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                ObjectInfo matchedInfo = null;
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    if (info.DisplayName == unitName) { matchedInfo = info; break; }
                }
                if (matchedInfo != null)
                {
                    var comps = matchedInfo.Prefab.GetComponentsInChildren<Component>(true);
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        string tn = c.GetType().Name;
                        if (tn == "Soldier" || tn == "PlayerMovement" || tn == "FPSMovement") hasSoldier = true;
                        else if (tn == "VehicleTurret" || tn == "TurretWeapon") hasVT = true;
                        else if (tn == "VehicleWheeled") hasWheeled = true;
                        else if (tn == "VehicleHovered") hasHovered = true;
                        else if (tn == "VehicleAir") hasAir = true;
                        else if (tn == "CreatureDecapod") hasDecapod = true;
                    }
                }
            }
            catch { }

            bool isMobile = hasSoldier || hasWheeled || hasHovered || hasAir || hasDecapod;
            bool isStructure = !isMobile;

            var names = new List<string>();
            var keys = new List<string[]>();

            // ── Health & Production (always) ──
            names.Add("Health & Production");
            if (isStructure)
                keys.Add(new[] { "health_mult", "cost_mult", "build_time_mult", "min_tier", "build_radius" });
            else if (string.Equals(unitName, "Hover Bike", StringComparison.OrdinalIgnoreCase))
                keys.Add(new[] { "health_mult", "cost_mult", "build_time_mult", "min_tier", "dispense_timeout" });
            else
                keys.Add(new[] { "health_mult", "cost_mult", "build_time_mult", "min_tier" });

            // ── Weapons ──
            // Armed structures must be checked first: they have VT but use non-prefixed keys
            if (isStructure && hasVT)
            {
                // Armed structure: full weapon params (non-prefixed shared keys)
                string label = "Damage & Weapons";
                if (!string.IsNullOrEmpty(priName)) label += " (" + priName + ")";
                names.Add(label);
                keys.Add(new[] { "damage_mult", "proj_speed_mult", "proj_lifetime_mult", "range_mult",
                                 "accuracy_mult", "magazine_mult", "fire_rate_mult", "reload_time_mult" });
            }
            else if (hasPri && hasVT)
            {
                // Vehicle primary weapon: all 7 params
                string label = "Primary Weapon";
                if (!string.IsNullOrEmpty(priName)) label += " (" + priName + ")";
                names.Add(label);
                var wk = new string[_weaponParamKeys.Length];
                for (int i = 0; i < _weaponParamKeys.Length; i++) wk[i] = "pri_" + _weaponParamKeys[i];
                keys.Add(wk);
            }
            else if (hasPri && hasDecapod)
            {
                // Creature primary weapon: 4 params if ranged, 1 if melee
                string label = "Primary Weapon";
                if (!string.IsNullOrEmpty(priName)) label += " (" + priName + ")";
                names.Add(label);
                bool isMelee = string.Equals(priName, "Melee", StringComparison.OrdinalIgnoreCase);
                var paramSet = isMelee ? _weaponParamKeysCreatureMelee : _weaponParamKeysCreatureRanged;
                var wk = new string[paramSet.Length];
                for (int i = 0; i < paramSet.Length; i++) wk[i] = "pri_" + paramSet[i];
                keys.Add(wk);
            }
            else if (!hasPri && hasSoldier)
            {
                // Infantry primary weapon: 3 params (dmg/spd/life only)
                names.Add("Primary Weapon");
                var wk = new string[_weaponParamKeysInfantry.Length];
                for (int i = 0; i < _weaponParamKeysInfantry.Length; i++) wk[i] = "pri_" + _weaponParamKeysInfantry[i];
                keys.Add(wk);
            }

            // Secondary weapon: only for mobile units (structures don't use pri_/sec_ prefixes)
            if (!isStructure)
            {
                if (hasSec && hasVT)
                {
                    // Vehicle secondary weapon: all 7 params
                    string label = "Secondary Weapon";
                    if (!string.IsNullOrEmpty(secName)) label += " (" + secName + ")";
                    names.Add(label);
                    var wk = new string[_weaponParamKeys.Length];
                    for (int i = 0; i < _weaponParamKeys.Length; i++) wk[i] = "sec_" + _weaponParamKeys[i];
                    keys.Add(wk);
                }
                else if (hasSec && hasDecapod)
                {
                    // Creature secondary weapon: melee → 1 param (damage only)
                    string label = "Secondary Weapon";
                    if (!string.IsNullOrEmpty(secName)) label += " (" + secName + ")";
                    names.Add(label);
                    bool isMelee = string.Equals(secName, "Melee", StringComparison.OrdinalIgnoreCase);
                    var paramSet = isMelee ? _weaponParamKeysCreatureMelee : _weaponParamKeysCreatureRanged;
                    var wk = new string[paramSet.Length];
                    for (int i = 0; i < paramSet.Length; i++) wk[i] = "sec_" + paramSet[i];
                    keys.Add(wk);
                }
            }

            // ── Movement (only applicable params per component type) ──
            if (hasSoldier)
            {
                names.Add("Movement");
                keys.Add(new[] { "move_speed_mult", "jump_speed_mult" });
            }
            else if (hasWheeled)
            {
                names.Add("Movement");
                keys.Add(new[] { "move_speed_mult", "turn_radius_mult" });
            }
            else if (hasHovered || hasAir)
            {
                names.Add("Movement");
                keys.Add(new[] { "move_speed_mult", "turbo_speed_mult" });
            }
            else if (hasDecapod)
            {
                names.Add("Movement");
                keys.Add(new[] { "move_speed_mult" });
            }
            // structures: no movement group

            // ── Vision & Sense (all mobile units get target + fow + VER) ──
            if (isMobile)
            {
                names.Add("Vision & Sense");
                keys.Add(new[] { "target_distance", "fow_distance", "visible_event_radius_mult" });
            }
            // structures: no vision group

            groupNames = names.ToArray();
            groupKeys = keys.ToArray();
        }

        private static readonly string _chatPrefix = "<b><color=#DDE98C>[</color><color=#7DD4FF>BAL</color><color=#DDE98C>]</color></b> ";
        private static readonly string _headerColor = "<color=#FFD700>";
        private static readonly string _itemColor = "<color=#AAFFAA>";
        private static readonly string _valueColor = "<color=#FFB86C>";
        private static readonly string _dimColor = "<color=#888888>";
        private static string _auditLogPath = "";

        private static void SendChatToPlayer(object player, string message)
        {
            if (_sendChatToPlayerMethod == null || player == null) return;
            try
            {
                _sendChatToPlayerMethod.Invoke(null, new object[] { player, new string[] { message } });
            }
            catch { }
        }

        private static void SendConsoleToPlayer(object player, string message)
        {
            if (_sendConsoleToPlayerMethod == null || player == null) return;
            try
            {
                _sendConsoleToPlayerMethod.Invoke(null, new object[] { player, new string[] { message } });
            }
            catch { }
        }

        private static long GetPlayerKey(object player)
        {
            // Use NetworkID.m_ID (which is the SteamID ulong) as stable key
            try
            {
                if (_playerIdField != null)
                {
                    var netId = _playerIdField.GetValue(player); // NetworkID struct
                    if (netId != null)
                    {
                        // NetworkID has public ulong m_ID field
                        var midField = netId.GetType().GetField("m_ID", BindingFlags.Public | BindingFlags.Instance);
                        if (midField != null)
                        {
                            var val = midField.GetValue(netId);
                            if (val is ulong u) return (long)u;
                            if (val is long l) return l;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        private static string GetPlayerName(object player)
        {
            try
            {
                if (_playerNameField != null)
                    return _playerNameField.GetValue(player)?.ToString() ?? "Unknown";
            }
            catch { }
            return "Unknown";
        }

        private static string GetPlayerSteamId(object player)
        {
            try
            {
                if (_playerIdField != null)
                {
                    var netId = _playerIdField.GetValue(player);
                    if (netId != null)
                    {
                        var midField = netId.GetType().GetField("m_ID", BindingFlags.Public | BindingFlags.Instance);
                        if (midField != null)
                            return midField.GetValue(netId)?.ToString() ?? "0";
                    }
                }
            }
            catch { }
            return "0";
        }

        // Shortcut handler for .1-.9, .0, .back commands
        // Only active when player is in the balance menu
        private static void OnMenuShortcut(object player, string args)
        {
            long key = GetPlayerKey(player);
            if (!_menuStates.ContainsKey(key)) return; // Not in menu, silently ignore

            // args is full text like "!1" or ".1 1.5" or "!back"
            // Extract the command and optional extra args
            string text = args?.Trim() ?? "";
            // Strip prefix char (!, /, .)
            if (text.Length > 0 && (text[0] == '!' || text[0] == '/' || text[0] == '.'))
                text = text.Substring(1);

            ProcessMenuInput(player, text);
        }

        // Entry point via AdminMod: !b toggles balance editor on/off.
        // If already in menu with args, route through ProcessMenuInput.
        private static void OnBalanceCommand(object player, string args)
        {
            long key = GetPlayerKey(player);

            // Parse args after "!b": e.g. "!b exit", "!b 1", or just "!b"
            string extraArgs = "";
            if (args != null)
            {
                var parts = args.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1) extraArgs = parts[1].Trim();
            }

            // "!b exit" → close
            if (extraArgs.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                _menuStates.Remove(key);
                SendChatToPlayer(player, _chatPrefix + _dimColor + "Exited balance editor.</color>");
                return;
            }

            // Already in menu?
            if (_menuStates.ContainsKey(key))
            {
                if (string.IsNullOrEmpty(extraArgs))
                {
                    // "!b" with no args while in menu → exit
                    _menuStates.Remove(key);
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Exited balance editor.</color>");
                    return;
                }
                // "!b <something>" while in menu → route to ProcessMenuInput
                ProcessMenuInput(player, extraArgs);
                return;
            }

            // Not in menu → enter menu mode
            var state = new BalanceMenuState();
            _menuStates[key] = state;

            if (!string.IsNullOrEmpty(extraArgs))
            {
                // "!b 1" → enter and immediately select
                ProcessMenuInput(player, extraArgs);
            }
            else
            {
                SendChatToPlayer(player, _chatPrefix + _headerColor + "=== Balance Editor ===</color> " + _dimColor + "(.1 .2 .. or !b 1)</color>");
                ShowCurrentMenu(player, state);
            }
        }

        // Look up vanilla (unmodified) base values for a unit's parameters.
        // Reads from prefab components which retain vanilla values (OM stores overrides separately).
        private static Dictionary<string, string> GetBaseValues(string unitName, string[] paramKeys)
        {
            var result = new Dictionary<string, string>();
            try
            {
                var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                ObjectInfo matchedInfo = null;
                foreach (var info in allInfos)
                {
                    if (info == null || info.Prefab == null) continue;
                    if (info.DisplayName == unitName) { matchedInfo = info; break; }
                }
                if (matchedInfo == null) return result;

                var prefab = matchedInfo.Prefab;
                var childComps = prefab.GetComponentsInChildren<Component>(true);

                // Cache component lookups by type
                Component vtComp = null, sensorComp = null, soldierComp = null;
                Component wheeledComp = null, decapodComp = null, twComp = null;
                var allVtComps = new List<Component>();
                foreach (var comp in childComps)
                {
                    if (comp == null) continue;
                    string tn = comp.GetType().Name;
                    if (tn == "VehicleTurret") { allVtComps.Add(comp); if (vtComp == null) vtComp = comp; }
                    else if (tn == "TurretWeapon" && twComp == null) twComp = comp;
                    else if (tn == "Sensor" && sensorComp == null) sensorComp = comp;
                    else if ((tn == "Soldier" || tn == "PlayerMovement" || tn == "FPSMovement") && soldierComp == null) soldierComp = comp;
                    else if (tn == "VehicleWheeled" && wheeledComp == null) wheeledComp = comp;
                    else if (tn == "CreatureDecapod" && decapodComp == null) decapodComp = comp;
                }
                // Build pri/sec weapon → (VehicleTurret, fieldPrefix, ProjectileData) mapping
                // Mirrors GetUnitWeaponInfo: iterate all VTs, first unique PD = pri, second = sec
                Component priVtComp = null, secVtComp = null;
                string priVtPrefix = "Primary", secVtPrefix = "Secondary";
                object priPD = null, secPD = null;
                var seenPDNames = new HashSet<string>();
                foreach (var vt in allVtComps)
                {
                    var vtType = vt.GetType();
                    foreach (string pfx in new[] { "Primary", "Secondary" })
                    {
                        object pd = null;
                        foreach (string suffix in new[] { "Projectile", "ProjectileData" })
                        {
                            var pdF = vtType.GetField(pfx + suffix, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (pdF == null) continue;
                            try { pd = pdF.GetValue(vt); } catch { }
                            if (pd != null) break;
                        }
                        if (pd == null) continue;
                        string pdName = "";
                        try { pdName = ((UnityEngine.Object)pd).name; } catch { }
                        if (string.IsNullOrEmpty(pdName) || seenPDNames.Contains(pdName)) continue;
                        seenPDNames.Add(pdName);
                        if (priPD == null) { priPD = pd; priVtComp = vt; priVtPrefix = pfx; }
                        else if (secPD == null) { secPD = pd; secVtComp = vt; secVtPrefix = pfx; }
                    }
                }

                // Pre-cache TurretWeapon ProjectileData (for armed structures)
                object twPD = null;
                if (twComp != null)
                {
                    var twPdF = twComp.GetType().GetField("ProjectileData", BindingFlags.Public | BindingFlags.Instance);
                    if (twPdF != null) try { twPD = twPdF.GetValue(twComp); } catch { }
                }

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                foreach (string key in paramKeys)
                {
                    try
                    {
                        string val = null;
                        switch (key)
                        {
                            case "health_mult":
                            {
                                var dm = prefab.GetComponent<DamageManager>();
                                if (dm != null)
                                {
                                    var dataField = dm.GetType().GetField("Data", flags);
                                    if (dataField != null)
                                    {
                                        object dataObj = dataField.GetValue(dm);
                                        if (dataObj != null)
                                        {
                                            float hp = GetFloatMember(dataObj, "Health");
                                            if (hp <= 0) hp = GetFloatMember(dataObj, "MaxHealth");
                                            if (hp > 0) val = hp.ToString("F0");
                                        }
                                    }
                                }
                                break;
                            }
                            case "cost_mult":
                                if (matchedInfo.ConstructionData != null)
                                    val = matchedInfo.ConstructionData.ResourceCost.ToString();
                                break;
                            case "build_time_mult":
                                if (matchedInfo.ConstructionData != null)
                                    val = matchedInfo.ConstructionData.BuildUpTime.ToString("F1") + "s";
                                break;
                            case "min_tier":
                                if (matchedInfo.ConstructionData != null)
                                    val = matchedInfo.ConstructionData.MinimumTeamTier.ToString();
                                break;
                            case "build_radius":
                                if (matchedInfo.ConstructionData != null)
                                    val = matchedInfo.ConstructionData.MaximumBaseStructureDistance.ToString("F0");
                                break;
                            case "damage_mult":
                            {
                                // Show primary projectile impact damage
                                if (vtComp != null)
                                {
                                    var pdField = vtComp.GetType().GetField("PrimaryProjectile", flags);
                                    if (pdField != null)
                                    {
                                        var pd = pdField.GetValue(vtComp);
                                        if (pd != null)
                                        {
                                            float dmg = GetFloatMember(pd, "m_fImpactDamage");
                                            if (dmg > 0) val = dmg.ToString("F0");
                                        }
                                    }
                                }
                                // TurretWeapon fallback (armed structures)
                                if (val == null && twPD != null)
                                {
                                    float dmg = GetFloatMember(twPD, "m_fImpactDamage");
                                    if (dmg > 0) val = dmg.ToString("F0");
                                }
                                // Creature attack damage
                                if (val == null && decapodComp != null)
                                {
                                    var atkField = decapodComp.GetType().GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                                    if (atkField != null)
                                    {
                                        var atk = atkField.GetValue(decapodComp);
                                        if (atk != null)
                                        {
                                            float dmg = GetFloatMember(atk, "Damage");
                                            if (dmg > 0) val = dmg.ToString("F0");
                                            else
                                            {
                                                var pdField2 = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                                if (pdField2 != null)
                                                {
                                                    var pd = pdField2.GetValue(atk);
                                                    if (pd != null)
                                                    {
                                                        dmg = GetFloatMember(pd, "m_fImpactDamage");
                                                        if (dmg > 0) val = dmg.ToString("F0");
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                            }
                            case "range_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("AimDistance", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(vtComp)).ToString("F0");
                                }
                                // TurretWeapon: use Sensor.TargetingDistance as range base
                                if (val == null && twComp != null && sensorComp != null)
                                {
                                    var f = sensorComp.GetType().GetField("TargetingDistance", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(sensorComp)).ToString("F0");
                                }
                                break;
                            case "proj_speed_mult":
                                if (vtComp != null)
                                {
                                    var pdField = vtComp.GetType().GetField("PrimaryProjectile", flags);
                                    if (pdField != null)
                                    {
                                        var pd = pdField.GetValue(vtComp);
                                        if (pd != null)
                                        {
                                            float spd = GetFloatMember(pd, "m_fBaseSpeed");
                                            if (spd > 0) val = spd.ToString("F0");
                                        }
                                    }
                                }
                                if (val == null && twPD != null)
                                {
                                    float spd = GetFloatMember(twPD, "m_fBaseSpeed");
                                    if (spd > 0) val = spd.ToString("F0");
                                }
                                break;
                            case "proj_lifetime_mult":
                                if (vtComp != null)
                                {
                                    var pdField = vtComp.GetType().GetField("PrimaryProjectile", flags);
                                    if (pdField != null)
                                    {
                                        var pd = pdField.GetValue(vtComp);
                                        if (pd != null)
                                        {
                                            float lt = GetFloatMember(pd, "m_fLifeTime");
                                            if (lt > 0) val = lt.ToString("F2") + "s";
                                        }
                                    }
                                }
                                if (val == null && twPD != null)
                                {
                                    float lt = GetFloatMember(twPD, "m_fLifeTime");
                                    if (lt > 0) val = lt.ToString("F2") + "s";
                                }
                                break;
                            case "accuracy_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("PrimaryMuzzleSpread", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(vtComp)).ToString("F4");
                                }
                                if (val == null && twComp != null)
                                {
                                    float spread = GetFloatMember(twComp, "MuzzleSpread");
                                    if (spread >= 0) val = spread.ToString("F4");
                                }
                                break;
                            case "magazine_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("PrimaryMagazineSize", flags);
                                    if (f != null && f.FieldType == typeof(int))
                                        val = ((int)f.GetValue(vtComp)).ToString();
                                }
                                if (val == null && twComp != null)
                                {
                                    var f = twComp.GetType().GetField("MagazineSize", flags);
                                    if (f != null && f.FieldType == typeof(int))
                                        val = ((int)f.GetValue(twComp)).ToString();
                                    else
                                    {
                                        var p = twComp.GetType().GetProperty("MagazineSize", flags);
                                        if (p != null && p.PropertyType == typeof(int))
                                            val = ((int)p.GetValue(twComp)).ToString();
                                    }
                                }
                                break;
                            case "fire_rate_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("PrimaryFireInterval", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(vtComp)).ToString("F3") + "s";
                                }
                                if (val == null && twComp != null)
                                {
                                    float fi = GetFloatMember(twComp, "FireInterval");
                                    if (fi > 0) val = fi.ToString("F3") + "s";
                                }
                                if (val == null && decapodComp != null)
                                {
                                    var af = decapodComp.GetType().GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                                    if (af != null)
                                    {
                                        var atk = af.GetValue(decapodComp);
                                        if (atk != null)
                                        {
                                            float cd = GetFloatMember(atk, "CoolDownTime");
                                            if (cd > 0) val = cd.ToString("F3") + "s";
                                        }
                                    }
                                }
                                break;
                            case "reload_time_mult":
                                if (vtComp != null)
                                {
                                    var f = vtComp.GetType().GetField("PrimaryReloadTime", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(vtComp)).ToString("F1") + "s";
                                }
                                if (val == null && twComp != null)
                                {
                                    float rt = GetFloatMember(twComp, "ReloadTime");
                                    if (rt > 0) val = rt.ToString("F1") + "s";
                                }
                                break;
                            case "move_speed_mult":
                                foreach (var comp in childComps)
                                {
                                    if (comp == null || val != null) continue;
                                    foreach (string fn in _speedFieldNames)
                                    {
                                        if (fn == "TurboSpeed") continue;
                                        var f = comp.GetType().GetField(fn, BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                                        if (f != null && f.FieldType == typeof(float))
                                        {
                                            float v = (float)f.GetValue(comp);
                                            if (v > 0) { val = v.ToString("F1"); break; }
                                        }
                                    }
                                }
                                break;
                            case "jump_speed_mult":
                                if (soldierComp != null)
                                {
                                    var f = soldierComp.GetType().GetField("JumpSpeed", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(soldierComp)).ToString("F1");
                                }
                                break;
                            case "turbo_speed_mult":
                                foreach (var comp in childComps)
                                {
                                    if (comp == null || val != null) continue;
                                    var f = comp.GetType().GetField("TurboSpeed", BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
                                    if (f != null && f.FieldType == typeof(float))
                                    {
                                        float v = (float)f.GetValue(comp);
                                        if (v > 0) val = v.ToString("F1");
                                    }
                                }
                                break;
                            case "turn_radius_mult":
                                if (wheeledComp != null)
                                {
                                    var f = wheeledComp.GetType().GetField("TurningCircleRadius", BindingFlags.Public | BindingFlags.Instance);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(wheeledComp)).ToString("F1");
                                }
                                break;
                            case "target_distance":
                                if (sensorComp != null)
                                {
                                    var f = sensorComp.GetType().GetField("TargetingDistance", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(sensorComp)).ToString("F0");
                                }
                                break;
                            case "fow_distance":
                                if (sensorComp != null)
                                {
                                    var f = sensorComp.GetType().GetField("FogOfWarViewDistance", flags);
                                    if (f != null && f.FieldType == typeof(float))
                                        val = ((float)f.GetValue(sensorComp)).ToString("F0");
                                }
                                break;
                            case "visible_event_radius_mult":
                                // Try VehicleTurret first
                                if (vtComp != null)
                                {
                                    var pdField = vtComp.GetType().GetField("PrimaryProjectile", flags);
                                    if (pdField != null)
                                    {
                                        var pd = pdField.GetValue(vtComp);
                                        if (pd != null)
                                        {
                                            float ver = GetFloatMember(pd, "VisibleEventRadius");
                                            if (ver > 0) val = ver.ToString("F0");
                                        }
                                    }
                                }
                                // Try CreatureDecapod AttackPrimary.AttackProjectileData
                                if (val == null && decapodComp != null)
                                {
                                    var atkField = decapodComp.GetType().GetField("AttackPrimary", BindingFlags.Public | BindingFlags.Instance);
                                    if (atkField != null)
                                    {
                                        var atk = atkField.GetValue(decapodComp);
                                        if (atk != null)
                                        {
                                            var pdF = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                            if (pdF != null)
                                            {
                                                var pd = pdF.GetValue(atk);
                                                if (pd != null)
                                                {
                                                    float ver = GetFloatMember(pd, "VisibleEventRadius");
                                                    if (ver > 0) val = ver.ToString("F0");
                                                }
                                            }
                                        }
                                    }
                                }
                                // Fallback: search any component field of type ProjectileData (infantry)
                                if (val == null)
                                {
                                    foreach (var comp in childComps)
                                    {
                                        if (comp == null || val != null) continue;
                                        var compFields = comp.GetType().GetFields(flags);
                                        foreach (var cf in compFields)
                                        {
                                            if (cf.FieldType.Name != "ProjectileData") continue;
                                            object pdObj2;
                                            try { pdObj2 = cf.GetValue(comp); } catch { continue; }
                                            if (pdObj2 == null) continue;
                                            float ver = GetFloatMember(pdObj2, "VisibleEventRadius");
                                            if (ver > 0) { val = ver.ToString("F0"); break; }
                                        }
                                    }
                                }
                                break;

                            case "dispense_timeout":
                            {
                                // Find VehicleDispenser that dispenses this unit
                                foreach (var info in allInfos)
                                {
                                    if (info == null || info.Prefab == null || val != null) continue;
                                    var disComps = info.Prefab.GetComponentsInChildren<Component>(true);
                                    foreach (var dc in disComps)
                                    {
                                        if (dc == null || dc.GetType().Name != "VehicleDispenser") continue;
                                        var vtdF = dc.GetType().GetField("VehicleToDispense", BindingFlags.Public | BindingFlags.Instance);
                                        if (vtdF == null) continue;
                                        var vtdObj = vtdF.GetValue(dc);
                                        if (vtdObj == null) continue;
                                        string dn = null;
                                        var dnF = vtdObj.GetType().GetField("DisplayName", BindingFlags.Public | BindingFlags.Instance);
                                        if (dnF != null) dn = dnF.GetValue(vtdObj) as string;
                                        else { var dnP = vtdObj.GetType().GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance); if (dnP != null) dn = dnP.GetValue(vtdObj) as string; }
                                        if (!string.Equals(dn, unitName, StringComparison.OrdinalIgnoreCase)) continue;
                                        var dtF = dc.GetType().GetField("DispenseTimeout", BindingFlags.Public | BindingFlags.Instance);
                                        if (dtF != null && dtF.FieldType == typeof(float))
                                            val = ((float)dtF.GetValue(dc)).ToString("F1") + "s";
                                        else
                                        {
                                            var dtP = dc.GetType().GetProperty("DispenseTimeout", BindingFlags.Public | BindingFlags.Instance);
                                            if (dtP != null && dtP.PropertyType == typeof(float))
                                                val = ((float)dtP.GetValue(dc)).ToString("F1") + "s";
                                        }
                                    }
                                }
                                break;
                            }

                            // Per-weapon keys: pri_X or sec_X → route to correct weapon slot
                            default:
                            {
                                bool isPri = key.StartsWith("pri_");
                                bool isSec = key.StartsWith("sec_");
                                if (!isPri && !isSec) break;
                                string baseKey = key.Substring(4); // strip "pri_" or "sec_"
                                string atkField = isPri ? "AttackPrimary" : "AttackSecondary";

                                // Use pre-computed weapon mapping (correct VT + field prefix)
                                object weaponPD = isPri ? priPD : secPD;
                                Component weaponVt = isPri ? priVtComp : secVtComp;
                                string weaponVtPfx = isPri ? priVtPrefix : secVtPrefix;

                                switch (baseKey)
                                {
                                    case "damage_mult":
                                        if (weaponPD != null)
                                        {
                                            float dmg = GetFloatMember(weaponPD, "m_fImpactDamage");
                                            if (dmg > 0) val = dmg.ToString("F0");
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    float dmg = GetFloatMember(atk, "Damage");
                                                    if (dmg > 0) val = dmg.ToString("F0");
                                                    else
                                                    {
                                                        var pdF2 = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                                        if (pdF2 != null)
                                                        {
                                                            var pd = pdF2.GetValue(atk);
                                                            if (pd != null)
                                                            {
                                                                dmg = GetFloatMember(pd, "m_fImpactDamage");
                                                                if (dmg > 0) val = dmg.ToString("F0");
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case "proj_speed_mult":
                                        if (weaponPD != null)
                                        {
                                            float spd = GetFloatMember(weaponPD, "m_fBaseSpeed");
                                            if (spd > 0) val = spd.ToString("F0");
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    var pdF2 = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                                    if (pdF2 != null)
                                                    {
                                                        var pd = pdF2.GetValue(atk);
                                                        if (pd != null)
                                                        {
                                                            float spd = GetFloatMember(pd, "m_fBaseSpeed");
                                                            if (spd > 0) val = spd.ToString("F0");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case "proj_lifetime_mult":
                                        if (weaponPD != null)
                                        {
                                            float lt = GetFloatMember(weaponPD, "m_fLifeTime");
                                            if (lt > 0) val = lt.ToString("F2") + "s";
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    var pdF2 = atk.GetType().GetField("AttackProjectileData", BindingFlags.Public | BindingFlags.Instance);
                                                    if (pdF2 != null)
                                                    {
                                                        var pd = pdF2.GetValue(atk);
                                                        if (pd != null)
                                                        {
                                                            float lt = GetFloatMember(pd, "m_fLifeTime");
                                                            if (lt > 0) val = lt.ToString("F2") + "s";
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case "accuracy_mult":
                                        if (weaponVt != null)
                                        {
                                            var f = weaponVt.GetType().GetField(weaponVtPfx + "MuzzleSpread", flags);
                                            if (f != null && f.FieldType == typeof(float))
                                                val = ((float)f.GetValue(weaponVt)).ToString("F4");
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    float spread = GetFloatMember(atk, "AttackProjectileSpread");
                                                    if (spread >= 0) val = spread.ToString("F4");
                                                }
                                            }
                                        }
                                        break;
                                    case "magazine_mult":
                                        if (weaponVt != null)
                                        {
                                            var f = weaponVt.GetType().GetField(weaponVtPfx + "MagazineSize", flags);
                                            if (f != null && f.FieldType == typeof(int))
                                                val = ((int)f.GetValue(weaponVt)).ToString();
                                        }
                                        break;
                                    case "fire_rate_mult":
                                        if (weaponVt != null)
                                        {
                                            var f = weaponVt.GetType().GetField(weaponVtPfx + "FireInterval", flags);
                                            if (f != null && f.FieldType == typeof(float))
                                                val = ((float)f.GetValue(weaponVt)).ToString("F3") + "s";
                                        }
                                        if (val == null && decapodComp != null)
                                        {
                                            var af = decapodComp.GetType().GetField(atkField, BindingFlags.Public | BindingFlags.Instance);
                                            if (af != null)
                                            {
                                                var atk = af.GetValue(decapodComp);
                                                if (atk != null)
                                                {
                                                    float cd = GetFloatMember(atk, "CoolDownTime");
                                                    if (cd > 0) val = cd.ToString("F3") + "s";
                                                }
                                            }
                                        }
                                        break;
                                    case "reload_time_mult":
                                        if (weaponVt != null)
                                        {
                                            var f = weaponVt.GetType().GetField(weaponVtPfx + "ReloadTime", flags);
                                            if (f != null && f.FieldType == typeof(float))
                                                val = ((float)f.GetValue(weaponVt)).ToString("F1") + "s";
                                        }
                                        break;
                                }
                                break;
                            }
                        }
                        if (val != null)
                            result[key] = val;
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static void ShowCurrentMenu(object player, BalanceMenuState state)
        {
            switch (state.Level)
            {
                case MenuLevel.Root:
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "1.</color> Sol  " + _itemColor + "2.</color> Centauri  " + _itemColor + "3.</color> Alien  " + _itemColor + "4.</color> JSON  " + _itemColor + "5.</color> HTP");
                    break;

                case MenuLevel.Faction:
                {
                    string factionName = new[] { "Sol", "Centauri", "Alien" }[state.FactionIdx];
                    SendChatToPlayer(player, _chatPrefix + _headerColor + factionName + "</color>");
                    var cats = _categoryNames[state.FactionIdx];
                    string line = "";
                    for (int i = 0; i < cats.Length; i++)
                    {
                        if (i > 0) line += "  ";
                        line += _itemColor + (i + 1) + ".</color> " + cats[i];
                    }
                    SendChatToPlayer(player, _chatPrefix + line);
                    break;
                }

                case MenuLevel.Category:
                {
                    string factionName = new[] { "Sol", "Centauri", "Alien" }[state.FactionIdx];
                    string catName = _categoryNames[state.FactionIdx][state.CategoryIdx];
                    SendChatToPlayer(player, _chatPrefix + _headerColor + factionName + " > " + catName + "</color>");
                    var units = _unitNames[state.FactionIdx][state.CategoryIdx];
                    for (int i = 0; i < units.Length; i += 3)
                    {
                        string line = "";
                        for (int j = i; j < Math.Min(i + 3, units.Length); j++)
                        {
                            if (j > i) line += "  ";
                            line += _itemColor + (j + 1) + ".</color> " + units[j];
                        }
                        SendChatToPlayer(player, _chatPrefix + line);
                    }
                    break;
                }

                case MenuLevel.Unit:
                {
                    string unitName = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];
                    GetUnitParamGroups(unitName, out string[] dynGroupNames, out string[][] _dkeys);
                    SendChatToPlayer(player, _chatPrefix + _headerColor + unitName + "</color>");
                    for (int i = 0; i < dynGroupNames.Length; i++)
                        SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + dynGroupNames[i]);
                    break;
                }

                case MenuLevel.ParamGroup:
                {
                    string unitName = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];
                    GetUnitParamGroups(unitName, out string[] dynGroupNames, out string[][] dynGroupKeys);
                    if (state.ParamGroupIdx >= dynGroupNames.Length) { state.Level = MenuLevel.Unit; ShowCurrentMenu(player, state); return; }
                    string groupName = dynGroupNames[state.ParamGroupIdx];
                    SendChatToPlayer(player, _chatPrefix + _headerColor + unitName + " > " + groupName + "</color>");

                    var keys = dynGroupKeys[state.ParamGroupIdx];
                    var unitConfig = ReadUnitConfigFromJson(unitName);
                    var baseVals = GetBaseValues(unitName, keys);

                    for (int i = 0; i < keys.Length; i++)
                    {
                        string val = "-";
                        if (unitConfig != null && unitConfig.ContainsKey(keys[i]))
                            val = unitConfig[keys[i]].ToString();
                        string baseSuffix = "";
                        if (baseVals.TryGetValue(keys[i], out string bv))
                            baseSuffix = " " + _dimColor + "(base: " + bv + ")</color>";
                        // Strip pri_/sec_ prefix for display: "pri_damage_mult" → "damage_mult"
                        string displayKey = keys[i];
                        if (displayKey.StartsWith("pri_") || displayKey.StartsWith("sec_"))
                            displayKey = displayKey.Substring(4);
                        SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + displayKey + " = " + _valueColor + val + "</color>" + baseSuffix);
                    }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 1.5 (or !b 1 1.5)</color>");
                    break;
                }

                case MenuLevel.JsonMenu:
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "JSON Config</color>");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "1.</color> Reset to Blank " + _dimColor + "(vanilla settings)</color>");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "2.</color> Save Current Config");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "3.</color> Load Saved Config");
                    break;

                case MenuLevel.JsonLoad:
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "Load Saved Config</color>");
                    if (state.JsonLoadFileList == null || state.JsonLoadFileList.Length == 0)
                    {
                        SendChatToPlayer(player, _chatPrefix + _dimColor + "No saved configs found.</color>");
                        SendChatToPlayer(player, _chatPrefix + _dimColor + "Use .back to return.</color>");
                    }
                    else
                    {
                        for (int i = 0; i < state.JsonLoadFileList.Length; i++)
                            SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + state.JsonLoadFileList[i]);
                        SendChatToPlayer(player, _chatPrefix + _dimColor + "Pick a number to load.</color>");
                    }
                    break;
                }

                // ── HTP (Hover · Tier · Teleportation) ──────────────
                case MenuLevel.HTPMenu:
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "HTP</color> " + _dimColor + "(Hover · Tier · Teleportation)</color>");
                    string shrimpStatus = _shrimpDisableAim ? "<color=#FF5555>OFF</color>" : "<color=#55FF55>ON</color>";
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "1.</color> Hoverbike  " + _itemColor + "2.</color> Tier  " + _itemColor + "3.</color> Teleportation  " + _itemColor + "4.</color> Shrimp Aim [" + shrimpStatus + "]");
                    break;
                }

                case MenuLevel.HTPHoverbike:
                {
                    GetUnitParamGroups("Hover Bike", out string[] hbNames, out string[][] _hk);
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "HTP > Hoverbike</color>");
                    for (int i = 0; i < hbNames.Length; i++)
                        SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + hbNames[i]);
                    break;
                }

                case MenuLevel.HTPHoverbikeParam:
                {
                    GetUnitParamGroups("Hover Bike", out string[] hbNames, out string[][] hbKeys);
                    if (state.HTPParamGroupIdx >= hbNames.Length) { state.Level = MenuLevel.HTPHoverbike; ShowCurrentMenu(player, state); return; }
                    string groupName = hbNames[state.HTPParamGroupIdx];
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "Hover Bike > " + groupName + "</color>");

                    var keys = hbKeys[state.HTPParamGroupIdx];
                    var unitConfig = ReadUnitConfigFromJson("Hover Bike");
                    var baseVals = GetBaseValues("Hover Bike", keys);

                    for (int i = 0; i < keys.Length; i++)
                    {
                        string val = "-";
                        if (unitConfig != null && unitConfig.ContainsKey(keys[i]))
                            val = unitConfig[keys[i]].ToString();
                        string baseSuffix = "";
                        if (baseVals.TryGetValue(keys[i], out string bv))
                            baseSuffix = " " + _dimColor + "(base: " + bv + ")</color>";
                        string displayKey = keys[i];
                        if (displayKey.StartsWith("pri_") || displayKey.StartsWith("sec_"))
                            displayKey = displayKey.Substring(4);
                        SendChatToPlayer(player, _chatPrefix + _itemColor + (i + 1) + ".</color> " + displayKey + " = " + _valueColor + val + "</color>" + baseSuffix);
                    }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 1.5 (or !b 1 1.5)</color>");
                    break;
                }

                case MenuLevel.HTPTier:
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "HTP > Tier</color> " + _dimColor + "(tech-up time per tier, in seconds)</color>");
                    // Read current tech_time from JSON
                    JObject techTime = null;
                    try
                    {
                        if (File.Exists(_configPath))
                        {
                            var root = JObject.Parse(File.ReadAllText(_configPath));
                            techTime = root["tech_time"] as JObject;
                        }
                    }
                    catch { }

                    for (int tier = 1; tier <= 8; tier++)
                    {
                        string val = "-";
                        if (techTime != null)
                        {
                            float? t = techTime[$"tier_{tier}"]?.Value<float>();
                            if (t.HasValue) val = t.Value.ToString("F0") + "s";
                        }
                        SendChatToPlayer(player, _chatPrefix + _itemColor + tier + ".</color> Tier " + tier + " = " + _valueColor + val + "</color>" + " " + _dimColor + "(default: 30s)</color>");
                    }
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 45 (or !b 1 45)</color>");
                    break;
                }

                case MenuLevel.HTPTeleport:
                {
                    SendChatToPlayer(player, _chatPrefix + _headerColor + "HTP > Teleportation</color>");
                    // Read current teleport values from JSON (stored under units > "_teleport")
                    var tpConfig = ReadUnitConfigFromJson("_teleport");
                    string cdVal = "-", durVal = "-";
                    if (tpConfig != null)
                    {
                        if (tpConfig.ContainsKey("cooldown")) cdVal = tpConfig["cooldown"].ToString() + "s";
                        if (tpConfig.ContainsKey("duration")) durVal = tpConfig["duration"].ToString() + "s";
                    }
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "1.</color> Cooldown = " + _valueColor + cdVal + "</color> " + _dimColor + "(base: 120s)</color>");
                    SendChatToPlayer(player, _chatPrefix + _itemColor + "2.</color> Duration = " + _valueColor + durVal + "</color> " + _dimColor + "(base: 5s)</color>");
                    SendChatToPlayer(player, _chatPrefix + _dimColor + "Set: .1 90 (or !b 1 90)</color>");
                    break;
                }
            }
        }

        private static void HandleParamEdit(object player, BalanceMenuState state, int paramIdx, string valueStr)
        {
            string unitNameForGroups = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];
            GetUnitParamGroups(unitNameForGroups, out string[] _dn, out string[][] dynGroupKeys);
            if (state.ParamGroupIdx >= dynGroupKeys.Length) return;
            var keys = dynGroupKeys[state.ParamGroupIdx];
            if (paramIdx < 1 || paramIdx > keys.Length)
            {
                SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{keys.Length}.</color>");
                return;
            }

            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float newValue))
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid number: " + valueStr + "</color>");
                return;
            }

            string paramKey = keys[paramIdx - 1];
            string unitName = _unitNames[state.FactionIdx][state.CategoryIdx][state.UnitIdx];

            // Read current value
            var unitConfig = ReadUnitConfigFromJson(unitName);
            string oldVal = "-";
            if (unitConfig != null && unitConfig.ContainsKey(paramKey))
                oldVal = unitConfig[paramKey].ToString();

            string newValStr = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Store pending change and ask for confirmation
            state.PendingConfirm = true;
            state.PendingParamKey = paramKey;
            state.PendingValue = newValue;
            state.PendingOldVal = oldVal;

            SendChatToPlayer(player, _chatPrefix + _headerColor + "Confirm:</color> " + unitName + " " + paramKey + " " + _valueColor + oldVal + "</color> -> " + _valueColor + newValStr + "</color>");
            SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Save  " + _itemColor + ".2</color>=Cancel  " + _itemColor + ".3</color>=Save+Rebalance");
        }

        // ── HTP Edit Handlers ────────────────────────────────────────

        private static void HandleHTPHoverbikeEdit(object player, BalanceMenuState state, int paramIdx, string valueStr)
        {
            GetUnitParamGroups("Hover Bike", out string[] _dn, out string[][] hbKeys);
            if (state.HTPParamGroupIdx >= hbKeys.Length) return;
            var keys = hbKeys[state.HTPParamGroupIdx];
            if (paramIdx < 1 || paramIdx > keys.Length)
            {
                SendChatToPlayer(player, _chatPrefix + $"<color=#FF5555>Pick 1-{keys.Length}.</color>");
                return;
            }
            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float newValue))
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid number: " + valueStr + "</color>");
                return;
            }

            string paramKey = keys[paramIdx - 1];
            var unitConfig = ReadUnitConfigFromJson("Hover Bike");
            string oldVal = "-";
            if (unitConfig != null && unitConfig.ContainsKey(paramKey))
                oldVal = unitConfig[paramKey].ToString();

            string newValStr = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            state.PendingConfirm = true;
            state.PendingParamKey = paramKey;
            state.PendingValue = newValue;
            state.PendingOldVal = oldVal;
            state.PendingUnitName = "Hover Bike";

            SendChatToPlayer(player, _chatPrefix + _headerColor + "Confirm:</color> Hover Bike " + paramKey + " " + _valueColor + oldVal + "</color> -> " + _valueColor + newValStr + "</color>");
            SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Save  " + _itemColor + ".2</color>=Cancel  " + _itemColor + ".3</color>=Save+Rebalance");
        }

        private static void HandleHTPTierEdit(object player, BalanceMenuState state, int tierNum, string valueStr)
        {
            if (tierNum < 1 || tierNum > 8)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick tier 1-8.</color>");
                return;
            }
            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float newValue))
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid number: " + valueStr + "</color>");
                return;
            }

            string tierKey = $"tier_{tierNum}";
            // Read current value
            string oldVal = "-";
            try
            {
                if (File.Exists(_configPath))
                {
                    var root = JObject.Parse(File.ReadAllText(_configPath));
                    var tt = root["tech_time"] as JObject;
                    if (tt != null)
                    {
                        float? t = tt[tierKey]?.Value<float>();
                        if (t.HasValue) oldVal = t.Value.ToString("F0");
                    }
                }
            }
            catch { }

            string newValStr = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            state.PendingConfirm = true;
            state.PendingParamKey = tierKey;
            state.PendingValue = newValue;
            state.PendingOldVal = oldVal;
            state.PendingTechTierKey = tierKey;

            SendChatToPlayer(player, _chatPrefix + _headerColor + "Confirm:</color> Tier " + tierNum + " tech time " + _valueColor + oldVal + "</color> -> " + _valueColor + newValStr + "s</color>");
            SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Save  " + _itemColor + ".2</color>=Cancel  " + _itemColor + ".3</color>=Save+Rebalance");
        }

        private static void HandleHTPTeleportEdit(object player, BalanceMenuState state, int paramIdx, string valueStr)
        {
            if (paramIdx < 1 || paramIdx > 2)
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Pick 1-2.</color>");
                return;
            }
            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float newValue))
            {
                SendChatToPlayer(player, _chatPrefix + "<color=#FF5555>Invalid number: " + valueStr + "</color>");
                return;
            }

            string paramKey = paramIdx == 1 ? "cooldown" : "duration";
            string label = paramIdx == 1 ? "Cooldown" : "Duration";

            var tpConfig = ReadUnitConfigFromJson("_teleport");
            string oldVal = "-";
            if (tpConfig != null && tpConfig.ContainsKey(paramKey))
                oldVal = tpConfig[paramKey].ToString();

            string newValStr = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            state.PendingConfirm = true;
            state.PendingParamKey = paramKey;
            state.PendingValue = newValue;
            state.PendingOldVal = oldVal;
            state.PendingUnitName = "_teleport";

            SendChatToPlayer(player, _chatPrefix + _headerColor + "Confirm:</color> Teleport " + label + " " + _valueColor + oldVal + "</color> -> " + _valueColor + newValStr + "s</color>");
            SendChatToPlayer(player, _chatPrefix + _itemColor + ".1</color>=Save  " + _itemColor + ".2</color>=Cancel  " + _itemColor + ".3</color>=Save+Rebalance");
        }

        private static bool WriteTechTierToJson(string tierKey, float value)
        {
            try
            {
                if (!File.Exists(_configPath)) return false;
                string json = File.ReadAllText(_configPath);
                var root = JObject.Parse(json);
                var tt = root["tech_time"] as JObject;
                if (tt == null)
                {
                    tt = new JObject();
                    root["tech_time"] = tt;
                }
                tt[tierKey] = (int)Math.Round(value);
                File.WriteAllText(_configPath, root.ToString());
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"WriteTechTierToJson: {ex.Message}");
                return false;
            }
        }

        private static bool WriteBoolToJson(string key, bool value)
        {
            try
            {
                if (!File.Exists(_configPath)) return false;
                string json = File.ReadAllText(_configPath);
                var root = JObject.Parse(json);
                root[key] = value;
                File.WriteAllText(_configPath, root.ToString());
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"WriteBoolToJson: {ex.Message}");
                return false;
            }
        }

        // Read a unit's config values from JSON on disk
        private static JObject ReadUnitConfigFromJson(string unitName)
        {
            try
            {
                if (!File.Exists(_configPath)) return null;
                string json = File.ReadAllText(_configPath);
                var root = JObject.Parse(json);
                var units = root["units"] as JObject;
                if (units == null) return null;
                return units[unitName] as JObject;
            }
            catch { return null; }
        }

        // Write a parameter value into the JSON config file using regex-based modification
        // (avoids Newtonsoft.Json write methods which are missing on the server)
        private static bool WriteParamToJson(string unitName, string paramKey, float value)
        {
            try
            {
                if (!File.Exists(_configPath)) return false;
                string json = File.ReadAllText(_configPath);

                string valueStr;
                if (paramKey == "min_tier" || paramKey == "build_radius" || paramKey == "target_distance" || paramKey == "fow_distance")
                    valueStr = ((int)value).ToString();
                else
                    valueStr = Math.Round(value, 4).ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Find the unit block in JSON and update/insert the param
                // Strategy: find "UnitName": { ... } block, then find/insert the key
                var root = JObject.Parse(json);
                var units = root["units"] as JObject;
                if (units == null) return false;
                var unitObj = units[unitName] as JObject;

                // Check if unit block exists in the raw JSON
                // Use string-based approach: find the unit, modify/add the key
                int unitStart = FindJsonKey(json, unitName);
                if (unitStart < 0)
                {
                    // Unit doesn't exist — insert before closing "}" of "units" block
                    int unitsEnd = FindClosingBrace(json, FindJsonKey(json, "units"));
                    if (unitsEnd < 0) return false;
                    // Insert new unit entry before the closing brace
                    string indent = "        ";
                    string newEntry = ",\n" + indent + "\"" + EscapeJsonString(unitName) + "\": { " +
                        "\"" + EscapeJsonString(paramKey) + "\": " + valueStr +
                        ", \"_note\": \"modified via !b\" }";
                    json = json.Insert(unitsEnd, newEntry);
                }
                else
                {
                    // Unit exists — find its object block
                    int braceStart = json.IndexOf('{', unitStart);
                    if (braceStart < 0) return false;
                    int braceEnd = FindClosingBrace(json, braceStart);
                    if (braceEnd < 0) return false;

                    string unitBlock = json.Substring(braceStart, braceEnd - braceStart + 1);

                    // Check if key already exists
                    int keyPos = FindJsonKey(unitBlock, paramKey);
                    if (keyPos >= 0)
                    {
                        // Replace existing value
                        int colonPos = unitBlock.IndexOf(':', keyPos);
                        if (colonPos < 0) return false;
                        // Find start and end of the value
                        int valStart = colonPos + 1;
                        while (valStart < unitBlock.Length && unitBlock[valStart] == ' ') valStart++;
                        int valEnd = valStart;
                        // Value ends at , or } or whitespace-then-comma
                        bool inString = false;
                        if (valEnd < unitBlock.Length && unitBlock[valEnd] == '"') inString = true;
                        while (valEnd < unitBlock.Length)
                        {
                            char c = unitBlock[valEnd];
                            if (inString)
                            {
                                if (c == '"' && valEnd > valStart) { valEnd++; break; }
                            }
                            else
                            {
                                if (c == ',' || c == '}' || c == '\n') break;
                            }
                            valEnd++;
                        }
                        string oldValRaw = unitBlock.Substring(valStart, valEnd - valStart).TrimEnd();
                        unitBlock = unitBlock.Substring(0, valStart) + " " + valueStr +
                                    unitBlock.Substring(valStart + oldValRaw.Length);
                    }
                    else
                    {
                        // Insert new key before closing brace
                        int lastBrace = unitBlock.LastIndexOf('}');
                        string insertion = ", \"" + EscapeJsonString(paramKey) + "\": " + valueStr;
                        unitBlock = unitBlock.Insert(lastBrace, insertion);
                    }

                    // Also update/insert _note
                    int notePos = FindJsonKey(unitBlock, "_note");
                    if (notePos >= 0)
                    {
                        int colonPos = unitBlock.IndexOf(':', notePos);
                        if (colonPos >= 0)
                        {
                            int valStart = colonPos + 1;
                            while (valStart < unitBlock.Length && unitBlock[valStart] == ' ') valStart++;
                            int valEnd = valStart;
                            if (valEnd < unitBlock.Length && unitBlock[valEnd] == '"')
                            {
                                valEnd++;
                                while (valEnd < unitBlock.Length && unitBlock[valEnd] != '"') valEnd++;
                                if (valEnd < unitBlock.Length) valEnd++;
                            }
                            unitBlock = unitBlock.Substring(0, valStart) + " \"modified via !b\"" +
                                        unitBlock.Substring(valEnd);
                        }
                    }

                    json = json.Substring(0, braceStart) + unitBlock + json.Substring(braceEnd + 1);
                }

                File.WriteAllText(_configPath, json);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BAL] Failed to write JSON: {ex.Message}");
                return false;
            }
        }

        // Find the position of a JSON key ("keyName":) in a string, returns index of the opening quote
        private static int FindJsonKey(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int pos = 0;
            while (pos < json.Length)
            {
                int found = json.IndexOf(pattern, pos, StringComparison.Ordinal);
                if (found < 0) return -1;
                // Verify it's followed by : (with optional whitespace)
                int afterQuote = found + pattern.Length;
                while (afterQuote < json.Length && (json[afterQuote] == ' ' || json[afterQuote] == '\t')) afterQuote++;
                if (afterQuote < json.Length && json[afterQuote] == ':')
                    return found;
                pos = found + 1;
            }
            return -1;
        }

        // Find the matching closing brace for an opening brace at position pos
        private static int FindClosingBrace(string json, int startPos)
        {
            if (startPos < 0) return -1;
            int bracePos = json.IndexOf('{', startPos);
            if (bracePos < 0) return -1;
            int depth = 0;
            bool inStr = false;
            for (int i = bracePos; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static string EscapeJsonString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void WriteAuditLog(string playerName, string steamId, string unitName, string paramKey, string oldVal, string newVal)
        {
            try
            {
                if (string.IsNullOrEmpty(_auditLogPath))
                    _auditLogPath = Path.Combine(Path.GetDirectoryName(_configPath)!, "Si_UnitBalance_Audit.log");
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {playerName} ({steamId}): {unitName} {paramKey} {oldVal} -> {newVal}";
                File.AppendAllText(_auditLogPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}

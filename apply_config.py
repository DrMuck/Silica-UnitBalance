import json

CFG_PATH = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\UnitBalance_cfg\Si_UnitBalance_Config.json"

with open(CFG_PATH, "r") as f:
    cfg = json.load(f)

# Global: tech_time
cfg["tech_time"]["tier_1"] = 29
cfg["tech_time"]["tier_2"] = 29
cfg["tech_time"]["tier_3"] = 29
cfg["tech_time"]["tier_4"] = 29
cfg["tech_time"]["tier_5"] = 60
cfg["tech_time"]["tier_6"] = 60
cfg["tech_time"]["tier_7"] = 90
cfg["tech_time"]["tier_8"] = 120

# Global: additional_spawn
cfg["additional_spawn"] = True

# Decay: per-faction structure decay settings
# enabled=True means decay is active (vanilla), False disables it
# delay/tick/amount_pct/randomize_pct: -1 = vanilla, any positive value = override
if "decay" not in cfg:
    cfg["decay"] = {}
cfg["decay"]["human"] = {"enabled": True, "delay": -1, "tick": -1, "amount_pct": -1, "randomize_pct": -1, "keep_production": False}
cfg["decay"]["alien"] = {"enabled": True, "delay": -1, "tick": -1, "amount_pct": -1, "randomize_pct": -1, "keep_production": False}

# Teleport
cfg["units"]["_teleport"]["cooldown"] = 180

u = cfg["units"]

def ensure(name):
    if name not in u:
        u[name] = {}

# HQ
for hq in ["Sol Headquarters", "Cent Headquarters"]:
    u[hq]["cost_mult"] = 1.5
    u[hq]["min_tier"] = 4
    u[hq]["fow_distance"] = 700
u["Sol Headquarters"]["build_radius"] = 1520

# Structures
u["Air Factory"]["cost_mult"] = 1.5
u["Heavy Factory"]["cost_mult"] = 1.5
u["Ultra Heavy Factory"]["cost_mult"] = 1.5
u["Ultra Heavy Factory"]["min_tier"] = 6
u["Radar Station"]["build_radius"] = 800
u["Radar Station"]["fow_distance"] = 800
u["Turret"]["build_time_mult"] = 2
u["Heavy Turret"]["build_time_mult"] = 2
u["Anti-Air Rocket Turret"]["build_time_mult"] = 2

# Sol Barracks
u["Heavy"]["pri_proj_speed_mult"] = 1.3
u["Heavy"]["pri_proj_lifetime_mult"] = 0.78
u["Commando"]["pri_impact_damage_mult"] = 1.05
u["Commando"]["pri_proj_speed_mult"] = 1.15
u["Commando"]["pri_proj_lifetime_mult"] = 0.9
u["Scout"]["build_time_mult"] = 1.5
u["Rifleman"]["build_time_mult"] = 1.5

# Sol Light Factory
u["Platoon Hauler"]["move_speed_mult"] = 1.8
u["Platoon Hauler"]["turn_radius_mult"] = 1.2
u["Platoon Hauler"]["pri_accuracy_mult"] = 0.5
u["Light Striker"]["pri_accuracy_mult"] = 0.8
u["Light Striker"]["pri_magazine_mult"] = 4
u["Light Striker"]["pri_fire_rate_mult"] = 1.5
u["Light Striker"]["pri_proj_speed_mult"] = 1.3
u["Light Striker"]["pri_proj_lifetime_mult"] = 0.85
u["Heavy Striker"]["pri_impact_damage_mult"] = 1.1
u["Heavy Striker"]["pri_proj_speed_mult"] = 1.1
u["AA Truck"]["pri_proj_speed_mult"] = 1.3
u["AA Truck"]["move_speed_mult"] = 1.1

# Sol Heavy Factory
u["Hover Tank"]["pri_impact_damage_mult"] = 1.1
u["Barrage Truck"]["cost_mult"] = 0.8
u["Barrage Truck"]["min_tier"] = 7
u["Barrage Truck"]["pri_proj_speed_mult"] = 2
u["Barrage Truck"]["pri_proj_lifetime_mult"] = 0.8
u["Barrage Truck"]["pri_accuracy_mult"] = 0.7
u["Railgun Tank"]["pri_reload_time_mult"] = 0.75
u["Pulse Truck"]["health_mult"] = 1.3
u["Pulse Truck"]["min_tier"] = 5
u["Pulse Truck"]["move_speed_mult"] = 1.2

# Sol Ultra Heavy
u["Siege Tank"]["cost_mult"] = 1.5
u["Siege Tank"]["pri_ricochet_damage_mult"] = 30
u["Siege Tank"]["pri_proj_speed_mult"] = 1.3
u["Siege Tank"]["pri_proj_lifetime_mult"] = 0.9

# Sol Air Factory
u["Gunship"]["min_tier"] = 0
u["Gunship"]["move_speed_mult"] = 1.2
u["Gunship"]["turbo_speed_mult"] = 1.2
u["Gunship"]["strafe_speed_mult"] = 1.2
u["Dropship"]["health_mult"] = 2
u["Dropship"]["min_tier"] = 0
u["Dropship"]["pri_impact_damage_mult"] = 0.6
u["Dropship"]["pri_ricochet_damage_mult"] = 0.6
u["Dropship"]["move_speed_mult"] = 2
u["Fighter"]["pri_proj_lifetime_mult"] = 2
u["Fighter"]["move_speed_mult"] = 1.2
u["Fighter"]["turbo_speed_mult"] = 1.2
u["Fighter"]["strafe_speed_mult"] = 1.2

# Centauri Barracks
u["Marksman"]["pri_proj_speed_mult"] = 1.2
u["Marksman"]["pri_proj_lifetime_mult"] = 0.8
u["Militia"]["build_time_mult"] = 1.5
u["Trooper"]["build_time_mult"] = 1.5

# Centauri Light Factory
u["Squad Transport"]["move_speed_mult"] = 2.1
u["Squad Transport"]["turn_radius_mult"] = 1.2
u["Strike Tank"]["pri_proj_speed_mult"] = 1.2
u["Strike Tank"]["pri_proj_lifetime_mult"] = 0.8
u["Strike Tank"]["pri_accuracy_mult"] = 0.8
u["Flak Car"]["pri_proj_speed_mult"] = 2
# Reset Flak Car lifetime to 1 (user spec only mentions proj_speed change)
u["Flak Car"]["pri_proj_lifetime_mult"] = 1

# Centauri Heavy Factory
u["Combat Tank"]["pri_proj_speed_mult"] = 1.15
u["Combat Tank"]["pri_proj_lifetime_mult"] = 0.95
u["Rocket Tank"]["min_tier"] = 7
u["Heavy Tank"]["pri_proj_speed_mult"] = 1.25
u["Heavy Tank"]["pri_proj_lifetime_mult"] = 0.9
u["Heavy Tank"]["sec_accuracy_mult"] = 0.5

ensure("Pyro Tank")
u["Pyro Tank"]["health_mult"] = 1.3
u["Pyro Tank"]["min_tier"] = 5
u["Pyro Tank"]["pri_proj_speed_mult"] = 2
u["Pyro Tank"]["pri_proj_lifetime_mult"] = 0.8
u["Pyro Tank"]["move_speed_mult"] = 1.5

# Centauri Ultra Heavy
ensure("Crimson Tank")
u["Crimson Tank"]["cost_mult"] = 1.5
u["Crimson Tank"]["pri_ricochet_damage_mult"] = 5
u["Crimson Tank"]["pri_proj_speed_mult"] = 1.25

# Centauri Air Factory
ensure("Shuttle")
u["Shuttle"]["health_mult"] = 2
u["Shuttle"]["pri_impact_damage_mult"] = 0.6
u["Shuttle"]["pri_splash_damage_mult"] = 0.6
u["Shuttle"]["move_speed_mult"] = 2

ensure("Dreadnought")
u["Dreadnought"]["health_mult"] = 1.5
u["Dreadnought"]["cost_mult"] = 0.9
u["Dreadnought"]["min_tier"] = 7
u["Dreadnought"]["sec_proj_speed_mult"] = 2
u["Dreadnought"]["sec_proj_lifetime_mult"] = 0.6
u["Dreadnought"]["strafe_speed_mult"] = 1.8

ensure("Interceptor")
u["Interceptor"]["cost_mult"] = 0.75
u["Interceptor"]["min_tier"] = 0
u["Interceptor"]["pri_proj_speed_mult"] = 1.4
u["Interceptor"]["pri_proj_lifetime_mult"] = 1.15
u["Interceptor"]["pri_accuracy_mult"] = 0.6
u["Interceptor"]["pri_magazine_mult"] = 3
u["Interceptor"]["move_speed_mult"] = 1.4
u["Interceptor"]["turbo_speed_mult"] = 1.4
u["Interceptor"]["strafe_speed_mult"] = 1.3
u["Interceptor"]["pri_fire_rate_mult"] = 1.1
u["Interceptor"]["health_mult"] = 1.7
u["Interceptor"]["pri_splash_radius_max_mult"] = 10

ensure("Freighter")
u["Freighter"]["cost_mult"] = 1.1

# Alien Structures
ensure("Nest")
u["Nest"]["min_tier"] = 4
u["Nest"]["build_radius"] = 350
u["Nest"]["fow_distance"] = 600

ensure("Node")
u["Node"]["health_mult"] = 1.5
u["Node"]["build_time_mult"] = 3.5
u["Node"]["build_radius"] = 150
u["Node"]["fow_distance"] = 150
u["Node"]["cost_mult"] = 1.5

ensure("Bio Cache")
u["Bio Cache"]["build_radius"] = 300
u["Bio Cache"]["build_time_mult"] = 3

ensure("Lesser Spawning Cyst")
u["Lesser Spawning Cyst"]["build_radius"] = 150

ensure("Greater Spawning Cyst")
u["Greater Spawning Cyst"]["min_tier"] = 1
u["Greater Spawning Cyst"]["build_radius"] = 150

ensure("Grand Spawning Cyst")
u["Grand Spawning Cyst"]["build_radius"] = 150

ensure("Colossal Spawning Cyst")
u["Colossal Spawning Cyst"]["cost_mult"] = 1.5
u["Colossal Spawning Cyst"]["build_radius"] = 200

ensure("Hive Spire")
u["Hive Spire"]["proj_speed_mult"] = 1.3
u["Hive Spire"]["proj_lifetime_mult"] = 0.85

ensure("Thorn Spire")
u["Thorn Spire"]["accuracy_mult"] = 0.6

# Alien Lesser Spawning Cyst units
ensure("Crab")
u["Crab"]["cost_mult"] = 0.4
u["Crab"]["build_time_mult"] = 0.3

ensure("Shocker")
u["Shocker"]["pri_impact_damage_mult"] = 0.9
u["Shocker"]["move_speed_mult"] = 1.1

ensure("Dragonfly")
u["Dragonfly"]["health_mult"] = 1.1
u["Dragonfly"]["min_tier"] = 6
u["Dragonfly"]["pri_proj_speed_mult"] = 1.4
u["Dragonfly"]["pri_proj_lifetime_mult"] = 0.95
u["Dragonfly"]["pri_accuracy_mult"] = 0.8
u["Dragonfly"]["strafe_speed_mult"] = 5

ensure("Squid")
u["Squid"]["min_tier"] = 5
u["Squid"]["sec_damage_mult"] = 0.6
u["Squid"]["move_speed_mult"] = 1.05
u["Squid"]["fly_speed_mult"] = 1.05

ensure("Wasp")
u["Wasp"]["fly_speed_mult"] = 1.05

# Alien Greater Spawning Cyst
ensure("Horned Crab")
u["Horned Crab"]["health_mult"] = 1.4
u["Horned Crab"]["cost_mult"] = 0.5
u["Horned Crab"]["build_time_mult"] = 0.5

ensure("Hunter")
u["Hunter"]["move_speed_mult"] = 1.1

ensure("Behemoth")
u["Behemoth"]["pri_proj_speed_mult"] = 1.25
u["Behemoth"]["pri_accuracy_mult"] = 0.5

ensure("Scorpion")
u["Scorpion"]["health_mult"] = 1.2
u["Scorpion"]["pri_proj_speed_mult"] = 1.1

ensure("Firebug")
u["Firebug"]["fly_speed_mult"] = 1.4
u["Firebug"]["strafe_speed_mult"] = 1.5
u["Firebug"]["pri_proj_speed_mult"] = 2

# Alien Colossal
ensure("Defiler")
u["Defiler"]["health_mult"] = 1.6
u["Defiler"]["pri_proj_speed_mult"] = 2
u["Defiler"]["pri_proj_lifetime_mult"] = 0.6
u["Defiler"]["fly_speed_mult"] = 1.5
u["Defiler"]["strafe_speed_mult"] = 1.5

ensure("Colossus")
u["Colossus"]["pri_ricochet_damage_mult"] = 100

# Alien Nest
ensure("Queen")
u["Queen"]["health_mult"] = 1.2
u["Queen"]["pri_proj_speed_mult"] = 2
u["Queen"]["move_speed_mult"] = 2
u["Queen"]["strafe_speed_mult"] = 2

# Hover Bike
u["Hover Bike"]["min_tier"] = 2

# Alien Grand Spawning Cyst
ensure("Goliath")
u["Goliath"]["pri_damage_mult"] = 0.75
u["Goliath"]["min_tier"] = 5

with open(CFG_PATH, "w") as f:
    json.dump(cfg, f, indent=2)

print("Config updated successfully")

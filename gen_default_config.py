import json

data = json.load(open('C:/Users/schwe/Projects/Si_UnitBalance/dumps/Si_UnitBalance_Dump_2026-03-04_v4_multiturret.json'))
units_list = data['units']

by_name = {}
for u in units_list:
    name = u['name']
    if name not in by_name:
        by_name[name] = u
    elif u.get('faction') == 'Sol' and by_name[name].get('faction') != 'Sol':
        by_name[name] = u

# =============================================================================
# PROJECTILE DATA — from Si_UnitBalance_Dump.json + unit_data_reference.md
# Format: dict with keys: impact, ricochet, splash, pen, speed, lifetime
#   splash_r_max, splash_r_min, splash_r_pow (splash radius fields, only if splash)
# Only non-zero damage sub-types are listed; missing = 0
# splash_r_min defaults to 1, splash_r_pow defaults to 3 (game defaults)
# =============================================================================
projectile_db = {
    # Alien
    'ProjectileData_Acidball':    {'impact': 400, 'splash': 400, 'splash_r_max': 6, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 240, 'life': 15},
    'ProjectileData_Behemoth':    {'impact': 150, 'speed': 80, 'life': 10},
    'ProjectileData_BioHive':     {'impact': 150, 'speed': 80, 'life': 6.875},
    'ProjectileData_BioTurret':   {'impact': 300, 'speed': 250, 'life': 7},
    'ProjectileData_Colossus':    {'impact': 70000, 'ricochet': 500, 'splash': 50000, 'splash_r_max': 80, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 500, 'life': 1.5},
    'ProjectileData_Dragonfly':   {'impact': 120, 'ricochet': 30, 'splash': 150, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 400, 'life': 1},
    'ProjectileData_Firebug':     {'impact': 1000, 'splash': 500, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 60, 'life': 10},
    'ProjectileData_Queen':       {'impact': 1000, 'splash': 500, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 90, 'life': 10},
    'ProjectileData_Ray':         {'impact': 400, 'ricochet': 400, 'splash': 600, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 400, 'life': 1},  # instant-hit
    'ProjectileData_Scorpion':    {'impact': 120, 'speed': 250, 'life': 10},
    'ProjectileData_Shard':       {'impact': 400, 'speed': 200, 'life': 2},   # Shrimp
    'ProjectileData_Shocker':     {'impact': 250, 'ricochet': 30, 'splash': 250, 'splash_r_max': 2, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 800, 'life': 0.375},
    'ProjectileData_SquidExplode':{'splash': 3500, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 1, 'life': 0.5},
    'ProjectileData_Swarm':       {'impact': 200, 'ricochet': 150, 'splash': 150, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 80, 'life': 7.5},
    # Infantry (impact only)
    'ProjectileData_BalteriumRifle': {'impact': 70, 'speed': 500, 'life': 5},
    'ProjectileData_Bullpup':       {'impact': 36, 'speed': 450, 'life': 3},
    'ProjectileData_MarksmanRifle': {'impact': 300, 'splash': 100, 'splash_r_max': 4, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 600, 'life': 5},
    'ProjectileData_Minigun':       {'impact': 55, 'speed': 500, 'life': 3},
    'ProjectileData_RGXRifle':      {'impact': 55, 'speed': 500, 'life': 3},
    'ProjectileData_Rifle':         {'impact': 45, 'speed': 500, 'life': 3},
    'ProjectileData_SMG':           {'impact': 27, 'speed': 350, 'life': 3},
    'ProjectileData_SMG2':          {'impact': 85, 'speed': 450, 'life': 3},
    'ProjectileData_SniperRifle':   {'impact': 700, 'speed': 800, 'life': 5},
    # Vehicle LMG/MMG
    'ProjectileData_LMG_LightQuad':        {'impact': 50, 'ricochet': 8, 'speed': 400, 'life': 3},
    'ProjectileData_LMG_LightArmoredCar':  {'impact': 75, 'ricochet': 25, 'speed': 500, 'life': 5},
    'ProjectileData_LMG_ChainMG':          {'impact': 40, 'speed': 350, 'life': 3},
    'ProjectileData_LMG_CrimsonFreighter': {'impact': 32, 'speed': 350, 'life': 3},
    'ProjectileData_MMG_HoverTank':        {'impact': 50, 'ricochet': 10, 'speed': 400, 'life': 3},
    'ProjectileData_MMG_TroopHauler':      {'impact': 50, 'ricochet': 10, 'speed': 400, 'life': 3},
    'ProjectileData_MMG_HeavyQuad2':       {'impact': 80, 'ricochet': 18, 'speed': 400, 'life': 3},
    'ProjectileData_MMG_LightQuad2':       {'impact': 70, 'ricochet': 14, 'speed': 400, 'life': 3},
    'ProjectileData_MMG_HeavyArmoredCar':  {'impact': 50, 'ricochet': 10, 'speed': 400, 'life': 3},
    'ProjectileData_SG_CombatTank':        {'impact': 100, 'ricochet': 40, 'speed': 300, 'life': 1},
    # Vehicle HMG
    'ProjectileData_HMG_HeavyQuad':        {'impact': 70, 'ricochet': 20, 'speed': 550, 'life': 5},
    'ProjectileData_HMG_Gunship':          {'impact': 160, 'ricochet': 60, 'splash': 80, 'splash_r_max': 3, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 400, 'life': 2.5},
    'ProjectileData_HMG_BomberCraft':      {'impact': 80, 'splash': 50, 'splash_r_max': 3, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 860, 'life': 5},
    'ProjectileData_HMG_HeavyTank':        {'impact': 80, 'ricochet': 20, 'splash': 50, 'splash_r_max': 1.5, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 800, 'life': 5},
    'ProjectileData_HMG_LightArmoredCar2': {'impact': 200, 'ricochet': 60, 'splash': 100, 'splash_r_max': 3, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 400, 'life': 2.5},
    'ProjectileData_HMG_OldHeavy':         {'impact': 80, 'speed': 350, 'life': 5},
    'ProjectileData_HMG_StealthDropship':  {'impact': 300, 'ricochet': 30, 'speed': 500, 'life': 2},
    'ProjectileData_HMG_StealthFighter':   {'impact': 100, 'ricochet': 30, 'speed': 1600, 'life': 0.156},
    'ProjectileData_HMG_ArmedTransport':   {'impact': 70, 'ricochet': 20, 'speed': 400, 'life': 5},
    'ProjectileData_HMG_TurretMedium':     {'impact': 160, 'speed': 550, 'life': 5},
    # Shells
    'ProjectileData_Shell_HoverTank':       {'impact': 1000, 'ricochet': 500, 'splash': 600, 'splash_r_max': 15, 'splash_r_min': 1, 'splash_r_pow': 3, 'pen': 5000, 'speed': 500, 'life': 5},
    'ProjectileData_Shell_CombatTank':      {'impact': 1500, 'ricochet': 500, 'splash': 3000, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 350, 'life': 5.71},
    'ProjectileData_Shell_CrimsonTank':     {'impact': 15000, 'ricochet': 3000, 'splash': 5000, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 250, 'life': 6.5},
    'ProjectileData_Shell_HeavyTank':       {'impact': 15000, 'ricochet': 3000, 'splash': 4000, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 300, 'life': 8},
    'ProjectileData_Shell_StrikeTank':      {'impact': 700, 'ricochet': 500, 'splash': 1000, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 300, 'life': 6.7},
    'ProjectileData_Shell_HeavyArmoredCar': {'impact': 800, 'ricochet': 400, 'splash': 500, 'splash_r_max': 15, 'splash_r_min': 1, 'splash_r_pow': 3, 'pen': 4500, 'speed': 400, 'life': 5},
    'ProjectileData_Shell_LightArmoredCar': {'impact': 350, 'ricochet': 120, 'splash': 220, 'splash_r_max': 4, 'splash_r_min': 1, 'splash_r_pow': 3, 'pen': 220, 'speed': 500, 'life': 5},
    'ProjectileData_Shell_ShuttleCannon':   {'impact': 350, 'ricochet': 120, 'splash': 220, 'splash_r_max': 4, 'splash_r_min': 1, 'splash_r_pow': 3, 'pen': 220, 'speed': 500, 'life': 5},
    'ProjectileData_Shell_Dreadnought':     {'impact': 1000, 'ricochet': 200, 'splash': 600, 'splash_r_max': 15, 'splash_r_min': 1, 'splash_r_pow': 3, 'pen': 1600, 'speed': 100, 'life': 5},
    'ProjectileData_Shell_DreadnoughtCannon': {'impact': 350, 'ricochet': 120, 'splash': 220, 'splash_r_max': 4, 'splash_r_min': 1, 'splash_r_pow': 3, 'pen': 220, 'speed': 500, 'life': 5},
    'ProjectileData_Shell_Interceptor':     {'impact': 350, 'ricochet': 120, 'splash': 220, 'splash_r_max': 4, 'splash_r_min': 1, 'splash_r_pow': 3, 'pen': 220, 'speed': 350, 'life': 1},
    'ProjectileData_Shell_StealthBomber':   {'impact': 1600, 'ricochet': 250, 'splash': 1000, 'splash_r_max': 8, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 400, 'life': 5},
    'ProjectileData_Shell_Turret_Heavy':    {'impact': 1600, 'speed': 200, 'life': 10},
    'ProjectileData_Railgun_RailgunTank':   {'impact': 4000, 'ricochet': 4000, 'pen': 8000, 'speed': 1500, 'life': 2},
    # Rockets
    'ProjectileData_Rocket_BarrageTruck':   {'impact': 500, 'splash': 900, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 85, 'life': 10},
    'ProjectileData_Rocket_RailgunTank':    {'impact': 700, 'splash': 500, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 50, 'life': 10},
    'ProjectileData_Rocket_RocketTank':     {'impact': 12000, 'splash': 8000, 'splash_r_max': 30, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 100, 'life': 20},
    'ProjectileData_Rocket_StealthGunship': {'impact': 300, 'splash': 500, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 150, 'life': 6.7},
    'ProjectileData_Rocket_StealthFighter': {'impact': 1000, 'splash': 1000, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 250, 'life': 3},
    'ProjectileData_Rocket_TurretAA':       {'impact': 600, 'splash': 600, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 150, 'life': 6.7},
    'ProjectileData_Rocket_AntiAirCar':     {'impact': 400, 'splash': 600, 'splash_r_max': 15, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 100, 'life': 6},
    # Special
    'ProjectileData_Flamethrower':       {'impact': 1000, 'splash': 600, 'splash_r_max': 10, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 70, 'life': 10},
    'ProjectileData_Flak_AAFlakCar':     {'impact': 500, 'splash': 500, 'splash_r_max': 30, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 150, 'life': 4},
    'ProjectileData_Plasma_SiegeTank':   {'impact': 30000, 'ricochet': 500, 'splash': 15000, 'splash_r_max': 50, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 100, 'life': 12},
    'ProjectileData_PulseTank':          {'ricochet': 500, 'splash': 1000, 'splash_r_max': 15, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 75, 'life': 5},
    'ProjectileData_MiningLaser':        {'impact': 300, 'splash': 300, 'splash_r_max': 2, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 500, 'life': 0.03},
    # Bombs
    'ProjectileData_DropBomb':           {'impact': 5000, 'splash': 3000, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 30, 'life': 30},
    'ProjectileData_DiveBomb':           {'impact': 15000, 'splash': 15000, 'splash_r_max': 30, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 1, 'life': 30},
    'ProjectileData_Bomb_DiveBomb':      {'impact': 15000, 'ricochet': 3000, 'splash': 15000, 'splash_r_max': 30, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 1, 'life': 30},
    'ProjectileData_Bomb_DropBomb':      {'impact': 5000, 'ricochet': 3000, 'splash': 3000, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 30, 'life': 30},
    'ProjectileData_ContainerBomb':      {'impact': 120000, 'splash': 120000, 'splash_r_max': 80, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 1, 'life': 30},
    'ProjectileData_Bomb_ContainerBomb': {'impact': 120000, 'ricochet': 3000, 'splash': 120000, 'splash_r_max': 80, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 1, 'life': 30},
    'ProjectileData_Bomb_DropTank':      {'impact': 3000, 'splash': 3000, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 1, 'life': 30},
    'ProjectileData_DropTank':           {'impact': 3000, 'splash': 3000, 'splash_r_max': 20, 'splash_r_min': 1, 'splash_r_pow': 3, 'speed': 1, 'life': 30},
}

def pd_get(pd_name, field, default=0):
    """Get a field from projectile_db, with backward-compat for old tuple format."""
    entry = projectile_db.get(pd_name, {})
    if isinstance(entry, dict):
        return entry.get(field, default)
    # Legacy tuple: (impact, speed, lifetime)
    if field == 'impact': return entry[0] if len(entry) > 0 else default
    if field == 'speed': return entry[1] if len(entry) > 1 else default
    if field == 'life': return entry[2] if len(entry) > 2 else default
    return default


# =============================================================================
# VEHICLE → PROJECTILE MAPPING
# Maps unit name → primary/secondary ProjectileData name
# =============================================================================
vehicle_projectiles = {
    # Sol Light Factory (verified against dump vt_proj/vt2_proj)
    'Light Quad':     {'pri': 'ProjectileData_MMG_LightQuad2'},
    'Platoon Hauler': {'pri': 'ProjectileData_MMG_TroopHauler'},
    'Heavy Quad':     {'pri': 'ProjectileData_MMG_HeavyQuad2'},
    'Light Striker':  {'pri': 'ProjectileData_HMG_LightArmoredCar2'},
    'Heavy Striker':  {'pri': 'ProjectileData_Shell_HeavyArmoredCar', 'sec': 'ProjectileData_MMG_HeavyArmoredCar'},
    'AA Truck':       {'pri': 'ProjectileData_Rocket_AntiAirCar'},
    # Sol Heavy Factory
    'Hover Tank':     {'pri': 'ProjectileData_Shell_HoverTank', 'sec': 'ProjectileData_MMG_HoverTank'},
    'Barrage Truck':  {'pri': 'ProjectileData_Rocket_BarrageTruck'},
    'Railgun Tank':   {'pri': 'ProjectileData_Railgun_RailgunTank', 'sec': 'ProjectileData_Rocket_RailgunTank'},
    'Pulse Truck':    {'pri': 'ProjectileData_PulseTank'},
    # Sol Ultra Heavy Factory
    'Siege Tank':     {'pri': 'ProjectileData_Plasma_SiegeTank'},
    # Sol Air Factory
    'Gunship':        {'pri': 'ProjectileData_HMG_Gunship', 'sec': 'ProjectileData_Rocket_StealthGunship'},
    'Dropship':       {'pri': 'ProjectileData_HMG_StealthDropship'},
    'Fighter':        {'pri': 'ProjectileData_HMG_StealthFighter', 'sec': 'ProjectileData_Bomb_DiveBomb'},
    'Bomber':         {'pri': 'ProjectileData_Shell_StealthBomber', 'sec': 'ProjectileData_Bomb_DropBomb'},
    # Centauri Light Factory
    'Light Raider':   {'pri': 'ProjectileData_LMG_LightQuad'},
    'Squad Transport':{'pri': 'ProjectileData_HMG_ArmedTransport'},
    'Heavy Raider':   {'pri': 'ProjectileData_HMG_HeavyQuad'},
    'Assault Car':    {'pri': 'ProjectileData_Shell_LightArmoredCar', 'sec': 'ProjectileData_LMG_LightArmoredCar'},
    'Strike Tank':    {'pri': 'ProjectileData_Shell_StrikeTank'},
    'Flak Car':       {'pri': 'ProjectileData_Flak_AAFlakCar'},
    # Centauri Heavy Factory
    'Combat Tank':    {'pri': 'ProjectileData_Shell_CombatTank', 'sec': 'ProjectileData_SG_CombatTank'},
    'Rocket Tank':    {'pri': 'ProjectileData_Rocket_RocketTank'},
    'Heavy Tank':     {'pri': 'ProjectileData_Shell_HeavyTank', 'sec': 'ProjectileData_HMG_HeavyTank'},
    'Pyro Tank':      {'pri': 'ProjectileData_Flamethrower'},
    # Centauri Ultra Heavy Factory
    'Crimson Tank':   {'pri': 'ProjectileData_Shell_CrimsonTank'},
    # Centauri Air Factory
    'Shuttle':        {'pri': 'ProjectileData_Shell_DreadnoughtCannon'},
    'Dreadnought':    {'pri': 'ProjectileData_Shell_DreadnoughtCannon', 'sec': 'ProjectileData_Shell_Dreadnought'},
    'Interceptor':    {'pri': 'ProjectileData_Shell_Interceptor', 'sec': 'ProjectileData_Bomb_DropTank'},
    'Freighter':      {'pri': 'ProjectileData_Shell_Dreadnought', 'sec': 'ProjectileData_Bomb_ContainerBomb'},
    # Hover Bike — no VehicleTurret (passengers use infantry weapons)
}

# Per-unit turret stat source from dump fields.
# Default: pri from 'vt_', sec from 'vt2_'. Only list exceptions.
# Multi-turret dump showed weapons on unexpected turret slots for these units.
turret_stats_prefix = {
    'Bomber':         {'pri': 'vt3_', 'sec': 'vt_'},    # cannon=vt3_(pri), bombs=vt_(sec)
    'Freighter':      {'pri': 'vt3_', 'sec': 'vt_'},    # cannon=vt3_(pri), bomb=vt_(sec)
    'Shuttle':        {'pri': 'vt3_'},                   # cannon=vt3_ (only weapon)
    'Gunship':        {'pri': 'vt3_'},                   # gun=vt3_
    'Platoon Hauler': {'pri': 'vt3_'},                   # weapon on 2nd VehicleTurret
}

# =============================================================================
# INFANTRY WEAPON DATA — from projectile reference
# =============================================================================
infantry_weapons = {
    'Scout':      {'proj': 'SMG',             'speed': 350, 'life': 3, 'damage': 27},
    'Rifleman':   {'proj': 'Rifle',           'speed': 500, 'life': 3, 'damage': 45},
    'Sniper':     {'proj': 'SniperRifle',     'speed': 800, 'life': 5, 'damage': 700},
    'Heavy':      {'proj': 'Minigun',         'speed': 500, 'life': 3, 'damage': 55},
    'Commando':   {'proj': 'SMG2',            'speed': 450, 'life': 3, 'damage': 85},
    'Militia':    {'proj': 'SMG',             'speed': 350, 'life': 3, 'damage': 27},
    'Trooper':    {'proj': 'Bullpup',         'speed': 450, 'life': 3, 'damage': 36},
    'Marksman':   {'proj': 'MarksmanRifle',   'speed': 600, 'life': 5, 'damage': 300},
    'Juggernaut': {'proj': 'BalteriumRifle',  'speed': 500, 'life': 5, 'damage': 70},
    'Templar':    {'proj': 'RGXRifle',        'speed': 500, 'life': 3, 'damage': 55},
}

infantry_speed = {n: {'walk': 2, 'run': 4, 'sprint': 7, 'jump': 4.25} for n in infantry_weapons}

# =============================================================================
# CREATURE MELEE DATA — from dump_knowledge.md
# =============================================================================
creature_melee = {
    'Crab':        {'pri_dmg': 240, 'sec_dmg': 240, 'pri_cd': 1, 'sec_cd': 3},
    'Horned Crab': {'pri_dmg': 500, 'sec_dmg': 500, 'pri_cd': 1, 'sec_cd': 3},
    'Hunter':      {'pri_dmg': 400, 'sec_dmg': 800, 'pri_cd': 0.5, 'sec_cd': 2},
    'Goliath':     {'pri_dmg': 12000, 'sec_dmg': 12000, 'pri_cd': 3, 'sec_cd': 5},
    'Wasp':        {'pri_dmg': 150, 'sec_dmg': 150, 'pri_cd': 1, 'sec_cd': 2},
    'Queen':       {'pri_dmg': 0, 'sec_dmg': 3000, 'pri_cd': 3, 'sec_cd': 5},
    'Squid':       {'pri_dmg': 0, 'sec_dmg': 3500, 'pri_cd': 0, 'sec_cd': 0},
}

# Creature ranged secondary melee (from dump_knowledge)
creature_ranged_melee = {
    'Behemoth':  {'sec_dmg': 2000, 'sec_cd': 3},
    'Scorpion':  {'sec_dmg': 500, 'sec_cd': 3},
    'Firebug':   {'sec_dmg': 300, 'sec_cd': 2},
    'Dragonfly': {'sec_dmg': 200, 'sec_cd': 2},
    'Shrimp':    {'sec_dmg': 120, 'sec_cd': 2},
    'Shocker':   {'sec_dmg': 250, 'sec_cd': 2},
    'Defiler':   {'sec_dmg': 4000, 'sec_cd': 5},
    'Colossus':  {'sec_dmg': 8000, 'sec_cd': 5},
    'Queen':     {'sec_dmg': 3000, 'sec_cd': 5},
}

# Shrimp ranged attack is missing from dump — hardcode
creature_projectile_override = {
    'Shrimp': {'atk_proj': 'ProjectileData_Shard', 'proj_speed': 200, 'proj_lifetime': 2,
               'atk_range': 300, 'atk_spread': 1.0},
}

# =============================================================================
# STRUCTURE TURRET DATA — from projectile reference
# =============================================================================
turret_data = {
    'Turret':                 {'proj': 'LMG_ChainMG',       'speed': 350, 'life': 3, 'damage': 40,   'range': 1050},
    'Heavy Turret':           {'proj': 'Shell_Turret_Heavy', 'speed': 200, 'life': 10, 'damage': 1600, 'range': 2000},
    'Anti-Air Rocket Turret': {'proj': 'Rocket_TurretAA',   'speed': 150, 'life': 6.7, 'damage': 600, 'range': 1005},
    'Hive Spire':             {'proj': 'BioHive',           'speed': 80,  'life': 6.875, 'damage': 150, 'range': 550},
    'Thorn Spire':            {'proj': 'BioTurret',         'speed': 250, 'life': 7, 'damage': 300,  'range': 1750},
}

# =============================================================================
# VEHICLE MOVEMENT DATA — from unit_data_reference.md
# Dump doesn't capture vehicle movement; only known values listed here.
# Runtime GetBaseValues() reads actual values from prefab components.
# =============================================================================
vehicle_movement = {
    # VehicleHovered
    'Hover Tank':  {'move': 10, 'turbo': 30, 'turn_speed': 60},
    'Hover Bike':  {'move': 37.5, 'turbo': 30},
}


def get_proj_stats(pd_name):
    """Get projectile stats dict from projectile_db, or None."""
    return projectile_db.get(pd_name)


def fmt_damage_parts(pd_name):
    """Format damage sub-type annotation: 'imp:1000 spl:600/r15 pen:5000' (only non-zero)."""
    stats = projectile_db.get(pd_name, {})
    if isinstance(stats, tuple):
        return f"dmg:{stats[0]}"  # legacy
    parts = []
    if stats.get('impact', 0) > 0: parts.append(f"imp:{int(stats['impact'])}")
    if stats.get('splash', 0) > 0:
        sr_max = stats.get('splash_r_max', 10)
        parts.append(f"spl:{int(stats['splash'])}/r{sr_max:g}")
    if stats.get('pen', 0) > 0: parts.append(f"pen:{int(stats['pen'])}")
    if stats.get('ricochet', 0) > 0: parts.append(f"ric:{int(stats['ricochet'])}")
    return ' '.join(parts) if parts else 'dmg:0'


def fmt_weapon_vehicle(pd_name, vt_fi, vt_spread, vt_mag, vt_reload):
    """Format weapon annotation with damage sub-types + 6 base values for vehicles."""
    clean = pd_name.replace('ProjectileData_', '') if pd_name else '?'
    dmg_str = fmt_damage_parts(pd_name) if pd_name else 'dmg:?'
    spd = int(pd_get(pd_name, 'speed')) if pd_name else '?'
    life = pd_get(pd_name, 'life') if pd_name else '?'
    spread_s = vt_spread if vt_spread is not None else '?'
    mag_s = vt_mag if vt_mag is not None else '?'
    fi_s = vt_fi if vt_fi is not None else '?'
    reload_s = vt_reload if vt_reload is not None else '?'
    return f"{clean} | {dmg_str} spd:{spd} life:{life} spread:{spread_s} mag:{mag_s} fi:{fi_s} reload:{reload_s}"


def fmt_weapon_infantry(proj_name, damage, speed, lifetime):
    """Format weapon annotation with 3 base values for infantry.
    Infantry only have impact damage, so show as imp:X.
    """
    return f"{proj_name} | imp:{damage} spd:{speed} life:{lifetime}"


def fmt_weapon_creature_ranged(pd_name, speed, lifetime, spread):
    """Format weapon annotation with damage sub-types for creature ranged attacks."""
    clean = pd_name.replace('ProjectileData_', '') if pd_name else '?'
    dmg_str = fmt_damage_parts(pd_name) if pd_name else 'dmg:?'
    return f"{clean} | {dmg_str} spd:{speed} life:{lifetime} spread:{spread}"


def emit_damage_keys(e, pd_name, prefix=''):
    """Emit per-damage-subtype multiplier keys for a projectile, only for non-zero fields.
    Also emits splash radius keys when splash damage exists.
    prefix: '' for structures, 'pri_' or 'sec_' for vehicles/creatures.
    """
    stats = projectile_db.get(pd_name, {})
    if isinstance(stats, tuple):
        e[f'{prefix}damage_mult'] = 1.00
        return
    emitted = False
    if stats.get('impact', 0) > 0:
        e[f'{prefix}impact_damage_mult'] = 1.00
        emitted = True
    if stats.get('splash', 0) > 0:
        e[f'{prefix}splash_damage_mult'] = 1.00
        emitted = True
    if stats.get('pen', 0) > 0:
        e[f'{prefix}penetrating_damage_mult'] = 1.00
        emitted = True
    if stats.get('ricochet', 0) > 0:
        e[f'{prefix}ricochet_damage_mult'] = 1.00
        emitted = True
    if not emitted:
        e[f'{prefix}damage_mult'] = 1.00
    # Splash radius keys (only if splash damage exists)
    if stats.get('splash', 0) > 0:
        e[f'{prefix}splash_radius_max_mult'] = 1.00
        e[f'{prefix}splash_radius_min_mult'] = 1.00
        e[f'{prefix}splash_radius_pow_mult'] = 1.00


def build_unit(name, u, ctype):
    """Build a unit config entry with only applicable multipliers for its type.

    Unit types and their applicable multipliers:
    - infantry:           3 weapon (dmg/spd/life), move+jump, target+fow
    - wheeled_vehicle:    7 weapon per slot, move+turn_radius, target+fow+ver
    - hovered_vehicle:    7 weapon per slot, move+turbo, target+fow+ver
    - air_vehicle:        7 weapon per slot, move+turbo, target+fow+ver
    - creature_melee:     1 weapon (dmg), move, target+fow
    - creature_ranged:    4 weapon (dmg/spd/life/accuracy), move, target+fow
    - creature_flying_melee: 1 weapon (dmg), move, target+fow
    - structure:          build_radius only, no weapons/movement/vision
    - structure_armed:    build_radius + simplified weapons (dmg/range/accuracy)
    """
    e = {}

    # ── _base annotation: always show tier ──
    parts = [f"HP:{u['hp']}"]
    if u['cost']: parts.append(f"Cost:{u['cost']}")
    if u['build_time']: parts.append(f"Build:{u['build_time']}s")
    mt = u['min_tier']
    parts.append(f"T{mt}" if mt >= 0 else "T0")
    e['_base'] = ' '.join(parts)

    # ── Health & Production ──
    e['health_mult'] = 1.00
    e['cost_mult'] = 1.00
    e['build_time_mult'] = 1.00
    e['min_tier'] = u['min_tier']

    # build_radius: only for structures (modifies ConstructionData.MaximumBaseStructureDistance)
    if ctype in ('structure', 'structure_armed'):
        md = u.get('max_dist', 0)
        e['build_radius'] = md if md > 0 else -1

    # dispense_timeout: only for Hover Bike
    if name == 'Hover Bike':
        e['dispense_timeout'] = -1

    # ── WEAPONS ──

    if ctype == 'infantry':
        # Infantry: only 3 functional weapon params (ProjectileData-based)
        # accuracy/magazine/fire_rate/reload use CharacterAttachment, not overridable
        iw = infantry_weapons.get(name, {})
        if iw:
            e['_pri_weapon'] = fmt_weapon_infantry(iw['proj'], iw['damage'], iw['speed'], iw['life'])
            # Infantry only have impact damage
            e['pri_impact_damage_mult'] = 1.00
            e['pri_proj_speed_mult'] = 1.00
            e['pri_proj_lifetime_mult'] = 1.00

    elif ctype in ('wheeled_vehicle', 'hovered_vehicle', 'air_vehicle'):
        # Vehicles: full 7 params per weapon slot via VehicleTurret
        vp = vehicle_projectiles.get(name, {})

        # Turret field prefixes for reading dump stats (fi, spread, mag, reload)
        # Default: pri→vt_, sec→vt2_. Override per-unit via turret_stats_prefix.
        tsp = turret_stats_prefix.get(name, {})
        pri_pf = tsp.get('pri', 'vt_')
        sec_pf = tsp.get('sec', 'vt2_')

        # Primary weapon
        if u.get(pri_pf + 'fire_interval', u.get('vt_fire_interval', 0)) > 0 or vp.get('pri'):
            pd_name = vp.get('pri', '')
            e['_pri_weapon'] = fmt_weapon_vehicle(
                pd_name,
                u.get(pri_pf + 'fire_interval', 0), u.get(pri_pf + 'spread', 0),
                u.get(pri_pf + 'magazine', 0), u.get(pri_pf + 'reload', 0))
            emit_damage_keys(e, pd_name, 'pri_')
            e['pri_proj_speed_mult'] = 1.00
            e['pri_proj_lifetime_mult'] = 1.00
            e['pri_accuracy_mult'] = 1.00
            e['pri_magazine_mult'] = 1.00
            e['pri_fire_rate_mult'] = 1.00
            e['pri_reload_time_mult'] = 1.00

        # Secondary weapon — require explicit mapping in vehicle_projectiles
        if vp.get('sec'):
            pd_name = vp.get('sec', '')
            e['_sec_weapon'] = fmt_weapon_vehicle(
                pd_name,
                u.get(sec_pf + 'fire_interval', 0), u.get(sec_pf + 'spread', 0),
                u.get(sec_pf + 'magazine', 0), u.get(sec_pf + 'reload', 0))
            emit_damage_keys(e, pd_name, 'sec_')
            e['sec_proj_speed_mult'] = 1.00
            e['sec_proj_lifetime_mult'] = 1.00
            e['sec_accuracy_mult'] = 1.00
            e['sec_magazine_mult'] = 1.00
            e['sec_fire_rate_mult'] = 1.00
            e['sec_reload_time_mult'] = 1.00

    elif ctype == 'creature_ranged':
        # Ranged creatures: dynamic damage sub-type keys + spd/life/accuracy
        ov = creature_projectile_override.get(name, {})
        atk_proj = ov.get('atk_proj', u.get('atk_proj', ''))
        proj_speed = ov.get('proj_speed', u.get('proj_speed', 0))
        proj_life = ov.get('proj_lifetime', u.get('proj_lifetime', 0))
        atk_spread = ov.get('atk_spread', u.get('atk_spread', 0))

        if atk_proj:
            e['_pri_weapon'] = fmt_weapon_creature_ranged(atk_proj, proj_speed, proj_life, atk_spread)
            emit_damage_keys(e, atk_proj, 'pri_')
            e['pri_proj_speed_mult'] = 1.00
            e['pri_proj_lifetime_mult'] = 1.00
            e['pri_accuracy_mult'] = 1.00

        # Secondary: melee attack (only damage is overridable)
        cm = creature_ranged_melee.get(name, {})
        if cm:
            e['_sec_weapon'] = f"Melee | dmg:{cm['sec_dmg']}"
            e['sec_damage_mult'] = 1.00

    elif ctype == 'creature_melee':
        # Melee creatures: only damage is overridable
        cm = creature_melee.get(name, {})
        if cm:
            if cm['pri_dmg'] > 0:
                e['_pri_weapon'] = f"Melee | dmg:{cm['pri_dmg']}"
                e['pri_damage_mult'] = 1.00
            if cm.get('sec_dmg') and cm['sec_dmg'] > 0:
                e['_sec_weapon'] = f"Melee | dmg:{cm['sec_dmg']}"
                e['sec_damage_mult'] = 1.00

    elif ctype == 'creature_flying_melee':
        # Flying melee: only damage is overridable
        cm = creature_melee.get(name, {})
        if cm:
            if cm['pri_dmg'] > 0:
                e['_pri_weapon'] = f"Melee | dmg:{cm['pri_dmg']}"
                e['pri_damage_mult'] = 1.00
            if cm.get('sec_dmg') and cm['sec_dmg'] > 0:
                e['_sec_weapon'] = f"Melee | dmg:{cm['sec_dmg']}"
                e['sec_damage_mult'] = 1.00

    elif ctype == 'structure_armed':
        # Armed structures: full weapon params (non-prefixed, shared key = applies to primary)
        # Same 7 multipliers as vehicles + range_mult (scales AimDistance)
        td = turret_data.get(name, {})
        if td:
            full_pd_name = f"ProjectileData_{td['proj']}"
            dmg_str = fmt_damage_parts(full_pd_name)
            e['_weapon'] = f"{td['proj']} | {dmg_str} spd:{td['speed']} life:{td['life']} range:{td['range']}"
            emit_damage_keys(e, full_pd_name, '')
            e['proj_speed_mult'] = 1.00
            e['proj_lifetime_mult'] = 1.00
            e['range_mult'] = 1.00
            e['accuracy_mult'] = 1.00
            e['magazine_mult'] = 1.00
            e['fire_rate_mult'] = 1.00
            e['reload_time_mult'] = 1.00

    # ── MOVEMENT ──
    # Only applicable params per unit type. No movement for structures.

    if ctype == 'infantry':
        # Soldier: WalkSpeed, RunSpeed, SprintSpeed (move_speed_mult), JumpSpeed (jump_speed_mult)
        sp = infantry_speed.get(name, {})
        if sp:
            e['_base_speed'] = f"Walk:{sp['walk']} Run:{sp['run']} Sprint:{sp['sprint']} Jump:{sp['jump']}"
        e['move_speed_mult'] = 1.00
        e['jump_speed_mult'] = 1.00

    elif ctype == 'wheeled_vehicle':
        # VehicleWheeled: MoveSpeed (move_speed_mult), TurningCircleRadius (turn_radius_mult)
        # No TurboSpeed, no JumpSpeed
        vm = vehicle_movement.get(name, {})
        parts = []
        if vm.get('move'): parts.append(f"Move:{vm['move']}")
        if vm.get('turn_radius'): parts.append(f"TurnRad:{vm['turn_radius']}")
        if parts:
            e['_base_speed'] = ' '.join(parts)
        e['move_speed_mult'] = 1.00
        e['turn_radius_mult'] = 1.00

    elif ctype == 'hovered_vehicle':
        # VehicleHovered: MoveSpeed (move_speed_mult), TurboSpeed (turbo_speed_mult)
        # No TurningCircleRadius, no JumpSpeed
        vm = vehicle_movement.get(name, {})
        parts = []
        if vm.get('move'): parts.append(f"Move:{vm['move']}")
        if vm.get('turbo'): parts.append(f"Turbo:{vm['turbo']}")
        if parts:
            e['_base_speed'] = ' '.join(parts)
        e['move_speed_mult'] = 1.00
        e['turbo_speed_mult'] = 1.00

    elif ctype == 'air_vehicle':
        # VehicleAir: ForwardSpeed+StrafeSpeed (move_speed_mult), TurboSpeed (turbo_speed_mult)
        # strafe_speed_mult scales StrafeSpeed independently
        afs = u.get('air_forward_speed', 0)
        ass_ = u.get('air_strafe_speed', 0)
        ats = u.get('air_turbo_speed', 0)
        parts = []
        if afs: parts.append(f"Fwd:{afs}")
        if ass_: parts.append(f"Strafe:{ass_}")
        if ats: parts.append(f"Turbo:{ats}")
        if parts: e['_base_speed'] = ' '.join(parts)
        e['move_speed_mult'] = 1.00
        e['turbo_speed_mult'] = 1.00
        e['strafe_speed_mult'] = 1.00

    elif ctype in ('creature_melee', 'creature_ranged', 'creature_flying_melee'):
        # CreatureDecapod: MoveSpeed (move_speed_mult), FlyMoveSpeed (fly_speed_mult)
        # strafe_speed_mult scales FlyMoveScaleSide (lateral movement while flying)
        ms = u.get('move_speed', 0)
        fs = u.get('fly_speed', 0)
        fss = u.get('fly_strafe_scale', -1)
        parts = []
        if ms: parts.append(f"Move:{ms}")
        if fs: parts.append(f"Fly:{fs}")
        if fss >= 0: parts.append(f"Strafe:{fss}")
        if parts: e['_base_speed'] = ' '.join(parts)
        e['move_speed_mult'] = 1.00
        if fs: e['fly_speed_mult'] = 1.00
        e['strafe_speed_mult'] = 1.00

    # structures: no movement section

    # ── VISION & SENSE ──
    # target_distance + fow_distance for all mobile units
    # visible_event_radius_mult only for units with VehicleTurret (vehicles)
    # No vision section for structures

    if ctype not in ('structure', 'structure_armed'):
        # All mobile units: target + fow + VER
        # VER scales ProjectileData.VisibleEventRadius (render distance of projectiles)
        # VER base value is only known at runtime (read from ProjectileData on prefab)
        fow = u.get('fow_view', 0)
        tgt = u.get('target_dist', 0)
        sense_parts = []
        if fow: sense_parts.append(f"FOW:{fow}")
        if tgt: sense_parts.append(f"Target:{tgt}")
        e['_base_sense'] = ' '.join(sense_parts)
        e['target_distance'] = tgt if tgt else -1
        e['fow_distance'] = fow if fow else -1
        e['visible_event_radius_mult'] = 1.00

    # structures: no vision section

    return e


# =============================================================================
# BUILD CONFIG
# =============================================================================
config = {
    "enabled": True,
    "dump_fields": False,
    "shrimp_disable_aim": False,
    "description": "Vanilla base config. All multipliers at 1.00 = no change. _base/_pri_weapon/_sec_weapon show actual game values. Use !rebalance to hot-reload.",
}

config["tech_time"] = {
    "_note": "Build time in seconds per tech tier research (all factions). Vanilla: 30s all tiers.",
    "tier_1": 30, "tier_2": 30, "tier_3": 30, "tier_4": 30,
    "tier_5": 30, "tier_6": 30, "tier_7": 30, "tier_8": 30,
}

uc = {}

uc["_teleport"] = {"cooldown": 120, "duration": 5, "_note": "Teleportation: cooldown 120s, cast time 5s"}

# SOL
uc["_comment_sol_barracks"] = "========== SOL — Barracks =========="
for n in ['Scout', 'Rifleman', 'Sniper', 'Heavy', 'Commando']:
    uc[n] = build_unit(n, by_name[n], 'infantry')

uc["_comment_sol_lf"] = "========== SOL — Light Factory =========="
for n in ['Light Quad', 'Platoon Hauler', 'Heavy Quad', 'Light Striker', 'Heavy Striker', 'AA Truck']:
    uc[n] = build_unit(n, by_name[n], 'wheeled_vehicle')

uc["_comment_sol_hf"] = "========== SOL — Heavy Factory =========="
uc['Hover Tank'] = build_unit('Hover Tank', by_name['Hover Tank'], 'hovered_vehicle')
for n in ['Barrage Truck', 'Railgun Tank', 'Pulse Truck']:
    uc[n] = build_unit(n, by_name[n], 'wheeled_vehicle')

uc["_comment_sol_uhf"] = "========== SOL — Ultra Heavy Factory =========="
uc['Sol Harvester'] = build_unit('Sol Harvester', by_name['Sol Harvester'], 'hovered_vehicle')
uc['Siege Tank'] = build_unit('Siege Tank', by_name['Siege Tank'], 'wheeled_vehicle')

uc["_comment_sol_air"] = "========== SOL — Air Factory =========="
for n in ['Gunship', 'Dropship', 'Fighter', 'Bomber']:
    uc[n] = build_unit(n, by_name[n], 'air_vehicle')

uc["_comment_struct"] = "========== SOL/CENTAURI — Structures =========="
uc['Headquarters'] = build_unit('Headquarters', by_name.get('Headquarters', by_name.get('Sol Headquarters')), 'structure')
for n in ['Refinery', 'Barracks', 'Light Factory', 'Air Factory', 'Heavy Factory', 'Ultra Heavy Factory']:
    uc[n] = build_unit(n, by_name[n], 'structure')
for n in ['Turret', 'Heavy Turret', 'Anti-Air Rocket Turret']:
    uc[n] = build_unit(n, by_name[n], 'structure_armed')

# CENTAURI
uc["_comment_cen_barracks"] = "========== CENTAURI — Barracks =========="
for n in ['Militia', 'Trooper', 'Marksman', 'Juggernaut', 'Templar']:
    uc[n] = build_unit(n, by_name[n], 'infantry')

uc["_comment_cen_lf"] = "========== CENTAURI — Light Factory =========="
for n in ['Light Raider', 'Squad Transport', 'Heavy Raider', 'Assault Car', 'Strike Tank', 'Flak Car']:
    uc[n] = build_unit(n, by_name[n], 'wheeled_vehicle')

uc["_comment_cen_hf"] = "========== CENTAURI — Heavy Factory =========="
for n in ['Combat Tank', 'Rocket Tank', 'Heavy Tank', 'Pyro Tank']:
    uc[n] = build_unit(n, by_name[n], 'wheeled_vehicle')

uc["_comment_cen_uhf"] = "========== CENTAURI — Ultra Heavy Factory =========="
uc['Cent Harvester'] = build_unit('Cent Harvester', by_name['Cent Harvester'], 'wheeled_vehicle')
uc['Crimson Tank'] = build_unit('Crimson Tank', by_name['Crimson Tank'], 'wheeled_vehicle')

uc["_comment_cen_air"] = "========== CENTAURI — Air Factory =========="
for n in ['Shuttle', 'Dreadnought', 'Interceptor', 'Freighter']:
    uc[n] = build_unit(n, by_name[n], 'air_vehicle')

# HOVERBIKE
uc["_comment_htp"] = "========== HTP — Hover Bike =========="
uc['Hover Bike'] = build_unit('Hover Bike', by_name['Hover Bike'], 'hovered_vehicle')

# ALIEN
uc["_comment_alien_lesser"] = "========== ALIEN — Lesser Spawning Cyst =========="
uc['Crab'] = build_unit('Crab', by_name['Crab'], 'creature_melee')
uc['Shrimp'] = build_unit('Shrimp', by_name['Shrimp'], 'creature_ranged')
uc['Shocker'] = build_unit('Shocker', by_name['Shocker'], 'creature_ranged')
uc['Wasp'] = build_unit('Wasp', by_name['Wasp'], 'creature_flying_melee')
uc['Dragonfly'] = build_unit('Dragonfly', by_name['Dragonfly'], 'creature_ranged')
uc['Squid'] = build_unit('Squid', by_name['Squid'], 'creature_flying_melee')

uc["_comment_alien_greater"] = "========== ALIEN — Greater Spawning Cyst =========="
uc['Horned Crab'] = build_unit('Horned Crab', by_name['Horned Crab'], 'creature_melee')
uc['Hunter'] = build_unit('Hunter', by_name['Hunter'], 'creature_melee')
uc['Behemoth'] = build_unit('Behemoth', by_name['Behemoth'], 'creature_ranged')
uc['Scorpion'] = build_unit('Scorpion', by_name['Scorpion'], 'creature_ranged')
uc['Firebug'] = build_unit('Firebug', by_name['Firebug'], 'creature_ranged')

uc["_comment_alien_grand"] = "========== ALIEN — Grand Spawning Cyst =========="
uc['Goliath'] = build_unit('Goliath', by_name['Goliath'], 'creature_melee')

uc["_comment_alien_colossal"] = "========== ALIEN — Colossal Spawning Cyst =========="
uc['Defiler'] = build_unit('Defiler', by_name['Defiler'], 'creature_ranged')
uc['Colossus'] = build_unit('Colossus', by_name['Colossus'], 'creature_ranged')

uc["_comment_alien_nest"] = "========== ALIEN — Nest =========="
uc['Queen'] = build_unit('Queen', by_name['Queen'], 'creature_ranged')

uc["_comment_alien_struct"] = "========== ALIEN — Structures =========="
for n in ['Nest', 'Node', 'Bio Cache', 'Lesser Spawning Cyst', 'Greater Spawning Cyst',
          'Grand Spawning Cyst', 'Colossal Spawning Cyst', 'Quantum Cortex']:
    uc[n] = build_unit(n, by_name[n], 'structure')
for n in ['Hive Spire', 'Thorn Spire']:
    uc[n] = build_unit(n, by_name[n], 'structure_armed')

config["units"] = uc

out = 'C:/Users/schwe/Projects/Si_UnitBalanceUI/Si_UnitBalance_Config_Default.json'
with open(out, 'w') as f:
    json.dump(config, f, indent=4)

real = {k: v for k, v in uc.items() if not k.startswith('_')}
print(f"Written {out}")
print(f"Total units/buildings: {len(real)}")
total_p = sum(len([k for k in v.keys() if not k.startswith('_')]) for v in real.values())
print(f"Total parameter fields: {total_p}")

# Audit: verify multiplier-to-base-value consistency per unit type
print("\n=== AUDIT ===")

# Define expected keys per unit type
# Damage sub-type keys that count as "has damage multiplier"
_damage_keys = {'damage_mult', 'impact_damage_mult', 'splash_damage_mult',
                'penetrating_damage_mult', 'ricochet_damage_mult',
                'pri_damage_mult', 'pri_impact_damage_mult', 'pri_splash_damage_mult',
                'pri_penetrating_damage_mult', 'pri_ricochet_damage_mult',
                'sec_damage_mult', 'sec_impact_damage_mult', 'sec_splash_damage_mult',
                'sec_penetrating_damage_mult', 'sec_ricochet_damage_mult'}

expected_keys = {
    'infantry': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier'],
        'move': ['move_speed_mult', 'jump_speed_mult'],
        'sense': ['target_distance', 'fow_distance', 'visible_event_radius_mult'],
    },
    'wheeled_vehicle': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier'],
        'move': ['move_speed_mult', 'turn_radius_mult'],
        'sense': ['target_distance', 'fow_distance', 'visible_event_radius_mult'],
    },
    'hovered_vehicle': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier'],
        'move': ['move_speed_mult', 'turbo_speed_mult'],
        'sense': ['target_distance', 'fow_distance', 'visible_event_radius_mult'],
    },
    'air_vehicle': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier'],
        'move': ['move_speed_mult', 'turbo_speed_mult', 'strafe_speed_mult'],
        'sense': ['target_distance', 'fow_distance', 'visible_event_radius_mult'],
    },
    'creature_melee': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier'],
        'move': ['move_speed_mult', 'fly_speed_mult', 'strafe_speed_mult'],
        'sense': ['target_distance', 'fow_distance', 'visible_event_radius_mult'],
    },
    'creature_ranged': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier'],
        'move': ['move_speed_mult', 'fly_speed_mult', 'strafe_speed_mult'],
        'sense': ['target_distance', 'fow_distance', 'visible_event_radius_mult'],
    },
    'creature_flying_melee': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier'],
        'move': ['move_speed_mult', 'fly_speed_mult', 'strafe_speed_mult'],
        'sense': ['target_distance', 'fow_distance', 'visible_event_radius_mult'],
    },
    'structure': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier', 'build_radius'],
    },
    'structure_armed': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier', 'build_radius'],
        'weapon_non_dmg': ['proj_speed_mult', 'proj_lifetime_mult', 'range_mult',
                   'accuracy_mult', 'magazine_mult', 'fire_rate_mult', 'reload_time_mult'],
    },
}

# Keys that should NOT appear per type
forbidden_keys = {
    'infantry': ['build_radius', 'turbo_speed_mult', 'turn_radius_mult', 'strafe_speed_mult', 'fly_speed_mult',
                 'pri_accuracy_mult', 'pri_magazine_mult', 'pri_fire_rate_mult', 'pri_reload_time_mult'],
    'wheeled_vehicle': ['build_radius', 'jump_speed_mult', 'turbo_speed_mult', 'strafe_speed_mult', 'fly_speed_mult'],
    'hovered_vehicle': ['build_radius', 'jump_speed_mult', 'turn_radius_mult', 'strafe_speed_mult', 'fly_speed_mult'],
    'air_vehicle': ['build_radius', 'jump_speed_mult', 'turn_radius_mult', 'fly_speed_mult'],
    'creature_melee': ['build_radius', 'jump_speed_mult', 'turbo_speed_mult', 'turn_radius_mult'],
    'creature_ranged': ['build_radius', 'jump_speed_mult', 'turbo_speed_mult', 'turn_radius_mult',
                        'pri_magazine_mult', 'pri_fire_rate_mult', 'pri_reload_time_mult'],
    'creature_flying_melee': ['build_radius', 'jump_speed_mult', 'turbo_speed_mult', 'turn_radius_mult'],
    'structure': ['move_speed_mult', 'jump_speed_mult', 'turbo_speed_mult', 'turn_radius_mult', 'fly_speed_mult',
                  'target_distance', 'fow_distance', 'visible_event_radius_mult'],
    'structure_armed': ['move_speed_mult', 'jump_speed_mult', 'turbo_speed_mult', 'turn_radius_mult', 'fly_speed_mult',
                        'target_distance', 'fow_distance', 'visible_event_radius_mult'],
}

# Map unit names to their types
unit_types = {}
for n in ['Scout', 'Rifleman', 'Sniper', 'Heavy', 'Commando', 'Militia', 'Trooper', 'Marksman', 'Juggernaut', 'Templar']:
    unit_types[n] = 'infantry'
for n in ['Light Quad', 'Platoon Hauler', 'Heavy Quad', 'Light Striker', 'Heavy Striker', 'AA Truck',
          'Barrage Truck', 'Railgun Tank', 'Pulse Truck', 'Cent Harvester', 'Siege Tank',
          'Light Raider', 'Squad Transport', 'Heavy Raider', 'Assault Car', 'Strike Tank', 'Flak Car',
          'Combat Tank', 'Rocket Tank', 'Heavy Tank', 'Pyro Tank', 'Crimson Tank']:
    unit_types[n] = 'wheeled_vehicle'
for n in ['Hover Tank', 'Hover Bike', 'Sol Harvester']:
    unit_types[n] = 'hovered_vehicle'
for n in ['Gunship', 'Dropship', 'Fighter', 'Bomber', 'Shuttle', 'Dreadnought', 'Interceptor', 'Freighter']:
    unit_types[n] = 'air_vehicle'
for n in ['Crab', 'Horned Crab', 'Hunter', 'Goliath']:
    unit_types[n] = 'creature_melee'
for n in ['Shrimp', 'Shocker', 'Dragonfly', 'Behemoth', 'Scorpion', 'Firebug', 'Defiler', 'Colossus', 'Queen']:
    unit_types[n] = 'creature_ranged'
for n in ['Wasp', 'Squid']:
    unit_types[n] = 'creature_flying_melee'
for n in ['Headquarters', 'Refinery', 'Barracks', 'Light Factory', 'Air Factory', 'Heavy Factory', 'Ultra Heavy Factory',
          'Nest', 'Node', 'Bio Cache', 'Lesser Spawning Cyst', 'Greater Spawning Cyst',
          'Grand Spawning Cyst', 'Colossal Spawning Cyst', 'Quantum Cortex']:
    unit_types[n] = 'structure'
for n in ['Turret', 'Heavy Turret', 'Anti-Air Rocket Turret', 'Hive Spire', 'Thorn Spire']:
    unit_types[n] = 'structure_armed'

issues_found = False
for uname, entry in real.items():
    ut = unit_types.get(uname)
    if not ut:
        print(f"  WARNING: {uname} has no type mapping!")
        issues_found = True
        continue

    issues = []

    # Check expected keys present
    exp = expected_keys.get(ut, {})
    for group_keys in exp.values():
        for k in group_keys:
            if k not in entry:
                issues.append(f"missing {k}")

    # Check forbidden keys absent
    forb = forbidden_keys.get(ut, [])
    for k in forb:
        if k in entry:
            issues.append(f"should not have {k}")

    if issues:
        print(f"  {uname} ({ut}): {', '.join(issues)}")
        issues_found = True

if not issues_found:
    print("  All units pass audit.")
print("Audit complete.")

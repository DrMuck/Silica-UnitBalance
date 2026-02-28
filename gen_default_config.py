import json

data = json.load(open('C:/Users/schwe/Projects/Si_UnitBalance/dumps/Si_UnitBalance_Dump_2026-02-27_v3.json'))
units_list = data['units']

by_name = {}
for u in units_list:
    name = u['name']
    if name not in by_name:
        by_name[name] = u
    elif u.get('faction') == 'Sol' and by_name[name].get('faction') != 'Sol':
        by_name[name] = u

# =============================================================================
# PROJECTILE DATA — from unit_data_reference.md Section 4
# Format: (impact_damage, speed, lifetime)
# =============================================================================
projectile_db = {
    # Alien
    'ProjectileData_Acidball':    (400, 240, 15),
    'ProjectileData_Behemoth':    (150, 80, 10),
    'ProjectileData_BioHive':     (150, 80, 6.875),
    'ProjectileData_BioTurret':   (300, 250, 7),
    'ProjectileData_Colossus':    (70000, 500, 1.5),
    'ProjectileData_Dragonfly':   (120, 400, 1),
    'ProjectileData_Firebug':     (1000, 60, 10),
    'ProjectileData_Queen':       (1000, 90, 10),
    'ProjectileData_Ray':         (400, 400, 1),       # instant-hit (speed=raycast dist)
    'ProjectileData_Scorpion':    (120, 250, 10),
    'ProjectileData_Shard':       (400, 200, 2),       # Shrimp projectile
    'ProjectileData_Shocker':     (250, 800, 0.375),
    'ProjectileData_SquidExplode':(0, 1, 0.5),
    'ProjectileData_Swarm':       (200, 80, 7.5),
    # Infantry
    'ProjectileData_BalteriumRifle': (70, 500, 5),
    'ProjectileData_Bullpup':       (36, 450, 3),
    'ProjectileData_MarksmanRifle': (300, 600, 5),
    'ProjectileData_Minigun':       (55, 500, 3),
    'ProjectileData_RGXRifle':      (55, 500, 3),
    'ProjectileData_Rifle':         (45, 500, 3),
    'ProjectileData_SMG':           (27, 350, 3),
    'ProjectileData_SMG2':          (85, 450, 3),
    'ProjectileData_SniperRifle':   (700, 800, 5),
    # Vehicle LMG/MMG
    'ProjectileData_LMG_LightQuad':        (50, 400, 3),
    'ProjectileData_LMG_LightArmoredCar':  (75, 500, 5),
    'ProjectileData_LMG_ChainMG':          (40, 350, 3),
    'ProjectileData_LMG_CrimsonFreighter': (32, 350, 3),
    'ProjectileData_MMG_HoverTank':        (50, 400, 3),
    'ProjectileData_MMG_TroopHauler':      (50, 400, 3),
    'ProjectileData_MMG_HeavyQuad2':       (80, 400, 3),
    'ProjectileData_MMG_LightQuad2':       (70, 400, 3),
    'ProjectileData_MMG_HeavyArmoredCar':  (50, 400, 3),
    'ProjectileData_SG_CombatTank':        (100, 300, 1),
    # Vehicle HMG
    'ProjectileData_HMG_HeavyQuad':        (70, 550, 5),
    'ProjectileData_HMG_Gunship':          (160, 400, 2.5),
    'ProjectileData_HMG_BomberCraft':      (80, 860, 5),
    'ProjectileData_HMG_HeavyTank':        (80, 800, 5),
    'ProjectileData_HMG_LightArmoredCar2': (200, 400, 2.5),
    'ProjectileData_HMG_OldHeavy':         (80, 350, 5),
    'ProjectileData_HMG_StealthDropship':  (300, 500, 2),
    'ProjectileData_HMG_StealthFighter':   (100, 1600, 0.156),
    'ProjectileData_HMG_ArmedTransport':   (70, 400, 5),
    'ProjectileData_HMG_TurretMedium':     (160, 550, 5),
    # Shells
    'ProjectileData_Shell_HoverTank':       (1000, 500, 5),
    'ProjectileData_Shell_CombatTank':      (1500, 350, 5.71),
    'ProjectileData_Shell_CrimsonTank':     (15000, 250, 6.5),
    'ProjectileData_Shell_HeavyTank':       (15000, 300, 8),
    'ProjectileData_Shell_StrikeTank':      (700, 300, 6.7),
    'ProjectileData_Shell_HeavyArmoredCar': (800, 400, 5),
    'ProjectileData_Shell_LightArmoredCar': (350, 500, 5),
    'ProjectileData_Shell_ShuttleCannon':   (350, 500, 5),
    'ProjectileData_Shell_Dreadnought':     (1000, 100, 5),
    'ProjectileData_Shell_DreadnoughtCannon': (350, 500, 5),
    'ProjectileData_Shell_Interceptor':     (350, 350, 1),
    'ProjectileData_Shell_StealthBomber':   (1600, 400, 5),
    'ProjectileData_Shell_Turret_Heavy':    (1600, 200, 10),
    'ProjectileData_Railgun_RailgunTank':   (4000, 1500, 2),
    # Rockets
    'ProjectileData_Rocket_BarrageTruck':   (500, 85, 10),
    'ProjectileData_Rocket_RailgunTank':    (700, 50, 10),
    'ProjectileData_Rocket_RocketTank':     (12000, 100, 20),
    'ProjectileData_Rocket_StealthGunship': (300, 150, 6.7),
    'ProjectileData_Rocket_StealthFighter': (1000, 250, 3),
    'ProjectileData_Rocket_TurretAA':       (600, 150, 6.7),
    'ProjectileData_Rocket_AntiAirCar':     (400, 100, 6),
    # Special
    'ProjectileData_Flamethrower':       (1000, 70, 10),
    'ProjectileData_Flak_AAFlakCar':     (500, 150, 4),
    'ProjectileData_Plasma_SiegeTank':   (30000, 100, 12),
    'ProjectileData_PulseTank':          (0, 75, 5),     # splash-only
    'ProjectileData_MiningLaser':        (300, 500, 0.03),
    # Bombs
    'ProjectileData_DropBomb':           (5000, 30, 30),
    'ProjectileData_DiveBomb':           (15000, 1, 30),
    'ProjectileData_ContainerBomb':      (120000, 1, 30),
    'ProjectileData_DropTank':           (3000, 1, 30),
}


# =============================================================================
# VEHICLE → PROJECTILE MAPPING
# Maps unit name → primary/secondary ProjectileData name
# =============================================================================
vehicle_projectiles = {
    # Sol Light Factory
    'Light Quad':     {'pri': 'ProjectileData_LMG_LightQuad'},
    'Platoon Hauler': {'pri': 'ProjectileData_MMG_TroopHauler', 'sec': 'ProjectileData_MMG_TroopHauler'},
    'Heavy Quad':     {'pri': 'ProjectileData_HMG_HeavyQuad'},
    'Light Striker':  {'pri': 'ProjectileData_LMG_LightArmoredCar', 'sec': 'ProjectileData_Shell_LightArmoredCar'},
    'Heavy Striker':  {'pri': 'ProjectileData_Shell_HeavyArmoredCar', 'sec': 'ProjectileData_HMG_OldHeavy'},
    'AA Truck':       {'pri': 'ProjectileData_Rocket_AntiAirCar', 'sec': 'ProjectileData_Shell_LightArmoredCar'},
    # Sol Heavy Factory
    'Hover Tank':     {'pri': 'ProjectileData_Shell_HoverTank', 'sec': 'ProjectileData_MMG_HoverTank'},
    'Barrage Truck':  {'pri': 'ProjectileData_Rocket_BarrageTruck'},
    'Railgun Tank':   {'pri': 'ProjectileData_Railgun_RailgunTank', 'sec': 'ProjectileData_Rocket_RailgunTank'},
    'Pulse Truck':    {'pri': 'ProjectileData_PulseTank'},
    # Sol Ultra Heavy Factory
    'Siege Tank':     {'pri': 'ProjectileData_Plasma_SiegeTank', 'sec': 'ProjectileData_Shell_HoverTank'},
    # Sol Air Factory
    'Gunship':        {'pri': 'ProjectileData_Rocket_StealthGunship', 'sec': 'ProjectileData_HMG_Gunship'},
    'Dropship':       {'pri': 'ProjectileData_HMG_StealthDropship', 'sec': 'ProjectileData_HMG_ArmedTransport'},
    'Fighter':        {'pri': 'ProjectileData_Rocket_StealthFighter', 'sec': 'ProjectileData_HMG_StealthFighter'},
    'Bomber':         {'pri': 'ProjectileData_Shell_StealthBomber', 'sec': 'ProjectileData_HMG_BomberCraft'},
    # Centauri Light Factory
    'Light Raider':   {'pri': 'ProjectileData_LMG_LightArmoredCar'},
    'Squad Transport':{'pri': 'ProjectileData_MMG_TroopHauler', 'sec': 'ProjectileData_MMG_TroopHauler'},
    'Heavy Raider':   {'pri': 'ProjectileData_LMG_LightArmoredCar'},
    'Assault Car':    {'pri': 'ProjectileData_Shell_HeavyArmoredCar', 'sec': 'ProjectileData_HMG_LightArmoredCar2'},
    'Strike Tank':    {'pri': 'ProjectileData_Shell_StrikeTank', 'sec': 'ProjectileData_Shell_StrikeTank'},
    'Flak Car':       {'pri': 'ProjectileData_Flak_AAFlakCar', 'sec': 'ProjectileData_Rocket_AntiAirCar'},
    # Centauri Heavy Factory
    'Combat Tank':    {'pri': 'ProjectileData_Shell_CombatTank', 'sec': 'ProjectileData_SG_CombatTank'},
    'Rocket Tank':    {'pri': 'ProjectileData_Rocket_RocketTank'},
    'Heavy Tank':     {'pri': 'ProjectileData_Shell_HeavyTank', 'sec': 'ProjectileData_HMG_HeavyTank'},
    'Pyro Tank':      {'pri': 'ProjectileData_Flamethrower'},
    # Centauri Ultra Heavy Factory
    'Crimson Tank':   {'pri': 'ProjectileData_Shell_CrimsonTank', 'sec': 'ProjectileData_LMG_CrimsonFreighter'},
    # Centauri Air Factory
    'Shuttle':        {'pri': 'ProjectileData_Shell_ShuttleCannon'},
    'Dreadnought':    {'pri': 'ProjectileData_Shell_Dreadnought', 'sec': 'ProjectileData_Shell_DreadnoughtCannon'},
    'Interceptor':    {'pri': 'ProjectileData_Shell_Interceptor', 'sec': 'ProjectileData_DiveBomb'},
    'Freighter':      {'pri': 'ProjectileData_LMG_CrimsonFreighter', 'sec': 'ProjectileData_ContainerBomb'},
    # Hoverbike
    'Hover Bike':     {'pri': 'ProjectileData_LMG_LightQuad'},
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
    'Hover Bike':  {'move': 10, 'turbo': 30},
}


def get_proj_stats(pd_name):
    """Get (damage, speed, lifetime) from projectile_db, or None."""
    return projectile_db.get(pd_name)


def fmt_weapon_vehicle(pd_name, vt_fi, vt_spread, vt_mag, vt_reload):
    """Format weapon annotation with all 7 base values for vehicles (VehicleTurret).
    Matches: damage_mult, proj_speed_mult, proj_lifetime_mult, accuracy_mult,
             magazine_mult, fire_rate_mult, reload_time_mult
    """
    clean = pd_name.replace('ProjectileData_', '') if pd_name else '?'
    stats = get_proj_stats(pd_name) if pd_name else None
    dmg = stats[0] if stats else '?'
    spd = stats[1] if stats else '?'
    life = stats[2] if stats else '?'
    spread_s = vt_spread if vt_spread is not None else '?'
    mag_s = vt_mag if vt_mag is not None else '?'
    fi_s = vt_fi if vt_fi is not None else '?'
    reload_s = vt_reload if vt_reload is not None else '?'
    return f"{clean} | dmg:{dmg} spd:{spd} life:{life} spread:{spread_s} mag:{mag_s} fi:{fi_s} reload:{reload_s}"


def fmt_weapon_infantry(proj_name, damage, speed, lifetime):
    """Format weapon annotation with 3 base values for infantry.
    Matches: damage_mult, proj_speed_mult, proj_lifetime_mult
    (accuracy/magazine/fire_rate/reload use CharacterAttachment — not overridable)
    """
    return f"{proj_name} | dmg:{damage} spd:{speed} life:{lifetime}"


def fmt_weapon_creature_ranged(pd_name, damage, speed, lifetime, spread):
    """Format weapon annotation with 4 base values for creature ranged attacks.
    Matches: damage_mult, proj_speed_mult, proj_lifetime_mult, accuracy_mult
    (magazine/fire_rate/reload not applicable to creatures)
    """
    clean = pd_name.replace('ProjectileData_', '') if pd_name else '?'
    return f"{clean} | dmg:{damage} spd:{speed} life:{lifetime} spread:{spread}"


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
            e['pri_damage_mult'] = 1.00
            e['pri_proj_speed_mult'] = 1.00
            e['pri_proj_lifetime_mult'] = 1.00

    elif ctype in ('wheeled_vehicle', 'hovered_vehicle', 'air_vehicle'):
        # Vehicles: full 7 params per weapon slot via VehicleTurret
        vp = vehicle_projectiles.get(name, {})

        # Primary weapon
        if u['vt_fire_interval'] > 0 or vp.get('pri'):
            pd_name = vp.get('pri', '')
            e['_pri_weapon'] = fmt_weapon_vehicle(
                pd_name,
                u['vt_fire_interval'], u['vt_spread'], u['vt_magazine'], u['vt_reload'])
            e['pri_damage_mult'] = 1.00
            e['pri_proj_speed_mult'] = 1.00
            e['pri_proj_lifetime_mult'] = 1.00
            e['pri_accuracy_mult'] = 1.00
            e['pri_magazine_mult'] = 1.00
            e['pri_fire_rate_mult'] = 1.00
            e['pri_reload_time_mult'] = 1.00

        # Secondary weapon — require explicit mapping or non-zero magazine
        # (fire_interval alone with mag=0 means turret slot exists but has no usable weapon)
        if vp.get('sec') or u['vt2_magazine'] > 0:
            pd_name = vp.get('sec', '')
            e['_sec_weapon'] = fmt_weapon_vehicle(
                pd_name,
                u['vt2_fire_interval'], u['vt2_spread'], u['vt2_magazine'],
                u.get('vt2_reload', 0))
            e['sec_damage_mult'] = 1.00
            e['sec_proj_speed_mult'] = 1.00
            e['sec_proj_lifetime_mult'] = 1.00
            e['sec_accuracy_mult'] = 1.00
            e['sec_magazine_mult'] = 1.00
            e['sec_fire_rate_mult'] = 1.00
            e['sec_reload_time_mult'] = 1.00

    elif ctype == 'creature_ranged':
        # Ranged creatures: 4 functional params (dmg/spd/life/accuracy)
        # magazine/fire_rate/reload not applicable
        ov = creature_projectile_override.get(name, {})
        atk_proj = ov.get('atk_proj', u.get('atk_proj', ''))
        proj_speed = ov.get('proj_speed', u.get('proj_speed', 0))
        proj_life = ov.get('proj_lifetime', u.get('proj_lifetime', 0))
        atk_spread = ov.get('atk_spread', u.get('atk_spread', 0))

        if atk_proj:
            pd_stats = get_proj_stats(atk_proj)
            dmg = pd_stats[0] if pd_stats else '?'
            e['_pri_weapon'] = fmt_weapon_creature_ranged(atk_proj, dmg, proj_speed, proj_life, atk_spread)
            e['pri_damage_mult'] = 1.00
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
            e['_weapon'] = f"{td['proj']} | dmg:{td['damage']} spd:{td['speed']} life:{td['life']} range:{td['range']}"
            e['damage_mult'] = 1.00
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
uc['Harvester'] = build_unit('Harvester', by_name['Harvester'], 'wheeled_vehicle')
uc['Siege Tank'] = build_unit('Siege Tank', by_name['Siege Tank'], 'wheeled_vehicle')

uc["_comment_sol_air"] = "========== SOL — Air Factory =========="
for n in ['Gunship', 'Dropship', 'Fighter', 'Bomber']:
    uc[n] = build_unit(n, by_name[n], 'air_vehicle')

uc["_comment_struct"] = "========== SOL/CENTAURI — Structures =========="
for n in ['Headquarters', 'Refinery', 'Barracks', 'Light Factory', 'Air Factory', 'Heavy Factory', 'Ultra Heavy Factory']:
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
expected_keys = {
    'infantry': {
        'hp': ['health_mult', 'cost_mult', 'build_time_mult', 'min_tier'],
        'weapon_pri': ['pri_damage_mult', 'pri_proj_speed_mult', 'pri_proj_lifetime_mult'],
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
        'weapon': ['damage_mult', 'proj_speed_mult', 'proj_lifetime_mult', 'range_mult',
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
          'Barrage Truck', 'Railgun Tank', 'Pulse Truck', 'Harvester', 'Siege Tank',
          'Light Raider', 'Squad Transport', 'Heavy Raider', 'Assault Car', 'Strike Tank', 'Flak Car',
          'Combat Tank', 'Rocket Tank', 'Heavy Tank', 'Pyro Tank', 'Crimson Tank']:
    unit_types[n] = 'wheeled_vehicle'
for n in ['Hover Tank', 'Hover Bike']:
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

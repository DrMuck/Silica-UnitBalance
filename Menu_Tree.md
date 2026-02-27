# Si_UnitBalance — Menu Tree

```
Navigation: .1-.9 select | .back go up | .0 exit | !b <args> direct

!b (Root)
├── 1. Sol
│   ├── 1. Barracks
│   │   ├── 1. Scout
│   │   │   ├── 1. Health & Production
│   │   │   │   ├── health_mult            (base: DamageManagerData.Health)
│   │   │   │   ├── cost_mult              (base: ConstructionData.ResourceCost)
│   │   │   │   ├── build_time_mult        (base: ConstructionData.BuildUpTime)
│   │   │   │   ├── min_tier               (base: ConstructionData.MinimumTeamTier)       [absolute]
│   │   │   │   └── build_radius           (base: ConstructionData.MaxBaseStructureDist)   [absolute]
│   │   │   ├── 2. Primary Weapon (ProjectileData name)
│   │   │   │   ├── damage_mult            (base: ProjectileData.m_fImpactDamage)
│   │   │   │   ├── proj_speed_mult        (base: ProjectileData.m_fBaseSpeed)
│   │   │   │   ├── proj_lifetime_mult     (base: ProjectileData.m_fLifeTime)
│   │   │   │   ├── accuracy_mult          (base: VT.PrimaryMuzzleSpread)
│   │   │   │   ├── magazine_mult          (base: VT.PrimaryMagazineSize)
│   │   │   │   ├── fire_rate_mult         (base: VT.PrimaryFireInterval)
│   │   │   │   └── reload_time_mult       (base: VT.PrimaryReloadTime)
│   │   │   ├── 3. Movement
│   │   │   │   ├── move_speed_mult        (base: first speed field found)
│   │   │   │   ├── jump_speed_mult        (base: Soldier.JumpSpeed)
│   │   │   │   ├── turbo_speed_mult       (base: TurboSpeed)
│   │   │   │   └── turn_radius_mult       (base: VehicleWheeled.TurningCircleRadius)
│   │   │   └── 4. Vision & Sense
│   │   │       ├── target_distance        (base: Sensor.TargetingDistance)                [absolute]
│   │   │       ├── fow_distance           (base: Sensor.FogOfWarViewDistance)             [absolute]
│   │   │       └── visible_event_radius_mult (base: ProjectileData.VisibleEventRadius)
│   │   ├── 2. Rifleman
│   │   │   └── (same param group structure)
│   │   ├── 3. Sniper
│   │   ├── 4. Heavy
│   │   └── 5. Commando
│   ├── 2. Light Factory
│   │   ├── 1. Light Quad
│   │   ├── 2. Platoon Hauler
│   │   ├── 3. Heavy Quad
│   │   ├── 4. Light Striker
│   │   ├── 5. Heavy Striker
│   │   └── 6. AA Truck
│   ├── 3. Heavy Factory
│   │   ├── 1. Hover Tank
│   │   ├── 2. Barrage Truck
│   │   ├── 3. Railgun Tank
│   │   └── 4. Pulse Truck
│   ├── 4. Ultra Heavy Factory
│   │   ├── 1. Harvester
│   │   └── 2. Siege Tank
│   ├── 5. Air Factory
│   │   ├── 1. Gunship
│   │   ├── 2. Dropship
│   │   ├── 3. Fighter
│   │   └── 4. Bomber
│   └── 6. Structures
│       ├── 1. Headquarters
│       ├── 2. Refinery
│       ├── 3. Barracks
│       ├── 4. Air Factory
│       ├── 5. Heavy Factory
│       ├── 6. Ultra Heavy Factory
│       ├── 7. Turret
│       ├── 8. Heavy Turret
│       └── 9. Anti-Air Rocket Turret
│
├── 2. Centauri
│   ├── 1. Barracks
│   │   ├── 1. Militia
│   │   ├── 2. Trooper
│   │   ├── 3. Marksman
│   │   ├── 4. Juggernaut
│   │   └── 5. Templar
│   ├── 2. Light Factory
│   │   ├── 1. Light Raider
│   │   ├── 2. Squad Transport
│   │   ├── 3. Heavy Raider
│   │   ├── 4. Assault Car
│   │   ├── 5. Strike Tank
│   │   └── 6. Flak Car
│   ├── 3. Heavy Factory
│   │   ├── 1. Combat Tank .................. [2 VehicleTurrets = Pri + Sec weapon groups]
│   │   │   ├── 1. Health & Production
│   │   │   │   └── (health, cost, build_time, min_tier, build_radius)
│   │   │   ├── 2. Primary Weapon (CombatTank_Shotgun)
│   │   │   │   └── (damage, proj_speed, proj_lifetime, accuracy, magazine, fire_rate, reload_time)
│   │   │   ├── 3. Secondary Weapon (CombatTank_Cannon)
│   │   │   │   └── (damage, proj_speed, proj_lifetime, accuracy, magazine, fire_rate, reload_time)
│   │   │   ├── 4. Movement
│   │   │   │   └── (move_speed, jump_speed, turbo_speed, turn_radius)
│   │   │   └── 5. Vision & Sense
│   │   │       └── (target_distance, fow_distance, visible_event_radius)
│   │   ├── 2. Rocket Tank
│   │   ├── 3. Heavy Tank
│   │   └── 4. Pyro Tank
│   ├── 4. Ultra Heavy Factory
│   │   ├── 1. Harvester
│   │   └── 2. Crimson Tank
│   ├── 5. Air Factory
│   │   ├── 1. Shuttle
│   │   ├── 2. Dreadnought
│   │   ├── 3. Interceptor
│   │   └── 4. Freighter
│   └── 6. Structures
│       ├── 1. Headquarters
│       ├── 2. Refinery
│       ├── 3. Barracks
│       ├── 4. Air Factory
│       ├── 5. Heavy Factory
│       ├── 6. Ultra Heavy Factory
│       ├── 7. Turret
│       ├── 8. Heavy Turret
│       └── 9. Anti-Air Rocket Turret
│
├── 3. Alien
│   ├── 1. Lesser Spawning Cyst
│   │   ├── 1. Crab
│   │   ├── 2. Shrimp
│   │   ├── 3. Shocker
│   │   ├── 4. Wasp
│   │   ├── 5. Dragonfly .................... [CreatureDecapod, ranged, spread 0.25]
│   │   └── 6. Squid
│   ├── 2. Greater Spawning Cyst
│   │   ├── 1. Horned Crab
│   │   ├── 2. Hunter ....................... [melee, 400/800 dmg]
│   │   ├── 3. Behemoth ..................... [ProjectileData_Ray, instant-hit laser]
│   │   ├── 4. Scorpion ..................... [Acidball, range 400]
│   │   └── 5. Firebug ...................... [8 projectiles, spread 5]
│   ├── 3. Grand Spawning Cyst
│   │   └── 1. Goliath ...................... [melee, 12000 dmg]
│   ├── 4. Colossal Spawning Cyst
│   │   ├── 1. Defiler ...................... [Swarm, 10 proj, 180° spread]
│   │   └── 2. Colossus ..................... [beam, range 800, 20s anim]
│   ├── 5. Nest
│   │   └── 1. Queen
│   └── 6. Structures
│       ├── 1. Nest
│       ├── 2. Node
│       ├── 3. Bio Cache
│       ├── 4. Lesser Spawning Cyst
│       ├── 5. Greater Spawning Cyst
│       ├── 6. Grand Spawning Cyst
│       ├── 7. Colossal Spawning Cyst
│       ├── 8. Quantum Cortex
│       ├── 9. Hive Spire
│       └── 10. Thorn Spire
│
├── 4. JSON Config
│   ├── 1. Reset to Blank ................... vanilla settings, empty units:{}, confirm required
│   │   ├── .1 = Confirm
│   │   └── .2 = Cancel
│   ├── 2. Save Current Config .............. saves to Si_UnitBalance_Configs/
│   │   ├── type a name ──► YYYYMMDDHHMM_name.json
│   │   └── .1 ──────────► YYYYMMDDHHMM.json
│   └── 3. Load Saved Config ................ lists .json files, newest first
│       ├── .1 = pick file #1
│       ├── .2 = pick file #2
│       ├── ...
│       └── confirm: .1=Load  .2=Cancel
│
└── 5. HTP (Hover · Tier · Teleportation)
    ├── 1. Hoverbike ....................... "Hover Bike" unit (moved from Sol > Barracks)
    │   ├── 1. Health & Production
    │   │   └── (health_mult, cost_mult, build_time_mult, min_tier, build_radius)
    │   ├── 2. Primary Weapon
    │   │   └── (damage, proj_speed, proj_lifetime, accuracy, magazine, fire_rate, reload_time)
    │   ├── 3. Movement
    │   │   └── (move_speed, jump_speed, turbo_speed, turn_radius)
    │   └── 4. Vision & Sense
    │       └── (target_distance, fow_distance, visible_event_radius)
    ├── 2. Tier ............................ tech-up research time per tier (absolute seconds)
    │   ├── 1. Tier 1 = <time>s          (default: 30s)
    │   ├── 2. Tier 2 = <time>s
    │   ├── ...
    │   └── 8. Tier 8 = <time>s
    └── 3. Teleportation .................. HQ teleport settings (all structures)
        ├── 1. Cooldown = <seconds>       (base: 120s, TeleportUI.TeleportCooldownTime)
        └── 2. Duration = <seconds>       (base: 5s, TeleportUI.TeleportTime)


═══════════════════════════════════════════════════════════════════
                    PARAMETER GROUP DETAIL
═══════════════════════════════════════════════════════════════════

Every unit expands into these parameter groups:

┌─────────────────────────────────────────────────────────────────┐
│  1. Health & Production  (always shown)                         │
├─────────────────────────────────────────────────────────────────┤
│  Key               │ Base Value From                │ Type      │
│────────────────────│────────────────────────────────│───────────│
│  health_mult       │ DamageManagerData.Health       │ multiplier│
│  cost_mult         │ ConstructionData.ResourceCost  │ multiplier│
│  build_time_mult   │ ConstructionData.BuildUpTime   │ multiplier│
│  min_tier          │ ConstructionData.MinTeamTier   │ absolute  │
│  build_radius      │ ConstructionData.MaxBaseDist   │ absolute  │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  2. Primary Weapon (name)  (shown if weapons detected)          │
│     JSON keys use pri_ prefix: pri_damage_mult, etc.            │
├─────────────────────────────────────────────────────────────────┤
│  Key               │ Base Value From                │ Type      │
│────────────────────│────────────────────────────────│───────────│
│  damage_mult       │ ProjectileData.m_fImpactDamage │ multiplier│
│  proj_speed_mult   │ ProjectileData.m_fBaseSpeed    │ multiplier│
│  proj_lifetime_mult│ ProjectileData.m_fLifeTime     │ multiplier│
│  accuracy_mult     │ VT.PrimaryMuzzleSpread /       │ multiplier│
│                    │ CreatureAttack.AtkProjSpread    │           │
│  magazine_mult     │ VT.PrimaryMagazineSize         │ multiplier│
│  fire_rate_mult    │ VT.PrimaryFireInterval         │ multiplier│
│  reload_time_mult  │ VT.PrimaryReloadTime           │ multiplier│
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  3. Secondary Weapon (name)  (shown only if 2nd weapon exists)  │
│     JSON keys use sec_ prefix: sec_damage_mult, etc.            │
├─────────────────────────────────────────────────────────────────┤
│  (same 7 parameters as Primary, but reads from secondary        │
│   VehicleTurret fields or CreatureDecapod.AttackSecondary)      │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  Fallback: Damage & Weapons  (only if NO weapons detected)      │
├─────────────────────────────────────────────────────────────────┤
│  damage_mult, range_mult, proj_speed_mult, accuracy_mult,      │
│  magazine_mult, fire_rate_mult, reload_time_mult                │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  4. Movement  (always shown)                                    │
├─────────────────────────────────────────────────────────────────┤
│  Key               │ Base Value From                │ Type      │
│────────────────────│────────────────────────────────│───────────│
│  move_speed_mult   │ MoveSpeed/WalkSpeed/RunSpeed   │ multiplier│
│  jump_speed_mult   │ Soldier.JumpSpeed              │ multiplier│
│  turbo_speed_mult  │ TurboSpeed                     │ multiplier│
│  turn_radius_mult  │ VehicleWheeled.TurnCircleRad   │ multiplier│
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  5. Vision & Sense  (always shown)                              │
├─────────────────────────────────────────────────────────────────┤
│  Key               │ Base Value From                │ Type      │
│────────────────────│────────────────────────────────│───────────│
│  target_distance   │ Sensor.TargetingDistance        │ absolute  │
│  fow_distance      │ Sensor.FogOfWarViewDistance     │ absolute  │
│  visible_event_    │ ProjectileData.                 │ multiplier│
│    radius_mult     │   VisibleEventRadius            │           │
└─────────────────────────────────────────────────────────────────┘


═══════════════════════════════════════════════════════════════════
                       VALUE DISPLAY
═══════════════════════════════════════════════════════════════════

  In-menu display:   1. damage_mult = 1.50 (base: 25)
                                      ^^^^        ^^
                                      │           └── vanilla game value from prefab
                                      └── current JSON multiplier ("-" if not set)

  Multiplier effect:  base 25  ×  mult 1.50  =  37.5 actual damage
  Absolute params:    min_tier, build_radius, target_distance, fow_distance
                      → value is set directly, not multiplied


═══════════════════════════════════════════════════════════════════
                    SPECIAL PROJECTILE NOTES
═══════════════════════════════════════════════════════════════════

  Normal projectile:   range = speed × lifetime
                       proj_speed_mult  → changes speed
                       proj_lifetime_mult → changes lifetime
                       Both affect effective range.

  Instant-hit (ray):   m_fBaseSpeed = raycast distance (not speed)
                       proj_speed_mult → scales raycast range
                       proj_lifetime_mult → scales visual beam duration only
                       (Behemoth laser, etc.)


═══════════════════════════════════════════════════════════════════
                     JSON CONFIG FORMAT
═══════════════════════════════════════════════════════════════════

  Active config:  Mods/Si_UnitBalance_Config.json
  Saved configs:  Mods/Si_UnitBalance_Configs/*.json

  {
      "enabled": true,
      "tech_time": { "tier_1": 30, ..., "tier_8": 30 },
      "units": {
          "Light Striker": {
              "pri_damage_mult": 1.5,
              "pri_proj_lifetime_mult": 1.2,
              "_note": "modified via !b"
          },
          "Combat Tank": {
              "pri_damage_mult": 1.0,
              "sec_damage_mult": 1.3,
              "_note": "modified via !b"
          }
      }
  }
```

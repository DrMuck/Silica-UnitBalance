# Silica Unit Data Reference
> Extracted from MelonLoader deep component dumps (v5.3.0)

---

## 1. RIFLEMAN (Sol_Soldier_Rifleman) — Soldier Type

### DamageManager (Data: Soldier_Rifleman)
| Field | Value |
|---|---|
| MaxHealth | 150 |
| ArmorHitAdjust | -0.3 |
| DestructionSizeScale | 1 |

### AutoHeal (Data: AutoHealData_Infantry)
| Field | Value |
|---|---|
| HealTick | 5s |
| HealAmountPct | 0.015 (1.5%/tick) |
| HealMaximum | 1.0 |
| DamageDelay | 10s |

### Sensor (Child: SensorInfantry_01)
| Field | Value |
|---|---|
| FogOfWarViewDistance | 200 |
| TargetingDistance | 400 |
| SensitivityFOV | 60 |
| SensitivityFalloff | 60 |

### UnitAimAt
| Field | Value |
|---|---|
| AimDistanceMin | 1 |
| AimDistanceMax | 2000 |

### Soldier
| Field | Value |
|---|---|
| WalkSpeed | 2 |
| RunSpeed | 4 |
| SprintSpeed | 7 |
| JumpSpeed | 4.25 |
| TurnSpeed | 540 |
| CrouchSpeedModifier | 0.5 |
| MaxSlopeAngle | 50 |
| HitSlowTime | 0.5 |
| HitSlowDamage | 15 |
| Skill | 0.25 |
| TopSpeed (prop) | 7 |
| NormalSpeed (prop) | 4 |
| TotalMass | 100 |
| CanBeConcealed | True |
| CanHideInShadow | True |
| DayConcealment | 0 |
| NightConcealment | -0.2 |
| NightMovementVisibilityMult | 0.75 |
| WeaponFOV | 40 |
| CanBoard | True |

### Weapon
- Attachments via `List<CharacterAttachment>` on Soldier component
- Weapon model child: `Cyborg_Droid_Sniper`
- ProjectileData: **NOT DUMPED** (hidden in CharacterAttachment system)

---

## 2. HOVER TANK (Sol_Heavy_HoverTank) — VehicleHovered Type

### DamageManager (Data: Sol_Heavy_HoverTank)
| Field | Value |
|---|---|
| MaxHealth | 13000 |
| ArmorHitAdjust | 0.25 |
| DestructionSizeScale | 2 |
| DestructionExplosionImpulseLinear | 8 |
| DestructionExplosionImpulseAngular | 90 |

### AutoHeal
NOT PRESENT — vehicles don't self-heal.

### Sensor (Child: SensorFar_01)
| Field | Value |
|---|---|
| FogOfWarViewDistance | 300 |
| TargetingDistance | 1000 |
| SensitivityFOV | 100 |
| SensitivityFalloff | 45 |

### UnitAimAt
| Field | Value |
|---|---|
| AimDistanceMin | 5 |
| AimDistanceMax | 2000 |

### VehicleHovered
| Field | Value |
|---|---|
| MoveSpeed | 10 |
| TurnSpeed | 60 |
| MoveAcceleration | 4.5 |
| TurnAcceleration | 30 |
| BrakesAcceleration | 2.5 |
| BrakesStrafeAcceleration | 3.5 |
| StrafeSpeed | 8 |
| CanTurboThrust | True |
| TurboSpeed | 30 |
| TurboAcceleration | 12 |
| TurboThrustTime | 1 |
| TurboRegenTime | 4 |
| MaxSlope | 40 |
| HoverHeightMin | 1.2 |
| HoverHeightMax | 4 |
| Skill | 0.25 |
| TopSpeed (prop) | 30 |
| NormalSpeed (prop) | 10 |
| TotalMass | 60000 |
| CanBeCrushed | True |
| NightConcealment | -0.2 |
| NightMovementVisibilityMult | 1.0 |

### CollisionDamageManager
| Field | Value |
|---|---|
| DoImpactOther | True |
| DoImpactSelf | True |
| DoFallSelf | True |
| ImpactDamageMult | 1 |
| FallDamageMult | 1 |
| ImpactDamageMinSpeed | 5 |
| FallDamageMinSpeed | 15 |

### VehicleTurret #1: Main Cannon (Child: Ref_Turret, ID: "PrimaryWeapon")
| Field | Value |
|---|---|
| AimDistance | 1000 |
| YawSpeed | 40 |
| PitchSpeed | 20 |
| YawMaxLeft/Right | 180 / 180 |
| PitchMaxUp/Down | 20 / 15 |
| AimTolerance | 0.1 |
| **PrimaryProjectileData** | **ProjectileData_Shell_HoverTank** |
| PrimaryMuzzleSpread | 0.1 |
| PrimaryFireInterval | 5 |
| PrimaryShotCount | 1 |
| PrimaryMagazineSize | 5 |
| PrimaryReloadTime | 1.5 |
| PrimaryReloadShotByShot | True |
| PrimaryShotByShotCount | 1 |
| PrimaryRecoil | 2 |
| PrimaryAIAimingTolerance | (0.40, 0.40) |
| PrimaryAICheckFriendlyFire | True |

### VehicleTurret #2: Machine Gun (Child: Ref_MachineGunBase, ID: "SecondaryWeapon")
| Field | Value |
|---|---|
| AimDistance | 300 |
| YawSpeed | 120 |
| PitchSpeed | 120 |
| YawMaxLeft/Right | 180 / 180 |
| PitchMaxUp/Down | 50 / 10 |
| AimOnFreeLook | True |
| **SecondaryProjectileData** | **ProjectileData_MMG_HoverTank** |
| SecondaryMuzzleSpread | 1.0 |
| SecondaryFireInterval | 0.055 (~18.2 rps) |
| SecondaryShotCount | 1 |
| SecondaryMagazineSize | 100 |
| SecondaryReloadTime | 2 |
| SecondaryReloadShotByShot | False |
| SecondaryLoopingWindUpWindDown | (1.00, 1.00) |
| SecondaryAIAimingTolerance | (15.00, 15.00) |

### VehicleTurret #3: Camera (Child: Ref_TurretCamBase)
| Field | Value |
|---|---|
| AimDistance | 100 |
| YawSpeed | 180 |
| PitchSpeed | 180 |
| (No weapon — camera/view only) |

---

## 3. CRAB (Alien_Crab) — CreatureDecapod Type

### DamageManager (Data: Alien_Crab)
| Field | Value |
|---|---|
| MaxHealth | 300 |
| ArmorHitAdjust | 0 |
| DestructionSizeScale | 1 |

### AutoHeal (Data: AutoHealData_AlienSmall)
| Field | Value |
|---|---|
| HealTick | 3s |
| HealAmountPct | 0.02 (2%/tick) |
| HealMaximum | 1.0 |
| DamageDelay | 3s |

### Sensor (Child: AlienSensor_01)
| Field | Value |
|---|---|
| FogOfWarViewDistance | 200 |
| TargetingDistance | 400 |
| SensitivityFOV | 140 |
| SensitivityFalloff | 40 |
| NightBonus | 0.4 |

### UnitAimAt
NOT PRESENT — Crab is melee only.

### CreatureDecapod
| Field | Value |
|---|---|
| MoveSpeed | 18 |
| CreepSpeed | 4 |
| TurnSpeed | 360 |
| WallSpeed | 10 |
| MaxSlopeAngle | 50 |
| MoveControl | 50 |
| AirControl | 3 |
| HitSlowTime | 0.5 |
| HitSlowDamage | 15 |
| HitSlowSpeedPct | 0.5 |
| TopSpeed (prop) | 18 |
| NormalSpeed (prop) | 18 |
| TotalMass | 120 |
| Skill | 0.25 |
| NightConcealment | -0.2 |
| NightMovementVisibilityMult | 0.2 |
| NightBonus (sensor) | 0.4 |
| CanBeCrushed | True |

### Melee & AI
| Field | Value |
|---|---|
| AIMeleeDistance | 100 |
| AIJumpProbability | 0.05 |
| AIJumpFastProbabilityMult | 3 |
| AIJumpMinDistance | 20 |
| AIUseAttackToMoveProbability | 0.1 |
| HasRangedAttack | False |
| HasMeleeAttack | True |
| HasAttackPrimary | True |
| HasAttackSecondary | True |
| IsAttackPrimaryHeatUpType | True |
| CanClimbAttackPrimary | True |
| CanClimbAttackSecondary | True |

### Pounce & Abilities
| Field | Value |
|---|---|
| PounceEnabled | True |
| PounceSpeed | 10 |
| ClimbEnabled | True |
| SubmergeEnabled | True |
| SubmergeTime | 1.1 |
| EmergeTime | 1.1 |
| FlyEnabled | False |

---

## Quick Comparison Table

| Stat | Rifleman | Hover Tank | Crab |
|---|---|---|---|
| MaxHealth | 150 | 13,000 | 300 |
| ArmorHitAdjust | -0.3 (takes less) | 0.25 (takes more) | 0 (neutral) |
| TopSpeed | 7 (sprint) | 30 (turbo) / 10 (normal) | 18 |
| TurnSpeed | 540 | 60 | 360 |
| Mass | 100 | 60,000 | 120 |
| Sensor Targeting | 400 | 1000 | 400 |
| Sensor FOV | 60 | 100 | 140 |
| AimDistMax | 2000 | 2000 | N/A (melee) |
| AutoHeal Rate | 1.5%/5s after 10s | None | 2%/3s after 3s |
| Night Move Vis | 0.75 | 1.0 | 0.2 |

## Key Observations
- **ArmorHitAdjust**: Negative = reduces incoming damage, Positive = amplifies. Rifleman is resilient to armor-type hits, Hover Tank is vulnerable.
- **Crab Night Stealth**: Lowest NightMovementVisibilityMult (0.2) + NightBonus 0.4 on sensor + widest FOV (140) = very effective at night.
- **Hover Tank Main Gun**: 5-round magazine, 5s between shots, shot-by-shot reload at 1.5s/shell. Effective range 1000 (turret AimDistance).
- **Hover Tank MG**: 100-round belt, 18.2 rps, 2s reload. Short range (300). Very fast turret tracking (120 deg/s).
- **Hover Tank Shell**: ProjectileData_Shell_HoverTank — 500 speed, 5s life, 2500 range, 1000 impact + 5000 pen + 600 splash/r15.
- **Hover Tank MG**: ProjectileData_MMG_HoverTank — 400 speed, 3s life, 1200 range, 50 impact.

---

## 4. PROJECTILE DATA (All 78 Assets)

### Field Key
- **Speed** = base projectile velocity (units/sec)
- **Lifetime** = seconds before despawn
- **Range** = Speed × Lifetime (theoretical max, matches game's MaxDistance)
- **ImpactDmg** = direct hit damage
- **PenDmg** = penetrating damage (only if penetration enabled)
- **SplashDmg/R** = splash damage / radius (only if splash enabled)
- **Gravity** = gravity scale (0=flat, 1=full drop)

### Alien Projectiles

| Name | Speed | Life | Range | Impact | Pen? | PenDmg | Splash? | SplashDmg | SplashR | Grav | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Acidball | 240 | 15 | 3600 | 400 | No | - | Yes | 400 | 6 | 1.5 | SpeedRand; NoFF |
| Behemoth | 80 | 10 | 800 | 150 | No | - | No | - | - | 0.5 | Ricochet:50 |
| BioHive | 80 | 6.875 | 550 | 150 | No | - | No | - | - | 0.5 | Ricochet:50 |
| BioTurret | 250 | 7 | 1750 | 300 | No | - | No | - | - | 0.5 | Ricochet:100 |
| Colossus | 500 | 1.5 | 750 | 70,000 | No | - | Yes | 50,000 | 80 | 0 | Zero grav; superweapon |
| Dragonfly | 400 | 1 | 400 | 120 | No | - | No | - | - | 0 | Ricochet:30 |
| Firebug | 60 | 10 | 600 | 1000 | No | - | Yes | 500 | 10 | 0.5 | SpeedRand |
| Queen | 90 | 10 | 900 | 1000 | No | - | Yes | 500 | 10 | 1.0 | SpeedRand |
| Ray | 400 | 1 | 400 | 400 | No | - | No | - | - | 1.0 | Hitscan; Deform |
| Scorpion | 250 | 10 | 2500 | 120 | Yes | 250 | No | - | - | 0.5 | Ricochet:80 |
| Shard | 200 | 2 | 400 | 400 | Yes | 800 | No | - | - | 1.0 | Deform |
| Shocker | 800 | 0.375 | 300 | 250 | No | - | Yes | 250 | 2 | 0 | Electric dmg |
| SquidExplode | 1 | 0.5 | 0.5 | 0 | No | - | Yes | 3500 | 20 | 0 | Suicide bomb |
| Swarm | 80 | 7.5 | 600 | 200 | Yes | 800 | No | - | - | 0.5 | Ricochet:150 |

### Bombs (Dropped Ordnance)

| Name | Speed | Life | Range | Impact | Splash | SplashR | Grav | Notes |
|---|---|---|---|---|---|---|---|---|
| ContainerBomb | 1 | 30 | 30 | 120,000 | 120,000 | 80 | 1 | Nuke-tier |
| DiveBomb | 1 | 30 | 30 | 15,000 | 15,000 | 30 | 1 | |
| DropBomb | 30 | 30 | 900 | 5,000 | 3,000 | 20 | 1 | High drag |
| DropTank | 1 | 30 | 30 | 3,000 | 3,000 | 20 | 1 | Fire bomb |

### Vehicle HMGs (Heavy Machine Guns)

| Name | Speed | Life | Range | Impact | Splash | SplashR | Grav | Notes |
|---|---|---|---|---|---|---|---|---|
| HMG_ArmedTransport | 400 | 5 | 2000 | 70 | - | - | 1 | |
| HMG_BomberCraft | 860 | 5 | 4300 | 80 | 50 | 3 | 1 | Longest range HMG |
| HMG_Gunship | 400 | 2.5 | 1000 | 160 | 80 | 3 | 1 | |
| HMG_HeavyQuad | 550 | 5 | 2750 | 70 | - | - | 1 | |
| HMG_HeavyTank | 800 | 5 | 4000 | 80 | 50 | 1.5 | 1 | |
| HMG_LightArmoredCar2 | 400 | 2.5 | 1000 | 200 | 100 | 3 | 1 | |
| HMG_OldHeavy | 350 | 5 | 1750 | 80 | - | - | 1 | |
| HMG_StealthDropship | 500 | 2 | 1000 | 300 | - | - | 0.5 | |
| HMG_StealthFighter | 1600 | 0.156 | 250 | 100 | - | - | 0.5 | Fastest projectile |
| HMG_TurretMedium | 550 | 5 | 2750 | 160 | - | - | 1 | |

### Infantry Weapons

| Name | Speed | Life | Range | Impact | Pen? | PenDmg | Grav | Notes |
|---|---|---|---|---|---|---|---|---|
| BalteriumRifle | 500 | 5 | 2500 | 70 | No | - | 0.5 | |
| Bullpup | 450 | 3 | 1350 | 36 | No | - | 1 | |
| MarksmanRifle | 600 | 5 | 3000 | 300 | Yes | 800 | 0.5 | Splash:100/r4 |
| Minigun | 500 | 3 | 1500 | 55 | Yes | 75 | 1 | |
| RGXRifle | 500 | 3 | 1500 | 55 | No | - | 1 | |
| Rifle | 500 | 3 | 1500 | 45 | No | - | 1 | Rifleman weapon |
| SMG | 350 | 3 | 1050 | 27 | No | - | 1 | |
| SMG2 | 450 | 3 | 1350 | 85 | No | - | 1 | |
| SniperRifle | 800 | 5 | 4000 | 700 | No | - | 0.5 | |

### Light/Medium Machine Guns

| Name | Speed | Life | Range | Impact | Grav | Notes |
|---|---|---|---|---|---|---|
| LMG_ChainMG | 350 | 3 | 1050 | 40 | 1 | |
| LMG_CrimsonFreighter | 350 | 3 | 1050 | 32 | 1 | |
| LMG_Drone | 350 | 3 | 1050 | 32 | 1 | |
| LMG_LightArmoredCar | 550 | 5 | 2750 | 75 | 1 | |
| LMG_LightQuad | 400 | 3 | 1200 | 50 | 1 | |
| MMG_HeavyArmoredCar | 400 | 3 | 1200 | 50 | 1 | |
| MMG_HeavyQuad2 | 400 | 3 | 1200 | 80 | 1 | |
| MMG_HoverTank | 400 | 3 | 1200 | 50 | 1 | |
| MMG_LightQuad2 | 400 | 3 | 1200 | 70 | 1 | |
| MMG_TroopHauler | 400 | 3 | 1200 | 50 | 1 | |
| SG_CombatTank | 300 | 1 | 300 | 100 | 1 | Shotgun; high drag |

### Special Weapons

| Name | Speed | Life | Range | Impact | Splash | SplashR | Grav | Notes |
|---|---|---|---|---|---|---|---|---|
| Flamethrower | 70 | 10 | 700 | 1000 | 600 | 10 | 1 | |
| Flak_AAFlakCar | 150 | 4 | 600 | 500 | 500 | 30 | 0 | Anti-air; zero grav |
| Grenade_ArmedTransport | 75 | 30 | 2250 | 300 | 700 | 8 | 1 | Arced |
| MiningLaser | 500 | 0.03 | 15 | 300 | 300 | 2 | 0 | Hitscan; Electric |
| Plasma_SiegeTank | 100 | 12 | 1200 | 30,000 | 15,000 | 50 | 1 | Siege weapon |
| PulseTank | 75 | 5 | 375 | 0 | 1000 | 15 | 0 | Splash-only; Electric |

### Railgun

| Name | Speed | Life | Range | Impact | PenDmg | Grav | Notes |
|---|---|---|---|---|---|---|---|
| Railgun_RailgunTank | 1500 | 2 | 3000 | 4000 | 8000 | 1 | Ricochet:4000 |

### Rockets

| Name | Speed | Life | Range | Impact | Splash | SplashR | Grav | Notes |
|---|---|---|---|---|---|---|---|---|
| Human_DuskRocket | 150 | 5.5 | 825 | 3100 | 2000 | 15 | 0.1 | |
| Human_Rocket | 200 | 4.125 | 825 | 700 | 500 | 10 | 0.1 | Standard RPG |
| Rocket_AntiAirCar | 100 | 6 | 600 | 400 | 600 | 15 | 0.1 | AA |
| Rocket_BarrageTruck | 85 | 10 | 850 | 500 | 900 | 10 | 0.1 | SpeedRand |
| Rocket_RailgunTank | 50 | 10 | 500 | 700 | 500 | 10 | 0.1 | Secondary |
| Rocket_RocketTank | 100 | 20 | 2000 | 12,000 | 8,000 | 30 | 0.1 | Heavy |
| Rocket_StealthFighter | 250 | 3 | 750 | 1000 | 1000 | 20 | 0.1 | Air-to-ground |
| Rocket_StealthGunship | 150 | 6.7 | 1005 | 300 | 500 | 10 | 0.1 | |
| Rocket_TurretAA | 150 | 6.7 | 1005 | 600 | 600 | 20 | 0.1 | AA turret |

### Tank Shells

| Name | Speed | Life | Range | Impact | Pen? | PenDmg | Splash | SplashR | Grav | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| Shell_CombatTank | 350 | 5.71 | 1999 | 1500 | No | - | 3000 | 10 | 0.25 | |
| Shell_CrimsonTank | 250 | 6.5 | 1625 | 15,000 | No | - | 5000 | 20 | 0.5 | |
| Shell_Dreadnought | 100 | 5 | 500 | 1000 | Yes | 1600 | 600 | 15 | 1 | |
| Shell_DreadnoughtCannon | 500 | 5 | 2500 | 350 | Yes | 220 | 220 | 4 | 1 | Rapid fire |
| Shell_HeavyArmoredCar | 400 | 5 | 2000 | 800 | Yes | 4500 | 500 | 15 | 1 | |
| Shell_HeavyTank | 300 | 8 | 2400 | 15,000 | No | - | 4000 | 20 | 0.25 | |
| Shell_HoverTank | 500 | 5 | 2500 | 1000 | Yes | 5000 | 600 | 15 | 1 | |
| Shell_Interceptor | 350 | 1 | 350 | 350 | Yes | 220 | 220 | 4 | 0 | Zero grav |
| Shell_LightArmoredCar | 500 | 5 | 2500 | 350 | Yes | 220 | 220 | 4 | 1 | |
| Shell_ShuttleCannon | 500 | 5 | 2500 | 350 | Yes | 220 | 220 | 4 | 1 | |
| Shell_StealthBomber | 400 | 5 | 2000 | 1600 | No | - | 1000 | 8 | 1 | |
| Shell_StrikeTank | 300 | 6.7 | 2010 | 700 | No | - | 1000 | 10 | 0.25 | |
| Shell_Turret_Heavy | 200 | 10 | 2000 | 1600 | No | - | 1000 | 8 | 1 | |
| Shell_Turret_UltraHeavy | 100 | 15 | 1500 | 7000 | No | - | 4000 | 15 | 0.25 | |

### Key ProjectileData Observations
- **Range = Speed × Lifetime** — to change effective range, modify either field
- **Penetration** is uncommon: HoverTank shell (5000), HeavyArmoredCar (4500), Railgun (8000), MarksmanRifle (800)
- **Highest vehicle damage**: Siege Tank plasma (30K impact + 15K splash/r50), Crimson Tank (15K + 5K/r20), Heavy Tank (15K + 4K/r20)
- **Alien superweapon**: Colossus (70K + 50K/r80), zero gravity
- **Electric damage**: Shocker, MiningLaser, PulseTank only
- **Hitscan**: Ray, MiningLaser only
- **All rockets share gravity 0.1** (nearly flat trajectory)

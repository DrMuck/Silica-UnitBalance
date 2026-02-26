# Silica Unit Dump Knowledge

Collected via Si_UnitBalance dump_fields feature (Feb 2026).

---

## Crab (CreatureDecapod)

### CreatureDecapod (root)
- `MoveSpeed = 9`
- `AIMeleeDistance = 100`
- `HasRangedAttack = False`
- `HitSlowDamage = 15`

### CreatureAttack:AttackPrimary (MELEE — lunge claws)
- `Enabled = True`
- `Time = 0.5`
- `HeatUpPerAttack = 0.25`
- `CoolDownTime = 1`
- `DamageTimePct = 0.8`
- `Damage = 240`
- `Radius = 1.5`
- `Type = Stab`
- `AttackProjectileData = null` (no projectile, melee)
- `AttackProjectileAimDistMax = 300`
- `PhysicsImpulse = 50`
- `IsRanged = False`, `IsMelee = True`
- `CanMoveDuringAttack = False`
- `CanAttackFlying = True`

### CreatureAttack:AttackSecondary (MELEE — claws)
- `Enabled = True`
- `Time = 0.5`
- `HeatUpPerAttack = 0`
- `CoolDownTime = 3`
- `Damage = 240`
- `Radius = 1`
- `Type = Stab`
- `AttackProjectileData = null`
- `AttackProjectileAimDistMax = 300`
- `PhysicsImpulse = 100`
- `IsRanged = False`, `IsMelee = True`
- `CanMoveDuringAttack = True`

### DamageManager
- `MaxHealth = 300`
- `ArmorHitAdjust = 0`
- `Data = Alien_Crab (DamageManagerData)`

---

## Behemoth (CreatureDecapod)

### CreatureDecapod (root)
- `MoveSpeed = 9`
- `AIMeleeDistance = 100`
- `HasRangedAttack = True`
- `HitSlowDamage = 200`

### CreatureAttack:AttackPrimary (RANGED — LASER/RAY)
- `Enabled = True`
- `Time = 0.2`
- `HeatUpPerAttack = 0.2`
- `CoolDownTime = 2`
- `DamageTimePct = 0.2`
- `Damage = 0` (damage from ProjectileData)
- `Radius = 0.1`
- `Type = Impact`
- **`AttackProjectileData = ProjectileData_Ray`** (NOT ProjectileData_Behemoth!)
- `AttackProjectileCount = 1`
- **`AttackProjectileAimDistMax = 400`** (RANGE LIMITER for AI firing)
- `AttackProjectileAimDistMin = 0`
- `AttackProjectileAimAngle = 3`
- `AttackProjectileSpread = 0.65`
- `AttackTransform = Muzzle`
- `MuzzleFlashData = MF_Ray`
- `AttackSoundEffectData = SFX_BehemothAttack_RayFire`
- `IsRanged = True`, `IsMelee = False`
- `CanMoveDuringAttack = True`
- `CanAttackDuringAttack = True`
- `PlayerTurnToFace = False`
- `AICheckFriendlyFire = False`

### CreatureAttack:AttackSecondary (MELEE — scythe stomp)
- `Enabled = True`
- `Time = 0.75`
- `HeatUpPerAttack = 0`
- `CoolDownTime = 3`
- `DamageTimePct = 0.85`
- **`Damage = 2000`** (massive melee hit)
- `Radius = 3.1`
- `Type = Slash`
- `AttackProjectileData = null` (no projectile, melee)
- `AttackProjectileAimDistMax = 300`
- `PhysicsImpulse = 50000`
- `AttackSoundEffectData = SFX_BehemothAttack_Scythes`
- `ScreamSoundEffectData = SFX_BehemothScream`
- `IsRanged = False`, `IsMelee = True`
- `CanMoveDuringAttack = True`
- `CanAttackDuringAttack = True`

### UnitAimAt (root)
- `AimDistanceMax = 2000`
- `AimDistanceMin = 5`

### Sensor (child "AlienSensorFar_01")
- `TargetingDistance = 800`
- `FogOfWarViewDistance = 300`
- `NightBonus = 0.4`
- `SensitivityFOV = 140`

### DamageManager
- `MaxHealth = 10000`
- `ArmorHitAdjust = 0.1`
- `Data = Alien_Behemoth (DamageManagerData)`

### CreatureTurret (root, 2 instances)
- PrimaryTurret (Muzzle) — laser turret
- SecondaryTurret (HeadAim) — melee head aim

---

## Range Control Chain (Behemoth Laser)

For the Behemoth to fire its laser, ALL of these must be satisfied:
1. **Sensor.TargetingDistance = 800** — AI must detect the target
2. **UnitAimAt.AimDistanceMax = 2000** — aim system must accept the distance
3. **CreatureAttack.AttackProjectileAimDistMax = 400** — attack must be within this range
4. **ProjectileData_Ray** — projectile must have sufficient range

The bottleneck is likely **AttackProjectileAimDistMax = 400** since it's the smallest value.
To increase Behemoth laser range, scale ALL of:
- `CreatureAttack.AttackProjectileAimDistMax` (on AttackPrimary)
- `UnitAimAt.AimDistanceMax`
- `Sensor.TargetingDistance`
- `ProjectileData_Ray.m_fBaseSpeed` (this IS the ray distance for instant-hit weapons)

---

## ProjectileData_Ray (Behemoth Laser Projectile)

**IMPORTANT**: `m_InstantHit = True` means `m_fBaseSpeed` is the **raycast distance** (not speed).

| Field | Value | Notes |
|-------|-------|-------|
| m_fBaseSpeed | 400 | **RAY DISTANCE** (max beam reach) |
| m_fLifeTime | 1 | Visual beam duration (seconds) |
| m_InstantHit | True | Hitscan — no travel time |
| m_fInstantTimeSlice | 0.1 | |
| m_fRaycastRadius | 0 | Line trace (no sphere cast) |
| m_fDragCoefficient | 0.3 | |
| m_fDiameter | 12 | |
| m_fMass | 20000 | |
| m_fAfterLifeWaitTime | 0.2 | |
| m_TrailColor | RGBA(1.0, 0.2, 0.53, 1.0) | Pink/magenta |
| m_bTrailConstant | True | |
| m_fTrailLengthScale | 1 | |
| m_fTrailWidthScale | 1.5 | |
| m_fCoreIntensityScale | 5 | |
| m_fLightIntensityScale | 2 | |
| m_fTrailIntensityScale | 10 | |
| m_ProjectilePrefab | Projectile_Ray | Visual prefab |
| m_DestroyEffectData | IFX_RailgunRicochet | Hit effect |
| m_DestroySoundData | SFX_ShellRicochet | Hit sound |
| **VisibleEventRadius** | **600** | **Visual render distance for beam** |
| m_fImpactDamage | 400 | Direct hit damage |
| m_fRicochetDamage | 400 | Ricochet damage |
| MaxDistance | 400 | Read-only property (computed from m_fBaseSpeed) |

### VisibleEventRadius
- Controls how far from the observer the beam visual is rendered
- Default 600 for ProjectileData_Ray — beam visual stops at 600m even if raycast goes further
- **Must be scaled by rangeMult** alongside m_fBaseSpeed for extended-range weapons

### Instant-Hit vs Normal Projectiles
- **Normal** (`m_InstantHit = False`): range = `m_fBaseSpeed * m_fLifeTime`. Scale lifetime for range.
- **Instant-hit** (`m_InstantHit = True`): range = `m_fBaseSpeed`. Scale speed for range. Lifetime = visual duration only.

---

## CreatureAttack Type — Full Field List

### Fields (public instance)
| Field | Type | Notes |
|-------|------|-------|
| Enabled | Boolean | Whether attack is active |
| Time | Single | Attack animation time |
| HeatUpPerAttack | Single | Heat gained per attack (overheats when full) |
| CoolDownTime | Single | Cooldown between attacks |
| DamageTimePct | Single | When in animation damage occurs (0-1) |
| Damage | Single | Direct damage (0 if uses projectile) |
| Radius | Single | Attack hit radius |
| UseViewRotationPct | Single | How much view rotation affects aim |
| AttackTransform | Transform | Muzzle/origin transform |
| Offset | Vector3 | Attack origin offset |
| ImpactNormalBias | Vector3 | Impact normal adjustment |
| Type | EDamageType | Stab, Impact, Slash, etc. |
| MoveForward | AnimationCurve | Forward movement during attack |
| CanMoveDuringAttack | Boolean | |
| CanClimbDuringAttack | Boolean | |
| CanAttackDuringAttack | Boolean | Can chain attacks |
| CanAttackDuringFall | Boolean | |
| CanAttackEmerged | Boolean | |
| CanAttackFlying | Boolean | |
| CanAttackSubmerged | Boolean | |
| PlayerTurnToFace | Boolean | |
| ImpactEffectData | ImpactEffectData | Visual effect on hit |
| MuzzleFlashData | ImpactEffectData | Muzzle flash effect |
| MuzzleFlashScale | Single | |
| DecalData | ImpactEffectData | Decal on hit |
| SoundEffectData | AudioEffectData | Hit sound |
| AttackSoundEffectData | AudioEffectData | Attack fire sound |
| ScreamSoundEffectData | AudioEffectData | Scream sound |
| **AttackProjectileData** | **ProjectileData** | **Projectile used (null = melee)** |
| AttackProjectileCount | Int32 | Projectiles per attack |
| AttackSoundTimePct | Single | When sound plays in animation |
| ScreamSoundTimePct | Single | When scream plays |
| ScreamSoundChancePct | Single | Chance of scream |
| AttackProjectileSpread | Single | Projectile spread |
| AttackProjectileAimAngle | Single | Aim cone angle |
| AttackProjectileAimDistMin | Single | Min aim distance |
| **AttackProjectileAimDistMax** | **Single** | **Max aim distance (RANGE CAP)** |
| ImpactSizeScale | Single | Impact visual scale |
| PhysicsImpulse | Single | Knockback force |
| AttackProjectileAimTrace | Boolean | Whether to trace aim |
| AICheckFriendlyFire | Boolean | AI checks friendly fire |
| RangedHasValidCollider | Boolean | |

### Properties (read-only)
| Property | Type | Notes |
|----------|------|-------|
| CreatureOwner | CreatureDecapod | Owning creature |
| IsRanged | Boolean | True if has projectile |
| IsMelee | Boolean | True if no projectile |
| IsPrimary | Boolean | |
| IsOverheated | Boolean | |
| IsAttacking | Boolean | |
| IsVisible | Boolean | |
| CurrentHeatUpPct | Single | |
| CurrentAttackTime | Single | |
| CurrentAttackTime01 | Single | |
| AttackTotalForwardMove | Single | |

---

## Commando (Soldier — Infantry)

### Soldier (root)
- `WalkSpeed`, `RunSpeed`, `SprintSpeed`, `JumpSpeed`, `CrouchSpeedModifier`
- Modified via `move_speed_mult` config

### Key: Client-side movement
- Player movement is client-authoritative
- Server prefab changes only affect AI
- Client needs `ApplyClientMoveSpeedOverrides` via `FindObjectsOfTypeAll<T>()`

---

## Rifleman / Hover Tank

See earlier dumps in server logs. Hover Tank uses ProjectileData for weapon range.

---

## General Architecture Notes

### Component vs Data Object
- **Components** (MonoBehaviour): on GameObjects, found via GetComponent/GetComponentsInChildren
  - Examples: CreatureDecapod, UnitAimAt, Sensor, DamageManager, CreatureTurret
- **Data Objects**: referenced as fields on components, NOT on GameObjects
  - Examples: CreatureAttack, ProjectileData, DamageManagerData, ObjectInfo
  - Must access via reflection on the parent component

### DamageManagerData Fields
The `DamageManager.Data` field points to a `DamageManagerData` ScriptableObject asset.
- `Health` (Single) — the actual **MaxHealth** backing field (NOT called "MaxHealth")
- `ArmorHitAdjust` (Single) — armor damage reduction factor
- `MultiplyHealthByScale` (Boolean) — whether to scale HP by object scale
- `DamageModifiers` (List) — damage type modifiers
- `DestructionDisable/NoCollide/MaterialSwap/...` — destruction behavior

`DamageManager.MaxHealth` (property) reads from `DamageManagerData.Health`.
To modify MaxHealth, set `DamageManagerData.Health` — this is a shared asset so all instances update.

### Il2Cpp vs Mono Reflection

On the **dedicated server** (Mono/MelonLoader v0.7.1), Unity serialized fields are exposed as **fields** via reflection.
On the **client** (Il2Cpp/MelonLoader v0.7.2), the same serialized fields are exposed as **properties**, not fields.

**Pattern**: Always try `GetField()` first, then fall back to `GetProperty()`:
```csharp
object val = null;
var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
if (field != null) val = field.GetValue(obj);
else {
    var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
    if (prop != null && prop.CanRead) val = prop.GetValue(obj);
}
```

This applies to:
- `CreatureDecapod.AttackPrimary` / `AttackSecondary` — properties on Il2Cpp
- `CreatureAttack.AttackProjectileAimDistMax` — property on Il2Cpp
- `CreatureAttack.AttackProjectileData` — property on Il2Cpp
- `ProjectileData.m_InstantHit` — property on Il2Cpp (use NonPublic flag too)
- All `float` members accessed via `GetFloatMember`/`SetFloatMember` helpers (already handle both)

### CreatureDecapod Hierarchy
```
CreatureDecapod (component on root)
  ├── AttackPrimary: CreatureAttack (data object - ranged or melee)
  ├── AttackSecondary: CreatureAttack (data object - usually melee)
  ├── MoveSpeed: float
  ├── AIMeleeDistance: float
  └── HasRangedAttack: bool

UnitAimAt (component on root)
  ├── AimDistanceMax: float
  └── AimDistanceMin: float

Sensor (component on child "AlienSensorFar_01")
  └── TargetingDistance: float

CreatureTurret (component on root, 1-2 instances)
  └── PrimaryTurret / SecondaryTurret

DamageManager (component on root)
  ├── MaxHealth: float (via property)
  └── Data: DamageManagerData
```

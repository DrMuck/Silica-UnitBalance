# Si_UnitBalance — Change Log

Tracks completed changes and tasks for the Si_UnitBalanceUI project.

---

## 2026-03-04 — Pri/Sec Turret Weapon Mapping (`_vtPriIndex`)

### Problem
- Bomber/Freighter had bombs as "pri" and cannon as "sec" — unintuitive (cannon is the main weapon)
- Shuttle, Gunship, Platoon Hauler had their only weapon on the 2nd VehicleTurret, but C# tagged it as "sec" while config had it as "pri" — multipliers wouldn't apply

### Fix: `_vtPriIndex` dictionary
- New `_vtPriIndex` dict in `Si_UnitBalance.Overrides.cs` maps unit name → which VehicleTurret index is the primary weapon (default: 0)
- Entries: Bomber(1), Freighter(1), Shuttle(1), Gunship(1), Platoon Hauler(1)
- Used in 4 places: `ApplyProjectileDamageOverrides`, `ApplyRangeOverrides` (AimDistance + weapon params + PD field scan), `GetWeaponPDObjects`

### Config Changes
- **Bomber**: pri=Shell_StealthBomber (cannon), sec=Bomb_DropBomb (bombs) — swapped
- **Freighter**: pri=Shell_Dreadnought (cannon), sec=Bomb_ContainerBomb (bomb) — swapped
- `turret_stats_prefix` updated: Bomber/Freighter now `{'pri': 'vt3_', 'sec': 'vt_'}`

---

## 2026-03-04 — m_bPenetrating Boolean Fix + Multi-Turret Corrections

### m_bPenetrating Fix
- **20 projectiles** had `m_fPenetratingDamage > 0` but `m_bPenetrating = False` (penetrating damage disabled in game engine)
- These were incorrectly showing penetrating damage multipliers in UI and default config
- Analogous to the m_bSplash fix — same pattern of disabled-but-nonzero values
- Only **7 projectiles** have m_bPenetrating=True: Shell_HoverTank, Shell_HeavyArmoredCar, Shell_LightArmoredCar, Shell_DreadnoughtCannon, Shell_Dreadnought, Shell_Interceptor, Railgun_RailgunTank

### Changes
- `gen_default_config.py` — removed pen from 20 projectile entries in `projectile_db`; added ricochet=30 to HMG_StealthFighter, ricochet=120 + pen=220 to Shell_Interceptor; added Bomb_DropBomb and Bomb_ContainerBomb entries
- `Si_UnitBalance.Overrides.cs` — `ScaleProjectileDamage()` now checks `m_bPenetrating` before scaling `m_fPenetratingDamage`
- `Si_UnitBalance.Menu.cs` — `GetDamageKeysForProjectile()` now checks `m_bPenetrating` before showing penetrating_damage_mult in UI

### Multi-Turret Data Corrections (from new dump)
- New dump revealed actual turret slot assignments for all aircraft
- **Removed `turret_field_swap`** — gun IS on vt_ (first turret) for Fighter/Interceptor, not later turrets
- **Added `turret_stats_prefix`** — flexible per-unit mapping for turret stat sources:
  - Bomber: pri=vt_ (bombs), sec=vt3_ (cannon)
  - Freighter: pri=vt_ (bomb), sec=vt3_ (cannon)
  - Gunship: pri=vt3_ (gun on 2nd VehicleTurret)
  - Shuttle: pri=vt3_ (cannon on 2nd VehicleTurret)
  - Platoon Hauler: pri=vt3_ (weapon on 2nd VehicleTurret)
- **Bomber**: Added pri=Bomb_DropBomb (bombs, fi=0.25 mag=24), sec=Shell_StealthBomber (cannon, fi=0.45 mag=2)
- **Freighter**: Added pri=Bomb_ContainerBomb (bomb, fi=0.5 mag=1), sec=Shell_Dreadnought (cannon, fi=0.25 mag=4)
- Updated dump file to v4 multiturret
- Config: 1242 → 1265 params (new weapon keys for Bomber/Freighter secondaries)

### Multi-Turret C# Runtime Fix
- `Si_UnitBalance.Menu.cs` — `GetWeaponPDObjects()` now treats 2nd VehicleTurret's PrimaryProjectileData as `secPD` (was being ignored)
- `Si_UnitBalance.Overrides.cs` — Added `vtIndex` tracker to both `ApplyProjectileDamageOverrides()` and `ApplyRangeOverrides()`:
  - 1st VehicleTurret: Primary→"pri", Secondary→"sec"
  - 2nd+ VehicleTurret: Primary→"sec" (second weapon)
  - Affects damage scaling, weapon params (reload/fire_rate/magazine/accuracy), and projectile range/speed/lifetime

---

## 2026-03-04 — vehicle_projectiles Audit

### Findings
- **Only 8 out of 31** `vehicle_projectiles` entries in `gen_default_config.py` match the dump data
- Root cause: mapping was built from assumptions, not from dump's `vt_proj`/`vt2_proj` fields

### Specific Issues Found
- **Squad Transport**: pri should be `HMG_ArmedTransport` (not `MMG_TroopHauler`), has NO secondary weapon (`vt2_proj` empty)
- **Platoon Hauler**: has NO secondary weapon (`vt2_proj` empty) — remove `sec`
- **Assault Car**: pri should be `Shell_LightArmoredCar` (not `Shell_HeavyArmoredCar`), sec should be `LMG_LightArmoredCar` (not `HMG_LightArmoredCar2`)
- **Light Striker**: pri should be `HMG_LightArmoredCar2` (not `LMG_LightArmoredCar`), has NO secondary weapon
- **Heavy Striker**: sec should be `MMG_HeavyArmoredCar` (not `HMG_OldHeavy`)
- **Dreadnought**: pri/sec swapped (dump: vt=DreadnoughtCannon, vt2=Dreadnought)
- **Gunship**: pri/sec swapped (dump: vt=HMG_Gunship, vt2=Rocket_StealthGunship)
- **Light Quad**: pri should be `MMG_LightQuad2` (not `LMG_LightQuad`)
- **Heavy Quad**: pri should be `MMG_HeavyQuad2` (not `HMG_HeavyQuad`)
- **Heavy Raider**: pri should be `HMG_HeavyQuad` (not `LMG_LightArmoredCar`)
- **Light Raider**: pri should be `LMG_LightQuad` (not `LMG_LightArmoredCar`)
- **Fighter**: pri should be `Bomb_DiveBomb` (not `Rocket_StealthFighter`)
- **Freighter**: pri should be `Shell_Dreadnought` (not `LMG_CrimsonFreighter`)
- **Shuttle**: pri should be `Shell_DreadnoughtCannon` (not `Shell_ShuttleCannon`)
- **Interceptor**: pri should be `Bomb_DropTank` (not `Shell_Interceptor`)
- **13 fake secondaries**: units with `sec` entries in mapping but empty `vt2_proj` in dump (AA Truck, Bomber, Crimson Tank, Dropship, Fighter, Flak Car, Freighter, Interceptor, Light Striker, Platoon Hauler, Siege Tank, Squad Transport, Strike Tank)
- Secondary condition `if vp.get('sec') or u['vt2_magazine'] > 0:` creates false positives (e.g. Squad Transport has vt2_magazine=100 but no weapon)

### Note on `vt2_proj` gaps
Some aircraft (Bomber, Dropship, Fighter, Interceptor, Freighter) may have secondary weapons via non-VehicleTurret systems (bombs, special abilities) that the dump's `vt2_proj` field doesn't capture. These need manual verification in-game.

### Fix Applied
- Corrected all 31 `vehicle_projectiles` entries to match dump's `vt_proj`/`vt2_proj` fields
- Removed 13 fake secondaries (units with no `vt2_proj` in dump)
- Removed Hover Bike (no VehicleTurret — passengers use infantry weapons)
- Fixed secondary condition: `if vp.get('sec')` (removed `u['vt2_magazine'] > 0` fallback)
- Config reduced from 1378 → 1242 params (136 removed fake/wrong weapon keys)
- Regenerated, built, deployed

### Fighter/Interceptor Gun Weapons
- Fighter: pri=`HMG_StealthFighter` (gun, imp:100), sec=`Bomb_DiveBomb` (bomb)
- Interceptor: pri=`Shell_Interceptor` (gun, imp:350 spl:220), sec=`Bomb_DropTank` (bomb)
- Added `turret_field_swap` set in gen_default_config.py — swaps vt_/vt2_ turret stats for units where dump's vt_proj is actually the secondary weapon (bomb)
- Gun turret stats in annotations are approximate (from default VehicleTurret values) pending multi-turret dump

### Multi-VehicleTurret Dump Support
- `Si_UnitBalance.LivePropagation.cs`: dump now tracks `vtCount` and captures second VehicleTurret data as `vt3_*` fields
- New JSON fields: `vt_count`, `vt3_proj`, `vt3_fire_interval`, `vt3_magazine`, `vt3_reload`, `vt3_impact_dmg`, etc.
- Next server run will reveal whether Fighter/Interceptor guns are on a separate VehicleTurret
- Config: 1242 → 1260 params (18 new weapon keys for Fighter/Interceptor guns)

---

## 2026-03-04 — m_bSplash Boolean Fix

### Bug Fix
- **12 projectiles** had `m_fSplashDamageMax > 0` but `m_bSplash = False` (splash disabled in game engine)
- These were incorrectly showing splash damage/radius multipliers in UI and default config
- Affected projectiles: SG_CombatTank, LMG_LightArmoredCar, HMG_HeavyQuad, LMG_LightQuad, HMG_ArmedTransport, HMG_StealthDropship, MMG_HoverTank, Railgun_RailgunTank, MMG_HeavyQuad2, MMG_HeavyArmoredCar, MMG_LightQuad2, MMG_TroopHauler

### Changes
- `gen_default_config.py` — removed splash/splash_r_max/splash_r_min/splash_r_pow from 12 projectile entries in `projectile_db`
- `Si_UnitBalance.Overrides.cs` — added `GetBoolMember()` helper; `ScaleProjectileDamage()` now checks `m_bSplash` before scaling splash damage and splash radius fields
- `Si_UnitBalance.Menu.cs` — `GetDamageKeysForProjectile()` now checks `m_bSplash` boolean before showing splash damage/radius keys in UI
- Config reduced from 1438 → 1378 params (60 removed splash/radius keys)

---

## 2026-03-03 — Per-Damage-Type Multipliers + Splash Radius

### Damage Sub-Type Multipliers
- **Replaced** single `damage_mult` with per-sub-type multipliers for ProjectileData fields:
  - `impact_damage_mult` → `m_fImpactDamage`
  - `splash_damage_mult` → `m_fSplashDamageMax`
  - `penetrating_damage_mult` → `m_fPenetratingDamage`
  - `ricochet_damage_mult` → `m_fRicochetDamage`
- Supports `pri_`/`sec_`/shared prefixes (12 new config keys)
- 4-level fallback: `pri_impact_damage_mult` > `impact_damage_mult` > `pri_damage_mult` > `damage_mult` > 1.0
- Old `damage_mult` still works as blanket fallback (backward compatible)
- Melee creatures unchanged — still use single `damage_mult`
- UI dynamically shows only relevant sub-types per unit (e.g. Pulse Truck: no impact)
- Base values displayed from actual ProjectileData fields at runtime

### Splash Radius Multipliers
- Added 3 splash radius multipliers (only shown for units with splash damage):
  - `splash_radius_max_mult` → `m_fSplashDamageRadiusMax` (max radius, 0% damage beyond)
  - `splash_radius_min_mult` → `m_fSplashDamageRadiusMin` (min radius, 100% damage within)
  - `splash_radius_pow_mult` → `m_fSplashDamageRadiusPow` (falloff exponent)
- Supports `pri_`/`sec_`/shared prefixes (9 config keys)
- 2-level fallback: `pri_splash_radius_max_mult` > `splash_radius_max_mult` > 1.0

### Files Modified
- `Si_UnitBalance.cs` — config parsing for 21 new keys + `_splashRadiusMultipliers` dictionary
- `Si_UnitBalance.Overrides.cs` — `GetDamageFieldMult()`, `GetSplashRadiusMult()`, `HasAnyDamageMult()`, updated `ScaleProjectileDamage()`
- `Si_UnitBalance.Menu.cs` — `GetDamageKeysForProjectile()`, `GetWeaponPDObjects()`, dynamic weapon param arrays, base value display cases
- `gen_default_config.py` — `projectile_db` with all 4 damage sub-types + splash radius, `emit_damage_keys()`, annotations show `spl:600/r15`

### Harvester Split (re-applied)
- `gen_default_config.py`: Split single "Harvester" back into "Sol Harvester" (hovered_vehicle) and "Cent Harvester" (wheeled_vehicle)
- C# `ResolveConfigName()` already handled this — only the default config generator needed fixing

---

## 2026-03-02 — v7.5.0 Push

- Removed `[CREATURE-DBG]` diagnostic logging (6 log lines + debug variables)
- Discord webhook edit-in-place for balance change notifications
- Base value caching for UI display
- Behemoth range fix (ProjectileData_Ray, not ProjectileData_Behemoth)
- Pushed as commit `30241cb` to `https://github.com/DrMuck/Silica-UnitBalance.git`

---

## Key Design Decisions

- **Composite key storage**: Damage and splash radius multipliers stored in shared dictionaries using composite keys (`"pri:impact:Hover Tank"`) instead of separate dictionaries per sub-type
- **Dynamic UI keys**: Weapon param lists built at runtime by inspecting ProjectileData non-zero fields, not static arrays
- **OverrideManager for ProjectileData**: Damage/splash fields scaled via OM (synced to clients) — no Harmony patches needed
- **Harvester split**: `ResolveConfigName()` maps both factions' "Harvester" DisplayName to "Sol Harvester" / "Cent Harvester" config keys based on internal name

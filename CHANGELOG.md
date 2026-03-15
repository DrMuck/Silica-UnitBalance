# Si_UnitBalance — Change Log

Tracks completed changes and tasks for the Si_UnitBalanceUI project.

---

## 2026-03-14 — Health Diagnostics, Behemoth Accuracy Fix, Proximity Detonation Support, Tech Time Diagnostics

### Refinery Health Bar Fix (Shared DamageManagerData)
- **Problem**: Refineries showed ~25% less than max health on fresh build, even with `health_mult: 1.0`. Root cause: `DamageManager.MaxHealth` is a live computed property from `DamageManagerData.Health`, while `HealthInternal` is set once at spawn. When another unit sharing the same DamageManagerData gets a health_mult applied, the Refinery's MaxHealth increases but its current Health stays at the old value.
- **Fix**: `ReclampLiveHealth()` now **maintains health percentage** for all live DamageManagers whose Data asset was modified. Previously only clamped DOWN (HP > MaxHealth); now also scales UP. Units at full health before the change stay at full health after. Units at partial health maintain their percentage.
- Also: `_originalHealth` anti-compounding cache, `modifiedDMD` HashSet, `[HEALTH-DIAG]` diagnostics.

### Behemoth Accuracy Fix (Instant-Hit Ray Weapons)
- **Problem**: `accuracy_mult` modified `AttackProjectileSpread` which has no effect on instant-hit (raycast) weapons — the ray goes straight to target regardless of spread.
- **Fix v1**: For instant-hit creatures, `accuracy_mult` now also scales `AttackProjectileAimAngle` (the AI's aiming cone). Behemoth AimAngle: 3.0 → 1.5 with `accuracy_mult: 0.50`. Uses `_originalAimAngle` cache to prevent compounding.
- **Fix v2 (2026-03-15)**: AimAngle scaling was nested inside `if (!float.IsNaN(spread))` — if `AttackProjectileSpread` couldn't be read (returns NaN), the entire AimAngle block was skipped. Moved instant-hit AimAngle scaling outside the spread NaN check so it runs independently.
- **Status**: Built, awaiting deploy (server must be stopped first — DLL locked).

### Proximity Detonation Support (Flak Truck)
- **New feature**: Config support for `ProjectileProximityDetonation` component on projectile prefabs.
- Config format: `"proximity"` sub-object inside per-projectile overrides:
  ```json
  "projectiles": {
      "ProjectileData_Flak_AAFlakCar": {
          "m_fBaseSpeed": 250,
          "proximity": {
              "MinimumTime": 0.5,
              "SplashRadiusScale": 2.0,
              "FlyingUnits": true,
              "GroundUnits": false,
              "Structures": true
          }
      }
  }
  ```
- Fields: `MinimumTime` (arming delay, default 0.25s), `SplashRadiusScale` (proximity detection radius scale, default 1.5), `FlyingUnits`/`GroundUnits`/`Structures` (bool toggles).
- Finds component via `ProjectileData.m_ProjectilePrefab` → `GetComponentsInChildren`.

### Tech Time Diagnostics
- **Problem**: Tech time changes appeared "additive not replacive" — unclear root cause.
- **Fix**: Enhanced `[TECH]` logging to include ConstructionData asset name, verify value after OM.Set, warn on OM failure with fallback, and log unconfigured tech tiers.

### Proximity Detonation In-Game UI (`!b` menu)
- Added "Proximity Detonation" parameter group for units with `ProjectileProximityDetonation` (e.g. Flak Car)
- Displays: MinimumTime, SplashRadiusScale, FlyingUnits, GroundUnits, Structures
- Bool fields show as True/False, accept 1/0 input
- Writes to nested JSON config (`projectiles -> PDName -> proximity -> field`)
- Auto-discovers ProjectileData name for the unit

### Flak Explosion Visual Distance
- **Finding**: Explosion visibility at range is controlled by `ImpactEffectLOD.MaxDistance * m_fDetonateImpactSizeScale` (client-side rendering). Trail has hardcoded 400m cutoff.
- **Workaround**: `m_fDetonateImpactSizeScale` is a ProjectileData field synced via OM. Increasing it extends explosion visual range (but also scales explosion visual size). Already configurable via per-projectile overrides:
  ```json
  "ProjectileData_Flak_AAFlakCar": { "m_fDetonateImpactSizeScale": 3.0 }
  ```

### Files Changed
- `Si_UnitBalance.cs` — new dictionaries (`_originalHealth`, `_originalAimAngle`, `_proximityOverrides`), proximity config parsing
- `Si_UnitBalance.Overrides.cs` — health fix (ReclampLiveHealth rescale), AimAngle scaling for instant-hit, `ApplyProximityDetonationOverrides()`, enhanced tech time logging
- `Si_UnitBalance.Menu.cs` — proximity UI (GetBaseValuesLive prox_* cases, WriteProximityToJson, ReadProximityConfigValue, FindUnitProjectileDataName), GetProximityComponent helper
- `Si_UnitBalance.Patches.cs` — proximity write routing in confirm handler, wire proximity overrides in rebalance, clear new caches
- `Si_UnitBalance.Lifecycle.cs` — wire proximity overrides, clear new caches on game end

---

## 2026-03-08 — Same-Map Restart Fix, Remove Revert on Round End

### Same-Map Restart Fix
- **Problem**: After `!rebalance`, `_overridesApplied` was not set back to `true`, causing `ApplyOverridesLogic` (with `OMRevertAll`) to fire on the next same-map restart — reverting prefabs and breaking starter structure overrides.
- **Fix**: `!rebalance` now sets `_overridesApplied = true` after re-applying overrides.

### Remove "Revert on Round End" Setting
- Hard-coded `_revertOnRoundEnd = false` — reverting at round end breaks starter structure overrides and is no longer needed.
- Removed from: HTP in-game menu, config JSON loading, Discord comparison, Interactive web tool (state.js + editor.js).

### Map Change Detection via Scene Name
- **Problem**: `Patch_GameInit` may not fire on all map changes, leaving `_overridesApplied = true` and skipping overrides entirely on the new map. Also, Apply* methods read current prefab values and multiply — re-applying on same-map restarts caused multiplier compounding.
- **Fix**: Track map name via `SceneManager.GetActiveScene().name`. On map change, clear vanilla cache and re-apply from fresh prefabs. On same-map restart, skip re-apply (prefabs already modified).
  - `OMRevertAll` in `ApplyOverridesLogic` ensures clean OM state on map change (notify=true syncs clients); never fires on same-map restarts since `_overridesApplied = true` skips entire function
  - `CacheVanillaBaseValues` only runs when cache is empty (first call or after map change)
  - `Patch_GameRestart` and `OnGameStartedLogic` both detect map changes as safety nets
  - Game end no longer resets `_overridesApplied`
  - Key insight (databomb): game calls `OverrideManager.RevertAll` inside `GameLevelLoader.LoadAsyncScene` during map changes — overrides must be re-applied after this

---

## 2026-03-07 — Additional Spawn Overhaul, Voting-Phase Overrides

### Additional Spawn Units Updated
- **Sol**: 2x Platoon Hauler, 2x Light Striker, 2x Heavy Quad (was 3x Platoon Hauler)
- **Centauri**: 2x Squad Transport, 2x Assault Car, 2x Heavy Raider (was 3x Squad Transport)
- **Alien**: 3x Hunter, 2x Squid, 4x Shocker, 1x Wasp (was 1x Hunter)

### Spawn Fall Damage Fix
- `DamageManager.DamageDisabled = true` during spawn, kept active for 20 seconds (try/finally for safety)
- Units distributed evenly in a circle around HQ (360°/totalUnits) instead of per-entry circles that caused overlapping
- Terrain height sampling via `Terrain.SampleHeight()` — units spawn 1m above ground instead of at HQ altitude

### Override Lifecycle Fix (Starter Structures)
- **Problem**: Starter HQ/Nest spawned with vanilla values (especially FOW) on rounds after the first. Only expansion structures got modded values.
- **Root cause**: Overrides applied too late (`OnGameStarted`, after starters already spawned). On map change, fresh prefabs had no overrides when starters spawned.
- **Fix**: Overrides now apply in `Patch_GameInit` (map load, before starters spawn) — replicating the working first-game-after-server-start behavior.
  - `Patch_GameInit`: Resets flag + calls `ApplyOverridesLogic()` on fresh prefabs
  - `Patch_GameRestart`: Safety net for same-map restarts (applies only if not already done)
  - `Patch_GameEnded`: No longer calls `OMRevertAll()` or resets `_overridesApplied` — prefabs stay modified between rounds so same-map restarts keep modded values on starters
  - `ApplyOverridesLogic`: Always does atomic `OMRevertAll` + re-apply to prevent multiplier compounding (only called on map change or first round)
  - `OnGameStartedLogic`: Now only handles propagation to live instances, additional spawns, and client sync

---

## 2026-03-06 — LivePropagation Fix, Menu Shortcuts, Run/Sprint Speed

### LivePropagation Il2Cpp Fix
- **Bug**: `LiveScaleFieldDeclaredOnlyLog` and `LiveScaleFieldDeclaredOnly` only used `GetField()` — on Il2Cpp, Soldier speed fields (`WalkSpeed`, `RunSpeed`, `SprintSpeed`) are properties, not fields
- Server-side instances of Soldier units never had their speed updated after `!rebalance` (no `[LIVE-DBG]` log entries for Heavy)
- **Fix**: Added property fallback (`GetProperty()`) to both methods, matching the pattern already used in `ApplyMoveSpeedOverrides`
- This also fixes accuracy (`MuzzleSpread`) propagation to existing server instances

### Menu Shortcuts Extended
- Registered `/10` through `/20` as shortcut commands (was `/1`-`/9` only)
- Unit lists with 10+ items (e.g. Sol structures: 12) can now be selected via shortcuts
- Error message updated to reflect `/1-/20` range

### Run/Sprint Speed Multipliers
- Added `run_speed_mult` and `sprint_speed_mult` config params for Soldier (infantry) units
- `RunSpeed` uses `run_speed_mult` if set, falls back to `move_speed_mult`
- `SprintSpeed` uses `sprint_speed_mult` if set, falls back to `move_speed_mult`
- Same pattern as `turbo_speed_mult` / `fly_speed_mult` for vehicles
- Soldier Movement menu now shows: `move_speed_mult`, `run_speed_mult`, `sprint_speed_mult`, `jump_speed_mult`
- Applied in both `ApplyMoveSpeedOverrides` (OM path) and `PropagateToLiveInstances`

### Missing Structures (from earlier)
- Added Research Facility, Radar Station, Silo to Menu.cs, gen_default_config.py, schema.js, default config JSON

---

## 2026-03-06 — Health Mult Server-Only Limitation

### Investigation
- Testers reported health bars showing vanilla values despite `health_mult` being applied
- Root cause: `DamageManagerData` is NOT registered in the game's built-in OverrideManager (`BuildSources()` only registers 7 types: GameModeInfo, LevelInfo, Team, ConstructionData, Resource, ProjectileData, ObjectInfo)
- All alternative paths to sync health to clients are blocked:
  - `DamageManager.MaxHealth` — read-only computed property (no setter)
  - `ObjectInfo.MaxHealth` — `[NonSerialized]` field (filtered by `IsMemberValid`)
  - `DamageManager.Health` setter clamps to `MaxHealth` — even if server sets HP=500000, client clamps to vanilla MaxHealth
- **Conclusion**: Health multiplier works server-side only. Cannot sync to clients without a client-side mod.

### Changes
- Added `health_mult_enabled` config setting (default: `false`) — must be explicitly enabled since it's server-only
- HTP menu item 9: toggle Health Mult ON/OFF with "(server-only)" remark
- Discord webhook: reports `health_mult_enabled` toggle changes
- Si_UnitBalance_Interactive: `health_mult` tooltip updated with server-only warning
- Default config: `health_mult_enabled: false` with server-only comment

---

## 2026-03-06 — Structure Vision & Turret Target Range

### Starter HQ FOW Fix — `revert_on_round_end` config setting
- **Problem**: `OMRevertAll()` on game end resets prefabs to vanilla → starter HQs spawn from vanilla prefab before `OnGameStartedLogic()` runs, so they miss FOW/target overrides
- **Solution**: Added `revert_on_round_end` JSON config setting (default: `true`)
  - When `false`, `OMRevertAll()` is skipped on game end — overrides persist between rounds
  - Fixes starter HQ FOW/target issue since prefabs keep modified values across rounds
  - Toggleable in-game via `!b` → HTP menu → option 8
- Previous attempts (PropagateToLiveInstances, FOW re-apply after revert) removed in favor of this cleaner approach
- **Note**: With `revert_on_round_end=false`, `!rebalance` still works (it always reverts before re-applying). However, multiplier-based overrides would compound if re-applied without revert, so `OnGameStartedLogic` skips Apply* on subsequent rounds when overrides are already active (`_overridesApplied` flag).

### Changes
- **Added**: `fow_distance` for all buildings (structures + armed structures) — controls Fog of War reveal range
- **Added**: `target_distance` for armed structures (turrets: Turret, Heavy Turret, AA Rocket Turret, Hive Spire, Thorn Spire) — controls AI targeting range
- Buildings now have a "Vision & Sense" section in the in-game menu
- `gen_default_config.py` — removed structure exclusion from vision section; added `_base_sense` annotation for buildings
- `Si_UnitBalance.Menu.cs` — extended `BuildGroupsForUnit()` to show vision group for structures (fow only) and armed structures (fow + target)
- Default config: 1290 params (+10 from 1280)

### Base Values (from dump)
- HQ: FOW 600, Target 300
- Most buildings: FOW 200, Target 300
- Turrets: FOW 300, Target 400–600
- Nest: FOW 300, Target 800
- Thorn Spire: FOW 200, Target 650

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

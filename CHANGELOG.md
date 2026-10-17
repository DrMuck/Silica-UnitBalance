# Si_UnitBalance — Change Log

Tracks completed changes and tasks for the Si_UnitBalanceUI project.

---

## 2026-10-17 — Game 1.0.1 compatibility (v7.8.0)

The Steam test branch moved the server from Beta 0.9.47 to Release 1.0.1 (new `SilicaCore.dll`).
Branch `1.0.1` of this repo builds against that assembly (`include/netstandard2.1/SilicaCore.dll`
replaced; target stays netstandard2.1).

- **`Structure.Construct` patch restored.** 1.0.1 appended `Player requester = null` to the
  signature, so the pinned 4-parameter `AccessTools.Method` lookup failed and the server logged
  `[CountCap] Structure.Construct not found — count caps / nest reach / nest finished-anchor /
  queen logistics inactive`. The lookup now takes the `Construct` declared on `Structure` whose
  first parameter is `ConstructionData`, whatever optional parameters follow (new
  `FindConstructMethod()`). The four prefixes bind by name (`constructionData`, `worldPosition`,
  `worldRotation`, `isClientRequest`) and those names are unchanged in 1.0.1, so they are untouched.
  The vanilla method body changed only in the `requester`/GameMaster check and a new scenario
  construction-permission gate for client requests, neither of which affects the prefixes.
- **PrefabPool flush uses the public 1.0.1 API.** `PrefabPool.Drop(GameObject)` and
  `PrefabPool.ClearAll()` are public now, and `OverrideManager` drops the pool entry of every
  prefab it resolves through an asset's `Prefab` fallback on its own (`MarkPoolDirty` /
  `FlushPoolDrops`). Our flush is still needed for prefabs this mod mutates directly (networked-twin
  Sensor values, the Nest `SpawnOnInit.MaxPerTeam` for `alien_queens_max`). `FlushPrefabPool(reason)`
  now calls `ClearAll()` (blanket, after a full apply / rebalance); the new
  `FlushPrefabPool(reason, prefabs)` calls `Drop()` per prefab, which also bumps the pool
  generation so refills still in flight are discarded instead of landing as pre-override clones —
  the `alien_queens_max` menu edit uses it for just the Nest prefab (`ApplyAlienQueensMax()` now
  returns the prefab it changed). The reflection sweep over the private `Pools` dictionary remains
  as a fallback for builds without the API (it now also clears `InFlight`). `[POOL]` log line kept.
- **Reflection audit against the 1.0.1 decompile.** Every string-named member the mod looks up
  (116 names: `AccessTools.*`, `GetMethod`/`GetField`/`GetProperty`/`GetNestedType`, the
  `OverrideManager` internals `AssetSourceRegistry` / `_sources` / `BuildSources` / `_overrides` /
  `MemberOverride.Enabled` / `Set` / `RevertAll`, `NetworkLayer.SendPlayerOverrides`,
  `GameLevelLoader.OnFinishLoadingLevel`, `MP_Strategy.SpawnStructuresAndStartingResources` /
  `SpawnBaseStructures` / `SetTeamVersusMode`, `GameModeExt.UpdateUnitCapMultFromSetting`,
  `VehicleDispenser.RequestVehicle`, `ConstructionSite.SpawnObject`, `UpdateHealDecay`,
  `Team.UpdateUnitCapValues`, `GameDatabase.GetSpawnablePrefabIndex`) still exists in 1.0.1; the
  `NetworkLayer.SendPlayerOverrides` body (whose packet layout the chunked-sync prefix
  re-implements) is byte-identical. No other change was needed; the mod also compiles directly
  against the 1.0.1 assembly.
- Version bumped to **7.8.0** in `MelonInfo` and the init line (7.7.1 had still printed "v7.7.0").

---

## 2026-09-26 — Watchdog waits for map loads (v7.7.1)

The round-transition watchdog forced `EndRound()` while the next map was already loading. On the
2026-09-25 server log, all 4 vanilla `Game ended during loading!` errors match a
`[Watchdog] … forcing EndRound` line to the millisecond (16:17:15, 23:28:08, 23:54:02, 00:19:38).
The load had started 0.3–7 s earlier, so every one of these was a false positive.

- While `GameLevelLoader.IsLoading` is set, the countdown pauses (log: `Level load in progress … —
  countdown paused`). When the load finishes, the watchdog restarts the full countdown for the new
  round to start.
- The announced countdown is now **60 s** (was 40 s) after the 20 s silent wait, so recovery happens
  80 s after game end at the earliest. New constants `WatchdogSilentSeconds` / `WatchdogCountdownSeconds`
  replace `WatchdogTimeoutSeconds`.
- The "stalled" chat and warning only appear once the countdown is really running, so they no
  longer show during a load. The same check runs right before recovery, so a load that starts in
  the last 10 s wait is also respected.

---

## 2026-09-18 — Queen logistics per Nest (v7.7.0)

New top-level config bool **`alien_queen_logistics_per_nest`** (default `false`). Vanilla gates all
alien building on `Team.GetNeedConstructionLogistics()`, which is **team-wide**: building is allowed
as long as *any* logistics compartment on the team (a Nest's `AlienQueenCompartment`, flagged
`IsConstructionLogistics`) has a docked unit. That's why the starter Queen leaving blocks the whole
team while an expansion Nest's Queen leaving changes nothing (the starter Queen is still docked).

- **Rule**: with this on, every alien structure belongs to its **nearest finished Nest** (2D
  distance, same "reach" notion as Nest Reach Radius; a Nest belongs to itself) and may only build /
  produce while *that* Nest's Queen is docked. With a single Nest that is exactly vanilla; with
  expansion Nests every structure depends on the Queen of the Nest it sits closest to.
- **Mechanism**: fourth prefix on `Structure.Construct` (base; `AlienStructure.Construct` calls it
  after its own link check). Nearest Nest = min `GameMath.Distance2D` over `Team.Structures` whose
  `ObjectInfo` is the Nest; docked = any `UnitCompartment.IsConstructionLogistics` on it with a
  `DriverUnit` (only the Nest's `QueenDock` `AlienQueenCompartment` carries that flag — verified in
  `sharedassets0`). Not docked → returns `ProductionActionResult.MissingLogistics` (vanilla's own
  result), nothing queued, no resources taken. No built Nest at all → left to vanilla. Skipped when
  `ConstructionSite.LimitationsDisabled`. Covers unit production from cysts too, like vanilla.
  (First cut used the alien link graph `_linkedStructures`; dropped because with chained nodes/cysts
  the whole base is one connected graph, which collapsed back into the team-wide vanilla rule.)
- **Feedback**: commander chat (`Queen away — Spawning Cyst can't build (no docked Queen in the Nest it
  is linked to) — return the Queen to that Nest.`) + AdminMod denied sound, 3 s throttle per team.
  The client UI still shows the structure as buildable (it computes the team-wide check locally).
- **Requires** `alien_queens_max` `0` (every Nest) or `N`: with vanilla `-1` expansion Nests never
  get a Queen, so their clusters could never build. The menu toggle prints a warning when Alien Queens
  is still `-1`.
- **In-game**: `!b` → Strategystyle → `5. Queen Logistics per Nest [ON/OFF]`, immediate. Discord report
  lists it under Global Settings.
- **Menu split**: new root entry `7. Strategystyle` (alien nest-centric build rules) takes
  `1. Alien Nest-Only Build`, `2. Alien Nest Reach Radius`, `3. Alien Queens`,
  `4. Alien Nest Finished-Anchor Only`, `5. Queen Logistics per Nest` off Cap&Decay, which is back to
  Decay / Player Cap Exempt / Unit Cap Mult 2-way / 3-way / `5.` Shrimp Cap / `6.` Harvester Cap.
  `/back` from the Nest Reach and Alien Queens pages returns to Strategystyle. Audit-log category
  for the moved toggles is `strategy`.

## 2026-09-18 — Alien nest finished-anchor only (v7.6.0)

New top-level config bool **`alien_nest_finished_anchor_only`** (default `false`): a new Nest must be
within anchor range of a **finished** structure. Vanilla's placement test
(`ConstructionPreview.GetWithinBaseStructuresDistanceAndFogOfWar`) accepts construction *sites* as
anchors, so aliens could chain Nest → Nest → Nest outward while the previous Nest was still growing.
With this on, a Nest under construction can't anchor the next one; a finished Nest can anchor any
number of new Nests (parallel expansion off a finished base stays allowed).

- **Mechanism**: third prefix on `Structure.Construct` (same server gate as count caps / nest
  reach). For a placeable request whose `ObjectInfo` is the team's `BaseStructure` /
  `ObjectInfo_Alien_Nest`, the mod re-runs the game's anchor distance test over `Structure.Structures`
  only (same team, `IsConstructionAnchor`, alive — same bounds-to-bounds 2D distance and
  `MaximumDistanceUseRadius` variant as the game), skipping `ConstructionSite.ConstructionSites`. If
  no finished anchor is within `ConstructionData.MaximumBaseStructureDistance` (= `build_radius`),
  the request returns `ProductionActionResult.AreaObstructed` — nothing placed, no resources taken.
  Skipped when `ConstructionSite.LimitationsDisabled`. With `alien_nest_only_build` off, a finished
  linked cyst/node also counts as an anchor, exactly as in vanilla.
- **Feedback**: commander chat line (`Nest needs a finished anchor (nearest finished anchor 312 m) —
  the Nest under construction (37%) can't anchor it yet — wait for it to finish or build next to a
  finished Nest.`) + the AdminMod denied sound, throttled to one per 3 s per team (client preview
  still shows green, as with nest reach).
- **In-game**: `!b` → Cap&Decay → `10. Alien Nest Finished-Anchor Only [ON/OFF]`, applies immediately
  (checked live per placement, no `!rebalance`). Discord report lists it under Global Settings.
  Cap&Decay guard widened to `1-10`.
- Independent of `alien_nest_only_build` / `alien_nest_reach_radius`; all three together = strictly
  sequential nest-centric expansion. Nodes / cysts around a Nest under construction are unaffected.

- Nest reach warning (Cap&Decay /8) now ends with `… build closer to a Nest. A Nest is required for
  further expansion.` so the commander knows what unlocks the area.

## 2026-09-17 — Alien nest reach radius + Queens per Nest (v7.5.0)

New top-level config number **`alien_nest_reach_radius`** (metres, default `-1` = vanilla): Nodes and
Bio Caches may only be placed within this 2D distance of a friendly Nest (built or under
construction). Idea: aliens expand via Nest bases; a Nest reaches out to resources through
Nodes/Bio Caches, but only within its reach — anything further needs a new Nest first. Other alien
structures (cysts, cortex, spires) are unaffected and still follow the normal anchor rule.

- **Mechanism**: second prefix on `Structure.Construct` (same server gate the count caps use; it
  carries the requested world position for placeable structures). Nearest Nest = min 2D distance
  over `Team.Structures` (alive) + `ConstructionSite.ConstructionSites` (active, same team) whose
  `ObjectInfo` is the team's `BaseStructure` / `ObjectInfo_Alien_Nest`. Out of reach → returns
  `ProductionActionResult.AreaObstructed`, nothing is placed, no resources taken.
- **Feedback**: the client's placement preview can't know the rule (it'd show green), so the team's
  commander gets a chat line (`Node out of Nest reach (nearest Nest 312 m, limit 250 m) — …`) and the
  AdminMod `AudioHelper.PlaySoundFile("sounds/access_denied.wav", commander)` denied sound, throttled
  to one per 3 s per team. Sound falls back to chat-only if AdminMod's AudioHelper or the WAV is
  missing (checked once, logged).
- **In-game**: `!b` → Cap&Decay → `8. Alien Nest Reach Radius`, page edited with `.1 <metres>`
  (`.1 -1` = vanilla). Applies immediately on save. Discord report lists non-default values under
  Global Settings.
- **Alien Queens** (`alien_queens_max`, default `-1` = vanilla): the Nest prefab's `SpawnOnInit`
  spawns a Queen into every Nest's `AlienQueenCompartment` on the server, but its `MaxPerTeam` (1)
  makes only the starting Nest get one. The mod mutates `MaxPerTeam` on the Nest prefab (`0` = no
  limit → every expansion Nest spawns its own docked Queen, `N` = at most N per team). Server-side
  only (clients never run the spawn), immediate on save for Nests built afterwards; the prefab pool
  is flushed so the next Nest isn't a pre-change clone (the pool is now also flushed after
  `!rebalance`). Cap&Decay `9. Alien Queens`, `.1 <n>`.
- Combines with `alien_nest_only_build` (both on = pure nest-centric play: everything anchors to a
  Nest AND resource reach is bounded) but is independent of it. Not for the RTS-AI server for the same
  reason as nest-only build.

## 2026-09-16 — Alien nest-only building (v7.4.0)

New top-level config bool **`alien_nest_only_build`** (default `false`) for a nest-centric alien
playstyle: every alien structure except the Nest stops counting as a construction anchor, so new
alien buildings can only be placed within `build_radius` of a Nest and expansion happens by building
new Nests (tier 3) instead of creeping cysts/nodes outward.

- **Mechanism**: `ObjectInfo.StructureConstructionAnchor` (vanilla `true` on all alien structures;
  humans have it only on the HQ) is set to `false` via OverrideManager on Node, the four Spawning
  Cysts, Quantum Cortex, Hive/Thorn Spire and Bio Cache. Placement
  (`ConstructionData.GetWithinBaseStructuresDistanceAndFogOfWar`) and the alien link chain
  (`AlienStructure.InitLink`, filtered on `CanBeConstructionAnchor`) both read the flag live from the
  asset, so the OM overlay is enough and it syncs to clients (placement preview agrees with the
  server). Side effect: structures link straight to a Nest (star instead of chain), so losing a cyst
  no longer cuts off what was built behind it — only losing the Nest unlinks.
- **In-game**: `!b` → Cap&Decay → `5. Alien Nest-Only Build [ON/OFF]`, then `!rebalance` (already
  placed structures keep their spawn-time links; new placements see the rule). Discord report lists it
  under Global Settings.
- **Menu split**: new root entry `6. Cap&Decay` takes Decay, Player Cap Exempt, Unit Cap Mult and
  Alien Nest-Only Build off the HTP menu (which is back to Hoverbike / Tier / Teleportation / Shrimp
  Aim / Additional Spawn / Discord / Watchdog). Unit Cap Mult is now two separate entries (`3.` 2-way,
  `4.` 3-way), each its own page edited with `.1 <mult>`. `/back` from Decay / Unit Cap pages returns
  to Cap&Decay.
- **Count caps** (`count_caps` config section: `shrimp`, `harvester`; `-1` = unlimited): absolute
  per-team limits on alien Shrimps and Sol/Centauri harvesters, counted as alive + queued + in
  production (the same three sources `Team.UpdateUnitCapValues` uses). Enforced by a prefix on
  `Structure.Construct` — the single server-side gate for client build requests, AI commanders and
  cyst pops — which answers `ProductionActionResult.UnitCap` once the cap is hit. The client's build
  button stays enabled (its local cap check passes), so the team's commander
  (`GameModeExt.GetCommanderForTeam`) gets a chat warning instead, throttled to one per 3 s per cap.
  Cap&Decay `6. Shrimp Cap` / `7. Harvester Cap`, edited with `.1 <max>`; applies immediately on
  save. Discord report lists non-default values.
- **Fix**: HTP selection guard rejected `10` (Unit Cap Mult was unreachable from the menu).
- **Fix (0.9.47 hotfix for other mods)**: `GameDatabase.GetSpawnablePrefabIndex(string)` is replaced
  by a null-safe prefix. 0.9.47 leaves a null entry in `AllNetworkedPrefabs`, so every by-name spawn
  (Si_Resources' reveal scout at the versus vote, Si_MapBalance, `AdminMod.SpawnAtLocation`) threw an
  NRE — and Si_Resources' team loop aborted after the first team, leaving the other teams without
  their starting-resources adjustment.
- **Not for the RTS-AI server**: Si_RTS_AI's alien planner expands via Node lines and would be stuck
  at the starting Nest radius with this on (vanilla commander AI likewise picks its expansion
  building by the anchor flag).

---

## 2026-09-13 — Unit cap multiplier per match type (v7.3.0)

New top-level config section **`unit_cap_mult`** with `two_way` / `three_way` (int; `-1` = keep the
server's `capmult` setting, `0` = unlimited, `N` = every cap entry's `CapBase × N`). 2-way covers
`HUMANS_VS_HUMANS` and `HUMANS_VS_ALIENS`; 3-way covers `HUMANS_VS_HUMANS_VS_ALIENS` and
`HUMANS2_VS_ALIENS` (still three teams' worth of units on the map).

- **Why**: vanilla derives `GameModeExt.UnitCapMultiplier` once from the server's `capmult` param in
  `Restart → UpdateInitialStrategySettings`, while `TeamsVersus` is still `NONE`; the versus vote resolves
  later via `SetTeamVersusMode`, which never touches the multiplier. data-bomb's Si_MatchSettings only
  postfixes that same recompute with one global value, so neither can tell a 2-way from a 3-way match.
- **How**: `MP_Strategy.SetTeamVersusMode` postfix re-derives the multiplier for the decided mode via
  `SetUnitCapMultiplier` (the setter RPC-syncs to clients, and `UnitCapEntry.Max` reads it live, so no cap
  entry has to be rewritten). A second postfix on `GameModeExt.UpdateUnitCapMultFromSetting` re-asserts
  the per-mode value if the game (or Si_MatchSettings) recomputes it after the mode is known. `-1` calls
  the vanilla recompute instead, so reverting to `-1` + `!rebalance` (or `!rebalance default`) restores the
  server setting mid-match.
- **In-game**: `!b` → HTP → `10. Unit Cap Mult` shows both values plus the live mode/multiplier;
  `.1 3` / `.2 2` edit with the usual confirm flow. A confirmed save applies immediately to the running
  match (no `!rebalance` needed).
- **Discord** balance report lists non-vanilla values under Global Settings. Default config, the live
  server config and `gen_default_config.py` carry the section with `-1/-1` (no behaviour change until set).

---

## 2026-07-26 — Ballistics: drop-off, drag, AI-lead & aim compensation (v7.2.0)

New per-projectile **Ballistics** group (in-game `!b` menu + web editor + config), for tuning
how shells fall off and how aiming compensates for it. Fields apply to every `ProjectileData`
a unit fires.

- **`impact_scale_by_speed`** (bool, `1`=on / `0`=off; omit = vanilla). Toggles the game's
  `m_bImpactScaleBySpeed`: on = impact damage scales with the drag-reduced speed (damage falls
  off with range); off = flat damage at any range. Written to config as a real JSON boolean.
  Server-authoritative → works. Menu shows it spelled out (`0 = OFF (flat)` / `1 = ON (falls off)`)
  to avoid the double-negative confusion.
- **`drag_mult`** (multiplier, default 1.0). Scales `m_fDragCoefficient`. `<1` = flatter,
  longer-carrying shot; `>1` = decelerates faster.
- **`ai_lead_velocity` / `ai_lead_gravity` / `ai_lead_drag`** (multipliers, default 1.0). Scale
  the projectile's `m_AIAimLeadFactor_*` (the aim-lead compensation factors), layered on top of
  the automatic speed compensation: `final = vanilla × (1/speedMult) × mult`. Computed in one
  place (`ScaleAimLeadFactors`) anchored to the cached vanilla value so re-applies can't compound;
  a factor whose final value lands on vanilla is left unwritten (no wasted OM slot). Only takes
  effect on units that also have a projectile speed/range override (they ride that pass).
- **`aim_compensation`** (int enum, `-1`/absent = vanilla). Forces the turret's
  `ETurretAimCompensation` mode: `0` None · `1` Primary_VerticalOnly · `2` Secondary_VerticalOnly ·
  `3` Primary_Full · `4` Secondary_Full. Clamped to 0-4. Applied directly to the prefab turret
  **and** to live scene instances (prefab edits alone don't reach already-spawned units — Unity
  copies MonoBehaviour fields at instantiation), with the vanilla value cached for restore.

### How aim compensation actually works (from decompiling `ProjectileData.CalculateTargetLeading`)
- The vertical drop is a real parabolic solve, not a constant: `aimY -= 0.5 · g · gravityScale · t² · m_AIAimLeadFactor_Gravity`, with `t` = distance/speed recomputed per shot. Drag and velocity terms likewise. So `aim_compensation` is the *on/off/mode switch*; the `ai_lead_*` multipliers are the *gradual strength dials*.
- The same function is called by **both** the AI agent (`Vehicle.Skill`, target velocity) **and** the player path (`if AimCompensation != None && Vehicle.PlayerControlled`). So the `ai_lead_*` factors affect AI aim and (in principle) player aim.
- Vanilla turret compensation is set for only 6 units (aircraft Interceptor/Gunship = `Primary_Full`; arc weapons Siege plasma / Pyro flamethrower = `Primary_VerticalOnly`). All direct-fire tanks incl. **Crimson ship as `None`**.

### ⚠️ KNOWN ISSUE — `aim_compensation` does NOT work for PLAYER-controlled units from this (server) mod
- Player aim + the reticle are computed **client-side** (`Vehicle.GetLocallyControlled()`).
  `AimCompensation` is a plain, **non-networked** `VehicleTurret` field. This mod runs on the
  **dedicated server**, so setting it changes only the server's copy — the player's client keeps
  the base-game prefab value (`None` for Crimson) and computes aim/reticle with no compensation.
- Confirmed empirically: forcing Crimson to `Primary_Full`/`VerticalOnly` produced no reticle,
  barrel or shell-impact change, at any mode or projectile speed; **Siege** (base-game
  `VerticalOnly`) compensates correctly because *every* client already has the flag.
- **AI-controlled** units are unaffected by this limitation — the AI aims server-side, so
  `ai_lead_*` genuinely tunes AI aim. `impact_scale_by_speed` and `drag_mult` are also
  server-authoritative and work. Only *player* `aim_compensation` is inert.
- Enabling player compensation for a `None` unit would require a **client-side** mod or the devs
  setting the flag in the base game. The `aim_compensation` override is kept in place (code, menu,
  config plumbing) for AI use and for the day a client build / dev change makes it live — but do
  not expect it to change a human player's aim.
- Server-side alternative to improve *player* accuracy on a draggy/arcing shell: flatten the
  trajectory instead (lower `gravity_scale` / raise speed via `ProjectileData`, which **is**
  networked). Not yet exposed as an override — candidate follow-up (`gravity_scale`).

### Also in this release
- **Fix**: `/stats` (read-only player inspector) `/1-/N` navigation was dead — `OnMenuShortcut`
  only proceeded when the player was in the admin editor (`_menuStates`); now also proceeds for
  `_statsStates`, so `/stats` drill-in works.
- **Fix (KoH-adjacent)**: Structure buy-menu display-name collisions — unrelated to this mod.
- Diagnostic logging: `[LEADFACTOR]` (prints the applied lead-factor value when a unit's
  `ai_lead_*` is off default) and `[AIMCOMP-LIVE]` (prints each live turret's AimCompensation
  change) — always-on, low-frequency (only on actual change), no debug flag needed.
- Dumps added under `UserData/UnitBalance_cfg/` when `dump_fields=true`:
  `ProjectileData_Dump.csv` (per-projectile drop-off / drag / AI-lead vanilla values) and
  `TurretAimComp_Dump.csv` (per-unit turret `AimCompensation` mode).

---

## 2026-04-12 — Extraction Radius (Shrimp harvesting)

- **New parameter** `extraction_radius` (absolute meters, `-1` = default). Overrides the harvester's `ExtractionRadius` field — the distance around the unit's extraction point where it can reach biotics cells.
- **Component detection**: matches by field name (`ExtractionRadius`) via reflection instead of exact class name, so it handles the Shrimp's `MiningClaws` (alien harvester, default 2m) as well as human harvesters using `MiningArmsBase` subclasses (`MiningArmsLaser`, `MiningArmsRoller`, default 20m). Originally scoped only to `MiningArmsBase`; widened after discovering Shrimp uses `MiningClaws`.
- **Menu integration**: `hasMiningArms` detection on the unit prefab conditionally adds `extraction_radius` to the Health & Production group for that unit only. Shrimp shows it; non-harvester units don't.
- **Apply path**: direct prefab mutation (not OverrideManager) — the component is a child MonoBehaviour, not an OM-registered asset. Follows the same pattern as `deposit_radius` (BioCache).
- **Wiring**: `ApplyExtractionRadiusOverrides` called from both `Lifecycle.cs` (first-load apply) and `Patches.cs` (`!rebalance` re-apply).

---

## 2026-04-12 — UpdateHealDecay Hot-Path Optimization (v7.0.1)

- **Problem**: Si_ModPerfMonitor showed `HumanStructure::UpdateHealDecay` + `AlienStructure::UpdateHealDecay` prefixes burning ~80-100ms/20s sustained. The game calls these per-structure-per-frame to detect linked↔unlinked transitions, so call count is fixed by the engine — only per-call body cost is controllable.
- **Fix** (both prefixes in `Si_UnitBalance.Patches.cs`):
  - `DamageManager` lookup was `hs.gameObject.GetComponent<DamageManager>()` every call (expensive in Il2Cpp). Now cached per structure in `Dictionary<int, DamageManager>` keyed by `GetInstanceID()`; one lookup per structure lifetime.
  - Human prefix read `AnchorStructure` via `FieldInfo.GetValue()` every call (slow reflection marshalling in Il2Cpp). Now uses Harmony's `AccessTools.FieldRefAccess<HumanStructure, Structure>` compiled delegate.
  - Cache invalidated on map change via `DecayCacheClear()` hook in `Si_UnitBalance.Lifecycle.cs` to avoid stale refs from destroyed structures.
- **Behavior preserved**: identical unlinked-state detection, identical write guards, vanilla still runs when keep_production/Enabled are off.
- **Expected savings**: ~50-70% of body cost → combined totals should drop from ~80-100ms/20s to ~25-35ms/20s.

---

## 2026-04-03 — Remove Redundant Tier Change Handler

- **Problem**: Server lag spike on each tech tier change. `OnTeamTierChanged` subscribed to `GameEvents.OnTeamTechnologyTierChanged` and ran `ResetDispenserLocalTimeout` which scanned all assemblies, resolved reflection members, and iterated all VehicleDispenser instances — all unnecessary.
- **Root cause**: The `LocalTimeout` reset was redundant. `min_tier` already sets `ConstructionData.MinimumTeamTier` via OverrideManager (native game tier gating, synced to clients), and `Patch_VehicleDispenser` Harmony prefix blocks dispensing below the required tier. When the tier is reached, both mechanisms allow the dispenser naturally — no timeout reset needed.
- **Fix**: Removed `OnTeamTierChanged`, `ResolveDispenserReflection`, `ResetDispenserLocalTimeout`, all cached VehicleDispenser reflection fields, `_dispenserTierResets` HashSet, and event subscription/unsubscription.
- **Backup**: `Mods/_backup_pre_tier_cleanup_2026-04-03/`

---

## 2026-03-22 — Legacy Override Mode Hotfix (Old Game Versions)

### TEMPORARY HOTFIX: Legacy Override Mode
- **Problem**: Old game versions (e.g. `Beta:0.9.1`) lack `GameLevelLoader.OnFinishLoadingLevel`, so overrides never get applied. The main server still runs on the old version until the beta is pushed to main.
- **Fix**: Auto-detect missing `OnFinishLoadingLevel` → set `_legacyOverrideMode = true`:
  - `Patch_GameStarted`: OMRevertAll + Apply every round start (revert ensures vanilla baseline → no compounding)
  - `Patch_OMRevertAll`: Don't block game's RevertAll in legacy mode (let it through)
  - All sections marked with `TEMPORARY HOTFIX` / `REMOVE THIS` for easy cleanup
- **New version behavior unchanged**: `OnFinishLoadingLevel` hook + blocked RevertAll (OM persists through map changes)
- **Note**: On old versions, starter HQ FOW won't work on map change (known limitation — no fix without OnFinishLoadingLevel timing). Overrides still apply to all units/buildings built after game start.

---

## 2026-03-20 — Construction Health Root Cause Fix, Spawn Height Fix

### Construction Health Root Cause Fix
- **Problem**: Refineries and other buildings showed ~25-35% less than max health on fresh build.
- **Root cause**: `DamageManager.DamageDisabled` is a **static/global** field. Our additional unit spawn code set it to `true` for 20 seconds (fall damage protection), which blocked ALL `SetHealth` calls that increase health — including construction site health accumulation ticks. The diminishing series reduced `HealthAddRemaining` each tick but `SetHealth` silently rejected the increases, causing permanent health loss.
- **Fix**: Removed global `DamageManager.DamageDisabled` from spawn code entirely. Spawned units survive fall damage due to sufficient HP and 1m above-terrain placement.
- **Previous workaround** (Harmony Prefix on `ConstructionSite.SpawnObject` forcing health to max): disabled, no longer needed.

### Spawn Height Fix (Multi-Terrain Maps)
- **Problem**: Additional spawned units on maps like Badlands spawned 50m above ground and died from fall damage.
- **Root cause**: `Terrain.SampleHeight()` returns 0 for positions outside a terrain tile's bounds, but we still added `terrain.transform.position.y` — picking a wrong tile's height offset.
- **Fix**: Check terrain tile XZ bounds before sampling. Only sample from the terrain tile that contains the spawn position. Fallback to HQ height if no matching tile.

---

## 2026-03-19 — Build Time Fix, Weapon Fixes, Unit Cap, Auto-Annotations, Game Version Detection, Starter Override Fix

### Starter Structure Override Fix (FOW/TargetDistance)
- **Problem**: Starter HQ spawned with vanilla FOW/TargetDistance after map changes via the beta map voting system. Worked on first server start but not on subsequent maps.
- **Root cause 1**: Map voting uses `NetworkGameServer.LoadLevel` which bypasses `MusicJukeboxHandler.OnGameInit`.
- **Root cause 2**: OM overrides are a runtime overlay — they don't modify actual prefab field values. Starters get vanilla values at spawn.
- **Root cause 3**: Timing — any overrides applied before `GameLevelLoader.LoadAsyncScene` completes get wiped by its internal `OM.RevertAll`.
- **Fix**: Hook `GameEvents.OnLevelEndLoad` — fires AFTER `OM.RevertAll` + AFTER scene assets loaded, BEFORE spawning starts. Works for ALL map transition paths including map voting. (Identified by databomb via dnSpy analysis of `LoadAsyncScene`.)
- **Direct prefab mutation**: `ApplyFoWDistanceOverrides` and `ApplyTargetDistanceOverrides` always mutate the prefab directly + set via OM. Direct mutation ensures starters inherit values at spawn. OM ensures client sync.
- **Override application hooks** (in order of firing):
  1. `GameEvents.OnLevelEndLoad` — primary hook, ideal timing (after revert, before spawn)
  2. `Patch_GameInit` — backup for first map load
  3. `SceneManager.sceneLoaded` — universal backup for any scene transition
  4. `Patch_GameRestart` — marks `_overridesApplied = false` for map changes
  5. `OnGameStartedLogic` — re-applies + propagates to live instances + client sync (2s delay)
- **Final working solution (2026-03-19)**:
  1. Harmony PREFIX on `OM.RevertAll` — BLOCKS the game's revert during scene transitions (OM overrides persist through map changes)
  2. Apply overrides ONCE on first map via `OnFinishLoadingLevel` (with our own `OMRevertAll` before apply to prevent compounding)
  3. On map changes: do nothing — OM state persists, `OnGameStartedLogic` handles propagation + client sync only
  4. `!rebalance` explicitly allowed to revert via `_allowRevertAll` flag
- **Key insight**: `GameEvents.OnLevelEndLoad` gets cleared by `GameEvents.Clear()` during scene transitions — useless. `GameLevelLoader.OnFinishLoadingLevel` persists (different class).
- **Failed approaches**: Harmony Postfix on `OM.RevertAll` caused compounding. Delayed syncs didn't work (client's local OM.RevertAll wipes received updates). Multiple apply hooks caused double-application.

### Build Time Scaling Fix
- **Problem**: `build_time_mult` only scaled `BuildUpTime`, but game UI shows `BuildUpTime + FinishedWaitTime + CleanUpTime`. A 0.75x multiplier on a 20s total (15s BU + 5s FWT/CUT) showed 16.25s instead of 15s.
- **Fix**: `build_time_mult` now scales all three fields by the same multiplier.
- **Tech tier times**: Config value now represents the desired TOTAL time, distributed proportionally across BU/FWT/CUT (e.g., `tier_1: 29` = 29s total, not just BuildUpTime).

### Weapon Cross-Contamination Fix
- **Problem**: Changing Interceptor primary splash damage also changed secondary (bombs). Two bugs: (1) `vtIndex` never incremented in `ApplyProjectileDamageOverrides` — all VTs treated as VT[0]. (2) Damage fallback priority checked shared subtype keys before weapon-specific keys.
- **Fix**: Added `vtIndex++`, reordered fallback: `pri:splash:Unit` → `pri:Unit` → `splash:Unit` → `Unit`.

### Infantry Damage Support
- **Problem**: Marksman/Sniper damage multipliers had no effect — `ApplyProjectileDamageOverrides` only scanned VehicleTurret and CreatureDecapod.
- **Fix**: Added generic ProjectileData field scanner for infantry weapons (HumanHandsAnimator).

### Fighter/Interceptor VT Mapping Correction
- **Problem**: Fighter and Interceptor were incorrectly added to `_vtPriIndex` (gun assumed on VT[1]).
- **Fix**: Confirmed via dump + in-game testing that gun is on VT[0] for both. Removed from `_vtPriIndex`.
- **OM limitation**: Weapon params (reload, fire_rate, magazine, accuracy) always target VT[0] via OM. `_vtPriIndex` only used for damage scaling (targets ProjectileData assets).

### Melee Damage Anti-Compounding
- **Problem**: Creature melee damage compounded on `!rebalance` (no original value cache).
- **Fix**: Added `_originalMeleeDamage` cache, same pattern as other anti-compounding caches.

### AI Aim Lead Compensation
- `proj_speed_mult` now auto-scales `m_AIAimLeadFactor_Velocity`, `_Gravity`, `_Drag` by `1/speedMult`.
- Prevents AI missing shots after projectile speed changes.

### Unit Cap Value Override
- **New param**: `unit_cap_value` — absolute int override for `ObjectInfo.UnitCapValue` via OM (synced to clients).
- Set to 0 to remove cap cost (e.g., unlimited infantry). Vanilla values: infantry 1-2, vehicles 1-6, Colossus 15.
- Cap types: Primary (vehicles/heavy), Secondary (infantry/light creatures), None (structures/Queen).
- **Note**: Unit cap changes only take effect after a map change (not mid-round or via `!rebalance`).
- Shown in `!b` menu under "Health & Production".

### Game Version Auto-Detection & Dump
- Reads `Application.version` (e.g., `Beta:0.9.15`) and saves to `game_version.txt`.
- On version change: auto-triggers field dump + config annotation update.
- No manual `dump_fields: true` needed for game updates.

### Auto-Update Config Annotations
- `UpdateConfigBaseAnnotations()` runs after dump, updates `_base`/`_pri_weapon`/`_sec_weapon`/`_base_speed`/`_base_sense` from dump data.
- Build time in `_base` now shows total (BU + FWT + CUT). Dump includes `finished_wait_time` and `clean_up_time`.
- `unit_cap_type` and `unit_cap_value` added to dump output.
- Entire pipeline in C# — no Python scripts needed.

### Debug Logging Toggle
- All verbose `MelonLogger.Msg` calls (111 lines) gated behind `Admin_EnableDebugLogging` MelonPreference.
- Default: off. Controlled via admin mod only.

### Files Changed
- `Si_UnitBalance.cs` — `_unitCapOverrides` dict, config parsing for `unit_cap_value`
- `Si_UnitBalance.Overrides.cs` — build time FWT/CUT scaling, tech time total distribution, `ApplyUnitCapOverrides`, vtIndex fix, damage fallback reorder, infantry PD scanner, melee cache, aim lead compensation, VT mapping fix
- `Si_UnitBalance.Lifecycle.cs` — wire `ApplyUnitCapOverrides`, game version detection, auto-dump trigger
- `Si_UnitBalance.LivePropagation.cs` — `UpdateConfigBaseAnnotations()`, dump FWT/CUT/UnitCap fields, JSON helpers
- `Si_UnitBalance.Menu.cs` — `unit_cap_value` in param groups + base display, build time total display
- `Si_UnitBalance.Patches.cs` — `_originalMeleeDamage` clear

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

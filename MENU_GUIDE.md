# Balance UI — Quick Manual

In-game admin guide for `Si_UnitBalance`. Players can read live stats with
[`/stats`](#players-stats-not-covered-here-read-only) (no admin needed); this
document is the **admin editor** that actually changes values.

---

## 1. Opening / closing the editor

| Command | Effect |
|---|---|
| `!b` | Open the editor at the root menu. Admin-only (AdminMod `Generic` power). |
| `!b exit` &nbsp;or&nbsp; `/0` &nbsp;or&nbsp; `!b` (while open) | Exit the editor. |
| `/back` | Go up one level. |

If you can't open it, you don't have admin. The mod logs `Registered !b admin command (balance editor)` at startup if it loaded correctly.

---

## 2. Navigation cheatsheet

```
/1 .. /20    pick option N at the current level
/back        go up one level
/0           exit
.N <value>   set option N to <value>  (".1 1.25"  ".1 -1"  ".1 false"…)
!b N <value> alternative if `/` is filtered
```

`/N` and `.N` are equivalent. Use whichever you prefer; both are AdminMod
player commands, so the message is hidden from public chat.

---

## 3. Menu tree

```
Root
├── 1. Sol         ┐
├── 2. Centauri    │   →  Categories  →  Units  →  Param Groups  →  edit
├── 3. Alien       ┘
├── 4. JSON        →  reset blank · save current as named file · load saved
└── 5. HTP         →  global / cross-faction settings
    ├── 1. Hoverbike     (per-tier param groups for the Hover Bike)
    ├── 2. Tier          →  1. Time   (research seconds per tier 1–8)
    │                       2. Cost   (research resources per tier 1–8;  -1 = vanilla)
    ├── 3. Teleportation (cooldown / duration)
    ├── 4. Shrimp Aim    [ON/OFF toggle]
    ├── 5. Additional Spawn [ON/OFF]
    ├── 6. Discord        (auto-post + webhook URL)
    ├── 7. Watchdog [ON/OFF]    (round-transition stall recovery)
    ├── 8. Decay         →  1. Human · 2. Alien   →  enable, keep_production, delay, tick, amount_pct, randomize_pct
    └── 9. Player Cap Exempt [ON/OFF]  (player-controlled infantry don't fill the unit cap)
```

Inside any **Unit > Param Group** you'll see lines like:

```
Strike Tank > Primary Weapon
1. impact_damage_mult = 1.25  (base: 800)
2. ricochet_damage_mult = 1   (base: 240)
3. proj_speed_mult = 1        (base: 400)
…
Set: .1 1.5 (or !b 1 1.5)
```

The number on the right of `=` is the current **multiplier** (or absolute value).
`(base: X)` is the vanilla game value, for reference.

---

## 4. Editing a value

1. Navigate to the param group (e.g. `!b → 1 → 4 → 1 → 2` = Sol > Heavy > Strike Tank > Primary Weapon).
2. Type `.<N> <value>`, e.g. `.1 1.25` to set option 1 to a 1.25× multiplier.
3. You'll get a **confirmation prompt**:
   ```
   Confirm: Strike Tank.impact_damage_mult 1 -> 1.25
   1/yes save · 2/no cancel · 3 save + !rebalance
   ```
4. Reply:
   - **`1`** / `y` / `yes` → save the value to the JSON config (does NOT apply in-game yet).
   - **`2`** / `n` / `no`  → cancel.
   - **`3`** → save **and** immediately run `!rebalance` (apply now).

After saving you'll see `Saved: <unit> <param> <old> -> <new>` and the change is in `Si_UnitBalance_Config.json`.

---

## 5. ⚠ When do changes take effect?

This is the part people get wrong. Three layers of behaviour:

### a) Until you run `!rebalance` (or the next round/map starts) — **nothing changes in-game**
`.1 1.25` only writes the new value into `UnitBalance_cfg/Si_UnitBalance_Config.json`.
The mod re-reads the config and re-applies overrides when:
- `!rebalance` is typed by an admin, **or**
- a new round starts (`OnGameStartedLogic`), **or**
- you reply `3` to the confirm prompt (which runs `!rebalance` for you).

### b) After apply — **new spawns get the new values**
The bulk of overrides go through SilicaCore's `OverrideManager` (OM), which is a
**runtime overlay on prefab data**. The game reads OM when it instantiates a
unit/structure from a prefab. So:
- A factory that produces a **Strike Tank after `!rebalance`** → new tank uses the new damage/range/etc.
- A Strike Tank **already on the field** → keeps the values it had when it was spawned (its component fields were copied from the prefab at spawn time, no live overwrite).

### c) Live propagation — limited set of params
The mod has a `PropagateToLiveInstances` pass on game start (and during `!rebalance`) that pushes some changes to **already-spawned** units/structures. This covers things like move speed, FOW distance, target distance, jump speed, and a few weapon params. It **does not** cover everything (HP, build time, cost, magazine size, projectile data, etc. — those only matter at spawn / build time anyway).

### Practical rule of thumb

| Change to … | Existing units? | New units? | Already-queued construction? |
|---|---|---|---|
| **Cost / Build time / Min tier** | n/a — they're already built | ✅ next purchase | ⚠ already-paid resource cost is locked in; build time depends on game logic |
| **Health (`health_mult`)** | ❌ keep old HP | ✅ | ✅ |
| **Damage / range / projectile** | ❌ keep old values | ✅ | ✅ |
| **Move/walk/run/sprint/jump/strafe/fly speed** | ✅ live propagated | ✅ | ✅ |
| **FOW / target distance** | ✅ live propagated | ✅ | ✅ |
| **Tech research time / cost** | n/a | ✅ — affects future tech-ups starting next research |
| **Decay (HTP > Decay)** | mostly live (keep_production toggle is immediate) | ✅ | ✅ |
| **Player Cap Exempt toggle (HTP /9)** | ✅ immediate (cap recompute each tick) | ✅ | ✅ |

**Rule of thumb:** if you want guaranteed-fresh values, **start a new round** after `!rebalance`. That's the cleanest "everything is the new version" state.

---

## 6. Other admin commands

| Command | What it does |
|---|---|
| `!rebalance` | Reload `Si_UnitBalance_Config.json` from disk and re-apply all overrides + propagate live. Use after editing the JSON file by hand, or after `!b` confirm `1` (save without immediate apply). |
| `!b` | Open editor (see §1). |
| `/stats` | **Player command** (anyone). Read-only inspector of the unit you currently control. Open with `/stats`, then `/1`–`/N` to view a category, `/back` to category list, `/0` to close. |
| `/1`–`/20`, `/back`, `/0` | Menu nav shortcuts. Now registered as **player commands** so they work both for the admin editor and the read-only `/stats` view (gated by which state the caller is in). |

---

## 7. Tips & gotchas

- **JSON menu (Root > 4)** lets you snapshot the current config to a named file and reload presets later — handy when testing variants.
- **Discord menu (HTP > 6)** auto-posts a diff of changed values vs the default config when you `!rebalance`. Useful for keeping a public changelog.
- **HTP > Tier > Cost** uses `-1` as a sentinel meaning "vanilla / no override". Set to `0+` for an explicit cost; set back to `-1` to restore vanilla.
- The `(base: X)` annotations next to every value are read from the **vanilla** prefab snapshot taken on first map load — they're the unmodded reference. The number you type is a **multiplier** (for `*_mult` params) or an **absolute value** (for `min_tier`, `build_radius`, `unit_cap_value`, `target_distance`, `fow_distance`, `dispense_timeout`, `deposit_radius`, `extraction_radius`, the tech tier fields, etc.).
- If you see `(unchanged)` next to a value in `/stats` it means the current value equals vanilla — no override is active for that param.

---

## 8. Where things live

- **Active config:** `UserData\UnitBalance_cfg\Si_UnitBalance_Config.json`
- **Default (vanilla baseline):** `UserData\UnitBalance_cfg\Si_UnitBalance_Config_Default.json`
- **Saved presets:** `UserData\UnitBalance_cfg\saves\*.json` (created via the JSON menu)
- **Audit log:** every `!b` write is logged with player name + Steam ID for accountability.
- **Server log lines to watch:** `[Unit Balance]`, `[BAL]`, `[TECH]`, `[TECH-COST]`, `[HEALTH]`, `[DMG]`, `[RANGE]`, `[MOVESPEED]`, etc.

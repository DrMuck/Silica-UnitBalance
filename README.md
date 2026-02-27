# Si_UnitBalance

A [Silica](https://store.steampowered.com/app/504900/Silica/) server mod for fine-tuning unit and structure stats. Includes a full **in-game admin UI** (`!b` chat command) for live editing — no config file editing or server restarts needed.

All changes are synced to clients via the game's built-in OverrideManager. No client-side mod required.

## Requirements

- [MelonLoader](https://github.com/LavaGang/MelonLoader) for Silica
- **[Silica Admin Mod](https://github.com/data-bomb/Silica)** by databomb — required for `!rebalance` / `!b` chat commands and admin permissions

## Installation

1. Install MelonLoader and the [Silica Admin Mod](https://github.com/data-bomb/Silica) package
2. Copy `Si_UnitBalance.dll` to your server's `Mods/` folder
3. Copy `Si_UnitBalance_Config.json` to your server's `Mods/` folder (optional — a default config is created if missing)
4. Start the server

## In-Game Balance Editor (`!b`)

Admins can type `!b` in chat to open an interactive menu for editing any unit or structure parameter live during a match. Navigate with `.1`-`.9` to select, `.back` to go up, `.0` to exit.

```
!b (Root Menu)
├── 1. Sol              — Sol faction units & structures
├── 2. Centauri         — Centauri faction units & structures
├── 3. Alien            — Alien faction units & structures
├── 4. JSON             — Save / Load / Reset configs
└── 5. HTP              — Hoverbike, Tier, Teleportation, Shrimp Aim
```

### Unit Parameter Groups

Selecting a unit opens these parameter groups:

| Group | Parameters |
|---|---|
| **Health & Production** | `health_mult`, `cost_mult`, `build_time_mult`, `min_tier`, `build_radius` |
| **Primary Weapon** | `damage_mult`, `proj_speed_mult`, `proj_lifetime_mult`, `accuracy_mult`, `magazine_mult`, `fire_rate_mult`, `reload_time_mult` |
| **Secondary Weapon** | Same as primary (shown only if unit has a second weapon) |
| **Movement** | `move_speed_mult`, `jump_speed_mult`, `turbo_speed_mult`, `turn_radius_mult` |
| **Vision & Sense** | `target_distance`, `fow_distance`, `visible_event_radius_mult` |

Each parameter displays its current value and the vanilla base value from the game prefab:
```
1. damage_mult = 1.50 (base: 25)
```

To set a value: `.1 1.5` (sets parameter #1 to 1.5), then confirm with `.1` (save), `.2` (cancel), or `.3` (save + rebalance immediately).

### HTP Menu (Hover / Tier / Teleportation)

| Option | Description |
|---|---|
| **1. Hoverbike** | All Hover Bike parameters (moved here from Sol > Barracks) + `dispense_timeout` + `min_tier` enforcement |
| **2. Tier** | Tech-up research time per tier (Tier 1-8, default 30s each) |
| **3. Teleportation** | HQ teleport cooldown (default 120s) and duration (default 5s) |
| **4. Shrimp Aim** | Toggle shrimp AI targeting on/off (shown as `[ON]`/`[OFF]`) |

### JSON Config Management

| Option | Description |
|---|---|
| **Reset to Blank** | Reverts all parameters to vanilla (confirm required) |
| **Save Current** | Saves current config to `Mods/Si_UnitBalance_Configs/` with timestamp |
| **Load Saved** | Lists saved configs, pick one to load (confirm required) |

## Hot-Reload

Type `!rebalance` in chat (admin only) to:
1. Revert all current overrides
2. Reload the config from disk
3. Re-apply all overrides
4. Sync changes to all connected players

Type `!rebalance default` to revert to vanilla settings without re-applying.

## Config File Reference

The JSON config (`Si_UnitBalance_Config.json`) can also be edited manually. The in-game `!b` menu reads and writes this same file.

### Per-Unit Parameters (synced via OverrideManager)

| Parameter | Type | Description |
|---|---|---|
| `damage_mult` | multiplier | Scales all projectile damage fields (impact, ricochet, splash, penetrating) |
| `health_mult` | multiplier | Scales unit max health |
| `cost_mult` | multiplier | Scales construction/spawn cost |
| `build_time_mult` | multiplier | Scales build/spawn time |
| `min_tier` | absolute | Override minimum tech tier required |
| `build_radius` | absolute | Build radius override (alien structures) |
| `range_mult` | multiplier | Scales weapon range (targeting distance + projectile lifetime/raycast) |
| `target_distance` | absolute | Sensor.TargetingDistance override |
| `fow_distance` | absolute | Sensor.FogOfWarViewDistance override |
| `proj_speed_mult` | multiplier | Scales projectile speed |
| `proj_lifetime_mult` | multiplier | Scales projectile lifetime |
| `reload_time_mult` | multiplier | Scales weapon reload time |
| `accuracy_mult` | multiplier | Scales projectile spread (lower = tighter) |
| `magazine_mult` | multiplier | Scales magazine capacity |
| `fire_rate_mult` | multiplier | Scales fire rate (divides fire interval) |
| `move_speed_mult` | multiplier | Scales movement speed |
| `turbo_speed_mult` | multiplier | Scales boost/turbo speed |
| `jump_speed_mult` | multiplier | Scales soldier jump speed |
| `turn_radius_mult` | multiplier | Scales vehicle turn radius (lower = tighter) |
| `visible_event_radius_mult` | multiplier | Scales projectile visual render distance |
| `dispense_timeout` | absolute | VehicleDispenser cooldown in seconds (Hoverbike) |

Per-weapon variants use `pri_` / `sec_` prefixes (e.g., `pri_damage_mult`, `sec_fire_rate_mult`).

### Per-Projectile Absolute Overrides

Set exact values on specific ProjectileData fields:
```json
"Combat Tank": {
    "projectiles": {
        "ProjectileData_Shell_CombatTank": {
            "m_fImpactDamage": 3000,
            "m_fRicochetDamage": 1000,
            "m_fBaseSpeed": 550,
            "m_fLifeTime": 4
        }
    }
}
```

### Global Settings

| Parameter | Description |
|---|---|
| `enabled` | Enable/disable the mod (default: `true`) |
| `shrimp_disable_aim` | Disable Shrimp AI targeting (default: `false`) |
| `dump_fields` | Dump unit field data to logs on game start (default: `false`) |

### Teleportation (pseudo-unit `_teleport`)

```json
"units": {
    "_teleport": {
        "cooldown": 90,
        "duration": 3
    }
}
```

### Tech Time

```json
"tech_time": {
    "tier_1": 30,
    "tier_2": 60,
    "tier_3": 90
}
```

## Building from Source

```
cd Si_UnitBalance
dotnet build -c Release
```

Output: `Si_UnitBalance/bin/Release/netstandard2.1/Si_UnitBalance.dll`

Reference DLLs from your Silica server's `Silica_Data/Managed/` folder are needed in `include/netstandard2.1/`.

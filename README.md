# Si_UnitBalance

A [Silica](https://store.steampowered.com/app/504900/Silica/) server mod for fine-tuning unit and structure stats via a JSON config file. Supports hot-reloading with the `!rebalance` admin chat command — no server restart needed.

## Requirements

- [MelonLoader](https://github.com/LavaGang/MelonLoader) for Silica
- **[Silica Admin Mod](https://github.com/data-bomb/Silica)** by databomb — required for the `!rebalance` chat command and admin permission handling

## Installation

1. Install MelonLoader and the [Silica Admin Mod](https://github.com/data-bomb/Silica) package
2. Copy `Si_UnitBalance.dll` to your server's `Mods/` folder
3. Copy `Si_UnitBalance_Config.json` to your server's `Mods/` folder
4. Edit the config to your liking and start the server

All parameters are synced to clients via the game's built-in OverrideManager — no client-side mod installation needed.

## Config Overview

The config file (`Si_UnitBalance_Config.json`) supports the following per-unit parameters:

### Synced via OverrideManager (server-side only, no client mod needed)
| Parameter | Description |
|---|---|
| `damage_mult` | Scales all projectile damage fields (impact, ricochet, splash, penetrating) |
| `health_mult` | Scales unit max health |
| `cost_mult` | Scales construction/spawn cost |
| `build_time_mult` | Scales build/spawn time |
| `min_tier` | Override minimum tech tier required |
| `build_radius` | Absolute build radius override (alien structures) |
| `range_mult` | Scales weapon range (AI targeting distance + projectile lifetime or raycast distance) |
| `target_distance` | Absolute Sensor.TargetingDistance override (applied after `range_mult`) |
| `proj_speed_mult` | Scales projectile speed |
| `reload_time_mult` | Scales weapon reload time |
| `accuracy_mult` | Scales projectile spread (lower = tighter) |
| `magazine_mult` | Scales magazine capacity |
| `fire_rate_mult` | Scales fire rate (fire interval divisor) |
| `move_speed_mult` | Scales movement speed (ground, air, soldier) |
| `turbo_speed_mult` | Scales boost/turbo speed only (air units) |
| `turn_radius_mult` | Scales vehicle turn radius |

### Per-Projectile Absolute Overrides
Use the `projectiles` section to set exact values on specific ProjectileData fields:
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

### Other
| Parameter | Description |
|---|---|
| `shrimp_disable_aim` | Disables AI aim tracking for Shrimp units |

## Hot-Reload

Type `!rebalance` in chat (requires admin privileges via the Admin Mod). This will:
1. Revert all current overrides
2. Reload the config from disk
3. Re-apply all overrides
4. Sync changes to all connected players

## Tech Time

The `tech_time` section configures research time per tier:
```json
"tech_time": {
    "tier_1": 30,
    "tier_2": 60,
    "tier_3": 90,
    ...
}
```

## Building from Source

```
cd Si_UnitBalance
dotnet build -c Release
```

Output: `Si_UnitBalance/bin/Release/netstandard2.1/Si_UnitBalance.dll`

Reference DLLs from your Silica server's `Silica_Data/Managed/` folder are needed in `include/netstandard2.1/`.

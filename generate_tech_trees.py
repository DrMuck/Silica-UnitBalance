"""
Generate Silica tech tree block diagrams as PDF - one per faction.
Shows original game values with proposed balance changes highlighted in amber.
"""
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
import os

OUTPUT_DIR = r"C:\Users\schwe\Projects\Si_UnitBalance"

# ========================================
# Progressive tech research times (all factions)
# Original: 30s per tier. New: progressive ramp.
# Total: 75+110+140+170+200+235+270+300 = 1500s = 25 min
# ========================================
TECH_TIMES = {1: 75, 2: 110, 3: 140, 4: 170, 5: 200, 6: 235, 7: 270, 8: 300}
TECH_TIME_ORIG = 30

TECH_CUMULATIVE = {}
_cum = 0
for _t in range(1, 9):
    _cum += TECH_TIMES[_t]
    TECH_CUMULATIVE[_t] = _cum

# ========================================
# Original game data (from server dump)
# ========================================

ALIEN = {
    "faction": "Alien",
    "tech_tiers": [
        ("Alpha I", 1), ("Beta II", 2), ("Gamma III", 3), ("Delta IV", 4),
        ("Theta V", 5), ("Lambda VI", 6), ("Sigma VII", 7), ("Omega VIII", 8),
    ],
    "structures": {
        "Nest": {
            "cost": 3000, "build": 60, "min_tier": 3, "type": "structure",
            "produces": [
                "Node", "Bio Cache", "Lesser Spawning Cyst", "Greater Spawning Cyst",
                "Grand Spawning Cyst", "Colossal Spawning Cyst", "Quantum Cortex",
                "Thorn Spire", "Hive Spire", "Nest",
            ]
        },
        "Quantum Cortex": {
            "cost": 2000, "build": 25, "min_tier": -1, "type": "structure",
            "produces": ["Alpha I", "Beta II", "Gamma III", "Delta IV",
                         "Theta V", "Lambda VI", "Sigma VII", "Omega VIII"]
        },
        "Lesser Spawning Cyst": {
            "cost": 1500, "build": 20, "min_tier": -1, "type": "structure",
            "produces": ["Shrimp", "Crab", "Shocker", "Wasp", "Dragonfly", "Squid"]
        },
        "Greater Spawning Cyst": {
            "cost": 3000, "build": 25, "min_tier": 2, "type": "structure",
            "produces": ["Horned Crab", "Hunter", "Behemoth", "Scorpion", "Firebug"]
        },
        "Grand Spawning Cyst": {
            "cost": 4000, "build": 30, "min_tier": 4, "type": "structure",
            "produces": ["Goliath"]
        },
        "Colossal Spawning Cyst": {
            "cost": 6000, "build": 30, "min_tier": 7, "type": "structure",
            "produces": ["Defiler", "Colossus"]
        },
    },
    "units": {
        "Shrimp":      {"cost": 160,  "build": 15, "min_tier": -1},
        "Crab":        {"cost": 80,   "build": 10, "min_tier": -1},
        "Shocker":     {"cost": 220,  "build": 20, "min_tier": 1},
        "Wasp":        {"cost": 200,  "build": 20, "min_tier": 4},
        "Dragonfly":   {"cost": 400,  "build": 25, "min_tier": 5},
        "Squid":       {"cost": 120,  "build": 10, "min_tier": 6},
        "Horned Crab": {"cost": 160,  "build": 20, "min_tier": -1},
        "Hunter":      {"cost": 500,  "build": 30, "min_tier": 3},
        "Behemoth":    {"cost": 1200, "build": 45, "min_tier": 4},
        "Scorpion":    {"cost": 1600, "build": 45, "min_tier": 5},
        "Firebug":     {"cost": 2400, "build": 45, "min_tier": 6},
        "Goliath":     {"cost": 4000, "build": 60, "min_tier": -1},
        "Defiler":     {"cost": 4200, "build": 90, "min_tier": -1},
        "Colossus":    {"cost": 6000, "build": 120, "min_tier": 8},
    },
    "other_structures": {
        "Node":        {"cost": 100,  "build": 6,  "min_tier": -1},
        "Bio Cache":   {"cost": 500,  "build": 10, "min_tier": -1},
        "Thorn Spire": {"cost": 1000, "build": 35, "min_tier": 2},
        "Hive Spire":  {"cost": 1000, "build": 35, "min_tier": 3},
    },
}

SOL = {
    "faction": "Sol",
    "structures": {
        "Headquarters": {
            "cost": 7000, "build": 120, "min_tier": 5, "type": "structure",
            "produces": [
                "Research Facility", "Refinery", "Radar Station", "Barracks",
                "Light Factory", "Heavy Factory", "Ultra Heavy Factory",
                "Air Factory", "Silo", "Headquarters",
                "Turret", "Heavy Turret", "Anti-Air Rocket Turret",
            ]
        },
        "Research Facility": {
            "cost": 3500, "build": 30, "min_tier": -1, "type": "structure",
            "produces": ["Mark I", "Mark II", "Mark III", "Mark IV",
                         "Mark V", "Mark VI", "Mark VII", "Mark VIII"]
        },
        "Barracks": {
            "cost": 1000, "build": 30, "min_tier": -1, "type": "structure",
            "produces": ["Scout", "Rifleman", "Sniper", "Heavy", "Commando"]
        },
        "Light Factory": {
            "cost": 3000, "build": 30, "min_tier": 1, "type": "structure",
            "produces": ["Light Quad", "Heavy Quad", "Light Striker", "Heavy Striker", "AA Truck", "Platoon Hauler"]
        },
        "Heavy Factory": {
            "cost": 4500, "build": 60, "min_tier": 4, "type": "structure",
            "produces": ["Hover Tank", "Barrage Truck", "Railgun Tank", "Pulse Truck"]
        },
        "Ultra Heavy Factory": {
            "cost": 6000, "build": 120, "min_tier": 7, "type": "structure",
            "produces": ["Siege Tank", "Harvester"]
        },
        "Air Factory": {
            "cost": 5000, "build": 90, "min_tier": 6, "type": "structure",
            "produces": ["Gunship", "Dropship", "Fighter", "Bomber"]
        },
    },
    "units": {
        "Scout":          {"cost": 30,   "build": 10, "min_tier": -1},
        "Rifleman":       {"cost": 40,   "build": 10, "min_tier": -1},
        "Sniper":         {"cost": 120,  "build": 10, "min_tier": 2},
        "Heavy":          {"cost": 180,  "build": 15, "min_tier": 3},
        "Commando":       {"cost": 360,  "build": 20, "min_tier": 4},
        "Light Quad":     {"cost": 300,  "build": 10, "min_tier": -1},
        "Heavy Quad":     {"cost": 500,  "build": 15, "min_tier": 2},
        "Light Striker":  {"cost": 800,  "build": 20, "min_tier": 3},
        "Heavy Striker":  {"cost": 1100, "build": 30, "min_tier": 4},
        "AA Truck":       {"cost": 1100, "build": 20, "min_tier": 5},
        "Platoon Hauler": {"cost": 600,  "build": 20, "min_tier": -1},
        "Hover Tank":     {"cost": 1800, "build": 35, "min_tier": -1},
        "Barrage Truck":  {"cost": 3000, "build": 30, "min_tier": 5},
        "Railgun Tank":   {"cost": 4000, "build": 45, "min_tier": 6},
        "Pulse Truck":    {"cost": 1800, "build": 30, "min_tier": 7},
        "Siege Tank":     {"cost": 4500, "build": 60, "min_tier": 7},
        "Harvester":      {"cost": 1500, "build": 60, "min_tier": -1},
        "Gunship":        {"cost": 2000, "build": 30, "min_tier": -1},
        "Dropship":       {"cost": 2400, "build": 35, "min_tier": -1},
        "Fighter":        {"cost": 3000, "build": 35, "min_tier": 7},
        "Bomber":         {"cost": 5000, "build": 45, "min_tier": 8},
    },
    "other_structures": {
        "Refinery":       {"cost": 3000, "build": 30, "min_tier": -1},
        "Radar Station":  {"cost": 1000, "build": 30, "min_tier": 2},
        "Silo":           {"cost": 500,  "build": 15, "min_tier": -1},
        "Turret":                {"cost": 750,  "build": 35, "min_tier": 2},
        "Heavy Turret":          {"cost": 1500, "build": 35, "min_tier": 4},
        "Anti-Air Rocket Turret":{"cost": 2000, "build": 35, "min_tier": 6},
    },
}

CENTAURI = {
    "faction": "Centauri",
    "structures": {
        "Headquarters": {
            "cost": 7000, "build": 120, "min_tier": 5, "type": "structure",
            "produces": [
                "Research Facility", "Refinery", "Radar Station", "Barracks",
                "Light Factory", "Heavy Factory", "Ultra Heavy Factory",
                "Air Factory", "Silo", "Headquarters",
                "Turret", "Heavy Turret", "Anti-Air Rocket Turret",
            ]
        },
        "Research Facility": {
            "cost": 3500, "build": 30, "min_tier": -1, "type": "structure",
            "produces": ["Mark I", "Mark II", "Mark III", "Mark IV",
                         "Mark V", "Mark VI", "Mark VII", "Mark VIII"]
        },
        "Barracks": {
            "cost": 1000, "build": 30, "min_tier": -1, "type": "structure",
            "produces": ["Militia", "Trooper", "Marksman", "Juggernaut", "Templar"]
        },
        "Light Factory": {
            "cost": 3000, "build": 30, "min_tier": 1, "type": "structure",
            "produces": ["Light Raider", "Heavy Raider", "Assault Car", "Strike Tank", "Flak Car", "Squad Transport"]
        },
        "Heavy Factory": {
            "cost": 4500, "build": 60, "min_tier": 4, "type": "structure",
            "produces": ["Combat Tank", "Rocket Tank", "Heavy Tank", "Pyro Tank"]
        },
        "Ultra Heavy Factory": {
            "cost": 6000, "build": 120, "min_tier": 7, "type": "structure",
            "produces": ["Crimson Tank", "Harvester"]
        },
        "Air Factory": {
            "cost": 5000, "build": 90, "min_tier": 6, "type": "structure",
            "produces": ["Dreadnought", "Shuttle", "Interceptor", "Freighter"]
        },
    },
    "units": {
        "Militia":        {"cost": 30,   "build": 10, "min_tier": -1},
        "Trooper":        {"cost": 40,   "build": 10, "min_tier": -1},
        "Marksman":       {"cost": 120,  "build": 10, "min_tier": 2},
        "Juggernaut":     {"cost": 180,  "build": 15, "min_tier": 3},
        "Templar":        {"cost": 360,  "build": 20, "min_tier": 4},
        "Light Raider":   {"cost": 300,  "build": 10, "min_tier": -1},
        "Heavy Raider":   {"cost": 500,  "build": 15, "min_tier": 2},
        "Assault Car":    {"cost": 800,  "build": 20, "min_tier": 3},
        "Strike Tank":    {"cost": 800,  "build": 20, "min_tier": 3},
        "Flak Car":       {"cost": 1100, "build": 20, "min_tier": 5},
        "Squad Transport":{"cost": 300,  "build": 20, "min_tier": -1},
        "Combat Tank":    {"cost": 1800, "build": 35, "min_tier": -1},
        "Rocket Tank":    {"cost": 3000, "build": 30, "min_tier": 5},
        "Heavy Tank":     {"cost": 4000, "build": 45, "min_tier": 6},
        "Pyro Tank":      {"cost": 1800, "build": 30, "min_tier": 7},
        "Crimson Tank":   {"cost": 4500, "build": 60, "min_tier": 7},
        "Harvester":      {"cost": 1500, "build": 60, "min_tier": -1},
        "Dreadnought":    {"cost": 3500, "build": 35, "min_tier": -1},
        "Shuttle":        {"cost": 2400, "build": 35, "min_tier": -1},
        "Interceptor":    {"cost": 3000, "build": 30, "min_tier": 7},
        "Freighter":      {"cost": 5000, "build": 45, "min_tier": 8},
    },
    "other_structures": {
        "Refinery":       {"cost": 3000, "build": 30, "min_tier": -1},
        "Radar Station":  {"cost": 1000, "build": 30, "min_tier": 2},
        "Silo":           {"cost": 500,  "build": 15, "min_tier": -1},
        "Turret":                {"cost": 750,  "build": 35, "min_tier": 2},
        "Heavy Turret":          {"cost": 1500, "build": 35, "min_tier": 4},
        "Anti-Air Rocket Turret":{"cost": 2000, "build": 35, "min_tier": 6},
    },
}

# ========================================
# Proposed balance changes
# ========================================

ALIEN_CHANGES = {
    # Structures
    "Colossal Spawning Cyst": {"cost": 10000, "build": 150},
    "Node":        {"health_mult": 2.50},
    "Hive Spire":  {"damage_mult": 1.25},
    "Thorn Spire": {"damage_mult": 1.75},
    # Units
    "Crab":        {"build": 3, "cost": 25},
    "Shocker":     {"cost": 300, "health_mult": 1.25},
    "Horned Crab": {"health_mult": 3.0, "damage_mult": 1.20, "cost": 100, "build": 10},
    "Hunter":      {"build": 25},
    "Behemoth":    {"damage_mult": 1.30, "health_mult": 1.30},
    "Dragonfly":   {"health_mult": 1.35, "damage_mult": 1.20, "build": 20},
    "Goliath":     {"cost": 3500},
    "Scorpion":    {"health_mult": 1.15},
    "Defiler":     {"health_mult": 1.65},
}

SOL_CHANGES = {
    # Structures
    "Headquarters":        {"health_mult": 1.75},
    "Refinery":            {"health_mult": 1.75},
    "Barracks":            {"health_mult": 1.75},
    "Air Factory":         {"cost": 7500},
    "Heavy Factory":       {"build": 90},
    "Ultra Heavy Factory": {"cost": 10000, "build": 150},
    # Units
    "Rifleman":      {"build": 15},
    "Scout":         {"damage_mult": 1.40, "build": 15},
    "Commando":      {"damage_mult": 1.20},
    "Light Striker":  {"damage_mult": 1.20},
    "Gunship":       {"min_tier": 7},
    "Dropship":      {"health_mult": 1.50, "damage_mult": 0.80},
    "Pulse Truck":   {"health_mult": 1.50},
    "Bomber":        {"health_mult": 1.30},
    "Siege Tank":    {"cost": 7500},
}

CENTAURI_CHANGES = {
    # Structures
    "Headquarters":        {"health_mult": 1.75},
    "Refinery":            {"health_mult": 1.75},
    "Barracks":            {"health_mult": 1.75},
    "Air Factory":         {"cost": 7500},
    "Heavy Factory":       {"build": 90},
    "Ultra Heavy Factory": {"cost": 10000, "build": 150},
    # Units
    "Militia":      {"damage_mult": 1.30, "build": 15},
    "Trooper":      {"build": 15},
    "Interceptor":  {"cost": 1500, "damage_note": "MG DMG x0.5"},
    "Dreadnought":  {"min_tier": 7, "cost": 2500},
    "Shuttle":      {"health_mult": 1.50, "damage_mult": 0.80},
    "Combat Tank":  {"damage_note": "DMG x1.5 / SG x2"},
    "Pyro Tank":    {"health_mult": 2.50, "damage_mult": 1.50},
    "Freighter":    {"health_mult": 1.25},
    "Crimson Tank": {"cost": 7500},
    "Flak Car":     {"damage_mult": 1.30, "damage_note": "DMG x1.3 (air)"},
}

# ========================================
# Color schemes
# ========================================
COLORS = {
    "Alien": {
        "bg": "#1a0a2e",
        "producer": "#6b2fa0",
        "producer_text": "#ffffff",
        "unit": "#2d8a4e",
        "unit_text": "#ffffff",
        "structure": "#8b5a2b",
        "structure_text": "#ffffff",
        "tech": "#b8860b",
        "tech_text": "#ffffff",
        "arrow": "#9370db",
        "title": "#d8b4fe",
        "change": "#ffd740",
    },
    "Sol": {
        "bg": "#0a1628",
        "producer": "#1e5fa8",
        "producer_text": "#ffffff",
        "unit": "#2e7d32",
        "unit_text": "#ffffff",
        "structure": "#6d4c41",
        "structure_text": "#ffffff",
        "tech": "#f57f17",
        "tech_text": "#ffffff",
        "arrow": "#64b5f6",
        "title": "#90caf9",
        "change": "#ffd740",
    },
    "Centauri": {
        "bg": "#1a0a0a",
        "producer": "#a02f2f",
        "producer_text": "#ffffff",
        "unit": "#2e7d32",
        "unit_text": "#ffffff",
        "structure": "#6d4c41",
        "structure_text": "#ffffff",
        "tech": "#f57f17",
        "tech_text": "#ffffff",
        "arrow": "#ef9a9a",
        "title": "#ef9a9a",
        "change": "#ffd740",
    },
}


def tier_label(min_tier):
    if min_tier <= 0:
        return "T0"
    return f"T{min_tier}"


def fmt_mult(v):
    """Format a multiplier value compactly."""
    if v == int(v):
        return str(int(v))
    return f"{v:.2f}".rstrip('0').rstrip('.')


def draw_box(ax, x, y, w, h, name, cost, build, min_tier, color, text_color,
             is_producer=False, changes=None, change_color='#ffd740'):
    """Draw a box with name, original stats, and optional change indicators."""
    lw = 2.5 if is_producer else 1.5
    ec = change_color if changes else '#cccccc'
    rect = mpatches.FancyBboxPatch(
        (x, y), w, h,
        boxstyle="round,pad=0.03",
        facecolor=color, edgecolor=ec, linewidth=lw,
    )
    ax.add_patch(rect)

    has_changes = changes and len(changes) > 0

    # Name (bold, larger)
    fontsize_name = 9 if len(name) <= 18 else 7
    if has_changes:
        name_y = y + h * 0.78
    else:
        name_y = y + h * 0.65
    ax.text(x + w/2, name_y, name,
            ha='center', va='center', fontsize=fontsize_name,
            fontweight='bold', color=text_color, family='monospace')

    # Original stats line
    tier_str = tier_label(min_tier)
    stats = f"${cost}  {build}s  {tier_str}"
    if has_changes:
        stats_y = y + h * 0.52
    else:
        stats_y = y + h * 0.28
    ax.text(x + w/2, stats_y, stats,
            ha='center', va='center', fontsize=7,
            color=text_color, family='monospace', alpha=0.85)

    # Changes line
    if has_changes:
        parts = []
        if "cost" in changes:
            parts.append(f"${changes['cost']}")
        if "build" in changes:
            parts.append(f"{changes['build']}s")
        if "min_tier" in changes:
            parts.append(f"T{max(0, changes['min_tier'])}")
        if "damage_note" in changes:
            parts.append(changes["damage_note"])
        elif "damage_mult" in changes:
            parts.append(f"DMG x{fmt_mult(changes['damage_mult'])}")
        if "health_mult" in changes:
            parts.append(f"HP x{fmt_mult(changes['health_mult'])}")
        change_text = " ".join(parts)
        ax.text(x + w/2, y + h * 0.22, change_text,
                ha='center', va='center', fontsize=6.5,
                color=change_color, family='monospace', fontweight='bold')


def draw_arrow(ax, x1, y1, x2, y2, color):
    ax.annotate('', xy=(x2, y2), xytext=(x1, y1),
                arrowprops=dict(arrowstyle='->', color=color, lw=1.2, alpha=0.7))


# ========================================
# Alien tech tree
# ========================================
def generate_alien_pdf(changes):
    colors = COLORS["Alien"]
    chg_color = colors["change"]
    fig, ax = plt.subplots(1, 1, figsize=(26, 20))
    fig.patch.set_facecolor(colors["bg"])
    ax.set_facecolor(colors["bg"])
    ax.set_xlim(0, 26)
    ax.set_ylim(0, 20)
    ax.axis('off')

    # Title
    ax.text(13, 19.3, "ALIEN TECH TREE", ha='center', va='center',
            fontsize=26, fontweight='bold', color=colors["title"], family='monospace')
    ax.text(13, 18.7, "Original values shown. Amber border + bottom line = proposed changes.",
            ha='center', va='center', fontsize=9, color='#aaaaaa', family='monospace')

    bw, bh = 2.8, 1.0   # unit box
    pw, ph = 3.0, 1.05   # producer box
    unit_sp = 1.15        # vertical spacing between units

    # === NEST (top center) ===
    nest_x, nest_y = 11.5, 17.0
    draw_box(ax, nest_x, nest_y, pw, ph, "NEST", 3000, 60, 3,
             colors["producer"], colors["producer_text"], is_producer=True,
             change_color=chg_color)

    # === Structures built by Nest (far left) ===
    nest_structs = [
        ("Node",        100,  6,  -1),
        ("Bio Cache",   500,  10, -1),
        ("Thorn Spire", 1000, 35, 2),
        ("Hive Spire",  1000, 35, 3),
    ]
    sx = 0.3
    for i, (name, cost, build, mt) in enumerate(nest_structs):
        sy = 16.8 - i * 1.15
        ch = changes.get(name)
        draw_box(ax, sx, sy, bw, bh, name, cost, build, mt,
                 colors["structure"], colors["structure_text"],
                 changes=ch, change_color=chg_color)
        draw_arrow(ax, nest_x, nest_y + ph/2, sx + bw, sy + bh/2, colors["arrow"])

    # === Quantum Cortex (tech research) ===
    qc_x, qc_y = 0.3, 12.0
    draw_box(ax, qc_x, qc_y, pw + 0.2, ph, "QUANTUM CORTEX", 2000, 25, -1,
             colors["producer"], colors["producer_text"], is_producer=True,
             change_color=chg_color)
    draw_arrow(ax, nest_x, nest_y, qc_x + (pw+0.2), qc_y + ph/2, colors["arrow"])

    # Tech tiers with progressive time changes
    tech_names = ["Alpha I", "Beta II", "Gamma III", "Delta IV",
                  "Theta V", "Lambda VI", "Sigma VII", "Omega VIII"]
    tw, th_box = 2.6, 0.85
    tech_sp = 0.95
    for i, name in enumerate(tech_names):
        tx = 0.2
        ty = 10.8 - i * tech_sp
        tier_num = i + 1
        tech_ch = {"build": TECH_TIMES[tier_num]}
        draw_box(ax, tx, ty, tw, th_box, name, 2000, TECH_TIME_ORIG, i,
                 colors["tech"], colors["tech_text"],
                 changes=tech_ch, change_color=chg_color)
        # Chain arrows
        if i == 0:
            draw_arrow(ax, qc_x + (pw+0.2)/2, qc_y,
                       tx + tw/2, ty + th_box, colors["arrow"])
        else:
            prev_ty = 10.8 - (i-1) * tech_sp
            draw_arrow(ax, tx + tw/2, prev_ty,
                       tx + tw/2, ty + th_box, colors["arrow"])
        # Cumulative time label
        cum_s = TECH_CUMULATIVE[tier_num]
        cum_m, cum_sec = divmod(cum_s, 60)
        ax.text(tx + tw + 0.15, ty + th_box/2, f"{cum_m}:{cum_sec:02d}",
                fontsize=6.5, color='#999999', va='center', family='monospace')

    # === Lesser Spawning Cyst ===
    lsc_x, lsc_y = 5.0, 15.8
    lsc_w = pw + 1.0
    draw_box(ax, lsc_x, lsc_y, lsc_w, ph, "LESSER SPAWNING CYST", 1500, 20, -1,
             colors["producer"], colors["producer_text"], is_producer=True,
             change_color=chg_color)
    draw_arrow(ax, nest_x, nest_y + ph/2, lsc_x + lsc_w, lsc_y + ph/2, colors["arrow"])

    lsc_units = [
        ("Crab",      80,   10, -1),
        ("Shrimp",    160,  15, -1),
        ("Shocker",   220,  20,  1),
        ("Wasp",      200,  20,  4),
        ("Dragonfly", 400,  25,  5),
        ("Squid",     120,  10,  6),
    ]
    for i, (name, cost, build, mt) in enumerate(lsc_units):
        ux = 4.5
        uy = 14.4 - i * unit_sp
        ch = changes.get(name)
        draw_box(ax, ux, uy, bw, bh, name, cost, build, mt,
                 colors["unit"], colors["unit_text"],
                 changes=ch, change_color=chg_color)
        draw_arrow(ax, lsc_x + lsc_w/2, lsc_y, ux + bw/2, uy + bh, colors["arrow"])

    # === Greater Spawning Cyst ===
    gsc_x, gsc_y = 10.0, 15.8
    gsc_w = pw + 1.2
    gsc_ch = changes.get("Greater Spawning Cyst")
    draw_box(ax, gsc_x, gsc_y, gsc_w, ph, "GREATER SPAWNING CYST", 3000, 25, 2,
             colors["producer"], colors["producer_text"], is_producer=True,
             changes=gsc_ch, change_color=chg_color)
    draw_arrow(ax, nest_x + pw/2, nest_y, gsc_x + gsc_w/2, gsc_y + ph, colors["arrow"])

    gsc_units = [
        ("Horned Crab", 160,  20, -1),
        ("Hunter",      500,  30,  3),
        ("Behemoth",    1200, 45,  4),
        ("Scorpion",    1600, 45,  5),
        ("Firebug",     2400, 45,  6),
    ]
    for i, (name, cost, build, mt) in enumerate(gsc_units):
        ux = 9.8
        uy = 14.4 - i * unit_sp
        ch = changes.get(name)
        draw_box(ax, ux, uy, bw, bh, name, cost, build, mt,
                 colors["unit"], colors["unit_text"],
                 changes=ch, change_color=chg_color)
        draw_arrow(ax, gsc_x + gsc_w/2, gsc_y, ux + bw/2, uy + bh, colors["arrow"])

    # === Grand Spawning Cyst ===
    grsc_x, grsc_y = 15.5, 15.8
    grsc_w = pw + 0.8
    draw_box(ax, grsc_x, grsc_y, grsc_w, ph, "GRAND SPAWNING CYST", 4000, 30, 4,
             colors["producer"], colors["producer_text"], is_producer=True,
             change_color=chg_color)
    draw_arrow(ax, nest_x + pw, nest_y + ph/2, grsc_x, grsc_y + ph/2, colors["arrow"])

    ch = changes.get("Goliath")
    draw_box(ax, 15.5, 14.4, bw, bh, "Goliath", 4000, 60, -1,
             colors["unit"], colors["unit_text"],
             changes=ch, change_color=chg_color)
    draw_arrow(ax, grsc_x + grsc_w/2, grsc_y, 15.5 + bw/2, 14.4 + bh, colors["arrow"])

    # === Colossal Spawning Cyst ===
    csc_x, csc_y = 20.0, 15.8
    csc_w = pw + 1.4
    csc_ch = changes.get("Colossal Spawning Cyst")
    draw_box(ax, csc_x, csc_y, csc_w, ph, "COLOSSAL SPAWNING CYST", 6000, 30, 7,
             colors["producer"], colors["producer_text"], is_producer=True,
             changes=csc_ch, change_color=chg_color)
    draw_arrow(ax, nest_x + pw, nest_y + ph/4, csc_x, csc_y + ph/2, colors["arrow"])

    csc_units = [
        ("Defiler",  4200, 90, -1),
        ("Colossus", 6000, 120, 8),
    ]
    for i, (name, cost, build, mt) in enumerate(csc_units):
        ux = 20.3
        uy = 14.4 - i * unit_sp
        ch = changes.get(name)
        draw_box(ax, ux, uy, bw, bh, name, cost, build, mt,
                 colors["unit"], colors["unit_text"],
                 changes=ch, change_color=chg_color)
        draw_arrow(ax, csc_x + csc_w/2, csc_y, ux + bw/2, uy + bh, colors["arrow"])

    # Legend
    legend_items = [
        (colors["producer"], "Production Structure"),
        (colors["unit"], "Unit"),
        (colors["structure"], "Support Structure"),
        (colors["tech"], "Tech Tier Research"),
    ]
    for i, (c, label) in enumerate(legend_items):
        lx = 19.5
        ly = 3.5 - i * 0.6
        rect = mpatches.FancyBboxPatch((lx, ly), 0.45, 0.4, boxstyle="round,pad=0.02",
                                        facecolor=c, edgecolor='#cccccc', linewidth=1)
        ax.add_patch(rect)
        ax.text(lx + 0.65, ly + 0.2, label, fontsize=8, color='#cccccc',
                va='center', family='monospace')
    # Change indicator in legend
    rect = mpatches.FancyBboxPatch((19.5, 3.5 - 4*0.6), 0.45, 0.4,
                                    boxstyle="round,pad=0.02",
                                    facecolor='#333333', edgecolor=chg_color, linewidth=2)
    ax.add_patch(rect)
    ax.text(19.5 + 0.65, 3.5 - 4*0.6 + 0.2, "Proposed Change",
            fontsize=8, color=chg_color, va='center', family='monospace')

    plt.tight_layout()
    return fig


# ========================================
# Human faction tech tree (Sol / Centauri)
# ========================================
def generate_human_pdf(faction_data, faction_name, changes):
    colors = COLORS[faction_name]
    chg_color = colors["change"]
    fig, ax = plt.subplots(1, 1, figsize=(30, 20))
    fig.patch.set_facecolor(colors["bg"])
    ax.set_facecolor(colors["bg"])
    ax.set_xlim(0, 30)
    ax.set_ylim(0, 20)
    ax.axis('off')

    # Title
    ax.text(15, 19.3, f"{faction_name.upper()} TECH TREE", ha='center', va='center',
            fontsize=26, fontweight='bold', color=colors["title"], family='monospace')
    ax.text(15, 18.7, "Original values shown. Amber border + bottom line = proposed changes.",
            ha='center', va='center', fontsize=9, color='#aaaaaa', family='monospace')

    bw, bh = 2.8, 1.0    # unit box
    pw, ph = 3.0, 1.05    # producer box
    unit_sp = 1.15         # vertical spacing between units
    tech_sp = 0.95         # vertical spacing between tech tiers

    structs = faction_data["structures"]
    units = faction_data["units"]
    other = faction_data["other_structures"]

    # === HQ (top center) ===
    hq_x, hq_y = 13.5, 17.3
    hq = structs["Headquarters"]
    hq_ch = changes.get("Headquarters")
    draw_box(ax, hq_x, hq_y, pw, ph, "HEADQUARTERS", hq["cost"], hq["build"], hq["min_tier"],
             colors["producer"], colors["producer_text"], is_producer=True,
             changes=hq_ch, change_color=chg_color)

    # === Production columns — evenly spaced ===
    columns = [
        ("Research Facility",   0.5),
        ("Barracks",            5.2),
        ("Light Factory",       9.9),
        ("Heavy Factory",       14.6),
        ("Air Factory",         19.3),
        ("Ultra Heavy Factory", 24.0),
    ]

    prod_y = 15.4

    for prod_name, col_x in columns:
        p = structs[prod_name]
        pbox_w = pw + 0.8 if len(prod_name) > 14 else pw
        prod_ch = changes.get(prod_name)
        draw_box(ax, col_x, prod_y, pbox_w, ph, prod_name.upper(),
                 p["cost"], p["build"], p["min_tier"],
                 colors["producer"], colors["producer_text"], is_producer=True,
                 changes=prod_ch, change_color=chg_color)
        draw_arrow(ax, hq_x + pw/2, hq_y, col_x + pbox_w/2, prod_y + ph, colors["arrow"])

        # Draw items produced by this structure
        produced = p["produces"]
        unit_idx = 0
        for item_name in produced:
            if item_name in units:
                u = units[item_name]
                ux = col_x
                uy = 13.9 - unit_idx * unit_sp
                unit_ch = changes.get(item_name)
                draw_box(ax, ux, uy, bw, bh, item_name,
                         u["cost"], u["build"], u["min_tier"],
                         colors["unit"], colors["unit_text"],
                         changes=unit_ch, change_color=chg_color)
                draw_arrow(ax, col_x + pbox_w/2, prod_y,
                           ux + bw/2, uy + bh, colors["arrow"])
                unit_idx += 1
            elif item_name.startswith("Mark"):
                tier_num = ["Mark I", "Mark II", "Mark III", "Mark IV",
                            "Mark V", "Mark VI", "Mark VII", "Mark VIII"].index(item_name)
                tier_idx = tier_num + 1  # 1-indexed
                tx = col_x
                ty = 13.9 - tier_num * tech_sp
                tw, th_box = 2.6, 0.85
                tech_ch = {"build": TECH_TIMES[tier_idx]}
                draw_box(ax, tx, ty, tw, th_box, item_name,
                         2000, TECH_TIME_ORIG, tier_num,
                         colors["tech"], colors["tech_text"],
                         changes=tech_ch, change_color=chg_color)
                # Chain arrows
                if tier_num == 0:
                    draw_arrow(ax, col_x + pbox_w/2, prod_y,
                               tx + tw/2, ty + th_box, colors["arrow"])
                else:
                    prev_ty = 13.9 - (tier_num - 1) * tech_sp
                    draw_arrow(ax, tx + tw/2, prev_ty,
                               tx + tw/2, ty + th_box, colors["arrow"])
                # Cumulative time label
                cum_s = TECH_CUMULATIVE[tier_idx]
                cum_m, cum_sec = divmod(cum_s, 60)
                ax.text(tx + tw + 0.15, ty + th_box/2, f"{cum_m}:{cum_sec:02d}",
                        fontsize=6.5, color='#999999', va='center', family='monospace')

    # === Support Structures — horizontal row near bottom ===
    ax.text(15, 5.2, "Support Structures (built from Headquarters)",
            ha='center', va='center', fontsize=10, fontweight='bold',
            color='#aaaaaa', family='monospace')

    support_items = [
        ("Refinery",              other["Refinery"]),
        ("Silo",                  other["Silo"]),
        ("Radar Station",         other["Radar Station"]),
        ("Turret",                other["Turret"]),
        ("Heavy Turret",          other["Heavy Turret"]),
        ("Anti-Air Rocket Turret", other["Anti-Air Rocket Turret"]),
    ]

    support_spacing = 4.6
    total_w = (len(support_items) - 1) * support_spacing + bw
    support_start_x = (30 - total_w) / 2

    for i, (name, data) in enumerate(support_items):
        sx = support_start_x + i * support_spacing
        sy = 3.8
        box_w = bw + 0.6 if len(name) > 16 else bw
        sup_ch = changes.get(name)
        draw_box(ax, sx, sy, box_w, bh, name,
                 data["cost"], data["build"], data["min_tier"],
                 colors["structure"], colors["structure_text"],
                 changes=sup_ch, change_color=chg_color)

    # Legend
    legend_items = [
        (colors["producer"], "Production Structure"),
        (colors["unit"], "Unit"),
        (colors["structure"], "Support Structure"),
        (colors["tech"], "Tech Tier Research"),
    ]
    for i, (c, label) in enumerate(legend_items):
        lx = 24.0
        ly = 2.5 - i * 0.6
        rect = mpatches.FancyBboxPatch((lx, ly), 0.45, 0.4, boxstyle="round,pad=0.02",
                                        facecolor=c, edgecolor='#cccccc', linewidth=1)
        ax.add_patch(rect)
        ax.text(lx + 0.65, ly + 0.2, label, fontsize=8, color='#cccccc',
                va='center', family='monospace')
    # Change indicator
    rect = mpatches.FancyBboxPatch((24.0, 2.5 - 4*0.6), 0.45, 0.4,
                                    boxstyle="round,pad=0.02",
                                    facecolor='#333333', edgecolor=chg_color, linewidth=2)
    ax.add_patch(rect)
    ax.text(24.0 + 0.65, 2.5 - 4*0.6 + 0.2, "Proposed Change",
            fontsize=8, color=chg_color, va='center', family='monospace')

    plt.tight_layout()
    return fig


def main():
    # Generate Alien PDF
    print("Generating Alien tech tree...")
    fig = generate_alien_pdf(ALIEN_CHANGES)
    fig.savefig(os.path.join(OUTPUT_DIR, "alien_tech_tree.pdf"), format='pdf',
                bbox_inches='tight', facecolor=fig.get_facecolor())
    plt.close(fig)
    print(f"  -> {os.path.join(OUTPUT_DIR, 'alien_tech_tree.pdf')}")

    # Generate Sol PDF
    print("Generating Sol tech tree...")
    fig = generate_human_pdf(SOL, "Sol", SOL_CHANGES)
    fig.savefig(os.path.join(OUTPUT_DIR, "sol_tech_tree.pdf"), format='pdf',
                bbox_inches='tight', facecolor=fig.get_facecolor())
    plt.close(fig)
    print(f"  -> {os.path.join(OUTPUT_DIR, 'sol_tech_tree.pdf')}")

    # Generate Centauri PDF
    print("Generating Centauri tech tree...")
    fig = generate_human_pdf(CENTAURI, "Centauri", CENTAURI_CHANGES)
    fig.savefig(os.path.join(OUTPUT_DIR, "centauri_tech_tree.pdf"), format='pdf',
                bbox_inches='tight', facecolor=fig.get_facecolor())
    plt.close(fig)
    print(f"  -> {os.path.join(OUTPUT_DIR, 'centauri_tech_tree.pdf')}")

    print("Done!")


if __name__ == "__main__":
    main()

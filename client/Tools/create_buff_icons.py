"""Build the in-game buff icon atlas in Blender and save its editable scene."""

from __future__ import annotations

import math
from pathlib import Path

import bpy


ROOT = Path(__file__).resolve().parents[1]
ATLAS = ROOT / "Assets/FortyTwoAndBeyond/Resources/BuffIcons.png"
SCENE = ROOT / "Art/Blender/buff_icons.blend"

# Row order is also the Unity atlas index. Keep the names visible in the HUD.
ICONS = (
    ("Turbo Weapons", "offense", "bolt"),
    ("Improved Beam Handling", "offense", "beam"),
    ("Improved Projectile Handling", "offense", "projectile"),
    ("Combat Trance", "offense", "trance"),
    ("Rally Damage", "rally", "bolt"),
    ("Rally Firing", "rally", "reticle"),
    ("Recharge Shields", "shield", "plus"),
    ("Shield Charging", "shield", "up"),
    ("Maximize Shields", "shield", "double"),
    ("Deflect Impact", "shield", "impact"),
    ("Deflect Explosive", "shield", "burst"),
    ("Deflect Plasma", "shield", "plasma"),
    ("Deflect Energy", "shield", "bolt"),
    ("Deflect EMP", "shield", "emp"),
    ("Deflect Chemical", "shield", "molecule"),
    ("Resist Psionic", "shield", "mind"),
    ("Psionic Shield", "shield", "orbit"),
    ("Environment Shield", "shield", "planet"),
    ("Reactor Boost", "reactor", "bolt"),
    ("Recharge Reactor", "reactor", "plus"),
    ("Reactor Optimization", "reactor", "orbit"),
    ("Shunt to Reactor", "reactor", "arrow"),
    ("Shield Inversion", "shield", "reverse"),
    ("Shield Sap", "shield", "drain"),
    ("Increase Speed", "movement", "chevrons"),
    ("Impulse Turbo", "movement", "thruster"),
    ("Turbo Warp", "movement", "warp"),
    ("Increase Maneuverability", "movement", "turn"),
    ("Scan Boost", "utility", "radar"),
    ("Cloak", "utility", "cloak"),
    ("Rally Defense", "rally", "shield"),
    ("Rally Stealth", "rally", "cloak"),
    ("Combat Cloak", "utility", "combat_cloak"),
    ("Repulsor Field", "utility", "repulsor"),
    ("Repair Equipment", "utility", "repair"),
    ("Power Down", "utility", "power"),
)

COLORS = {
    "offense": (1.0, 0.43, 0.21, 1.0),
    "shield": (0.19, 0.78, 1.0, 1.0),
    "reactor": (0.38, 1.0, 0.69, 1.0),
    "movement": (0.52, 0.72, 1.0, 1.0),
    "utility": (0.75, 0.62, 1.0, 1.0),
    "rally": (1.0, 0.72, 0.29, 1.0),
}


def material(name: str, color: tuple[float, float, float, float]) -> bpy.types.Material:
    """Returns:
        bpy.types.Material: Flat luminous material for crisp small icons.
    """
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    emission = nodes.new("ShaderNodeEmission")
    emission.inputs[0].default_value = color
    emission.inputs[1].default_value = 1.0
    mat.node_tree.links.new(emission.outputs[0], output.inputs[0])
    mat.diffuse_color = color
    return mat


def polygon(name: str, points: list[tuple[float, float]], mat: bpy.types.Material,
            z: float) -> None:
    """Place one filled planar facet above its tile."""
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([(x, y, z) for x, y in points], [], [tuple(range(len(points)))])
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.data.materials.append(mat)


def stroke(name: str, points: list[tuple[float, float]], mat: bpy.types.Material,
           width: float = 0.045, z: float = 0.1, closed: bool = False) -> None:
    """Draw a round beveled line from planar points."""
    curve = bpy.data.curves.new(name, "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = width / 2
    curve.bevel_resolution = 3
    spline = curve.splines.new("POLY")
    spline.points.add(len(points) - 1)
    for point, (x, y) in zip(spline.points, points):
        point.co = (x, y, z, 1)
    spline.use_cyclic_u = closed
    obj = bpy.data.objects.new(name, curve)
    bpy.context.scene.collection.objects.link(obj)
    obj.data.materials.append(mat)


def ring(name: str, cx: float, cy: float, radius: float, mat: bpy.types.Material,
         start: float = 0.0, end: float = math.tau, width: float = 0.045,
         z: float = 0.1) -> None:
    """Draw a complete or partial circular energy track."""
    steps = max(10, int(56 * abs(end - start) / math.tau))
    points = [(cx + radius * math.cos(start + (end - start) * i / steps),
               cy + radius * math.sin(start + (end - start) * i / steps))
              for i in range(steps + 1)]
    stroke(name, points, mat, width, z)


def line(name: str, a: tuple[float, float], b: tuple[float, float],
         mat: bpy.types.Material, width: float = 0.06) -> None:
    """Draw one icon segment."""
    stroke(name, [a, b], mat, width)


def shield(cx: float, cy: float, mat: bpy.types.Material, size: float = 1.0) -> None:
    """Draw the common protective hull silhouette."""
    points = [(-.42, .36), (0, .52), (.42, .36), (.37, -.12),
              (.23, -.42), (0, -.57), (-.23, -.42), (-.37, -.12)]
    stroke("Shield contour", [(cx + x * size, cy + y * size) for x, y in points],
           mat, .065, closed=True)


def bolt(cx: float, cy: float, mat: bpy.types.Material, size: float = 1.0) -> None:
    """Draw a split energy strike."""
    points = [(-.06, .36), (.19, .36), (.02, .08), (.21, .08),
              (-.18, -.39), (-.07, -.07), (-.25, -.07)]
    stroke("Energy bolt", [(cx + x * size, cy + y * size) for x, y in points],
           mat, .09)


def reticle(cx: float, cy: float, mat: bpy.types.Material,
            size: float = 1.0) -> None:
    """Draw a compact targeting ring and four ticks."""
    ring("Reticle", cx, cy, .22 * size, mat, width=.05)
    for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        line("Reticle tick", (cx + dx * .28 * size, cy + dy * .28 * size),
             (cx + dx * .43 * size, cy + dy * .43 * size), mat, .055)


def chevron(cx: float, cy: float, mat: bpy.types.Material,
            size: float = 1.0) -> None:
    """Draw one forward thrust marker."""
    stroke("Thrust chevron", [(cx - .16 * size, cy + .19 * size),
                              (cx + .17 * size, cy),
                              (cx - .16 * size, cy - .19 * size)], mat, .07)


def arrow(cx: float, cy: float, mat: bpy.types.Material,
          size: float = 1.0, direction: float = 0.0) -> None:
    """Draw a directional energy transfer arrow."""
    ux, uy = math.cos(direction), math.sin(direction)
    vx, vy = -uy, ux
    a = (cx - ux * .29 * size, cy - uy * .29 * size)
    b = (cx + ux * .27 * size, cy + uy * .27 * size)
    line("Transfer shaft", a, b, mat, .065)
    for sign in (-1, 1):
        line("Transfer head", b,
             (b[0] - ux * .18 * size + vx * sign * .13 * size,
              b[1] - uy * .18 * size + vy * sign * .13 * size), mat, .065)


def mark(cx: float, cy: float, variant: str, mat: bpy.types.Material) -> None:
    """Draw the unique small symbol inside one family silhouette."""
    if variant in ("bolt", "trance"):
        bolt(cx, cy, mat, .72)
    elif variant in ("plus", "repair"):
        line("Plus horizontal", (cx - .19, cy), (cx + .19, cy), mat, .075)
        line("Plus vertical", (cx, cy - .19), (cx, cy + .19), mat, .075)
    elif variant in ("reticle", "beam", "projectile", "impact"):
        reticle(cx, cy, mat, .72)
    elif variant in ("up", "thruster"):
        arrow(cx, cy, mat, .75, math.pi / 2)
    elif variant in ("drain", "arrow"):
        arrow(cx, cy, mat, .75, math.pi)
    elif variant in ("reverse", "turn"):
        ring("Reverse arc", cx, cy, .25, mat, -.25, math.pi * 1.3, .065)
        chevron(cx - .25, cy - .10, mat, .40)
    elif variant in ("burst", "repulsor"):
        for i in range(8):
            a = math.tau * i / 8
            line("Radial pulse", (cx + .09 * math.cos(a), cy + .09 * math.sin(a)),
                 (cx + .33 * math.cos(a), cy + .33 * math.sin(a)), mat, .06)
    elif variant in ("plasma", "cloak"):
        stroke("Plasma flame", [(cx, cy + .33), (cx + .20, cy + .04),
                                (cx + .12, cy - .27), (cx, cy - .34),
                                (cx - .17, cy - .12), (cx, cy + .33)], mat, .065)
    elif variant in ("emp", "radar"):
        ring("Signal inner", cx - .13, cy - .13, .20, mat, 0, math.pi / 2, .055)
        ring("Signal outer", cx - .13, cy - .13, .40, mat, 0, math.pi / 2, .055)
    elif variant in ("molecule", "planet"):
        for dx, dy in ((-.19, -.08), (.18, -.16), (.06, .20)):
            ring("Molecule node", cx + dx, cy + dy, .075, mat, width=.05)
        line("Molecule bond", (cx - .12, cy - .05), (cx + .07, cy - .13), mat, .045)
        line("Molecule bond", (cx + .13, cy - .08), (cx + .07, cy + .13), mat, .045)
    elif variant in ("mind", "orbit"):
        ring("Inner orbit", cx, cy, .15, mat, width=.06)
        ring("Outer orbit", cx, cy, .31, mat, .25, math.tau - .6, .055)
    elif variant == "double":
        ring("Inner shield", cx, cy, .23, mat, width=.07)
    elif variant == "warp":
        for dy in (-.18, 0, .18):
            line("Warp streak", (cx - .35, cy + dy), (cx + .31, cy + dy * .5), mat, .07)
    elif variant == "chevrons":
        chevron(cx - .15, cy, mat, .85)
        chevron(cx + .14, cy, mat, .85)
    elif variant == "combat_cloak":
        reticle(cx, cy, mat, .73)
        line("Cloak slash", (cx - .33, cy - .31), (cx + .33, cy + .31), mat, .085)
    elif variant == "power":
        ring("Power ring", cx, cy - .04, .27, mat, math.pi * .18, math.pi * 1.82, .07)
        line("Power stem", (cx, cy + .37), (cx, cy - .04), mat, .075)


def draw_icon(index: int, group: str, variant: str,
              materials: dict[str, bpy.types.Material]) -> None:
    """Draw one beveled HUD tile with its family and effect symbol."""
    col, row = index % 6, index // 6
    cx, cy = -5 + col * 2, 5 - row * 2
    color = materials[group]
    polygon("Tile graphite", [(cx - .94, cy - .94), (cx + .94, cy - .94),
                              (cx + .94, cy + .94), (cx - .94, cy + .94)],
            materials["tile"], -.04)
    stroke("Tile rim", [(cx - .77, cy + .84), (cx + .73, cy + .84),
                         (cx + .84, cy + .73), (cx + .84, cy - .73),
                         (cx + .73, cy - .84), (cx - .73, cy - .84),
                         (cx - .84, cy - .73), (cx - .84, cy + .73)],
           materials["rim"], .025, .03, True)
    ring("Energy bezel", cx, cy, .64, color, math.pi * .15,
         math.tau * .92, .035)
    ring("Bezel shadow", cx, cy, .56, materials["inner"], width=.032)
    if group == "shield":
        shield(cx, cy, color)
    elif group == "reactor":
        ring("Reactor core", cx, cy, .42, color, width=.075)
        for i in range(6):
            a = math.tau * i / 6
            line("Core spoke", (cx + .44 * math.cos(a), cy + .44 * math.sin(a)),
                 (cx + .53 * math.cos(a), cy + .53 * math.sin(a)), color, .055)
    elif group == "rally":
        ring("Rally arc", cx, cy, .47, color, math.pi * .1, math.pi * .9, .085)
        arrow(cx, cy - .02, color, .95, math.pi / 2)
    elif group == "offense":
        reticle(cx, cy, color)
    elif group == "movement":
        chevron(cx - .25, cy, color, .88)
    elif group == "utility":
        ring("Utility field", cx, cy, .39, color, width=.055)
    mark(cx, cy, variant, materials["white"])
    polygon("Status glint", [(cx - .62, cy + .62), (cx - .42, cy + .62),
                             (cx - .57, cy + .54)], materials["white"], .13)


def build() -> None:
    """Create and render the icon sheet in a separate editable Blender file."""
    scene = bpy.data.scenes.new("42 and Beyond Buff Icons")
    bpy.context.window.scene = scene
    for other in tuple(bpy.data.scenes):
        if other != scene:
            bpy.data.scenes.remove(other)
    scene.name = "42 and Beyond Buff Icons"
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = 1536
    scene.render.resolution_y = 1536
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = False
    scene.render.filepath = str(ATLAS)
    scene.world = bpy.data.worlds.new("Icon studio night")
    scene.world.color = (.015, .025, .045)
    materials = {name: material(name, color) for name, color in COLORS.items()}
    materials["tile"] = material("Graphite tile", (.025, .057, .085, 1))
    materials["rim"] = material("Muted rim", (.16, .29, .36, 1))
    materials["inner"] = material("Inner track", (.06, .12, .17, 1))
    materials["white"] = material("Icy highlight", (.78, .96, 1, 1))
    for index, (_, group, variant) in enumerate(ICONS):
        draw_icon(index, group, variant, materials)
    camera_data = bpy.data.cameras.new("Atlas orthographic")
    camera = bpy.data.objects.new("Atlas orthographic", camera_data)
    scene.collection.objects.link(camera)
    camera.location = (0, 0, 30)
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = 12
    scene.camera = camera
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "Medium High Contrast"
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    ATLAS.parent.mkdir(parents=True, exist_ok=True)
    SCENE.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.render.render(write_still=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(SCENE))
    print(f"Rendered {len(ICONS)} buff icons: {ATLAS}")
    print(f"Editable Blender scene: {SCENE}")


if __name__ == "__main__":
    build()

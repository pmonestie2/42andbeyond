"""Render editable Blender concepts for Shield Sap, Turbo, and Shield Burn."""

from __future__ import annotations

import math
import random
from pathlib import Path

import bpy


ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "Art/Blender"
FILE = ART / "ability_effect_concepts.blend"
PROMPT = (
    "Shield Sap vacuums energy off an enemy shield; Turbo draws three red rounded "
    "stripes into the ship heart; Shield Burn projects energy to scorch an enemy shield."
)


def emit(name: str, color: tuple[float, float, float, float],
         strength: float = 1.0) -> bpy.types.Material:
    """Returns:
        bpy.types.Material: Flat luminous studio material with predictable color.
    """
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    emission = nodes.new("ShaderNodeEmission")
    emission.inputs[0].default_value = color
    emission.inputs[1].default_value = strength
    material.node_tree.links.new(emission.outputs[0], output.inputs[0])
    material.diffuse_color = color
    return material


def poly(name: str, xy: list[tuple[float, float]], material: bpy.types.Material,
         z: float = 0.0) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: One filled facet in the concept stage.
    """
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([(x, y, z) for x, y in xy], [], [tuple(range(len(xy)))])
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.data.materials.append(material)
    return obj


def path(name: str, xy: list[tuple[float, float]], material: bpy.types.Material,
         width: float = .05, z: float = .1, closed: bool = False) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Rounded luminous line or ribbon ready for animation.
    """
    curve = bpy.data.curves.new(name, "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = width / 2
    curve.bevel_resolution = 4
    curve.use_fill_caps = True
    spline = curve.splines.new("POLY")
    spline.points.add(len(xy) - 1)
    for point, (x, y) in zip(spline.points, xy):
        point.co = (x, y, z, 1)
    spline.use_cyclic_u = closed
    obj = bpy.data.objects.new(name, curve)
    bpy.context.scene.collection.objects.link(obj)
    obj.data.materials.append(material)
    return obj


def arc(name: str, center: tuple[float, float], radius: float,
        start: float, end: float, material: bpy.types.Material,
        width: float = .05, z: float = .1) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Circular shield, ripple, or plasma arc.
    """
    steps = max(15, int(abs(end - start) / math.tau * 88))
    points = [(center[0] + radius * math.cos(start + (end - start) * i / steps),
               center[1] + radius * math.sin(start + (end - start) * i / steps))
              for i in range(steps + 1)]
    return path(name, points, material, width, z)


def disc(name: str, center: tuple[float, float], radius: float,
         material: bpy.types.Material, z: float = .12) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Small luminous particle or ship core.
    """
    points = [(radius * math.cos(math.tau * i / 32),
               radius * math.sin(math.tau * i / 32))
              for i in range(32)]
    obj = poly(name, points, material, z)
    obj.location = (center[0], center[1], 0)
    return obj


def bezier(a: tuple[float, float], b: tuple[float, float],
           c: tuple[float, float], d: tuple[float, float],
           samples: int = 48) -> list[tuple[float, float]]:
    """Returns:
        list[tuple[float, float]]: Smooth sampled cubic path for an energy ribbon.
    """
    points = []
    for i in range(samples + 1):
        t = i / samples
        u = 1 - t
        points.append((u**3 * a[0] + 3 * u*u*t * b[0] +
                       3 * u*t*t * c[0] + t**3 * d[0],
                       u**3 * a[1] + 3 * u*u*t * b[1] +
                       3 * u*t*t * c[1] + t**3 * d[1]))
    return points


def animate_trace(obj: bpy.types.Object, start: int, full: int,
                  clear: int = 48) -> None:
    """Grow a rounded stripe toward its target, then clear it for looping."""
    curve = obj.data
    for frame, value in ((1, 0.0), (start, .03), (full, 1.0),
                         (clear - 2, 1.0), (clear, 0.0)):
        curve.bevel_factor_end = value
        curve.keyframe_insert(data_path="bevel_factor_end", frame=frame)


def animate_particle(obj: bpy.types.Object,
                     points: list[tuple[float, float]],
                     start: int, end: int) -> None:
    """Move one energy bead from a shield or emitter into its destination."""
    for frame, index in ((1, 0), (start, 0), (end, len(points) - 1),
                         (48, len(points) - 1)):
        x, y = points[index]
        obj.location = (x, y, 0)
        obj.keyframe_insert(data_path="location", frame=frame)
    for frame, value in ((1, .2), (start, 1), (end, .7), (48, .2)):
        obj.scale = (value, value, value)
        obj.keyframe_insert(data_path="scale", frame=frame)


def text(name: str, content: str, x: float, y: float, size: float,
         material: bpy.types.Material) -> None:
    """Place a short studio caption above an effect without covering the art."""
    data = bpy.data.curves.new(name, "FONT")
    data.body = content
    data.size = size
    data.align_x = "CENTER"
    data.align_y = "CENTER"
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = (x, y, .25)
    obj.data.materials.append(material)


def stage(cx: float, title: str, subtitle: str,
          accent: bpy.types.Material, mats: dict[str, bpy.types.Material]) -> None:
    """Add one dark presentation bay with subtle scanning guides."""
    poly(f"{title} bay", [(cx - 2.48, -3.24), (cx + 2.48, -3.24),
                           (cx + 2.48, 3.24), (cx - 2.48, 3.24)], mats["panel"], -.3)
    for y in (-3.18, 3.18):
        path(f"{title} edge", [(cx - 2.45, y), (cx + 2.45, y)], mats["frame"], .018, -.16)
    for x in (cx - 2.43, cx + 2.43):
        path(f"{title} edge", [(x, -3.17), (x, 3.17)], mats["frame"], .018, -.16)
    for y in (-2.5, -1.5, -.5, .5, 1.5):
        path(f"{title} guide", [(cx - 2.30, y), (cx + 2.30, y)], mats["grid"], .012, -.15)
    randomizer = random.Random(round(cx * 100) + 42)
    for i in range(18):
        x = cx + randomizer.uniform(-2.25, 2.25)
        y = randomizer.uniform(-2.25, 2.25)
        disc(f"{title} distant star {i}", (x, y), randomizer.uniform(.008, .024),
             mats["star"], -.12)
    text(f"{title} title", title, cx, 2.70, .34, accent)
    text(f"{title} subtitle", subtitle, cx, 2.25, .14, mats["caption"])


def ship(name: str, center: tuple[float, float], forward: int,
         accent: bpy.types.Material, mats: dict[str, bpy.types.Material]) -> None:
    """Create a layered top-view ship with a visible central heart."""
    cx, cy = center
    def transformed(points: list[tuple[float, float]]) -> list[tuple[float, float]]:
        """Returns:
            list[tuple[float, float]]: Hull points facing the requested direction.
        """
        return [(cx + forward * x, cy + y) for x, y in points]
    outline = [(-.92, 0), (-.48, .22), (-.18, .48), (.46, .30),
               (1.03, 0), (.46, -.30), (-.18, -.48), (-.48, -.22)]
    poly(f"{name} luminous hull edge", transformed(outline), accent, -.03)
    inner = [(-.76, 0), (-.39, .16), (-.12, .35), (.40, .21),
             (.84, 0), (.40, -.21), (-.12, -.35), (-.39, -.16)]
    poly(f"{name} graphite fuselage", transformed(inner), mats["hull"], .01)
    for sign in (-1, 1):
        poly(f"{name} wing {sign}", transformed([(-.44, sign * .20),
            (-.71, sign * .83), (.26, sign * .52), (.38, sign * .20)]), mats["wing"], .02)
        path(f"{name} wing edge {sign}", transformed([(-.64, sign * .75),
            (.19, sign * .48)]), accent, .035, .05)
        path(f"{name} nose conductor {sign}", transformed([(.18, sign * .15),
            (.69, sign * .04)]), accent, .024, .07)
    disc(f"{name} heart surround", (cx, cy), .25, mats["core_housing"], .08)
    disc(f"{name} heart", (cx, cy), .12, accent, .13)
    arc(f"{name} heart ring", center, .22, 0, math.tau, accent, .036, .14)
    for dy in (-.20, 0, .20):
        path(f"{name} engine plume", transformed([(-.9, dy * .65),
             (-1.14, dy * .8), (-1.40, dy)]), accent, .025, -.01)


def shield_field(name: str, center: tuple[float, float], radius: float,
                 color: bpy.types.Material, dim: bpy.types.Material) -> list[bpy.types.Object]:
    """Returns:
        list[bpy.types.Object]: Layered shield tracks that can compress under damage.
    """
    tracks = [arc(f"{name} outer membrane", center, radius, 0, math.tau,
                  color, .067, .13),
              arc(f"{name} inner flow", center, radius * .93, .15,
                  math.tau - .15, dim, .028, .12)]
    for index in range(9):
        angle = math.tau * index / 9
        inner = (center[0] + radius * .86 * math.cos(angle),
                 center[1] + radius * .86 * math.sin(angle))
        outer = (center[0] + radius * 1.02 * math.cos(angle + .08),
                 center[1] + radius * 1.02 * math.sin(angle + .08))
        tracks.append(path(f"{name} membrane seam {index}", [inner, outer], dim,
                           .025, .14))
    return tracks


def shield_sap(cx: float, mats: dict[str, bpy.types.Material]) -> None:
    """Pull three cyan shield strands into the caster heart as the bubble caves in."""
    target = (cx - 1.35, -.18)
    caster = (cx + 1.42, -.18)
    ship("Sap target", target, 1, mats["enemy"], mats)
    ship("Sap caster", caster, -1, mats["ally"], mats)
    tracks = shield_field("Sap target", target, .91, mats["shield"], mats["shield_dim"])
    for track in tracks:
        for frame, scale in ((1, 1), (14, .98), (29, .75), (48, 1)):
            track.scale = (scale, scale, 1)
            track.keyframe_insert(data_path="scale", frame=frame)
            # Curves use world-space control points, so compensate around the target.
            track.location = (target[0] * (1 - scale), target[1] * (1 - scale), 0)
            track.keyframe_insert(data_path="location", frame=frame)
    for index, offset in enumerate((-.42, 0, .42)):
        points = bezier((target[0] + .81, target[1] + offset),
                        (cx - .03, -.20 + offset * .85),
                        (cx + .50, -.20 + offset * .30), caster)
        broad = path(f"Sap vacuum ribbon {index}", points, mats["sap_dim"], .16, .20)
        fine = path(f"Sap vacuum core {index}", points, mats["sap"], .062, .23)
        animate_trace(broad, 3 + index * 2, 21 + index * 2)
        animate_trace(fine, 5 + index * 2, 23 + index * 2)
        bead = disc(f"Sap moving fragment {index}", points[0], .085, mats["sap"], .28)
        animate_particle(bead, points, 6 + index * 2, 24 + index * 2)
    for index in range(3):
        ripple = arc(f"Vacuum mouth {index}", (cx - .30 + index * .19, -.18),
                     .35 + index * .17, -1.10, 1.10, mats["sap_dim"], .047, .25)
        ripple.data.bevel_factor_end = 1
    core = disc("Sap recipient charge", caster, .18, mats["sap"], .30)
    for frame, scale in ((1, .5), (12, .8), (26, 1.45), (39, 1.1), (48, .5)):
        core.scale = (scale, scale, 1)
        core.keyframe_insert(data_path="scale", frame=frame)


def turbo(cx: float, mats: dict[str, bpy.types.Material]) -> None:
    """Feed three separate rounded red stripes into the ship heart and pulse it."""
    center = (cx, -.18)
    ship("Turbo carrier", center, 1, mats["ally"], mats)
    for index, y in enumerate((-.92, 0, .92)):
        points = bezier((cx - 2.15, -.18 + y), (cx - 1.28, -.18 + y * 1.04),
                        (cx - .38, -.18 + y * .55), center)
        shadow = path(f"Turbo rounded red stripe shadow {index}", points,
                      mats["red_dim"], .22, .27)
        stripe = path(f"Turbo rounded red stripe {index}", points,
                      mats["red"], .116, .31)
        hot = path(f"Turbo stripe hot center {index}", points,
                   mats["red_hot"], .034, .34)
        for obj in (shadow, stripe, hot):
            animate_trace(obj, 2 + index * 3, 20 + index * 3)
        cap = disc(f"Turbo rounded stripe tail {index}", points[0],
                   .112, mats["red"], .33)
        for frame, scale in ((1, .01), (7 + index * 3, 1), (40, 1), (48, .01)):
            cap.scale = (scale, scale, 1)
            cap.keyframe_insert(data_path="scale", frame=frame)
        bead = disc(f"Turbo stripe head {index}", points[0], .105, mats["red_hot"], .35)
        animate_particle(bead, points, 3 + index * 3, 20 + index * 3)
    for index, radius in enumerate((.30, .49, .70)):
        hum = arc(f"Turbo audible hum ring {index}", center, radius,
                  -.62, math.tau - .62, mats["red_dim"], .046, .28)
        for frame, scale in ((1, .5), (14, .65), (27, 1.0), (38, 1.15), (48, .5)):
            hum.scale = (scale, scale, 1)
            hum.keyframe_insert(data_path="scale", frame=frame)
    heart = disc("Turbo hot humming heart", center, .18, mats["red_hot"], .37)
    for frame, scale in ((1, .38), (14, .85), (25, 1.33), (35, 1.12), (48, .38)):
        heart.scale = (scale, scale, 1)
        heart.keyframe_insert(data_path="scale", frame=frame)


def shield_burn(cx: float, mats: dict[str, bpy.types.Material]) -> None:
    """Drive hot energy into the enemy bubble, then spread a burning impact patch."""
    caster = (cx - 1.48, -.18)
    target = (cx + 1.30, -.18)
    ship("Burn caster", caster, 1, mats["ally"], mats)
    ship("Burn target", target, -1, mats["enemy"], mats)
    shield_field("Burn target", target, .91, mats["shield"], mats["shield_dim"])
    impact = (target[0] - .91, target[1])
    emitter = (caster[0] + .72, caster[1])
    for index, offset in enumerate((-.14, 0, .14)):
        points = bezier((emitter[0], emitter[1] + offset),
                        (cx - .40, -.18 + offset * 1.3),
                        (cx + .20, -.18 + offset * .65),
                        (impact[0], impact[1] + offset * .36))
        beam = path(f"Burn penetrating energy strand {index}", points,
                    mats["burn_dim"] if index else mats["burn"],
                    .07 if index else .18, .26 + index * .015)
        animate_trace(beam, 3 + index * 2, 17 + index * 2)
        bead = disc(f"Burn traveling packet {index}", points[0],
                    .06 if index else .09, mats["burn_hot"], .30)
        animate_particle(bead, points, 4 + index * 2, 18 + index * 2)
    arc("Burn contact shock", impact, .21, 0, math.tau,
        mats["burn_hot"], .09, .34)
    arc("Burn shield scorch", target, .92, math.pi * .64,
        math.pi * 1.36, mats["burn"], .13, .35)
    for index in range(11):
        angle = math.pi + (index - 5) * .24
        a = (target[0] + .82 * math.cos(angle), target[1] + .82 * math.sin(angle))
        b = (target[0] + (1.06 + .08 * (index % 3)) * math.cos(angle + .10),
             target[1] + (1.06 + .08 * (index % 3)) * math.sin(angle + .10))
        spark = path(f"Burn shield ember {index}", [a, b],
                     mats["burn_hot"] if index % 3 == 0 else mats["burn"],
                     .035, .38)
        for frame, value in ((1, 0), (15, .05), (24 + index % 5, 1), (48, 0)):
            spark.data.bevel_factor_end = value
            spark.data.keyframe_insert(data_path="bevel_factor_end", frame=frame)
    flare = disc("Burn hot impact point", impact, .19, mats["burn_hot"], .39)
    for frame, scale in ((1, .1), (16, .3), (23, 1.55), (37, 1.2), (48, .1)):
        flare.scale = (scale, scale, 1)
        flare.keyframe_insert(data_path="scale", frame=frame)


def render_views(scene: bpy.types.Scene, camera: bpy.types.Object) -> None:
    """Save the overview, three closeups, and an onset frame for review."""
    renders = (
        ("ability_effects_overview.png", 26, 0, 16.2, 1920, 900),
        ("shield_sap_concept.png", 26, -5.25, 6.5, 900, 1100),
        ("turbo_concept.png", 26, 0, 6.5, 900, 1100),
        ("shield_burn_concept.png", 26, 5.25, 6.5, 900, 1100),
        ("ability_effects_onset.png", 9, 0, 16.2, 1920, 900),
    )
    for name, frame, x, scale, width, height in renders:
        scene.frame_set(frame)
        camera.location.x = x
        camera.data.ortho_scale = scale
        scene.render.resolution_x = width
        scene.render.resolution_y = height
        scene.render.filepath = str(ART / name)
        bpy.ops.render.render(write_still=True)
        print(f"Rendered {name}")
    scene.frame_set(26)
    camera.location.x = 0
    camera.data.ortho_scale = 16.2
    scene.render.resolution_x = 1920
    scene.render.resolution_y = 900
    scene.render.filepath = str(ART / "ability_effects_overview.png")


def build() -> None:
    """Create the three animated effects without changing the saved prior scene."""
    scene = bpy.data.scenes.new("42 and Beyond Ability Effects")
    bpy.context.window.scene = scene
    for old in tuple(bpy.data.scenes):
        if old != scene:
            bpy.data.scenes.remove(old)
    scene.name = "42 and Beyond Ability Effects"
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = False
    scene.world = bpy.data.worlds.new("Deep space")
    scene.world.color = (.004, .009, .02)
    scene.frame_start = 1
    scene.frame_end = 48
    scene.render.fps = 24
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "Medium High Contrast"
    materials = {
        "panel": emit("Bay graphite", (.008, .022, .040, 1)),
        "frame": emit("Bay metal edge", (.11, .29, .40, 1)),
        "grid": emit("Subtle flight grid", (.018, .060, .085, 1)),
        "star": emit("Distant starlight", (.20, .34, .46, 1)),
        "caption": emit("Caption silver", (.55, .69, .77, 1)),
        "hull": emit("Hull dark ceramic", (.045, .090, .135, 1)),
        "wing": emit("Wing titanium", (.10, .19, .26, 1)),
        "core_housing": emit("Recessed core", (.010, .030, .055, 1)),
        "ally": emit("Allied hull trace", (.27, .81, 1.0, 1)),
        "enemy": emit("Hostile hull trace", (.83, .27, .30, 1)),
        "shield": emit("Shield membrane blue", (.17, .75, 1.0, 1)),
        "shield_dim": emit("Shield inner flow", (.07, .34, .52, 1)),
        "sap": emit("Shield Sap white cyan", (.55, 1.0, 1.0, 1)),
        "sap_dim": emit("Shield Sap vacuum haze", (.045, .49, .66, 1)),
        "red": emit("Turbo red rounded stripe", (1.0, .09, .10, 1)),
        "red_hot": emit("Turbo hot coral", (1.0, .38, .31, 1)),
        "red_dim": emit("Turbo red hum haze", (.40, .035, .065, 1)),
        "burn": emit("Shield Burn orange", (1.0, .30, .045, 1)),
        "burn_dim": emit("Shield Burn red corona", (.63, .075, .018, 1)),
        "burn_hot": emit("Shield Burn white hot", (1.0, .80, .32, 1)),
    }
    for cx, name, subtitle, accent in (
        (-5.25, "SHIELD SAP", "VACUUM THE SHIELD INTO YOUR CORE", "sap"),
        (0, "TURBO", "THREE RED BANDS CONVERGE ON THE HEART", "red_hot"),
        (5.25, "SHIELD BURN", "PROJECT HEAT INTO AN ENEMY BUBBLE", "burn_hot"),
    ):
        stage(cx, name, subtitle, materials[accent], materials)
    shield_sap(-5.25, materials)
    turbo(0, materials)
    shield_burn(5.25, materials)
    camera_data = bpy.data.cameras.new("Concept top camera")
    camera = bpy.data.objects.new("Concept top camera", camera_data)
    scene.collection.objects.link(camera)
    camera.location = (0, 0, 24)
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = 16.2
    scene.camera = camera
    ART.mkdir(parents=True, exist_ok=True)
    render_views(scene, camera)
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                area.spaces.active.region_3d.view_perspective = "CAMERA"
                area.spaces.active.shading.type = "MATERIAL"
    bpy.ops.wm.save_as_mainfile(filepath=str(FILE))
    print(f"Editable animated scene: {FILE}")


if __name__ == "__main__":
    build()

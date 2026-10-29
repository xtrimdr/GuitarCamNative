"""Renders the icon's lens in Blender (tools/make-icon.ps1 runs it): a camera iris of six brushed steel blades around
the sensor, an instrument lightly engraved on each blade, inside a polished steel ring. Transparent outside it.

    blender -b --factory-startup -P escena.py -- <engraving maps dir> <output.png> <size> [--plano]

--plano leaves the blades bare (no engravings), for the small sizes where they'd be noise.

The blades are the classic iris: each one is the part of the lens beyond a line tangent to the opening, and it shows
only where the next one doesn't cover it, so the six visible pieces are identical wedges. Each wedge leans (higher
along its own edge, lower where it slides under the next blade), so every blade casts a real shadow on the one
below, and its bevelled edge catches the light. The engraving is shallow: a dark, slightly sunken silhouette with
the drawing's lines as fine bright detail, mapped from its map through the wedge's own coordinates.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
MAPS, OUT, SIZE = args[0], args[1], int(args[2])
FLAT = "--plano" in args

# Geometry, in lens radii: the opening's half-width, the ring, how much each blade leans (height per unit across)
OPENING = 0.25
RING_IN, RING_OUT = 0.985, 1.13
LEAN = 0.22
# Blades start this far below the ring's top, so even their raised side stays under it
SINK = 0.22
# Where each instrument goes (the direction of its wedge, degrees counterclockwise from the right), the angle its
# drawing is turned (counterclockwise), the whole drawing's longest side in lens radii (big on purpose: the wedge
# crops it like a close-up in a film) and its most recognizable part (u, v from the drawing's bottom left), which
# goes in the middle of the wedge
PLACE = {
    "guitarra":  (120, 45, 1.95, (0.24, 0.5)),     # pickups, bridge and strings
    "teclado":   (60, -30, 1.7, (0.42, 0.55)),    # a few keys
    "microfono": (0, -8, 1.25, (0.5, 0.76)),      # the grille
    "saxo":      (-60, -12, 1.35, (0.6, 0.27)),    # the bell and its keys
    "violin":    (-120, -35, 1.25, (0.5, 0.3)),   # f-holes and bridge
    "trompeta":  (180, 12, 1.15, (0.62, 0.55)),   # the three valves
}
# How far from the wedge's tip its instrument sits
FOCUS = 0.58

scene = bpy.context.scene
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)


def normal(i):
    a = math.radians(60 * (i % 6))
    return Vector((math.sin(a), math.cos(a)))


def clip(poly, n, keep_ge):
    """Sutherland-Hodgman against the line p.n = OPENING, keeping p.n >= it (or <= it)."""
    out = []
    f = (lambda p: p.dot(n) - OPENING) if keep_ge else (lambda p: OPENING - p.dot(n))
    for j in range(len(poly)):
        a, b = poly[j], poly[(j + 1) % len(poly)]
        fa, fb = f(a), f(b)
        if fa >= 0:
            out.append(a)
        if (fa >= 0) != (fb >= 0):
            out.append(a + (b - a) * (fa / (fa - fb)))
    return out


def wedge(i):
    circle = [Vector((math.cos(t), math.sin(t))) * 1.04 for t in (2 * math.pi * k / 360 for k in range(360))]
    return clip(clip(circle, normal(i), True), normal(i + 1), False)


def apex(i):
    n1, n2 = normal(i), normal(i + 1)
    det = n1.x * n2.y - n1.y * n2.x
    return Vector(((OPENING * n2.y - OPENING * n1.y) / det, (n1.x * OPENING - n2.x * OPENING) / det))


def node(nt, kind, x, y, **props):
    n = nt.nodes.new(kind)
    n.location = (x, y)
    for k, v in props.items():
        setattr(n, k, v)
    return n


def link(nt, a, b):
    nt.links.new(a, b)


def principled(name, color, rough, metal=1.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*color, 1)
    b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    return m, b


def blade_material(i, name, bis, centre):
    """Brushed steel: fine streaks along the blade plus a few longer scratches across, varying its tone and polish."""
    m = bpy.data.materials.new(f"hoja{i}")
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = node(nt, "ShaderNodeOutputMaterial", 1300, 0)
    bsdf = node(nt, "ShaderNodeBsdfPrincipled", 1000, 0)
    link(nt, bsdf.outputs["BSDF"], out.inputs["Surface"])
    bsdf.inputs["Metallic"].default_value = 1.0
    tc = node(nt, "ShaderNodeTexCoord", -1400, 0)
    along = math.atan2(bis.y, bis.x)

    def streaks(x, y, angle, stretch, scale, detail):
        # Turn first, then stretch across: one Mapping node would stretch before turning, the same way on every blade
        turn = node(nt, "ShaderNodeMapping", x, y, vector_type="POINT")
        turn.inputs["Rotation"].default_value = (0, 0, -angle)
        mp = node(nt, "ShaderNodeMapping", x + 100, y, vector_type="POINT")
        mp.inputs["Scale"].default_value = (1, stretch, 1)
        nz = node(nt, "ShaderNodeTexNoise", x + 200, y)
        nz.inputs["Scale"].default_value = scale
        nz.inputs["Detail"].default_value = detail
        link(nt, tc.outputs["Object"], turn.inputs["Vector"])
        link(nt, turn.outputs["Vector"], mp.inputs["Vector"])
        link(nt, mp.outputs["Vector"], nz.inputs["Vector"])
        return nz.outputs["Fac"]

    fine = streaks(-1200, 300, along + math.pi / 2, 250, 10, 3)
    scratch = streaks(-1200, 0, along + math.pi / 2 + 0.35, 120, 4, 2)
    brush = node(nt, "ShaderNodeMath", -700, 200, operation="MULTIPLY_ADD")
    link(nt, fine, brush.inputs[0])
    brush.inputs[1].default_value = 0.7
    link(nt, scratch, brush.inputs[2])
    tone = node(nt, "ShaderNodeMapRange", -500, 300)
    tone.inputs["From Min"].default_value = 0.35
    tone.inputs["From Max"].default_value = 1.2
    tone.inputs["To Min"].default_value = 0.88
    tone.inputs["To Max"].default_value = 1.08
    link(nt, brush.outputs["Value"], tone.inputs["Value"])
    steel = node(nt, "ShaderNodeMix", -300, 400, data_type="RGBA", blend_type="MULTIPLY")
    steel.inputs["Factor"].default_value = 1.0
    steel.inputs["A"].default_value = (0.42, 0.4, 0.375, 1)
    link(nt, tone.outputs["Result"], steel.inputs["B"])
    rough = node(nt, "ShaderNodeMapRange", -500, 0)
    rough.inputs["From Min"].default_value = 0.35
    rough.inputs["From Max"].default_value = 1.2
    rough.inputs["To Min"].default_value = 0.34
    rough.inputs["To Max"].default_value = 0.2
    link(nt, brush.outputs["Value"], rough.inputs["Value"])
    color, roughness, height = steel.outputs["Result"], rough.outputs["Result"], brush.outputs["Value"]
    bump_scale = 0.00015

    if not FLAT:
        ang, length, (fu, fv) = math.radians(PLACE[name][1]), PLACE[name][2], PLACE[name][3]
        img = bpy.data.images.load(os.path.join(MAPS, name + ".png"))
        img.colorspace_settings.name = "Non-Color"
        aspect = img.size[0] / img.size[1]
        s = length / aspect if aspect > 1 else length
        # Object coords -> the drawing's: centred on the wedge's middle, turned, scaled to the image, with the
        # instrument's recognizable part there
        m1 = node(nt, "ShaderNodeMapping", -1100, -400, vector_type="POINT")
        m1.inputs["Location"].default_value = (-centre.x, -centre.y, 0)
        m2 = node(nt, "ShaderNodeMapping", -900, -400, vector_type="POINT")
        m2.inputs["Rotation"].default_value = (0, 0, -ang)
        m3 = node(nt, "ShaderNodeMapping", -700, -400, vector_type="POINT")
        m3.inputs["Scale"].default_value = (1 / (s * aspect), 1 / s, 1)
        m3.inputs["Location"].default_value = (fu, fv, 0)
        tex = node(nt, "ShaderNodeTexImage", -500, -400, image=img, extension="CLIP", interpolation="Cubic")
        link(nt, tc.outputs["Object"], m1.inputs["Vector"])
        link(nt, m1.outputs["Vector"], m2.inputs["Vector"])
        link(nt, m2.outputs["Vector"], m3.inputs["Vector"])
        link(nt, m3.outputs["Vector"], tex.inputs["Vector"])
        sep = node(nt, "ShaderNodeSeparateColor", -300, -400)
        link(nt, tex.outputs["Color"], sep.inputs["Color"])
        cut, lines = sep.outputs["Red"], sep.outputs["Green"]
        # The cut: dark patina, duller; its detail lines a little brighter than the patina
        patina = node(nt, "ShaderNodeMix", 100, 400, data_type="RGBA")
        patina.inputs["B"].default_value = (0.04, 0.037, 0.034, 1)
        fac = node(nt, "ShaderNodeMath", -100, 500, operation="MULTIPLY")
        link(nt, cut, fac.inputs[0])
        fac.inputs[1].default_value = 0.9
        link(nt, fac.outputs["Value"], patina.inputs["Factor"])
        link(nt, color, patina.inputs["A"])
        detail = node(nt, "ShaderNodeMix", 300, 400, data_type="RGBA")
        detail.inputs["B"].default_value = (0.26, 0.25, 0.235, 1)
        dl = node(nt, "ShaderNodeMath", 100, 600, operation="MULTIPLY")
        link(nt, lines, dl.inputs[0])
        link(nt, cut, dl.inputs[1])
        link(nt, dl.outputs["Value"], detail.inputs["Factor"])
        link(nt, patina.outputs["Result"], detail.inputs["A"])
        rmix = node(nt, "ShaderNodeMix", 300, 0, data_type="FLOAT")
        link(nt, cut, rmix.inputs["Factor"])
        link(nt, roughness, rmix.inputs["A"])
        rmix.inputs["B"].default_value = 0.5
        # Sunken: the brushing minus the cut, so its edge catches a thin line of light
        hgt = node(nt, "ShaderNodeMath", 300, -200, operation="MULTIPLY_ADD")
        link(nt, cut, hgt.inputs[0])
        hgt.inputs[1].default_value = -16.0
        link(nt, height, hgt.inputs[2])
        color, roughness, height = detail.outputs["Result"], rmix.outputs["Result"], hgt.outputs["Value"]
        bump_scale = 0.0003

    bump = node(nt, "ShaderNodeBump", 700, -200)
    bump.inputs["Distance"].default_value = bump_scale
    link(nt, height, bump.inputs["Height"])
    link(nt, bump.outputs["Normal"], bsdf.inputs["Normal"])
    link(nt, color, bsdf.inputs["Base Color"])
    link(nt, roughness, bsdf.inputs["Roughness"])
    return m


def add_mesh(name, verts, faces, mat=None, solid=0.0, bevel=0.0, segments=3):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    ob = bpy.data.objects.new(name, me)
    scene.collection.objects.link(ob)
    if solid:
        s = ob.modifiers.new("solid", "SOLIDIFY")
        s.thickness = solid
        s.offset = -1
    if bevel:
        b = ob.modifiers.new("bevel", "BEVEL")
        b.width = bevel
        b.segments = segments
        b.limit_method = "ANGLE"
    if mat:
        me.materials.append(mat)
    for p in me.polygons:
        p.use_smooth = True
    return ob


def annulus(name, r_in, r_out, z, mat, solid, bevel, segments=5):
    seg = 256
    v, f = [], []
    for k in range(seg):
        t = 2 * math.pi * k / seg
        v += [(math.cos(t) * r_in, math.sin(t) * r_in, z), (math.cos(t) * r_out, math.sin(t) * r_out, z)]
    for k in range(seg):
        a, b = 2 * k, 2 * ((k + 1) % seg)
        f.append([a, b, b + 1, a + 1])
    return add_mesh(name, v, f, mat, solid, bevel, segments)


def box(name, cx, cy, w, h, z, mat, bevel=0.0):
    v = [(cx - w / 2, cy - h / 2, z), (cx + w / 2, cy - h / 2, z), (cx + w / 2, cy + h / 2, z), (cx - w / 2, cy + h / 2, z)]
    return add_mesh(name, v, [[0, 1, 2, 3]], mat, solid=0.01, bevel=bevel)


# --- the blades: each wedge gets the instrument whose direction is closest to it ------------------------------------
for i in range(6):
    poly = wedge(i)
    c = sum(poly, Vector((0, 0))) / len(poly)
    a = apex(i)
    bis = (c - a).normalized()
    deg = math.degrees(math.atan2(c.y, c.x))
    name = min(PLACE, key=lambda n: abs((PLACE[n][0] - deg + 180) % 360 - 180))
    n_next = normal(i + 1)
    verts = [(p.x, p.y, LEAN * (OPENING - p.dot(n_next)) - SINK) for p in poly]
    add_mesh(f"hoja{i}", verts, [list(range(len(verts)))], blade_material(i, name, bis, a + bis * FOCUS),
             solid=0.08, bevel=0.02)

# --- the ring: polished steel with a rounded lip, a dark step inside it --------------------------------------------
ring, rb = principled("aro", (0.82, 0.8, 0.77), 0.1)
rb.inputs["Anisotropic"].default_value = 0.7
tan = ring.node_tree.nodes.new("ShaderNodeTangent")
tan.direction_type = "RADIAL"
tan.axis = "Z"
ring.node_tree.links.new(tan.outputs["Tangent"], rb.inputs["Tangent"])
annulus("aro", RING_IN, RING_OUT, 0.14, ring, solid=0.24, bevel=0.04, segments=8)
step, _ = principled("escalon", (0.07, 0.07, 0.075), 0.3)
annulus("escalon", 0.955, RING_IN + 0.005, 0.1, step, solid=0.15, bevel=0.008)

# --- the sensor in the opening -------------------------------------------------------------------------------------
dark, _ = principled("fondo", (0.004, 0.004, 0.005), 0.4, 0.0)
add_mesh("fondo", [(math.cos(2 * math.pi * k / 96) * 0.6, math.sin(2 * math.pi * k / 96) * 0.6, -0.34) for k in range(96)],
         [list(range(96))], dark)
body, _ = principled("encapsulado", (0.012, 0.012, 0.014), 0.25, 0.0)
box("encapsulado", 0, 0, 0.4, 0.4, -0.28, body, bevel=0.006)
gold, _ = principled("contactos", (1.0, 0.72, 0.32), 0.22)
for k in range(18):
    t = -0.15 + 0.3 * k / 17
    for (x, y, w, h) in ((t, 0.165, 0.007, 0.022), (t, -0.165, 0.007, 0.022), (0.165, t, 0.022, 0.007), (-0.165, t, 0.022, 0.007)):
        box("contacto", x, y, w, h, -0.267, gold)
chip = bpy.data.materials.new("chip")
chip.use_nodes = True
cn = chip.node_tree
cb = cn.nodes["Principled BSDF"]
cb.inputs["Roughness"].default_value = 0.5
cb.inputs["Coat Weight"].default_value = 0.3
cb.inputs["Metallic"].default_value = 0.0
grad = cn.nodes.new("ShaderNodeTexGradient")
gtc = cn.nodes.new("ShaderNodeTexCoord")
gmp = cn.nodes.new("ShaderNodeMapping")
gmp.inputs["Rotation"].default_value = (0, 0, math.radians(-45))
cn.links.new(gtc.outputs["Generated"], gmp.inputs["Vector"])
cn.links.new(gmp.outputs["Vector"], grad.inputs["Vector"])
ramp = cn.nodes.new("ShaderNodeValToRGB")
els = ramp.color_ramp.elements
els[0].position, els[0].color = 0.15, (0.0, 0.35, 0.12, 1)
els[1].position, els[1].color = 0.95, (0.22, 0.0, 0.42, 1)
mid = els.new(0.55)
mid.color = (0.0, 0.08, 0.6, 1)
cn.links.new(grad.outputs["Fac"], ramp.inputs["Fac"])
cn.links.new(ramp.outputs["Color"], cb.inputs["Base Color"])
cn.links.new(ramp.outputs["Color"], cb.inputs["Emission Color"])
cb.inputs["Emission Strength"].default_value = 0.35
box("chip", 0, 0, 0.27, 0.27, -0.262, chip)

# --- light and camera -------------------------------------------------------------------------------------------
world = bpy.data.worlds.new("mundo")
scene.world = world
world.use_nodes = True
wn = world.node_tree
env = wn.nodes.new("ShaderNodeTexEnvironment")
env.image = bpy.data.images.load(os.path.join(os.path.dirname(bpy.app.binary_path), f"{bpy.app.version[0]}.{bpy.app.version[1]}",
                                              "datafiles", "studiolights", "world", "studio.exr"))
mp = wn.nodes.new("ShaderNodeMapping")
mp.inputs["Rotation"].default_value = (0, 0, math.radians(135))
tcw = wn.nodes.new("ShaderNodeTexCoord")
wn.links.new(tcw.outputs["Generated"], mp.inputs["Vector"])
wn.links.new(mp.outputs["Vector"], env.inputs["Vector"])
wn.links.new(env.outputs["Color"], wn.nodes["Background"].inputs["Color"])
wn.nodes["Background"].inputs["Strength"].default_value = 0.1


def area(name, loc, energy, size, color=(1, 1, 1)):
    ld = bpy.data.lights.new(name, "AREA")
    ld.energy = energy
    ld.size = size
    ld.color = color
    ob = bpy.data.objects.new(name, ld)
    scene.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = (Vector((0, 0, 0)) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()


# A warm key from the top left, low enough to rake the brushing; a soft one above for the blades' sheen
area("clave", (-2.3, 2.3, 1.5), 420, 1.4, (1.0, 0.9, 0.78))
area("arriba", (0.4, 1.2, 3.0), 20, 2.0, (1.0, 0.96, 0.9))
area("contra", (2.6, -2.4, 0.8), 70, 1.0, (0.75, 0.85, 1.0))

cam_d = bpy.data.cameras.new("cam")
cam_d.type = "ORTHO"
cam_d.ortho_scale = 2 * RING_OUT + 0.02
cam = bpy.data.objects.new("cam", cam_d)
scene.collection.objects.link(cam)
cam.location = (0, 0, 8)
scene.camera = cam

# --- render -----------------------------------------------------------------------------------------------------
scene.render.engine = "CYCLES"
prefs = bpy.context.preferences.addons["cycles"].preferences
try:
    prefs.compute_device_type = "HIP"
    prefs.get_devices()
    for d in prefs.devices:
        d.use = True
    scene.cycles.device = "GPU"
except Exception:
    scene.cycles.device = "CPU"
scene.cycles.samples = 192
scene.cycles.use_denoising = True
scene.render.film_transparent = True
scene.render.resolution_x = scene.render.resolution_y = SIZE
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.view_settings.view_transform = "AgX"
try:
    scene.view_settings.look = "AgX - Medium High Contrast"
except Exception:
    pass
scene.render.filepath = OUT
bpy.ops.render.render(write_still=True)
print("OK", OUT)

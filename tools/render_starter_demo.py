"""Blender 4.4: deterministic six-second LifeViz logo loop, no external assets."""
import argparse
import math
import sys
from pathlib import Path
import bpy

parser = argparse.ArgumentParser()
parser.add_argument('--output', required=True)
parser.add_argument('--preview', action='store_true')
args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
out = Path(args.output).resolve()
out.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.engine = 'BLENDER_EEVEE_NEXT'
scene.render.resolution_x = 960
scene.render.resolution_y = 540
scene.render.resolution_percentage = 100
scene.render.fps = 30
scene.render.image_settings.file_format = 'PNG'
scene.render.film_transparent = False
scene.frame_start, scene.frame_end = 1, 180
scene.world = bpy.data.worlds.new('Midnight')
scene.world.use_nodes = True
scene.world.node_tree.nodes['Background'].inputs[0].default_value = (0.012, 0.018, 0.04, 1)
scene.world.node_tree.nodes['Background'].inputs[1].default_value = 0.3
scene.view_settings.view_transform = 'AgX'

def material(name, color, emission=0, metallic=0.0):
    m = bpy.data.materials.new(name)
    m.diffuse_color = (*color, 1)
    m.use_nodes = True
    p = m.node_tree.nodes.get('Principled BSDF')
    p.inputs['Base Color'].default_value = (*color, 1)
    p.inputs['Metallic'].default_value = metallic
    p.inputs['Roughness'].default_value = 0.26
    p.inputs['Emission Color'].default_value = (*color, 1)
    p.inputs['Emission Strength'].default_value = emission
    return m

teal = material('Living mint', (0.015, 0.85, 0.53), 1.0, 0.3)
violet = material('Electric violet', (0.29, 0.035, 0.8), 1.4, 0.3)
white = material('Pearlescent lettering', (0.8, 0.95, 1.0), 0.25, 0.65)
dark = material('Midnight backdrop', (0.004, 0.008, 0.019))
dark.node_tree.nodes['Principled BSDF'].inputs['Roughness'].default_value = 1.0
dark.node_tree.nodes['Principled BSDF'].inputs['Specular IOR Level'].default_value = 0.0
grid_mat = material('Quiet cell field', (0.014, 0.06, 0.065), 0.16, 0.3)

def cube(name, pos, size, mat, bevel=0.04):
    bpy.ops.mesh.primitive_cube_add(size=1, location=pos)
    o = bpy.context.object
    o.name = name
    o.dimensions = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    o.data.materials.append(mat)
    mod = o.modifiers.new('Soft cell edges', 'BEVEL')
    mod.width, mod.segments = bevel, 3
    o.modifiers.new('Weighted normals', 'WEIGHTED_NORMAL')
    return o

cube('Backdrop', (0, 0, -5), (30, 20, 0.1), dark)
logo = bpy.data.objects.new('LifeViz logo / periodic motion', None)
scene.collection.objects.link(logo)
for x, y in [(1, 2), (2, 1), (0, 0), (1, 0), (2, 0)]:
    o = cube('Glider cell', (-4.65 + x * 0.5, -0.52 + y * 0.5, 0.2), (0.43, 0.43, 0.38), teal, 0.065)
    o.parent = logo

bpy.ops.object.text_add(location=(-2.85, -0.49, 0))
word = bpy.context.object
word.name = 'LifeViz wordmark'
word.data.body = 'LIFEVIZ'
word.data.size = 1.45
word.data.space_character = 1.13
word.data.extrude = 0.11
word.data.bevel_depth = 0.018
word.data.bevel_resolution = 3
word.data.materials.append(white)
word.parent = logo

rings = []
for i, (radius, mat) in enumerate([(3.20, teal), (3.65, violet)]):
    bpy.ops.mesh.primitive_torus_add(major_radius=radius, minor_radius=0.026,
                                   major_segments=144, minor_segments=8, location=(0, 0, -0.9))
    o = bpy.context.object
    o.name = f'Orbital trace {i + 1}'
    o.data.materials.append(mat)
    rings.append(o)

cells = []
for y in range(-6, 7):
    for x in range(-11, 12):
        # Leave breathing room around the wordmark.
        if abs(y) <= 1 and abs(x) <= 9:
            continue
        o = cube('Cell field', (x * 0.60, y * 0.60, -1.8), (0.10, 0.10, 0.06), grid_mat, 0.016)
        cells.append((o, x, y))

satellites = []
for i in range(12):
    o = cube('Orbiting cell', (0, 0, 0), (0.14, 0.14, 0.14), teal if i % 2 == 0 else violet, 0.026)
    satellites.append(o)

for name, pos, color, power, size in [
    ('Cool key', (-3, 4, 7), (0.5, 0.85, 1), 1600, 7),
    ('Violet rim', (5, -1, 4), (0.55, 0.2, 1), 1100, 5),
]:
    bpy.ops.object.light_add(type='AREA', location=pos)
    lamp = bpy.context.object
    lamp.name = name
    lamp.data.energy, lamp.data.color, lamp.data.shape, lamp.data.size = power, color, 'DISK', size

bpy.ops.object.camera_add(location=(0, 0, 18))
camera = bpy.context.object
camera.data.type, camera.data.ortho_scale = 'ORTHO', 13.5
scene.camera = camera

scene.use_nodes = True
nodes = scene.node_tree.nodes
nodes.clear()
render = nodes.new('CompositorNodeRLayers')
glow = nodes.new('CompositorNodeGlare')
glow.glare_type, glow.quality, glow.threshold = 'FOG_GLOW', 'HIGH', 1.4
output = nodes.new('CompositorNodeComposite')
scene.node_tree.links.new(render.outputs['Image'], glow.inputs['Image'])
scene.node_tree.links.new(glow.outputs['Image'], output.inputs['Image'])

# All animation is an integer harmonic of one cycle. Frame 181 repeats frame 1;
# encode only 1..180 so the endpoint is not held twice at the loop seam.
for frame in range(1, 182):
    t = 2 * math.pi * ((frame - 1) % 180) / 180
    logo.location.y = 0.13 * math.sin(t)
    logo.rotation_euler = (0.06 * math.sin(t), 0.10 * math.sin(t), 0.015 * math.cos(t))
    logo.keyframe_insert('location', frame=frame)
    logo.keyframe_insert('rotation_euler', frame=frame)
    for i, ring in enumerate(rings):
        ring.rotation_euler = (0.7 + 0.22 * math.sin(t + i * math.pi),
                               0.26 * math.cos(t + i), 0.65 * math.sin(t + i))
        ring.keyframe_insert('rotation_euler', frame=frame)
    for o, x, y in cells:
        pulse = 0.6 + 0.4 * math.sin(t + x * 0.48 + y * 0.65)
        o.scale = (pulse, pulse, 1)
        o.keyframe_insert('scale', frame=frame)
    for i, o in enumerate(satellites):
        a = t + i * math.tau / len(satellites)
        o.location = (5.55 * math.cos(a), 2.95 * math.sin(a), -0.7 + 0.35 * math.sin(2 * a))
        o.rotation_euler = (0.5 * math.sin(a), 0.5 * math.cos(a), 0.7 * math.sin(2 * a))
        o.keyframe_insert('location', frame=frame)
        o.keyframe_insert('rotation_euler', frame=frame)

for action in bpy.data.actions:
    for curve in action.fcurves:
        for key in curve.keyframe_points:
            key.interpolation = 'LINEAR'
scene.frame_set(1)
bpy.ops.wm.save_as_mainfile(filepath=str(out / 'lifeviz-starter.blend'))
if args.preview:
    for frame in [1, 46, 91, 136, 181]:
        scene.frame_set(frame)
        scene.render.filepath = str(out / f'preview-{frame:04d}.png')
        bpy.ops.render.render(write_still=True)
else:
    (out / 'frames').mkdir(exist_ok=True)
    scene.render.filepath = str(out / 'frames' / '') + '/'
    bpy.ops.render.render(animation=True)
    scene.frame_set(181)
    scene.render.filepath = str(out / 'loop-endpoint.png')
    bpy.ops.render.render(write_still=True)

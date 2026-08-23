"""
Coffee right-middle-finger refinement for a baked Blender animation.

Run this script in Blender after the MediaPipe/JSON motion has already been
applied and baked to the character rig.  It keeps the character's right index
finger straight in the global XOY plane and the middle finger straight in the
global negative Z direction. It also rotates the root phalanges of the right
ring and little fingers down toward global negative Z. The wrist and arms are
intentionally left unchanged.
"""

import bpy
import math
from pathlib import Path
from mathutils import Matrix, Vector


MIDDLE_FINGER_ROOT = "DEF-f_middle.01.R"
MIDDLE_FINGER_CHILDREN = ("DEF-f_middle.02.R", "DEF-f_middle.03.R")
INDEX_FINGER_ROOT = "DEF-f_index.01.R"
INDEX_FINGER_CHILDREN = ("DEF-f_index.02.R", "DEF-f_index.03.R")
RING_FINGER_BASE = "DEF-f_ring.01.R"
PINKY_FINGER_BASE = "DEF-f_pinky.01.R"
TARGET_WORLD_DIRECTION = Vector((0.0, 0.0, -1.0))
OUTPUT_FILE_NAME = "CoffeeRingPinkyBase.blend"


def find_rig():
    """Return the Rigify armature that actually drives the character mesh."""
    rig = bpy.data.objects.get("rig")
    if rig is not None and rig.type == "ARMATURE" and rig.pose.bones.get(MIDDLE_FINGER_ROOT):
        return rig

    for item in bpy.context.scene.objects:
        if item.type == "ARMATURE" and item.pose.bones.get(MIDDLE_FINGER_ROOT):
            return item
    raise RuntimeError("The character rig with DEF finger bones was not found.")


def motion_frame_range(rig):
    """Use the active action range when available, otherwise the scene range."""
    action = rig.animation_data.action if rig.animation_data else None
    if action is not None:
        start, end = action.frame_range
        return int(math.floor(start)), int(math.ceil(end))
    scene = bpy.context.scene
    return scene.frame_start, scene.frame_end


def prepare_finger_bones(rig):
    """Make only the target right-hand finger bones keyframeable."""
    prepared = {}
    for bone_name in (
        INDEX_FINGER_ROOT,
        *INDEX_FINGER_CHILDREN,
        MIDDLE_FINGER_ROOT,
        *MIDDLE_FINGER_CHILDREN,
        RING_FINGER_BASE,
        PINKY_FINGER_BASE,
    ):
        bone = rig.pose.bones.get(bone_name)
        if bone is None:
            raise RuntimeError("Missing finger bone: " + bone_name)
        for constraint in bone.constraints:
            constraint.mute = True
        bone.lock_rotation = (False, False, False)
        bone.rotation_mode = "XYZ"
        prepared[bone_name] = bone
    return prepared


def align_bone_y_axis_to_world_direction(bone, world_direction):
    """Rotate a pose bone in world space while keeping its root in place."""
    current_matrix = bone.matrix.copy()
    current_direction = current_matrix.to_3x3() @ Vector((0.0, 1.0, 0.0))
    if current_direction.length == 0.0:
        raise RuntimeError("Cannot determine direction for " + bone.name)
    correction = current_direction.normalized().rotation_difference(world_direction)
    pivot = Matrix.Translation(current_matrix.translation)
    bone.matrix = pivot @ correction.to_matrix().to_4x4() @ pivot.inverted() @ current_matrix


def horizontal_direction_from_current_bone(bone):
    """Keep the current horizontal heading but remove its vertical component."""
    current_direction = bone.matrix.to_3x3() @ Vector((0.0, 1.0, 0.0))
    horizontal_direction = Vector((current_direction.x, current_direction.y, 0.0))
    if horizontal_direction.length < 0.0001:
        return Vector((1.0, 0.0, 0.0))
    return horizontal_direction.normalized()


def apply_coffee_hand_refinement(rig, frame_start, frame_end):
    """Apply the requested right-hand index, middle, ring and pinky corrections."""
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    bones = prepare_finger_bones(rig)
    index_root = bones[INDEX_FINGER_ROOT]
    middle_root = bones[MIDDLE_FINGER_ROOT]
    ring_base = bones[RING_FINGER_BASE]
    pinky_base = bones[PINKY_FINGER_BASE]
    child_bones = [bones[name] for name in INDEX_FINGER_CHILDREN + MIDDLE_FINGER_CHILDREN]

    for frame_index in range(frame_start, frame_end + 1):
        bpy.context.scene.frame_set(frame_index)
        for bone in child_bones:
            bone.rotation_euler = (0.0, 0.0, 0.0)
            bone.keyframe_insert(data_path="rotation_euler", frame=frame_index)

        align_bone_y_axis_to_world_direction(index_root, horizontal_direction_from_current_bone(index_root))
        align_bone_y_axis_to_world_direction(middle_root, TARGET_WORLD_DIRECTION)
        align_bone_y_axis_to_world_direction(ring_base, TARGET_WORLD_DIRECTION)
        align_bone_y_axis_to_world_direction(pinky_base, TARGET_WORLD_DIRECTION)
        index_root.keyframe_insert(data_path="rotation_euler", frame=frame_index)
        middle_root.keyframe_insert(data_path="rotation_euler", frame=frame_index)
        ring_base.keyframe_insert(data_path="rotation_euler", frame=frame_index)
        pinky_base.keyframe_insert(data_path="rotation_euler", frame=frame_index)

    bpy.context.view_layer.update()
    bpy.ops.object.mode_set(mode="OBJECT")


rig = find_rig()
frame_start, frame_end = motion_frame_range(rig)
apply_coffee_hand_refinement(rig, frame_start, frame_end)

# Save a separate named copy for review.  The original source asset is never
# overwritten by this first refinement step.
current_directory = Path(bpy.path.abspath("//"))
output_path = current_directory / OUTPUT_FILE_NAME
bpy.context.scene.frame_set(frame_start)
bpy.ops.wm.save_as_mainfile(filepath=str(output_path))
print("Coffee hand refinement saved to: " + str(output_path))

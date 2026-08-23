"""Coffee hand-shape and elbow-height refinement for a baked animation.

Run this after the MediaPipe/JSON motion has already been applied and baked to
the Rigify character rig. It corrects the right-hand finger pose, then lowers
both elbow regions while preserving each wrist's evaluated world height.
No wrist rotation is written by this script.
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
# The evaluated mesh is driven by this DEF chain rather than by the visible
# Rigify tweak controls. The first upper-arm segment remains unchanged; the
# second segment supplies the lowered elbow, while the forearm is rebuilt to
# meet the original wrist endpoint.
ARM_CHAINS = {
    "L": (
        "DEF-upper_arm.L.001",
        "DEF-forearm.L",
        "DEF-forearm.L.001",
        "DEF-hand.L",
    ),
    "R": (
        "DEF-upper_arm.R.001",
        "DEF-forearm.R",
        "DEF-forearm.R.001",
        "DEF-hand.R",
    ),
}
ELBOW_WORLD_OFFSET = Vector((0.0, 0.0, -0.04))
OUTPUT_FILE_NAME = "CoffeeElbowHeight.blend"


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


def prepare_arm_deform_bones(rig):
    """Release only the direct arm deformation chain for manual keyframing."""
    prepared = {}
    for side, bone_names in ARM_CHAINS.items():
        bones = []
        for bone_name in bone_names:
            bone = rig.pose.bones.get(bone_name)
            if bone is None:
                raise RuntimeError("Missing arm deform bone: " + bone_name)
            for constraint in bone.constraints:
                constraint.mute = True
            bone.lock_rotation = (False, False, False)
            bone.lock_scale = (False, False, False)
            bone.rotation_mode = "XYZ"
            bones.append(bone)
        prepared[side] = bones
    return prepared


def point_bone_tail_at(bone, target_point):
    """Aim a bone's local Y axis at a point and match its effective length."""
    # Parent-scale inheritance makes a one-shot local scale estimate slightly
    # inaccurate. Iterate on the evaluated tail so the world-space endpoint is
    # aligned precisely, while X/Z scale remain unchanged.
    for _ in range(6):
        head = bone.head.copy()
        target_vector = target_point - head
        if target_vector.length < 0.00001:
            raise RuntimeError("Cannot aim zero-length bone: " + bone.name)

        align_bone_y_axis_to_world_direction(bone, target_vector.normalized())
        bpy.context.view_layer.update()
        current_length = (bone.tail - bone.head).length
        if current_length < 0.00001:
            raise RuntimeError("Invalid evaluated length for bone: " + bone.name)
        bone.scale.y *= target_vector.length / current_length
        bpy.context.view_layer.update()

        if (bone.tail - target_point).length < 0.00001:
            break


def insert_arm_transform_keys(bone, frame_index):
    bone.keyframe_insert(data_path="location", frame=frame_index)
    bone.keyframe_insert(data_path="rotation_euler", frame=frame_index)
    bone.keyframe_insert(data_path="scale", frame=frame_index)


def sample_arm_targets(rig, frame_start, frame_end):
    """Capture constrained elbow and wrist poses before releasing the chain."""
    samples = {}
    for frame_index in range(frame_start, frame_end + 1):
        bpy.context.scene.frame_set(frame_index)
        frame_samples = {}
        for side, bone_names in ARM_CHAINS.items():
            upper_end, forearm_a, forearm_b, hand = (
                rig.pose.bones[name] for name in bone_names
            )
            frame_samples[side] = {
                "elbow": upper_end.tail.copy(),
                "wrist": hand.head.copy(),
                "hand_matrix": hand.matrix.copy(),
            }
        samples[frame_index] = frame_samples
    return samples


def apply_elbow_height_refinement(rig, frame_start, frame_end):
    """Lower elbows while preserving the evaluated world wrist endpoints."""
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    samples = sample_arm_targets(rig, frame_start, frame_end)
    arms = prepare_arm_deform_bones(rig)

    for frame_index in range(frame_start, frame_end + 1):
        bpy.context.scene.frame_set(frame_index)
        for side, (upper_end, forearm_a, forearm_b, hand) in arms.items():
            sample = samples[frame_index][side]
            lowered_elbow = sample["elbow"] + ELBOW_WORLD_OFFSET

            point_bone_tail_at(upper_end, lowered_elbow)
            insert_arm_transform_keys(upper_end, frame_index)
            bpy.context.view_layer.update()

            # The two forearm segments span from the lowered elbow to the
            # sampled wrist point. This keeps the wrist position unchanged.
            wrist_point = sample["wrist"]
            forearm_vector = wrist_point - forearm_a.head
            if forearm_vector.length < 0.00001:
                raise RuntimeError("Invalid forearm vector for side " + side)
            total_rest_length = forearm_a.bone.length + forearm_b.bone.length
            scale = forearm_vector.length / total_rest_length

            first_forearm_end = (
                forearm_a.head
                + forearm_vector.normalized() * forearm_a.bone.length * scale
            )
            point_bone_tail_at(forearm_a, first_forearm_end)
            insert_arm_transform_keys(forearm_a, frame_index)
            bpy.context.view_layer.update()

            point_bone_tail_at(forearm_b, wrist_point)
            insert_arm_transform_keys(forearm_b, frame_index)
            bpy.context.view_layer.update()

            # Preserve the original wrist orientation. Its connected head is
            # already at wrist_point, so this does not move the wrist.
            hand.matrix = sample["hand_matrix"]
            insert_arm_transform_keys(hand, frame_index)
            bpy.context.view_layer.update()

    bpy.context.view_layer.update()
    bpy.ops.object.mode_set(mode="OBJECT")


rig = find_rig()
frame_start, frame_end = motion_frame_range(rig)
apply_coffee_hand_refinement(rig, frame_start, frame_end)
apply_elbow_height_refinement(rig, frame_start, frame_end)

# Save a separate named copy for review. The input source asset is never
# overwritten.
current_directory = Path(bpy.path.abspath("//"))
output_path = current_directory / OUTPUT_FILE_NAME
bpy.context.scene.frame_set(frame_start)
bpy.ops.wm.save_as_mainfile(filepath=str(output_path))
print("Coffee hand refinement saved to: " + str(output_path))

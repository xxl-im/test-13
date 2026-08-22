"""
Coffee hand-pose refinement for a baked Blender animation.

Run this script in Blender after the MediaPipe/JSON motion has already been
applied and baked to the character rig.  It changes only the right-hand finger
bones: index finger and thumb stay straight; middle, ring and little fingers
curl toward the palm.  The right wrist (DEF-hand.R) is intentionally untouched.
"""

import bpy
import math
from pathlib import Path


# Change only these values when refining the coffee hand shape.
RIGHT_FINGER_CURL_DEGREES = {
    "DEF-f_index.01.R": 0,
    "DEF-f_index.02.R": 0,
    "DEF-f_index.03.R": 0,
    "DEF-thumb.01.R": 0,
    "DEF-thumb.02.R": 0,
    "DEF-thumb.03.R": 0,
    "DEF-f_middle.01.R": 28,
    "DEF-f_middle.02.R": 38,
    "DEF-f_middle.03.R": 28,
    "DEF-f_ring.01.R": 30,
    "DEF-f_ring.02.R": 40,
    "DEF-f_ring.03.R": 30,
    "DEF-f_pinky.01.R": 32,
    "DEF-f_pinky.02.R": 42,
    "DEF-f_pinky.03.R": 32,
}

OUTPUT_FILE_NAME = "CoffeeAnimation.blend"


def find_rig():
    """Find the first armature in the active Blender file."""
    for item in bpy.context.scene.objects:
        if item.type == "ARMATURE":
            return item
    raise RuntimeError("No armature was found in the active Blender file.")


def motion_frame_range(rig):
    """Use the active action range when available, otherwise the scene range."""
    action = rig.animation_data.action if rig.animation_data else None
    if action is not None:
        start, end = action.frame_range
        return int(math.floor(start)), int(math.ceil(end))
    scene = bpy.context.scene
    return scene.frame_start, scene.frame_end


def gesture_weight(frame_index, frame_start, frame_end):
    """Keep the neutral pose at both ends and reach full curl mid-animation."""
    if frame_end <= frame_start:
        return 1.0
    progress = (frame_index - frame_start) / (frame_end - frame_start)
    progress = max(0.0, min(1.0, progress))
    return math.sin(math.pi * progress)


def prepare_finger_bones(rig):
    """Make only the target DEF finger bones available for direct keyframing."""
    prepared = []
    for bone_name, degrees in RIGHT_FINGER_CURL_DEGREES.items():
        bone = rig.pose.bones.get(bone_name)
        if bone is None:
            print("Missing finger bone: " + bone_name)
            continue
        for constraint in bone.constraints:
            constraint.mute = True
        bone.lock_rotation = (False, False, False)
        bone.rotation_mode = "XYZ"
        prepared.append((bone, degrees))
    return prepared


def apply_coffee_hand_refinement(rig, frame_start, frame_end):
    """Keyframe the coffee hand pose without touching DEF-hand.R or arm bones."""
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    fingers = prepare_finger_bones(rig)

    for frame_index in range(frame_start, frame_end + 1):
        bpy.context.scene.frame_set(frame_index)
        strength = gesture_weight(frame_index, frame_start, frame_end)
        for bone, degrees in fingers:
            bone.rotation_euler = (math.radians(degrees * strength), 0.0, 0.0)
            bone.keyframe_insert(data_path="rotation_euler", frame=frame_index)

    bpy.context.view_layer.update()
    bpy.ops.object.mode_set(mode="OBJECT")


rig = find_rig()
frame_start, frame_end = motion_frame_range(rig)
apply_coffee_hand_refinement(rig, frame_start, frame_end)

# Save a separate named copy for export.  The originally opened source file is
# never overwritten when it has a different name.
current_directory = Path(bpy.path.abspath("//"))
output_path = current_directory / OUTPUT_FILE_NAME
bpy.context.scene.frame_set(frame_start)
bpy.ops.wm.save_as_mainfile(filepath=str(output_path))
print("Coffee hand refinement saved to: " + str(output_path))

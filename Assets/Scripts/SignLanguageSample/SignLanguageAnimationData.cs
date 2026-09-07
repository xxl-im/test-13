// ============================================================
// JSON data contracts for the auto-generated SignLanguagePlayer_sample_*.cs
// players (Unity MediaPipe export format, see sample_*_unity_anim.json).
// The generator emits the player scripts but not these data types,
// so they are defined here once and shared by all sample scripts.
// ============================================================

using System;

[Serializable]
public class UnityAnimationData
{
    public string sample_id;
    public string word;
    public float fps;
    public int total_frames;
    public BoneRegistryEntry[] bone_registry;
    public FrameData[] frames;
}

[Serializable]
public class BoneRegistryEntry
{
    public string bone_name;
    public string description;
}

[Serializable]
public class FrameData
{
    public int frame_index;
    public float timestamp;
    public bool is_keyframe;
    public BonePoseData[] bone_poses;
    public BlendShapeData[] blendshapes;
}

[Serializable]
public class BonePoseData
{
    public string bone_name;
    public float[] local_position;
    public float[] local_rotation;
}

[Serializable]
public class BlendShapeData
{
    public string name;
    public float weight;
}

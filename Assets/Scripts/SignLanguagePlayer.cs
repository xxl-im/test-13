using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads a sign-language animation JSON (MediaPipe pose export, Unity format)
/// and drives the character's metarig at runtime.
///
/// JSON format:
/// - local_rotation: [x, y, z, w] (Unity quaternion, already correct handedness)
/// - local_position: [x, y, z] (Unity coordinates)
/// - Bone names use HumanBodyBones naming, mapped to Blender metarig bones
/// </summary>
public class SignLanguagePlayer : MonoBehaviour
{
    [Header("Animation Data")]
    [Tooltip("Drag a .json asset here (from Project window).")]
    public UnityEngine.Object jsonFile;

    [Header("Playback")]
    public bool playOnStart = true;
    public bool loop = true;

    [Header("Finger Constraints")]
    [Tooltip("Max finger curl angle in degrees (prevents over-bending). 0 = disabled.")]
    [Range(0, 180)]
    public float fingerMaxFlex = 100f;

    [Tooltip("Allow backward finger bend this many degrees past straight. 0 = no backward bend.")]
    [Range(0, 90)]
    public float fingerMaxExtension = 15f;

    [Header("Position")]
    [Tooltip("Scale for position deltas from JSON local_position data. 0 = ignore position data.")]
    public float positionScale = 0f;

    // Parsed data
    private AnimData _animData;
    private float _playbackTime;
    private bool _isPlaying;

    // Bone lookup: JSON bone name -> resolved Transform
    private readonly Dictionary<string, Transform> _boneMap = new Dictionary<string, Transform>();

    // Unity rest pose (for reset)
    private readonly Dictionary<Transform, Quaternion> _initialLocalRotations = new Dictionary<Transform, Quaternion>();
    private readonly Dictionary<Transform, Vector3> _initialLocalPositions = new Dictionary<Transform, Vector3>();

    // HumanBodyBones name -> Rigify DEF- bone name (mesh is skinned to rig, not metarig)
    private static readonly Dictionary<string, string> BoneNameRemap = new Dictionary<string, string>
    {
        { "Hips",            "DEF-spine.004" },
        { "Spine",           "DEF-spine.004" },
        { "Chest",           "DEF-spine.005" },
        { "UpperChest",      "DEF-spine.005" },
        { "Neck",            "DEF-spine.006" },
        { "Head",            "DEF-spine.006" },
        { "LeftUpperArm",    "DEF-upper_arm.L" },
        { "LeftLowerArm",    "DEF-forearm.L" },
        { "LeftHand",        "DEF-hand.L" },
        { "RightUpperArm",   "DEF-upper_arm.R" },
        { "RightLowerArm",   "DEF-forearm.R" },
        { "RightHand",       "DEF-hand.R" },
        { "LeftThumbProximal",      "DEF-thumb.01.L" },
        { "LeftThumbIntermediate",  "DEF-thumb.02.L" },
        { "LeftIndexProximal",      "DEF-f_index.01.L" },
        { "LeftIndexIntermediate",  "DEF-f_index.02.L" },
        { "LeftMiddleProximal",     "DEF-f_middle.01.L" },
        { "LeftMiddleIntermediate", "DEF-f_middle.02.L" },
        { "LeftRingProximal",       "DEF-f_ring.01.L" },
        { "LeftRingIntermediate",   "DEF-f_ring.02.L" },
        { "LeftLittleProximal",     "DEF-f_pinky.01.L" },
        { "LeftLittleIntermediate", "DEF-f_pinky.02.L" },
        { "RightThumbProximal",      "DEF-thumb.01.R" },
        { "RightThumbIntermediate",  "DEF-thumb.02.R" },
        { "RightIndexProximal",      "DEF-f_index.01.R" },
        { "RightIndexIntermediate",  "DEF-f_index.02.R" },
        { "RightMiddleProximal",     "DEF-f_middle.01.R" },
        { "RightMiddleIntermediate", "DEF-f_middle.02.R" },
        { "RightRingProximal",       "DEF-f_ring.01.R" },
        { "RightRingIntermediate",   "DEF-f_ring.02.R" },
        { "RightLittleProximal",     "DEF-f_pinky.01.R" },
        { "RightLittleIntermediate", "DEF-f_pinky.02.R" },
    };

    // Bones that need axis correction (arm bones with ~90° Y rest rotation)
    private static readonly HashSet<string> AxisCorrectedBones = new HashSet<string>
    {
        "LeftUpperArm", "LeftLowerArm", "LeftHand",
        "RightUpperArm", "RightLowerArm", "RightHand",
    };

    // JSON frame-0 baseline rotations (for delta computation)
    private readonly Dictionary<string, Quaternion> _jsonFrame0Rotations = new Dictionary<string, Quaternion>();

    // JSON frame-0 baseline positions (for delta computation)
    private readonly Dictionary<string, Vector3> _jsonFrame0Positions = new Dictionary<string, Vector3>();

    #region JSON Data Structures

    [System.Serializable]
    private class BonePose
    {
        public string bone_name;
        public float[] local_position;
        public float[] local_rotation;
    }

    [System.Serializable]
    private class Frame
    {
        public int frame_index;
        public float timestamp;
        public bool is_keyframe;
        public BonePose[] bone_poses;
    }

    [System.Serializable]
    private class AnimData
    {
        public string sample_id;
        public string word;
        public float fps;
        public int total_frames;
        public Frame[] frames;
    }

    #endregion

    void Start()
    {
        LoadAndBuild();
        RecordInitialState();
        if (playOnStart) _isPlaying = true;
    }

    /// <summary>
    /// Opens a file picker dialog (Editor only) to select a JSON animation file.
    /// Loads the file text and reloads the animation.
    /// </summary>
    [ContextMenu("Pick JSON File")]
    public void PickJsonFile()
    {
#if UNITY_EDITOR
        string path = UnityEditor.EditorUtility.OpenFilePanel(
            "Select Animation JSON", Application.streamingAssetsPath, "json");
        if (!string.IsNullOrEmpty(path))
        {
            LoadFromFile(path);
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[SignLanguagePlayer] Loaded JSON from: {path}");
        }
#endif
    }

    /// <summary>
    /// Loads animation data from an absolute path at runtime.
    /// Resets bones to their initial state before reloading.
    /// </summary>
    public void LoadFromFile(string absolutePath)
    {
        // Stop playback and reset bones to recorded initial state
        _isPlaying = false;
        _playbackTime = 0f;

        foreach (var kvp in _initialLocalRotations)
            if (kvp.Key != null) kvp.Key.localRotation = kvp.Value;
        foreach (var kvp in _initialLocalPositions)
            if (kvp.Key != null) kvp.Key.localPosition = kvp.Value;

        _initialLocalRotations.Clear();
        _initialLocalPositions.Clear();
        _boneMap.Clear();
        _jsonFrame0Rotations.Clear();
        _jsonFrame0Positions.Clear();

        LoadAndBuild(absolutePath);
        RecordInitialState();
    }

    void Update()
    {
        if (!_isPlaying || _animData == null) return;

        _playbackTime += Time.deltaTime;

        float animDuration = _animData.frames[_animData.frames.Length - 1].timestamp;
        if (_playbackTime >= animDuration)
        {
            if (loop)
                _playbackTime = Mathf.Repeat(_playbackTime, animDuration);
            else
            {
                _playbackTime = animDuration;
                _isPlaying = false;
            }
        }

        ApplyPoseAtTime(_playbackTime);
    }

    public void Play() { _isPlaying = true; }
    public void Pause() { _isPlaying = false; }
    public void Stop() { _isPlaying = false; _playbackTime = 0; }
    public bool IsPlaying => _isPlaying;

    private void LoadAndBuild(string overridePath = null)
    {
        string jsonText;

        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            jsonText = File.ReadAllText(overridePath);
        }
        else if (jsonFile != null)
        {
#if UNITY_EDITOR
            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(jsonFile);
            if (!string.IsNullOrEmpty(assetPath))
            {
                jsonText = File.ReadAllText(assetPath);
            }
            else
            {
                Debug.LogError("[SignLanguagePlayer] Cannot resolve path for jsonFile.");
                return;
            }
#else
            Debug.LogError("[SignLanguagePlayer] jsonFile reference only works in Editor.");
            return;
#endif
        }
        else
        {
            Debug.LogError("[SignLanguagePlayer] No JSON file assigned.");
            return;
        }

        _animData = JsonUtility.FromJson<AnimData>(jsonText);

        if (_animData == null || _animData.frames == null || _animData.frames.Length == 0)
        {
            Debug.LogError("[SignLanguagePlayer] Failed to parse JSON or no frames found.");
            return;
        }

        Debug.Log($"[SignLanguagePlayer] Loaded: {_animData.word} | FPS: {_animData.fps} | Frames: {_animData.total_frames}");

        BuildBoneMap();
        RecordJsonFrame0();

        int matched = 0, missing = 0;
        var missingNames = new List<string>();
        var allBoneNames = new HashSet<string>();
        foreach (var frame in _animData.frames)
        {
            if (frame?.bone_poses == null) continue;
            foreach (var bp in frame.bone_poses)
            {
                if (!string.IsNullOrEmpty(bp.bone_name))
                    allBoneNames.Add(bp.bone_name);
            }
        }
        foreach (var name in allBoneNames)
        {
            if (_boneMap.ContainsKey(name)) matched++;
            else { missing++; missingNames.Add(name); }
        }
        Debug.Log($"[SignLanguagePlayer] Bones matched: {matched}, missing: {missing}");
        if (missing > 0)
            Debug.LogWarning($"[SignLanguagePlayer] Missing bones: {string.Join(", ", missingNames)}");
    }

    private void RecordInitialState()
    {
        var recorded = new HashSet<Transform>();
        foreach (var kvp in _boneMap)
        {
            var bone = kvp.Value;
            if (bone == null || recorded.Contains(bone)) continue;
            _initialLocalRotations[bone] = bone.localRotation;
            _initialLocalPositions[bone] = bone.localPosition;
            recorded.Add(bone);
        }
        Debug.Log($"[SignLanguagePlayer] Recorded initial state for {recorded.Count} bones");
    }

    private void RecordJsonFrame0()
    {
        _jsonFrame0Rotations.Clear();
        _jsonFrame0Positions.Clear();
        if (_animData?.frames == null || _animData.frames.Length == 0) return;

        foreach (var bp in _animData.frames[0].bone_poses)
        {
            if (string.IsNullOrEmpty(bp.bone_name)) continue;
            if (bp.local_rotation != null && bp.local_rotation.Length >= 4)
                _jsonFrame0Rotations[bp.bone_name] = new Quaternion(
                    bp.local_rotation[0], bp.local_rotation[1],
                    bp.local_rotation[2], bp.local_rotation[3]);
            if (bp.local_position != null && bp.local_position.Length >= 3)
                _jsonFrame0Positions[bp.bone_name] = new Vector3(
                    bp.local_position[0], bp.local_position[1],
                    bp.local_position[2]);
        }
        Debug.Log($"[SignLanguagePlayer] Recorded JSON frame-0 baseline: {_jsonFrame0Rotations.Count} rotations, {_jsonFrame0Positions.Count} positions");
    }

    private void BuildBoneMap()
    {
        var allTransforms = GetComponentsInChildren<Transform>(true);
        var nameToTransform = new Dictionary<string, Transform>();
        foreach (var t in allTransforms)
        {
            if (!nameToTransform.ContainsKey(t.name))
                nameToTransform[t.name] = t;
        }

        var jsonBoneNames = new HashSet<string>();
        foreach (var frame in _animData.frames)
        {
            if (frame?.bone_poses == null) continue;
            foreach (var bp in frame.bone_poses)
            {
                if (!string.IsNullOrEmpty(bp.bone_name))
                    jsonBoneNames.Add(bp.bone_name);
            }
        }

        foreach (var jsonName in jsonBoneNames)
        {
            if (BoneNameRemap.TryGetValue(jsonName, out var rigName))
            {
                if (nameToTransform.TryGetValue(rigName, out var t))
                    _boneMap[jsonName] = t;
            }
        }
    }

    private void ApplyPoseAtTime(float time)
    {
        var frames = _animData.frames;
        if (frames == null || frames.Length == 0) return;

        int i = 0;
        while (i < frames.Length - 1 && frames[i + 1].timestamp <= time)
            i++;

        Frame f0 = frames[i];
        Frame f1 = i < frames.Length - 1 ? frames[i + 1] : f0;

        float t = 0f;
        float span = f1.timestamp - f0.timestamp;
        if (span > 0.0001f)
            t = Mathf.Clamp01((time - f0.timestamp) / span);

        ApplyInterpolatedFrame(f0, f1, t);
    }

    private void ApplyInterpolatedFrame(Frame f0, Frame f1, float t)
    {
        var f1Lookup = new Dictionary<string, BonePose>();
        if (f1?.bone_poses != null)
        {
            foreach (var bp in f1.bone_poses)
            {
                if (!string.IsNullOrEmpty(bp.bone_name))
                    f1Lookup[bp.bone_name] = bp;
            }
        }

        if (f0?.bone_poses == null) return;

        var writtenBones = new HashSet<Transform>();

        foreach (var bp0 in f0.bone_poses)
        {
            if (string.IsNullOrEmpty(bp0.bone_name)) continue;
            if (!_boneMap.TryGetValue(bp0.bone_name, out var bone)) continue;
            if (writtenBones.Contains(bone)) continue;

            f1Lookup.TryGetValue(bp0.bone_name, out var bp1);
            bp1 = bp1 ?? bp0;

            // Apply rotation using delta-from-frame0 approach:
            //   delta = Inv(jsonFrame0Rot) * jsonRot
            //   finalRot = restRot * axisCorrection * delta
            //
            // This extracts only the animation motion (relative to frame 0) and
            // composes it with the DEF bone's rest pose. The axisCorrection
            // converts the delta from standard Humanoid axes to DEF- bone axes.
            if (bp0.local_rotation != null && bp0.local_rotation.Length >= 4)
            {
                var q0 = new Quaternion(bp0.local_rotation[0], bp0.local_rotation[1],
                                         bp0.local_rotation[2], bp0.local_rotation[3]);
                var q1 = bp1.local_rotation != null
                    ? new Quaternion(bp1.local_rotation[0], bp1.local_rotation[1],
                                     bp1.local_rotation[2], bp1.local_rotation[3])
                    : q0;

                var jsonRot = Quaternion.Slerp(q0, q1, t);

                // Compute delta from frame 0
                Quaternion delta = jsonRot;
                if (_jsonFrame0Rotations.TryGetValue(bp0.bone_name, out var frame0Rot))
                    delta = Quaternion.Inverse(frame0Rot) * jsonRot;

                // Apply axis correction for ARM bones ONLY (upper arm / forearm / hand).
                // NOTE: Hand-bone names match "RightHand"/"LeftHand" exactly. Finger bones
                // (e.g. "RightThumbProximal") MUST NOT be remapped - their DEF- bones have
                // ~identity rest orientation matching standard Humanoid axes.
                bool isArmBone = bp0.bone_name == "LeftUpperArm"  || bp0.bone_name == "LeftLowerArm" || bp0.bone_name == "LeftHand"
                              || bp0.bone_name == "RightUpperArm" || bp0.bone_name == "RightLowerArm" || bp0.bone_name == "RightHand";

                // Arm bones: delta is in Unity world space (no Python x/z swap)
                // Applied as delta * restRot so the world-space delta composes correctly
                // with the bone's rest pose
                if (IsFingerBone(bp0.bone_name))
                {
                    // Clamp finger rotation: prevent over-bending and backward bending.
                    // The finger flexes around its local Z axis (pointing sideways when
                    // the hand is at rest). We constrain that axis so fingers can only
                    // curl toward the palm within a max angle.
                    delta = clampFingerRotation(delta, bp0.bone_name);
                }

                // Compose with rest pose (delta * restRot for world-space delta)
                if (_initialLocalRotations.TryGetValue(bone, out var restRot))
                    bone.localRotation = delta * restRot;
                else
                    bone.localRotation = delta;
            }

            // Apply position delta (if enabled and data exists)
            if (positionScale > 0f && bp0.local_position != null && bp0.local_position.Length >= 3
                && _initialLocalPositions.TryGetValue(bone, out var restPos))
            {
                var p0 = new Vector3(bp0.local_position[0], bp0.local_position[1], bp0.local_position[2]);
                var p1 = bp1.local_position != null
                    ? new Vector3(bp1.local_position[0], bp1.local_position[1], bp1.local_position[2])
                    : p0;
                var jsonPos = Vector3.Lerp(p0, p1, t);

                if (_jsonFrame0Positions.TryGetValue(bp0.bone_name, out var frame0Pos))
                {
                    var delta = jsonPos - frame0Pos;
                    bone.localPosition = restPos + delta * positionScale;
                }
            }

            writtenBones.Add(bone);
        }
    }

    // Finger bones: all bones whose name contains "Thumb", "Index", "Middle", "Ring", or "Little"
    private static bool IsFingerBone(string boneName)
    {
        return boneName.Contains("Thumb") || boneName.Contains("Index")
            || boneName.Contains("Middle") || boneName.Contains("Ring") || boneName.Contains("Little");
    }

    /// <summary>
    /// Clamp a finger joint's delta rotation to prevent over-bending and backward bending.
    /// The delta is decomposed to angle-axis. The rotation axis is checked against the
    /// local Z axis (standard Humanoid finger curl axis):
    ///   Right hand: curl toward palm = +Z rotation (positive)
    ///   Left hand:  curl toward palm = -Z rotation (positive, mirrored)
    /// Flex (toward palm) is clamped to fingerMaxFlex, extension (backward) to fingerMaxExtension.
    /// </summary>
    private Quaternion clampFingerRotation(Quaternion delta, string boneName)
    {
        if (fingerMaxFlex <= 0f && fingerMaxExtension <= 0f)
            return Quaternion.identity;

        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle < 0.01f) return delta;

        axis.Normalize();
        bool isRight = boneName.StartsWith("Right");
        // In standard Humanoid: right hand curls around +Z, left hand around -Z
        Vector3 curlAxis = isRight ? Vector3.forward : Vector3.back;
        float dot = Vector3.Dot(axis, curlAxis);
        // Positive dot = flex (toward palm), negative = extension (backward)
        float signedAngle = angle * (dot >= 0 ? 1f : -1f);

        float maxFlex = fingerMaxFlex > 0 ? fingerMaxFlex : 180f;
        float maxExt = fingerMaxExtension > 0 ? fingerMaxExtension : 0f;
        float clampedAngle = Mathf.Clamp(signedAngle, -maxExt, maxFlex);

        // Reconstruct: use original axis direction, scaled by sign
        Vector3 finalAxis = axis * (signedAngle >= 0 ? 1f : -1f);
        if (finalAxis == Vector3.zero) finalAxis = curlAxis;
        return Quaternion.AngleAxis(Mathf.Abs(clampedAngle), finalAxis.normalized);
    }
}

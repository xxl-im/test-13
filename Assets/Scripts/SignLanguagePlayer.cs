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

    [Header("Position-Based Driving")]
    [Tooltip("If true, main body bones (spine + arms) are driven by JSON local_position data (direction-based), not rotation. This is more accurate than rotation-based driving.")]
    public bool usePositionBasedRotation = true;

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

    // Rest world rotations (for position-based driving)
    private readonly Dictionary<string, Quaternion> _restWorldRotations = new Dictionary<string, Quaternion>();

    // Rest bone directions in world space (from parent bone to this bone, at T-pose)
    // Used to compute absolute rotation from JSON positions (not delta)
    private readonly Dictionary<string, Vector3> _restBoneDirections = new Dictionary<string, Vector3>();

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

    // Bone segments for position-based driving: (jsonBoneName, fromBoneName, toBoneName)
    // The direction is computed as: normalize(toPos - fromPos)
    private static readonly (string bone, string from, string to)[] PositionSegments = {
        ("Chest",         "Hips",           "Chest"),
        ("Neck",          "Chest",          "Neck"),
        ("LeftUpperArm",  "LeftUpperArm",   "LeftLowerArm"),
        ("LeftLowerArm",  "LeftLowerArm",   "LeftHand"),
        ("LeftHand",      "LeftLowerArm",   "LeftHand"),
        ("RightUpperArm", "RightUpperArm",  "RightLowerArm"),
        ("RightLowerArm", "RightLowerArm",  "RightHand"),
        ("RightHand",     "RightLowerArm",  "RightHand"),
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
        _restWorldRotations.Clear();
        _restBoneDirections.Clear();
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

        // Record rest world rotations for position-based driving
        _restWorldRotations.Clear();
        foreach (var kvp in _boneMap)
        {
            if (!_restWorldRotations.ContainsKey(kvp.Key))
                _restWorldRotations[kvp.Key] = kvp.Value.rotation;
        }

        // Record rest bone directions for absolute position-based driving
        // Direction = normalize(toBoneWorldPos - fromBoneWorldPos) at rest pose
        _restBoneDirections.Clear();
        foreach (var (boneName, fromName, toName) in PositionSegments)
        {
            if (!_boneMap.TryGetValue(fromName, out var fromBone)) continue;
            if (!_boneMap.TryGetValue(toName, out var toBone)) continue;
            Vector3 dir = toBone.position - fromBone.position;
            if (dir.sqrMagnitude > 1e-10f)
            {
                dir.Normalize();
                _restBoneDirections[boneName] = dir;
            }
        }
        Debug.Log($"[SignLanguagePlayer] Recorded {_restBoneDirections.Count} rest bone directions");

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

        if (usePositionBasedRotation)
        {
            ApplyPositionBasedFrame(f0, f1, t, f1Lookup);
            return;
        }

        ApplyRotationBasedFrame(f0, f1, t, f1Lookup);
    }

    /// <summary>
    /// Position-based driving: uses JSON local_position data to compute bone directions,
    /// then sets bone world rotations directly. More accurate than rotation-based
    /// because it bypasses the coordinate system mismatch between MediaPipe and DEF- bones.
    /// Only applies to bones that HAVE local_position data; others fall back to rotation.
    /// </summary>
    private void ApplyPositionBasedFrame(Frame f0, Frame f1, float t, Dictionary<string, BonePose> f1Lookup)
    {
        // Collect interpolated JSON positions and rotations for this frame
        var jsonPos = new Dictionary<string, Vector3>();
        foreach (var bp0 in f0.bone_poses)
        {
            if (string.IsNullOrEmpty(bp0.bone_name)) continue;
            if (bp0.local_position == null || bp0.local_position.Length < 3) continue;

            var p0 = new Vector3(bp0.local_position[0], bp0.local_position[1], bp0.local_position[2]);
            f1Lookup.TryGetValue(bp0.bone_name, out var bp1);
            var p1 = bp1?.local_position != null
                ? new Vector3(bp1.local_position[0], bp1.local_position[1], bp1.local_position[2])
                : p0;
            jsonPos[bp0.bone_name] = Vector3.Lerp(p0, p1, t);
        }

        var writtenBones = new HashSet<Transform>();

        // 1. Drive main body bones using position-based directions (where data exists)
        foreach (var (boneName, fromName, toName) in PositionSegments)
        {
            if (!_boneMap.TryGetValue(boneName, out var bone)) continue;
            if (writtenBones.Contains(bone)) continue;
            if (!_restWorldRotations.TryGetValue(boneName, out var restWorldRot)) continue;

            // Check if we have position data for this segment
            if (!jsonPos.ContainsKey(toName) || !jsonPos.ContainsKey(fromName))
                continue; // Skip — will be handled by rotation fallback

            // Also need the rest bone direction (character's actual T-pose direction)
            if (!_restBoneDirections.TryGetValue(boneName, out var restDir))
                continue;

            var toPos = jsonPos[toName];
            var fromPos = jsonPos[fromName];

            // Current direction from JSON positions (in Unity space)
            Vector3 jsonDir = toPos - fromPos;
            if (jsonDir.sqrMagnitude < 1e-10f) continue;
            jsonDir.Normalize();

            // Absolute rotation: rotate from character's rest bone direction to JSON direction
            // This makes the bone point in the direction specified by JSON, regardless of rest pose
            Quaternion delta = Quaternion.FromToRotation(restDir, jsonDir);
            bone.rotation = delta * restWorldRot;

            writtenBones.Add(bone);
        }

        // 2. For remaining bones (no position data, or finger bones), use rotation-based approach
        foreach (var bp0 in f0.bone_poses)
        {
            if (string.IsNullOrEmpty(bp0.bone_name)) continue;
            if (!_boneMap.TryGetValue(bp0.bone_name, out var bone)) continue;
            if (writtenBones.Contains(bone)) continue;

            f1Lookup.TryGetValue(bp0.bone_name, out var bp1);
            bp1 = bp1 ?? bp0;

            if (bp0.local_rotation == null || bp0.local_rotation.Length < 4) continue;

            var q0 = new Quaternion(bp0.local_rotation[0], bp0.local_rotation[1],
                                     bp0.local_rotation[2], bp0.local_rotation[3]);
            var q1 = bp1.local_rotation != null
                ? new Quaternion(bp1.local_rotation[0], bp1.local_rotation[1],
                                 bp1.local_rotation[2], bp1.local_rotation[3])
                : q0;

            var jsonRot = Quaternion.Slerp(q0, q1, t);

            // Apply absolute rotation directly (not delta-from-frame-0)
            if (IsFingerBone(bp0.bone_name))
            {
                // For fingers: compute delta from frame 0 for clamping, then reconstruct absolute
                Quaternion delta = jsonRot;
                if (_jsonFrame0Rotations.TryGetValue(bp0.bone_name, out var frame0Rot))
                    delta = Quaternion.Inverse(frame0Rot) * jsonRot;
                delta = clampFingerRotation(delta, bp0.bone_name);
                if (_jsonFrame0Rotations.TryGetValue(bp0.bone_name, out var f0Rot))
                    jsonRot = delta * f0Rot;
            }

            bone.localRotation = jsonRot;

            writtenBones.Add(bone);
        }
    }

    /// <summary>
    /// Rotation-based driving (original approach). Used when usePositionBasedRotation is false.
    /// </summary>
    private void ApplyRotationBasedFrame(Frame f0, Frame f1, float t, Dictionary<string, BonePose> f1Lookup)
    {
        var writtenBones = new HashSet<Transform>();

        foreach (var bp0 in f0.bone_poses)
        {
            if (string.IsNullOrEmpty(bp0.bone_name)) continue;
            if (!_boneMap.TryGetValue(bp0.bone_name, out var bone)) continue;
            if (writtenBones.Contains(bone)) continue;

            f1Lookup.TryGetValue(bp0.bone_name, out var bp1);
            bp1 = bp1 ?? bp0;

            if (bp0.local_rotation != null && bp0.local_rotation.Length >= 4)
            {
                var q0 = new Quaternion(bp0.local_rotation[0], bp0.local_rotation[1],
                                         bp0.local_rotation[2], bp0.local_rotation[3]);
                var q1 = bp1.local_rotation != null
                    ? new Quaternion(bp1.local_rotation[0], bp1.local_rotation[1],
                                     bp1.local_rotation[2], bp1.local_rotation[3])
                    : q0;

                var jsonRot = Quaternion.Slerp(q0, q1, t);

                // Apply absolute rotation directly (not delta-from-frame-0)
                if (IsFingerBone(bp0.bone_name))
                {
                    // For fingers: compute delta from frame 0 for clamping, then reconstruct absolute
                    Quaternion delta = jsonRot;
                    if (_jsonFrame0Rotations.TryGetValue(bp0.bone_name, out var frame0Rot))
                        delta = Quaternion.Inverse(frame0Rot) * jsonRot;
                    delta = clampFingerRotation(delta, bp0.bone_name);
                    if (_jsonFrame0Rotations.TryGetValue(bp0.bone_name, out var f0Rot))
                        jsonRot = delta * f0Rot;
                }

                bone.localRotation = jsonRot;
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
    /// </summary>
    private Quaternion clampFingerRotation(Quaternion delta, string boneName)
    {
        if (fingerMaxFlex <= 0f && fingerMaxExtension <= 0f)
            return Quaternion.identity;

        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle < 0.01f) return delta;

        axis.Normalize();
        bool isRight = boneName.StartsWith("Right");
        Vector3 curlAxis = isRight ? Vector3.forward : Vector3.back;
        float dot = Vector3.Dot(axis, curlAxis);
        float signedAngle = angle * (dot >= 0 ? 1f : -1f);

        float maxFlex = fingerMaxFlex > 0 ? fingerMaxFlex : 180f;
        float maxExt = fingerMaxExtension > 0 ? fingerMaxExtension : 0f;
        float clampedAngle = Mathf.Clamp(signedAngle, -maxExt, maxFlex);

        Vector3 finalAxis = axis * (signedAngle >= 0 ? 1f : -1f);
        if (finalAxis == Vector3.zero) finalAxis = curlAxis;
        return Quaternion.AngleAxis(Mathf.Abs(clampedAngle), finalAxis.normalized);
    }
}

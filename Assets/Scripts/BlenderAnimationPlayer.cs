using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads a Blender-exported animation JSON (Blender Rigify format, right-handed Z-up)
/// and drives the character's metarig at runtime.
///
/// JSON format (Blender type — e.g. sample_xxx_anim.json):
/// - location: [x, y, z] (Blender right-handed Z-up coordinates)
/// - rotation_quaternion: [w, x, y, z] (Blender quaternion order)
/// - rotation_euler: [x, y, z] (Blender euler in radians, XYZ order)
/// - Bone names use Blender Rigify conventions (ORG-, DEF-, MCH-ROT-, _master, _drv, etc.)
///
/// Coordinate conversion (Blender → Unity):
/// - Position:  (x, y, z)_B  → (x, z, y)_U   (swap Y and Z)
/// - Quaternion [w,x,y,z]_B  → [x,y,z,w]_U  where  U=(-B.x, -B.z, B.y, B.w)
/// </summary>
public class BlenderAnimationPlayer : MonoBehaviour
{
    [Header("Animation Data")]
    [Tooltip("Drag a .json asset here (Blender type, e.g. sample_xxx_anim.json).")]
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

    // Parsed data
    private AnimData _animData;
    private float _playbackTime;
    private bool _isPlaying;

    // Bone lookup: JSON bone name -> resolved Transform
    private readonly Dictionary<string, Transform> _boneMap = new Dictionary<string, Transform>();

    // Unity rest pose (for reset)
    private readonly Dictionary<Transform, Quaternion> _initialLocalRotations = new Dictionary<Transform, Quaternion>();

    // JSON frame-0 baseline rotations (for delta computation), already converted to Unity space
    private readonly Dictionary<string, Quaternion> _jsonFrame0Rotations = new Dictionary<string, Quaternion>();

    // Blender Rigify bone name → metarig bone name
    private static readonly Dictionary<string, string> BoneNameRemap = new Dictionary<string, string>
    {
        // Spine chain (DEF- deformation bones → metarig bones)
        { "DEF-spine",      "spine" },
        { "DEF-spine.001",  "spine.001" },
        { "DEF-spine.002",  "spine.002" },
        { "DEF-spine.003",  "spine.003" },
        { "DEF-spine.004",  "spine.004" },
        { "DEF-spine.005",  "spine.005" },

        // Head / Neck (MCH-ROT- mechanism bones → metarig bones)
        { "MCH-ROT-neck",   "spine.004" },  // neck
        { "MCH-ROT-head",   "spine.005" },  // head

        // Arms (ORG- organization bones → metarig bones)
        { "ORG-upper_arm.L", "upper_arm.L" },
        { "ORG-forearm.L",   "forearm.L" },
        { "ORG-hand.L",      "hand.L" },
        { "ORG-upper_arm.R", "upper_arm.R" },
        { "ORG-forearm.R",   "forearm.R" },
        { "ORG-hand.R",      "hand.R" },

        // Left fingers (_master → .01, _drv → .02)
        { "thumb.01_master.L",     "thumb.01.L" },
        { "thumb.02_drv.L",        "thumb.02.L" },
        { "f_index.01_master.L",   "f_index.01.L" },
        { "f_index.02_drv.L",      "f_index.02.L" },
        { "f_middle.01_master.L",  "f_middle.01.L" },
        { "f_middle.02_drv.L",     "f_middle.02.L" },
        { "f_ring.01_master.L",    "f_ring.01.L" },
        { "f_ring.02_drv.L",       "f_ring.02.L" },
        { "f_pinky.01_master.L",   "f_pinky.01.L" },
        { "f_pinky.02_drv.L",      "f_pinky.02.L" },

        // Right fingers
        { "thumb.01_master.R",     "thumb.01.R" },
        { "thumb.02_drv.R",        "thumb.02.R" },
        { "f_index.01_master.R",   "f_index.01.R" },
        { "f_index.02_drv.R",      "f_index.02.R" },
        { "f_middle.01_master.R",  "f_middle.01.R" },
        { "f_middle.02_drv.R",     "f_middle.02.R" },
        { "f_ring.01_master.R",    "f_ring.01.R" },
        { "f_ring.02_drv.R",       "f_ring.02.R" },
        { "f_pinky.01_master.R",   "f_pinky.01.R" },
        { "f_pinky.02_drv.R",      "f_pinky.02.R" },

        // Face
        { "jaw_master",     "jaw" },
    };

    #region JSON Data Structures

    [System.Serializable]
    private class BonePose
    {
        public string bone_name;
        public float[] location;               // [x, y, z] Blender coords
        public float[] rotation_quaternion;     // [w, x, y, z] Blender order
        public float[] rotation_euler;         // [x, y, z] radians, XYZ order
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
    /// Opens a file picker dialog (Editor only) to select a Blender-type JSON animation file.
    /// </summary>
    [ContextMenu("Pick JSON File")]
    public void PickJsonFile()
    {
#if UNITY_EDITOR
        string path = UnityEditor.EditorUtility.OpenFilePanel(
            "Select Blender Animation JSON", Application.streamingAssetsPath, "json");
        if (!string.IsNullOrEmpty(path))
        {
            LoadFromFile(path);
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlenderAnimationPlayer] Loaded JSON from: {path}");
        }
#endif
    }

    /// <summary>
    /// Loads animation data from an absolute path at runtime.
    /// Resets bones to their initial state before reloading.
    /// </summary>
    public void LoadFromFile(string absolutePath)
    {
        _isPlaying = false;
        _playbackTime = 0f;

        foreach (var kvp in _initialLocalRotations)
            if (kvp.Key != null) kvp.Key.localRotation = kvp.Value;

        _initialLocalRotations.Clear();
        _boneMap.Clear();
        _jsonFrame0Rotations.Clear();

        LoadAndBuild(absolutePath);
        RecordInitialState();
    }

    void Update()
    {
        if (!_isPlaying || _animData == null) return;

        _playbackTime += Time.deltaTime;

        float animDuration = _animData.total_frames / _animData.fps;
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
                Debug.LogError("[BlenderAnimationPlayer] Cannot resolve path for jsonFile.");
                return;
            }
#else
            Debug.LogError("[BlenderAnimationPlayer] jsonFile reference only works in Editor.");
            return;
#endif
        }
        else
        {
            Debug.LogError("[BlenderAnimationPlayer] No JSON file assigned.");
            return;
        }

        _animData = JsonUtility.FromJson<AnimData>(jsonText);

        if (_animData == null || _animData.frames == null || _animData.frames.Length == 0)
        {
            Debug.LogError("[BlenderAnimationPlayer] Failed to parse JSON or no frames found.");
            return;
        }

        Debug.Log($"[BlenderAnimationPlayer] Loaded: {_animData.word} | FPS: {_animData.fps} | Frames: {_animData.total_frames}");

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
        Debug.Log($"[BlenderAnimationPlayer] Bones matched: {matched}, missing: {missing}");
        if (missing > 0)
            Debug.LogWarning($"[BlenderAnimationPlayer] Missing bones: {string.Join(", ", missingNames)}");
    }

    private void RecordInitialState()
    {
        var recorded = new HashSet<Transform>();
        foreach (var kvp in _boneMap)
        {
            var bone = kvp.Value;
            if (bone == null || recorded.Contains(bone)) continue;
            _initialLocalRotations[bone] = bone.localRotation;
            recorded.Add(bone);
        }
        Debug.Log($"[BlenderAnimationPlayer] Recorded initial state for {recorded.Count} bones");
    }

    private void RecordJsonFrame0()
    {
        _jsonFrame0Rotations.Clear();
        if (_animData?.frames == null || _animData.frames.Length == 0) return;

        foreach (var bp in _animData.frames[0].bone_poses)
        {
            if (string.IsNullOrEmpty(bp.bone_name)) continue;
            Quaternion? q = ExtractBlenderRotation(bp);
            if (q.HasValue)
                _jsonFrame0Rotations[bp.bone_name] = q.Value;
        }
        Debug.Log($"[BlenderAnimationPlayer] Recorded JSON frame-0 baseline for {_jsonFrame0Rotations.Count} bones");
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
            if (BoneNameRemap.TryGetValue(jsonName, out var metarigName))
            {
                if (nameToTransform.TryGetValue(metarigName, out var t))
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

            Quaternion? q0Opt = ExtractBlenderRotation(bp0);
            if (!q0Opt.HasValue) continue;

            Quaternion q0 = q0Opt.Value;
            Quaternion? q1Opt = ExtractBlenderRotation(bp1);
            Quaternion q1 = q1Opt.HasValue ? q1Opt.Value : q0;

            Quaternion unityRot = Quaternion.Slerp(q0, q1, t);

            // Compute delta from frame 0 (both already in Unity space)
            Quaternion delta = unityRot;
            if (_jsonFrame0Rotations.TryGetValue(bp0.bone_name, out var frame0Rot))
                delta = Quaternion.Inverse(frame0Rot) * unityRot;

            // Clamp finger rotations
            if (IsFingerBone(bp0.bone_name))
                delta = ClampFingerRotation(delta, bp0.bone_name);

            // Compose with rest pose
            if (_initialLocalRotations.TryGetValue(bone, out var restRot))
                bone.localRotation = restRot * delta;
            else
                bone.localRotation = delta;

            writtenBones.Add(bone);
        }
    }

    /// <summary>
    /// Extract rotation from a Blender BonePose and convert to Unity quaternion.
    /// Handles both rotation_quaternion [w,x,y,z] and rotation_euler [rx,ry,rz] (radians, XYZ).
    /// </summary>
    private static Quaternion? ExtractBlenderRotation(BonePose bp)
    {
        if (bp.rotation_quaternion != null && bp.rotation_quaternion.Length >= 4)
        {
            // Blender quaternion: [w, x, y, z] → Unity: [x, y, z, w] = (-x, -z, y, w)
            float w = bp.rotation_quaternion[0];
            float x = bp.rotation_quaternion[1];
            float y = bp.rotation_quaternion[2];
            float z = bp.rotation_quaternion[3];
            return new Quaternion(-x, -z, y, w);
        }

        if (bp.rotation_euler != null && bp.rotation_euler.Length >= 3)
        {
            // Blender euler XYZ in radians → Blender quaternion → Unity quaternion
            float rx = bp.rotation_euler[0];
            float ry = bp.rotation_euler[1];
            float rz = bp.rotation_euler[2];

            // Convert Blender XYZ euler (radians) to Blender quaternion [w, x, y, z]
            float cx = Mathf.Cos(rx * 0.5f), sx = Mathf.Sin(rx * 0.5f);
            float cy = Mathf.Cos(ry * 0.5f), sy = Mathf.Sin(ry * 0.5f);
            float cz = Mathf.Cos(rz * 0.5f), sz = Mathf.Sin(rz * 0.5f);

            float bw = cx * cy * cz + sx * sy * sz;
            float bx = sx * cy * cz - cx * sy * sz;
            float by = cx * sy * cz + sx * cy * sz;
            float bz = cx * cy * sz - sx * sy * cz;

            // Apply Blender → Unity quaternion conversion
            return new Quaternion(-bx, -bz, by, bw);
        }

        return null;
    }

    private static bool IsFingerBone(string boneName)
    {
        return boneName.Contains("thumb") || boneName.Contains("f_index")
            || boneName.Contains("f_middle") || boneName.Contains("f_ring") || boneName.Contains("f_pinky");
    }

    /// <summary>
    /// Clamp a finger joint's delta rotation to prevent over-bending and backward bending.
    /// Uses the same logic as SignLanguagePlayer.clampFingerRotation.
    /// </summary>
    private Quaternion ClampFingerRotation(Quaternion delta, string boneName)
    {
        if (fingerMaxFlex <= 0f && fingerMaxExtension <= 0f)
            return Quaternion.identity;

        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle < 0.01f) return delta;

        axis.Normalize();
        bool isRight = boneName.EndsWith(".R");
        // In metarig: right hand curls around +Z, left hand around -Z
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

using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Debug visualizer: reads the same MediaPipe JSON and creates a sphere skeleton.
///
/// Position strategy:
/// - Bones WITH local_position (Head, Neck, RightUpperArm, RightLowerArm, RightHand):
///   placed at root-space coordinates (flat under root)
/// - Bones WITHOUT local_position (all 20 finger bones):
///   attached to parent Hand sphere, offset by metarig rest pose,
///   rotated by JSON local_rotation to show finger curl
///
/// Rotation: applied as-is (the visualizer uses its own simple hierarchy,
/// not the character's DEF- bones, so no rest-pose delta needed here).
/// </summary>
public class BonePositionVisualizer : MonoBehaviour
{
    [Tooltip("Drag the same .json asset used by SignLanguagePlayer.")]
    public UnityEngine.Object jsonFile;

    [Tooltip("Sphere radius for visualization.")]
    public float sphereRadius = 0.03f;

    [Tooltip("World-space offset for the debug skeleton.")]
    public Vector3 rootOffset = new Vector3(2f, 1f, 0f);

    [Tooltip("Scale applied to all bone positions from JSON.")]
    public float positionScale = 1f;

    [Tooltip("Show bone name labels in Scene view.")]
    public bool showLabels = true;

    private AnimData _animData;
    private float _playbackTime;
    private bool _isPlaying;

    private readonly Dictionary<string, Transform> _spheres = new Dictionary<string, Transform>();
    private readonly List<(LineRenderer lr, string parent, string child)> _lines = new List<(LineRenderer, string, string)>();
    private Transform _root;

    // JSON bone name -> parent JSON bone name (for finger hierarchy)
    private static readonly Dictionary<string, string> BoneParents = new Dictionary<string, string>
    {
        { "LeftThumbProximal",      "LeftHand" },
        { "LeftThumbIntermediate",  "LeftThumbProximal" },
        { "LeftIndexProximal",      "LeftHand" },
        { "LeftIndexIntermediate",  "LeftIndexProximal" },
        { "LeftMiddleProximal",     "LeftHand" },
        { "LeftMiddleIntermediate", "LeftMiddleProximal" },
        { "LeftRingProximal",       "LeftHand" },
        { "LeftRingIntermediate",   "LeftRingProximal" },
        { "LeftLittleProximal",     "LeftHand" },
        { "LeftLittleIntermediate", "LeftLittleProximal" },
        { "RightThumbProximal",      "RightHand" },
        { "RightThumbIntermediate",  "RightThumbProximal" },
        { "RightIndexProximal",      "RightHand" },
        { "RightIndexIntermediate",  "RightIndexProximal" },
        { "RightMiddleProximal",     "RightHand" },
        { "RightMiddleIntermediate", "RightMiddleProximal" },
        { "RightRingProximal",       "RightHand" },
        { "RightRingIntermediate",   "RightRingProximal" },
        { "RightLittleProximal",     "RightHand" },
        { "RightLittleIntermediate", "RightLittleProximal" },
    };

    // Default finger rest offsets (from metarig: most are ~0.04 along Y, thumb differs)
    private static readonly Dictionary<string, Vector3> FingerRestOffsets = new Dictionary<string, Vector3>
    {
        { "LeftThumbProximal",      new Vector3(0.01f, 0.00f, 0.01f) },
        { "LeftThumbIntermediate",  new Vector3(0.00f, 0.04f, 0.00f) },
        { "LeftIndexProximal",      new Vector3(0.00f, 0.04f, 0.00f) },
        { "LeftIndexIntermediate",  new Vector3(0.00f, 0.03f, 0.00f) },
        { "LeftMiddleProximal",     new Vector3(0.00f, 0.04f, 0.00f) },
        { "LeftMiddleIntermediate", new Vector3(0.00f, 0.03f, 0.00f) },
        { "LeftRingProximal",       new Vector3(0.00f, 0.04f, 0.00f) },
        { "LeftRingIntermediate",   new Vector3(0.00f, 0.03f, 0.00f) },
        { "LeftLittleProximal",     new Vector3(0.00f, 0.04f, 0.00f) },
        { "LeftLittleIntermediate", new Vector3(0.00f, 0.02f, 0.00f) },
        { "RightThumbProximal",      new Vector3(-0.01f, 0.00f, 0.01f) },
        { "RightThumbIntermediate",  new Vector3(0.00f, 0.04f, 0.00f) },
        { "RightIndexProximal",      new Vector3(0.00f, 0.04f, 0.00f) },
        { "RightIndexIntermediate",  new Vector3(0.00f, 0.03f, 0.00f) },
        { "RightMiddleProximal",     new Vector3(0.00f, 0.04f, 0.00f) },
        { "RightMiddleIntermediate", new Vector3(0.00f, 0.03f, 0.00f) },
        { "RightRingProximal",       new Vector3(0.00f, 0.04f, 0.00f) },
        { "RightRingIntermediate",   new Vector3(0.00f, 0.03f, 0.00f) },
        { "RightLittleProximal",     new Vector3(0.00f, 0.04f, 0.00f) },
        { "RightLittleIntermediate", new Vector3(0.00f, 0.02f, 0.00f) },
    };

    // Bone connections for lines
    private static readonly List<(string parent, string child)> BoneConnections = new List<(string, string)>
    {
        ("Head", "Neck"),
        ("Neck", "RightUpperArm"),
        ("RightUpperArm", "RightLowerArm"),
        ("RightLowerArm", "RightHand"),
        ("RightHand", "RightThumbProximal"),
        ("RightThumbProximal", "RightThumbIntermediate"),
        ("RightHand", "RightIndexProximal"),
        ("RightIndexProximal", "RightIndexIntermediate"),
        ("RightHand", "RightMiddleProximal"),
        ("RightMiddleProximal", "RightMiddleIntermediate"),
        ("RightHand", "RightRingProximal"),
        ("RightRingProximal", "RightRingIntermediate"),
        ("RightHand", "RightLittleProximal"),
        ("RightLittleProximal", "RightLittleIntermediate"),
    };

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
        LoadJson();
        CreateSpheres();
        CreateLines();
        _isPlaying = true;
    }

    private void LoadJson()
    {
        if (jsonFile == null)
        {
            Debug.LogError("[BonePositionVisualizer] No JSON file assigned.");
            return;
        }

#if UNITY_EDITOR
        string assetPath = UnityEditor.AssetDatabase.GetAssetPath(jsonFile);
        if (string.IsNullOrEmpty(assetPath))
        {
            Debug.LogError("[BonePositionVisualizer] Cannot resolve path for jsonFile.");
            return;
        }
        string jsonText = File.ReadAllText(assetPath);
#else
        Debug.LogError("[BonePositionVisualizer] Only works in Editor.");
        return;
#endif

        _animData = JsonUtility.FromJson<AnimData>(jsonText);
        if (_animData == null || _animData.frames == null || _animData.frames.Length == 0)
        {
            Debug.LogError("[BonePositionVisualizer] Failed to parse JSON.");
            return;
        }

        // Count bones with vs without positions
        int withPos = 0, withoutPos = 0;
        var boneNames = new HashSet<string>();
        foreach (var frame in _animData.frames)
        {
            if (frame?.bone_poses == null) continue;
            foreach (var bp in frame.bone_poses)
            {
                if (string.IsNullOrEmpty(bp.bone_name)) continue;
                if (boneNames.Add(bp.bone_name))
                {
                    if (bp.local_position != null) withPos++;
                    else withoutPos++;
                }
            }
        }

        Debug.Log($"[BonePositionVisualizer] Loaded: {_animData.word} | FPS: {_animData.fps} | Frames: {_animData.total_frames} | Bones: {boneNames.Count} (with pos: {withPos}, rot only: {withoutPos})");
    }

    private void CreateSpheres()
    {
        _root = new GameObject("DebugBones").transform;
        _root.SetParent(transform);
        _root.localPosition = rootOffset;
        _root.localRotation = Quaternion.identity;

        // Collect all unique bone names
        var boneNames = new HashSet<string>();
        foreach (var frame in _animData.frames)
        {
            if (frame?.bone_poses == null) continue;
            foreach (var bp in frame.bone_poses)
                if (!string.IsNullOrEmpty(bp.bone_name))
                    boneNames.Add(bp.bone_name);
        }

        // Two-pass creation:
        // Pass 1: create root-space bones (those with local_position) as flat children of root
        // Pass 2: create finger bones (without local_position) as children of their parent hand
        foreach (var boneName in boneNames)
        {
            bool isFinger = FingerRestOffsets.ContainsKey(boneName);
            if (isFinger) continue; // skip for now, create in pass 2

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = boneName;
            go.transform.localScale = Vector3.one * sphereRadius * 2f;
            go.transform.SetParent(_root, false);
            go.transform.localPosition = Vector3.zero;
            go.GetComponent<Renderer>().material.color = GetBoneColor(boneName);
            _spheres[boneName] = go.transform;
        }

        // Pass 2: create finger bones as children of their parent
        foreach (var boneName in boneNames)
        {
            if (!FingerRestOffsets.ContainsKey(boneName)) continue;

            Transform parentSphere = null;
            if (BoneParents.TryGetValue(boneName, out var parentName))
                _spheres.TryGetValue(parentName, out parentSphere);

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = boneName;
            go.transform.localScale = Vector3.one * sphereRadius * 2f;
            go.GetComponent<Renderer>().material.color = GetBoneColor(boneName);

            if (parentSphere != null)
            {
                go.transform.SetParent(parentSphere, false);
                go.transform.localPosition = FingerRestOffsets[boneName];
            }
            else
            {
                // Parent hand bone is missing from JSON (e.g., LeftHand not in data)
                // Attach to root as fallback
                go.transform.SetParent(_root, false);
                go.transform.localPosition = Vector3.zero;
            }
            go.transform.localRotation = Quaternion.identity;

            _spheres[boneName] = go.transform;
        }

        Debug.Log($"[BonePositionVisualizer] Created {_spheres.Count} debug spheres.");
    }

    private void CreateLines()
    {
        var lineMat = new Material(Shader.Find("Unlit/Color"));
        lineMat.color = new Color(0.8f, 0.8f, 0.8f, 0.5f);

        foreach (var (parent, child) in BoneConnections)
        {
            if (!_spheres.ContainsKey(parent) || !_spheres.ContainsKey(child)) continue;

            var go = new GameObject($"Line_{parent}_{child}");
            go.transform.SetParent(_root);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = lineMat;
            lr.startWidth = 0.005f;
            lr.endWidth = 0.005f;
            lr.positionCount = 2;
            _lines.Add((lr, parent, child));
        }

        Debug.Log($"[BonePositionVisualizer] Created {_lines.Count} bone connections.");
    }

    private void UpdateLines()
    {
        foreach (var (lr, parent, child) in _lines)
        {
            if (_spheres.TryGetValue(parent, out var pTrans) && _spheres.TryGetValue(child, out var cTrans))
            {
                lr.SetPosition(0, pTrans.position);
                lr.SetPosition(1, cTrans.position);
            }
        }
    }

    private static Color GetBoneColor(string boneName)
    {
        if (boneName.StartsWith("Left")) return new Color(0.3f, 0.6f, 1f); // blue
        if (boneName.StartsWith("Right")) return new Color(1f, 0.3f, 0.3f); // red
        if (boneName == "Head" || boneName == "Neck") return Color.yellow;
        return Color.green; // spine chain
    }

    void Update()
    {
        if (!_isPlaying || _animData == null) return;

        _playbackTime += Time.deltaTime;

        float animDuration = _animData.frames[_animData.frames.Length - 1].timestamp;
        if (_playbackTime >= animDuration)
            _playbackTime = Mathf.Repeat(_playbackTime, animDuration);

        ApplyPoseAtTime(_playbackTime);
        UpdateLines();
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

        var f1Lookup = new Dictionary<string, BonePose>();
        if (f1?.bone_poses != null)
            foreach (var bp in f1.bone_poses)
                if (!string.IsNullOrEmpty(bp.bone_name))
                    f1Lookup[bp.bone_name] = bp;

        if (f0?.bone_poses == null) return;

        foreach (var bp0 in f0.bone_poses)
        {
            if (string.IsNullOrEmpty(bp0.bone_name)) continue;
            if (!_spheres.TryGetValue(bp0.bone_name, out var sphere)) continue;

            f1Lookup.TryGetValue(bp0.bone_name, out var bp1);
            bp1 = bp1 ?? bp0;

            bool isFinger = FingerRestOffsets.ContainsKey(bp0.bone_name);

            // Position: only for root-space bones (non-finger, has local_position)
            if (!isFinger && bp0.local_position != null && bp0.local_position.Length >= 3)
            {
                var p0 = new Vector3(bp0.local_position[0], bp0.local_position[1], bp0.local_position[2]) * positionScale;
                var p1 = bp1.local_position != null
                    ? new Vector3(bp1.local_position[0], bp1.local_position[1], bp1.local_position[2]) * positionScale
                    : p0;
                sphere.localPosition = Vector3.Lerp(p0, p1, t);
            }

            // Rotation: apply to all bones that have local_rotation
            if (bp0.local_rotation != null && bp0.local_rotation.Length >= 4)
            {
                var r0 = new Quaternion(bp0.local_rotation[0], bp0.local_rotation[1],
                                         bp0.local_rotation[2], bp0.local_rotation[3]);
                var r1 = bp1.local_rotation != null
                    ? new Quaternion(bp1.local_rotation[0], bp1.local_rotation[1],
                                     bp1.local_rotation[2], bp1.local_rotation[3])
                    : r0;
                sphere.localRotation = Quaternion.Slerp(r0, r1, t);
            }
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showLabels) return;
        foreach (var kvp in _spheres)
        {
            if (kvp.Value == null) continue;
            UnityEditor.Handles.color = GetBoneColor(kvp.Key);
            UnityEditor.Handles.Label(kvp.Value.position + Vector3.up * (sphereRadius + 0.02f), kvp.Key);
        }
    }
#endif
}

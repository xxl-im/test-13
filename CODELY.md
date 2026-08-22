

## Codely Structured Memories

### User

### Feedback

### Project
- [2026-08-16 15:31:32] User is building a sign-language / motion-capture system in Unity. Character has a Blender metarig (Rigify) with bone names like `upper_arm.L`, `f_index.01.R`, `spine.004`, etc. Python script `capture_motion.py` (at `C:\Users\sherr\CodeBuddy\Claw\python\`) uses OpenCV + MediaPipe to capture face (478pt) and pose (33pt) landmarks, outputs both raw MediaPipe JSON and a SignLanguagePlayer-compatible `_unity.json` with computed bone rotations. Python interpreter at `C:\Users\sherr\AppData\Local\Programs\Python\Python310\python.exe`.
- [2026-08-16 15:31:34] Two JSON formats in this project: (1) Blender type (`sample_xxx_anim.json`) — fields `location`/`rotation_quaternion`[w,x,y,z]/`rotation_euler`, Rigify bone names (`ORG-upper_arm.R`, `thumb.01_master.L`), read by `BlenderAnimationPlayer.cs`; (2) MediaPipe/Unity type (`sample_xxx_unity_anim.json` or `motion_xxx_unity.json`) — fields `local_position`/`local_rotation`[x,y,z,w], HumanBodyBones names (`LeftUpperArm`), read by `SignLanguagePlayer.cs`. Both players use delta-from-frame-0 approach and compose with DEF/metarig rest pose.

### Reference


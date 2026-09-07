

## Codely Structured Memories

### User

### Feedback

### Project
- [2026-08-16 15:31:32] User is building a sign-language / motion-capture system in Unity. Character has a Blender metarig (Rigify) with bone names like `upper_arm.L`, `f_index.01.R`, `spine.004`, etc. Python script `capture_motion.py` (at `C:\Users\sherr\CodeBuddy\Claw\python\`) uses OpenCV + MediaPipe to capture face (478pt) and pose (33pt) landmarks, outputs both raw MediaPipe JSON and a SignLanguagePlayer-compatible `_unity.json` with computed bone rotations. Python interpreter at `C:\Users\sherr\AppData\Local\Programs\Python\Python310\python.exe`.
- [2026-09-04 21:18:05] Two JSON formats in this project: (1) Blender type (`sample_xxx_anim.json`) — fields `location`/`rotation_quaternion`[w,x,y,z]/`rotation_euler`, Rigify bone names (`ORG-upper_arm.R`, `thumb.01_master.L`), read by `BlenderAnimationPlayer.cs`（纯旋转驱动，location 未使用）; (2) MediaPipe/Unity type (`sample_xxx_unity_anim.json` or `motion_xxx_unity.json`) — fields `local_position`/`local_rotation`[x,y,z,w], HumanBodyBones names (`LeftUpperArm`), read by `SignLanguagePlayer.cs`。SignLanguagePlayer 已于 2026-09-04 改为纯旋转驱动（用户要求：位置/平移驱动会使人物变形），删除了 usePositionBasedRotation/positionScale/ApplyPositionBasedFrame 等全部位置逻辑。咖啡 JSON 数据分布：Hips/Spine/Chest/UpperChest 有 rotation+position（81 帧），Neck/Head 只有 position，手臂有 rotation，Hand 只有 position，手指只有 rotation —— 纯旋转模式下 Neck/Head（DEF-spine.006）与 DEF-hand.L/R 保持静止。另：ManageScreenshot.CaptureGameViewAndSave 的 PNG 输出是垂直翻转的（天空在下、地面在上），判断画面实际方向需以骨骼测量为准。

- [2026-09-03 10:22:27] Assets/StreamingAssets 的 .json 文件以 DefaultImporter 导入为 DefaultAsset（非 TextAsset），给 SignLanguagePlayer.jsonFile 赋值必须用 AssetDatabase.LoadMainAssetAtPath；且 sample_咖啡 文件名实际码点是 5496 5561（第二个字符非标准"啡"U+5564，是形近字符 U+5561），脚本内用字面中文串匹配文件名会失败，需先枚举磁盘文件或用 \u 转义码点。当前场景模型为 Assets/数字人1.blend原版.blend-5.fbx（Rigify DEF-骨架，160根DEF骨骼），SignLanguagePlayer 挂在根对象上。

### Reference


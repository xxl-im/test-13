using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(BlenderAnimationPlayer))]
public class BlenderAnimationPlayerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var player = (BlenderAnimationPlayer)target;

        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("File Selection", EditorStyles.boldLabel);

        if (GUILayout.Button("Browse JSON File...", GUILayout.Height(30)))
        {
            player.PickJsonFile();
        }

        if (player.jsonFile != null)
        {
            EditorGUILayout.HelpBox(
                "Using JSON file:\n" + player.jsonFile.name,
                MessageType.Info);
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Reload Animation"))
        {
            player.LoadFromFile(null);
        }
    }
}

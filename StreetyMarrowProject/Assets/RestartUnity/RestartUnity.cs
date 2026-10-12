#if UNITY_EDITOR

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class RestartEditor : Editor
{
    [MenuItem("Tools/Restart Editor/Restart")]
    public static void RestartUnity()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var projectPath = System.IO.Path.GetDirectoryName(Application.dataPath);
        System.Diagnostics.Process.Start(EditorApplication.applicationPath, "-projectPath " + projectPath);
        EditorApplication.Exit(0);
    }
}

#endif
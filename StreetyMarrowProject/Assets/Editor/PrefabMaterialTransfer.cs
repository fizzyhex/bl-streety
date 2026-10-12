using UnityEngine;
using UnityEditor;

public class PrefabMaterialTransfer : EditorWindow
{
    private GameObject sourcePrefab;
    private GameObject targetPrefab;

    [MenuItem("Tools/Prefab Material Transfer")]
    public static void ShowWindow()
    {
        GetWindow<PrefabMaterialTransfer>("Prefab Material Transfer");
    }

    private void OnGUI()
    {
        GUILayout.Label("Transfer Materials from Source to Target Prefab", EditorStyles.boldLabel);

        sourcePrefab = (GameObject)EditorGUILayout.ObjectField("Source Prefab", sourcePrefab, typeof(GameObject), false);
        targetPrefab = (GameObject)EditorGUILayout.ObjectField("Target Prefab", targetPrefab, typeof(GameObject), false);

        if (GUILayout.Button("Transfer Materials"))
        {
            if (sourcePrefab == null || targetPrefab == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign both source and target prefabs.", "OK");
                return;
            }

            TransferMaterials(sourcePrefab, targetPrefab);
        }
    }

    private void TransferMaterials(GameObject source, GameObject target)
    {
        // Instantiate temporary copies
        GameObject sourceInstance = (GameObject)PrefabUtility.InstantiatePrefab(source);
        GameObject targetInstance = (GameObject)PrefabUtility.InstantiatePrefab(target);

        try
        {
            var sourceRenderers = sourceInstance.GetComponentsInChildren<Renderer>(true);
            var targetRenderers = targetInstance.GetComponentsInChildren<Renderer>(true);

            foreach (var targetRenderer in targetRenderers)
            {
                // Find matching source renderer by name + hierarchy path
                string targetPath = GetHierarchyPath(targetRenderer.transform, targetInstance.transform);
                var sourceRenderer = FindRendererByPath(sourceRenderers, targetPath);

                if (sourceRenderer != null)
                {
                    Undo.RecordObject(targetRenderer, "Transfer Materials");
                    targetRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
                }
                else
                {
                    Debug.LogWarning($"No matching source renderer found for: {targetPath}");
                }
            }

            // Save changes back to prefab
            PrefabUtility.SaveAsPrefabAsset(targetInstance, AssetDatabase.GetAssetPath(target));
            Debug.Log("Material transfer complete.");
        }
        finally
        {
            DestroyImmediate(sourceInstance);
            DestroyImmediate(targetInstance);
        }
    }

    private string GetHierarchyPath(Transform current, Transform root)
    {
        string path = current.name;
        while (current.parent != null && current.parent != root)
        {
            current = current.parent;
            path = current.name + "/" + path;
        }
        return path;
    }

    private Renderer FindRendererByPath(Renderer[] renderers, string path)
    {
        foreach (var r in renderers)
        {
            string rPath = GetHierarchyPath(r.transform, r.transform.root);
            if (rPath == path)
                return r;
        }
        return null;
    }
}

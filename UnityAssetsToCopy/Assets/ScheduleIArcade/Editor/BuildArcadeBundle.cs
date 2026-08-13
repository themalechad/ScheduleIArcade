#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildArcadeBundle
{
    private const string PrefabPath = "Assets/ScheduleIArcade/Generated/ArcadeRoom.prefab";

    [MenuItem("Schedule I Arcade/2 - Build Windows AssetBundle")]
    public static void Build()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("Generate ArcadeRoom.prefab first with Schedule I Arcade > 1 - Prepare Arcade Room.");
            return;
        }

        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../ArcadeAssetBundleOutput"));
        Directory.CreateDirectory(output);

        // Build from a temporary clean prefab so the red/green/orange/blue editor guides can
        // never leak into the shipped AssetBundle even if Unity keeps EditorOnly objects in an
        // AssetBundle dependency graph. All real room edits are preserved.
        const string buildPrefabPath = "Assets/ScheduleIArcade/Generated/ArcadeRoom_Build.prefab";
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var guides = instance.transform.Find("LAYOUT_GUIDES_EDITOR_ONLY");
        if (guides != null) UnityEngine.Object.DestroyImmediate(guides.gameObject);
        PrefabUtility.SaveAsPrefabAsset(instance, buildPrefabPath);
        UnityEngine.Object.DestroyImmediate(instance);
        AssetDatabase.SaveAssets();

        var build = new AssetBundleBuild
        {
            assetBundleName = "scheduleiarcade",
            assetNames = new[] { buildPrefabPath }
        };

        BuildPipeline.BuildAssetBundles(
            output,
            new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);

        AssetDatabase.DeleteAsset(buildPrefabPath);
        AssetDatabase.SaveAssets();
        Debug.Log("Built Schedule I Arcade bundle: " + Path.Combine(output, "scheduleiarcade"));
        EditorUtility.RevealInFinder(output);
    }
}
#endif

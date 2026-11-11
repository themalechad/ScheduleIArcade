using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Finds Schedule I's own props and makes VISUAL-ONLY copies of them: plain GameObjects holding the
/// vanilla meshes and materials, with no scripts, network objects, interactions or colliders.
/// Lookup order (0.6.0):
///   1. an unbatched instance already in the loaded scene, by exact name;
///   2. the game's own Resources (Resources.Load of a buildable prefab path such as
///      "storage/displaycabinet/DisplayCabinet_Built"). The prefab asset is only READ as a template:
///      it is never instantiated, so none of its scripts/network objects run (Schedule I 0.4.7f12 has no
///      Addressables or AssetBundles: StreamingAssets holds only default saves);
///   3. the optional personal LocalAssets pack (last resort; absent in the public build).
/// Nothing is extracted to disk or packaged.
///
/// level1 is statically batched, so many instances render through a world-space
/// "Combined Mesh (root: scene) N"; copying those would draw a chunk of the town. Only instances
/// whose active LOD0 renderers all use their own mesh are accepted.
/// </summary>
internal static class VanillaPropLibrary
{
    private static Dictionary<string, List<Transform>>? _byName;

    public static void Reset() => _byName = null;

    private static string BaseName(string name)
    {
        // "Trash Can Prop (7)" -> "Trash Can Prop"
        int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(")", StringComparison.Ordinal) && int.TryParse(name.Substring(paren + 2, name.Length - paren - 3), out _))
            return name.Substring(0, paren);
        return name;
    }

    private static void EnsureIndex()
    {
        if (_byName != null)
            return;
        _byName = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t == null)
                continue;
            string name = t.gameObject.name;
            if (name.StartsWith("ARCADE_", StringComparison.Ordinal) || name.StartsWith("ScheduleIArcade", StringComparison.Ordinal) || name.StartsWith("Vanilla_", StringComparison.Ordinal))
                continue;
            string key = BaseName(name);
            if (!_byName.TryGetValue(key, out List<Transform>? list))
                _byName[key] = list = new List<Transform>();
            list.Add(t);
        }
    }

    private static bool IsLodOrProxy(string name) =>
        name.Contains("LOD1", StringComparison.Ordinal) || name.Contains("LOD2", StringComparison.Ordinal)
        || name.Contains("LOD3", StringComparison.Ordinal) || name.Contains("Impostor", StringComparison.Ordinal);

    /// <summary>The LOD0 mesh renderers of <paramref name="root"/>, or null if any is statically batched.</summary>
    private static List<MeshRenderer>? ReusableParts(Transform root)
    {
        var parts = new List<MeshRenderer>();
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (renderer == null || !renderer.enabled || IsLodOrProxy(renderer.gameObject.name))
                continue;
            bool hidden = false;
            for (Transform? t = renderer.transform; t != null && t != root; t = t.parent)
            {
                if (!t.gameObject.activeSelf || IsLodOrProxy(t.gameObject.name)) { hidden = true; break; }
            }
            if (hidden)
                continue;
            MeshFilter? filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                continue;
            if (renderer.isPartOfStaticBatch || filter.sharedMesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal))
                return null;
            parts.Add(renderer);
        }
        return parts.Count > 0 ? parts : null;
    }

    /// <summary>First reusable instance for any of the names, in order.</summary>
    public static bool TryFind(string[] names, out Transform? source, out List<MeshRenderer>? parts, out string matchedName)
        => TryFind(names, Array.Empty<string>(), out source, out parts, out matchedName, out _);

    /// <summary>Where the last successful lookup came from: "scene", "Resources" or "LocalAssets".</summary>
    public static bool TryFind(string[] names, string[] resourcePaths, out Transform? source, out List<MeshRenderer>? parts, out string matchedName, out string origin)
    {
        origin = "scene";
        EnsureIndex();
        foreach (string name in names)
        {
            if (!_byName!.TryGetValue(name, out List<Transform>? candidates))
                continue;
            foreach (Transform candidate in candidates)
            {
                if (candidate == null)
                    continue;
                List<MeshRenderer>? found = ReusableParts(candidate);
                if (found == null)
                    continue;
                source = candidate;
                parts = found;
                matchedName = name;
                return true;
            }
        }
        // Live scene missed (nothing unbatched, or the name is not loaded): the game's Resources.
        foreach (string path in resourcePaths)
        {
            Transform? prefab = LoadResourceTemplate(path);
            if (prefab == null)
                continue;
            List<MeshRenderer>? found = ReusableParts(prefab);
            if (found == null)
                continue;
            source = prefab;
            parts = found;
            matchedName = path;
            origin = "Resources";
            return true;
        }
        // Last resort: the optional personal LocalAssets pack (no-op when the folder is absent).
        origin = "LocalAssets";
        if (LocalAssetPack.TryGetVisual(names, out source, out parts, out matchedName))
            return true;
        source = null;
        parts = null;
        matchedName = string.Empty;
        return false;
    }

    /// <summary>Resources.Load of a prefab asset (forward slashes, no extension); null when absent.</summary>
    public static Transform? LoadResourceTemplate(string path)
    {
        try
        {
            UnityEngine.Object? asset = Resources.Load(path);
            GameObject? go = asset != null ? asset.TryCast<GameObject>() : null;
            return go != null ? go.transform : null;
        }
        catch
        {
            return null;
        }
    }

    public static int CountInstances(string name)
    {
        EnsureIndex();
        return _byName!.TryGetValue(name, out List<Transform>? list) ? list.Count : 0;
    }

    /// <summary>Visual-only copy (meshes + shared materials) at identity, metric size preserved.</summary>
    public static GameObject CopyVisual(Transform source, List<MeshRenderer> parts, string name)
    {
        var root = new GameObject(name);
        Quaternion inverse = Quaternion.Inverse(source.rotation);
        foreach (MeshRenderer renderer in parts)
        {
            Transform t = renderer.transform;
            var part = new GameObject(t.gameObject.name);
            part.transform.SetParent(root.transform, false);
            part.transform.localPosition = inverse * (t.position - source.position);
            part.transform.localRotation = inverse * t.rotation;
            part.transform.localScale = t.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = renderer.GetComponent<MeshFilter>()!.sharedMesh;
            var copy = part.AddComponent<MeshRenderer>();
            copy.sharedMaterials = renderer.sharedMaterials;
        }
        return root;
    }

    public static string PathOf(Transform t)
    {
        string path = t.gameObject.name;
        for (Transform? p = t.parent; p != null; p = p.parent)
            path = p.gameObject.name + "/" + path;
        return path;
    }
}

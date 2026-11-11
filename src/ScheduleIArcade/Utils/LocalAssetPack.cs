using MelonLoader;
using ScheduleIArcade.Core;
using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Optional personal asset pack loaded from Mods/ScheduleIArcade/LocalAssets when that folder is
/// present. Runtime cloning of already-loaded vanilla objects is always tried first; this pack is
/// only used for textures and meshes that could not be resolved from the live scene. The folder
/// is not part of the public release and the loader does nothing when it is missing.
/// </summary>
internal static class LocalAssetPack
{
    private static bool _initialized;
    private static string? _root;
    private static LocalAssetManifest? _manifest;
    private static readonly Dictionary<string, Texture2D> Textures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Material> Materials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Mesh> Meshes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, GameObject> Templates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<UnityEngine.Object> Created = new();
    private static Transform? _templateRoot;

    public static bool IsAvailable => _manifest != null && _manifest.Entries.Count > 0;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        _root = null;
        _manifest = null;
        try
        {
            string root = ModPaths.LocalAssetsRoot;
            string? existing = ModFiles.FindCaseInsensitiveDirectory(root);
            if (existing == null)
            {
                MelonLogger.Msg("Arcade local asset pack: Mods/ScheduleIArcade/LocalAssets is not present; using live vanilla objects and materials only.");
                return;
            }
            _root = existing;
            string? manifestPath = ModFiles.ResolveExisting(Path.Combine(existing, "manifest.json"));
            if (manifestPath == null)
            {
                MelonLogger.Warning("Arcade local asset pack: LocalAssets exists but has no manifest.json; ignoring the folder.");
                return;
            }
            string json = EncodingUtf8(ModFiles.ReadAllBytes(manifestPath));
            if (!LocalAssetManifest.TryParse(json, out LocalAssetManifest manifest, out string error))
            {
                MelonLogger.Warning("Arcade local asset pack: manifest.json could not be read (" + error + ").");
                return;
            }
            _manifest = manifest;
            MelonLogger.Msg("Arcade local asset pack: loaded " + manifest.Entries.Count + " entr" + (manifest.Entries.Count == 1 ? "y" : "ies") + " from " + existing + (string.IsNullOrEmpty(manifest.Version) ? string.Empty : " (pack " + manifest.Version + ")") + ". Runtime cloning of live vanilla objects is still preferred.");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("Arcade local asset pack: initialization failed; falling back to live assets only: " + ex.Message);
            _manifest = null;
        }
    }

    public static void Reset()
    {
        foreach (UnityEngine.Object created in Created)
        {
            if (created != null)
                UnityEngine.Object.Destroy(created);
        }
        Created.Clear();
        Textures.Clear();
        Materials.Clear();
        Meshes.Clear();
        Templates.Clear();
        if (_templateRoot != null)
            UnityEngine.Object.Destroy(_templateRoot.gameObject);
        _templateRoot = null;
        _initialized = false;
        _root = null;
        _manifest = null;
    }

    /// <summary>Material for one of the names, built from a pack texture (or a transparent glass tint). Null if the pack has no match.</summary>
    public static Material? TryCreateMaterial(params string[] names)
    {
        Initialize();
        if (!IsAvailable || names == null || names.Length == 0)
            return null;
        foreach (string name in names)
        {
            if (Materials.TryGetValue(name, out Material? cached) && cached != null)
                return cached;
        }

        LocalAssetEntry? entry = _manifest!.FindAny("texture", names);
        bool glass = IsGlassName(names);
        if (entry == null && !glass)
            return null;

        try
        {
            Texture2D? texture = entry != null && !entry.Transparent ? LoadTexture(entry) : null;
            bool transparent = glass || (entry != null && entry.Transparent);
            string label = names[0];
            Material material = NewLitMaterial("ScheduleIArcade_Local_" + Sanitize(label), transparent);
            if (texture != null)
            {
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            }
            if (transparent)
            {
                Color tint = new(0.55f, 0.65f, 0.7f, 0.28f);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
                if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
                material.renderQueue = 3000;
            }
            foreach (string name in names)
                Materials[name] = material;
            if (entry != null)
            {
                foreach (string n in entry.Names)
                    Materials[n] = material;
            }
            MelonLogger.Msg("Arcade local asset pack: material '" + label + "' from " + (entry != null ? entry.File : "generated transparent glass") + ".");
            return material;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("Arcade local asset pack: could not build material '" + names[0] + "': " + ex.Message);
            return null;
        }
    }

    /// <summary>Visual-only template built from a pack mesh. Used only after live vanilla search fails.</summary>
    public static bool TryGetVisual(string[] names, out Transform? source, out List<MeshRenderer>? parts, out string matchedName)
    {
        source = null;
        parts = null;
        matchedName = string.Empty;
        Initialize();
        if (!IsAvailable || names == null || names.Length == 0)
            return false;

        LocalAssetEntry? entry = _manifest!.FindAny("mesh", names);
        if (entry == null)
            return false;

        string key = entry.File;
        if (!Templates.TryGetValue(key, out GameObject? template) || template == null)
        {
            Mesh? mesh = LoadMesh(entry);
            if (mesh == null)
                return false;
            Material? material = null;
            if (!string.IsNullOrWhiteSpace(entry.Texture))
                material = TryCreateMaterial(entry.Texture!);
            if (material == null)
                material = TryCreateMaterial(entry.Names);
            if (material == null)
                material = NewLitMaterial("ScheduleIArcade_Local_Default", false);

            template = new GameObject("ScheduleIArcade_Local_" + Sanitize(entry.Names[0]));
            template.SetActive(false);
            EnsureTemplateRoot();
            template.transform.SetParent(_templateRoot, false);
            template.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = template.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            Created.Add(template);
            Templates[key] = template;
            MelonLogger.Msg("Arcade local asset pack: mesh '" + entry.Names[0] + "' from " + entry.File + " (live vanilla object was not available).");
        }

        source = template.transform;
        parts = new List<MeshRenderer>(template.GetComponentsInChildren<MeshRenderer>(true));
        matchedName = entry.Names[0] + " (local pack)";
        return parts.Count > 0;
    }

    private static Texture2D? LoadTexture(LocalAssetEntry entry)
    {
        if (Textures.TryGetValue(entry.File, out Texture2D? cached) && cached != null)
            return cached;
        string? path = ResolvePackFile(entry.File);
        if (path == null)
            return null;
        byte[]? bytes = ModFiles.ReadAllBytes(path);
        if (bytes == null || bytes.Length == 0)
            return null;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "ScheduleIArcade_LocalTex_" + Sanitize(entry.Names[0]), wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
        if (!ImageConversion.LoadImage(texture, bytes, true))
        {
            UnityEngine.Object.Destroy(texture);
            return null;
        }
        Created.Add(texture);
        Textures[entry.File] = texture;
        return texture;
    }

    private static Mesh? LoadMesh(LocalAssetEntry entry)
    {
        if (Meshes.TryGetValue(entry.File, out Mesh? cached) && cached != null)
            return cached;
        string? path = ResolvePackFile(entry.File);
        if (path == null)
            return null;
        byte[]? bytes = ModFiles.ReadAllBytes(path);
        if (bytes == null || bytes.Length == 0)
            return null;
        if (!GlbMeshParser.TryParse(bytes, out ParsedGlbMesh parsed, out string error))
        {
            MelonLogger.Warning("Arcade local asset pack: " + entry.File + " is not a usable GLB (" + error + ").");
            return null;
        }
        var mesh = new Mesh { name = "ScheduleIArcade_LocalMesh_" + Sanitize(parsed.Name) };
        var verts = new Vector3[parsed.VertexCount];
        for (int i = 0; i < parsed.VertexCount; i++)
            verts[i] = new Vector3(parsed.Positions[i * 3], parsed.Positions[i * 3 + 1], parsed.Positions[i * 3 + 2]);
        mesh.vertices = verts;
        if (parsed.Normals != null && parsed.Normals.Length == parsed.Positions.Length)
        {
            var nrm = new Vector3[parsed.VertexCount];
            for (int i = 0; i < parsed.VertexCount; i++)
                nrm[i] = new Vector3(parsed.Normals[i * 3], parsed.Normals[i * 3 + 1], parsed.Normals[i * 3 + 2]);
            mesh.normals = nrm;
        }
        else mesh.RecalculateNormals();
        if (parsed.Uvs != null && parsed.Uvs.Length == parsed.VertexCount * 2)
        {
            var uv = new Vector2[parsed.VertexCount];
            for (int i = 0; i < parsed.VertexCount; i++)
                uv[i] = new Vector2(parsed.Uvs[i * 2], parsed.Uvs[i * 2 + 1]);
            mesh.uv = uv;
        }
        mesh.triangles = parsed.Indices;
        mesh.RecalculateBounds();
        Created.Add(mesh);
        Meshes[entry.File] = mesh;
        return mesh;
    }

    private static string? ResolvePackFile(string relative)
    {
        if (_root == null || string.IsNullOrWhiteSpace(relative))
            return null;
        // Reject path escape so a pack cannot reach outside LocalAssets.
        string combined = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string rootFull = Path.GetFullPath(_root);
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return null;
        return ModFiles.ResolveExisting(combined);
    }

    private static void EnsureTemplateRoot()
    {
        if (_templateRoot != null)
            return;
        var go = new GameObject("ScheduleIArcade_LocalAssetTemplates");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.SetActive(false);
        _templateRoot = go.transform;
        Created.Add(go);
    }

    private static Material NewLitMaterial(string name, bool transparent)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Texture") ?? Shader.Find("Unlit/Color");
        var material = new Material(shader) { name = name };
        if (transparent)
            material.renderQueue = 3000;
        Created.Add(material);
        return material;
    }

    private static bool IsGlassName(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (name.Equals("window_glass_material", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Glass", StringComparison.OrdinalIgnoreCase)
                || name.Equals("glass", StringComparison.OrdinalIgnoreCase)
                || name.Equals("window_glass", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Sanitize(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
                chars[i] = '_';
        }
        return new string(chars);
    }

    private static string EncodingUtf8(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return string.Empty;
        int start = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}

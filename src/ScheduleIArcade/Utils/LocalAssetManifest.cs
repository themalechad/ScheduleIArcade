namespace ScheduleIArcade.Utils;

/// <summary>
/// Description of an optional local asset pack (PNG textures + GLB meshes) that may sit next to
/// the mod at Mods/ScheduleIArcade/LocalAssets. The pack is never shipped in the public ZIP.
/// This type has no Unity dependency so the manifest parser can be unit-tested headlessly.
/// </summary>
internal sealed class LocalAssetEntry
{
    public string Kind { get; init; } = string.Empty; // texture | mesh
    public string[] Names { get; init; } = Array.Empty<string>();
    public string File { get; init; } = string.Empty;
    public string? Texture { get; init; }
    public bool Transparent { get; init; }
}

internal sealed class LocalAssetManifest
{
    public string Version { get; init; } = string.Empty;
    public string Notice { get; init; } = string.Empty;
    public IReadOnlyList<LocalAssetEntry> Entries { get; init; } = Array.Empty<LocalAssetEntry>();

    public static bool TryParse(string json, out LocalAssetManifest manifest, out string error)
    {
        manifest = new LocalAssetManifest();
        error = string.Empty;
        try
        {
            var root = MiniJson.Obj(MiniJson.Parse(json));
            if (root == null)
            {
                error = "manifest.json is not an object.";
                return false;
            }
            var list = new List<LocalAssetEntry>();
            var entries = MiniJson.Arr(root, "entries") ?? new List<object?>();
            foreach (object? node in entries)
            {
                var obj = MiniJson.Obj(node);
                if (obj == null) continue;
                string kind = MiniJson.Str(obj, "kind") ?? string.Empty;
                string file = MiniJson.Str(obj, "file") ?? string.Empty;
                var names = new List<string>();
                var rawNames = MiniJson.Arr(obj, "names");
                if (rawNames != null)
                {
                    foreach (object? n in rawNames)
                    {
                        if (n is string s && !string.IsNullOrWhiteSpace(s))
                            names.Add(s);
                    }
                }
                if (names.Count == 0)
                {
                    string? single = MiniJson.Str(obj, "name");
                    if (!string.IsNullOrWhiteSpace(single))
                        names.Add(single!);
                }
                if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(file) || names.Count == 0)
                    continue;
                list.Add(new LocalAssetEntry
                {
                    Kind = kind.Trim().ToLowerInvariant(),
                    Names = names.ToArray(),
                    File = file.Trim(),
                    Texture = MiniJson.Str(obj, "texture"),
                    Transparent = MiniJson.Bool(obj, "transparent", false)
                });
            }
            manifest = new LocalAssetManifest
            {
                Version = MiniJson.Str(root, "version") ?? string.Empty,
                Notice = MiniJson.Str(root, "notice") ?? string.Empty,
                Entries = list
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public LocalAssetEntry? Find(string kind, string name)
    {
        foreach (LocalAssetEntry entry in Entries)
        {
            if (!string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (string n in entry.Names)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        return null;
    }

    public LocalAssetEntry? FindAny(string kind, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            LocalAssetEntry? entry = Find(kind, name);
            if (entry != null)
                return entry;
        }
        return null;
    }
}

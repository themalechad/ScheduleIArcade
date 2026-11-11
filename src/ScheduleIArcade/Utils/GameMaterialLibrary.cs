using MelonLoader;
using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Looks up Schedule I's own materials that are already loaded in the running game (for example
/// "Ceiling", "Skirting", "arcade red neon on mat" from the Main scene). Nothing is extracted,
/// copied to disk or packaged: the arcade renders with live references to the game's materials,
/// and falls back to its own procedural materials when a name is not loaded.
///
/// Shared game materials are never modified; callers that need a variant must clone at runtime.
/// </summary>
internal static class GameMaterialLibrary
{
    private static Dictionary<string, Material>? _byName;

    public static void Reset() => _byName = null;

    /// <summary>Returns the first loaded game material matching one of the names, in order.</summary>
    public static Material? Find(params string[] names)
    {
        EnsureIndex();
        foreach (string name in names)
        {
            if (_byName!.TryGetValue(name, out Material? material) && material != null)
                return material;
        }
        // Live scene missed: optional personal pack (no-op when LocalAssets is absent).
        return LocalAssetPack.TryCreateMaterial(names);
    }

    private static void EnsureIndex()
    {
        if (_byName != null)
            return;

        _byName = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                Material material = all[i];
                if (material == null || material.shader == null)
                    continue;
                string name = material.name;
                // Skip this mod's own runtime materials and per-renderer instances.
                if (string.IsNullOrEmpty(name) || name.StartsWith("ScheduleIArcade", StringComparison.Ordinal) || name.EndsWith("(Instance)", StringComparison.Ordinal))
                    continue;
                if (!_byName.ContainsKey(name))
                    _byName[name] = material;
            }
            MelonLogger.Msg($"Indexed {_byName.Count} loaded Schedule I materials for arcade interior dressing.");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not index loaded game materials; arcade keeps its own materials: {ex.Message}");
        }
    }
}

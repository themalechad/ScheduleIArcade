using MelonLoader.Utils;
using ScheduleIArcade.Utils;

namespace ScheduleIArcade.Core;

internal static class ModPaths
{
    public static string GameRoot => MelonEnvironment.GameRootDirectory;
    private static string? _modRoot;

    /// <summary>
    /// Mods/ScheduleIArcade, resolved from MelonEnvironment.ModsDirectory (absolute) or, failing that,
    /// next to this assembly. Case-insensitive and independent of the process working directory.
    /// </summary>
    public static string ModRoot => _modRoot ??= ResolveModRoot();
    public static string AssetsRoot => Path.Combine(ModRoot, "Assets");
    public static string GamesRoot => Path.Combine(ModRoot, "Games");
    public static string BundlePath => Path.Combine(AssetsRoot, Constants.BundleFileName);
    public static string RawSceneRoot => Path.Combine(AssetsRoot, "RawScene");
    public static string RawSceneMeshPath => Path.Combine(RawSceneRoot, "arcade_runtime_meshes.bin");
    public static string RawSceneLayoutPath => Path.Combine(RawSceneRoot, "arcade_layout.txt");
    public static string RawSceneTexturesRoot => Path.Combine(RawSceneRoot, "Textures");
    public static string AudioRoot => Path.Combine(AssetsRoot, "Audio");
    public static string PacManAssetsRoot => Path.Combine(AssetsRoot, "PacMan");
    public static string AmbiencePath => Path.Combine(AudioRoot, Constants.AmbienceFileName);
    /// <summary>Bundled Doom engine (separate GPL-2.0 assembly), Freedoom IWADs and licences.</summary>
    public static string DoomRoot => Path.Combine(ModRoot, "Doom");
    public static string DoomEnginePath => Path.Combine(DoomRoot, "ScheduleIArcade.Doom.dll");
    /// <summary>Doom save games/config live under UserData, never next to the game executable.</summary>
    public static string DoomDataRoot => Path.Combine(MelonEnvironment.UserDataDirectory, Constants.ModFolderName, "Doom");

    /// <summary>
    /// Returns the existing file for <paramref name="path"/> (case-insensitive, symlink-safe: the file is
    /// opened, never judged by FileInfo.Length), or the original path when nothing can be opened.
    /// </summary>
    public static string Find(string path) => ModFiles.ResolveExisting(path) ?? path;

    /// <summary>True if the file opens (follows Vortex symlinks; a 0-byte FileInfo.Length is not "missing").</summary>
    public static bool Exists(string path) => ModFiles.ResolveExisting(path) != null;

    private static string ResolveModRoot()
    {
        var candidates = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(MelonEnvironment.ModsDirectory))
                candidates.Add(Path.Combine(Path.GetFullPath(MelonEnvironment.ModsDirectory), Constants.ModFolderName));
        }
        catch
        {
        }

        try
        {
            string location = typeof(ModPaths).Assembly.Location;
            string? dir = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(dir))
                candidates.Add(Path.Combine(dir!, Constants.ModFolderName));
        }
        catch
        {
        }

        foreach (string candidate in candidates)
        {
            string? found = ModFiles.FindCaseInsensitiveDirectory(candidate);
            if (found != null)
                return found;
        }

        return candidates.Count > 0 ? candidates[0] : Path.GetFullPath(Path.Combine("Mods", Constants.ModFolderName));
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ModRoot);
        Directory.CreateDirectory(AssetsRoot);
        Directory.CreateDirectory(RawSceneRoot);
        Directory.CreateDirectory(RawSceneTexturesRoot);
        Directory.CreateDirectory(PacManAssetsRoot);
        Directory.CreateDirectory(AudioRoot);
        Directory.CreateDirectory(GamesRoot);
    }
}

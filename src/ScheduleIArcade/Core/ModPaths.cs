using MelonLoader.Utils;
using ScheduleIArcade.Utils;

namespace ScheduleIArcade.Core;

internal static class ModPaths
{
    public static string GameRoot => MelonEnvironment.GameRootDirectory;
    public static string ModRoot => Path.Combine(MelonEnvironment.ModsDirectory, Constants.ModFolderName);
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

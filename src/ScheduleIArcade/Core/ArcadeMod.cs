using MelonLoader;
using S1API.Lifecycle;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Services;
using ScheduleIArcade.Utils;

[assembly: MelonInfo(typeof(ScheduleIArcade.Core.ArcadeMod), Constants.ModName, Constants.ModVersion, Constants.ModAuthor)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace ScheduleIArcade.Core;

public sealed class ArcadeMod : MelonMod
{
    private ArcadePreferences? _preferences;
    private ArcadeRuntime? _runtime;

    public override void OnInitializeMelon()
    {
        ModPaths.EnsureDirectories();
        _preferences = new ArcadePreferences();
        _runtime = new ArcadeRuntime(_preferences);

        GameLifecycle.OnLoadComplete += OnLoadComplete;
        GameLifecycle.OnPreSceneChange += OnPreSceneChange;

        LoggerInstance.Msg($"{Constants.ModName} {Constants.ModVersion} initialized.");
    }

    private void OnLoadComplete() => _runtime?.InitializeWorld();
    private void OnPreSceneChange() => _runtime?.DisposeWorld();

    public override void OnUpdate() => _runtime?.Tick();
    public override void OnGUI() => _runtime?.OnGUI();

    public override void OnApplicationQuit()
    {
        GameLifecycle.OnLoadComplete -= OnLoadComplete;
        GameLifecycle.OnPreSceneChange -= OnPreSceneChange;
        _runtime?.Dispose();
        _runtime = null;
    }
}

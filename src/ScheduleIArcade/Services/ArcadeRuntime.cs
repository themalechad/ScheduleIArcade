using MelonLoader;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Games;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class ArcadeRuntime : IDisposable
{
    private readonly ArcadePreferences _preferences;
    private readonly AssetBundleService _assets = new();
    private readonly ArcadeGameRegistry _games = new();
    private readonly CabinetRegistryService _cabinets = new();

    private ArcadeLocationService? _location;
    private ArcadeSessionService? _session;
    private CabinetInteractionService? _interaction;
    private ArcadeEntranceService? _entrance;
    private ArcadeExitInteractionService? _exit;
    private VanillaDoorInteractionHookService? _doorHooks;
    private ArcadeBuildSurfaceService? _buildSurfaces;
    private ArcadeBuildPlacementHookService? _buildPlacementHooks;
    private ArcadeAmbienceService? _ambience;
    private ArcadeCustomAssetPlacementService? _customAssets;

    public ArcadeRuntime(ArcadePreferences preferences) => _preferences = preferences;

    public void InitializeWorld()
    {
        DisposeWorld();

        _games.Register("test_pattern", () => new TestPatternArcadeGame());
        _games.Register("arcade_tv", () => new ArcadeTvGame());
        _games.Register("pacman", () => new PacmanCocktailGame());
        _location = new ArcadeLocationService(_assets, _preferences);
        _session = new ArcadeSessionService(_games);

        // AssetBundle loading is optional for this test build. If the real custom-room bundle is
        // missing, ArcadeLocationService generates a collidable procedural arcade so the complete
        // entrance -> cabinet -> exit flow is testable immediately.
        _assets.Load();
        if (!_location.Spawn() || _location.RoomInstance == null)
        {
            MelonLogger.Error("Arcade world bootstrap stopped because neither the AssetBundle nor the procedural fallback room could be loaded.");
            return;
        }

        _cabinets.Scan(_location.RoomInstance);

        _entrance = new ArcadeEntranceService(_location, _preferences);
        _entrance.Initialize();
        _doorHooks = new VanillaDoorInteractionHookService(_entrance);
        _doorHooks.Initialize();
        _interaction = new CabinetInteractionService(_cabinets, _session, () => _entrance.IsInsideArcade);
        _customAssets = new ArcadeCustomAssetPlacementService(
            _location,
            () => _entrance.IsInsideArcade,
            () => _session.IsPlaying);
        _exit = new ArcadeExitInteractionService(_location, _entrance, _session);

        _ambience = new ArcadeAmbienceService(_location, _preferences, () => _entrance.IsInsideArcade);
        _ambience.Initialize();

        // Building placement uses a native owned Property ONLY as a safe vanilla ownership template.
        // The arcade keeps its own Grid GUID, and placed items are detached from the template
        // property's culling list immediately after creation. This avoids registering a synthetic
        // Property in Schedule I's global lists, which was the source of the GetNearestProperty
        // / RecalculateCurrentProperty NullReference spam.
        _buildSurfaces = new ArcadeBuildSurfaceService(_location, () => _entrance.IsInsideArcade);
        _buildPlacementHooks = new ArcadeBuildPlacementHookService(_buildSurfaces);
        _buildPlacementHooks.Initialize();

        if (_location.UsingRawScene)
            MelonLogger.Msg("Schedule I Arcade runtime initialized with REAL FBX GEOMETRY + TEXTURES. Use the vanilla Arcade door to enter.");
        else if (_location.UsingProceduralFallback)
            MelonLogger.Msg("Schedule I Arcade runtime initialized with PROCEDURAL TEST ROOM. Use the vanilla Arcade door to enter.");
        else
            MelonLogger.Msg("Schedule I Arcade runtime initialized with custom AssetBundle room. Use the vanilla Arcade door to enter.");
    }

    public void Tick()
    {
        _entrance?.Tick();
        _interaction?.Tick();
        _customAssets?.Tick();
        _exit?.Tick();
        _buildSurfaces?.Tick();
        _ambience?.Tick();
        _session?.Tick(Time.deltaTime);
    }

    public void OnGUI()
    {
        _entrance?.OnGUI();
        _interaction?.OnGUI();
        _customAssets?.OnGUI();
        _exit?.OnGUI();
    }

    public void DisposeWorld()
    {
        _session?.Dispose();
        _customAssets?.Dispose();
        _doorHooks?.Dispose();
        _entrance?.Dispose();
        _buildPlacementHooks?.Dispose();
        _buildSurfaces?.Dispose();
        _ambience?.Dispose();
        _location?.Dispose();
        _session = null;
        _doorHooks = null;
        _location = null;
        _interaction = null;
        _customAssets = null;
        _entrance = null;
        _exit = null;
        _buildSurfaces = null;
        _buildPlacementHooks = null;
        _ambience = null;
    }

    public void Dispose()
    {
        DisposeWorld();
        _assets.Dispose();
    }
}

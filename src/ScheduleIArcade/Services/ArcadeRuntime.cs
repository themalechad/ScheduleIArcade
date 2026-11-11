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
    private ArcadeWalkInService? _walkIn;
    private ArcadeInteriorDressingService? _dressing;
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
        _games.Register("doom1", () => new EmbeddedDoomArcadeGame(DoomCabinetEdition.Doom1));
        _games.Register("doom2", () => new EmbeddedDoomArcadeGame(DoomCabinetEdition.Doom2));
        _games.Register("space_cadet_pinball", () => new SpaceCadetPinballGame());
        _games.Register("air_hockey", () => new Games.AirHockey.AirHockeyGame());
        _location = new ArcadeLocationService(_assets, _preferences);
        _session = new ArcadeSessionService(_games, _preferences);

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
        LogDoomDependencies();

        _dressing = new ArcadeInteriorDressingService();
        try { _dressing.Apply(_location.RoomInstance, _location.UsingRawScene); }
        catch (Exception ex) { MelonLogger.Warning($"Arcade interior dressing skipped: {ex.Message}"); }

        // The arcade is a walk-in part of the real Arcade building: no teleport, no knock menu.
        _walkIn = new ArcadeWalkInService(
            _location,
            _preferences,
            () => _session.IsPlaying || _interaction?.IsLookingAtCabinet == true || _customAssets?.IsHoldingAsset == true);
        _walkIn.Initialize();
        ArcadeWalkInService walkIn = _walkIn;
        _interaction = new CabinetInteractionService(_cabinets, _session, () => walkIn.IsInsideArcade);
        _customAssets = new ArcadeCustomAssetPlacementService(
            _location,
            () => walkIn.IsInsideArcade,
            () => _session.IsPlaying,
            () => _interaction?.IsLookingAtCabinet == true);

        _ambience = new ArcadeAmbienceService(_location, _preferences, () => walkIn.IsInsideArcade);
        _ambience.Initialize();

        // Building placement uses a native owned Property ONLY as a safe vanilla ownership template.
        // The arcade keeps its own Grid GUID, and placed items are detached from the template
        // property's culling list immediately after creation. This avoids registering a synthetic
        // Property in Schedule I's global lists, which was the source of the GetNearestProperty
        // / RecalculateCurrentProperty NullReference spam.
        _buildSurfaces = new ArcadeBuildSurfaceService(_location, () => walkIn.IsInsideArcade);
        _buildPlacementHooks = new ArcadeBuildPlacementHookService(_buildSurfaces);
        _buildPlacementHooks.Initialize();

        if (_location.UsingRawScene)
            MelonLogger.Msg(_walkIn.IsWalkInReady
                ? "Schedule I Arcade runtime initialized. Walk in through the Arcade's glass corner doors (press E on a door to open it)."
                : "Schedule I Arcade runtime initialized, but the walk-in entrance could not be prepared; see the errors above.");
        else if (_location.UsingProceduralFallback)
            MelonLogger.Msg("Schedule I Arcade runtime initialized with PROCEDURAL TEST ROOM (not connected to the Arcade building).");
        else
            MelonLogger.Msg("Schedule I Arcade runtime initialized with custom AssetBundle room (not connected to the Arcade building).");
    }

    public void Tick()
    {
        _walkIn?.Tick();
        _dressing?.Tick();
        _interaction?.Tick();
        _customAssets?.Tick();
        _buildSurfaces?.Tick();
        _ambience?.Tick();
        _session?.Tick(Time.deltaTime);
    }

    public void OnGUI()
    {
        _walkIn?.OnGUI();
        _interaction?.OnGUI();
        _customAssets?.OnGUI();
    }

    public void DisposeWorld()
    {
        _session?.Dispose();
        _customAssets?.Dispose();
        _walkIn?.Dispose();
        _dressing?.Dispose();
        _buildPlacementHooks?.Dispose();
        _buildSurfaces?.Dispose();
        _ambience?.Dispose();
        _location?.Dispose();
        _session = null;
        _walkIn = null;
        _dressing = null;
        _location = null;
        _interaction = null;
        _customAssets = null;
        _buildSurfaces = null;
        _buildPlacementHooks = null;
        _ambience = null;
    }

    private static void LogDoomDependencies()
    {
        foreach (DoomCabinetEdition edition in new[] { DoomCabinetEdition.Doom1, DoomCabinetEdition.Doom2 })
        {
            string cabinet = edition == DoomCabinetEdition.Doom1 ? "Doom cabinet 1" : "Doom cabinet 2";
            if (EmbeddedDoomBridge.IsReady(edition, out string detail))
            {
                string? wad = EmbeddedDoomBridge.ResolveWad(edition, out string label);
                MelonLogger.Msg($"{cabinet}: bundled Doom engine ready with {label} ({Path.GetFileName(wad)}).");
            }
            else
            {
                MelonLogger.Warning($"{cabinet} is OUT OF ORDER (no charge): {detail}");
            }
        }
    }

    public void Dispose()
    {
        DisposeWorld();
        _assets.Dispose();
    }
}

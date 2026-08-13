using MelonLoader;

namespace ScheduleIArcade.Services;

/// <summary>
/// Hooks vanilla GridItem creation for final arcade-only visibility diagnostics/guarding. The
/// dedicated arcade Property/Container now lets vanilla keep the correct parent naturally.
/// </summary>
internal sealed class ArcadeBuildPlacementHookService : IDisposable
{
    private const string HarmonyId = "GrandmasAnkles.ScheduleIArcade.BuildPlacementHooks";

    private static ArcadeBuildSurfaceService? _activeBuildSurfaceService;
    private readonly global::HarmonyLib.Harmony _harmony = new global::HarmonyLib.Harmony(HarmonyId);
    private bool _patched;

    public ArcadeBuildPlacementHookService(ArcadeBuildSurfaceService buildSurfaceService)
    {
        _activeBuildSurfaceService = buildSurfaceService;
    }

    public void Initialize()
    {
        if (_patched)
            return;

        try
        {
            MethodInfo? target = typeof(Il2CppScheduleOne.Building.BuildManager)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "CreateGridItem" && m.GetParameters().Length >= 4);

            MethodInfo? postfix = typeof(ArcadeBuildPlacementHookService).GetMethod(
                nameof(CreateGridItemPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo? gridCheckTarget = typeof(Il2CppScheduleOne.Building.BuildUpdate_Grid)
                .GetMethod("CheckIntersections", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? gridCheckPostfix = typeof(ArcadeBuildPlacementHookService).GetMethod(
                nameof(CheckIntersectionsPostfix), BindingFlags.Static | BindingFlags.NonPublic);

            if (target == null || postfix == null || gridCheckTarget == null || gridCheckPostfix == null)
            {
                MelonLogger.Warning("Arcade build hook: a native placement wrapper was not found. Dedicated arcade placement can still work, but the final validity guard will be unavailable.");
                return;
            }

            _harmony.Patch(target, postfix: new global::HarmonyLib.HarmonyMethod(postfix));
            _harmony.Patch(gridCheckTarget, postfix: new global::HarmonyLib.HarmonyMethod(gridCheckPostfix));
            _patched = true;
            MelonLogger.Msg("Installed arcade GridItem visibility and native-grid validity hooks.");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Arcade build hook could not patch BuildManager.CreateGridItem: {ex.Message}");
        }
    }

    private static void CreateGridItemPostfix(
        Il2CppScheduleOne.Tiles.Grid grid,
        Il2CppScheduleOne.EntityFramework.GridItem __result)
    {
        if (__result == null || grid == null)
            return;

        _activeBuildSurfaceService?.NotifyGridItemCreated(grid, __result);
    }

    private static void CheckIntersectionsPostfix(Il2CppScheduleOne.Building.BuildUpdate_Grid __instance)
    {
        _activeBuildSurfaceService?.RestoreNativeArcadePlacementValidity(__instance);
    }

    public void Dispose()
    {
        _activeBuildSurfaceService = null;

        if (!_patched)
            return;

        try
        {
            _harmony.UnpatchSelf();
        }
        catch
        {
        }

        _patched = false;
    }
}

using MelonLoader;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Event-driven hooks verified against the user's current Assembly-CSharp dump.
/// No world scans or recurring object discovery are used.
/// </summary>
internal sealed class VanillaDoorInteractionHookService : IDisposable
{
    private const string HarmonyId = "GrandmasAnkles.ScheduleIArcade.DoorHooks";

    private static ArcadeEntranceService? _activeEntrance;
    private readonly global::HarmonyLib.Harmony _harmony = new global::HarmonyLib.Harmony(HarmonyId);
    private bool _patched;

    public VanillaDoorInteractionHookService(ArcadeEntranceService entrance)
    {
        _activeEntrance = entrance;
    }

    public void Initialize()
    {
        if (_patched)
            return;

        int patchCount = 0;

        // Verified in the supplied Assembly-CSharp dump:
        // ScheduleOne.Interaction.InteractableObject.StartInteract()
        // ScheduleOne.Doors.StaticDoor.Interacted()
        // ScheduleOne.Doors.StaticDoor.Knock()
        // ScheduleOne.Building.Doors.DoorKnocker.Knock()
        // ScheduleOne.UI.NPCSummonMenu.Open(List<NPC>, Action<NPC>)
        patchCount += TryPatchPostfix(typeof(Il2CppScheduleOne.Interaction.InteractableObject), "StartInteract", nameof(InteractableStartPostfix)) ? 1 : 0;
        patchCount += TryPatchPostfix(typeof(Il2CppScheduleOne.Doors.StaticDoor), "Interacted", nameof(StaticDoorInteractedPostfix)) ? 1 : 0;
        patchCount += TryPatchPostfix(typeof(Il2CppScheduleOne.Doors.StaticDoor), "Knock", nameof(StaticDoorKnockPostfix)) ? 1 : 0;
        patchCount += TryPatchPostfix(typeof(Il2CppScheduleOne.Building.Doors.DoorKnocker), "Knock", nameof(DoorKnockerKnockPostfix)) ? 1 : 0;
        patchCount += TryPatchPostfix(typeof(Il2CppScheduleOne.UI.NPCSummonMenu), "Open", nameof(NpcSummonMenuOpenPostfix)) ? 1 : 0;

        _patched = patchCount > 0;
        if (_patched)
            MelonLogger.Msg($"Installed {patchCount} Assembly-CSharp-verified arcade interaction hook(s).");
        else
            MelonLogger.Error("Could not install any arcade interaction hooks. The current IL2CPP wrappers did not match the supplied Assembly-CSharp dump.");
    }

    private bool TryPatchPostfix(Type targetType, string methodName, string postfixName)
    {
        try
        {
            MethodInfo? targetMethod = FindTargetMethod(targetType, methodName);
            MethodInfo? postfixMethod = typeof(VanillaDoorInteractionHookService).GetMethod(
                postfixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (targetMethod == null || postfixMethod == null)
            {
                MelonLogger.Warning($"Arcade hook method was not found: {targetType.FullName}.{methodName}");
                return false;
            }

            _harmony.Patch(targetMethod, postfix: new global::HarmonyLib.HarmonyMethod(postfixMethod));
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Failed to patch {targetType.FullName}.{methodName}: {ex.Message}");
            return false;
        }
    }

    private static MethodInfo? FindTargetMethod(Type type, string methodName)
    {
        MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == methodName)
            .ToArray();
        if (methods.Length == 1)
            return methods[0];

        // All verified targets except NPCSummonMenu.Open are parameterless in the supplied dump.
        // Open has one overload in the dump, so if multiple wrapper helpers exist choose the first
        // actual method with two parameters.
        if (methodName == "Open")
            return methods.FirstOrDefault(m => m.GetParameters().Length == 2);

        return methods.FirstOrDefault(m => m.GetParameters().Length == 0);
    }

    private static void InteractableStartPostfix(object __instance)
    {
        if (__instance is MonoBehaviour behaviour)
            _activeEntrance?.NotifyVanillaInteractableStarted(behaviour);
    }

    private static void StaticDoorInteractedPostfix(object __instance)
    {
        if (__instance is MonoBehaviour behaviour)
            _activeEntrance?.NotifyVanillaStaticDoorInteracted(behaviour, "StaticDoor.Interacted");
    }

    private static void StaticDoorKnockPostfix(object __instance)
    {
        if (__instance is MonoBehaviour behaviour)
            _activeEntrance?.NotifyVanillaStaticDoorInteracted(behaviour, "StaticDoor.Knock");
    }

    private static void DoorKnockerKnockPostfix(object __instance)
    {
        if (__instance is MonoBehaviour behaviour)
            _activeEntrance?.NotifyVanillaDoorKnockerUsed(behaviour);
    }

    private static void NpcSummonMenuOpenPostfix(object __instance)
    {
        if (__instance is MonoBehaviour behaviour)
            _activeEntrance?.NotifyNpcSummonMenuOpened(behaviour);
    }

    public void Dispose()
    {
        _activeEntrance = null;

        if (!_patched)
            return;

        try
        {
            _harmony.UnpatchSelf();
        }
        catch
        {
            // Game teardown; nothing useful to recover.
        }

        _patched = false;
    }
}

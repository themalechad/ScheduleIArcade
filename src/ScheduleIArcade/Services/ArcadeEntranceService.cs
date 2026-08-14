using MelonLoader;
using S1API.Entities;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Utils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ScheduleIArcade.Services;

/// <summary>
/// Connects the custom arcade interior to Schedule I's real exterior Arcade door.
/// Door detection is entirely event-driven: Harmony notifies this service only when
/// Schedule I itself processes a StaticDoor interaction or DoorKnocker knock.
/// </summary>
internal sealed class ArcadeEntranceService : IDisposable
{
    private const float QuickChoiceDelaySeconds = 0.75f; // Fast custom choice; if vanilla NPC UI opens later, we hand off to it.
    private const float DoorSignalDebounceSeconds = 0.20f;
    private const float VanillaSummonDurationSeconds = 8.0f;

    private readonly ArcadeLocationService _location;
    private readonly ArcadePreferences _preferences;

    private MonoBehaviour? _arcadeDoor;
    private Transform? _arcadeDoorTransform;
    private MonoBehaviour? _npcSummonMenu;
    private GameObject? _nativeEntryClone;

    private float _pendingUntil;
    private float _suppressDoorUntil;
    private float _suppressLateVanillaMenuUntil;
    private bool _awaitingDoorMenu;
    private bool _fallbackChoiceVisible;
    private bool _npcMenuLookupAttempted;
    private bool _fallbackInputCaptured;
    private bool _fallbackMovementStateCaptured;
    private bool _previousCanMove = true;
    private bool _previousCanJump = true;
    private CursorLockMode _previousCursorLockState = CursorLockMode.Locked;
    private bool _previousCursorVisible;
    private float _lastDoorSignalTime = -999f;
    private Vector3? _outsideReturnPosition;

    public ArcadeEntranceService(ArcadeLocationService location, ArcadePreferences preferences)
    {
        _location = location;
        _preferences = preferences;
    }

    public bool IsInsideArcade { get; private set; }

    public void Initialize()
    {
        ResolveNpcSummonMenu();
        MelonLogger.Msg("Arcade entrance service ready. Waiting for the game's real door interaction event.");
    }

    public void Tick()
    {
        CleanupNativeEntryIfMenuClosed();

        if (IsInsideArcade)
        {
            // The vanilla StaticDoor knock coroutine can finish several seconds after our fast
            // Enter Arcade choice was selected. If its delayed NPCSummonMenu opens while the
            // player is already inside the custom arcade, close it immediately so it cannot
            // remain as an invisible/late UI blocker when the player returns outside.
            if (IsNpcSummonMenuOpen())
            {
                CloseNativeNpcMenu();
                DestroyNativeEntry();
            }

            EnsureInsideArcadeSafePosition();
            return;
        }

        if (Time.unscaledTime < _suppressDoorUntil)
            return;

        if (_awaitingDoorMenu)
        {
            if (TryInjectNativeEnterChoice())
            {
                _awaitingDoorMenu = false;
                _fallbackChoiceVisible = false;
            }
            else if (Time.unscaledTime >= _pendingUntil)
            {
                // Do not make the player wait through the full vanilla summon duration just to enter.
                // If the NPC menu has not appeared quickly, show our Arcade choice now. If the
                // vanilla NPC menu opens later, the code below replaces this fallback with the
                // injected native Enter Arcade entry.
                _awaitingDoorMenu = false;
                ShowFallbackChoice();
            }
        }

        // If the native menu activates late, prefer it. Leave player input deactivated because
        // NPCSummonMenu owns that state until it closes; only stop forcing our fallback cursor.
        if (_fallbackChoiceVisible && IsNpcSummonMenuOpen() && TryInjectNativeEnterChoice())
            HideFallbackChoice(restoreGameplay: false);

        if (_fallbackChoiceVisible)
        {
            EnsureFallbackMouseFree();

            // Mouse is the intended path, but keyboard fallback makes the prompt impossible to
            // get stuck on if another mod interferes with pointer events.
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E))
                EnterArcade();
            else if (Input.GetKeyDown(KeyCode.Escape))
                CancelFallbackChoice();
        }
    }

    /// <summary>
    /// Called only from the Harmony postfix for Schedule I's StaticDoor.Interacted method.
    /// </summary>
    internal void NotifyVanillaStaticDoorInteracted(MonoBehaviour door, string source = "StaticDoor.Interacted")
    {
        if (door == null || IsInsideArcade || Time.unscaledTime < _suppressDoorUntil)
            return;

        if (!TryAcceptArcadeDoor(door))
            return;

        // StaticDoor.Knock, StaticDoor.Interacted and InteractableObject.StartInteract all fire
        // for the same click in this build. Debounce them so one knock owns one quick-choice
        // window instead of continually resetting the timer.
        if (Time.unscaledTime - _lastDoorSignalTime > DoorSignalDebounceSeconds)
        {
            _lastDoorSignalTime = Time.unscaledTime;
            _suppressLateVanillaMenuUntil = 0f; // This is a fresh knock; any menu it opens belongs to this interaction.
            CaptureExteriorReturnPosition();
            HideFallbackChoice(restoreGameplay: false);
            _awaitingDoorMenu = true;
            _pendingUntil = Time.unscaledTime + QuickChoiceDelaySeconds;

            // The singleton may not have existed at world bootstrap but can exist now.
            _npcMenuLookupAttempted = false;
            ResolveNpcSummonMenu();

            // If the vanilla NPC menu is already open, inject immediately. Otherwise give it a short
            // chance to open before showing our faster mouse-selectable fallback.
            if (TryInjectNativeEnterChoice())
                _awaitingDoorMenu = false;
        }

        MelonLogger.Msg($"Vanilla Arcade door signal ({source}) detected; Enter Arcade will be available after a short delay if the NPC menu does not open first.");
    }

    /// <summary>
    /// Verified public interaction entry point from the supplied Assembly-CSharp dump.
    /// This catches the exact InteractableObject the player actually used without scanning the world.
    /// </summary>
    internal void NotifyVanillaInteractableStarted(MonoBehaviour interactable)
    {
        if (interactable == null || IsInsideArcade || Time.unscaledTime < _suppressDoorUntil)
            return;

        // StaticDoor owns a public IntObj field in the supplied dump. In normal scenes the
        // InteractableObject is on the same object or a nearby parent in the door hierarchy.
        MonoBehaviour? door = FindRelatedStaticDoor(interactable);
        if (door != null)
        {
            NotifyVanillaStaticDoorInteracted(door, "InteractableObject.StartInteract");
            return;
        }

        string path = BuildHierarchyPath(interactable.transform);
        if (!ContainsArcade(path))
            return;

        // Some scene doors use a plain interactable + DoorKnocker rather than StaticDoor.
        // For a hierarchy explicitly named Arcade, the exact interacted transform is enough
        // to anchor the exterior return point and show the fallback choice.
        if (_arcadeDoorTransform == null)
        {
            _arcadeDoorTransform = interactable.transform;
            MelonLogger.Msg($"Bound Arcade entrance from InteractableObject path='{path}'.");
        }

        _suppressLateVanillaMenuUntil = 0f;
        CaptureExteriorReturnPosition();
        HideFallbackChoice(restoreGameplay: false);
        _awaitingDoorMenu = true;
        _pendingUntil = Time.unscaledTime + QuickChoiceDelaySeconds;
    }

    /// <summary>
    /// Postfix for NPCSummonMenu.Open, whose exact two-parameter signature is present in the
    /// supplied dump. This lets us inject after vanilla has populated its entries.
    /// </summary>
    internal void NotifyNpcSummonMenuOpened(MonoBehaviour menu)
    {
        if (menu == null)
            return;

        _npcSummonMenu = menu;
        _npcMenuLookupAttempted = true;

        // Our fast fallback can teleport the player before StaticDoor's 8-second vanilla summon
        // coroutine completes. A delayed menu from that old knock must never be left open: it can
        // capture the cursor/input and make the exterior door appear permanently unusable later.
        if (IsInsideArcade || (!_awaitingDoorMenu && Time.unscaledTime < _suppressLateVanillaMenuUntil))
        {
            CloseNativeNpcMenu();
            DestroyNativeEntry();
            if (_preferences.DebugLogs)
                MelonLogger.Msg("Closed delayed vanilla NPC summon menu from the previous Arcade knock.");
            return;
        }

        if (!_awaitingDoorMenu)
            return;

        if (TryInjectNativeEnterChoice())
        {
            _awaitingDoorMenu = false;
            HideFallbackChoice(restoreGameplay: false);
        }
    }

    /// <summary>
    /// Compatibility path for game builds/doors that only expose DoorKnocker.Knock.
    /// There is no NPCSummonMenu relationship on DoorKnocker, so the custom choice is shown.
    /// </summary>
    internal void NotifyVanillaDoorKnockerUsed(MonoBehaviour knocker)
    {
        if (knocker == null || IsInsideArcade || Time.unscaledTime < _suppressDoorUntil)
            return;

        // The current Arcade uses StaticDoor and is already identified by BuildingName=Arcade.
        // Keep DoorKnocker only as a compatibility path, but never bind arbitrary doors anymore.
        string path = BuildHierarchyPath(knocker.transform);
        if (_arcadeDoorTransform != null)
        {
            if (!IsSameOrRelatedTransform(knocker.transform, _arcadeDoorTransform))
                return;
        }
        else if (!ContainsArcade(path))
        {
            return;
        }
        else
        {
            _arcadeDoorTransform = knocker.transform;
            MelonLogger.Msg($"Bound Arcade entrance from DoorKnocker path='{path}'.");
        }

        if (Time.unscaledTime - _lastDoorSignalTime <= DoorSignalDebounceSeconds)
            return;

        _lastDoorSignalTime = Time.unscaledTime;
        _suppressLateVanillaMenuUntil = 0f;
        CaptureExteriorReturnPosition();
        HideFallbackChoice(restoreGameplay: false);
        _awaitingDoorMenu = true;
        _pendingUntil = Time.unscaledTime + QuickChoiceDelaySeconds;
        MelonLogger.Msg("Arcade DoorKnocker signal detected; preparing fast Enter Arcade choice.");
    }

    public void OnGUI()
    {
        if (!_fallbackChoiceVisible || IsInsideArcade)
            return;

        EnsureFallbackMouseFree();

        ArcadePromptUi.Choice choice = ArcadePromptUi.DrawChoiceCard(
            "ARCADE ENTRANCE",
            "ENTER THE ARCADE?",
            "Open the custom arcade interior through this door.",
            "ENTER ARCADE",
            "CANCEL",
            new Color(0.15f, 0.78f, 1.0f, 1f));
        if (choice == ArcadePromptUi.Choice.Primary)
            EnterArcade();
        else if (choice == ArcadePromptUi.Choice.Secondary)
            CancelFallbackChoice();
    }

    public bool EnterArcade()
    {
        if (IsInsideArcade)
            return true;

        Player? player = Player.Local;
        if (player == null || _location.SpawnMarker == null)
        {
            MelonLogger.Warning("Cannot enter custom arcade: player or ARCADE_SPAWN marker is unavailable.");
            return false;
        }

        CaptureExteriorReturnPosition();

        // The original procedural test room lived at Y=300, which made a failed floor contact
        // look like the player had been launched into the sky. Anchor the test interior far
        // away horizontally but at the same elevation as the real Arcade entrance instead.
        Vector3 exteriorAnchor = _outsideReturnPosition ?? player.Position;
        if (!_location.PrepareForEntry(exteriorAnchor) || _location.SpawnMarker == null)
        {
            MelonLogger.Warning("Cannot enter custom arcade: failed to prepare a safe interior spawn.");
            return false;
        }

        // StaticDoor.Knock owns an 8-second coroutine. Because our entry choice is intentionally
        // much faster, remember that a stale NPC menu may still try to open from this old knock.
        _suppressLateVanillaMenuUntil = Time.unscaledTime + VanillaSummonDurationSeconds + 1.0f;
        CloseNativeNpcMenu();
        HideFallbackChoice(restoreGameplay: true);
        _awaitingDoorMenu = false;
        DestroyNativeEntry();

        Vector3 interiorSpawn = _location.SpawnMarker.position;
        TeleportPlayerWithController(player, interiorSpawn, "enter arcade");
        IsInsideArcade = true;
        _nextInsideSafetyCheck = 0f;
        MelonLogger.Msg($"Entered the custom arcade through the real interior door spawn {interiorSpawn}.");
        return true;
    }

    public bool ExitArcade()
    {
        if (!IsInsideArcade)
            return false;

        Player? player = Player.Local;
        if (player == null)
            return false;

        Vector3 destination;
        if (_outsideReturnPosition.HasValue)
        {
            destination = _outsideReturnPosition.Value;
        }
        else if (TryGetDoorAccessPoint(out Vector3 accessPoint))
        {
            destination = accessPoint;
        }
        else if (_arcadeDoorTransform != null)
        {
            destination = _arcadeDoorTransform.position + _arcadeDoorTransform.forward * 1.2f;
        }
        else
        {
            MelonLogger.Warning("Cannot leave custom arcade: exterior return point is unavailable.");
            return false;
        }

        return CompleteExit(destination, "Exited the custom arcade and re-armed the vanilla Arcade door for another knock.");
    }

    public bool ExitArcadeToWestSide()
    {
        // Requested rear-door destination: the western side behind the real arcade building.
        // Keep this separate from the front-door return point so the interior door behaves like
        // a real second exit instead of snapping the player back to the entrance.
        return CompleteExit(
            // Shift back 1.25m from the original west-facing anchor. The previous point put
            // the player too far forward of the real rear door after controller alignment.
            new Vector3(-58.65f, -3.0f, 146.1f),
            "Exited the custom arcade through the rear door to the west side of the building.",
            -90f);
    }

    private bool CompleteExit(Vector3 destination, string logMessage, float? facingYaw = null)
    {
        if (!IsInsideArcade)
            return false;

        Player? player = Player.Local;
        if (player == null)
            return false;

        // Clear any delayed vanilla summon UI before returning to the exterior and explicitly
        // re-arm the real StaticDoor interaction. This makes enter -> exit -> knock -> enter a
        // repeatable loop instead of a one-shot interaction.
        CloseNativeNpcMenu();
        DestroyNativeEntry();
        HideFallbackChoice(restoreGameplay: true);
        _awaitingDoorMenu = false;

        // The native teleport keeps the controller grounded when the west-side destination is
        // reached from the pocket interior. It also safely handles the front-door return point.
        TeleportPlayerWithController(player, destination, "exit arcade");
        if (facingYaw.HasValue)
            FacePlayer(facingYaw.Value);
        Physics.SyncTransforms();
        IsInsideArcade = false;
        _outsideReturnPosition = null;
        _lastDoorSignalTime = -999f;
        _suppressDoorUntil = Time.unscaledTime + 0.20f;
        RearmExteriorDoorInteraction();
        MelonLogger.Msg(logMessage);
        return true;
    }

    private static void FacePlayer(float yaw)
    {
        try
        {
            Camera? mainCamera = Camera.main;
            if (mainCamera != null)
                mainCamera.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
        catch
        {
            // The destination remains correct even if a camera wrapper changes between builds.
        }
    }

    private void RearmExteriorDoorInteraction()
    {
        if (_arcadeDoor == null)
            return;

        try
        {
            // Use reflection here rather than taking new compile-time dependencies on generated
            // wrapper members. The supplied Assembly-CSharp dump confirms these names exist, while
            // MelonLoader wrapper exposure can vary between game builds.
            ReflectionUtil.TrySetMemberValue(_arcadeDoor, "Usable", true);
            ReflectionUtil.TrySetMemberValue(_arcadeDoor, "CanKnock", true);

            object? intObj = ReflectionUtil.GetMemberValue(_arcadeDoor, "IntObj");
            // EInteractableState.Default == 0 in the supplied dump.
            ReflectionUtil.TryInvokeWithDefaultEnumValue(intObj, "SetInteractableState", 0);
        }
        catch (Exception ex)
        {
            if (_preferences.DebugLogs)
                MelonLogger.Warning($"Could not explicitly re-arm Arcade StaticDoor state: {ex.Message}");
        }
    }

    private bool TryAcceptArcadeDoor(MonoBehaviour door)
    {
        if (_arcadeDoor == door)
            return true;

        if (_arcadeDoor != null)
            return false;

        object? building = ReflectionUtil.GetMemberValue(door, "Building");
        string buildingName = ReflectionUtil.GetString(building, "BuildingName");
        string path = BuildHierarchyPath(door.transform);

        // Captured from the user's current game build:
        // BuildingName='Arcade'
        // Map/Hyland Point/Region_Northtown/Arcade (1)/arcade/CornerDoor/Metal Glass Door (Static)
        // Do not accept unrelated doors anymore.
        if (!string.Equals(buildingName, "Arcade", StringComparison.OrdinalIgnoreCase) && !ContainsArcade(path))
            return false;

        _arcadeDoor = door;
        _arcadeDoorTransform = door.transform;
        MelonLogger.Msg($"Bound vanilla Arcade entrance: building='{buildingName}' path='{path}'.");
        return true;
    }

    private static MonoBehaviour? FindRelatedStaticDoor(MonoBehaviour interactable)
    {
        Transform? current = interactable.transform;
        int depth = 0;
        while (current != null && depth++ < 6)
        {
            MonoBehaviour[] behaviours = current.GetComponents<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null)
                    continue;

                string typeName = behaviour.GetType().FullName ?? behaviour.GetType().Name;
                if (typeName.EndsWith(".StaticDoor", StringComparison.Ordinal) ||
                    typeName.EndsWith(".DealerStaticDoor", StringComparison.Ordinal))
                    return behaviour;
            }

            current = current.parent;
        }

        return null;
    }

    private static bool ContainsArcade(string? value)
        => !string.IsNullOrEmpty(value) && value.IndexOf("arcade", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsSameOrRelatedTransform(Transform a, Transform b)
        => a == b || a.IsChildOf(b) || b.IsChildOf(a);

    private void ShowFallbackChoice()
    {
        if (_fallbackChoiceVisible)
            return;

        _fallbackChoiceVisible = true;
        CaptureFallbackUiInput();
        MelonLogger.Msg("Showing fast mouse-selectable Enter Arcade choice while vanilla NPC response may continue in the background.");
    }

    private void HideFallbackChoice(bool restoreGameplay)
    {
        _fallbackChoiceVisible = false;

        if (!_fallbackInputCaptured)
            return;

        _fallbackInputCaptured = false;
        if (restoreGameplay)
        {
            RestoreGameplayInput();
        }
        else
        {
            // A vanilla menu has taken ownership. Remove only our UI registration/cursor forcing
            // without fighting the vanilla menu's own movement/camera state.
            try
            {
                if (Il2CppScheduleOne.PlayerScripts.PlayerCamera.InstanceExists)
                    Il2CppScheduleOne.PlayerScripts.PlayerCamera.Instance.RemoveActiveUIElement(Constants.PreferencesCategory + ".EnterArcade");
            }
            catch
            {
            }
            _fallbackMovementStateCaptured = false;
        }
    }

    private void CancelFallbackChoice()
    {
        HideFallbackChoice(restoreGameplay: true);
        _awaitingDoorMenu = false;
    }

    private void CaptureFallbackUiInput()
    {
        if (_fallbackInputCaptured)
            return;

        _fallbackInputCaptured = true;
        _previousCursorLockState = Cursor.lockState;
        _previousCursorVisible = Cursor.visible;

        // The supplied Assembly-CSharp dump exposes Player.Activate/Deactivate, but the
        // generated IL2CPP wrapper in the user's current build does not surface those static
        // methods to C#. Use the same lower-level player/camera controls that S1API uses.
        try
        {
            if (Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.InstanceExists)
            {
                var movement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                _previousCanMove = movement.CanMove;
                _previousCanJump = movement.CanJump;
                movement.CanMove = false;
                movement.CanJump = false;
                _fallbackMovementStateCaptured = true;
            }
        }
        catch (Exception ex)
        {
            if (_preferences.DebugLogs)
                MelonLogger.Warning($"Could not temporarily lock player movement for Arcade choice: {ex.Message}");
        }

        try
        {
            if (Il2CppScheduleOne.PlayerScripts.PlayerCamera.InstanceExists)
            {
                var camera = Il2CppScheduleOne.PlayerScripts.PlayerCamera.Instance;
                camera.AddActiveUIElement(Constants.PreferencesCategory + ".EnterArcade");
                camera.SetCanLook(false);
                camera.FreeMouse();
            }
        }
        catch (Exception ex)
        {
            if (_preferences.DebugLogs)
                MelonLogger.Warning($"Could not register/free camera for Arcade choice: {ex.Message}");
        }

        EnsureFallbackMouseFree();
    }

    private static void EnsureFallbackMouseFree()
    {
        try
        {
            if (Il2CppScheduleOne.PlayerScripts.PlayerCamera.InstanceExists)
                Il2CppScheduleOne.PlayerScripts.PlayerCamera.Instance.FreeMouse();
        }
        catch
        {
            // Cursor state below is still enough for IMGUI input in most builds.
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void RestoreGameplayInput()
    {
        try
        {
            if (Il2CppScheduleOne.PlayerScripts.PlayerCamera.InstanceExists)
            {
                var camera = Il2CppScheduleOne.PlayerScripts.PlayerCamera.Instance;
                camera.RemoveActiveUIElement(Constants.PreferencesCategory + ".EnterArcade");
                // The generated IL2CPP wrapper exposes SetCanLook(bool) but does not
                // expose the canLook getter in this Schedule I build. Restore the normal
                // gameplay state explicitly when our modal closes.
                camera.SetCanLook(true);

                // activeUIElementCount is present in the native Assembly-CSharp dump but is
                // not surfaced by the generated IL2CPP wrapper used by MelonLoader 0.7.3.
                // Restore the mouse using the cursor state captured before opening our modal.
                if (_previousCursorLockState == CursorLockMode.Locked)
                    camera.LockMouse();
            }
        }
        catch
        {
        }

        if (_fallbackMovementStateCaptured)
        {
            try
            {
                if (Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.InstanceExists)
                {
                    var movement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                    movement.CanMove = _previousCanMove;
                    movement.CanJump = _previousCanJump;
                }
            }
            catch
            {
            }
            _fallbackMovementStateCaptured = false;
        }

        Cursor.lockState = _previousCursorLockState;
        Cursor.visible = _previousCursorVisible;
    }

    private float _nextInsideSafetyCheck;

    private void EnsureInsideArcadeSafePosition()
    {
        if (Time.unscaledTime < _nextInsideSafetyCheck || _location.SpawnMarker == null)
            return;

        _nextInsideSafetyCheck = Time.unscaledTime + 0.05f;
        Player? player = Player.Local;
        if (player == null)
            return;

        Vector3 spawn = _location.SpawnMarker.position;
        Vector3 current = player.Position;

        // Only recover a REAL fall below the room. Do not use distance from the entrance spawn as
        // a validity test: the player is expected to roam the whole arcade, and tight cabinet
        // collision can temporarily move the player root enough to trip a proximity watchdog.
        // The visible/safety floor sits within a few centimetres of the spawn-foot Y, so being
        // more than 2.25 m below that plane means the controller has actually escaped the room.
        const float trueFallDepth = 2.25f;
        if (current.y < spawn.y - trueFallDepth)
        {
            TeleportPlayerWithController(player, spawn, "arcade floor safety");
            MelonLogger.Warning("Arcade floor safety caught a genuine fall below the room and returned the player to the entrance floor.");
        }
    }

    private void TeleportPlayerWithController(Player player, Vector3 footPosition, string reason)
    {
        try
        {
            if (Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.InstanceExists)
            {
                var movement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                // This is the game's native teleport path from the supplied Assembly-CSharp dump.
                // alignFeetToPosition=true keeps the 1.85m CharacterController above the floor
                // instead of placing the player root partially through it.
                movement.Teleport(footPosition, true);
                Physics.SyncTransforms();
                return;
            }
        }
        catch (Exception ex)
        {
            if (_preferences.DebugLogs)
                MelonLogger.Warning($"Native PlayerMovement.Teleport failed during {reason}: {ex.Message}");
        }

        // Last-resort fallback for a wrapper mismatch. Lift the root by half the default player
        // controller height so even the fallback cannot start inside the floor collider.
        player.Position = footPosition + Vector3.up * 0.95f;
        Physics.SyncTransforms();
    }

    private void CaptureExteriorReturnPosition()
    {
        if (_outsideReturnPosition.HasValue)
            return;

        Player? player = Player.Local;
        if (player != null)
        {
            _outsideReturnPosition = player.Position;
            return;
        }

        if (TryGetDoorAccessPoint(out Vector3 accessPoint))
            _outsideReturnPosition = accessPoint;
    }

    private bool TryGetDoorAccessPoint(out Vector3 position)
    {
        position = default;
        if (_arcadeDoor == null)
            return false;

        Transform? accessPoint = ReflectionUtil.GetMemberValue(_arcadeDoor, "AccessPoint") as Transform;
        if (accessPoint == null)
            return false;

        position = accessPoint.position;
        return true;
    }

    private void ResolveNpcSummonMenu()
    {
        if (_npcSummonMenu != null || _npcMenuLookupAttempted)
            return;

        _npcMenuLookupAttempted = true;
        try
        {
            if (!Il2CppScheduleOne.UI.NPCSummonMenu.InstanceExists)
                return;

            _npcSummonMenu = Il2CppScheduleOne.UI.NPCSummonMenu.Instance;
            if (_npcSummonMenu != null && _preferences.DebugLogs)
                MelonLogger.Msg("Resolved vanilla NPCSummonMenu from Schedule I singleton cache.");
        }
        catch (Exception ex)
        {
            if (_preferences.DebugLogs)
                MelonLogger.Warning($"NPCSummonMenu singleton lookup failed: {ex.Message}");
        }
    }

    private bool IsNpcSummonMenuOpen()
    {
        ResolveNpcSummonMenu();
        if (_npcSummonMenu == null)
            return false;

        if (ReflectionUtil.GetBool(_npcSummonMenu, "IsOpen"))
            return true;

        Canvas? canvas = ReflectionUtil.GetMemberValue(_npcSummonMenu, "Canvas") as Canvas;
        return canvas != null && canvas.enabled && canvas.gameObject.activeInHierarchy;
    }

    private bool TryInjectNativeEnterChoice()
    {
        if (!IsNpcSummonMenuOpen() || _npcSummonMenu == null)
            return false;

        if (_nativeEntryClone != null)
            return true;

        RectTransform? entryContainer = ReflectionUtil.GetMemberValue(_npcSummonMenu, "EntryContainer") as RectTransform;
        if (entryContainer == null)
            return false;

        Button[] existingButtons = entryContainer.GetComponentsInChildren<Button>(true);
        if (existingButtons.Length == 0)
            return false;

        GameObject? templateRoot = FindTopLevelEntryRoot(existingButtons[0].transform, entryContainer);
        if (templateRoot == null)
            return false;

        _nativeEntryClone = UnityEngine.Object.Instantiate(templateRoot, entryContainer, false);
        _nativeEntryClone.name = "ScheduleIArcade_EnterArcadeEntry";
        _nativeEntryClone.transform.SetAsLastSibling();
        _nativeEntryClone.SetActive(true);

        Button? button = _nativeEntryClone.GetComponentInChildren<Button>(true);
        if (button == null)
        {
            DestroyNativeEntry();
            return false;
        }

        button.interactable = true;
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener((UnityAction)OnNativeEnterClicked);
        SetEntryText(_nativeEntryClone, "Enter Arcade");

        MelonLogger.Msg("Injected 'Enter Arcade' into the vanilla NPC summon menu.");
        return true;
    }

    private void OnNativeEnterClicked()
    {
        CloseNativeNpcMenu();
        EnterArcade();
    }

    private void CloseNativeNpcMenu()
    {
        if (_npcSummonMenu != null)
            ReflectionUtil.TryInvoke(_npcSummonMenu, "Close");
    }

    private static GameObject? FindTopLevelEntryRoot(Transform buttonTransform, Transform entryContainer)
    {
        Transform current = buttonTransform;
        while (current.parent != null && current.parent != entryContainer)
            current = current.parent;

        return current.parent == entryContainer ? current.gameObject : null;
    }

    private static void SetEntryText(GameObject root, string text)
    {
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null)
                continue;

            if (component is Text uiText)
            {
                uiText.text = text;
                continue;
            }

            string typeName = component.GetType().FullName ?? component.GetType().Name;
            if (typeName.IndexOf("TMPro", StringComparison.OrdinalIgnoreCase) < 0 &&
                typeName.IndexOf("TextMesh", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            try
            {
                PropertyInfo? property = component.GetType().GetProperty("text");
                if (property?.CanWrite == true)
                    property.SetValue(component, text);
            }
            catch
            {
                // Decorative text-like components are not fatal.
            }
        }
    }

    private void CleanupNativeEntryIfMenuClosed()
    {
        if (_nativeEntryClone != null && !IsNpcSummonMenuOpen())
            DestroyNativeEntry();
    }

    private void DestroyNativeEntry()
    {
        if (_nativeEntryClone == null)
            return;

        UnityEngine.Object.Destroy(_nativeEntryClone);
        _nativeEntryClone = null;
    }

    private static string BuildHierarchyPath(Transform transform)
    {
        Stack<string> parts = new();
        Transform? current = transform;
        int guard = 0;
        while (current != null && guard++ < 32)
        {
            parts.Push(current.name);
            current = current.parent;
        }
        return string.Join("/", parts);
    }

    public void Dispose()
    {
        DestroyNativeEntry();
        HideFallbackChoice(restoreGameplay: true);
        _awaitingDoorMenu = false;
        IsInsideArcade = false;
        _outsideReturnPosition = null;
        _arcadeDoor = null;
        _arcadeDoorTransform = null;
        _npcSummonMenu = null;
        _npcMenuLookupAttempted = false;
        _lastDoorSignalTime = -999f;
        _suppressLateVanillaMenuUntil = 0f;
    }
}

using MelonLoader;
using ScheduleIArcade.Models;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Owns the native-style modal state while a custom screen is being played. Games continue to use
/// legacy arrow/TAB/ESC input, while Schedule I's PlayerInput action map is paused so the phone,
/// hotbar and gameplay hotkeys cannot react underneath them.
/// </summary>
internal sealed class ArcadeTvViewService
{
    private const string CameraUiToken = "ScheduleIArcade.PlayingTv";
    private const float CameraTransitionSeconds = 0.16f;

    private ArcadeCabinet? _cabinet;
    private bool _active;
    private bool _movementCaptured;
    private bool _previousCanMove = true;
    private bool _previousCanJump = true;
    private CursorLockMode _previousCursorLockState;
    private bool _previousCursorVisible;
    private Il2CppScheduleOne.UI.HUD? _hud;
    private bool _hotbarWasVisible;
    private Il2CppScheduleOne.UI.Phone.Phone? _phone;
    private bool _phoneWasOpenable = true;
    private object? _playerInput;
    private bool _playerInputPaused;

    public void Enter(ArcadeCabinet cabinet)
    {
        if (_active || cabinet == null)
            return;

        _active = true;
        _cabinet = cabinet;
        _previousCursorLockState = Cursor.lockState;
        _previousCursorVisible = Cursor.visible;

        try
        {
            CaptureAndLockPhoneAndHud();
            PauseNativePlayerActions();
            LockMovementAndFrameCamera(cabinet);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            MelonLogger.Msg($"{cabinet.Id} session entered native-style screen mode: camera framed, player/hotbar/phone controls locked.");
        }
        catch (Exception ex)
        {
            // Never leave the player trapped if an optional UI object changed in a game update.
            MelonLogger.Warning($"Arcade screen mode partially unavailable; keeping the playable session safe: {ex.Message}");
        }
    }

    public void Tick()
    {
        if (!_active)
            return;

        // Some vanilla UI handlers can try to reactivate their input map a frame after it is
        // closed. Re-pause only while this explicit TV modal is active; arrow/TAB/ESC use legacy
        // Unity input and remain available to ArcadeTvGame.
        if (_playerInputPaused)
            ReflectionUtil.TryInvoke(_playerInput, "DeactivateInput");

        if (_phone != null)
        {
            ReflectionUtil.TrySetMemberValue(_phone, "_isOpenable_k__BackingField", false);
            if (_phone.IsOpen)
                _phone.SetIsOpen(false);
        }
    }

    public void Exit()
    {
        if (!_active && _cabinet == null)
            return;

        try
        {
            if (Il2CppScheduleOne.PlayerScripts.PlayerCamera.InstanceExists)
            {
                var camera = Il2CppScheduleOne.PlayerScripts.PlayerCamera.Instance;
                camera.StopTransformOverride(CameraTransitionSeconds, true, true);
                camera.StopFOVOverride(CameraTransitionSeconds);
                camera.RemoveActiveUIElement(CameraUiToken);
                camera.SetCanLook(true);
                if (_previousCursorLockState == CursorLockMode.Locked)
                    camera.LockMouse();
            }
        }
        catch
        {
        }

        if (_movementCaptured)
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
        }

        if (_playerInputPaused)
            ReflectionUtil.TryInvoke(_playerInput, "ActivateInput");

        if (_phone != null)
            ReflectionUtil.TrySetMemberValue(_phone, "_isOpenable_k__BackingField", _phoneWasOpenable);

        if (_hud != null && _hud.HotbarContainer != null)
            _hud.HotbarContainer.gameObject.SetActive(_hotbarWasVisible);

        Cursor.lockState = _previousCursorLockState;
        Cursor.visible = _previousCursorVisible;
        _cabinet = null;
        _active = false;
        _movementCaptured = false;
        _playerInputPaused = false;
        _playerInput = null;
        _hud = null;
        _phone = null;
    }

    private void CaptureAndLockPhoneAndHud()
    {
        _hud = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.UI.HUD>();
        if (_hud != null && _hud.HotbarContainer != null)
        {
            _hotbarWasVisible = _hud.HotbarContainer.gameObject.activeSelf;
            _hud.HotbarContainer.gameObject.SetActive(false);
        }

        _phone = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.UI.Phone.Phone>();
        if (_phone != null)
        {
            _phoneWasOpenable = ReflectionUtil.GetBool(_phone, "isOpenable", true);
            if (_phone.IsOpen)
                _phone.SetIsOpen(false);
            ReflectionUtil.TrySetMemberValue(_phone, "_isOpenable_k__BackingField", false);
        }
    }

    private void PauseNativePlayerActions()
    {
        Il2CppScheduleOne.GameInput? gameInput = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.GameInput>();
        if (gameInput == null || gameInput.PlayerInput == null)
            return;

        _playerInput = gameInput.PlayerInput;
        _playerInputPaused = ReflectionUtil.TryInvoke(_playerInput, "DeactivateInput");
    }

    private void LockMovementAndFrameCamera(ArcadeCabinet cabinet)
    {
        if (Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.InstanceExists)
        {
            var movement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
            _previousCanMove = movement.CanMove;
            _previousCanJump = movement.CanJump;
            movement.CanMove = false;
            movement.CanJump = false;
            _movementCaptured = true;
        }

        if (!Il2CppScheduleOne.PlayerScripts.PlayerCamera.InstanceExists)
            return;

        Vector3 screenCenter = cabinet.ScreenRenderer.bounds.center;
        bool cocktailTable = cabinet.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase);
        Vector3 cameraPosition;
        Quaternion cameraRotation;
        float fov;
        if (cocktailTable)
        {
            // The cocktail screen is horizontal. Lock an overhead view with its texture's top
            // pointed toward the player, like a real two-sided Pac-Man table.
            cameraPosition = screenCenter + Vector3.up * 1.28f;
            cameraRotation = Quaternion.LookRotation(Vector3.down, -cabinet.Root.transform.forward);
            fov = 47f;
        }
        else
        {
            // Upright cabinet meshes do not all share one root-facing axis. The fitted display
            // is the reliable source of truth: place the player on its rendered/front side,
            // never at a hard-coded root back direction. This keeps the Doom view at the CRT
            // instead of its cabinet side or outside the room.
            Vector3 viewingDirection = cabinet.ScreenRenderer.transform.forward.normalized;
            if (viewingDirection.sqrMagnitude < 0.01f)
                viewingDirection = cabinet.Root.transform.forward;
            cameraPosition = screenCenter + viewingDirection * 1.10f + Vector3.up * 0.04f;
            cameraRotation = Quaternion.LookRotation(screenCenter - cameraPosition, Vector3.up);
            fov = 56f;
        }

        var camera = Il2CppScheduleOne.PlayerScripts.PlayerCamera.Instance;
        camera.AddActiveUIElement(CameraUiToken);
        camera.SetCanLook(false);
        camera.OverrideTransform(cameraPosition, cameraRotation, CameraTransitionSeconds, false);
        camera.OverrideFOV(fov, CameraTransitionSeconds);
        camera.LockMouse();
    }
}

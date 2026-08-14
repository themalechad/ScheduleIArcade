using S1API.Entities;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class ArcadeExitInteractionService
{
    private readonly ArcadeLocationService _location;
    private readonly ArcadeEntranceService _entrance;
    private readonly ArcadeSessionService _session;
    private ExitTarget _target;

    private enum ExitTarget
    {
        None,
        Entrance,
        BackDoor
    }

    public ArcadeExitInteractionService(
        ArcadeLocationService location,
        ArcadeEntranceService entrance,
        ArcadeSessionService session)
    {
        _location = location;
        _entrance = entrance;
        _session = session;
    }

    public void Tick()
    {
        _target = ExitTarget.None;
        if (!_entrance.IsInsideArcade || _session.IsPlaying)
            return;

        var player = Player.Local;
        if (player == null)
            return;

        // Exit prompts deliberately use the same close, aimed interaction rule as machines.
        // Being near a door is not enough: the player's view must be centred on its hotspot.
        if (IsLookingAt(_location.BackExitMarker, player, 1.55f))
            _target = ExitTarget.BackDoor;
        else if (IsLookingAt(_location.ExitMarker, player, Constants.ExitInteractionDistance))
            _target = ExitTarget.Entrance;

        if (!Input.GetKeyDown(KeyCode.E))
            return;

        if (_target == ExitTarget.BackDoor)
            _entrance.ExitArcadeToWestSide();
        else if (_target == ExitTarget.Entrance)
            _entrance.ExitArcade();
    }

    public void OnGUI()
    {
        if (_target == ExitTarget.None || !_entrance.IsInsideArcade || _session.IsPlaying)
            return;

        bool backDoor = _target == ExitTarget.BackDoor;
        ArcadePromptUi.DrawCard(
            backDoor ? "WEST SIDE DOOR" : "ARCADE EXIT",
            backDoor ? "PRESS [E]  EXIT TO WEST SIDE" : "PRESS [E]  RETURN OUTSIDE",
            backDoor ? "Exit through the rear arcade door." : "Return through the arcade entrance.",
            132f,
            new Color(0.95f, 0.35f, 0.38f, 1f));
    }

    private static bool IsLookingAt(Transform? marker, Player player, float maximumDistance)
    {
        if (marker == null)
            return false;

        Camera? camera = Camera.main;
        if (camera == null)
            return false;

        // Player.Position is foot-aligned while the exit hotspot is at eye level. Range should
        // measure floor distance, otherwise lifting the hotspot would shrink its usable radius.
        Vector3 toMarkerFromPlayer = marker.position - player.Position;
        toMarkerFromPlayer.y = 0f;
        if (toMarkerFromPlayer.sqrMagnitude > maximumDistance * maximumDistance)
            return false;

        Vector3 toMarkerFromCamera = marker.position - camera.transform.position;
        if (toMarkerFromCamera.sqrMagnitude < 0.0001f)
            return false;

        // 0.978 is approximately a 12-degree cone: intentional without being pixel-perfect.
        return Vector3.Dot(camera.transform.forward.normalized, toMarkerFromCamera.normalized) >= 0.978f;
    }
}

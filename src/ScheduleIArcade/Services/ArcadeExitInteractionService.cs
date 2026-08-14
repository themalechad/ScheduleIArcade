using S1API.Entities;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class ArcadeExitInteractionService
{
    private readonly ArcadeLocationService _location;
    private readonly ArcadeEntranceService _entrance;
    private readonly ArcadeSessionService _session;
    private bool _nearExit;

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
        _nearExit = false;
        if (!_entrance.IsInsideArcade || _session.IsPlaying || _location.ExitMarker == null)
            return;

        var player = Player.Local;
        if (player == null)
            return;

        float sqrDistance = (player.Position - _location.ExitMarker.position).sqrMagnitude;
        _nearExit = sqrDistance <= Constants.ExitInteractionDistance * Constants.ExitInteractionDistance;

        if (_nearExit && Input.GetKeyDown(KeyCode.E))
            _entrance.ExitArcade();
    }

    public void OnGUI()
    {
        if (!_nearExit || !_entrance.IsInsideArcade || _session.IsPlaying)
            return;

        ArcadePromptUi.DrawCard(
            "ARCADE EXIT",
            "PRESS [E]  RETURN OUTSIDE",
            "Walk close to the doorway and look at the exit to leave the arcade.",
            132f,
            new Color(0.95f, 0.35f, 0.38f, 1f));
    }
}

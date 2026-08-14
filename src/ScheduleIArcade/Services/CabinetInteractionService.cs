using ScheduleIArcade.Models;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class CabinetInteractionService
{
    private readonly CabinetRegistryService _registry;
    private readonly ArcadeSessionService _session;
    private readonly Func<bool> _canInteract;
    private ArcadeCabinet? _lookedAt;

    public bool IsLookingAtCabinet => _lookedAt != null;

    public CabinetInteractionService(
        CabinetRegistryService registry,
        ArcadeSessionService session,
        Func<bool> canInteract)
    {
        _registry = registry;
        _session = session;
        _canInteract = canInteract;
    }

    public void Tick()
    {
        _lookedAt = null;
        if (!_canInteract() || _session.IsPlaying)
            return;

        Camera camera = Camera.main;
        if (camera == null)
            return;

        var ray = new Ray(camera.transform.position, camera.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, Constants.InteractionDistance))
            return;

        _lookedAt = _registry.Resolve(hit.collider);
        if (_lookedAt != null && Input.GetKeyDown(KeyCode.E))
            _session.Start(_lookedAt);
    }

    public void OnGUI()
    {
        if (!_canInteract())
            return;

        if (_session.IsPlaying)
        {
            ArcadePromptUi.DrawCard(
                GetDisplayName(_session.ActiveCabinet),
                _session.ActiveCabinet?.Id.Equals("TV", StringComparison.OrdinalIgnoreCase) == true
                    ? "ARROWS PLAY  •  TAB MENU  •  ESC EXIT"
                    : _session.ActiveCabinet?.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase) == true
                        ? "ARROWS MOVE  •  TAB PAUSE  •  ESC EXIT"
                        : "ARROWS PLAY  •  ESC EXIT",
                "Arcade session active — player controls and hotbar are locked.",
                52f,
                new Color(0.10f, 0.88f, 1.00f, 1f));
            return;
        }

        if (_lookedAt == null)
            return;

        string action = _lookedAt.IsOccupied
            ? "MACHINE IN USE"
            : _lookedAt.Id.Equals("TV", StringComparison.OrdinalIgnoreCase)
                ? "PRESS [E]  PLAY PONG + SNAKE"
                : _lookedAt.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase)
                    ? "PRESS [E]  PLAY PAC-MAN"
                    : "PRESS [E]  START ARCADE DEMO";
        string detail = _lookedAt.IsOccupied
            ? "Wait for the current player to finish."
            : "Look directly at this machine to interact.  [F] picks it up to move it.";
        ArcadePromptUi.DrawCard(
            GetDisplayName(_lookedAt),
            action,
            detail,
            224f,
            _lookedAt.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase)
                ? new Color(1.00f, 0.80f, 0.12f, 1f)
                : new Color(0.10f, 0.88f, 1.00f, 1f));
    }

    private static string GetDisplayName(ArcadeCabinet? cabinet)
    {
        if (cabinet == null)
            return "ARCADE SESSION";
        if (cabinet.Id.Equals("TV", StringComparison.OrdinalIgnoreCase))
            return "ARCADE TV";
        if (cabinet.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase))
            return "PAC-MAN COCKTAIL TABLE";
        return cabinet.Id switch
        {
            "01" => "STAND-UP ARCADE 1",
            "02" => "STAND-UP ARCADE 2",
            _ => "ARCADE MACHINE"
        };
    }
}

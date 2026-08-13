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
            GUI.Box(new Rect(Screen.width / 2f - 145f, Screen.height - 90f, 290f, 34f),
                _session.ActiveCabinet?.Id.Equals("TV", StringComparison.OrdinalIgnoreCase) == true
                    ? "TV: arrows play | TAB menu | ESC exit"
                    : "Arcade playing - ESC to stop game");
            return;
        }

        if (_lookedAt == null)
            return;

        string state = _lookedAt.IsOccupied
            ? "Machine In Use"
            : _lookedAt.Id.Equals("TV", StringComparison.OrdinalIgnoreCase)
                ? "[E] Play Arcade TV"
                : "[E] Play Arcade";
        GUI.Box(new Rect(Screen.width / 2f - 130f, Screen.height - 90f, 260f, 34f), state);
    }
}

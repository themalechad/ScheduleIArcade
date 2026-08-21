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
    private string _startFeedback = string.Empty;
    private string _startFeedbackCabinetId = string.Empty;
    private float _startFeedbackUntil;

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
        if (Physics.Raycast(
                ray,
                out RaycastHit hit,
                Constants.InteractionDistance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide))
        {
            _lookedAt = _registry.Resolve(hit.collider);
        }

        // Keep a tight look cone as a fallback for source-mesh collider gaps. This uses the live
        // screen/playfield position and normal standing aim height, so it works after players move
        // an arcade asset and never requires crouching to hit a low imported collider.
        _lookedAt ??= ResolveStandingAim(camera);
        if (_lookedAt != null && Input.GetKeyDown(KeyCode.E))
        {
            if (_session.Start(_lookedAt))
            {
                _startFeedback = string.Empty;
                _startFeedbackCabinetId = string.Empty;
                _startFeedbackUntil = 0f;
            }
            else
            {
                _startFeedback = string.IsNullOrWhiteSpace(_session.LastStartFailure)
                    ? "MACHINE COULD NOT START — NO CHARGE"
                    : _session.LastStartFailure;
                _startFeedbackCabinetId = _lookedAt.Id;
                _startFeedbackUntil = Time.unscaledTime + 2.8f;
            }
        }
    }

    private ArcadeCabinet? ResolveStandingAim(Camera camera)
    {
        const float minimumFacingDot = 0.955f; // ~17 degrees; deliberate but not pixel-perfect.
        ArcadeCabinet? best = null;
        float bestDot = minimumFacingDot;

        foreach (ArcadeCabinet cabinet in _registry.Cabinets)
        {
            if (!cabinet.Root.activeInHierarchy)
                continue;

            Vector3 toFocus = CabinetRegistryService.GetStandingInteractionFocus(cabinet) - camera.transform.position;
            float distance = toFocus.magnitude;
            if (distance < 0.05f || distance > Constants.InteractionDistance)
                continue;

            float facing = Vector3.Dot(camera.transform.forward.normalized, toFocus / distance);
            if (facing > bestDot)
            {
                best = cabinet;
                bestDot = facing;
            }
        }

        return best;
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
                    : _session.ActiveCabinet?.GameId.Equals("doom1", StringComparison.OrdinalIgnoreCase) == true ||
                      _session.ActiveCabinet?.GameId.Equals("doom2", StringComparison.OrdinalIgnoreCase) == true
                        ? "WASD / ARROWS PLAY  •  ESC EXIT"
                        : _session.ActiveCabinet?.GameId.Equals("space_cadet_pinball", StringComparison.OrdinalIgnoreCase) == true
                            ? "LEFT / RIGHT FLIPPERS  •  SPACE LAUNCH  •  ESC EXIT"
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

        bool showStartFeedback =
            Time.unscaledTime < _startFeedbackUntil &&
            _lookedAt.Id.Equals(_startFeedbackCabinetId, StringComparison.OrdinalIgnoreCase);
        string action = showStartFeedback
            ? _startFeedback
            : _lookedAt.IsOccupied
                ? "MACHINE IN USE"
                : "PRESS [E]  PLAY  •  $10 CASH";
        string detail = showStartFeedback
            ? "A game only charges when its session starts successfully."
            : _lookedAt.IsOccupied
                ? "Wait for the current player to finish."
                : GetGameDescription(_lookedAt) + "  •  [F] picks it up to move it.";
        ArcadePromptUi.DrawCard(
            GetDisplayName(_lookedAt),
            action,
            detail,
            224f,
            showStartFeedback
                ? new Color(1.00f, 0.28f, 0.22f, 1f)
                : _lookedAt.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase)
                ? new Color(1.00f, 0.80f, 0.12f, 1f)
                : new Color(0.10f, 0.88f, 1.00f, 1f));
    }

    private static string GetGameDescription(ArcadeCabinet cabinet)
    {
        if (cabinet.Id.Equals("TV", StringComparison.OrdinalIgnoreCase))
            return "Pong + Snake";
        if (cabinet.GameId.Equals("doom1", StringComparison.OrdinalIgnoreCase))
            return "Doom I";
        if (cabinet.GameId.Equals("doom2", StringComparison.OrdinalIgnoreCase))
            return "Doom II";
        if (cabinet.GameId.Equals("space_cadet_pinball", StringComparison.OrdinalIgnoreCase))
            return "Space Cadet pinball";
        if (cabinet.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase))
            return "Pac-Man";
        return "Arcade demo";
    }

    private static string GetDisplayName(ArcadeCabinet? cabinet)
    {
        if (cabinet == null)
            return "ARCADE SESSION";
        if (cabinet.Id.Equals("TV", StringComparison.OrdinalIgnoreCase))
            return "ARCADE TV";
        if (cabinet.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase))
            return "PAC-MAN COCKTAIL TABLE";
        if (cabinet.GameId.Equals("doom1", StringComparison.OrdinalIgnoreCase))
            return "DOOM I CABINET";
        if (cabinet.GameId.Equals("doom2", StringComparison.OrdinalIgnoreCase))
            return "DOOM II CABINET";
        if (cabinet.GameId.Equals("space_cadet_pinball", StringComparison.OrdinalIgnoreCase))
            return "SPACE CADET PINBALL";
        return cabinet.Id switch
        {
            "01" => "STAND-UP ARCADE 1",
            "02" => "STAND-UP ARCADE 2",
            _ => "ARCADE MACHINE"
        };
    }
}

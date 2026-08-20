using MelonLoader;
using ScheduleIArcade.Models;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class CabinetRegistryService
{
    private readonly List<ArcadeCabinet> _cabinets = new();
    public ArcadeCabinet[] Cabinets => _cabinets.ToArray();

    public void Scan(GameObject room)
    {
        _cabinets.Clear();
        var transforms = room.GetComponentsInChildren<Transform>(true);

        foreach (var transform in transforms)
        {
            bool isPinball = TryGetPinballSuffix(transform.name, out string suffix);
            if (!isPinball && !transform.name.StartsWith(Constants.CabinetPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!isPinball)
                suffix = transform.name[Constants.CabinetPrefix.Length..];
            var screen = FindScreen(transform, suffix);
            if (screen == null)
            {
                MelonLogger.Warning($"Cabinet '{transform.name}' has no matching screen marker.");
                continue;
            }

            string gameId = isPinball
                ? "space_cadet_pinball"
                : suffix.Equals("TV", StringComparison.OrdinalIgnoreCase)
                ? "arcade_tv"
                : suffix.Equals("PACMAN", StringComparison.OrdinalIgnoreCase)
                    ? "pacman"
                    : suffix.Equals("01", StringComparison.OrdinalIgnoreCase)
                        ? "doom1"
                        : suffix.Equals("02", StringComparison.OrdinalIgnoreCase)
                            ? "doom2"
                            : Constants.DefaultGameId;
            var cabinet = new ArcadeCabinet(suffix, transform.gameObject, screen, gameId);
            CreateStandingInteractionHotspot(cabinet);
            _cabinets.Add(cabinet);
            MelonLogger.Msg($"Registered arcade cabinet {suffix} -> {gameId}.");
        }

        MelonLogger.Msg($"Arcade cabinet scan complete: {_cabinets.Count} cabinet(s).");
    }

    public ArcadeCabinet? Resolve(Collider collider)
    {
        Transform? current = collider.transform;
        while (current != null)
        {
            var cabinet = _cabinets.FirstOrDefault(c => c.Root.transform == current);
            if (cabinet != null)
                return cabinet;
            current = current.parent;
        }
        return null;
    }

    /// <summary>
    /// Returns a player-height target for the interaction cone. The real rendered playfield is
    /// low on cocktail and pinball machines; raising only the aim target lets a standing player
    /// interact naturally without moving the physical screen or loosening the close-range rule.
    /// </summary>
    public static Vector3 GetStandingInteractionFocus(ArcadeCabinet cabinet)
    {
        Vector3 focus = cabinet.ScreenRenderer.bounds.center;
        if (cabinet.GameId.Equals("space_cadet_pinball", StringComparison.OrdinalIgnoreCase))
            focus.y += 0.46f;
        else if (cabinet.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase))
            focus.y += 0.34f;
        return focus;
    }

    private static void CreateStandingInteractionHotspot(ArcadeCabinet cabinet)
    {
        // Imported props have low/split source colliders, so direct screen rays can miss them at
        // normal eye height. This trigger is intentionally invisible and non-blocking; it only
        // receives the explicit arcade interaction ray and remains a child when the machine moves.
        var hotspot = new GameObject("__ArcadeInteraction_" + cabinet.Id);
        hotspot.transform.SetParent(cabinet.Root.transform, false);
        hotspot.transform.position = GetStandingInteractionFocus(cabinet);
        hotspot.transform.rotation = Quaternion.identity;
        hotspot.transform.localScale = Vector3.one;

        Vector3 size;
        if (cabinet.GameId.Equals("space_cadet_pinball", StringComparison.OrdinalIgnoreCase))
            size = new Vector3(1.20f, 0.72f, 1.16f);
        else if (cabinet.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase))
            size = new Vector3(1.45f, 0.62f, 1.20f);
        else if (cabinet.Id.Equals("TV", StringComparison.OrdinalIgnoreCase))
            size = new Vector3(1.72f, 1.02f, 0.55f);
        else
            size = new Vector3(0.92f, 0.70f, 0.78f);

        var collider = hotspot.AddComponent<BoxCollider>();
        collider.size = size;
        collider.isTrigger = true;
    }

    private static Renderer? FindScreen(Transform cabinetRoot, string suffix)
    {
        string exact = Constants.ScreenPrefix + suffix;
        foreach (var renderer in cabinetRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.name.Equals(exact, StringComparison.OrdinalIgnoreCase) ||
                renderer.name.StartsWith(Constants.ScreenPrefix, StringComparison.OrdinalIgnoreCase))
                return renderer;
        }
        return null;
    }

    private static bool TryGetPinballSuffix(string name, out string suffix)
    {
        const string prefix = "Pinball Machine ";
        suffix = string.Empty;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!int.TryParse(name[prefix.Length..], out int number) || number < 1 || number > 4)
            return false;

        suffix = "PINBALL" + number.ToString("00");
        return true;
    }
}

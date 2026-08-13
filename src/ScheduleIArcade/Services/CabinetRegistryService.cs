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
            if (!transform.name.StartsWith(Constants.CabinetPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string suffix = transform.name[Constants.CabinetPrefix.Length..];
            var screen = FindScreen(transform, suffix);
            if (screen == null)
            {
                MelonLogger.Warning($"Cabinet '{transform.name}' has no matching screen marker.");
                continue;
            }

            string gameId = suffix.Equals("TV", StringComparison.OrdinalIgnoreCase)
                ? "arcade_tv"
                : Constants.DefaultGameId;
            _cabinets.Add(new ArcadeCabinet(suffix, transform.gameObject, screen, gameId));
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
}

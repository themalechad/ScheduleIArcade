using UnityEngine;

namespace ScheduleIArcade.Models;

internal sealed class ArcadeCabinet
{
    public ArcadeCabinet(string id, GameObject root, Renderer screenRenderer, string gameId)
    {
        Id = id;
        Root = root;
        ScreenRenderer = screenRenderer;
        GameId = gameId;
    }

    public string Id { get; }
    public GameObject Root { get; }
    public Renderer ScreenRenderer { get; }
    public string GameId { get; set; }
    public bool IsOccupied { get; set; }
}

using UnityEngine;

namespace ScheduleIArcade.Games;

public interface IArcadeGame : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    Texture? ScreenTexture { get; }

    void Initialize();
    void Start();
    void Tick(float deltaTime);
    void Stop();
}

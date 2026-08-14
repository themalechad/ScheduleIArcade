using MelonLoader;
using ScheduleIArcade.Services;
using UnityEngine;

namespace ScheduleIArcade.Games;

/// <summary>
/// Uses a matching separately-installed Doom TV mod for its own WAD/native frame source. When
/// that mod is absent or unhealthy, the cabinet deliberately runs the normal test pattern instead.
/// </summary>
internal sealed class ExternalDoomArcadeGame : IArcadeGame
{
    private readonly DoomCabinetEdition _edition;
    private DoomCabinetModBridge.Session? _doom;
    private TestPatternArcadeGame? _fallback;
    private Texture2D? _texture;
    private bool _usingFallback;

    public ExternalDoomArcadeGame(DoomCabinetEdition edition) => _edition = edition;

    public string Id => _edition == DoomCabinetEdition.Doom1 ? "doom1" : "doom2";
    public string DisplayName => _usingFallback
        ? (_edition == DoomCabinetEdition.Doom1 ? "Doom I Cabinet — Test Screen" : "Doom II Cabinet — Test Screen")
        : (_edition == DoomCabinetEdition.Doom1 ? "Doom I Cabinet" : "Doom II Cabinet");
    public Texture? ScreenTexture => _usingFallback ? _fallback?.ScreenTexture : _texture;

    public void Initialize()
    {
        if (_texture != null || _fallback != null)
            return;

        if (!DoomCabinetModBridge.TryCreate(_edition, out _doom, out string detail) || _doom == null)
        {
            StartFallback(detail);
            return;
        }

        _texture = new Texture2D(640, 400, TextureFormat.RGBA32, false)
        {
            name = _edition == DoomCabinetEdition.Doom1 ? "ScheduleIArcade_Doom1Frame" : "ScheduleIArcade_Doom2Frame",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        Color32[] loading = Enumerable.Repeat(new Color32(5, 5, 5, 255), 640 * 400).ToArray();
        _texture.SetPixels32(loading);
        _texture.Apply(false, false);
    }

    public void Start()
    {
        if (_usingFallback)
        {
            _fallback?.Start();
            return;
        }

        string detail = "The matching Doom runtime could not start.";
        if (_doom == null || !_doom.Start(out detail))
            StartFallback(string.IsNullOrWhiteSpace(detail) ? "The matching Doom runtime could not start." : detail);
    }

    public void Tick(float deltaTime)
    {
        if (_usingFallback)
        {
            _fallback?.Tick(deltaTime);
            return;
        }

        if (_doom == null || _texture == null)
        {
            StartFallback("The matching Doom runtime is unavailable.");
            return;
        }

        if (!_doom.TryTick(out byte[]? frame, out string detail))
        {
            StartFallback(string.IsNullOrWhiteSpace(detail) ? "The Doom framebuffer stopped." : detail);
            return;
        }

        if (frame != null)
        {
            _texture.LoadRawTextureData(frame);
            _texture.Apply(false, false);
        }
    }

    public void Stop() { }

    public void Dispose()
    {
        _doom?.Dispose();
        _doom = null;
        _fallback?.Dispose();
        _fallback = null;
        if (_texture != null)
        {
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
    }

    private void StartFallback(string reason)
    {
        if (!_usingFallback)
            MelonLogger.Warning((_edition == DoomCabinetEdition.Doom1 ? "Doom I" : "Doom II") + " cabinet is using the test screen: " + reason);

        _doom?.Dispose();
        _doom = null;
        _usingFallback = true;
        _fallback ??= new TestPatternArcadeGame();
        _fallback.Initialize();
        _fallback.Start();
    }
}

using UnityEngine;

namespace ScheduleIArcade.Games;

/// <summary>
/// Development game used to prove that a live framebuffer can be drawn on a cabinet screen.
/// It deliberately has no emulator dependency.
/// </summary>
internal sealed class TestPatternArcadeGame : IArcadeGame
{
    private const int Width = 160;
    private const int Height = 120;
    private Texture2D? _texture;
    private Color32[]? _pixels;
    private float _time;
    private float _nextUpload;

    public string Id => "test_pattern";
    public string DisplayName => "Arcade Test Pattern";
    public Texture? ScreenTexture => _texture;

    public void Initialize()
    {
        if (_texture != null)
            return;

        _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_TestPattern",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _pixels = new Color32[Width * Height];
        DrawFrame();
    }

    public void Start()
    {
        _time = 0f;
        _nextUpload = 0f;
    }

    public void Tick(float deltaTime)
    {
        _time += deltaTime;
        if (_time < _nextUpload)
            return;

        _nextUpload = _time + 0.1f;
        DrawFrame();
    }

    private void DrawFrame()
    {
        if (_texture == null || _pixels == null)
            return;

        int barOffset = (int)(_time * 35f) % Width;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte r = (byte)((x * 255) / (Width - 1));
                byte g = (byte)((y * 255) / (Height - 1));
                byte b = (byte)(Math.Abs(x - barOffset) < 5 ? 255 : 40);
                _pixels[y * Width + x] = new Color32(r, g, b, 255);
            }
        }

        _texture.SetPixels32(_pixels);
        _texture.Apply(false, false);
    }

    public void Stop() { }

    public void Dispose()
    {
        if (_texture != null)
        {
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
        _pixels = null;
    }
}

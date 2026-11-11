using ScheduleIArcade.Configuration;
using ScheduleIArcade.Games.Pinball;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Games;

/// <summary>
/// Space Cadet style table rendered into a texture. Physics lives in <see cref="PinballSimulation"/>
/// (fixed 1/480 s steps, capsule colliders, rotating flippers) and the table is drawn from the
/// same collider data, so every rail, target and wall the player sees is exactly what the ball
/// hits. Original graphics; no assets from the Windows game are used.
/// </summary>
internal sealed class SpaceCadetPinballGame : IArcadeGame
{
    private const int Width = (int)PinballSimulation.Width;
    private const int Height = (int)PinballSimulation.Height;

    private readonly PinballSimulation _sim = new();
    private Texture2D? _texture;
    private Color32[]? _pixels;
    private float _nextDraw;
    private float _bannerTime;
    private string _banner = string.Empty;
    private ProceduralCabinetAudio? _audio;
    private const int SoundGateCapture = 0, SoundGateEject = 1, SoundExtraBall = 2;

    public string Id => "space_cadet_pinball";
    public string DisplayName => "Space Cadet Pinball";
    public Texture? ScreenTexture => _texture;

    public void Initialize()
    {
        if (_texture != null)
            return;

        _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_SpaceCadetPinball",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _pixels = new Color32[Width * Height];
        _sim.ResetGame();
        DrawFrame();
    }

    public void Start()
    {
        if (_sim.GameOver)
            _sim.ResetGame();
    }

    public void AttachAudio(Transform speaker, ArcadePreferences preferences)
    {
        _audio?.Dispose();
        _audio = new ProceduralCabinetAudio(speaker, preferences, "Pinball",
            (0.30f, 420f, 9f), (0.12f, 900f, 30f), (0.45f, 1320f, 6f));
    }

    public void Tick(float deltaTime)
    {
        if (_texture == null || _pixels == null)
            return;

        if (_sim.GameOver)
        {
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return))
                _sim.ResetGame();
        }
        else
        {
            bool leftPressed = Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.Z);
            bool rightPressed = Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.Slash) || Input.GetKey(KeyCode.X);
            bool launch = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow);
            // Real (unscaled) frame time; the simulation consumes it in fixed sub-steps so the
            // table behaves identically at any frame rate.
            float frame = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : deltaTime;
            _sim.Advance(frame, leftPressed, rightPressed, launch);
        }

        foreach (PinballEvent e in _sim.DrainEvents())
        {
            if (e.Kind == PinballEventKind.BallSaved)
                ShowBanner("BALL SAVED");
            else if (e.Kind == PinballEventKind.Orbit)
                ShowBanner("ORBIT 1000");
            else if (e.Kind == PinballEventKind.TargetBankComplete)
                ShowBanner("MULTIPLIER X" + _sim.Multiplier);
            else if (e.Kind == PinballEventKind.Drain && !_sim.GameOver && _sim.BallSaver <= 0f)
                ShowBanner("BALL LOST");
            else if (e.Kind == PinballEventKind.StarGateCapture)
                _audio?.Play(SoundGateCapture, 0.8f);
            else if (e.Kind == PinballEventKind.ExtraBall)
            {
                ShowBanner("EXTRA BALL  +" + PinballSimulation.StarGateBonus);
                _bannerTime = 2.4f;
                _audio?.Play(SoundExtraBall, 0.9f);
            }
            else if (e.Kind == PinballEventKind.StarGateEject)
                _audio?.Play(SoundGateEject, 0.7f);
        }

        _bannerTime = Mathf.Max(0f, _bannerTime - deltaTime);
        _nextDraw -= deltaTime;
        if (_nextDraw <= 0f)
        {
            _nextDraw = 1f / 60f;
            DrawFrame();
        }
    }

    public void Stop() { }

    public void Dispose()
    {
        _audio?.Dispose();
        _audio = null;
        if (_texture != null)
        {
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
        _pixels = null;
    }

    private void ShowBanner(string text)
    {
        _banner = text;
        _bannerTime = 1.4f;
    }

    private static Vector2 V(Vec2 v) => new(v.X, v.Y);

    private void DrawFrame()
    {
        if (_pixels == null || _texture == null)
            return;

        Fill(0, 0, Width, Height, new Color32(1, 4, 16, 255));
        Fill(13, 12, Width - 26, Height - 24, new Color32(6, 16, 49, 255));
        Fill(PinballSimulation.Left, PinballSimulation.Bottom, PinballSimulation.Right - PinballSimulation.Left, PinballSimulation.Top - PinballSimulation.Bottom, new Color32(12, 28, 82, 255));
        Fill(PinballSimulation.LaneLeft, PinballSimulation.Bottom, PinballSimulation.Right - PinballSimulation.LaneLeft, PinballSimulation.LaneGateY - PinballSimulation.Bottom, new Color32(7, 30, 68, 255));
        // The drain hole between the aprons is the only way out of the playfield.
        Fill(PinballSimulation.ApronBottomLeftX, 0f, PinballSimulation.ApronBottomRightX - PinballSimulation.ApronBottomLeftX, PinballSimulation.Bottom, new Color32(2, 3, 10, 255));

        DrawScenery();
        DrawColliders();
        DrawBall();
        DrawHud();

        _texture.SetPixels32(_pixels);
        _texture.Apply(false, false);
    }

    private void DrawScenery()
    {
        // Background-only decoration: single-pixel stars and small lamp dots that are clearly
        // not obstacles. Everything solid is drawn from collider data in DrawColliders.
        for (int i = 0; i < 58; i++)
        {
            int x = 34 + (i * 47 % 226);
            int y = 118 + (i * 71 % 280);
            Color32 colour = i % 3 == 0 ? new Color32(70, 72, 190, 255) : new Color32(80, 160, 200, 255);
            Fill(x, y, 1, 1, colour);
        }
        for (int i = 0; i < 6; i++)
        {
            int y = 200 + i * 30;
            Fill(30, y, 2, 2, i % 2 == 0 ? new Color32(255, 93, 175, 255) : new Color32(86, 231, 255, 255));
            Fill(262, y, 2, 2, i % 2 == 0 ? new Color32(86, 231, 255, 255) : new Color32(255, 93, 175, 255));
        }
        // STAR GATE saucer drawn exactly at its capture zone, label split around it.
        bool lit = _sim.StarGateLit;
        bool holding = _sim.StarGateHolding;
        bool pulse = ((int)(Time.unscaledTime * 8f) & 1) == 0;
        Vector2 gate = new(PinballSimulation.StarGateX, PinballSimulation.StarGateY);
        int gateRadius = Mathf.RoundToInt(PinballSimulation.StarGateCaptureRadius);
        Circle(gate, gateRadius + 2, holding && pulse ? new Color32(255, 255, 210, 255) : new Color32(14, 34, 96, 255));
        Ring(gate, gateRadius + 2, 2, lit || holding ? (pulse ? new Color32(120, 220, 255, 255) : new Color32(46, 120, 255, 255)) : new Color32(40, 64, 120, 255));
        Circle(gate, gateRadius - 3, new Color32(4, 8, 30, 255));
        Color32 label = lit || holding ? new Color32(255, 226, 96, 255) : new Color32(120, 110, 70, 255);
        DrawText("STAR", (int)PinballSimulation.StarGateX - gateRadius - 4 - TextWidth("STAR", 1), 433, 1, label);
        DrawText("GATE", (int)PinballSimulation.StarGateX + gateRadius + 5, 433, 1, label);
        DrawText("FIRE", (int)PinballSimulation.LaneLeft + 3, 30, 1, new Color32(230, 246, 255, 255));
    }

    private void DrawColliders()
    {
        foreach (PinballSegment segment in _sim.Segments)
        {
            int thickness = Mathf.Max(2, Mathf.RoundToInt(segment.Radius * 2f));
            Vector2 a = V(segment.A);
            Vector2 b = V(segment.B);
            switch (segment.Kind)
            {
                case PinballSegmentKind.Target:
                    bool lit = (_sim.TargetsLit & (1 << segment.TargetIndex)) != 0 || _sim.TargetFlash(segment.TargetIndex) > 0f;
                    Line(a, b, thickness + 2, new Color32(230, 244, 255, 255));
                    Line(a, b, thickness, lit ? new Color32(255, 232, 92, 255) : new Color32(69, 115, 213, 255));
                    break;
                case PinballSegmentKind.OneWayGate:
                    Line(a, b, 3, new Color32(255, 222, 97, 255));
                    break;
                case PinballSegmentKind.Guide:
                    Line(a, b, thickness + 2, new Color32(30, 18, 70, 255));
                    Line(a, b, thickness, new Color32(130, 90, 255, 255));
                    break;
                case PinballSegmentKind.Rail:
                    Line(a, b, thickness + 2, new Color32(22, 69, 137, 255));
                    Line(a, b, thickness, new Color32(80, 235, 255, 255));
                    break;
                default:
                    Line(a, b, thickness + 1, new Color32(40, 226, 255, 255));
                    break;
            }
        }

        for (int i = 0; i < _sim.Circles.Count; i++)
        {
            PinballCircle circle = _sim.Circles[i];
            Vector2 center = V(circle.Center);
            int radius = Mathf.RoundToInt(circle.Radius);
            bool flash = _sim.CircleFlash(i) > 0f;
            switch (circle.Kind)
            {
                case PinballCircleKind.Bumper:
                    Circle(center, radius, new Color32(28, 15, 62, 255));
                    Ring(center, radius, 3, flash ? new Color32(255, 235, 121, 255) : new Color32(254, 75, 146, 255));
                    Circle(center, radius / 2, new Color32(37, 194, 255, 255));
                    Circle(center, 4, new Color32(239, 249, 255, 255));
                    break;
                case PinballCircleKind.Sling:
                    Circle(center, radius, flash ? new Color32(255, 214, 96, 255) : new Color32(210, 53, 111, 255));
                    Ring(center, radius, 2, new Color32(255, 214, 96, 255));
                    break;
                default:
                    Circle(center, radius, new Color32(20, 45, 111, 255));
                    Ring(center, radius, 2, new Color32(82, 221, 255, 255));
                    break;
            }
        }

        DrawFlipper(_sim.LeftFlipper);
        DrawFlipper(_sim.RightFlipper);
    }

    private void DrawFlipper(PinballFlipper flipper)
    {
        int width = Mathf.RoundToInt(flipper.Radius * 2f);
        Vector2 pivot = V(flipper.Pivot);
        Vector2 tip = V(flipper.Tip);
        Line(pivot, tip, width + 3, new Color32(38, 17, 39, 255));
        Line(pivot, tip, width, new Color32(255, 105, 76, 255));
        Line(pivot, tip, 2, new Color32(255, 230, 170, 255));
        Circle(pivot, Mathf.RoundToInt(flipper.Radius), new Color32(234, 238, 248, 255));
    }

    private void DrawBall()
    {
        if (_sim.GameOver)
            return;
        Vector2 ball = V(_sim.Ball);
        Circle(ball + new Vector2(2f, -2f), 6, new Color32(1, 2, 8, 180));
        Circle(ball, 6, new Color32(224, 246, 255, 255));
        Circle(ball + new Vector2(-2f, 2f), 2, new Color32(255, 255, 255, 255));
    }

    private void DrawHud()
    {
        DrawTextCentered("SPACE CADET", 464, 2, new Color32(123, 238, 255, 255));
        DrawText("SCORE " + _sim.Score.ToString("0000000"), 28, 442, 1, new Color32(255, 225, 93, 255));
        DrawText("BALL " + Mathf.Max(0, _sim.Balls), 218, 442, 1, new Color32(205, 228, 255, 255));
        DrawText("X" + _sim.Multiplier, 28, 13, 1, new Color32(255, 150, 67, 255));
        if (_sim.BallSaver > 0f && !_sim.WaitingToLaunch)
            DrawText("SAVE", 250, 13, 1, new Color32(120, 255, 160, 255));

        if (_sim.WaitingToLaunch)
            DrawTextCentered("SPACE LAUNCH", 220, 1, new Color32(255, 230, 105, 255));
        if (_bannerTime > 0f && !_sim.GameOver)
        {
            int bannerWidth = TextWidth(_banner, 1) + 12;
            Fill((Width - bannerWidth) / 2, 231, bannerWidth, 17, new Color32(4, 8, 30, 220));
            DrawTextCentered(_banner, 236, 1, new Color32(255, 255, 255, 255));
        }
        if (_sim.GameOver)
        {
            Fill(49, 208, 222, 48, new Color32(2, 3, 14, 245));
            Frame(49, 208, 222, 48, 2, new Color32(255, 87, 124, 255));
            DrawTextCentered("GAME OVER", 236, 2, new Color32(255, 102, 130, 255));
            DrawTextCentered("SPACE RESTART", 216, 1, new Color32(235, 240, 255, 255));
        }
    }

    private void Fill(float x, float y, float width, float height, Color32 colour)
        => Fill((int)x, (int)y, (int)width, (int)height, colour);

    private void Fill(int x, int y, int width, int height, Color32 colour)
    {
        if (_pixels == null)
            return;
        int minX = Mathf.Clamp(x, 0, Width);
        int maxX = Mathf.Clamp(x + width, 0, Width);
        int minY = Mathf.Clamp(y, 0, Height);
        int maxY = Mathf.Clamp(y + height, 0, Height);
        for (int py = minY; py < maxY; py++)
            for (int px = minX; px < maxX; px++)
                _pixels[py * Width + px] = colour;
    }

    private void Frame(int x, int y, int width, int height, int thickness, Color32 colour)
    {
        Fill(x, y, width, thickness, colour);
        Fill(x, y + height - thickness, width, thickness, colour);
        Fill(x, y, thickness, height, colour);
        Fill(x + width - thickness, y, thickness, height, colour);
    }

    private void Circle(Vector2 center, int radius, Color32 colour)
    {
        if (_pixels == null)
            return;
        int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
        int maxX = Mathf.Min(Width - 1, Mathf.CeilToInt(center.x + radius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
        int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(center.y + radius));
        int radiusSquared = radius * radius;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                if ((x - center.x) * (x - center.x) + (y - center.y) * (y - center.y) <= radiusSquared)
                    _pixels[y * Width + x] = colour;
    }

    private void Ring(Vector2 center, int radius, int thickness, Color32 colour)
    {
        if (_pixels == null)
            return;
        int outerSquared = radius * radius;
        int innerRadius = Mathf.Max(0, radius - thickness);
        int innerSquared = innerRadius * innerRadius;
        int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
        int maxX = Mathf.Min(Width - 1, Mathf.CeilToInt(center.x + radius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
        int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(center.y + radius));
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float distanceSquared = (x - center.x) * (x - center.x) + (y - center.y) * (y - center.y);
                if (distanceSquared <= outerSquared && distanceSquared >= innerSquared)
                    _pixels[y * Width + x] = colour;
            }
    }

    private void Line(Vector2 start, Vector2 end, int thickness, Color32 colour)
    {
        float distance = Vector2.Distance(start, end);
        int steps = Mathf.Max(1, Mathf.CeilToInt(distance));
        float radius = thickness * 0.5f;
        for (int i = 0; i <= steps; i++)
            Circle(Vector2.Lerp(start, end, i / (float)steps), Mathf.CeilToInt(radius), colour);
    }

    private void DrawTextCentered(string text, int y, int scale, Color32 colour)
        => DrawText(text, (Width - TextWidth(text, scale)) / 2, y, scale, colour);

    private static int TextWidth(string text, int scale) => text.Length * 6 * scale - scale;

    private void DrawText(string text, int x, int y, int scale, Color32 colour)
    {
        foreach (char raw in text)
        {
            string glyph = Glyph(char.ToUpperInvariant(raw));
            for (int row = 0; row < 7; row++)
                for (int column = 0; column < 5; column++)
                    if (glyph[row * 5 + column] == '1')
                        Fill(x + column * scale, y + (6 - row) * scale, scale, scale, colour);
            x += 6 * scale;
        }
    }

    private static string Glyph(char character) => ScheduleIArcade.Utils.PixelFont.Glyph(character);
}

using ScheduleIArcade.Configuration;
using ScheduleIArcade.Games.Pinball;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Games.AirHockey;

/// <summary>
/// A game that must not be charged when the session opens, only once it reports
/// <see cref="ReadyToCharge"/> (for air hockey: when both sides are occupied).
/// </summary>
internal interface IDeferredChargeGame
{
    bool ReadyToCharge { get; }
    bool Charged { get; }
    void ChargeAccepted();
    void ChargeRejected(string reason);
}

/// <summary>
/// Two-human air hockey on the arcade's Air Hockey table. LOCAL two-player on one machine:
/// player 1 (left goal) uses W/A/S/D, player 2 (right goal) uses the arrow keys and joins with
/// ENTER. The $10 is only taken when both sides are occupied and the match starts; leaving
/// before that costs nothing. First to 7 wins; ENTER for a rematch, ESC leaves.
/// No network sync: on a co-op server each player's table session is local to their own client.
/// </summary>
internal sealed class AirHockeyGame : IArcadeGame, IDeferredChargeGame
{
    public const int TextureWidth = 344;
    public const int TextureHeight = 172;
    private const float PixelsPerMetre = TextureWidth / AirHockeySimulation.Length; // 200 px/m

    private enum Phase
    {
        WaitingForPlayerTwo,
        AwaitingPayment,
        Countdown,
        Playing,
        GameOver,
        Refused
    }

    private const int SoundMallet = 0, SoundRail = 1, SoundGoal = 2;

    private readonly AirHockeySimulation _sim = new();
    private Texture2D? _texture;
    private Color32[]? _pixels;
    private Phase _phase = Phase.WaitingForPlayerTwo;
    private float _phaseTimer;
    private float _nextDraw;
    private string _message = string.Empty;
    private float _messageTime;
    private ProceduralCabinetAudio? _audio;

    public string Id => "air_hockey";
    public string DisplayName => "Air Hockey (2 players)";
    public Texture? ScreenTexture => _texture;
    public bool ReadyToCharge => _phase == Phase.AwaitingPayment;
    public bool Charged { get; private set; }
    public bool PlayerTwoJoined { get; private set; }

    public void Initialize()
    {
        if (_texture != null)
            return;
        _texture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_AirHockey",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _pixels = new Color32[TextureWidth * TextureHeight];
        Draw();
    }

    public void Start()
    {
        _sim.Reset();
        _phase = Phase.WaitingForPlayerTwo;
        PlayerTwoJoined = false;
    }

    public void AttachAudio(Transform speaker, ArcadePreferences preferences)
    {
        _audio?.Dispose();
        _audio = new ProceduralCabinetAudio(speaker, preferences, "AirHockey",
            (0.05f, 1400f, 70f), (0.06f, 700f, 55f), (0.35f, 260f, 9f));
    }

    public void ChargeAccepted()
    {
        Charged = true;
        _phase = Phase.Countdown;
        _phaseTimer = 2.4f;
        _sim.Reset();
    }

    public void ChargeRejected(string reason)
    {
        _phase = Phase.Refused;
        _message = reason;
        PlayerTwoJoined = false;
    }

    public void Tick(float deltaTime)
    {
        if (_texture == null)
            return;

        switch (_phase)
        {
            case Phase.WaitingForPlayerTwo:
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    PlayerTwoJoined = true;
                    _phase = Phase.AwaitingPayment; // the session takes the $10 now, then calls ChargeAccepted
                }
                break;
            case Phase.Refused:
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    _phase = Phase.WaitingForPlayerTwo;
                break;
            case Phase.Countdown:
                _phaseTimer -= deltaTime;
                if (_phaseTimer <= 0f)
                    _phase = Phase.Playing;
                break;
            case Phase.Playing:
            {
                float frame = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : deltaTime;
                _sim.Advance(frame, ReadStick(KeyCode.W, KeyCode.S, KeyCode.A, KeyCode.D), ReadStick(KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow));
                foreach (AirHockeyEvent e in _sim.DrainEvents())
                {
                    switch (e.Kind)
                    {
                        case AirHockeyEventKind.MalletHit: _audio?.Play(SoundMallet, 0.35f + 0.65f * e.Strength); break;
                        case AirHockeyEventKind.WallHit: _audio?.Play(SoundRail, 0.25f + 0.6f * e.Strength); break;
                        case AirHockeyEventKind.Goal:
                            _audio?.Play(SoundGoal, 0.8f);
                            Flash(e.Player == 0 ? "GOAL  PLAYER 1" : "GOAL  PLAYER 2");
                            break;
                        case AirHockeyEventKind.MatchWon:
                            _phase = Phase.GameOver;
                            break;
                    }
                }
                break;
            }
            case Phase.GameOver:
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    // Rematch inside the paid session, like the restart on the other machines.
                    _sim.Reset();
                    _phase = Phase.Countdown;
                    _phaseTimer = 2.4f;
                }
                break;
        }

        _messageTime = Mathf.Max(0f, _messageTime - deltaTime);
        _nextDraw -= deltaTime;
        if (_nextDraw <= 0f)
        {
            _nextDraw = 1f / 60f;
            Draw();
        }
    }

    private static Vec2 ReadStick(KeyCode up, KeyCode down, KeyCode left, KeyCode right)
    {
        float x = (Input.GetKey(right) ? 1f : 0f) - (Input.GetKey(left) ? 1f : 0f);
        float y = (Input.GetKey(up) ? 1f : 0f) - (Input.GetKey(down) ? 1f : 0f);
        return new Vec2(x, y);
    }

    private void Flash(string text)
    {
        _message = text;
        _messageTime = 1.2f;
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

    // ---------------------------------------------------------------- drawing

    private static int Px(float x) => Mathf.RoundToInt((x + AirHockeySimulation.HalfLength) * PixelsPerMetre);
    private static int Py(float y) => Mathf.RoundToInt((y + AirHockeySimulation.HalfWidth) * PixelsPerMetre);

    private void Draw()
    {
        if (_pixels == null || _texture == null)
            return;

        var surface = new Color32(214, 236, 246, 255);
        var line = new Color32(220, 40, 60, 255);
        var blue = new Color32(40, 90, 220, 255);
        Fill(0, 0, TextureWidth, TextureHeight, surface);
        // Air holes.
        for (int y = 6; y < TextureHeight; y += 10)
            for (int x = 6; x < TextureWidth; x += 10)
                Fill(x, y, 1, 1, new Color32(170, 196, 210, 255));
        // Centre line, circle and goal creases.
        Fill(TextureWidth / 2 - 1, 0, 3, TextureHeight, line);
        Ring(TextureWidth / 2, TextureHeight / 2, 30, 2, line);
        Ring(0, TextureHeight / 2, Mathf.RoundToInt(AirHockeySimulation.GoalHalfWidth * PixelsPerMetre) + 14, 2, blue);
        Ring(TextureWidth, TextureHeight / 2, Mathf.RoundToInt(AirHockeySimulation.GoalHalfWidth * PixelsPerMetre) + 14, 2, blue);
        int goalHalf = Mathf.RoundToInt(AirHockeySimulation.GoalHalfWidth * PixelsPerMetre);
        Fill(0, TextureHeight / 2 - goalHalf, 4, goalHalf * 2, new Color32(20, 20, 26, 255));
        Fill(TextureWidth - 4, TextureHeight / 2 - goalHalf, 4, goalHalf * 2, new Color32(20, 20, 26, 255));

        // Mallets and puck.
        int malletR = Mathf.RoundToInt(AirHockeySimulation.MalletRadius * PixelsPerMetre);
        int puckR = Mathf.RoundToInt(AirHockeySimulation.PuckRadius * PixelsPerMetre);
        DrawMallet(_sim.Mallet(0), malletR, new Color32(230, 50, 60, 255));
        DrawMallet(_sim.Mallet(1), malletR, new Color32(40, 110, 235, 255));
        if (_phase != Phase.GameOver)
        {
            Disc(Px(_sim.Puck.X), Py(_sim.Puck.Y), puckR, new Color32(25, 25, 30, 255));
            Disc(Px(_sim.Puck.X), Py(_sim.Puck.Y), puckR - 3, new Color32(60, 60, 70, 255));
        }

        // Score.
        DrawText(_sim.Score(0).ToString(), TextureWidth / 2 - 30, TextureHeight - 22, 3, new Color32(230, 50, 60, 255));
        DrawText(_sim.Score(1).ToString(), TextureWidth / 2 + 16, TextureHeight - 22, 3, new Color32(40, 110, 235, 255));
        DrawText("P1 WASD", 6, 4, 1, new Color32(160, 30, 40, 255));
        DrawText("P2 ARROWS", TextureWidth - 60, 4, 1, new Color32(30, 70, 170, 255));

        switch (_phase)
        {
            case Phase.WaitingForPlayerTwo:
                Banner("WAITING FOR PLAYER 2", "P2 PRESS ENTER TO JOIN   ESC LEAVE  NO CHARGE YET");
                break;
            case Phase.AwaitingPayment:
                Banner("STARTING MATCH", "$10");
                break;
            case Phase.Refused:
                Banner(_message.Length > 0 ? _message : "CANNOT START", "ENTER TRY AGAIN   ESC LEAVE");
                break;
            case Phase.Countdown:
                Banner("FIRST TO 7", Mathf.CeilToInt(_phaseTimer).ToString());
                break;
            case Phase.GameOver:
                Banner(_sim.Winner == 0 ? "PLAYER 1 WINS" : "PLAYER 2 WINS", "ENTER REMATCH   ESC LEAVE");
                break;
            default:
                if (_messageTime > 0f)
                    Banner(_message, string.Empty);
                break;
        }

        _texture.SetPixels32(_pixels);
        _texture.Apply(false, false);
    }

    private void DrawMallet(Vec2 p, int radius, Color32 colour)
    {
        int x = Px(p.X), y = Py(p.Y);
        Disc(x, y, radius, colour);
        Disc(x, y, radius - 4, new Color32((byte)(colour.r * 0.7f), (byte)(colour.g * 0.7f), (byte)(colour.b * 0.7f), 255));
        Disc(x, y, radius / 3, colour);
    }

    private void Banner(string title, string subtitle)
    {
        int width = Math.Max(TextWidth(title, 2), TextWidth(subtitle, 1)) + 16;
        int x = (TextureWidth - width) / 2;
        Fill(x, 60, width, 50, new Color32(8, 10, 20, 235));
        DrawTextCentered(title, 88, 2, new Color32(255, 236, 120, 255));
        if (subtitle.Length > 0)
            DrawTextCentered(subtitle, 70, 1, new Color32(230, 240, 255, 255));
    }

    private void Fill(int x, int y, int width, int height, Color32 colour)
    {
        if (_pixels == null) return;
        int minX = Mathf.Clamp(x, 0, TextureWidth), maxX = Mathf.Clamp(x + width, 0, TextureWidth);
        int minY = Mathf.Clamp(y, 0, TextureHeight), maxY = Mathf.Clamp(y + height, 0, TextureHeight);
        for (int py = minY; py < maxY; py++)
            for (int px = minX; px < maxX; px++)
                _pixels[py * TextureWidth + px] = colour;
    }

    private void Disc(int cx, int cy, int radius, Color32 colour)
    {
        if (_pixels == null || radius <= 0) return;
        int r2 = radius * radius;
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(TextureHeight - 1, cy + radius); y++)
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(TextureWidth - 1, cx + radius); x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2)
                    _pixels[y * TextureWidth + x] = colour;
    }

    private void Ring(int cx, int cy, int radius, int thickness, Color32 colour)
    {
        if (_pixels == null) return;
        int outer = radius * radius, inner = (radius - thickness) * (radius - thickness);
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(TextureHeight - 1, cy + radius); y++)
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(TextureWidth - 1, cx + radius); x++)
            {
                int d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d <= outer && d >= inner)
                    _pixels[y * TextureWidth + x] = colour;
            }
    }

    private void DrawTextCentered(string text, int y, int scale, Color32 colour)
        => DrawText(text, (TextureWidth - TextWidth(text, scale)) / 2, y, scale, colour);

    private static int TextWidth(string text, int scale) => text.Length * 6 * scale - scale;

    private void DrawText(string text, int x, int y, int scale, Color32 colour)
    {
        foreach (char raw in text)
        {
            string glyph = PixelFont.Glyph(char.ToUpperInvariant(raw));
            for (int row = 0; row < 7; row++)
                for (int column = 0; column < 5; column++)
                    if (glyph[row * 5 + column] == '1')
                        Fill(x + column * scale, y + (6 - row) * scale, scale, scale, colour);
            x += 6 * scale;
        }
    }
}

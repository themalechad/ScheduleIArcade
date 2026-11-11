using ScheduleIArcade.Games.Pinball;

namespace ScheduleIArcade.Games.AirHockey;

internal enum AirHockeyEventKind
{
    WallHit,
    MalletHit,
    Goal,
    MatchWon
}

internal readonly struct AirHockeyEvent
{
    public AirHockeyEvent(AirHockeyEventKind kind, int player, float strength)
    {
        Kind = kind;
        Player = player;
        Strength = strength;
    }

    public AirHockeyEventKind Kind { get; }
    /// <summary>Scoring player for Goal/MatchWon, hitting player for MalletHit, -1 otherwise.</summary>
    public int Player { get; }
    public float Strength { get; }
}

/// <summary>
/// Headless two-player air hockey in metres, sized to the arcade table's measured play surface
/// (inner rails 0.86 m x 1.72 m). X runs along the table (player 0 defends -X, player 1 defends
/// +X); Y runs across it. Fixed 1/480 s steps like the pinball: puck travel per step (max 1.25 cm)
/// is far below the puck radius, so it cannot tunnel through rails or mallets.
/// </summary>
internal sealed class AirHockeySimulation
{
    public const float Length = 1.72f;
    public const float Width = 0.86f;
    public const float HalfLength = Length * 0.5f;
    public const float HalfWidth = Width * 0.5f;
    public const float GoalHalfWidth = 0.13f;
    public const float PuckRadius = 0.032f;
    public const float MalletRadius = 0.048f;
    public const float FixedStep = 1f / 480f;
    public const float MaxFrameTime = 0.1f;
    public const float MaxPuckSpeed = 6.0f;
    public const float MalletSpeed = 2.4f;
    public const float MalletAcceleration = 30f;
    public const float WallRestitution = 0.88f;
    public const float MalletRestitution = 0.92f;
    /// <summary>Velocity kept per second (air cushion is almost frictionless).</summary>
    public const float FrictionPerSecond = 0.22f;
    public const int WinningScore = 7;
    public const float ServeDelay = 0.9f;

    private readonly Vec2[] _mallet = new Vec2[2];
    private readonly Vec2[] _malletVelocity = new Vec2[2];
    private readonly int[] _score = new int[2];
    private readonly List<AirHockeyEvent> _events = new();
    private float _accumulator;
    private float _serveTimer;
    private readonly float[] _hitCooldown = new float[2];

    public AirHockeySimulation()
    {
        Reset();
    }

    public Vec2 Puck { get; private set; }
    public Vec2 PuckVelocity { get; private set; }
    public Vec2 Mallet(int player) => _mallet[player];
    public Vec2 MalletVelocity(int player) => _malletVelocity[player];
    public int Score(int player) => _score[player];
    public int Winner { get; private set; } = -1;
    public bool IsServing => _serveTimer > 0f;
    public float SimulatedTime { get; private set; }

    /// <summary>Invoked after every fixed step (tests assert invariants).</summary>
    public Action<AirHockeySimulation>? StepObserver { get; set; }

    public static Vec2 MalletHome(int player) => new(player == 0 ? -HalfLength + 0.16f : HalfLength - 0.16f, 0f);

    public void Reset()
    {
        _score[0] = _score[1] = 0;
        Winner = -1;
        _accumulator = 0f;
        for (int p = 0; p < 2; p++)
        {
            _mallet[p] = MalletHome(p);
            _malletVelocity[p] = Vec2.Zero;
        }
        Serve(towardPlayer: 0);
    }

    public List<AirHockeyEvent> DrainEvents()
    {
        var copy = new List<AirHockeyEvent>(_events);
        _events.Clear();
        return copy;
    }

    /// <summary>Test hook: place the puck directly.</summary>
    internal void PlacePuck(Vec2 position, Vec2 velocity)
    {
        Puck = position;
        PuckVelocity = velocity;
        _serveTimer = 0f;
    }

    /// <summary>Test hook: place a mallet directly.</summary>
    internal void PlaceMallet(int player, Vec2 position, Vec2 velocity)
    {
        _mallet[player] = ClampMallet(player, position);
        _malletVelocity[player] = velocity;
    }

    /// <summary>
    /// Advance by a real frame time with each player's stick input (-1..1 per axis, +X toward the
    /// opponent for player 0). Time above <see cref="MaxFrameTime"/> is dropped.
    /// </summary>
    public void Advance(float frameSeconds, Vec2 input0, Vec2 input1)
    {
        if (Winner >= 0 || !float.IsFinite(frameSeconds) || frameSeconds <= 0f)
            return;
        _accumulator += MathF.Min(frameSeconds, MaxFrameTime);
        while (_accumulator >= FixedStep)
        {
            _accumulator -= FixedStep;
            Step(FixedStep, input0, input1);
            if (Winner >= 0)
            {
                _accumulator = 0f;
                break;
            }
        }
    }

    private void Step(float dt, Vec2 input0, Vec2 input1)
    {
        SimulatedTime += dt;
        MoveMallet(0, input0, dt);
        MoveMallet(1, input1, dt);
        _hitCooldown[0] = MathF.Max(0f, _hitCooldown[0] - dt);
        _hitCooldown[1] = MathF.Max(0f, _hitCooldown[1] - dt);

        if (_serveTimer > 0f)
        {
            _serveTimer -= dt;
            StepObserver?.Invoke(this);
            return;
        }

        Vec2 v = PuckVelocity * MathF.Pow(1f - FrictionPerSecond, dt);
        v = v.ClampLength(MaxPuckSpeed);
        Puck += v * dt;
        PuckVelocity = v;

        for (int pass = 0; pass < 2; pass++)
        {
            ResolveMallet(0);
            ResolveMallet(1);
            if (ResolveRails())
                return; // goal scored; puck re-served
        }

        PuckVelocity = PuckVelocity.ClampLength(MaxPuckSpeed);
        StepObserver?.Invoke(this);
    }

    private void MoveMallet(int player, Vec2 input, float dt)
    {
        Vec2 wish = new Vec2(Math.Clamp(input.X, -1f, 1f), Math.Clamp(input.Y, -1f, 1f));
        if (wish.LengthSquared > 1f)
            wish = wish.Normalized();
        Vec2 targetVelocity = wish * MalletSpeed;
        Vec2 delta = targetVelocity - _malletVelocity[player];
        float maxDelta = MalletAcceleration * dt;
        if (delta.Length > maxDelta)
            delta = delta.Normalized() * maxDelta;
        Vec2 velocity = _malletVelocity[player] + delta;
        Vec2 previous = _mallet[player];
        Vec2 next = ClampMallet(player, previous + velocity * dt);
        _malletVelocity[player] = (next - previous) / dt;
        _mallet[player] = next;
    }

    /// <summary>Mallets stay on their own half, inside the rails.</summary>
    public static Vec2 ClampMallet(int player, Vec2 p)
    {
        float minX = player == 0 ? -HalfLength + MalletRadius : MalletRadius;
        float maxX = player == 0 ? -MalletRadius : HalfLength - MalletRadius;
        return new Vec2(Math.Clamp(p.X, minX, maxX), Math.Clamp(p.Y, -HalfWidth + MalletRadius, HalfWidth - MalletRadius));
    }

    private void ResolveMallet(int player)
    {
        Vec2 offset = Puck - _mallet[player];
        float minDistance = PuckRadius + MalletRadius;
        float distanceSquared = offset.LengthSquared;
        if (distanceSquared >= minDistance * minDistance)
            return;
        float distance = MathF.Sqrt(distanceSquared);
        Vec2 normal = distance > 1e-5f ? offset / distance : new Vec2(player == 0 ? 1f : -1f, 0f);
        Puck = _mallet[player] + normal * minDistance;

        // Mallet is driven by the player (effectively infinite mass): reflect in its frame.
        Vec2 relative = PuckVelocity - _malletVelocity[player];
        float approach = relative.Dot(normal);
        if (approach < 0f)
        {
            relative -= normal * ((1f + MalletRestitution) * approach);
            PuckVelocity = (relative + _malletVelocity[player]).ClampLength(MaxPuckSpeed);
            if (_hitCooldown[player] <= 0f)
            {
                _hitCooldown[player] = 0.08f;
                _events.Add(new AirHockeyEvent(AirHockeyEventKind.MalletHit, player, MathF.Min(1f, -approach / 3f)));
            }
        }

        // A puck pinned against a rail by a mallet is pushed out along the rail instead of
        // overlapping it; the rail pass that follows keeps it inside.
    }

    /// <returns>True if a goal was scored this step.</returns>
    private bool ResolveRails()
    {
        float x = Puck.X, y = Puck.Y, vx = PuckVelocity.X, vy = PuckVelocity.Y;
        bool hit = false;
        float strength = 0f;

        if (y < -HalfWidth + PuckRadius) { y = -HalfWidth + PuckRadius; if (vy < 0f) { strength = -vy; vy = -vy * WallRestitution; hit = true; } }
        else if (y > HalfWidth - PuckRadius) { y = HalfWidth - PuckRadius; if (vy > 0f) { strength = vy; vy = -vy * WallRestitution; hit = true; } }

        bool inMouth = MathF.Abs(y) < GoalHalfWidth - PuckRadius * 0.25f;
        if (inMouth)
        {
            // Goal when the whole puck has crossed the end line inside the mouth.
            if (x < -HalfLength - PuckRadius)
            {
                Goal(scorer: 1);
                return true;
            }
            if (x > HalfLength + PuckRadius)
            {
                Goal(scorer: 0);
                return true;
            }
        }
        else
        {
            if (x < -HalfLength + PuckRadius) { x = -HalfLength + PuckRadius; if (vx < 0f) { strength = MathF.Max(strength, -vx); vx = -vx * WallRestitution; hit = true; } }
            else if (x > HalfLength - PuckRadius) { x = HalfLength - PuckRadius; if (vx > 0f) { strength = MathF.Max(strength, vx); vx = -vx * WallRestitution; hit = true; } }
        }

        Puck = new Vec2(x, y);
        PuckVelocity = new Vec2(vx, vy);

        // Goal-mouth posts: the rail ends at (+-HalfLength, +-GoalHalfWidth) are round posts.
        foreach (float px in new[] { -HalfLength, HalfLength })
        {
            foreach (float py in new[] { -GoalHalfWidth, GoalHalfWidth })
            {
                Vec2 post = new(px, py);
                Vec2 offset = Puck - post;
                float d2 = offset.LengthSquared;
                if (d2 >= PuckRadius * PuckRadius || d2 < 1e-10f)
                    continue;
                float d = MathF.Sqrt(d2);
                Vec2 n = offset / d;
                Puck = post + n * PuckRadius;
                float approach = PuckVelocity.Dot(n);
                if (approach < 0f)
                {
                    PuckVelocity -= n * ((1f + WallRestitution) * approach);
                    strength = MathF.Max(strength, -approach);
                    hit = true;
                }
            }
        }

        if (hit && strength > 0.15f)
            _events.Add(new AirHockeyEvent(AirHockeyEventKind.WallHit, -1, MathF.Min(1f, strength / 3f)));
        return false;
    }

    private void Goal(int scorer)
    {
        _score[scorer]++;
        _events.Add(new AirHockeyEvent(AirHockeyEventKind.Goal, scorer, 1f));
        if (_score[scorer] >= WinningScore)
        {
            Winner = scorer;
            Puck = Vec2.Zero;
            PuckVelocity = Vec2.Zero;
            _events.Add(new AirHockeyEvent(AirHockeyEventKind.MatchWon, scorer, 1f));
            return;
        }
        // The player who conceded serves from their own half.
        Serve(towardPlayer: 1 - scorer);
    }

    private void Serve(int towardPlayer)
    {
        Puck = new Vec2(towardPlayer == 0 ? -0.30f : 0.30f, 0f);
        PuckVelocity = Vec2.Zero;
        _serveTimer = ServeDelay;
    }

    /// <summary>True when the puck is entirely on the visible table surface (or inside a goal mouth).</summary>
    public bool PuckOnTable()
    {
        if (!Puck.IsFinite)
            return false;
        if (MathF.Abs(Puck.Y) > HalfWidth - PuckRadius + 1e-3f)
            return false;
        return MathF.Abs(Puck.X) <= HalfLength + PuckRadius * 1.5f;
    }
}

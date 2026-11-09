namespace ScheduleIArcade.Games.Pinball;

/// <summary>Minimal 2D vector used by the pinball simulation. Pure managed code (no Unity
/// interop) so the physics can run and be regression-tested headlessly.</summary>
internal readonly struct Vec2
{
    public readonly float X;
    public readonly float Y;

    public Vec2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public static Vec2 Zero => new(0f, 0f);
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(float s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, float s) => new(a.X / s, a.Y / s);
    public float Dot(Vec2 other) => X * other.X + Y * other.Y;
    public float LengthSquared => X * X + Y * Y;
    public float Length => MathF.Sqrt(X * X + Y * Y);
    public Vec2 Perpendicular => new(-Y, X);
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y);
    public Vec2 Normalized()
    {
        float length = Length;
        return length > 1e-6f ? this / length : Zero;
    }
    public Vec2 ClampLength(float max)
    {
        float lengthSquared = LengthSquared;
        return lengthSquared > max * max ? this * (max / MathF.Sqrt(lengthSquared)) : this;
    }
    public override string ToString() => $"({X:0.0},{Y:0.0})";
}

internal enum PinballSegmentKind
{
    Wall,
    Guide,
    Rail,
    Target,
    OneWayGate
}

/// <summary>A static capsule: a segment with a collision radius. Rendering uses exactly the same
/// data, so every visible rail/wall/target is a real collider and nothing invisible collides.</summary>
internal sealed class PinballSegment
{
    public PinballSegment(string name, Vec2 a, Vec2 b, float radius, float restitution, PinballSegmentKind kind, int targetIndex = -1)
    {
        Name = name;
        A = a;
        B = b;
        Radius = radius;
        Restitution = restitution;
        Kind = kind;
        TargetIndex = targetIndex;
    }

    public string Name { get; }
    public Vec2 A { get; }
    public Vec2 B { get; }
    public float Radius { get; }
    public float Restitution { get; }
    public PinballSegmentKind Kind { get; }
    public int TargetIndex { get; }
}

internal enum PinballCircleKind
{
    Bumper,
    Sling,
    Post
}

internal sealed class PinballCircle
{
    public PinballCircle(string name, Vec2 center, float radius, float restitution, float kick, PinballCircleKind kind)
    {
        Name = name;
        Center = center;
        Radius = radius;
        Restitution = restitution;
        Kick = kick;
        Kind = kind;
    }

    public string Name { get; }
    public Vec2 Center { get; }
    public float Radius { get; }
    public float Restitution { get; }
    public float Kick { get; }
    public PinballCircleKind Kind { get; }
}

internal sealed class PinballFlipper
{
    public PinballFlipper(Vec2 pivot, float length, float radius, float restDegrees, float activeDegrees)
    {
        Pivot = pivot;
        Length = length;
        Radius = radius;
        RestDegrees = restDegrees;
        ActiveDegrees = activeDegrees;
        AngleDegrees = restDegrees;
    }

    public Vec2 Pivot { get; }
    public float Length { get; }
    public float Radius { get; }
    public float RestDegrees { get; }
    public float ActiveDegrees { get; }
    public float AngleDegrees { get; internal set; }
    /// <summary>Angular velocity of the most recent fixed step, radians per second.</summary>
    public float AngularVelocity { get; internal set; }
    public Vec2 Tip => Pivot + Direction(AngleDegrees) * Length;

    internal static Vec2 Direction(float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        return new Vec2(MathF.Cos(radians), MathF.Sin(radians));
    }
}

internal enum PinballEventKind
{
    Launch,
    Bumper,
    Sling,
    Target,
    TargetBankComplete,
    Orbit,
    Flipper,
    Drain,
    BallSaved,
    BallReturnedToPlunger,
    GameOver,
    StuckNudge,
    Escaped
}

internal readonly struct PinballEvent
{
    public PinballEvent(PinballEventKind kind, Vec2 position)
    {
        Kind = kind;
        Position = position;
    }

    public PinballEventKind Kind { get; }
    public Vec2 Position { get; }
}

/// <summary>
/// Frame-rate independent pinball simulation for the Space Cadet style table.
///
/// Mechanisms (each covered by tests/ScheduleIArcade.Tests):
/// * fixed 1/480 s sub-steps through an accumulator, so physics is identical at 30/60/144/240 Hz
///   and slow frames are caught up instead of silently slowing time;
/// * per-step travel (max 1.9 px) is far smaller than the 5.5 px ball radius plus the thinnest
///   collider, so walls, flippers, rails and targets cannot be tunnelled through;
/// * flippers are rotating capsules whose surface velocity is transferred to the ball;
/// * the playfield is closed except for the plunger lane and the centre drain between the
///   flippers, so the ball can only leave through the drain/relaunch flow;
/// * every collider is also what gets drawn.
/// </summary>
internal sealed class PinballSimulation
{
    public const float Width = 320f;
    public const float Height = 480f;
    public const float Left = 22f;
    public const float Right = 298f;
    public const float Bottom = 26f;
    public const float Top = 452f;
    public const float BallRadius = 5.5f;
    public const float FixedStep = 1f / 480f;
    public const float MaxFrameTime = 0.10f;
    public const float MaxSpeed = 900f;
    public const float Gravity = 430f;
    public const float LaunchSpeed = 660f;
    public const float LaneLeft = 271f;
    public const float LaneTopY = 390f;
    public const float LaneGateY = 398f;
    public const float PlungerY = 46f;
    public const float DrainY = 12f;
    public const float WallRadius = 2f;
    public const float GuideEndLeftX = 92f;
    public const float GuideEndRightX = 228f;
    public const float GuideEndY = 80f;
    public const float ApronBottomLeftX = 120f;
    public const float ApronBottomRightX = 200f;
    public const float OrbitRuleEndY = 408f;
    public const float OrbitRulePeakY = 420f;
    public const float BallSaverSeconds = 3f;
    public const int BallsPerGame = 3;

    private const float FlipperUpSpeed = 1400f; // degrees per second
    private const float FlipperDownSpeed = 900f;
    private const float StuckSpeed = 6f;
    private const float StuckSeconds = 3f;

    private readonly List<PinballSegment> _segments = new();
    private readonly List<PinballCircle> _circles = new();
    private readonly List<PinballEvent> _events = new();
    private readonly float[] _circleCooldown;
    private readonly float[] _targetCooldown;
    private float _accumulator;
    private float _stuckTimer;
    private float _orbitCooldown;
    private bool _orbitArmed = true;

    public PinballSimulation()
    {
        BuildTable();
        LeftFlipper = new PinballFlipper(new Vec2(96f, 74f), 50f, 6f, -28f, 30f);
        RightFlipper = new PinballFlipper(new Vec2(224f, 74f), 50f, 6f, 208f, 150f);
        _circleCooldown = new float[_circles.Count];
        _targetCooldown = new float[TargetCount];
        ResetGame();
    }

    public IReadOnlyList<PinballSegment> Segments => _segments;
    public IReadOnlyList<PinballCircle> Circles => _circles;
    public PinballFlipper LeftFlipper { get; }
    public PinballFlipper RightFlipper { get; }
    public Vec2 Ball { get; private set; }
    public Vec2 Velocity { get; private set; }
    public int Score { get; private set; }
    public int Balls { get; private set; }
    public int Multiplier { get; private set; }
    public int TargetsLit { get; private set; }
    public int TargetCount => 6;
    public bool WaitingToLaunch { get; private set; }
    public bool GameOver { get; private set; }
    public float BallSaver { get; private set; }
    public float SimulatedTime { get; private set; }
    public float CircleFlash(int index) => _circleCooldown[index];
    public float TargetFlash(int index) => _targetCooldown[index];
    public static Vec2 PlungerRestPosition => new((LaneLeft + Right) * 0.5f, PlungerY + WallRadius + BallRadius + 0.01f);

    /// <summary>Invoked after every fixed step; tests use it to assert invariants on every step.</summary>
    public Action<PinballSimulation>? StepObserver { get; set; }

    /// <summary>Returns and clears the events raised since the previous call.</summary>
    public List<PinballEvent> DrainEvents()
    {
        var copy = new List<PinballEvent>(_events);
        _events.Clear();
        return copy;
    }

    public void ResetGame()
    {
        Score = 0;
        Balls = BallsPerGame;
        Multiplier = 1;
        TargetsLit = 0;
        GameOver = false;
        _accumulator = 0f;
        LeftFlipper.AngleDegrees = LeftFlipper.RestDegrees;
        RightFlipper.AngleDegrees = RightFlipper.RestDegrees;
        Array.Clear(_circleCooldown, 0, _circleCooldown.Length);
        Array.Clear(_targetCooldown, 0, _targetCooldown.Length);
        PrepareBall();
    }

    /// <summary>Advances by a real frame time. Time above <see cref="MaxFrameTime"/> (a hitch or a
    /// paused frame) is dropped; everything else is simulated in fixed steps.</summary>
    public void Advance(float frameSeconds, bool leftPressed, bool rightPressed, bool launchPressed)
    {
        if (GameOver)
            return;

        if (!float.IsFinite(frameSeconds) || frameSeconds <= 0f)
            return;

        if (WaitingToLaunch && launchPressed)
            Launch();

        _accumulator += MathF.Min(frameSeconds, MaxFrameTime);
        while (_accumulator >= FixedStep)
        {
            _accumulator -= FixedStep;
            Step(FixedStep, leftPressed, rightPressed);
            if (GameOver)
            {
                _accumulator = 0f;
                break;
            }
        }
    }

    /// <summary>Test hook: places the ball directly, leaving the launch state.</summary>
    internal void PlaceBall(Vec2 position, Vec2 velocity)
    {
        WaitingToLaunch = false;
        Ball = position;
        Velocity = velocity;
        _stuckTimer = 0f;
    }

    internal void SetBallSaver(float seconds) => BallSaver = seconds;

    private void Launch()
    {
        WaitingToLaunch = false;
        Ball = PlungerRestPosition;
        Velocity = new Vec2(0f, LaunchSpeed);
        AddScore(250);
        Raise(PinballEventKind.Launch);
    }

    private void PrepareBall()
    {
        WaitingToLaunch = true;
        Ball = PlungerRestPosition;
        Velocity = Vec2.Zero;
        BallSaver = BallSaverSeconds;
        _stuckTimer = 0f;
    }

    private void Step(float dt, bool leftPressed, bool rightPressed)
    {
        SimulatedTime += dt;
        UpdateFlipper(LeftFlipper, leftPressed, dt);
        UpdateFlipper(RightFlipper, rightPressed, dt);

        for (int i = 0; i < _circleCooldown.Length; i++)
            _circleCooldown[i] = MathF.Max(0f, _circleCooldown[i] - dt);
        for (int i = 0; i < _targetCooldown.Length; i++)
            _targetCooldown[i] = MathF.Max(0f, _targetCooldown[i] - dt);
        _orbitCooldown = MathF.Max(0f, _orbitCooldown - dt);

        if (WaitingToLaunch)
        {
            StepObserver?.Invoke(this);
            return;
        }

        BallSaver = MathF.Max(0f, BallSaver - dt);

        Vec2 velocity = Velocity + new Vec2(0f, -Gravity * dt);
        velocity *= 1f - 0.12f * dt;
        velocity = velocity.ClampLength(MaxSpeed);
        Vec2 position = Ball + velocity * dt;
        Ball = position;
        Velocity = velocity;

        // Two relaxation passes resolve corner contacts (e.g. rail end + post) in one step.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _segments.Count; i++)
                ResolveSegment(_segments[i]);
            for (int i = 0; i < _circles.Count; i++)
                ResolveCircle(i);
            ResolveFlipper(LeftFlipper, pass == 0);
            ResolveFlipper(RightFlipper, pass == 0);
        }

        Velocity = Velocity.ClampLength(MaxSpeed);
        TrackOrbit();
        CheckStuck(dt);
        CheckDrainAndEscape();
        StepObserver?.Invoke(this);
    }

    private static void UpdateFlipper(PinballFlipper flipper, bool pressed, float dt)
    {
        float target = pressed ? flipper.ActiveDegrees : flipper.RestDegrees;
        float speed = pressed ? FlipperUpSpeed : FlipperDownSpeed;
        float previous = flipper.AngleDegrees;
        float delta = target - previous;
        float maxDelta = speed * dt;
        float applied = MathF.Abs(delta) <= maxDelta ? delta : MathF.Sign(delta) * maxDelta;
        flipper.AngleDegrees = previous + applied;
        flipper.AngularVelocity = applied * (MathF.PI / 180f) / dt;
    }

    private void ResolveSegment(PinballSegment segment)
    {
        Vec2 closest = ClosestPoint(Ball, segment.A, segment.B);
        Vec2 offset = Ball - closest;
        float minDistance = BallRadius + segment.Radius;
        float distanceSquared = offset.LengthSquared;
        if (distanceSquared >= minDistance * minDistance)
            return;

        Vec2 segmentNormal = (segment.B - segment.A).Perpendicular.Normalized();
        if (segment.Kind == PinballSegmentKind.OneWayGate)
        {
            // Gate normal points up (+Y). It stops balls falling back into the plunger lane but
            // lets a launched ball (centre below the gate, moving up) pass straight through.
            if (segmentNormal.Y < 0f)
                segmentNormal = -segmentNormal;
            if ((Ball - segment.A).Dot(segmentNormal) < 0f || Velocity.Dot(segmentNormal) >= 0f)
                return;
        }

        float distance = MathF.Sqrt(distanceSquared);
        Vec2 normal = distance > 1e-4f ? offset / distance : FacingNormal(segmentNormal);
        Ball = closest + normal * minDistance;
        float approach = Velocity.Dot(normal);
        if (approach >= 0f)
            return;

        Velocity = Bounce(Velocity, normal, segment.Restitution);
        if (segment.Kind == PinballSegmentKind.Target && segment.TargetIndex >= 0)
            HitTarget(segment.TargetIndex);
    }

    private void ResolveCircle(int index)
    {
        PinballCircle circle = _circles[index];
        Vec2 offset = Ball - circle.Center;
        float minDistance = BallRadius + circle.Radius;
        float distanceSquared = offset.LengthSquared;
        if (distanceSquared >= minDistance * minDistance)
            return;

        float distance = MathF.Sqrt(distanceSquared);
        Vec2 normal = distance > 1e-4f ? offset / distance : new Vec2(0f, 1f);
        Ball = circle.Center + normal * minDistance;
        float approach = Velocity.Dot(normal);
        if (approach >= 0f)
            return;

        Vec2 bounced = Bounce(Velocity, normal, circle.Restitution);
        if (circle.Kick > 0f && _circleCooldown[index] <= 0f)
        {
            bounced += normal * circle.Kick;
            _circleCooldown[index] = 0.12f;
            if (circle.Kind == PinballCircleKind.Bumper)
            {
                AddScore(500);
                Raise(PinballEventKind.Bumper);
            }
            else if (circle.Kind == PinballCircleKind.Sling)
            {
                AddScore(150);
                Raise(PinballEventKind.Sling);
            }
        }

        Velocity = bounced.ClampLength(MaxSpeed);
    }

    private void ResolveFlipper(PinballFlipper flipper, bool scoreContact)
    {
        Vec2 tip = flipper.Tip;
        Vec2 closest = ClosestPoint(Ball, flipper.Pivot, tip);
        Vec2 offset = Ball - closest;
        float minDistance = BallRadius + flipper.Radius;
        float distanceSquared = offset.LengthSquared;
        if (distanceSquared >= minDistance * minDistance)
            return;

        float distance = MathF.Sqrt(distanceSquared);
        Vec2 axisNormal = (tip - flipper.Pivot).Perpendicular.Normalized();
        Vec2 normal = distance > 1e-4f ? offset / distance : FacingNormal(axisNormal);
        Ball = closest + normal * minDistance;

        // Surface velocity of the rotating flipper at the contact point: omega x r.
        Vec2 lever = closest - flipper.Pivot;
        Vec2 surfaceVelocity = lever.Perpendicular * flipper.AngularVelocity;
        Vec2 relative = Velocity - surfaceVelocity;
        float approach = relative.Dot(normal);
        if (approach >= 0f)
            return;

        relative = Bounce(relative, normal, 0.35f);
        Velocity = (relative + surfaceVelocity).ClampLength(MaxSpeed);
        if (scoreContact && MathF.Abs(flipper.AngularVelocity) > 1f)
            Raise(PinballEventKind.Flipper);
    }

    private Vec2 FacingNormal(Vec2 normal) => Velocity.Dot(normal) > 0f ? -normal : normal;

    private static Vec2 Bounce(Vec2 velocity, Vec2 normal, float restitution)
    {
        float approach = velocity.Dot(normal);
        Vec2 tangent = velocity - normal * approach;
        return tangent * 0.985f - normal * (approach * restitution);
    }

    private void HitTarget(int index)
    {
        if (_targetCooldown[index] > 0f)
            return;

        _targetCooldown[index] = 0.28f;
        TargetsLit |= 1 << index;
        AddScore(250);
        Raise(PinballEventKind.Target);
        if (TargetsLit == (1 << TargetCount) - 1)
        {
            TargetsLit = 0;
            Multiplier = Math.Min(8, Multiplier + 1);
            AddScore(2500);
            Raise(PinballEventKind.TargetBankComplete);
        }
    }

    private void TrackOrbit()
    {
        // The upper orbit is the channel above the peaked rule. Crossing its centre line
        // scores once; the ball must leave the channel before it can score again.
        bool inChannel = Ball.Y > OrbitRulePeakY + 2f + BallRadius;
        if (!inChannel)
        {
            _orbitArmed = true;
            return;
        }

        if (_orbitArmed && _orbitCooldown <= 0f && MathF.Abs(Ball.X - 160f) < 4f)
        {
            _orbitArmed = false;
            _orbitCooldown = 0.5f;
            AddScore(1000);
            Raise(PinballEventKind.Orbit);
        }
    }

    private void CheckStuck(float dt)
    {
        if (Velocity.Length > StuckSpeed || IsInPlungerLane(Ball) || IsCradled(LeftFlipper) || IsCradled(RightFlipper))
        {
            _stuckTimer = 0f;
            return;
        }

        _stuckTimer += dt;
        if (_stuckTimer < StuckSeconds)
            return;

        // A ball cradled on a raised flipper is not stuck; anything else gets a table nudge.
        _stuckTimer = 0f;
        Velocity += new Vec2(Ball.X < 160f ? 60f : -60f, 240f);
        Raise(PinballEventKind.StuckNudge);
    }

    private bool IsCradled(PinballFlipper flipper)
    {
        if (MathF.Abs(flipper.AngleDegrees - flipper.ActiveDegrees) > 0.5f)
            return false;
        Vec2 closest = ClosestPoint(Ball, flipper.Pivot, flipper.Tip);
        return (Ball - closest).Length < BallRadius + flipper.Radius + 3f;
    }

    private void CheckDrainAndEscape()
    {
        if (!Ball.IsFinite || !Velocity.IsFinite || Ball.X < 0f || Ball.X > Width || Ball.Y > Height)
        {
            // Defensive only: the table is closed, so this never happens in tests. It still keeps
            // a corrupted state from leaving the ball off-screen forever.
            Raise(PinballEventKind.Escaped);
            PrepareBall();
            return;
        }

        if (IsInPlungerLane(Ball) && Ball.Y < PlungerY + BallRadius + 14f && Velocity.Length < 25f)
        {
            // A ball that falls back into the plunger lane comes to rest on the spring.
            Raise(PinballEventKind.BallReturnedToPlunger);
            PrepareBall();
            return;
        }

        if (Ball.Y >= DrainY)
            return;

        Raise(PinballEventKind.Drain);
        if (BallSaver > 0f)
        {
            Raise(PinballEventKind.BallSaved);
            PrepareBall();
            return;
        }

        Balls--;
        if (Balls <= 0)
        {
            GameOver = true;
            WaitingToLaunch = false;
            Ball = new Vec2(160f, DrainY);
            Velocity = Vec2.Zero;
            Raise(PinballEventKind.GameOver);
            return;
        }

        PrepareBall();
    }

    public static bool IsInPlungerLane(Vec2 point) => point.X > LaneLeft && point.Y < LaneGateY - BallRadius;

    private void AddScore(int value) => Score += value * Multiplier;

    private void Raise(PinballEventKind kind) => _events.Add(new PinballEvent(kind, Ball));

    internal static Vec2 ClosestPoint(Vec2 point, Vec2 a, Vec2 b)
    {
        Vec2 segment = b - a;
        float lengthSquared = segment.LengthSquared;
        if (lengthSquared < 1e-6f)
            return a;
        float t = Math.Clamp((point - a).Dot(segment) / lengthSquared, 0f, 1f);
        return a + segment * t;
    }

    private void BuildTable()
    {
        const float wallRadius = WallRadius;
        void Wall(string name, float ax, float ay, float bx, float by, float e = 0.55f, PinballSegmentKind kind = PinballSegmentKind.Wall)
            => _segments.Add(new PinballSegment(name, new Vec2(ax, ay), new Vec2(bx, by), wallRadius, e, kind));

        // Outer cabinet: closed on every side. The bottom is open only between the flippers.
        Wall("left wall", Left, Bottom - 30f, Left, 420f);
        Wall("top-left corner", Left, 420f, 54f, Top);
        Wall("top", 54f, Top, 262f, Top);
        Wall("top-right deflector", 262f, Top, Right, 416f, 0.6f);
        Wall("right wall", Right, 416f, Right, Bottom - 30f);

        // Plunger lane: its own wall, the spring floor and a one-way gate at the lane exit.
        Wall("lane wall", LaneLeft, Bottom - 30f, LaneLeft, LaneTopY);
        Wall("plunger", LaneLeft, PlungerY, Right, PlungerY, 0.15f);
        // Sloped toward the playfield: nothing on an inclined table may be a flat shelf, or a
        // slow ball comes to rest on it.
        Wall("lane gate", LaneLeft, LaneGateY, Right, LaneGateY + 14f, 0.4f, PinballSegmentKind.OneWayGate);

        // In-lane guides funnel both sides onto the flippers. Each ends on top of its flipper
        // pivot, so the ball rolls guide -> pivot -> flipper with no side gap and no notch in
        // which it could wedge.
        Wall("left guide", Left, 190f, GuideEndLeftX, GuideEndY, 0.35f, PinballSegmentKind.Guide);
        Wall("right guide", LaneLeft, 190f, GuideEndRightX, GuideEndY, 0.35f, PinballSegmentKind.Guide);
        // Apron walls under each flipper close the area between the guides and the drain.
        Wall("left apron", GuideEndLeftX, GuideEndY, ApronBottomLeftX, Bottom, 0.3f, PinballSegmentKind.Guide);
        Wall("right apron", GuideEndRightX, GuideEndY, ApronBottomRightX, Bottom, 0.3f, PinballSegmentKind.Guide);
        // Short vertical drain chute below the aprons: a drained ball falls straight down.
        Wall("left drain chute", ApronBottomLeftX, Bottom, ApronBottomLeftX, Bottom - 30f, 0.2f, PinballSegmentKind.Guide);
        Wall("right drain chute", ApronBottomRightX, Bottom, ApronBottomRightX, Bottom - 30f, 0.2f, PinballSegmentKind.Guide);

        // Upper rule that forms the orbit channel along the top of the table. It peaks in the
        // middle so a ball can never settle on it.
        Wall("orbit rule left", 84f, OrbitRuleEndY, 160f, OrbitRulePeakY, 0.5f, PinballSegmentKind.Rail);
        Wall("orbit rule right", 160f, OrbitRulePeakY, 236f, OrbitRuleEndY, 0.5f, PinballSegmentKind.Rail);

        // Drop-target banks: short capsules drawn as lit bars.
        float[] targetY = { 365f, 316f, 267f };
        for (int i = 0; i < 3; i++)
            _segments.Add(new PinballSegment($"left target {i + 1}", new Vec2(78f, targetY[i] - 10f), new Vec2(78f, targetY[i] + 10f), 5f, 0.5f, PinballSegmentKind.Target, i));
        for (int i = 0; i < 3; i++)
            _segments.Add(new PinballSegment($"right target {i + 1}", new Vec2(242f, targetY[i] - 10f), new Vec2(242f, targetY[i] + 10f), 5f, 0.5f, PinballSegmentKind.Target, 3 + i));

        _circles.Add(new PinballCircle("bumper left", new Vec2(116f, 323f), 20f, 0.9f, 170f, PinballCircleKind.Bumper));
        _circles.Add(new PinballCircle("bumper right", new Vec2(203f, 334f), 20f, 0.9f, 170f, PinballCircleKind.Bumper));
        _circles.Add(new PinballCircle("bumper centre", new Vec2(163f, 255f), 20f, 0.9f, 170f, PinballCircleKind.Bumper));
        // Slingshots sit on the in-lane guides (outside the flippers' shot line), as on a real table.
        _circles.Add(new PinballCircle("sling left", new Vec2(60f, 150f), 12f, 0.8f, 150f, PinballCircleKind.Sling));
        _circles.Add(new PinballCircle("sling right", new Vec2(243f, 150f), 12f, 0.8f, 150f, PinballCircleKind.Sling));
        _circles.Add(new PinballCircle("planet", new Vec2(160f, 395f), 18f, 0.7f, 0f, PinballCircleKind.Post));
        _circles.Add(new PinballCircle("left pivot", new Vec2(96f, 74f), 6f, 0.3f, 0f, PinballCircleKind.Post));
        _circles.Add(new PinballCircle("right pivot", new Vec2(224f, 74f), 6f, 0.3f, 0f, PinballCircleKind.Post));
    }
}

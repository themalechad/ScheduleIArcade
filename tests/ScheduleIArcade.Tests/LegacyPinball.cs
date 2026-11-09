using ScheduleIArcade.Games.Pinball;

namespace ScheduleIArcade.Tests;

/// <summary>
/// Line-for-line port of the v0.2.65 SpaceCadetPinballGame physics (Tick/Simulate and its
/// Resolve* helpers), with UnityEngine.Vector2/Mathf swapped for Vec2/MathF. It exists only to
/// measure the defects the new simulation fixes; it is not used by the mod.
/// </summary>
internal sealed class LegacyPinball
{
    private const float Left = 22f, Right = 298f, Top = 452f, BallRadius = 5.5f;
    private const float DrainGuardLeft = 123f, DrainGuardRight = 197f, DrainGuardY = 31f, MaxStopperSpeed = 380f;
    private const float LaunchBarrelLeft = 271f, LaunchBarrelRight = Right, LaunchBarrelBottom = 38f, LaunchBarrelExitY = 394f, LaunchBallRestY = 58f;
    private static readonly Vec2[] Bumpers = { new(116f, 323f), new(203f, 334f), new(163f, 255f) };
    private static readonly Vec2[] Targets = { new(78f, 365f), new(78f, 316f), new(78f, 267f), new(242f, 365f), new(242f, 316f), new(242f, 267f) };

    public Vec2 Ball;
    public Vec2 Velocity;
    public float LeftAngle = 22f, RightAngle = 158f;
    public bool WaitingToLaunch, InsideBarrel, GameOver;
    public int Balls = 3;
    public float BallSaver;
    public float SimulatedTime;
    public int ObstacleHits;
    public int Drains;
    public readonly List<float> DrainX = new();
    private float _flipperCooldown;
    private readonly float[] _bumperCooldowns = new float[3];
    private readonly float[] _targetCooldowns = new float[6];

    public LegacyPinball() => PrepareBall();

    private void PrepareBall()
    {
        WaitingToLaunch = true;
        InsideBarrel = true;
        Ball = new Vec2((LaunchBarrelLeft + LaunchBarrelRight) * 0.5f, LaunchBallRestY);
        Velocity = Vec2.Zero;
        BallSaver = 2.5f;
    }

    private static float MoveTowards(float current, float target, float maxDelta)
        => MathF.Abs(target - current) <= maxDelta ? target : current + MathF.Sign(target - current) * maxDelta;

    public void Tick(float deltaTime, bool leftPressed, bool rightPressed, bool launch)
    {
        if (GameOver)
            return;
        LeftAngle = MoveTowards(LeftAngle, leftPressed ? 67f : 22f, deltaTime * 540f);
        RightAngle = MoveTowards(RightAngle, rightPressed ? 113f : 158f, deltaTime * 540f);
        if (WaitingToLaunch)
        {
            if (launch)
            {
                WaitingToLaunch = false;
                Velocity = new Vec2(0f, 560f);
            }
            return;
        }

        int steps = Math.Clamp((int)MathF.Ceiling(deltaTime * 180f), 1, 10);
        float step = MathF.Min(deltaTime, 0.05f) / steps;
        for (int i = 0; i < steps && !WaitingToLaunch; i++)
            Simulate(step, leftPressed, rightPressed);
    }

    private void Simulate(float dt, bool leftPressed, bool rightPressed)
    {
        SimulatedTime += dt;
        _flipperCooldown = MathF.Max(0f, _flipperCooldown - dt);
        BallSaver = MathF.Max(0f, BallSaver - dt);
        for (int i = 0; i < 3; i++) _bumperCooldowns[i] = MathF.Max(0f, _bumperCooldowns[i] - dt);
        for (int i = 0; i < 6; i++) _targetCooldowns[i] = MathF.Max(0f, _targetCooldowns[i] - dt);

        Velocity += new Vec2(0f, -260f) * dt;
        Velocity *= MathF.Pow(0.996f, dt * 60f);
        Ball += Velocity * dt;

        ResolveWalls();
        TryEnterLaunchBarrel();
        ResolveLaunchBarrel();
        if (InsideBarrel)
            return;

        ResolveBumpers();
        ResolveTargets();
        ResolveSlings();
        ResolveFlipper(new Vec2(96f, 74f), 54f, LeftAngle, leftPressed, true);
        ResolveFlipper(new Vec2(224f, 74f), 54f, RightAngle, rightPressed, false);
        ResolveDrainGuard();

        if (Ball.Y < 10f)
        {
            if (BallSaver > 0f)
            {
                Ball = new Vec2(160f, 130f);
                Velocity = new Vec2(24f, 410f);
            }
            else
            {
                Drains++;
                DrainX.Add(Ball.X);
                Balls--;
                if (Balls <= 0) { GameOver = true; WaitingToLaunch = false; return; }
                PrepareBall();
            }
        }
    }

    private void ResolveWalls()
    {
        float x = Ball.X, y = Ball.Y, vx = Velocity.X, vy = Velocity.Y;
        if (x < Left + BallRadius) { x = Left + BallRadius; vx = MathF.Abs(vx) * 0.88f; }
        else if (x > Right - BallRadius) { x = Right - BallRadius; vx = -MathF.Abs(vx) * 0.88f; }
        if (y > Top - BallRadius) { y = Top - BallRadius; vy = -MathF.Abs(vy) * 0.88f; }
        Ball = new Vec2(x, y); Velocity = new Vec2(vx, vy);
    }

    private void ResolveLaunchBarrel()
    {
        if (!InsideBarrel) return;
        float x = Ball.X, y = Ball.Y, vx = Velocity.X, vy = Velocity.Y;
        float leftWall = LaunchBarrelLeft + BallRadius, rightWall = LaunchBarrelRight - BallRadius;
        if (x < leftWall) { x = leftWall; vx = MathF.Abs(vx) * 0.45f; }
        else if (x > rightWall) { x = rightWall; vx = -MathF.Abs(vx) * 0.45f; }
        if (!WaitingToLaunch && vy <= 0f && y <= LaunchBallRestY)
        {
            WaitingToLaunch = true;
            Ball = new Vec2((LaunchBarrelLeft + LaunchBarrelRight) * 0.5f, LaunchBallRestY);
            Velocity = Vec2.Zero; BallSaver = 2.5f; return;
        }
        if (y < LaunchBarrelBottom + BallRadius) { y = LaunchBarrelBottom + BallRadius; vy = MathF.Abs(vy) * 0.5f; }
        Ball = new Vec2(x, y); Velocity = new Vec2(vx, vy);
        if (y < LaunchBarrelExitY || vy <= 0f) return;
        InsideBarrel = false;
        Ball = new Vec2(LaunchBarrelLeft - BallRadius - 3f, LaunchBarrelExitY + 5f);
        Velocity = new Vec2(-170f, Math.Clamp(vy * 0.82f, 260f, 350f));
    }

    private void TryEnterLaunchBarrel()
    {
        if (InsideBarrel || WaitingToLaunch || Velocity.Y >= 0f) return;
        float leftWall = LaunchBarrelLeft + BallRadius, rightWall = LaunchBarrelRight - BallRadius;
        if (Ball.X < leftWall || Ball.X > rightWall || Ball.Y > LaunchBarrelExitY + 14f) return;
        InsideBarrel = true;
        Ball = new Vec2(Math.Clamp(Ball.X, leftWall, rightWall), Ball.Y);
    }

    private static Vec2 Reflect(Vec2 v, Vec2 n) => v - n * (2f * v.Dot(n));

    private void ResolveBumpers()
    {
        for (int i = 0; i < Bumpers.Length; i++)
        {
            Vec2 offset = Ball - Bumpers[i]; float d = offset.Length; const float r = 22f;
            if (d >= r + BallRadius) continue;
            Vec2 n = d > 0.001f ? offset / d : new Vec2(0f, 1f);
            Ball = Bumpers[i] + n * (r + BallRadius);
            Velocity = (Reflect(Velocity, n) + n * 58f).ClampLength(MaxStopperSpeed);
            if (_bumperCooldowns[i] <= 0f) { _bumperCooldowns[i] = 0.12f; ObstacleHits++; }
        }
    }

    private void ResolveTargets()
    {
        for (int i = 0; i < Targets.Length; i++)
        {
            Vec2 offset = Ball - Targets[i]; float d = offset.Length; const float r = 10f;
            if (d >= r + BallRadius) continue;
            Vec2 n = d > 0.001f ? offset / d : new Vec2(1f, 0f);
            Ball = Targets[i] + n * (r + BallRadius);
            Velocity = (Reflect(Velocity, n) + n * 34f).ClampLength(MaxStopperSpeed);
            if (_targetCooldowns[i] <= 0f) { _targetCooldowns[i] = 0.28f; ObstacleHits++; }
        }
    }

    private void ResolveSlings()
    {
        ResolveCircle(new Vec2(82f, 138f), 20f, 52f);
        ResolveCircle(new Vec2(238f, 138f), 20f, 52f);
        if (Ball.Y > 397f && Ball.X > 84f && Ball.X < 236f)
            Velocity = new Vec2(Velocity.X, -MathF.Abs(Velocity.Y));
    }

    private void ResolveCircle(Vec2 c, float radius, float kick)
    {
        Vec2 offset = Ball - c; float d = offset.Length;
        if (d >= radius + BallRadius) return;
        Vec2 n = d > 0.001f ? offset / d : new Vec2(0f, 1f);
        Ball = c + n * (radius + BallRadius);
        Velocity = (Reflect(Velocity, n) + n * kick).ClampLength(MaxStopperSpeed);
        ObstacleHits++;
    }

    private void ResolveFlipper(Vec2 pivot, float length, float degrees, bool pressed, bool left)
    {
        float rad = degrees * MathF.PI / 180f;
        Vec2 tip = pivot + new Vec2(MathF.Cos(rad), MathF.Sin(rad)) * length;
        Vec2 closest = PinballSimulation.ClosestPoint(Ball, pivot, tip);
        Vec2 offset = Ball - closest; float d = offset.Length; const float thickness = 8f;
        if (d >= BallRadius + thickness) return;
        Vec2 n = d > 0.001f ? offset / d : new Vec2(-MathF.Sin(rad), MathF.Cos(rad));
        Ball = closest + n * (BallRadius + thickness);
        Velocity = Reflect(Velocity, n);
        Velocity += n * 95f + new Vec2(left ? -32f : 32f, pressed ? 455f : 90f);
        Velocity = Velocity.ClampLength(640f);
    }

    private void ResolveDrainGuard()
    {
        if (Velocity.Y >= 0f || Ball.X < DrainGuardLeft - BallRadius || Ball.X > DrainGuardRight + BallRadius || Ball.Y > DrainGuardY + BallRadius)
            return;
        Ball = new Vec2(Ball.X, DrainGuardY + BallRadius);
        Velocity = new Vec2(Velocity.X * 0.92f, MathF.Max(82f, MathF.Abs(Velocity.Y) * 0.76f)).ClampLength(MaxStopperSpeed);
    }
}

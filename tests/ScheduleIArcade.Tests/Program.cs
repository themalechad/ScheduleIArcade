using ScheduleIArcade.Games.Pinball;
using ScheduleIArcade.Tests;

var tests = new (string Name, Action Body)[]
{
    ("launch leaves the plunger lane and enters the playfield", PinballTests.LaunchEntersPlayfield),
    ("flipper transfers its swing to the ball", PinballTests.FlipperImpulse),
    ("bumper contact reflects and kicks", PinballTests.BumperContact),
    ("drawn rails and targets are real colliders", PinballTests.DrawnGeometryCollides),
    ("max-speed shots never tunnel through walls, targets or flippers", PinballTests.NoTunnelling),
    ("side lanes are closed: dropped balls reach the flippers, not the drain", PinballTests.SideLanesClosed),
    ("drain -> ball saver -> relaunch -> game over -> restart", PinballTests.DrainRelaunchFlow),
    ("frame-rate independence and invariants over long autoplay", PinballTests.FrameRateIndependence),
    ("legacy v0.2.65 physics reproduces the reported defects", PinballTests.LegacyDefectsReproduced),
    ("arcade audio defaults are much quieter than v0.3.0 and follow the game's volume", AudioTests.DefaultsAreQuieter),
    ("arcade audio falloff is silent outside the building", AudioTests.FalloffStopsAtStreet),
    ("overlapping cabinet voices do not stack loudness", AudioTests.VoicesDoNotStack),
};

int failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {ex.Message}");
    }
}

Console.WriteLine(failed == 0 ? $"\nAll {tests.Length} tests passed." : $"\n{failed} of {tests.Length} tests FAILED.");
return failed == 0 ? 0 : 1;

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}

internal static class PinballTests
{
    private const float Dt60 = 1f / 60f;

    private static List<PinballEvent> Run(PinballSimulation sim, float seconds, float dt, Func<PinballSimulation, (bool l, bool r, bool launch)>? input = null)
    {
        var events = new List<PinballEvent>();
        for (float t = 0f; t < seconds; t += dt)
        {
            var (l, r, launch) = input?.Invoke(sim) ?? (false, false, false);
            sim.Advance(dt, l, r, launch);
            events.AddRange(sim.DrainEvents());
        }
        return events;
    }

    public static void LaunchEntersPlayfield()
    {
        var sim = new PinballSimulation();
        Assert.True(sim.WaitingToLaunch, "a fresh game should wait on the plunger");
        bool left = false;
        bool first = true;
        Run(sim, 2.5f, Dt60, s =>
        {
            if (s.Ball.X < PinballSimulation.LaneLeft - PinballSimulation.BallRadius)
                left = true;
            bool launch = first;
            first = false;
            return (false, false, launch);
        });
        Assert.True(left, $"launched ball never left the plunger lane (ball {sim.Ball})");
    }

    public static void FlipperImpulse()
    {
        var sim = new PinballSimulation();
        // Drop the ball onto the middle of the resting left flipper, then fire it.
        Vec2 mid = sim.LeftFlipper.Pivot + PinballFlipper.Direction(sim.LeftFlipper.RestDegrees) * 32f;
        sim.PlaceBall(mid + new Vec2(0f, 14f), new Vec2(0f, -40f));
        sim.SetBallSaver(10f);
        Run(sim, 0.15f, Dt60);
        float before = sim.Velocity.Y;
        float best = before;
        for (int i = 0; i < 6; i++)
        {
            sim.Advance(Dt60, true, false, false);
            best = MathF.Max(best, sim.Velocity.Y);
        }
        Assert.True(before < 60f && best > 500f, $"flipper shot too weak: vy before={before:0}, peak after={best:0}");
    }

    public static void BumperContact()
    {
        var sim = new PinballSimulation();
        PinballCircle bumper = sim.Circles.First(c => c.Kind == PinballCircleKind.Bumper);
        sim.PlaceBall(bumper.Center + new Vec2(0f, -60f), new Vec2(0f, 500f));
        sim.SetBallSaver(10f);
        var events = Run(sim, 0.25f, 1f / 144f);
        Assert.True(events.Any(e => e.Kind == PinballEventKind.Bumper), "ball fired straight at a bumper did not register a hit");
        Assert.True(sim.Velocity.Y < 0f || sim.Ball.Y < bumper.Center.Y, "ball did not bounce back off the bumper");
    }

    public static void DrawnGeometryCollides()
    {
        var sim = new PinballSimulation();
        // The orbit rule used to be drawn at y=416 but collided at y=397 (and the lane art
        // lines were not colliders at all). Fire a ball upward under the rule's midpoint.
        PinballSegment rule = sim.Segments.First(s => s.Name == "orbit rule right");
        Vec2 underRule = new(200f, 370f);
        sim.PlaceBall(underRule, new Vec2(0f, 600f));
        sim.SetBallSaver(10f);
        float highest = 0f;
        for (int i = 0; i < 60; i++)
        {
            sim.Advance(1f / 240f, false, false, false);
            highest = MathF.Max(highest, sim.Ball.Y);
        }
        float ruleYAt200 = rule.A.Y + (rule.B.Y - rule.A.Y) * (200f - rule.A.X) / (rule.B.X - rule.A.X);
        float limit = ruleYAt200 - rule.Radius - PinballSimulation.BallRadius + 1.5f;
        Assert.True(highest <= limit, $"ball passed the drawn orbit rule (max y {highest:0.0} > {limit:0.0})");

        PinballSegment target = sim.Segments.First(s => s.Kind == PinballSegmentKind.Target);
        sim = new PinballSimulation();
        sim.SetBallSaver(10f);
        sim.PlaceBall(new Vec2(target.A.X + 40f, (target.A.Y + target.B.Y) * 0.5f), new Vec2(-500f, 0f));
        var events = Run(sim, 0.2f, 1f / 240f);
        Assert.True(events.Any(e => e.Kind == PinballEventKind.Target), "ball fired at a drawn target did not hit it");
        Assert.True(sim.Ball.X > target.A.X, $"ball passed through the drawn target (x {sim.Ball.X:0.0})");
    }

    public static void NoTunnelling()
    {
        int shots = 0;
        var obstacles = new List<(string name, Vec2 aim)>();
        var probe = new PinballSimulation();
        foreach (PinballSegment s in probe.Segments.Where(s => s.Kind != PinballSegmentKind.OneWayGate))
            obstacles.Add((s.Name, (s.A + s.B) * 0.5f));
        obstacles.Add(("left flipper", probe.LeftFlipper.Pivot + PinballFlipper.Direction(probe.LeftFlipper.RestDegrees) * 30f));
        obstacles.Add(("right flipper", probe.RightFlipper.Pivot + PinballFlipper.Direction(probe.RightFlipper.RestDegrees) * 30f));

        foreach (var (name, aim) in obstacles)
        {
            for (int a = 0; a < 48; a++)
            {
                float angle = a * MathF.PI * 2f / 48f;
                Vec2 dir = new(MathF.Cos(angle), MathF.Sin(angle));
                Vec2 start = aim - dir * 40f;
                if (!InsidePlayable(start))
                    continue;
                var startProbe = new PinballSimulation();
                startProbe.PlaceBall(start, Vec2.Zero);
                if (PenetrationDepth(startProbe) > -1f)
                    continue; // start position touches another collider
                foreach (bool swing in new[] { false, true })
                {
                    var sim = new PinballSimulation();
                    sim.SetBallSaver(100f);
                    sim.PlaceBall(start, dir * PinballSimulation.MaxSpeed);
                    bool violated = false;
                    sim.StepObserver = s =>
                    {
                        if (!violated && PenetrationDepth(s) > 1.0f)
                            violated = true;
                    };
                    for (int i = 0; i < 30; i++)
                        sim.Advance(1f / 30f / 6f, swing, swing, false);
                    shots++;
                    Assert.True(!violated, $"{name}: ball penetrated a collider (shot angle {a}, swing {swing})");
                }
            }
        }
        Assert.True(shots > 500, $"too few tunnelling shots evaluated ({shots})");
    }

    // Penetration depth after every fixed step is the tunnelling measure: crossing a capsule
    // requires passing through a state that overlaps it by more than its radius.
    private static bool InsidePlayable(Vec2 p) => p.X > PinballSimulation.Left + 8f && p.X < PinballSimulation.LaneLeft - 8f && p.Y > 60f && p.Y < PinballSimulation.Top - 8f;

    /// <summary>Deepest overlap of the ball with any collider after a fixed step.</summary>
    internal static float PenetrationDepth(PinballSimulation s)
    {
        float worst = float.NegativeInfinity;
        foreach (PinballSegment seg in s.Segments)
        {
            if (seg.Kind == PinballSegmentKind.OneWayGate)
                continue;
            float d = (s.Ball - PinballSimulation.ClosestPoint(s.Ball, seg.A, seg.B)).Length;
            worst = MathF.Max(worst, PinballSimulation.BallRadius + seg.Radius - d);
        }
        foreach (PinballCircle c in s.Circles)
            worst = MathF.Max(worst, PinballSimulation.BallRadius + c.Radius - (s.Ball - c.Center).Length);
        foreach (PinballFlipper f in new[] { s.LeftFlipper, s.RightFlipper })
        {
            float d = (s.Ball - PinballSimulation.ClosestPoint(s.Ball, f.Pivot, f.Tip)).Length;
            worst = MathF.Max(worst, PinballSimulation.BallRadius + f.Radius - d);
        }
        return worst;
    }

    public static void SideLanesClosed()
    {
        // The old table had ~70 px open channels beside each flipper. Drop balls every 4 px
        // across the lower table and check, on every fixed step, that the ball never gets below
        // the flippers outside the centre drain funnel formed by the two aprons.
        int violations = 0, tested = 0, drains = 0;
        for (float x = PinballSimulation.Left + 8f; x < PinballSimulation.LaneLeft - 8f; x += 4f)
        {
            var sim = new PinballSimulation();
            sim.SetBallSaver(0f);
            sim.PlaceBall(new Vec2(x, 200f), new Vec2(0f, -120f));
            bool violated = false;
            sim.StepObserver = s =>
            {
                if (s.Ball.Y >= PinballSimulation.GuideEndY || s.WaitingToLaunch || PinballSimulation.IsInPlungerLane(s.Ball))
                    return;
                float t = Math.Min(1f, (PinballSimulation.GuideEndY - s.Ball.Y) / (PinballSimulation.GuideEndY - PinballSimulation.Bottom));
                float leftApron = PinballSimulation.GuideEndLeftX + t * (PinballSimulation.ApronBottomLeftX - PinballSimulation.GuideEndLeftX);
                float rightApron = PinballSimulation.GuideEndRightX + t * (PinballSimulation.ApronBottomRightX - PinballSimulation.GuideEndRightX);
                if (!violated && (s.Ball.X < leftApron - 1f || s.Ball.X > rightApron + 1f))
                {
                    violated = true;
                    Console.WriteLine($"      funnel violation: ball {s.Ball} v {s.Velocity} apron [{leftApron:0.0},{rightApron:0.0}]");
                }
            };
            var events = Run(sim, 6f, Dt60);
            tested++;
            foreach (PinballEvent drain in events.Where(e => e.Kind == PinballEventKind.Drain))
            {
                drains++;
                Assert.True(drain.Position.X > 118f && drain.Position.X < 202f, $"ball dropped at x={x} drained outside the centre drain (x={drain.Position.X:0})");
            }
            if (violated)
            {
                violations++;
                Console.WriteLine($"      drop at x={x:0} reached the area outside the drain funnel");
            }
        }
        Console.WriteLine($"      {tested} drops, {drains} centre drains, {violations} funnel violations");
        Assert.True(violations == 0, $"{violations}/{tested} drops escaped the flipper/drain funnel");
    }

    public static void DrainRelaunchFlow()
    {
        var sim = new PinballSimulation();
        sim.PlaceBall(new Vec2(160f, 120f), new Vec2(0f, -200f));
        sim.SetBallSaver(5f);
        var events = Run(sim, 2f, Dt60);
        Assert.True(events.Any(e => e.Kind == PinballEventKind.BallSaved), "ball saver did not save an early drain");
        Assert.True(sim.Balls == PinballSimulation.BallsPerGame && sim.WaitingToLaunch, "saved ball must return to the plunger without costing a ball");

        for (int expected = PinballSimulation.BallsPerGame - 1; expected >= 0; expected--)
        {
            sim.PlaceBall(new Vec2(160f, 120f), new Vec2(0f, -200f));
            sim.SetBallSaver(0f);
            events = Run(sim, 2f, Dt60);
            Assert.True(events.Count(e => e.Kind == PinballEventKind.Drain) == 1, "each centre drop must drain exactly once");
            if (expected > 0)
            {
                Assert.True(sim.Balls == expected, $"ball count {sim.Balls} != {expected}");
                Assert.True(sim.WaitingToLaunch, "next ball must wait on the plunger");
                Vec2 rest = PinballSimulation.PlungerRestPosition;
                Assert.True((sim.Ball - rest).Length < 0.01f, $"next ball not at the plunger ({sim.Ball})");
            }
        }
        Assert.True(sim.GameOver, "game should be over after the last ball drains");
        sim.ResetGame();
        Assert.True(!sim.GameOver && sim.Balls == PinballSimulation.BallsPerGame && sim.WaitingToLaunch, "restart did not reset the game");
    }

    /// <summary>A simple player: flips when the ball approaches a flipper, holds briefly, then
    /// releases (so it never cradles forever). Stateful hold timers are per autoplayer.</summary>
    private static Func<PinballSimulation, float, (bool, bool, bool)> Autoplayer()
    {
        float leftHold = 0f, rightHold = 0f, leftRest = 0f, rightRest = 0f;
        return (s, dt) =>
        {
            bool l = Decide(NearFlipper(s, s.LeftFlipper), ref leftHold, ref leftRest, dt);
            bool r = Decide(NearFlipper(s, s.RightFlipper), ref rightHold, ref rightRest, dt);
            return (l, r, s.WaitingToLaunch);
        };
    }

    private static bool Decide(bool want, ref float hold, ref float rest, float dt)
    {
        if (hold > 0f)
        {
            hold -= dt;
            if (hold <= 0f)
                rest = 0.25f;
            return true;
        }
        if (rest > 0f)
        {
            rest -= dt;
            return false;
        }
        if (!want)
            return false;
        hold = 0.18f;
        return true;
    }

    private static bool NearFlipper(PinballSimulation s, PinballFlipper f)
    {
        Vec2 restTip = f.Pivot + PinballFlipper.Direction(f.RestDegrees) * f.Length;
        float d = (s.Ball - PinballSimulation.ClosestPoint(s.Ball, f.Pivot, restTip)).Length;
        return d < 26f && s.Ball.Y > restTip.Y - 4f && s.Velocity.Y < 120f;
    }

    public static void FrameRateIndependence()
    {
        var profiles = new (string name, Func<Random, float> dt)[]
        {
            ("30 Hz", _ => 1f / 30f),
            ("60 Hz", _ => 1f / 60f),
            ("144 Hz", _ => 1f / 144f),
            ("240 Hz", _ => 1f / 240f),
            ("10 Hz", _ => 0.1f),
            ("jitter 8-200 Hz", r => 0.005f + (float)r.NextDouble() * 0.12f),
        };

        foreach (var (name, dtSource) in profiles)
        {
            var random = new Random(1234);
            var sim = new PinballSimulation();
            float worstPenetration = 0f;
            int outOfBounds = 0;
            sim.StepObserver = s =>
            {
                worstPenetration = MathF.Max(worstPenetration, PenetrationDepth(s));
                if (s.Ball.X < PinballSimulation.Left || s.Ball.X > PinballSimulation.Right || s.Ball.Y > PinballSimulation.Top)
                    outOfBounds++;
            };
            var input = Autoplayer();
            var events = new List<PinballEvent>();
            float wall = 0f;
            while (wall < 600f)
            {
                float dt = dtSource(random);
                var (l, r, launch) = input(sim, dt);
                sim.Advance(dt, l, r, launch);
                wall += MathF.Min(dt, PinballSimulation.MaxFrameTime);
                events.AddRange(sim.DrainEvents());
                if (sim.GameOver)
                    sim.ResetGame();
            }

            int drains = events.Count(e => e.Kind == PinballEventKind.Drain);
            int hits = events.Count(e => e.Kind is PinballEventKind.Bumper or PinballEventKind.Sling or PinballEventKind.Target or PinballEventKind.Orbit);
            int escaped = events.Count(e => e.Kind == PinballEventKind.Escaped);
            int sideDrains = events.Count(e => e.Kind == PinballEventKind.Drain && (e.Position.X < 118f || e.Position.X > 202f));
            foreach (var e in events.Where(e => e.Kind == PinballEventKind.Drain && (e.Position.X < 118f || e.Position.X > 202f)))
                Console.WriteLine($"      off-centre drain at {e.Position}");
            float timeRatio = sim.SimulatedTime / wall;
            Console.WriteLine($"      {name,-16} simulated/wall={timeRatio:0.000} drains={drains,3} sideDrains={sideDrains} obstacleHits/min={hits / 10f,6:0.0} escapes={escaped} outOfBounds={outOfBounds} worstPenetration={worstPenetration:0.00}px");
            Assert.True(MathF.Abs(timeRatio - 1f) < 0.01f, $"{name}: simulated time drifted ({timeRatio:0.000}x wall time)");
            Assert.True(escaped == 0 && outOfBounds == 0, $"{name}: ball left the table ({escaped} escapes, {outOfBounds} out-of-bounds steps)");
            Assert.True(sideDrains == 0, $"{name}: {sideDrains} drains happened outside the centre drain");
            Assert.True(worstPenetration < 1.0f, $"{name}: ball penetrated a collider by {worstPenetration:0.00}px");
            Assert.True(hits / 10f > 20f, $"{name}: only {hits / 10f:0.0} obstacle hits per minute");
            Assert.True(drains > 0, $"{name}: no drains in 10 minutes means the drain flow never ran");
        }
    }

    public static void LegacyDefectsReproduced()
    {
        // (a) Autoplay at 60 Hz: the old flipper always added upward "lift", even to a ball
        //     underneath it, so the ball bounced around below the drawn board (y < 26) without
        //     ever reaching the y < 10 drain check: the reported "flies off the bottom".
        var legacy = new LegacyPinball();
        int belowBoardFrames = 0;
        for (int i = 0; i < 60 * 600; i++)
        {
            bool l = legacy.Ball.X < 160f && legacy.Ball.Y < 110f && legacy.Ball.Y > 50f;
            bool r = legacy.Ball.X >= 160f && legacy.Ball.Y < 110f && legacy.Ball.Y > 50f;
            legacy.Tick(1f / 60f, l, r, legacy.WaitingToLaunch);
            if (!legacy.WaitingToLaunch && !legacy.InsideBarrel && legacy.Ball.Y < 26f)
                belowBoardFrames++;
            if (legacy.GameOver)
                legacy = new LegacyPinball();
        }
        Console.WriteLine($"      legacy autoplay @60 Hz: drains={legacy.Drains} framesWithBallBelowBoard={belowBoardFrames} obstacleHits/min={legacy.ObstacleHits / 10f:0.0}");
        Assert.True(belowBoardFrames > 60, "expected the legacy table to leave the ball below the board");

        // (b) No flipper input: drains fall straight through the ~70 px open side channels.
        legacy = new LegacyPinball();
        for (int i = 0; i < 60 * 600 && !legacy.GameOver; i++)
            legacy.Tick(1f / 60f, false, false, legacy.WaitingToLaunch);
        int side = legacy.DrainX.Count(x => x < 118f || x > 202f);
        Console.WriteLine($"      legacy no-input: drains={legacy.Drains} sideDrains={side} drainX=[{string.Join(",", legacy.DrainX.Select(x => x.ToString("0")))}]");
        Assert.True(legacy.Drains > 0 && side == legacy.Drains, "expected every legacy drain to bypass the flippers through a side channel");

        // (c) 10 Hz: the old Min(deltaTime, 0.05) clamp discards half of every frame.
        legacy = new LegacyPinball();
        for (int i = 0; i < 10 * 60; i++)
            legacy.Tick(0.1f, false, false, legacy.WaitingToLaunch);
        Console.WriteLine($"      legacy @10 Hz: simulated/wall={legacy.SimulatedTime / 60f:0.000}");
        Assert.True(legacy.SimulatedTime / 60f < 0.6f, "expected the legacy 0.05 s clamp to lose simulated time at 10 Hz");
    }
}

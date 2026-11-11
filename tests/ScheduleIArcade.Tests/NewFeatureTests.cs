using ScheduleIArcade.Games.AirHockey;
using ScheduleIArcade.Games.Pinball;
using ScheduleIArcade.Utils;

namespace ScheduleIArcade.Tests;

internal static class StarGateTests
{
    public static void CaptureAwardEject()
    {
        var sim = new PinballSimulation();
        sim.PlaceBall(new Vec2(200f, PinballSimulation.StarGateY), new Vec2(-280f, 0f));
        Assert.True(sim.StarGateLit, "the STAR GATE is lit at the start of a game");
        int balls = sim.Balls, score = sim.Score;
        var events = new List<PinballEvent>();
        bool captured = false;
        float t = 0f;
        for (; t < 0.6f; t += 1f / 240f)
        {
            sim.Advance(1f / 240f, false, false, false);
            events.AddRange(sim.DrainEvents());
            if (sim.StarGateHolding) { captured = true; break; }
        }
        Assert.True(captured, "a ball rolling through the orbit channel is captured by the lit gate");
        Assert.True(sim.Balls == balls + 1, $"EXTRA BALL: balls {balls} -> {sim.Balls}");
        Assert.True(sim.Score - score >= PinballSimulation.StarGateBonus && PinballSimulation.StarGateBonus >= 25000, $"bonus awarded ({sim.Score - score})");
        Assert.True(events.Any(e => e.Kind == PinballEventKind.ExtraBall) && events.Any(e => e.Kind == PinballEventKind.StarGateCapture), "capture + extra-ball events for the banner/sound");

        // Held in the saucer (sinks to the centre), then kicked out cleanly.
        for (int i = 0; i < 24; i++)
            sim.Advance(1f / 60f, false, false, false);
        Assert.True(sim.StarGateHolding && (sim.Ball - new Vec2(PinballSimulation.StarGateX, PinballSimulation.StarGateY)).Length < 0.5f, "ball sits in the saucer during the hold");
        bool ejected = false;
        for (float h = 0f; h < 2.5f && !ejected; h += 1f / 60f)
        {
            sim.Advance(1f / 60f, false, false, false);
            ejected = sim.DrainEvents().Any(e => e.Kind == PinballEventKind.StarGateEject);
        }
        Assert.True(ejected && !sim.StarGateHolding, "ball is ejected after the hold");
        for (int i = 0; i < 60; i++)
            sim.Advance(1f / 60f, false, false, false);
        Assert.True((sim.Ball - new Vec2(PinballSimulation.StarGateX, PinballSimulation.StarGateY)).Length > 20f, $"ball left the gate area (at {sim.Ball})");
        Assert.True(!sim.StarGateLit && sim.ExtraBallCollectedThisBall, "gate goes dark: one extra ball per ball");

        // Not farmable: a second pass while unlit is not captured and awards nothing.
        int ballsAfter = sim.Balls;
        sim.PlaceBall(new Vec2(200f, PinballSimulation.StarGateY), new Vec2(-280f, 0f));
        for (float h = 0f; h < 0.5f; h += 1f / 240f)
            sim.Advance(1f / 240f, false, false, false);
        Assert.True(!sim.StarGateHolding && sim.Balls == ballsAfter, "unlit gate does not capture or award");

        // Relit by the drop-target bank, but still only once per ball in play.
        sim.SetStarGateLit(true);
        sim.PlaceBall(new Vec2(200f, PinballSimulation.StarGateY), new Vec2(-280f, 0f));
        for (float h = 0f; h < 0.5f; h += 1f / 240f)
            sim.Advance(1f / 240f, false, false, false);
        Assert.True(sim.Balls == ballsAfter, "relit gate does not pay a second extra ball on the same ball");
    }

    public static void ReachableFromThePlayfield()
    {
        // A ball travelling up the left side enters the orbit channel and reaches the saucer.
        var sim = new PinballSimulation();
        sim.PlaceBall(new Vec2(40f, 300f), new Vec2(30f, 520f));
        bool captured = false;
        for (float t = 0f; t < 3f && !captured; t += 1f / 240f)
        {
            sim.Advance(1f / 240f, false, false, false);
            captured = sim.StarGateHolding;
        }
        Assert.True(captured, "a left-side orbit shot reaches the STAR GATE saucer");
    }
}

internal static class PinballViewTests
{
    public static void BallStaysInVisibleRect()
    {
        // Mapping helper: every wall endpoint maps inside the cabinet's inner rails.
        var sim = new PinballSimulation();
        foreach (PinballSegment s in sim.Segments)
        {
            foreach (Vec2 p in new[] { s.A, s.B })
            {
                var clamped = new Vec2(Math.Clamp(p.X, 0f, PinballSimulation.Width), Math.Clamp(p.Y, 0f, PinballSimulation.Height));
                var (x, z) = PinballPlayfieldMapping.SimToLocal(clamped);
                Assert.True(PinballPlayfieldMapping.LocalInsideRails(x, z), $"{s.Name} endpoint {p} maps to ({x:0.000},{z:0.000}) outside the rails");
            }
        }
        var (w, l) = PinballPlayfieldMapping.QuadSize();
        Assert.True(MathF.Abs(w / l - PinballSimulation.Width / PinballSimulation.Height) < 1e-3f, "quad keeps the texture aspect");
        Assert.True(w <= PinballPlayfieldMapping.AvailableWidth + 1e-4f && l <= PinballPlayfieldMapping.AvailableLength + 1e-4f, "quad fits inside rails and apron");
        var (u0, v0) = PinballPlayfieldMapping.SimToUv(new Vec2(0f, 0f));
        var (u1, v1) = PinballPlayfieldMapping.SimToUv(new Vec2(PinballSimulation.Width, PinballSimulation.Height));
        Assert.True(u0 == 0f && v0 == 0f && u1 == 1f && v1 == 1f, "sim rect == texture UV rect");

        // Long autoplay with flippers: the ball is always fully inside the visible rect.
        sim.StepObserver = s =>
        {
            if (!PinballSimulation.InsideVisibleRect(s.Ball))
                throw new Exception($"ball left the visible rect at {s.Ball}");
            var (x, z) = PinballPlayfieldMapping.SimToLocal(s.Ball);
            if (!PinballPlayfieldMapping.LocalInsideRails(x, z))
                throw new Exception($"ball at {s.Ball} maps under the cabinet rails ({x:0.000},{z:0.000})");
        };
        var random = new Random(11);
        for (int frame = 0; frame < 60 * 600; frame++)
        {
            if (sim.GameOver) sim.ResetGame();
            bool l2 = random.Next(6) == 0, r2 = random.Next(6) == 0;
            sim.Advance(1f / 60f, l2, r2, sim.WaitingToLaunch);
            sim.DrainEvents();
        }
    }
}

internal static class AirHockeyTests
{
    private const float Dt = 1f / 60f;

    private static AirHockeySimulation Ready()
    {
        var sim = new AirHockeySimulation();
        for (int i = 0; i < 70; i++) sim.Advance(Dt, default, default); // serve delay
        return sim;
    }

    public static void WallsAndFriction()
    {
        var sim = Ready();
        sim.PlacePuck(new Vec2(0.2f, 0f), new Vec2(0.3f, 3.0f));
        float maxY = 0f;
        for (int i = 0; i < 120; i++)
        {
            sim.Advance(Dt, default, default);
            maxY = MathF.Max(maxY, MathF.Abs(sim.Puck.Y));
        }
        Assert.True(maxY <= AirHockeySimulation.HalfWidth - AirHockeySimulation.PuckRadius + 1e-4f, "puck bounces off the side rails");
        float speed0 = new Vec2(0.3f, 3.0f).Length;
        Assert.True(sim.PuckVelocity.Length < speed0, "friction + rail losses slow the puck");
    }

    public static void MalletHitTransfersVelocity()
    {
        var sim = Ready();
        sim.PlacePuck(new Vec2(-0.30f, 0f), Vec2.Zero);
        sim.PlaceMallet(0, new Vec2(-0.50f, 0f), Vec2.Zero);
        List<AirHockeyEvent> events = new();
        for (int i = 0; i < 30; i++)
        {
            sim.Advance(Dt, new Vec2(1f, 0f), default);
            events.AddRange(sim.DrainEvents());
        }
        Assert.True(sim.PuckVelocity.X > 1.0f, $"a moving mallet drives the puck (vx {sim.PuckVelocity.X:0.00})");
        Assert.True(events.Any(e => e.Kind == AirHockeyEventKind.MalletHit && e.Player == 0), "mallet hit event for table sound");
    }

    public static void GoalsAndMatch()
    {
        var sim = Ready();
        sim.PlaceMallet(1, new Vec2(0.30f, 0.35f), Vec2.Zero);
        sim.PlacePuck(new Vec2(0.6f, 0.02f), new Vec2(3f, 0f));
        List<AirHockeyEvent> events = new();
        for (int i = 0; i < 30; i++) { sim.Advance(Dt, default, default); events.AddRange(sim.DrainEvents()); }
        Assert.True(sim.Score(0) == 1 && events.Any(e => e.Kind == AirHockeyEventKind.Goal && e.Player == 0), "puck through the +X mouth scores for player 1");
        Assert.True(sim.IsServing && sim.Puck.X > 0f, "player 2 (who conceded) serves from their half");

        sim = Ready();
        sim.PlaceMallet(1, new Vec2(0.30f, -0.35f), Vec2.Zero);
        sim.PlacePuck(new Vec2(0.6f, 0.30f), new Vec2(3f, 0f));
        for (int i = 0; i < 30; i++) sim.Advance(Dt, default, default);
        Assert.True(sim.Score(0) == 0 && sim.PuckVelocity.X < 0f, "outside the goal mouth the end rail bounces the puck");

        sim = Ready();
        for (int goal = 0; goal < AirHockeySimulation.WinningScore; goal++)
        {
            for (int i = 0; i < 70; i++) sim.Advance(Dt, default, default);
            sim.PlaceMallet(0, new Vec2(-0.30f, 0.35f), Vec2.Zero);
            sim.PlacePuck(new Vec2(-0.6f, 0f), new Vec2(-3f, 0f));
            for (int i = 0; i < 30; i++) sim.Advance(Dt, default, default);
        }
        Assert.True(sim.Score(1) == AirHockeySimulation.WinningScore && sim.Winner == 1, "first to 7 wins");
        var frozen = sim.Puck;
        sim.Advance(Dt, new Vec2(1, 1), new Vec2(1, 1));
        Assert.True(sim.Puck.X == frozen.X && sim.Puck.Y == frozen.Y, "no play after the match is won");
    }

    public static void RandomPlayInvariants()
    {
        var sim = new AirHockeySimulation();
        var random = new Random(5);
        int goals = 0;
        sim.StepObserver = s =>
        {
            if (!s.PuckOnTable())
                throw new Exception($"puck left the table at {s.Puck}");
            for (int p = 0; p < 2; p++)
            {
                Vec2 m = s.Mallet(p);
                if (p == 0 ? m.X > -AirHockeySimulation.MalletRadius + 1e-4f : m.X < AirHockeySimulation.MalletRadius - 1e-4f)
                    throw new Exception($"mallet {p} crossed the centre line ({m})");
            }
            if (s.PuckVelocity.Length > AirHockeySimulation.MaxPuckSpeed + 1e-3f)
                throw new Exception("puck exceeded max speed");
        };
        Vec2 a = default, b = default;
        for (int frame = 0; frame < 60 * 300 && sim.Winner < 0; frame++)
        {
            if (frame % 12 == 0)
            {
                // Simple chasers: each player drives toward the puck with noise.
                // Each player drives at a point just behind the puck (relative to the goal it attacks).
                Vec2 aimA = sim.Puck + new Vec2(-0.05f, (float)(random.NextDouble() - 0.5) * 0.04f) - sim.Mallet(0);
                Vec2 aimB = sim.Puck + new Vec2(0.05f, (float)(random.NextDouble() - 0.5) * 0.04f) - sim.Mallet(1);
                a = aimA.LengthSquared > 1e-6f ? aimA.Normalized() : default;
                b = aimB.LengthSquared > 1e-6f ? aimB.Normalized() : default;
            }
            sim.Advance(frame % 7 == 0 ? 1f / 30f : Dt, a, b);
            goals += sim.DrainEvents().Count(e => e.Kind == AirHockeyEventKind.Goal);
        }
        Console.WriteLine($"      air hockey autoplay: goals={goals} score={sim.Score(0)}-{sim.Score(1)} winner={sim.Winner} simulated={sim.SimulatedTime:0}s");
        Assert.True(goals > 0, "autoplay produces goals (puck never stuck forever)");
    }
}

internal static class SymlinkTests
{
    /// <summary>
    /// Reproduces the Vortex deployment: a SymbolicLink whose FileInfo.Length is 0 while the file opens
    /// fine. The 0.4.0 check (FileInfo.Length &lt; 4096) rejected it; ModFiles must accept it.
    /// </summary>
    public static void VortexSymlinkedWadIsAccepted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ScheduleIArcadeSymlinkTest_" + Guid.NewGuid().ToString("N"));
        string realDir = Path.Combine(dir, "staging");
        string modDir = Path.Combine(dir, "Mods", "ScheduleIArcade", "Doom");
        Directory.CreateDirectory(realDir);
        Directory.CreateDirectory(modDir);
        try
        {
            string real = Path.Combine(realDir, "freedoom1.wad");
            var bytes = new byte[64 * 1024];
            System.Text.Encoding.ASCII.GetBytes("IWAD").CopyTo(bytes, 0);
            File.WriteAllBytes(real, bytes);
            string link = Path.Combine(modDir, "freedoom1.wad");
            bool linked;
            try
            {
                File.CreateSymbolicLink(link, real);
                linked = true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                linked = false;
                Console.WriteLine($"      symlink creation denied on this machine ({ex.GetType().Name}: {ex.Message.Trim()}); testing the copied file only");
                File.Copy(real, link);
            }

            var info = new FileInfo(link);
            if (linked)
            {
                Console.WriteLine($"      symlink: FileInfo.Length={info.Length} attributes={info.Attributes} LinkTarget={info.LinkTarget}");
                Assert.True((info.Attributes & FileAttributes.ReparsePoint) != 0, "link is a reparse point");
                bool oldCheckRejected = !info.Exists || info.Length < 4096;
                Assert.True(oldCheckRejected, "the 0.4.0 FileInfo.Length check rejects the symlinked WAD (reproduced)");
            }
            Assert.True(ModFiles.IsDoomWad(link, requireIwad: true), "ModFiles accepts the (symlinked) IWAD by opening it");
            Assert.True(ModFiles.GetLength(link) == bytes.Length, "stream length is the real file size");
            Assert.True(ModFiles.ReadAllBytes(link)?.Length == bytes.Length, "ReadAllBytes follows the link (Assembly.Load(bytes) path)");
            string upper = Path.Combine(dir, "MODS", "scheduleiarcade", "DOOM", "FREEDOOM1.WAD");
            Assert.True(ModFiles.ResolveExisting(upper) != null, "lookup is case-insensitive");
            Assert.True(ModFiles.ResolveExisting(Path.Combine(modDir, "missing.wad")) == null, "a missing file is still missing");
            string bad = Path.Combine(modDir, "notawad.wad");
            File.WriteAllBytes(bad, new byte[8192]);
            Assert.True(!ModFiles.IsDoomWad(bad), "a file without an IWAD/PWAD header is rejected");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

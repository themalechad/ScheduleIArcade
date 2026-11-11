using ScheduleIArcade.Games.Pacman;

namespace ScheduleIArcade.Tests;

/// <summary>Arcade ghost behaviour (PacmanGhostAi) checked against the Pac-Man Dossier rules.</summary>
internal static class GhostTests
{
    private static readonly PacTile Pac = new(13, 7);

    public static void TargetSelection()
    {
        // Blinky: Pac-Man's tile.
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Blinky, Pac, PacTile.Left, new PacTile(0, 0), new PacTile(1, 1)), Pac, "Blinky targets Pac-Man");
        // Pinky: 4 ahead; facing up also 4 left (overflow bug).
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Pinky, Pac, PacTile.Left, default, default), new PacTile(9, 7), "Pinky 4 ahead (left)");
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Pinky, Pac, PacTile.Right, default, default), new PacTile(17, 7), "Pinky 4 ahead (right)");
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Pinky, Pac, PacTile.Down, default, default), new PacTile(13, 3), "Pinky 4 ahead (down)");
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Pinky, Pac, PacTile.Up, default, default), new PacTile(9, 11), "Pinky facing up: 4 up AND 4 left");
        // Inky: pivot 2 ahead, vector from Blinky doubled.
        var blinky = new PacTile(20, 12);
        // pivot (15,7): target = 2*pivot - blinky = (10, 2)
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Inky, Pac, PacTile.Right, blinky, default), new PacTile(10, 2), "Inky = 2*(Pac+2 ahead) - Blinky");
        // facing up: pivot (11, 9) -> (2, 6)
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Inky, Pac, PacTile.Up, blinky, default), new PacTile(2, 6), "Inky facing up keeps the 2-up-2-left bug");
        // Clyde: chase when farther than 8 tiles, else his scatter corner.
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Clyde, Pac, PacTile.Left, default, new PacTile(13, 20)), Pac, "Clyde far (13 tiles) chases");
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Clyde, Pac, PacTile.Left, default, new PacTile(13, 14)), PacmanGhostAi.ScatterCorner(GhostId.Clyde), "Clyde near (7 tiles) retreats");
        Expect(PacmanGhostAi.ChaseTarget(GhostId.Clyde, Pac, PacTile.Left, default, new PacTile(13, 15)), PacmanGhostAi.ScatterCorner(GhostId.Clyde), "Clyde at exactly 8 tiles retreats (needs > 8)");
        // Scatter corners: y = 30 is the top row.
        Assert.True(PacmanGhostAi.ScatterCorner(GhostId.Blinky).X > 20 && PacmanGhostAi.ScatterCorner(GhostId.Blinky).Y > 30, "Blinky scatter = top-right");
        Assert.True(PacmanGhostAi.ScatterCorner(GhostId.Pinky).X < 5 && PacmanGhostAi.ScatterCorner(GhostId.Pinky).Y > 30, "Pinky scatter = top-left");
        Assert.True(PacmanGhostAi.ScatterCorner(GhostId.Inky).X > 20 && PacmanGhostAi.ScatterCorner(GhostId.Inky).Y < 0, "Inky scatter = bottom-right");
        Assert.True(PacmanGhostAi.ScatterCorner(GhostId.Clyde).X < 5 && PacmanGhostAi.ScatterCorner(GhostId.Clyde).Y < 0, "Clyde scatter = bottom-left");
        // Maze orientation: the top row is the wall row, Pac-Man's start is open.
        var maze = new PacmanMaze();
        Assert.True(maze.IsWall(new PacTile(5, 30)) && maze.IsOpenForActor(PacmanMaze.PacmanStart), "maze y=0 bottom / y=30 top");
    }

    public static void IntersectionRules()
    {
        var maze = new PacmanMaze();
        var ai = new PacmanGhostAi(maze, 1);
        // (6, 25) is an intersection with up/down/left/right open (row 5 from top crosses column 6).
        var at = new PacTile(6, 25);
        Assert.True(maze.IsOpenForActor(at + PacTile.Up) && maze.IsOpenForActor(at + PacTile.Down) && maze.IsOpenForActor(at + PacTile.Left) && maze.IsOpenForActor(at + PacTile.Right), "test tile is a 4-way intersection");
        // Target straight right: must go right, never reverse.
        Expect(ai.ChooseDirection(at, PacTile.Right, new PacTile(27, 25), false), PacTile.Right, "heads to target");
        // Target directly behind while moving right: cannot reverse (left), picks the closest other.
        PacTile choice = ai.ChooseDirection(at, PacTile.Right, new PacTile(0, 25), false);
        Assert.True(choice != PacTile.Left, "a ghost never reverses at an intersection");
        // Tie: target equidistant from up and down neighbours -> Up wins (Up, Left, Down, Right).
        Expect(ai.ChooseDirection(at, PacTile.Right, new PacTile(6, 25 + 0) + new PacTile(20, 0), false), PacTile.Right, "sanity");
        Expect(ai.ChooseDirection(at, PacTile.Left, new PacTile(20, 25), false), PacTile.Up, "tie between up and down resolves Up first");
        // No upward turn in the special zones (moving left at (15, 19) with the target straight up).
        var zone = new PacTile(15, 19);
        Assert.True(PacmanMaze.IsNoUpZone(zone), "(15,19) is a no-up zone");
        if (maze.IsOpenForActor(zone + PacTile.Up))
            Assert.True(ai.ChooseDirection(zone, PacTile.Left, new PacTile(15, 40), false) != PacTile.Up, "no upward turn in a red zone");
        var zone2 = new PacTile(12, 7);
        Assert.True(maze.IsOpenForActor(zone2 + PacTile.Up), "(12,7) has an upward exit");
        Assert.True(ai.ChooseDirection(zone2, PacTile.Right, new PacTile(12, 40), false) != PacTile.Up, "no upward turn above Pac-Man's start");
        // Frightened ghosts ignore the restriction and pick random legal (non-reversing) turns.
        var seen = new HashSet<PacTile>();
        for (int i = 0; i < 200; i++)
            seen.Add(ai.ChooseDirection(at, PacTile.Right, Pac, true));
        Assert.True(!seen.Contains(PacTile.Left) && seen.Count >= 2, "frightened turns are random but never reverse");
    }

    public static void WaveTiming()
    {
        var level1 = GhostWaveTimer.Durations(1);
        Assert.True(level1.SequenceEqual(new[] { 7f, 20f, 7f, 20f, 5f, 20f, 5f, float.PositiveInfinity }), "level 1 waves 7/20/7/20/5/20/5/inf");
        var timer = new GhostWaveTimer(1);
        float t = 0f;
        var changes = new List<(float time, GhostWave wave)>();
        for (int i = 0; i < 100 * 60; i++)
        {
            if (timer.Advance(1f / 60f, frightened: false) > 0)
                changes.Add((t + 1f / 60f, timer.Wave));
            t += 1f / 60f;
        }
        float[] expected = { 7f, 27f, 34f, 54f, 59f, 79f, 84f };
        Assert.True(changes.Count == expected.Length, $"7 wave changes in 100 s (got {changes.Count})");
        for (int i = 0; i < expected.Length; i++)
            Assert.True(MathF.Abs(changes[i].time - expected[i]) < 0.05f && changes[i].wave == (i % 2 == 0 ? GhostWave.Chase : GhostWave.Scatter), $"change {i} at {expected[i]} s (got {changes[i].time:0.00} {changes[i].wave})");
        Assert.True(timer.Wave == GhostWave.Chase, "chase forever after 84 s");

        // Frightened pauses the wave clock.
        var paused = new GhostWaveTimer(1);
        paused.Advance(6f, false);
        paused.Advance(10f, frightened: true);
        Assert.True(paused.Wave == GhostWave.Scatter && MathF.Abs(paused.ElapsedInPhase - 6f) < 1e-4f, "frightened time does not advance the scatter/chase timer");
        paused.Advance(1.01f, false);
        Assert.True(paused.Wave == GhostWave.Chase, "timer resumes after frightened ends");
    }

    public static void ModesInPlay()
    {
        var maze = new PacmanMaze();
        var ai = new PacmanGhostAi(maze, 1);
        PacTile pac = PacmanMaze.PacmanStart;
        // Pinky leaves immediately; Inky waits for 30 dots, Clyde for 60.
        ai.Update(0.05f, pac, PacTile.Left);
        Assert.True(ai.Ghosts[1].State != GhostState.InHouse, "Pinky released immediately");
        for (int i = 0; i < 29; i++) { ai.OnDotEaten(false); ai.Update(0.01f, pac, PacTile.Left); }
        Assert.True(ai.Ghosts[2].State == GhostState.InHouse, "Inky still home after 29 dots");
        ai.OnDotEaten(false);
        Assert.True(ai.Ghosts[2].State == GhostState.LeavingHouse, "Inky leaves at 30 dots");
        for (int i = 0; i < 59; i++) { ai.OnDotEaten(false); ai.Update(0.01f, pac, PacTile.Left); }
        Assert.True(ai.Ghosts[3].State == GhostState.InHouse, "Clyde still home after 59 of his dots");
        ai.OnDotEaten(false);
        Assert.True(ai.Ghosts[3].State == GhostState.LeavingHouse, "Clyde leaves at 60 dots");

        // Reverse on the scatter -> chase change.
        var waves = new PacmanGhostAi(maze, 1);
        PacmanGhost blinky = waves.Blinky;
        for (int i = 0; i < 60 * 6; i++) waves.Update(1f / 60f, pac, PacTile.Left);
        PacTile before = blinky.Direction;
        bool reversed = false;
        for (int i = 0; i < 60 * 2 && !reversed; i++)
        {
            PacTile prev = blinky.Direction;
            waves.Update(1f / 60f, pac, PacTile.Left);
            if (waves.Waves.Wave == GhostWave.Chase && blinky.Direction == -prev && prev != PacTile.Zero)
                reversed = true;
        }
        Assert.True(reversed, $"ghosts reverse when scatter changes to chase (was heading {before})");

        // Energizer: frightened, reversal, slower; eaten -> eyes go home and come back out.
        var fright = new PacmanGhostAi(maze, 1);
        for (int i = 0; i < 60; i++) fright.Update(1f / 60f, pac, PacTile.Left);
        float normal = fright.GhostSpeedFraction(fright.Blinky);
        fright.OnDotEaten(energizer: true);
        Assert.True(fright.Blinky.Frightened && fright.AnyFrightened, "energizer frightens roaming ghosts");
        Assert.True(fright.GhostSpeedFraction(fright.Blinky) < normal, "frightened ghosts are slower");
        int points = fright.EatGhost(fright.Blinky);
        Assert.True(points == 200 && fright.Blinky.State == GhostState.Eaten, "first ghost eaten = 200 and turns into eyes");
        bool home = false;
        for (int i = 0; i < 60 * 20 && !home; i++)
        {
            fright.Update(1f / 60f, pac, PacTile.Left);
            home = fright.Blinky.State == GhostState.Active && !fright.Blinky.Frightened;
        }
        Assert.True(home, "eyes return to the house, revive and come back out");

        // Tunnel slowdown and Cruise Elroy.
        var speed = new PacmanGhostAi(maze, 1);
        var g = speed.Blinky;
        g.Tile = new PacTile(2, 16);
        Assert.True(MathF.Abs(speed.GhostSpeedFraction(g) - 0.40f) < 1e-4f, "tunnel speed 40% on level 1");
        g.Tile = new PacTile(6, 25);
        Assert.True(MathF.Abs(speed.GhostSpeedFraction(g) - 0.75f) < 1e-4f, "normal speed 75% on level 1");
        int total = speed.DotsRemaining;
        for (int i = 0; i < total - 20; i++) speed.OnDotEaten(false);
        Assert.True(speed.ElroyLevel == 1 && speed.GhostSpeedFraction(g) > 0.75f, "Cruise Elroy 1 at 20 dots left");
        for (int i = 0; i < 10; i++) speed.OnDotEaten(false);
        Assert.True(speed.ElroyLevel == 2, "Cruise Elroy 2 at 10 dots left");
    }

    public static void LongPlayInvariants()
    {
        var maze = new PacmanMaze();
        var ai = new PacmanGhostAi(maze, 1, seed: 7);
        var random = new Random(3);
        PacTile pac = PacmanMaze.PacmanStart;
        PacTile dir = PacTile.Left;
        for (int frame = 0; frame < 60 * 120; frame++)
        {
            // Wander Pac-Man around legally.
            if (frame % 8 == 0)
            {
                var options = PacTile.PreferenceOrder.Where(d => maze.IsOpenForActor(maze.Step(pac, d))).ToList();
                if (!maze.IsOpenForActor(maze.Step(pac, dir)) || random.Next(4) == 0)
                    dir = options[random.Next(options.Count)];
                pac = maze.Step(pac, dir);
            }
            if (frame % 50 == 0) ai.OnDotEaten(frame % 900 == 0);
            ai.Update(1f / 60f, pac, dir);
            foreach (PacmanGhost g in ai.Ghosts)
            {
                bool inHouseStates = g.State is GhostState.InHouse or GhostState.LeavingHouse or GhostState.EnteringHouse;
                Assert.True(inHouseStates || maze.IsOpenForActor(g.Tile), $"{g.Id} on an open tile ({g.Tile}, {g.State})");
                Assert.True(!maze.IsWall(g.Tile), $"{g.Id} never inside a wall ({g.Tile})");
            }
        }
        Assert.True(ai.Ghosts.All(g => g.State != GhostState.InHouse), "all ghosts are out after two minutes");
    }

    private static void Expect(PacTile actual, PacTile expected, string what)
        => Assert.True(actual == expected, $"{what}: expected {expected}, got {actual}");
}

namespace ScheduleIArcade.Games.Pacman;

/// <summary>Integer maze tile. y = 0 is the bottom row of the maze, y = Rows - 1 the top.</summary>
internal readonly struct PacTile : IEquatable<PacTile>
{
    public PacTile(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; }
    public int Y { get; }

    public static readonly PacTile Up = new(0, 1);
    public static readonly PacTile Left = new(-1, 0);
    public static readonly PacTile Down = new(0, -1);
    public static readonly PacTile Right = new(1, 0);
    public static readonly PacTile Zero = new(0, 0);

    /// <summary>Arcade tie-break order when two moves are equally close to the target.</summary>
    public static readonly PacTile[] PreferenceOrder = { Up, Left, Down, Right };

    public static PacTile operator +(PacTile a, PacTile b) => new(a.X + b.X, a.Y + b.Y);
    public static PacTile operator -(PacTile a, PacTile b) => new(a.X - b.X, a.Y - b.Y);
    public static PacTile operator -(PacTile a) => new(-a.X, -a.Y);
    public static PacTile operator *(PacTile a, int s) => new(a.X * s, a.Y * s);
    public static bool operator ==(PacTile a, PacTile b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(PacTile a, PacTile b) => !(a == b);

    public int DistanceSquared(PacTile other) => (X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y);
    public bool Equals(PacTile other) => this == other;
    public override bool Equals(object? obj) => obj is PacTile other && this == other;
    public override int GetHashCode() => X * 397 ^ Y;
    public override string ToString() => $"({X},{Y})";
}

internal enum GhostId
{
    Blinky,
    Pinky,
    Inky,
    Clyde
}

/// <summary>Global wave mode shared by every ghost that is out of the house.</summary>
internal enum GhostWave
{
    Scatter,
    Chase
}

internal enum GhostState
{
    InHouse,
    LeavingHouse,
    Active,
    Eaten,
    EnteringHouse
}

/// <summary>
/// The 28 x 31 maze, stored top row first exactly like the cabinet board. '#' wall, '-' ghost-house
/// door (passable only by ghosts leaving or eyes returning), '.' dot, 'o' energizer.
/// </summary>
internal sealed class PacmanMaze
{
    public const int Columns = 28;
    public const int Rows = 31;

    public static readonly string[] DefaultRows =
    {
        "############################",
        "#............##............#",
        "#.####.#####.##.#####.####.#",
        "#o####.#####.##.#####.####o#",
        "#.####.#####.##.#####.####.#",
        "#..........................#",
        "#.####.##.########.##.####.#",
        "#.####.##.########.##.####.#",
        "#......##....##....##......#",
        "######.#####.##.#####.######",
        "     #.#####.##.#####.#     ",
        "     #.##          ##.#     ",
        "     #.##.###--###.##.#     ",
        "######.##.#      #.##.######",
        "      .   #      #   .      ",
        "######.##.#      #.##.######",
        "     #.##.########.##.#     ",
        "     #.##          ##.#     ",
        "     #.##.########.##.#     ",
        "######.##.########.##.######",
        "#............##............#",
        "#.####.#####.##.#####.####.#",
        "#.####.#####.##.#####.####.#",
        "#o..##................##..o#",
        "###.##.##.########.##.##.###",
        "###.##.##.########.##.##.###",
        "#......##....##....##......#",
        "#.##########.##.##########.#",
        "#.##########.##.##########.#",
        "#..........................#",
        "############################"
    };

    /// <summary>Tile directly above the ghost-house door where ghosts exit and eyes aim.</summary>
    public static readonly PacTile HouseExit = new(13, 19);
    /// <summary>Centre tile inside the house where eyes revive.</summary>
    public static readonly PacTile HouseCentre = new(13, 16);
    public static readonly PacTile PacmanStart = new(13, 7);

    /// <summary>
    /// The arcade's four "no upward turn" intersections: two just above the ghost house and two
    /// just above Pac-Man's start (maze rows 11 and 23 counted from the top, columns 12 and 15).
    /// </summary>
    public static readonly PacTile[] NoUpZones = { new(12, 19), new(15, 19), new(12, 7), new(15, 7) };

    private readonly string[] _rows;

    public PacmanMaze(string[]? rows = null)
    {
        _rows = rows ?? DefaultRows;
    }

    /// <summary>Character at (x, y) with y = 0 the bottom row (MazeRows[Rows - 1 - y]).</summary>
    public char Cell(int x, int y)
    {
        if (y < 0 || y >= Rows)
            return '#';
        x = Wrap(x);
        return _rows[Rows - 1 - y][x];
    }

    public static int Wrap(int x) => ((x % Columns) + Columns) % Columns;

    public bool IsWall(PacTile t) => Cell(t.X, t.Y) == '#';
    public bool IsDoor(PacTile t) => Cell(t.X, t.Y) == '-';

    /// <summary>Open for Pac-Man and for roaming ghosts (walls and the house door are closed).</summary>
    public bool IsOpenForActor(PacTile t) => !IsWall(t) && !IsDoor(t) && !IsHouseInterior(t);

    public bool IsHouseInterior(PacTile t) => t.X >= 11 && t.X <= 16 && t.Y >= 15 && t.Y <= 17;

    /// <summary>Side tunnel on the middle row (ghosts slow down here).</summary>
    public bool IsTunnel(PacTile t) => t.Y == 16 && (Wrap(t.X) <= 5 || Wrap(t.X) >= 22);

    public static bool IsNoUpZone(PacTile t)
    {
        foreach (PacTile zone in NoUpZones)
            if (zone == t)
                return true;
        return false;
    }

    public PacTile Step(PacTile from, PacTile dir) => new(Wrap(from.X + dir.X), from.Y + dir.Y);

    public int CountDots()
    {
        int count = 0;
        foreach (string row in _rows)
            foreach (char c in row)
                if (c == '.' || c == 'o')
                    count++;
        return count;
    }
}

/// <summary>
/// Arcade scatter/chase wave timer. Frightened time pauses it; each wave change reverses ghosts.
/// Level 1: scatter 7, chase 20, scatter 7, chase 20, scatter 5, chase 20, scatter 5, chase forever.
/// </summary>
internal sealed class GhostWaveTimer
{
    private readonly float[] _durations;
    private float _elapsedInPhase;

    public GhostWaveTimer(int level)
    {
        _durations = Durations(level);
        Phase = 0;
    }

    public int Phase { get; private set; }
    public GhostWave Wave => Phase % 2 == 0 ? GhostWave.Scatter : GhostWave.Chase;
    public float ElapsedInPhase => _elapsedInPhase;

    public static float[] Durations(int level)
    {
        if (level <= 1)
            return new[] { 7f, 20f, 7f, 20f, 5f, 20f, 5f, float.PositiveInfinity };
        if (level <= 4)
            return new[] { 7f, 20f, 7f, 20f, 5f, 1033f, 1f / 60f, float.PositiveInfinity };
        return new[] { 5f, 20f, 5f, 20f, 5f, 1037f, 1f / 60f, float.PositiveInfinity };
    }

    /// <summary>Advances the wave clock; returns the number of wave changes that happened.</summary>
    public int Advance(float dt, bool frightened)
    {
        if (frightened || dt <= 0f)
            return 0;
        int changes = 0;
        _elapsedInPhase += dt;
        while (Phase < _durations.Length - 1 && _elapsedInPhase >= _durations[Phase])
        {
            _elapsedInPhase -= _durations[Phase];
            Phase++;
            changes++;
        }
        return changes;
    }
}

internal sealed class PacmanGhost
{
    public PacmanGhost(GhostId id, PacTile start, PacTile startDir, GhostState state)
    {
        Id = id;
        Tile = start;
        Previous = start;
        Direction = startDir;
        State = state;
        StartTile = start;
        StartDirection = startDir;
        StartState = state;
    }

    public GhostId Id { get; }
    public PacTile Tile { get; internal set; }
    public PacTile Previous { get; internal set; }
    public PacTile Direction { get; internal set; }
    public GhostState State { get; internal set; }
    public bool Frightened { get; internal set; }
    public int DotCounter { get; internal set; }
    /// <summary>Fraction (0..1) of the way from <see cref="Previous"/> to <see cref="Tile"/>.</summary>
    public float Progress { get; internal set; }
    public PacTile LastTarget { get; internal set; }
    internal bool PendingReverse { get; set; }
    internal PacTile StartTile { get; }
    internal PacTile StartDirection { get; }
    internal GhostState StartState { get; }

    public PacTile ScatterTarget => PacmanGhostAi.ScatterCorner(Id);
}

/// <summary>
/// Arcade-accurate ghost behaviour (after Jamey Pittman's "Pac-Man Dossier"), free of Unity types
/// so it is unit tested headlessly:
/// * a global scatter/chase wave timer per level, paused while ghosts are frightened, with a
///   direction reversal on every wave change and when an energizer is eaten;
/// * intersection targeting: of the non-reversing open moves pick the one whose next tile is
///   closest (Euclidean) to the target, ties broken Up, Left, Down, Right; no upward turns in the
///   four special zones; frightened ghosts pick pseudo-random legal turns;
/// * targets: Blinky = Pac-Man (and "Cruise Elroy" speed-ups by dots left, ignoring scatter);
///   Pinky = 4 tiles ahead (4 up + 4 left when facing up: the original overflow bug);
///   Inky = Blinky->(2 ahead, same bug) doubled; Clyde = Pac-Man when > 8 tiles away, else his
///   scatter corner;
/// * eaten ghosts become eyes, return to the house at high speed and come back out;
/// * house release: Pinky at once, Inky after 30 dots, Clyde after 60 on level 1 (per-ghost dot
///   counters), plus the arcade's idle timer that forces the next ghost out;
/// * speed table with tunnel and frightened slow-downs.
/// </summary>
internal sealed class PacmanGhostAi
{
    /// <summary>100% arcade speed: 75.75757625 px/s on 8 px tiles.</summary>
    public const float FullSpeedTilesPerSecond = 75.75757625f / 8f;
    public const float EyesSpeedFraction = 1.60f;

    private readonly PacmanMaze _maze;
    private readonly Random _random;
    private readonly int _totalDots;
    private float _frightenedLeft;
    private float _idleTimer;
    private bool _globalCounterActive;
    private int _globalDotCounter;

    public PacmanGhostAi(PacmanMaze maze, int level, int seed = 1)
    {
        _maze = maze;
        Level = Math.Max(1, level);
        _random = new Random(seed);
        _totalDots = maze.CountDots();
        Waves = new GhostWaveTimer(Level);
        Ghosts = new[]
        {
            new PacmanGhost(GhostId.Blinky, PacmanMaze.HouseExit, PacTile.Left, GhostState.Active),
            new PacmanGhost(GhostId.Pinky, PacmanMaze.HouseCentre, PacTile.Down, GhostState.InHouse),
            new PacmanGhost(GhostId.Inky, new PacTile(11, 16), PacTile.Up, GhostState.InHouse),
            new PacmanGhost(GhostId.Clyde, new PacTile(15, 16), PacTile.Up, GhostState.InHouse),
        };
        DotsRemaining = _totalDots;
    }

    public int Level { get; }
    public PacmanGhost[] Ghosts { get; }
    public GhostWaveTimer Waves { get; }
    public int DotsRemaining { get; private set; }
    public bool AnyFrightened => _frightenedLeft > 0f;
    public float FrightenedTimeLeft => _frightenedLeft;
    public int GhostsEatenThisEnergizer { get; private set; }

    public PacmanGhost Blinky => Ghosts[0];

    /// <summary>Scatter corners (outside the maze, like the arcade). y = 30 is the top row.</summary>
    public static PacTile ScatterCorner(GhostId id) => id switch
    {
        GhostId.Blinky => new PacTile(25, 33),
        GhostId.Pinky => new PacTile(2, 33),
        GhostId.Inky => new PacTile(27, -2),
        _ => new PacTile(0, -2),
    };

    public static float FrightenedSeconds(int level) => level switch
    {
        1 => 6f, 2 => 5f, 3 => 4f, 4 => 3f, 5 => 2f, 6 => 5f, 7 => 2f, 8 => 2f, 9 => 1f,
        10 => 5f, 11 => 2f, 12 => 1f, 13 => 1f, 14 => 3f, 15 => 1f, 16 => 1f, 18 => 1f, _ => 0f
    };

    /// <summary>Cruise Elroy dot thresholds (dots left) for Blinky's two speed-ups.</summary>
    public static (int elroy1, int elroy2) ElroyDots(int level) => level switch
    {
        1 => (20, 10), 2 => (30, 15), 3 or 4 or 5 => (40, 20), 6 or 7 or 8 => (50, 25),
        9 or 10 or 11 => (60, 30), 12 or 13 or 14 => (80, 40), 15 or 16 or 17 or 18 => (100, 50), _ => (120, 60)
    };

    public int ElroyLevel
    {
        get
        {
            var (e1, e2) = ElroyDots(Level);
            if (DotsRemaining <= e2) return 2;
            if (DotsRemaining <= e1) return 1;
            return 0;
        }
    }

    /// <summary>Per-ghost dot limits before leaving the house (level 1: 0 / 30 / 60).</summary>
    public static int DotLimit(GhostId id, int level)
    {
        if (id == GhostId.Blinky || id == GhostId.Pinky) return 0;
        if (level <= 1) return id == GhostId.Inky ? 30 : 60;
        if (level == 2) return id == GhostId.Inky ? 0 : 50;
        return 0;
    }

    public static float PacmanSpeedFraction(int level, bool frightened)
    {
        if (level <= 1) return frightened ? 0.90f : 0.80f;
        if (level <= 4) return frightened ? 0.95f : 0.90f;
        if (level <= 20) return 1.00f;
        return 0.90f;
    }

    public float GhostSpeedFraction(PacmanGhost ghost)
    {
        if (ghost.State == GhostState.Eaten || ghost.State == GhostState.EnteringHouse)
            return EyesSpeedFraction;
        float normal = Level <= 1 ? 0.75f : Level <= 4 ? 0.85f : 0.95f;
        float tunnel = Level <= 1 ? 0.40f : Level <= 4 ? 0.45f : 0.50f;
        float fright = Level <= 1 ? 0.50f : Level <= 4 ? 0.55f : 0.60f;
        if (ghost.State == GhostState.InHouse || ghost.State == GhostState.LeavingHouse)
            return tunnel;
        if (_maze.IsTunnel(ghost.Tile))
            return tunnel;
        if (ghost.Frightened)
            return fright;
        if (ghost.Id == GhostId.Blinky && ElroyLevel > 0)
            return normal + (ElroyLevel == 2 ? 0.10f : 0.05f);
        return normal;
    }

    /// <summary>Tile <paramref name="n"/> ahead of Pac-Man, reproducing the arcade's facing-up overflow.</summary>
    public static PacTile AheadWithOverflow(PacTile pac, PacTile pacDir, int n)
    {
        PacTile ahead = pac + pacDir * n;
        if (pacDir == PacTile.Up)
            ahead += PacTile.Left * n;
        return ahead;
    }

    /// <summary>Chase-mode target tile for one ghost.</summary>
    public static PacTile ChaseTarget(GhostId id, PacTile pac, PacTile pacDir, PacTile blinky, PacTile self)
    {
        switch (id)
        {
            case GhostId.Blinky:
                return pac;
            case GhostId.Pinky:
                return AheadWithOverflow(pac, pacDir, 4);
            case GhostId.Inky:
            {
                PacTile pivot = AheadWithOverflow(pac, pacDir, 2);
                return pivot + (pivot - blinky);
            }
            default:
                return self.DistanceSquared(pac) > 64 ? pac : ScatterCorner(GhostId.Clyde);
        }
    }

    /// <summary>The target the ghost steers to right now (scatter corner, chase target, or home).</summary>
    public PacTile CurrentTarget(PacmanGhost ghost, PacTile pac, PacTile pacDir)
    {
        if (ghost.State == GhostState.Eaten)
            return PacmanMaze.HouseExit;
        bool elroyChases = ghost.Id == GhostId.Blinky && ElroyLevel > 0 && Ghosts[3].State != GhostState.InHouse;
        if (Waves.Wave == GhostWave.Scatter && !elroyChases)
            return ghost.ScatterTarget;
        return ChaseTarget(ghost.Id, pac, pacDir, Blinky.Tile, ghost.Tile);
    }

    /// <summary>
    /// Arcade intersection decision from <paramref name="at"/> while travelling <paramref name="heading"/>:
    /// never reverse, never enter walls/door, no upward turn in the special zones (scatter/chase only),
    /// minimise Euclidean distance to <paramref name="target"/>, ties Up/Left/Down/Right.
    /// </summary>
    public PacTile ChooseDirection(PacTile at, PacTile heading, PacTile target, bool frightened)
    {
        var legal = new List<PacTile>(4);
        foreach (PacTile dir in PacTile.PreferenceOrder)
        {
            if (dir == -heading && heading != PacTile.Zero)
                continue;
            if (!frightened && dir == PacTile.Up && PacmanMaze.IsNoUpZone(at))
                continue;
            if (!_maze.IsOpenForActor(_maze.Step(at, dir)))
                continue;
            legal.Add(dir);
        }

        if (legal.Count == 0)
            return heading == PacTile.Zero ? PacTile.Left : -heading; // dead end: only option is back

        if (frightened)
            return legal[_random.Next(legal.Count)];

        PacTile best = legal[0];
        int bestDistance = int.MaxValue;
        foreach (PacTile dir in legal) // already in Up, Left, Down, Right order: strict < keeps the earlier one on ties
        {
            int d = _maze.Step(at, dir).DistanceSquared(target);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = dir;
            }
        }
        return best;
    }

    /// <summary>Call when Pac-Man eats a dot or an energizer.</summary>
    public void OnDotEaten(bool energizer)
    {
        DotsRemaining = Math.Max(0, DotsRemaining - 1);
        _idleTimer = 0f;

        if (_globalCounterActive)
        {
            _globalDotCounter++;
            // After a life is lost: Pinky leaves at 7, Inky at 17, Clyde at 32 global dots.
            foreach (PacmanGhost ghost in Ghosts)
            {
                if (ghost.State != GhostState.InHouse) continue;
                int limit = ghost.Id == GhostId.Pinky ? 7 : ghost.Id == GhostId.Inky ? 17 : 32;
                if (_globalDotCounter >= limit)
                {
                    ghost.State = GhostState.LeavingHouse;
                    if (ghost.Id == GhostId.Clyde)
                        _globalCounterActive = false;
                }
                break; // only the first ghost still in the house is considered
            }
        }
        else
        {
            PacmanGhost? next = NextInHouse();
            if (next != null)
                next.DotCounter++;
        }

        if (energizer)
            StartFrightened();
        ReleaseByCounters();
    }

    /// <summary>Energizer: every roaming ghost turns blue and reverses; the wave timer pauses.</summary>
    public void StartFrightened()
    {
        float seconds = FrightenedSeconds(Level);
        GhostsEatenThisEnergizer = 0;
        foreach (PacmanGhost ghost in Ghosts)
        {
            if (ghost.State == GhostState.Eaten || ghost.State == GhostState.EnteringHouse)
                continue;
            if (ghost.State == GhostState.Active)
                ghost.PendingReverse = true;
            ghost.Frightened = seconds > 0f;
        }
        _frightenedLeft = seconds;
    }

    /// <summary>Pac-Man caught a frightened ghost. Returns the points (200, 400, 800, 1600).</summary>
    public int EatGhost(PacmanGhost ghost)
    {
        if (!ghost.Frightened || ghost.State != GhostState.Active)
            return 0;
        ghost.Frightened = false;
        ghost.State = GhostState.Eaten;
        GhostsEatenThisEnergizer++;
        return 200 << Math.Min(3, GhostsEatenThisEnergizer - 1);
    }

    /// <summary>After Pac-Man dies: ghosts back to start, global dot counter takes over.</summary>
    public void ResetAfterLifeLost()
    {
        foreach (PacmanGhost g in Ghosts)
            ResetGhost(g);
        _frightenedLeft = 0f;
        _idleTimer = 0f;
        _globalCounterActive = true;
        _globalDotCounter = 0;
    }

    private void ResetGhost(PacmanGhost g)
    {
        g.Tile = g.StartTile;
        g.Previous = g.StartTile;
        g.Direction = g.StartDirection;
        g.State = g.StartState;
        g.Frightened = false;
        g.Progress = 0f;
        g.PendingReverse = false;
    }

    private PacmanGhost? NextInHouse()
    {
        // Preferred order: Pinky, Inky, Clyde.
        for (int i = 1; i < Ghosts.Length; i++)
            if (Ghosts[i].State == GhostState.InHouse)
                return Ghosts[i];
        return null;
    }

    private void ReleaseByCounters()
    {
        if (_globalCounterActive)
            return;
        PacmanGhost? next = NextInHouse();
        if (next != null && next.DotCounter >= DotLimit(next.Id, Level))
            next.State = GhostState.LeavingHouse;
    }

    /// <summary>
    /// Advances everything by <paramref name="dt"/> seconds. Returns true if a wave change happened.
    /// </summary>
    public bool Update(float dt, PacTile pac, PacTile pacDir)
    {
        if (dt <= 0f)
            return false;

        // Frightened countdown pauses the wave timer.
        bool frightened = _frightenedLeft > 0f;
        if (frightened)
        {
            _frightenedLeft = Math.Max(0f, _frightenedLeft - dt);
            if (_frightenedLeft <= 0f)
                foreach (PacmanGhost g in Ghosts)
                    g.Frightened = false;
        }

        int changes = Waves.Advance(dt, frightened);
        if (changes % 2 == 1)
            foreach (PacmanGhost g in Ghosts)
                if (g.State == GhostState.Active)
                    g.PendingReverse = true;

        // Idle timer: if Pac-Man stops eating, the next ghost is forced out (4 s, 3 s from level 5).
        _idleTimer += dt;
        float idleLimit = Level < 5 ? 4f : 3f;
        if (_idleTimer >= idleLimit)
        {
            _idleTimer = 0f;
            PacmanGhost? next = NextInHouse();
            if (next != null)
                next.State = GhostState.LeavingHouse;
        }
        ReleaseByCounters();

        foreach (PacmanGhost ghost in Ghosts)
        {
            float tilesPerSecond = GhostSpeedFraction(ghost) * FullSpeedTilesPerSecond;
            ghost.Progress += tilesPerSecond * dt;
            int guard = 0;
            while (ghost.Progress >= 1f && guard++ < 8)
            {
                ghost.Progress -= 1f;
                StepGhost(ghost, pac, pacDir);
            }
        }
        return changes > 0;
    }

    /// <summary>Moves one ghost by exactly one tile.</summary>
    internal void StepGhost(PacmanGhost ghost, PacTile pac, PacTile pacDir)
    {
        ghost.Previous = ghost.Tile;
        switch (ghost.State)
        {
            case GhostState.InHouse:
            {
                // Bob up and down inside the house.
                PacTile next = ghost.Tile + ghost.Direction;
                if (!_maze.IsHouseInterior(next) || next.Y < 15 || next.Y > 17)
                {
                    ghost.Direction = -ghost.Direction;
                    if (ghost.Direction == PacTile.Zero) ghost.Direction = PacTile.Up;
                    next = ghost.Tile + ghost.Direction;
                }
                if (_maze.IsHouseInterior(next))
                    ghost.Tile = next;
                return;
            }
            case GhostState.LeavingHouse:
            {
                // Centre column, then straight up through the door to the exit tile.
                if (ghost.Tile.X != PacmanMaze.HouseExit.X && ghost.Tile.Y < 18)
                    ghost.Direction = ghost.Tile.X < PacmanMaze.HouseExit.X ? PacTile.Right : PacTile.Left;
                else
                    ghost.Direction = PacTile.Up;
                ghost.Tile += ghost.Direction;
                if (ghost.Tile == PacmanMaze.HouseExit)
                {
                    ghost.State = GhostState.Active;
                    ghost.Direction = PacTile.Left;
                    ghost.PendingReverse = false;
                }
                return;
            }
            case GhostState.EnteringHouse:
            {
                ghost.Direction = PacTile.Down;
                ghost.Tile += PacTile.Down;
                if (ghost.Tile.Y <= PacmanMaze.HouseCentre.Y)
                {
                    ghost.Tile = PacmanMaze.HouseCentre;
                    ghost.State = GhostState.LeavingHouse; // revived: straight back out
                }
                return;
            }
        }

        // Active or Eaten (eyes) roam the maze.
        if (ghost.PendingReverse && ghost.State == GhostState.Active)
        {
            ghost.PendingReverse = false;
            PacTile back = _maze.Step(ghost.Tile, -ghost.Direction);
            if (_maze.IsOpenForActor(back))
            {
                ghost.Direction = -ghost.Direction;
                ghost.Tile = back;
                return;
            }
        }

        if (ghost.State == GhostState.Eaten && ghost.Tile == PacmanMaze.HouseExit)
        {
            ghost.State = GhostState.EnteringHouse;
            ghost.Direction = PacTile.Down;
            ghost.Tile += PacTile.Down; // through the door
            return;
        }

        PacTile target = CurrentTarget(ghost, pac, pacDir);
        ghost.LastTarget = target;
        PacTile dir = ChooseDirection(ghost.Tile, ghost.Direction, target, ghost.Frightened && ghost.State == GhostState.Active);
        ghost.Direction = dir;
        ghost.Tile = _maze.Step(ghost.Tile, dir);
    }
}

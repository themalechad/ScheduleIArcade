using UnityEngine;
using MelonLoader;
using ScheduleIArcade.Core;

namespace ScheduleIArcade.Games;

/// <summary>
/// Self-contained cocktail-table Pac-Man game. Its grid movement, pellet scoring, power-pellet
/// frightened ghosts, lives, level reset and arrow-key controls follow the supplied PacMan v1.1.1
/// game's gameplay structure, while rendering to Schedule I's in-world table texture instead of
/// attempting to load that separate Mono Unity executable into an IL2CPP game.
/// </summary>
internal sealed class PacmanCocktailGame : IArcadeGame
{
    private const int Width = 224;
    private const int Height = 248;
    private const int Columns = 28;
    private const int Rows = 31;
    private const int Cell = 7;
    private const int OriginX = (Width - Columns * Cell) / 2;
    private const int OriginY = 15;

    private static readonly Color32 Black = new(2, 2, 8, 255);
    private static readonly Color32 MazeBlue = new(20, 72, 245, 255);
    private static readonly Color32 MazeInner = new(5, 18, 88, 255);
    private static readonly Color32 White = new(245, 245, 236, 255);
    private static readonly Color32 Yellow = new(255, 228, 36, 255);
    private static readonly Color32 Pink = new(255, 105, 195, 255);
    private static readonly Color32 Cyan = new(24, 235, 247, 255);
    private static readonly Color32 Red = new(255, 58, 72, 255);
    private static readonly Color32 Orange = new(255, 170, 48, 255);
    private static readonly Color32 Frightened = new(55, 72, 255, 255);

    private sealed class Ghost
    {
        public Ghost(Vector2Int spawn, Color32 color)
        {
            Spawn = spawn;
            Position = spawn;
            PreviousPosition = spawn;
            Color = color;
        }

        public Vector2Int Spawn { get; }
        public Vector2Int Position { get; set; }
        public Vector2Int PreviousPosition { get; set; }
        public Vector2Int Direction { get; set; }
        public Color32 Color { get; }
    }

    private enum GameState
    {
        Ready,
        Playing,
        Paused,
        GameOver,
        LevelClear
    }

    private readonly bool[,] _walls = new bool[Columns, Rows];
    private readonly HashSet<Vector2Int> _pellets = new();
    private readonly HashSet<Vector2Int> _powerPellets = new();
    private readonly List<Ghost> _ghosts = new();
    private readonly System.Random _random = new();
    private readonly List<Texture2D> _suppliedArt = new();

    private Texture2D? _texture;
    private Texture2D? _sourcePellet;
    private Texture2D? _sourcePowerPellet;
    private readonly Texture2D?[] _sourcePacmanSprites = new Texture2D?[3];
    private readonly Texture2D?[] _sourceGhostSprites = new Texture2D?[4];
    private Color32[]? _pixels;
    private Vector2Int _player;
    private Vector2Int _playerPrevious;
    private Vector2Int _playerDirection;
    private Vector2Int _requestedDirection;
    private int _score;
    private int _lives;
    private int _level;
    private float _stateTimer;
    private float _playerTimer;
    private float _ghostTimer;
    private float _frightenedTimer;
    private float _nextDraw;
    private GameState _state;

    // A complete 28x31 Pac-Man maze, stored top-to-bottom just like the supplied game's board.
    // # is a wall, . a pellet, o an energizer, and blank cells are the ghost pen/tunnel lanes.
    private static readonly string[] MazeRows =
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

    public string Id => "pacman";
    public string DisplayName => "Pac-Man Cocktail Table";
    public Texture? ScreenTexture => _texture;

    public void Initialize()
    {
        if (_texture != null)
            return;

        _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_PacmanCocktail",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _pixels = new Color32[Width * Height];
        LoadSuppliedPacmanVisuals();
        BuildMaze();
        DrawFrame();
    }

    public void Start()
    {
        _score = 0;
        _lives = 3;
        _level = 1;
        StartLevel();
    }

    public void Tick(float deltaTime)
    {
        CaptureInput();
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            _state = _state == GameState.Paused ? GameState.Playing : GameState.Paused;
            _nextDraw = 0f;
        }

        switch (_state)
        {
            case GameState.Ready:
                _stateTimer -= deltaTime;
                if (_stateTimer <= 0f)
                    _state = GameState.Playing;
                break;
            case GameState.Playing:
                TickPlaying(deltaTime);
                break;
            case GameState.LevelClear:
                _stateTimer -= deltaTime;
                if (_stateTimer <= 0f)
                {
                    _level++;
                    StartLevel();
                }
                break;
            case GameState.GameOver:
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space))
                {
                    _score = 0;
                    _lives = 3;
                    _level = 1;
                    StartLevel();
                }
                break;
        }

        _nextDraw -= deltaTime;
        if (_nextDraw <= 0f)
        {
            _nextDraw = 1f / 60f;
            DrawFrame();
        }
    }

    private void BuildMaze()
    {
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                _walls[x, y] = MazeCell(x, y) == '#';
            }
        }
    }

    private void StartLevel()
    {
        _pellets.Clear();
        _powerPellets.Clear();
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                var cell = new Vector2Int(x, y);
                char mazeCell = MazeCell(x, y);
                if (mazeCell == '.')
                    _pellets.Add(cell);
                else if (mazeCell == 'o')
                    _powerPellets.Add(cell);
            }
        }

        _ghosts.Clear();
        _ghosts.Add(new Ghost(new Vector2Int(12, 15), Red));
        _ghosts.Add(new Ghost(new Vector2Int(13, 15), Pink));
        _ghosts.Add(new Ghost(new Vector2Int(14, 15), Cyan));
        _ghosts.Add(new Ghost(new Vector2Int(15, 15), Orange));
        ResetPositions();
        _state = GameState.Ready;
        _stateTimer = 1.2f;
        _nextDraw = 0f;
    }

    private void ResetPositions()
    {
        _player = new Vector2Int(13, 7);
        _playerPrevious = _player;
        _playerDirection = Vector2Int.left;
        _requestedDirection = _playerDirection;
        _playerTimer = 0f;
        _ghostTimer = 0f;
        _frightenedTimer = 0f;
        for (int i = 0; i < _ghosts.Count; i++)
        {
            _ghosts[i].Position = _ghosts[i].Spawn;
            _ghosts[i].PreviousPosition = _ghosts[i].Spawn;
            _ghosts[i].Direction = i % 2 == 0 ? Vector2Int.left : Vector2Int.right;
        }
    }

    private void CaptureInput()
    {
        if (Input.GetKeyDown(KeyCode.LeftArrow)) _requestedDirection = Vector2Int.left;
        else if (Input.GetKeyDown(KeyCode.RightArrow)) _requestedDirection = Vector2Int.right;
        else if (Input.GetKeyDown(KeyCode.UpArrow)) _requestedDirection = Vector2Int.up;
        else if (Input.GetKeyDown(KeyCode.DownArrow)) _requestedDirection = Vector2Int.down;
    }

    private void TickPlaying(float deltaTime)
    {
        _frightenedTimer = Mathf.Max(0f, _frightenedTimer - deltaTime);
        _playerTimer += deltaTime;
        float playerStep = Mathf.Max(0.070f, 0.115f - (_level - 1) * 0.004f);
        while (_playerTimer >= playerStep)
        {
            _playerTimer -= playerStep;
            StepPlayer();
            ConsumeCurrentCell();
            if (_state != GameState.Playing)
                return;
        }

        _ghostTimer += deltaTime;
        float ghostStep = Mathf.Max(0.082f, 0.145f - (_level - 1) * 0.003f);
        while (_ghostTimer >= ghostStep)
        {
            _ghostTimer -= ghostStep;
            for (int i = 0; i < _ghosts.Count; i++)
                StepGhost(_ghosts[i]);
            CheckGhostCollision();
            if (_state != GameState.Playing)
                return;
        }
    }

    private void StepPlayer()
    {
        _playerPrevious = _player;
        if (TryGetOpenStep(_player, _requestedDirection, out Vector2Int requestedStep))
            _playerDirection = _requestedDirection;
        if (TryGetOpenStep(_player, _playerDirection, out Vector2Int forwardStep))
            _player = forwardStep;
    }

    private void ConsumeCurrentCell()
    {
        if (_pellets.Remove(_player))
            _score += 10;
        if (_powerPellets.Remove(_player))
        {
            _score += 50;
            _frightenedTimer = 6.0f;
        }
        CheckGhostCollision();
        if (_pellets.Count == 0 && _powerPellets.Count == 0)
        {
            _state = GameState.LevelClear;
            _stateTimer = 1.8f;
        }
    }

    private void StepGhost(Ghost ghost)
    {
        var choices = new List<Vector2Int>(4);
        Vector2Int reverse = -ghost.Direction;
        foreach (Vector2Int direction in Directions)
        {
            if (TryGetOpenStep(ghost.Position, direction, out _) && (direction != reverse || choices.Count == 0))
                choices.Add(direction);
        }
        if (choices.Count == 0)
            return;

        Vector2Int directionChoice = choices[0];
        if (_frightenedTimer > 0f)
        {
            directionChoice = choices[_random.Next(choices.Count)];
        }
        else
        {
            int bestDistance = int.MaxValue;
            for (int i = 0; i < choices.Count; i++)
            {
                TryGetOpenStep(ghost.Position, choices[i], out Vector2Int probe);
                int distance = Mathf.Abs(probe.x - _player.x) + Mathf.Abs(probe.y - _player.y);
                if (distance < bestDistance || (distance == bestDistance && _random.NextDouble() < 0.18))
                {
                    bestDistance = distance;
                    directionChoice = choices[i];
                }
            }
        }
        ghost.Direction = directionChoice;
        if (TryGetOpenStep(ghost.Position, directionChoice, out Vector2Int nextPosition))
        {
            ghost.PreviousPosition = ghost.Position;
            ghost.Position = nextPosition;
        }
    }

    private void CheckGhostCollision()
    {
        for (int i = 0; i < _ghosts.Count; i++)
        {
            Ghost ghost = _ghosts[i];
            if (ghost.Position != _player)
                continue;

            if (_frightenedTimer > 0f)
            {
                _score += 200;
                ghost.Position = ghost.Spawn;
                ghost.PreviousPosition = ghost.Spawn;
                ghost.Direction = Vector2Int.left;
                continue;
            }

            _lives--;
            if (_lives <= 0)
            {
                _state = GameState.GameOver;
                _nextDraw = 0f;
            }
            else
            {
                ResetPositions();
                _state = GameState.Ready;
                _stateTimer = 1.0f;
            }
            return;
        }
    }

    private bool IsOpen(Vector2Int position)
        => position.x >= 0 && position.x < Columns && position.y >= 0 && position.y < Rows && !_walls[position.x, position.y];

    private static char MazeCell(int x, int y) => MazeRows[Rows - 1 - y][x];

    private bool TryGetOpenStep(Vector2Int position, Vector2Int direction, out Vector2Int destination)
    {
        destination = position + direction;
        // The middle row is a proper wraparound tunnel rather than a dead edge. Other board
        // edges are walled by the maze data, so this never lets a player leave the board.
        if (destination.x < 0)
            destination.x = Columns - 1;
        else if (destination.x >= Columns)
            destination.x = 0;

        return IsOpen(destination);
    }

    private static readonly Vector2Int[] Directions = { Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down };

    private void LoadSuppliedPacmanVisuals()
    {
        // These are extracted directly from the user's PacMan v1.1.1 resources/sharedassets
        // files. Its managed Mono DLL cannot run in Schedule I's IL2CPP process, but its PNG
        // art is fully compatible and is deliberately used by the in-table game renderer.
        _sourcePacmanSprites[0] = LoadSuppliedArt("pacman_chomp_1.png");
        _sourcePacmanSprites[1] = LoadSuppliedArt("pacman_chomp_2.png");
        _sourcePacmanSprites[2] = LoadSuppliedArt("pacman_chomp_3.png");
        _sourceGhostSprites[0] = LoadSuppliedArt("blinky_right_1.png");
        _sourceGhostSprites[1] = LoadSuppliedArt("pinky_right_1.png");
        _sourceGhostSprites[2] = LoadSuppliedArt("inky_right_1.png");
        _sourceGhostSprites[3] = LoadSuppliedArt("clyde_right_1.png");
        _sourcePellet = LoadSuppliedArt("pelletFixed.png");
        _sourcePowerPellet = LoadSuppliedArt("pellet_energizerFixed.png");

        if (_sourcePacmanSprites[0] != null && _sourceGhostSprites[0] != null)
            MelonLogger.Msg("Pac-Man cocktail table loaded sprites and pellet art from the supplied PacMan v1.1.1 files.");
        else
            MelonLogger.Warning("Pac-Man source visual assets were not installed; using the compatible built-in pixel fallback.");
    }

    private Texture2D? LoadSuppliedArt(string fileName)
    {
        string path = Path.Combine(ModPaths.PacManAssetsRoot, fileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var art = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "ScheduleIArcade_SourcePacMan_" + Path.GetFileNameWithoutExtension(fileName),
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!ImageConversion.LoadImage(art, File.ReadAllBytes(path), false))
            {
                UnityEngine.Object.Destroy(art);
                return null;
            }
            _suppliedArt.Add(art);
            return art;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not load supplied Pac-Man art '{fileName}': {ex.Message}");
            return null;
        }
    }

    private void DrawFrame()
    {
        if (_texture == null || _pixels == null)
            return;

        Clear(Black);
        DrawText("1UP " + _score.ToString("00000"), 10, 238, 1, White);
        DrawText("LIVES " + _lives, 148, 238, 1, White);
        DrawMaze();
        if (_state != GameState.GameOver)
        {
            DrawPacMan();
            DrawGhosts();
        }

        if (_state == GameState.Ready)
            DrawOverlay("READY!", Yellow);
        else if (_state == GameState.Paused)
            DrawOverlay("PAUSED", White);
        else if (_state == GameState.GameOver)
            DrawOverlay("GAME OVER", Red);
        else if (_state == GameState.LevelClear)
            DrawOverlay("LEVEL " + _level + " CLEAR", Cyan);

        DrawTextCentered(_state == GameState.GameOver ? "ENTER RETRY" : "ARROWS MOVE  TAB PAUSE", 5, 1, White);
        _texture.SetPixels32(_pixels);
        _texture.Apply(false, false);
    }

    private void DrawMaze()
    {
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                int px = OriginX + x * Cell;
                int py = OriginY + y * Cell;
                if (_walls[x, y])
                {
                    FillRect(px, py, Cell, Cell, MazeBlue);
                    continue;
                }

                Vector2Int point = new(x, y);
                if (_powerPellets.Contains(point))
                {
                    int pulse = (int)(Time.time * 5f) & 1;
                    if (_sourcePowerPellet != null)
                        DrawSourceImage(_sourcePowerPellet, px + 2 - pulse, py + 2 - pulse, 5 + pulse * 2, 5 + pulse * 2);
                    else
                        FillRect(px + 2 - pulse, py + 2 - pulse, 5 + pulse * 2, 5 + pulse * 2, White);
                }
                else if (_pellets.Contains(point))
                {
                    if (_sourcePellet != null)
                        DrawSourceImage(_sourcePellet, px + 3, py + 3, 3, 3);
                    // Always stamp a high-contrast two-pixel centre after source art. Some
                    // source-pack pellets have nearly opaque dark edge pixels at this scale,
                    // which made the pellet path disappear on the in-world table display.
                    FillRect(px + 3, py + 3, 2, 2, new Color32(255, 228, 184, 255));
                }
            }
        }
    }

    private void DrawPacMan()
    {
        float playerStep = Mathf.Max(0.070f, 0.115f - (_level - 1) * 0.004f);
        float stepProgress = Mathf.Clamp01(_playerTimer / playerStep);
        float easedProgress = stepProgress * stepProgress * (3f - 2f * stepProgress);
        float renderProgress = Mathf.Abs(_player.x - _playerPrevious.x) > 1 || Mathf.Abs(_player.y - _playerPrevious.y) > 1
            ? 1f
            : easedProgress;
        float cellX = Mathf.Lerp(_playerPrevious.x, _player.x, renderProgress);
        float cellY = Mathf.Lerp(_playerPrevious.y, _player.y, renderProgress);
        int cx = OriginX + Mathf.RoundToInt(cellX * Cell) + Cell / 2;
        int cy = OriginY + Mathf.RoundToInt(cellY * Cell) + Cell / 2;
        Texture2D? sprite = _sourcePacmanSprites[(int)(Time.time * 10f) % _sourcePacmanSprites.Length];
        if (sprite != null)
        {
            DrawSourceImage(sprite, cx - 4, cy - 4, 9, 9, _playerDirection == Vector2Int.left);
            return;
        }

        bool openMouth = ((int)(Time.time * 10f) & 1) == 0;
        for (int y = -4; y <= 4; y++)
        {
            for (int x = -4; x <= 4; x++)
            {
                if (x * x + y * y > 18)
                    continue;
                bool mouth = openMouth && ((_playerDirection == Vector2Int.right && x > 0 && Mathf.Abs(y) <= x / 2) ||
                    (_playerDirection == Vector2Int.left && x < 0 && Mathf.Abs(y) <= -x / 2) ||
                    (_playerDirection == Vector2Int.up && y > 0 && Mathf.Abs(x) <= y / 2) ||
                    (_playerDirection == Vector2Int.down && y < 0 && Mathf.Abs(x) <= -y / 2));
                if (!mouth)
                    SetPixel(cx + x, cy + y, Yellow);
            }
        }
    }

    private void DrawGhosts()
    {
        float ghostStep = Mathf.Max(0.082f, 0.145f - (_level - 1) * 0.003f);
        float stepProgress = Mathf.Clamp01(_ghostTimer / ghostStep);
        // Smooth the visual motion continuously between grid cells, while gameplay collision and
        // pathing remain grid-exact. This keeps the supplied pixel ghosts crisp but removes the
        // stop/start stepping that was visible at the old movement cadence.
        float easedProgress = stepProgress * stepProgress * (3f - 2f * stepProgress);
        for (int i = 0; i < _ghosts.Count; i++)
        {
            Ghost ghost = _ghosts[i];
            Vector2Int from = ghost.PreviousPosition;
            Vector2Int to = ghost.Position;
            // A tunnel wrap should never animate a ghost straight across the full maze.
            float renderProgress = Mathf.Abs(to.x - from.x) > 1 || Mathf.Abs(to.y - from.y) > 1
                ? 1f
                : easedProgress;
            float cellX = Mathf.Lerp(from.x, to.x, renderProgress);
            float cellY = Mathf.Lerp(from.y, to.y, renderProgress);
            int x = OriginX + Mathf.RoundToInt(cellX * Cell) + 1;
            int y = OriginY + Mathf.RoundToInt(cellY * Cell) + 1;
            Color32 color = _frightenedTimer > 0f ? Frightened : ghost.Color;
            Texture2D? sprite = _sourceGhostSprites[i % _sourceGhostSprites.Length];
            if (sprite != null && _frightenedTimer <= 0f)
            {
                DrawSourceImage(sprite, x, y, 8, 8);
                continue;
            }
            FillRect(x, y, 7, 6, color);
            FillRect(x + 1, y + 6, 2, 1, color);
            FillRect(x + 4, y + 6, 2, 1, color);
            FillRect(x + 1, y + 3, 2, 2, White);
            FillRect(x + 4, y + 3, 2, 2, White);
            SetPixel(x + 2, y + 3, MazeInner);
            SetPixel(x + 5, y + 3, MazeInner);
        }
    }

    private void DrawOverlay(string text, Color32 color)
    {
        int width = TextWidth(text, 2) + 18;
        int x = (Width - width) / 2;
        FillRect(x, 116, width, 24, new Color32(0, 0, 0, 230));
        DrawRect(x, 116, width, 24, color, 1);
        DrawTextCentered(text, 124, 2, color);
    }

    private void Clear(Color32 color)
    {
        if (_pixels != null)
            Array.Fill(_pixels, color);
    }

    private void FillRect(int x, int y, int width, int height, Color32 color)
    {
        if (_pixels == null)
            return;
        int minX = Mathf.Clamp(x, 0, Width);
        int maxX = Mathf.Clamp(x + width, 0, Width);
        int minY = Mathf.Clamp(y, 0, Height);
        int maxY = Mathf.Clamp(y + height, 0, Height);
        for (int py = minY; py < maxY; py++)
        {
            int row = py * Width;
            for (int px = minX; px < maxX; px++)
                _pixels[row + px] = color;
        }
    }

    private void DrawRect(int x, int y, int width, int height, Color32 color, int thickness)
    {
        FillRect(x, y, width, thickness, color);
        FillRect(x, y + height - thickness, width, thickness, color);
        FillRect(x, y, thickness, height, color);
        FillRect(x + width - thickness, y, thickness, height, color);
    }

    private void SetPixel(int x, int y, Color32 color)
    {
        if (_pixels != null && x >= 0 && x < Width && y >= 0 && y < Height)
            _pixels[y * Width + x] = color;
    }

    private void DrawSourceImage(Texture2D source, int x, int y, int drawWidth, int drawHeight, bool flipX = false)
    {
        if (_pixels == null || source == null || drawWidth <= 0 || drawHeight <= 0)
            return;

        Color32[] sourcePixels = source.GetPixels32();
        int sourceWidth = source.width;
        int sourceHeight = source.height;
        for (int dy = 0; dy < drawHeight; dy++)
        {
            int sourceY = Mathf.Clamp(dy * sourceHeight / drawHeight, 0, sourceHeight - 1);
            for (int dx = 0; dx < drawWidth; dx++)
            {
                int mappedX = flipX ? drawWidth - 1 - dx : dx;
                int sourceX = Mathf.Clamp(mappedX * sourceWidth / drawWidth, 0, sourceWidth - 1);
                Color32 color = sourcePixels[sourceY * sourceWidth + sourceX];
                if (color.a < 16)
                    continue;
                SetPixel(x + dx, y + dy, color);
            }
        }
    }

    private void DrawTextCentered(string text, int y, int scale, Color32 color)
        => DrawText(text, (Width - TextWidth(text, scale)) / 2, y, scale, color);

    private static int TextWidth(string text, int scale) => Math.Max(0, text.Length * 4 * scale - scale);

    private void DrawText(string text, int x, int y, int scale, Color32 color)
    {
        int cursor = x;
        foreach (char raw in text)
        {
            string glyph = Glyph(char.ToUpperInvariant(raw));
            for (int row = 0; row < 5; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    if (glyph[row * 3 + col] == '1')
                        FillRect(cursor + col * scale, y + (4 - row) * scale, scale, scale, color);
                }
            }
            cursor += 4 * scale;
        }
    }

    private static string Glyph(char character) => character switch
    {
        'A' => "010101111101101", 'B' => "110101110101110", 'C' => "111100100100111",
        'D' => "110101101101110", 'E' => "111100110100111", 'F' => "111100110100100",
        'G' => "111100101101111", 'I' => "111010010010111", 'L' => "100100100100111",
        'H' => "101101111101101", 'M' => "101111111101101", 'N' => "101111111111101", 'O' => "111101101101111",
        'P' => "110101110100100", 'R' => "110101110101101", 'S' => "111100111001111",
        'T' => "111010010010010", 'U' => "101101101101111", 'V' => "101101101101010",
        'W' => "101101111111101", 'Y' => "101101010010010", 'K' => "101101110101101",
        '0' => "111101101101111", '1' => "010110010010111", '2' => "111001111100111",
        '3' => "111001111001111", '4' => "101101111001001", '5' => "111100111001111",
        '6' => "111100111101111", '7' => "111001010010010", '8' => "111101111101111",
        '9' => "111101111001111", '-' => "000000111000000", '!' => "010010010000010",
        ' ' => "000000000000000", _ => "000000000000000"
    };

    public void Stop() { }

    public void Dispose()
    {
        if (_texture != null)
        {
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
        _pixels = null;
        foreach (Texture2D art in _suppliedArt)
        {
            if (art != null)
                UnityEngine.Object.Destroy(art);
        }
        _suppliedArt.Clear();
        _sourcePellet = null;
        _sourcePowerPellet = null;
        Array.Clear(_sourcePacmanSprites, 0, _sourcePacmanSprites.Length);
        Array.Clear(_sourceGhostSprites, 0, _sourceGhostSprites.Length);
        _pellets.Clear();
        _powerPellets.Clear();
        _ghosts.Clear();
    }
}

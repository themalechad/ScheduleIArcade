using UnityEngine;
using MelonLoader;
using ScheduleIArcade.Core;
using ScheduleIArcade.Utils;
using ScheduleIArcade.Games.Pacman;

namespace ScheduleIArcade.Games;

/// <summary>
/// Self-contained cocktail-table Pac-Man game. Its grid movement, pellet scoring, power-pellet
/// frightened ghosts, lives, level reset and arrow-key controls follow the supplied PacMan v1.1.1
/// game's gameplay structure, while rendering to Schedule I's in-world table texture instead of
/// attempting to load that separate Mono Unity executable into an IL2CPP game.
/// </summary>
internal sealed class PacmanCocktailGame : IArcadeGame
{
    // Native arcade resolution: 28 x 36 tiles of 8 px = 224 x 288 (portrait). The 28 x 31 maze sits
    // between two status rows at the bottom and three score rows at the top, exactly like the cabinet.
    public const int Width = 224;
    public const int Height = 288;
    private const int Columns = PacmanMaze.Columns;
    private const int Rows = PacmanMaze.Rows;
    private const int Cell = 8;
    private const int OriginX = 0;
    private const int OriginY = 16;

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
    private static readonly Color32 DoorPink = new(255, 184, 222, 255);

    private enum GameState
    {
        Ready,
        Playing,
        Paused,
        GameOver,
        LevelClear
    }

    private readonly PacmanMaze _maze = new();
    private readonly HashSet<Vector2Int> _pellets = new();
    private readonly HashSet<Vector2Int> _powerPellets = new();
    private readonly List<Texture2D> _suppliedArt = new();
    private PacmanGhostAi _ai = new(new PacmanMaze(), 1);

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
    private float _playerProgress;
    private float _nextDraw;
    private GameState _state;

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
        _score = 0;
        _lives = 3;
        _level = 1;
        StartLevel();
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
                TickPlaying(Mathf.Min(deltaTime, 0.1f));
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

    private void StartLevel()
    {
        _pellets.Clear();
        _powerPellets.Clear();
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                var cell = new Vector2Int(x, y);
                char mazeCell = _maze.Cell(x, y);
                if (mazeCell == '.')
                    _pellets.Add(cell);
                else if (mazeCell == 'o')
                    _powerPellets.Add(cell);
            }
        }

        _ai = new PacmanGhostAi(_maze, _level, seed: 1 + _level);
        ResetPositions();
        _state = GameState.Ready;
        _stateTimer = 1.2f;
        _nextDraw = 0f;
    }

    private void ResetPositions()
    {
        _player = new Vector2Int(PacmanMaze.PacmanStart.X, PacmanMaze.PacmanStart.Y);
        _playerPrevious = _player;
        _playerDirection = Vector2Int.left;
        _requestedDirection = _playerDirection;
        _playerProgress = 0f;
    }

    private void CaptureInput()
    {
        if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _requestedDirection = Vector2Int.left;
        else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _requestedDirection = Vector2Int.right;
        else if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _requestedDirection = Vector2Int.up;
        else if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _requestedDirection = Vector2Int.down;
    }

    private static PacTile T(Vector2Int v) => new(v.x, v.y);

    private void TickPlaying(float deltaTime)
    {
        float pacSpeed = PacmanGhostAi.PacmanSpeedFraction(_level, _ai.AnyFrightened) * PacmanGhostAi.FullSpeedTilesPerSecond;
        _playerProgress += pacSpeed * deltaTime;
        int guard = 0;
        while (_playerProgress >= 1f && guard++ < 8)
        {
            _playerProgress -= 1f;
            StepPlayer();
            ConsumeCurrentCell();
            if (_state != GameState.Playing)
                return;
        }

        _ai.Update(deltaTime, T(_player), T(_playerDirection));
        CheckGhostCollision();
    }

    private void StepPlayer()
    {
        _playerPrevious = _player;
        if (TryGetOpenStep(_player, _requestedDirection, out _))
            _playerDirection = _requestedDirection;
        if (TryGetOpenStep(_player, _playerDirection, out Vector2Int forwardStep))
            _player = forwardStep;
    }

    private void ConsumeCurrentCell()
    {
        if (_pellets.Remove(_player))
        {
            _score += 10;
            _ai.OnDotEaten(energizer: false);
        }
        if (_powerPellets.Remove(_player))
        {
            _score += 50;
            _ai.OnDotEaten(energizer: true);
        }
        CheckGhostCollision();
        if (_state == GameState.Playing && _pellets.Count == 0 && _powerPellets.Count == 0)
        {
            _state = GameState.LevelClear;
            _stateTimer = 1.8f;
        }
    }

    private void CheckGhostCollision()
    {
        if (_state != GameState.Playing)
            return;
        PacTile pac = T(_player);
        PacTile pacPrevious = T(_playerPrevious);
        foreach (PacmanGhost ghost in _ai.Ghosts)
        {
            if (ghost.State != GhostState.Active)
                continue;
            bool sameTile = ghost.Tile == pac;
            bool crossed = ghost.Tile == pacPrevious && ghost.Previous == pac;
            if (!sameTile && !crossed)
                continue;

            if (ghost.Frightened)
            {
                _score += _ai.EatGhost(ghost);
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
                _ai.ResetAfterLifeLost();
                ResetPositions();
                _state = GameState.Ready;
                _stateTimer = 1.0f;
            }
            return;
        }
    }

    private bool TryGetOpenStep(Vector2Int position, Vector2Int direction, out Vector2Int destination)
    {
        PacTile next = _maze.Step(T(position), new PacTile(direction.x, direction.y));
        destination = new Vector2Int(next.X, next.Y);
        // The middle row is a wraparound tunnel; the house door and interior are closed to Pac-Man.
        return _maze.IsOpenForActor(next);
    }

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
        string path = ModPaths.Find(Path.Combine(ModPaths.PacManAssetsRoot, fileName));
        byte[]? bytes = ModFiles.ReadAllBytes(path);
        if (bytes == null || bytes.Length == 0)
            return null;

        try
        {
            var art = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "ScheduleIArcade_SourcePacMan_" + Path.GetFileNameWithoutExtension(fileName),
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!ImageConversion.LoadImage(art, bytes, false))
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
        // Score rows (top three tile rows) and status rows (bottom two) like the arcade screen.
        DrawText("1UP", 16, Height - 9, 1, White);
        DrawText(_score.ToString("00000"), 16, Height - 17, 1, White);
        DrawText("LEVEL " + _level, 150, Height - 9, 1, White);
        DrawText("LIVES " + _lives, 150, Height - 17, 1, White);
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
                char c = _maze.Cell(x, y);
                if (c == '#')
                {
                    FillRect(px, py, Cell, Cell, MazeBlue);
                    FillRect(px + 2, py + 2, Cell - 4, Cell - 4, MazeInner);
                    continue;
                }
                if (c == '-')
                {
                    FillRect(px, py + 3, Cell, 2, DoorPink);
                    continue;
                }

                Vector2Int point = new(x, y);
                if (_powerPellets.Contains(point))
                {
                    int pulse = (int)(Time.time * 5f) & 1;
                    if (_sourcePowerPellet != null)
                        DrawSourceImage(_sourcePowerPellet, px + 1 - pulse, py + 1 - pulse, 6 + pulse * 2, 6 + pulse * 2);
                    else
                        FillRect(px + 1 - pulse, py + 1 - pulse, 6 + pulse * 2, 6 + pulse * 2, White);
                }
                else if (_pellets.Contains(point))
                {
                    if (_sourcePellet != null)
                        DrawSourceImage(_sourcePellet, px + 3, py + 3, 2, 2);
                    // Always stamp a high-contrast centre so pellets never vanish at table distance.
                    FillRect(px + 3, py + 3, 2, 2, new Color32(255, 228, 184, 255));
                }
            }
        }
    }

    private void DrawPacMan()
    {
        float renderProgress = Mathf.Abs(_player.x - _playerPrevious.x) > 1 || Mathf.Abs(_player.y - _playerPrevious.y) > 1
            ? 1f
            : Mathf.Clamp01(_playerProgress);
        float cellX = Mathf.Lerp(_playerPrevious.x, _player.x, renderProgress);
        float cellY = Mathf.Lerp(_playerPrevious.y, _player.y, renderProgress);
        int cx = OriginX + Mathf.RoundToInt(cellX * Cell) + Cell / 2;
        int cy = OriginY + Mathf.RoundToInt(cellY * Cell) + Cell / 2;
        Texture2D? sprite = _sourcePacmanSprites[(int)(Time.time * 10f) % _sourcePacmanSprites.Length];
        if (sprite != null)
        {
            DrawSourceImage(sprite, cx - 6, cy - 6, 13, 13, _playerDirection == Vector2Int.left);
            return;
        }

        bool openMouth = ((int)(Time.time * 10f) & 1) == 0;
        for (int y = -6; y <= 6; y++)
        {
            for (int x = -6; x <= 6; x++)
            {
                if (x * x + y * y > 40)
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

    private static Color32 GhostColour(GhostId id) => id switch
    {
        GhostId.Blinky => Red,
        GhostId.Pinky => Pink,
        GhostId.Inky => Cyan,
        _ => Orange
    };

    private void DrawGhosts()
    {
        foreach (PacmanGhost ghost in _ai.Ghosts)
        {
            PacTile from = ghost.Previous;
            PacTile to = ghost.Tile;
            // A tunnel wrap should never animate a ghost straight across the full maze.
            float renderProgress = Math.Abs(to.X - from.X) > 1 || Math.Abs(to.Y - from.Y) > 1
                ? 1f
                : Mathf.Clamp01(ghost.Progress);
            float cellX = Mathf.Lerp(from.X, to.X, renderProgress);
            float cellY = Mathf.Lerp(from.Y, to.Y, renderProgress);
            int x = OriginX + Mathf.RoundToInt(cellX * Cell) - 2;
            int y = OriginY + Mathf.RoundToInt(cellY * Cell) - 2;

            bool eyesOnly = ghost.State == GhostState.Eaten || ghost.State == GhostState.EnteringHouse;
            if (eyesOnly)
            {
                FillRect(x + 2, y + 5, 4, 4, White);
                FillRect(x + 7, y + 5, 4, 4, White);
                FillRect(x + 3 + Math.Max(0, ghost.Direction.X), y + 6 + ghost.Direction.Y, 2, 2, MazeBlue);
                FillRect(x + 8 + Math.Max(0, ghost.Direction.X), y + 6 + ghost.Direction.Y, 2, 2, MazeBlue);
                continue;
            }

            bool flashing = ghost.Frightened && _ai.FrightenedTimeLeft < 2f && ((int)(Time.time * 6f) & 1) == 0;
            Color32 color = ghost.Frightened ? (flashing ? White : Frightened) : GhostColour(ghost.Id);
            Texture2D? sprite = _sourceGhostSprites[(int)ghost.Id % _sourceGhostSprites.Length];
            if (sprite != null && !ghost.Frightened)
            {
                DrawSourceImage(sprite, x, y, 12, 12);
                continue;
            }
            FillRect(x + 1, y + 3, 10, 8, color);
            FillRect(x + 3, y + 11, 6, 1, color);
            FillRect(x + 1, y + 2, 2, 1, color);
            FillRect(x + 4, y + 2, 4, 1, color);
            FillRect(x + 9, y + 2, 2, 1, color);
            FillRect(x + 3, y + 7, 2, 2, ghost.Frightened ? Pink : White);
            FillRect(x + 7, y + 7, 2, 2, ghost.Frightened ? Pink : White);
        }
    }

    private void DrawOverlay(string text, Color32 color)
    {
        int width = TextWidth(text, 2) + 18;
        int x = (Width - width) / 2;
        // Centred on the row below the ghost house, where the arcade prints READY!
        FillRect(x, 108, width, 24, new Color32(0, 0, 0, 230));
        DrawRect(x, 108, width, 24, color, 1);
        DrawTextCentered(text, 116, 2, color);
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

    }
}

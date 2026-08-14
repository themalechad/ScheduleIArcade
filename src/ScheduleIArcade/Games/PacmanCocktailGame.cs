using UnityEngine;

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
    private const int Columns = 21;
    private const int Rows = 23;
    private const int Cell = 9;
    private const int OriginX = (Width - Columns * Cell) / 2;
    private const int OriginY = 24;

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
            Color = color;
        }

        public Vector2Int Spawn { get; }
        public Vector2Int Position { get; set; }
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

    private Texture2D? _texture;
    private Color32[]? _pixels;
    private Vector2Int _player;
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
            _nextDraw = 1f / 30f;
            DrawFrame();
        }
    }

    private void BuildMaze()
    {
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                bool edge = x == 0 || x == Columns - 1 || y == 0 || y == Rows - 1;
                bool verticalOuter = (x == 4 || x == 16) && ((y >= 2 && y <= 7) || (y >= 15 && y <= 20));
                bool verticalMiddle = x == 10 && ((y >= 2 && y <= 6) || (y >= 16 && y <= 20));
                bool horizontalUpper = y == 5 && x >= 6 && x <= 14;
                bool horizontalCentre = y == 11 && (x <= 6 || x >= 14);
                bool horizontalLower = y == 17 && x >= 6 && x <= 14;
                // The ghost pen is deliberately pellet-free, but its interior must remain open:
                // the four ghosts spawn there and need a route out into the maze.
                _walls[x, y] = edge || verticalOuter || verticalMiddle || horizontalUpper || horizontalCentre || horizontalLower;
            }
        }
    }

    private void StartLevel()
    {
        _pellets.Clear();
        _powerPellets.Clear();
        for (int y = 1; y < Rows - 1; y++)
        {
            for (int x = 1; x < Columns - 1; x++)
            {
                var cell = new Vector2Int(x, y);
                if (IsOpen(cell) && !IsGhostHouse(cell))
                    _pellets.Add(cell);
            }
        }

        AddPowerPellet(new Vector2Int(1, 1));
        AddPowerPellet(new Vector2Int(Columns - 2, 1));
        AddPowerPellet(new Vector2Int(1, Rows - 2));
        AddPowerPellet(new Vector2Int(Columns - 2, Rows - 2));

        _ghosts.Clear();
        _ghosts.Add(new Ghost(new Vector2Int(9, 10), Red));
        _ghosts.Add(new Ghost(new Vector2Int(11, 10), Pink));
        _ghosts.Add(new Ghost(new Vector2Int(9, 13), Cyan));
        _ghosts.Add(new Ghost(new Vector2Int(11, 13), Orange));
        ResetPositions();
        _state = GameState.Ready;
        _stateTimer = 1.2f;
        _nextDraw = 0f;
    }

    private void AddPowerPellet(Vector2Int position)
    {
        if (!IsOpen(position))
            return;
        _pellets.Remove(position);
        _powerPellets.Add(position);
    }

    private void ResetPositions()
    {
        _player = new Vector2Int(10, 14);
        _playerDirection = Vector2Int.left;
        _requestedDirection = _playerDirection;
        _playerTimer = 0f;
        _ghostTimer = 0f;
        _frightenedTimer = 0f;
        for (int i = 0; i < _ghosts.Count; i++)
        {
            _ghosts[i].Position = _ghosts[i].Spawn;
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
        if (IsOpen(_player + _requestedDirection))
            _playerDirection = _requestedDirection;
        if (IsOpen(_player + _playerDirection))
            _player += _playerDirection;
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
            Vector2Int candidate = ghost.Position + direction;
            if (IsOpen(candidate) && (direction != reverse || choices.Count == 0))
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
                Vector2Int probe = ghost.Position + choices[i];
                int distance = Mathf.Abs(probe.x - _player.x) + Mathf.Abs(probe.y - _player.y);
                if (distance < bestDistance || (distance == bestDistance && _random.NextDouble() < 0.18))
                {
                    bestDistance = distance;
                    directionChoice = choices[i];
                }
            }
        }
        ghost.Direction = directionChoice;
        ghost.Position += directionChoice;
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

    private static bool IsGhostHouse(Vector2Int position)
        => position.y >= 9 && position.y <= 12 && position.x >= 8 && position.x <= 12;

    private static readonly Vector2Int[] Directions = { Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down };

    private void DrawFrame()
    {
        if (_texture == null || _pixels == null)
            return;

        Clear(Black);
        DrawTextCentered("PAC-MAN", 234, 2, Yellow);
        DrawText("SCORE " + _score.ToString("00000"), 9, 5, 1, White);
        DrawText("LIVES " + _lives, 142, 5, 1, White);
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

        DrawTextCentered(_state == GameState.GameOver ? "ENTER RETRY" : "ARROWS MOVE  TAB PAUSE", 12, 1, White);
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
                    FillRect(px + 2, py + 2, Cell - 4, Cell - 4, MazeInner);
                    continue;
                }

                Vector2Int point = new(x, y);
                if (_powerPellets.Contains(point))
                {
                    int pulse = (int)(Time.time * 5f) & 1;
                    FillRect(px + 2 - pulse, py + 2 - pulse, 5 + pulse * 2, 5 + pulse * 2, White);
                }
                else if (_pellets.Contains(point))
                {
                    FillRect(px + 4, py + 4, 2, 2, White);
                }
            }
        }
    }

    private void DrawPacMan()
    {
        int cx = OriginX + _player.x * Cell + Cell / 2;
        int cy = OriginY + _player.y * Cell + Cell / 2;
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
        for (int i = 0; i < _ghosts.Count; i++)
        {
            Ghost ghost = _ghosts[i];
            int x = OriginX + ghost.Position.x * Cell + 1;
            int y = OriginY + ghost.Position.y * Cell + 1;
            Color32 color = _frightenedTimer > 0f ? Frightened : ghost.Color;
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
        'M' => "101111111101101", 'N' => "101111111111101", 'O' => "111101101101111",
        'P' => "110101110100100", 'R' => "110101110101101", 'S' => "111100111001111",
        'T' => "111010010010010", 'U' => "101101101101111", 'V' => "101101101101010",
        'Y' => "101101010010010", 'K' => "101101110101101",
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
        _pellets.Clear();
        _powerPellets.Clear();
        _ghosts.Clear();
    }
}

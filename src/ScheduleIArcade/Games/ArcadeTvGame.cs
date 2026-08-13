using UnityEngine;

namespace ScheduleIArcade.Games;

/// <summary>
/// A tiny self-contained TV arcade containing two games. It renders directly into a low-resolution
/// Texture2D, so it needs no external emulator, UI canvas, or game asset at runtime.
/// </summary>
internal sealed class ArcadeTvGame : IArcadeGame
{
    private const int Width = 256;
    private const int Height = 144;
    private const int SnakeColumns = 28;
    private const int SnakeRows = 14;
    private const int SnakeCell = 8;

    private static readonly Color32 Background = new(3, 7, 20, 255);
    private static readonly Color32 Panel = new(12, 22, 48, 255);
    private static readonly Color32 White = new(235, 245, 255, 255);
    private static readonly Color32 Cyan = new(20, 225, 255, 255);
    private static readonly Color32 Pink = new(255, 42, 132, 255);
    private static readonly Color32 Yellow = new(255, 218, 52, 255);
    private static readonly Color32 Green = new(58, 245, 115, 255);

    private static readonly Dictionary<char, string> Font = new()
    {
        [' '] = "....." + "....." + "....." + "....." + "....." + "....." + ".....",
        ['-'] = "....." + "....." + "....." + ".###." + "....." + "....." + ".....",
        ['+'] = "....." + "..#.." + "..#.." + ".###." + "..#.." + "..#.." + ".....",
        ['0'] = ".###." + "#...#" + "#..##" + "#.#.#" + "##..#" + "#...#" + ".###.",
        ['1'] = "..#.." + ".##.." + "..#.." + "..#.." + "..#.." + "..#.." + ".###.",
        ['2'] = ".###." + "#...#" + "....#" + "...#." + "..#.." + ".#..." + "#####",
        ['3'] = "####." + "....#" + "....#" + ".###." + "....#" + "....#" + "####.",
        ['4'] = "...#." + "..##." + ".#.#." + "#..#." + "#####" + "...#." + "...#.",
        ['5'] = "#####" + "#...." + "#...." + "####." + "....#" + "....#" + "####.",
        ['6'] = ".###." + "#...." + "#...." + "####." + "#...#" + "#...#" + ".###.",
        ['7'] = "#####" + "....#" + "...#." + "..#.." + ".#..." + ".#..." + ".#...",
        ['8'] = ".###." + "#...#" + "#...#" + ".###." + "#...#" + "#...#" + ".###.",
        ['9'] = ".###." + "#...#" + "#...#" + ".####" + "....#" + "....#" + ".###.",
        ['A'] = ".###." + "#...#" + "#...#" + "#####" + "#...#" + "#...#" + "#...#",
        ['B'] = "####." + "#...#" + "#...#" + "####." + "#...#" + "#...#" + "####.",
        ['C'] = ".####" + "#...." + "#...." + "#...." + "#...." + "#...." + ".####",
        ['D'] = "####." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + "####.",
        ['E'] = "#####" + "#...." + "#...." + "####." + "#...." + "#...." + "#####",
        ['F'] = "#####" + "#...." + "#...." + "####." + "#...." + "#...." + "#....",
        ['G'] = ".####" + "#...." + "#...." + "#.###" + "#...#" + "#...#" + ".###.",
        ['H'] = "#...#" + "#...#" + "#...#" + "#####" + "#...#" + "#...#" + "#...#",
        ['I'] = ".###." + "..#.." + "..#.." + "..#.." + "..#.." + "..#.." + ".###.",
        ['J'] = "..###" + "...#." + "...#." + "...#." + "...#." + "#..#." + ".##..",
        ['K'] = "#...#" + "#..#." + "#.#.." + "##..." + "#.#.." + "#..#." + "#...#",
        ['L'] = "#...." + "#...." + "#...." + "#...." + "#...." + "#...." + "#####",
        ['M'] = "#...#" + "##.##" + "#.#.#" + "#.#.#" + "#...#" + "#...#" + "#...#",
        ['N'] = "#...#" + "##..#" + "##..#" + "#.#.#" + "#..##" + "#..##" + "#...#",
        ['O'] = ".###." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".###.",
        ['P'] = "####." + "#...#" + "#...#" + "####." + "#...." + "#...." + "#....",
        ['Q'] = ".###." + "#...#" + "#...#" + "#...#" + "#.#.#" + "#..#." + ".##.#",
        ['R'] = "####." + "#...#" + "#...#" + "####." + "#.#.." + "#..#." + "#...#",
        ['S'] = ".####" + "#...." + "#...." + ".###." + "....#" + "....#" + "####.",
        ['T'] = "#####" + "..#.." + "..#.." + "..#.." + "..#.." + "..#.." + "..#..",
        ['U'] = "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".###.",
        ['V'] = "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".#.#." + "..#..",
        ['W'] = "#...#" + "#...#" + "#...#" + "#.#.#" + "#.#.#" + "##.##" + "#...#",
        ['X'] = "#...#" + "#...#" + ".#.#." + "..#.." + ".#.#." + "#...#" + "#...#",
        ['Y'] = "#...#" + "#...#" + ".#.#." + "..#.." + "..#.." + "..#.." + "..#..",
        ['Z'] = "#####" + "....#" + "...#." + "..#.." + ".#..." + "#...." + "#####"
    };

    private enum TvMode
    {
        Menu,
        Pong,
        Snake
    }

    private Texture2D? _texture;
    private Color32[]? _pixels;
    private TvMode _mode;
    private int _menuSelection;
    private float _nextDraw;

    private float _pongPlayerY;
    private float _pongAiY;
    private float _pongBallX;
    private float _pongBallY;
    private float _pongBallVelocityX;
    private float _pongBallVelocityY;
    private int _pongPlayerScore;
    private int _pongAiScore;

    private readonly List<Vector2Int> _snake = new();
    private readonly System.Random _random = new();
    private Vector2Int _snakeDirection;
    private Vector2Int _pendingSnakeDirection;
    private Vector2Int _food;
    private float _snakeTimer;
    private bool _snakeGameOver;

    public string Id => "arcade_tv";
    public string DisplayName => "Arcade TV - Pong and Snake";
    public Texture? ScreenTexture => _texture;

    public void Initialize()
    {
        if (_texture != null)
            return;

        _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_InteractiveTv",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _pixels = new Color32[Width * Height];
        DrawFrame();
    }

    public void Start()
    {
        _mode = TvMode.Menu;
        _menuSelection = 0;
        _nextDraw = 0f;
        DrawFrame();
    }

    public void Tick(float deltaTime)
    {
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            _mode = TvMode.Menu;
            _nextDraw = 0f;
        }

        switch (_mode)
        {
            case TvMode.Menu:
                TickMenu();
                break;
            case TvMode.Pong:
                TickPong(deltaTime);
                break;
            case TvMode.Snake:
                TickSnake(deltaTime);
                break;
        }

        _nextDraw -= deltaTime;
        if (_nextDraw <= 0f)
        {
            _nextDraw = 1f / 30f;
            DrawFrame();
        }
    }

    private void TickMenu()
    {
        if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow))
        {
            _menuSelection = 1 - _menuSelection;
            _nextDraw = 0f;
        }

        if (!Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter) &&
            !Input.GetKeyDown(KeyCode.Space) && !Input.GetKeyDown(KeyCode.E))
            return;

        if (_menuSelection == 0)
        {
            ResetPong();
            _mode = TvMode.Pong;
        }
        else
        {
            ResetSnake();
            _mode = TvMode.Snake;
        }
        _nextDraw = 0f;
    }

    private void ResetPong()
    {
        _pongPlayerY = Height * 0.5f;
        _pongAiY = Height * 0.5f;
        _pongPlayerScore = 0;
        _pongAiScore = 0;
        ServePong(1f);
    }

    private void ServePong(float direction)
    {
        _pongBallX = Width * 0.5f;
        _pongBallY = Height * 0.5f;
        _pongBallVelocityX = 92f * direction;
        _pongBallVelocityY = (float)(_random.NextDouble() * 70.0 - 35.0);
    }

    private void TickPong(float deltaTime)
    {
        const float paddleHalf = 15f;
        const float playBottom = 16f;
        const float playTop = Height - 9f;

        float playerInput = 0f;
        if (Input.GetKey(KeyCode.UpArrow)) playerInput += 1f;
        if (Input.GetKey(KeyCode.DownArrow)) playerInput -= 1f;
        _pongPlayerY = Mathf.Clamp(_pongPlayerY + playerInput * 105f * deltaTime, playBottom + paddleHalf, playTop - paddleHalf);

        float aiTarget = _pongBallY + Mathf.Sin(Time.time * 1.8f) * 6f;
        _pongAiY = Mathf.MoveTowards(_pongAiY, aiTarget, 72f * deltaTime);
        _pongAiY = Mathf.Clamp(_pongAiY, playBottom + paddleHalf, playTop - paddleHalf);

        _pongBallX += _pongBallVelocityX * deltaTime;
        _pongBallY += _pongBallVelocityY * deltaTime;

        if (_pongBallY <= playBottom || _pongBallY >= playTop)
        {
            _pongBallY = Mathf.Clamp(_pongBallY, playBottom, playTop);
            _pongBallVelocityY = -_pongBallVelocityY;
        }

        if (_pongBallVelocityX < 0f && _pongBallX <= 17f && _pongBallX >= 10f &&
            Mathf.Abs(_pongBallY - _pongPlayerY) <= paddleHalf + 3f)
        {
            _pongBallX = 17f;
            _pongBallVelocityX = Mathf.Abs(_pongBallVelocityX) * 1.035f;
            _pongBallVelocityY += (_pongBallY - _pongPlayerY) * 3.0f;
        }
        else if (_pongBallVelocityX > 0f && _pongBallX >= Width - 18f && _pongBallX <= Width - 10f &&
                 Mathf.Abs(_pongBallY - _pongAiY) <= paddleHalf + 3f)
        {
            _pongBallX = Width - 18f;
            _pongBallVelocityX = -Mathf.Abs(_pongBallVelocityX) * 1.035f;
            _pongBallVelocityY += (_pongBallY - _pongAiY) * 2.4f;
        }

        if (_pongBallX < -4f)
        {
            _pongAiScore++;
            ServePong(1f);
        }
        else if (_pongBallX > Width + 4f)
        {
            _pongPlayerScore++;
            ServePong(-1f);
        }
    }

    private void ResetSnake()
    {
        _snake.Clear();
        _snake.Add(new Vector2Int(8, 7));
        _snake.Add(new Vector2Int(7, 7));
        _snake.Add(new Vector2Int(6, 7));
        _snakeDirection = Vector2Int.right;
        _pendingSnakeDirection = _snakeDirection;
        _snakeTimer = 0f;
        _snakeGameOver = false;
        PlaceFood();
    }

    private void TickSnake(float deltaTime)
    {
        if (_snakeGameOver)
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space))
                ResetSnake();
            return;
        }

        if (Input.GetKeyDown(KeyCode.UpArrow) && _snakeDirection != Vector2Int.down)
            _pendingSnakeDirection = Vector2Int.up;
        else if (Input.GetKeyDown(KeyCode.DownArrow) && _snakeDirection != Vector2Int.up)
            _pendingSnakeDirection = Vector2Int.down;
        else if (Input.GetKeyDown(KeyCode.LeftArrow) && _snakeDirection != Vector2Int.right)
            _pendingSnakeDirection = Vector2Int.left;
        else if (Input.GetKeyDown(KeyCode.RightArrow) && _snakeDirection != Vector2Int.left)
            _pendingSnakeDirection = Vector2Int.right;

        _snakeTimer += deltaTime;
        float stepTime = Mathf.Max(0.065f, 0.145f - (_snake.Count - 3) * 0.0025f);
        if (_snakeTimer < stepTime)
            return;

        _snakeTimer -= stepTime;
        _snakeDirection = _pendingSnakeDirection;
        Vector2Int head = _snake[0] + _snakeDirection;
        if (head.x < 0 || head.x >= SnakeColumns || head.y < 0 || head.y >= SnakeRows || _snake.Contains(head))
        {
            _snakeGameOver = true;
            return;
        }

        _snake.Insert(0, head);
        if (head == _food)
            PlaceFood();
        else
            _snake.RemoveAt(_snake.Count - 1);
    }

    private void PlaceFood()
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = new Vector2Int(_random.Next(SnakeColumns), _random.Next(SnakeRows));
            if (!_snake.Contains(candidate))
            {
                _food = candidate;
                return;
            }
        }
    }

    private void DrawFrame()
    {
        if (_texture == null || _pixels == null)
            return;

        Clear(Background);
        switch (_mode)
        {
            case TvMode.Menu:
                DrawMenu();
                break;
            case TvMode.Pong:
                DrawPong();
                break;
            case TvMode.Snake:
                DrawSnake();
                break;
        }

        _texture.SetPixels32(_pixels);
        _texture.Apply(false, false);
    }

    private void DrawMenu()
    {
        DrawRect(2, 2, Width - 4, Height - 4, Cyan, 2);
        DrawTextCentered("ARCADE TV", 119, 3, Yellow);
        DrawTextCentered("SELECT A GAME", 97, 1, White);

        DrawGamePanel(17, 39, 102, 45, "PONG", _menuSelection == 0, Cyan);
        DrawGamePanel(137, 39, 102, 45, "SNAKE", _menuSelection == 1, Green);

        DrawTextCentered("LEFT RIGHT  SELECT", 21, 1, White);
        DrawTextCentered("ENTER  START", 10, 1, Pink);
    }

    private void DrawGamePanel(int x, int y, int width, int height, string title, bool selected, Color32 accent)
    {
        FillRect(x, y, width, height, selected ? new Color32(23, 42, 72, 255) : Panel);
        DrawRect(x, y, width, height, selected ? White : accent, selected ? 3 : 1);
        DrawTextCenteredIn(title, x, width, y + 18, 2, selected ? Yellow : accent);
        if (selected)
            DrawTextCenteredIn("PLAY", x, width, y + 6, 1, White);
    }

    private void DrawPong()
    {
        const int playBottom = 16;
        const int playTop = Height - 9;
        DrawRect(3, playBottom, Width - 6, playTop - playBottom, Cyan, 1);
        for (int y = playBottom + 4; y < playTop - 2; y += 8)
            FillRect(Width / 2 - 1, y, 2, 4, new Color32(65, 92, 125, 255));

        FillRect(11, Mathf.RoundToInt(_pongPlayerY) - 15, 5, 30, White);
        FillRect(Width - 16, Mathf.RoundToInt(_pongAiY) - 15, 5, 30, Pink);
        FillRect(Mathf.RoundToInt(_pongBallX) - 2, Mathf.RoundToInt(_pongBallY) - 2, 5, 5, Yellow);
        DrawTextCentered($"{_pongPlayerScore}  {_pongAiScore}", 124, 2, White);
        DrawTextCentered("UP DOWN MOVE   TAB MENU", 5, 1, White);
    }

    private void DrawSnake()
    {
        const int originX = 16;
        const int originY = 16;
        DrawTextCentered($"SNAKE  SCORE {_snake.Count - 3}", 132, 1, Green);
        DrawRect(originX - 2, originY - 2, SnakeColumns * SnakeCell + 4, SnakeRows * SnakeCell + 4, Green, 1);

        FillRect(originX + _food.x * SnakeCell + 1, originY + _food.y * SnakeCell + 1, SnakeCell - 2, SnakeCell - 2, Pink);
        for (int i = _snake.Count - 1; i >= 0; i--)
        {
            Vector2Int segment = _snake[i];
            Color32 color = i == 0 ? Yellow : Green;
            FillRect(originX + segment.x * SnakeCell + 1, originY + segment.y * SnakeCell + 1, SnakeCell - 2, SnakeCell - 2, color);
        }

        if (_snakeGameOver)
        {
            FillRect(48, 58, 160, 34, new Color32(5, 8, 20, 245));
            DrawRect(48, 58, 160, 34, Pink, 2);
            DrawTextCentered("GAME OVER", 77, 2, Pink);
            DrawTextCentered("ENTER RETRY", 64, 1, White);
        }
        else
        {
            DrawTextCentered("ARROWS MOVE   TAB MENU", 5, 1, White);
        }
    }

    private void Clear(Color32 color)
    {
        if (_pixels == null)
            return;
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

    private void DrawTextCentered(string text, int y, int scale, Color32 color)
    {
        int width = TextWidth(text, scale);
        DrawText(text, (Width - width) / 2, y, scale, color);
    }

    private void DrawTextCenteredIn(string text, int x, int width, int y, int scale, Color32 color)
    {
        DrawText(text, x + (width - TextWidth(text, scale)) / 2, y, scale, color);
    }

    private static int TextWidth(string text, int scale) => Math.Max(0, text.Length * 6 * scale - scale);

    private void DrawText(string text, int x, int y, int scale, Color32 color)
    {
        int cursor = x;
        foreach (char rawCharacter in text)
        {
            char character = char.ToUpperInvariant(rawCharacter);
            if (!Font.TryGetValue(character, out string? glyph))
                glyph = Font[' '];

            for (int row = 0; row < 7; row++)
            {
                for (int column = 0; column < 5; column++)
                {
                    if (glyph[row * 5 + column] == '#')
                        FillRect(cursor + column * scale, y + (6 - row) * scale, scale, scale, color);
                }
            }
            cursor += 6 * scale;
        }
    }

    public void Stop() { }

    public void Dispose()
    {
        if (_texture != null)
        {
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
        _pixels = null;
        _snake.Clear();
    }
}

using UnityEngine;

namespace ScheduleIArcade.Games;

/// <summary>
/// A self-contained arcade adaptation of the open SpaceCadetPinball project's table-driven
/// pinball concepts. It deliberately uses original graphics and an original table layout because
/// the upstream project does not distribute the original Windows game resource files.
/// </summary>
internal sealed class SpaceCadetPinballGame : IArcadeGame
{
    private const int Width = 320;
    private const int Height = 480;
    private const float Left = 22f;
    private const float Right = 298f;
    private const float Bottom = 26f;
    private const float Top = 452f;
    private const float BallRadius = 5.5f;
    // A short centre guard rejects a cheap straight-line drain without sealing the normal side
    // drains. It is deliberately below the flippers, so player timing still matters.
    private const float DrainGuardLeft = 123f;
    private const float DrainGuardRight = 197f;
    private const float DrainGuardY = 31f;
    private const float MaxStopperSpeed = 380f;
    // The right side is a real plunger barrel: a launch remains inside it until it clears
    // the lower sling/target cluster, then a gate feeds it cleanly into the playfield.
    private const float LaunchBarrelLeft = 271f;
    private const float LaunchBarrelRight = Right;
    private const float LaunchBarrelBottom = 38f;
    private const float LaunchBarrelExitY = 394f;
    private const float LaunchBallRestY = 58f;

    private static readonly Vector2[] Bumpers =
    {
        new(116f, 323f),
        new(203f, 334f),
        new(163f, 255f)
    };

    private static readonly Vector2[] Targets =
    {
        new(78f, 365f), new(78f, 316f), new(78f, 267f),
        new(242f, 365f), new(242f, 316f), new(242f, 267f)
    };

    private Texture2D? _texture;
    private Color32[]? _pixels;
    private Vector2 _ball;
    private Vector2 _velocity;
    private float _leftFlipperAngle;
    private float _rightFlipperAngle;
    private float _nextDraw;
    private float _flipperCooldown;
    private float _ballSaver;
    private readonly float[] _bumperCooldowns = new float[Bumpers.Length];
    private readonly float[] _targetCooldowns = new float[Targets.Length];
    private int _score;
    private int _balls;
    private int _multiplier;
    private int _targetsLit;
    private bool _waitingToLaunch;
    private bool _insideLaunchBarrel;
    private bool _gameOver;

    public string Id => "space_cadet_pinball";
    public string DisplayName => "Space Cadet Pinball";
    public Texture? ScreenTexture => _texture;

    public void Initialize()
    {
        if (_texture != null)
            return;

        _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_SpaceCadetPinball",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _pixels = new Color32[Width * Height];
        ResetGame();
        DrawFrame();
    }

    public void Start()
    {
        if (_gameOver)
            ResetGame();
    }

    public void Tick(float deltaTime)
    {
        if (_texture == null || _pixels == null)
            return;

        if (_gameOver)
        {
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return))
                ResetGame();
            DrawFrame();
            return;
        }

        bool leftPressed = Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.Z);
        bool rightPressed = Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.Slash) || Input.GetKey(KeyCode.X);
        _leftFlipperAngle = Mathf.MoveTowards(_leftFlipperAngle, leftPressed ? 67f : 22f, deltaTime * 540f);
        _rightFlipperAngle = Mathf.MoveTowards(_rightFlipperAngle, rightPressed ? 113f : 158f, deltaTime * 540f);

        if (_waitingToLaunch)
        {
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow))
                LaunchBall();
        }
        else
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(deltaTime * 180f), 1, 10);
            float step = Mathf.Min(deltaTime, 0.05f) / steps;
            for (int i = 0; i < steps && !_waitingToLaunch; i++)
                Simulate(step, leftPressed, rightPressed);
        }

        _nextDraw -= deltaTime;
        if (_nextDraw <= 0f)
        {
            _nextDraw = 1f / 45f;
            DrawFrame();
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
    }

    private void ResetGame()
    {
        _score = 0;
        _balls = 3;
        _multiplier = 1;
        _targetsLit = 0;
        _gameOver = false;
        _leftFlipperAngle = 22f;
        _rightFlipperAngle = 158f;
        Array.Clear(_bumperCooldowns, 0, _bumperCooldowns.Length);
        Array.Clear(_targetCooldowns, 0, _targetCooldowns.Length);
        PrepareBall();
    }

    private void PrepareBall()
    {
        _waitingToLaunch = true;
        _insideLaunchBarrel = true;
        _ball = new Vector2((LaunchBarrelLeft + LaunchBarrelRight) * 0.5f, LaunchBallRestY);
        _velocity = Vector2.zero;
        _ballSaver = 2.5f;
    }

    private void LaunchBall()
    {
        _waitingToLaunch = false;
        // This is deliberately stronger than a normal field shot so gravity cannot stall the
        // ball inside the tall barrel. The gate below converts it back to a calm field speed.
        _velocity = new Vector2(0f, 560f);
        AddScore(250);
    }

    private void Simulate(float dt, bool leftPressed, bool rightPressed)
    {
        _flipperCooldown = Mathf.Max(0f, _flipperCooldown - dt);
        _ballSaver = Mathf.Max(0f, _ballSaver - dt);
        for (int i = 0; i < _bumperCooldowns.Length; i++)
            _bumperCooldowns[i] = Mathf.Max(0f, _bumperCooldowns[i] - dt);
        for (int i = 0; i < _targetCooldowns.Length; i++)
            _targetCooldowns[i] = Mathf.Max(0f, _targetCooldowns[i] - dt);

        _velocity += new Vector2(0f, -260f) * dt;
        _velocity *= Mathf.Pow(0.996f, dt * 60f);
        _ball += _velocity * dt;

        ResolveWalls();
        TryEnterLaunchBarrel();
        ResolveLaunchBarrel();

        // Nothing in the playfield may hit the ball while it is still travelling up the
        // launcher. This prevents the old immediate target/sling collision on launch.
        if (_insideLaunchBarrel)
            return;

        ResolveBumpers();
        ResolveTargets();
        ResolveSlings();
        ResolveFlipper(new Vector2(96f, 74f), 54f, _leftFlipperAngle, leftPressed, true);
        ResolveFlipper(new Vector2(224f, 74f), 54f, _rightFlipperAngle, rightPressed, false);
        ResolveDrainGuard();

        if (_ball.y < 10f)
        {
            if (_ballSaver > 0f)
            {
                _ball = new Vector2(160f, 130f);
                _velocity = new Vector2(24f, 410f);
                AddScore(500);
            }
            else
                DrainBall();
        }
    }

    private void ResolveWalls()
    {
        if (_ball.x < Left + BallRadius)
        {
            _ball.x = Left + BallRadius;
            _velocity.x = Mathf.Abs(_velocity.x) * 0.88f;
        }
        else if (_ball.x > Right - BallRadius)
        {
            _ball.x = Right - BallRadius;
            _velocity.x = -Mathf.Abs(_velocity.x) * 0.88f;
        }

        if (_ball.y > Top - BallRadius)
        {
            _ball.y = Top - BallRadius;
            _velocity.y = -Mathf.Abs(_velocity.y) * 0.88f;
            AddScore(100);
        }

    }

    private void ResolveLaunchBarrel()
    {
        if (!_insideLaunchBarrel)
            return;

        float leftWall = LaunchBarrelLeft + BallRadius;
        float rightWall = LaunchBarrelRight - BallRadius;
        if (_ball.x < leftWall)
        {
            _ball.x = leftWall;
            _velocity.x = Mathf.Abs(_velocity.x) * 0.45f;
        }
        else if (_ball.x > rightWall)
        {
            _ball.x = rightWall;
            _velocity.x = -Mathf.Abs(_velocity.x) * 0.45f;
        }

        // A returning ball can fall back into the plunger barrel. Catch it on the spring and
        // restore the normal Space/Up launch state instead of draining or bouncing forever.
        if (!_waitingToLaunch && _velocity.y <= 0f && _ball.y <= LaunchBallRestY)
        {
            _waitingToLaunch = true;
            _ball = new Vector2((LaunchBarrelLeft + LaunchBarrelRight) * 0.5f, LaunchBallRestY);
            _velocity = Vector2.zero;
            _ballSaver = 2.5f;
            AddScore(100);
            return;
        }

        if (_ball.y < LaunchBarrelBottom + BallRadius)
        {
            _ball.y = LaunchBarrelBottom + BallRadius;
            _velocity.y = Mathf.Abs(_velocity.y) * 0.5f;
        }

        if (_ball.y < LaunchBarrelExitY || _velocity.y <= 0f)
            return;

        // The open gate is deliberately above all right-side targets. The ball enters with a
        // gentle leftward kick, then gravity carries it naturally back through the table.
        _insideLaunchBarrel = false;
        _ball = new Vector2(LaunchBarrelLeft - BallRadius - 3f, LaunchBarrelExitY + 5f);
        _velocity = new Vector2(-170f, Mathf.Clamp(_velocity.y * 0.82f, 260f, 350f));
        AddScore(500);
    }

    private void TryEnterLaunchBarrel()
    {
        if (_insideLaunchBarrel || _waitingToLaunch || _velocity.y >= 0f)
            return;

        float leftWall = LaunchBarrelLeft + BallRadius;
        float rightWall = LaunchBarrelRight - BallRadius;
        if (_ball.x < leftWall || _ball.x > rightWall || _ball.y > LaunchBarrelExitY + 14f)
            return;

        _insideLaunchBarrel = true;
        _ball.x = Mathf.Clamp(_ball.x, leftWall, rightWall);
    }

    private void ResolveBumpers()
    {
        for (int i = 0; i < Bumpers.Length; i++)
        {
            Vector2 offset = _ball - Bumpers[i];
            float distance = offset.magnitude;
            const float radius = 22f;
            if (distance >= radius + BallRadius)
                continue;

            Vector2 normal = distance > 0.001f ? offset / distance : Vector2.up;
            _ball = Bumpers[i] + normal * (radius + BallRadius);
            _velocity = Vector2.Reflect(_velocity, normal) + normal * 58f;
            LimitStopperSpeed();
            if (_bumperCooldowns[i] <= 0f)
            {
                _bumperCooldowns[i] = 0.12f;
                AddScore(500);
            }
        }
    }

    private void ResolveTargets()
    {
        for (int i = 0; i < Targets.Length; i++)
        {
            Vector2 offset = _ball - Targets[i];
            float distance = offset.magnitude;
            const float radius = 10f;
            if (distance >= radius + BallRadius)
                continue;

            Vector2 normal = distance > 0.001f ? offset / distance : Vector2.right;
            _ball = Targets[i] + normal * (radius + BallRadius);
            _velocity = Vector2.Reflect(_velocity, normal) + normal * 34f;
            LimitStopperSpeed();
            if (_targetCooldowns[i] <= 0f)
            {
                _targetCooldowns[i] = 0.28f;
                _targetsLit |= 1 << i;
                AddScore(250);
                if (_targetsLit == 0x3F)
                {
                    _targetsLit = 0;
                    _multiplier = Mathf.Min(8, _multiplier + 1);
                    AddScore(2500);
                }
            }
        }
    }

    private void ResolveSlings()
    {
        ResolveCircle(new Vector2(82f, 138f), 20f, 52f, 120);
        ResolveCircle(new Vector2(238f, 138f), 20f, 52f, 120);

        if (_ball.y > 397f && _ball.x > 84f && _ball.x < 236f)
        {
            _velocity.y = -Mathf.Abs(_velocity.y);
            AddScore(75);
        }
    }

    private void ResolveCircle(Vector2 center, float radius, float kick, int score)
    {
        Vector2 offset = _ball - center;
        float distance = offset.magnitude;
        if (distance >= radius + BallRadius)
            return;

        Vector2 normal = distance > 0.001f ? offset / distance : Vector2.up;
        _ball = center + normal * (radius + BallRadius);
        _velocity = Vector2.Reflect(_velocity, normal) + normal * kick;
        LimitStopperSpeed();
        AddScore(score);
    }

    private void ResolveFlipper(Vector2 pivot, float length, float degrees, bool pressed, bool left)
    {
        float radians = degrees * Mathf.Deg2Rad;
        Vector2 tip = pivot + new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * length;
        Vector2 closest = ClosestPoint(_ball, pivot, tip);
        Vector2 offset = _ball - closest;
        float distance = offset.magnitude;
        const float thickness = 8f;
        if (distance >= BallRadius + thickness)
            return;

        Vector2 normal = distance > 0.001f
            ? offset / distance
            : new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
        _ball = closest + normal * (BallRadius + thickness);
        _velocity = Vector2.Reflect(_velocity, normal);
        float lift = pressed ? 455f : 90f;
        _velocity += normal * 95f + new Vector2(left ? -32f : 32f, lift);
        _velocity = Vector2.ClampMagnitude(_velocity, 640f);
        if (_flipperCooldown <= 0f)
        {
            _flipperCooldown = 0.06f;
            AddScore(10);
        }
    }

    private void ResolveDrainGuard()
    {
        if (_velocity.y >= 0f ||
            _ball.x < DrainGuardLeft - BallRadius ||
            _ball.x > DrainGuardRight + BallRadius ||
            _ball.y > DrainGuardY + BallRadius)
        {
            return;
        }

        _ball.y = DrainGuardY + BallRadius;
        _velocity.y = Mathf.Max(82f, Mathf.Abs(_velocity.y) * 0.76f);
        _velocity.x *= 0.92f;
        LimitStopperSpeed();
        AddScore(25);
    }

    private void LimitStopperSpeed()
    {
        // Stopper hits should return the ball smoothly, never accumulate unlimited kick energy.
        _velocity = Vector2.ClampMagnitude(_velocity, MaxStopperSpeed);
    }

    private void DrainBall()
    {
        _balls--;
        if (_balls <= 0)
        {
            _gameOver = true;
            _waitingToLaunch = false;
            return;
        }
        PrepareBall();
    }

    private void AddScore(int value) => _score += value * _multiplier;

    private static Vector2 ClosestPoint(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared < 0.0001f)
            return start;
        float t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
        return start + segment * t;
    }

    private void DrawFrame()
    {
        if (_pixels == null || _texture == null)
            return;

        Fill(0, 0, Width, Height, new Color32(1, 4, 16, 255));
        Fill(13, 12, Width - 26, Height - 24, new Color32(6, 16, 49, 255));
        DrawRoundedBoard();
        DrawHud();
        DrawTableArt();
        DrawBall();

        _texture.SetPixels32(_pixels);
        _texture.Apply(false, false);
    }

    private void DrawRoundedBoard()
    {
        Fill(Left, Bottom, Right - Left, Top - Bottom, new Color32(12, 28, 82, 255));
        Frame((int)Left, (int)Bottom, (int)(Right - Left), (int)(Top - Bottom), 4, new Color32(40, 226, 255, 255));
        Line(new Vector2(Left + 8f, 155f), new Vector2(78f, 416f), 2, new Color32(100, 63, 255, 255));
        Line(new Vector2(Right - 8f, 155f), new Vector2(242f, 416f), 2, new Color32(100, 63, 255, 255));
        Line(new Vector2(78f, 416f), new Vector2(242f, 416f), 2, new Color32(100, 63, 255, 255));
        Fill(LaunchBarrelLeft, LaunchBarrelBottom, LaunchBarrelRight - LaunchBarrelLeft, LaunchBarrelExitY - LaunchBarrelBottom + 3f, new Color32(7, 30, 68, 255));
        Frame((int)LaunchBarrelLeft, (int)LaunchBarrelBottom, (int)(LaunchBarrelRight - LaunchBarrelLeft), (int)(LaunchBarrelExitY - LaunchBarrelBottom + 3f), 2, new Color32(54, 177, 255, 255));
    }

    private void DrawHud()
    {
        DrawTextCentered("SPACE CADET", 464, 2, new Color32(123, 238, 255, 255));
        DrawText("SCORE " + _score.ToString("0000000"), 28, 438, 1, new Color32(255, 225, 93, 255));
        DrawText("BALL " + _balls, 218, 438, 1, new Color32(205, 228, 255, 255));
        DrawText("X" + _multiplier, 28, 13, 1, new Color32(255, 150, 67, 255));

        if (_waitingToLaunch)
            DrawTextCentered("SPACE LAUNCH", 220, 1, new Color32(255, 230, 105, 255));
        if (_gameOver)
        {
            Fill(49, 208, 222, 48, new Color32(2, 3, 14, 245));
            Frame(49, 208, 222, 48, 2, new Color32(255, 87, 124, 255));
            DrawTextCentered("GAME OVER", 236, 2, new Color32(255, 102, 130, 255));
            DrawTextCentered("SPACE RESTART", 216, 1, new Color32(235, 240, 255, 255));
        }
    }

    private void DrawTableArt()
    {
        DrawPlayfieldScenery();

        for (int i = 0; i < Bumpers.Length; i++)
        {
            Color32 outer = _bumperCooldowns[i] > 0f
                ? new Color32(255, 235, 121, 255)
                : new Color32(254, 75, 146, 255);
            Circle(Bumpers[i], 23, new Color32(28, 15, 62, 255));
            Ring(Bumpers[i], 23, 3, outer);
            Circle(Bumpers[i], 12, new Color32(37, 194, 255, 255));
            Circle(Bumpers[i], 5, new Color32(239, 249, 255, 255));
        }

        for (int i = 0; i < Targets.Length; i++)
        {
            bool lit = (_targetsLit & (1 << i)) != 0;
            Color32 colour = lit ? new Color32(255, 232, 92, 255) : new Color32(69, 115, 213, 255);
            Fill((int)Targets[i].x - 6, (int)Targets[i].y - 11, 12, 22, colour);
            Frame((int)Targets[i].x - 7, (int)Targets[i].y - 12, 14, 24, 2, new Color32(230, 244, 255, 255));
        }

        Circle(new Vector2(82f, 138f), 20, new Color32(210, 53, 111, 255));
        Ring(new Vector2(82f, 138f), 20, 3, new Color32(255, 214, 96, 255));
        Circle(new Vector2(238f, 138f), 20, new Color32(210, 53, 111, 255));
        Ring(new Vector2(238f, 138f), 20, 3, new Color32(255, 214, 96, 255));

        DrawFlipper(new Vector2(96f, 74f), 54f, _leftFlipperAngle, new Color32(255, 105, 76, 255));
        DrawFlipper(new Vector2(224f, 74f), 54f, _rightFlipperAngle, new Color32(255, 105, 76, 255));
        Circle(new Vector2(96f, 74f), 10, new Color32(234, 238, 248, 255));
        Circle(new Vector2(224f, 74f), 10, new Color32(234, 238, 248, 255));

        // Visible short guard rail at the lower centre. The open left/right lanes still drain,
        // but a normal central return is deflected back toward the flippers.
        Line(new Vector2(DrainGuardLeft, DrainGuardY), new Vector2(DrainGuardRight, DrainGuardY), 8, new Color32(20, 34, 76, 255));
        Line(new Vector2(DrainGuardLeft, DrainGuardY), new Vector2(DrainGuardRight, DrainGuardY), 3, new Color32(92, 220, 255, 255));
        Circle(new Vector2(DrainGuardLeft, DrainGuardY), 5, new Color32(255, 218, 96, 255));
        Circle(new Vector2(DrainGuardRight, DrainGuardY), 5, new Color32(255, 218, 96, 255));

        DrawLaunchBarrel();
    }

    private void DrawPlayfieldScenery()
    {
        // A deterministic star field keeps the table richly textured without shimmering from
        // frame to frame. Gameplay components are drawn after it and stay perfectly readable.
        for (int i = 0; i < 58; i++)
        {
            int x = 34 + (i * 47 % 226);
            int y = 118 + (i * 71 % 296);
            int size = i % 11 == 0 ? 3 : i % 4 == 0 ? 2 : 1;
            Color32 colour = i % 3 == 0
                ? new Color32(106, 104, 255, 255)
                : new Color32(122, 227, 255, 255);
            Fill(x, y, size, size, colour);
        }

        // Layered orbital guides, a planet, and banks of lamps make the playfield feel like a
        // proper arcade table rather than a bare collision board.
        Circle(new Vector2(160f, 397f), 25, new Color32(20, 45, 111, 255));
        Ring(new Vector2(160f, 397f), 25, 3, new Color32(82, 221, 255, 255));
        Ring(new Vector2(160f, 397f), 17, 2, new Color32(245, 92, 181, 255));
        Circle(new Vector2(153f, 405f), 7, new Color32(92, 157, 249, 255));
        Circle(new Vector2(166f, 390f), 4, new Color32(255, 220, 96, 255));
        Line(new Vector2(43f, 204f), new Vector2(111f, 231f), 4, new Color32(22, 69, 137, 255));
        Line(new Vector2(277f, 204f), new Vector2(209f, 231f), 4, new Color32(22, 69, 137, 255));
        Line(new Vector2(43f, 204f), new Vector2(111f, 231f), 1, new Color32(80, 235, 255, 255));
        Line(new Vector2(277f, 204f), new Vector2(209f, 231f), 1, new Color32(80, 235, 255, 255));

        for (int i = 0; i < 7; i++)
        {
            float y = 172f + i * 32f;
            Circle(new Vector2(43f, y), 4, i % 2 == 0 ? new Color32(255, 93, 175, 255) : new Color32(86, 231, 255, 255));
            Circle(new Vector2(277f, y), 4, i % 2 == 0 ? new Color32(86, 231, 255, 255) : new Color32(255, 93, 175, 255));
        }

        DrawTextCentered("ORBIT", 221, 1, new Color32(163, 224, 255, 255));
        DrawTextCentered("STAR GATE", 407, 1, new Color32(255, 226, 96, 255));
    }

    private void DrawLaunchBarrel()
    {
        float centerX = (LaunchBarrelLeft + LaunchBarrelRight) * 0.5f;
        Line(new Vector2(LaunchBarrelLeft + 4f, LaunchBarrelBottom + 8f), new Vector2(LaunchBarrelLeft + 4f, LaunchBarrelExitY - 5f), 2, new Color32(126, 236, 255, 255));
        Line(new Vector2(LaunchBarrelRight - 4f, LaunchBarrelBottom + 8f), new Vector2(LaunchBarrelRight - 4f, LaunchBarrelExitY - 5f), 2, new Color32(126, 236, 255, 255));

        for (int i = 0; i < 9; i++)
        {
            float y = 73f + i * 34f;
            Circle(new Vector2(centerX, y), 3, i % 2 == 0 ? new Color32(255, 218, 88, 255) : new Color32(255, 79, 153, 255));
        }

        // The sloped exit is visualised as a one-way gate feeding left into the active table.
        Line(new Vector2(LaunchBarrelLeft, LaunchBarrelExitY), new Vector2(LaunchBarrelLeft - 19f, LaunchBarrelExitY + 15f), 4, new Color32(25, 62, 125, 255));
        Line(new Vector2(LaunchBarrelLeft, LaunchBarrelExitY), new Vector2(LaunchBarrelLeft - 19f, LaunchBarrelExitY + 15f), 2, new Color32(255, 222, 97, 255));
        DrawText("FIRE", (int)LaunchBarrelLeft + 1, 46, 1, new Color32(230, 246, 255, 255));
    }

    private void DrawBall()
    {
        Circle(_ball + new Vector2(2f, -2f), 7, new Color32(1, 2, 8, 180));
        Circle(_ball, 6, new Color32(224, 246, 255, 255));
        Circle(_ball + new Vector2(-2f, 2f), 2, new Color32(255, 255, 255, 255));
    }

    private void DrawFlipper(Vector2 pivot, float length, float degrees, Color32 colour)
    {
        float radians = degrees * Mathf.Deg2Rad;
        Vector2 tip = pivot + new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * length;
        Line(pivot, tip, 13, new Color32(38, 17, 39, 255));
        Line(pivot, tip, 9, colour);
        Line(pivot, tip, 2, new Color32(255, 230, 170, 255));
    }

    private void Fill(float x, float y, float width, float height, Color32 colour)
        => Fill((int)x, (int)y, (int)width, (int)height, colour);

    private void Fill(int x, int y, int width, int height, Color32 colour)
    {
        if (_pixels == null)
            return;
        int minX = Mathf.Clamp(x, 0, Width);
        int maxX = Mathf.Clamp(x + width, 0, Width);
        int minY = Mathf.Clamp(y, 0, Height);
        int maxY = Mathf.Clamp(y + height, 0, Height);
        for (int py = minY; py < maxY; py++)
            for (int px = minX; px < maxX; px++)
                _pixels[py * Width + px] = colour;
    }

    private void Frame(int x, int y, int width, int height, int thickness, Color32 colour)
    {
        Fill(x, y, width, thickness, colour);
        Fill(x, y + height - thickness, width, thickness, colour);
        Fill(x, y, thickness, height, colour);
        Fill(x + width - thickness, y, thickness, height, colour);
    }

    private void Circle(Vector2 center, int radius, Color32 colour)
    {
        if (_pixels == null)
            return;
        int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
        int maxX = Mathf.Min(Width - 1, Mathf.CeilToInt(center.x + radius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
        int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(center.y + radius));
        int radiusSquared = radius * radius;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                if ((x - center.x) * (x - center.x) + (y - center.y) * (y - center.y) <= radiusSquared)
                    _pixels[y * Width + x] = colour;
    }

    private void Ring(Vector2 center, int radius, int thickness, Color32 colour)
    {
        if (_pixels == null)
            return;
        int outerSquared = radius * radius;
        int innerRadius = Mathf.Max(0, radius - thickness);
        int innerSquared = innerRadius * innerRadius;
        int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
        int maxX = Mathf.Min(Width - 1, Mathf.CeilToInt(center.x + radius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
        int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(center.y + radius));
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float distanceSquared = (x - center.x) * (x - center.x) + (y - center.y) * (y - center.y);
                if (distanceSquared <= outerSquared && distanceSquared >= innerSquared)
                    _pixels[y * Width + x] = colour;
            }
    }

    private void Line(Vector2 start, Vector2 end, int thickness, Color32 colour)
    {
        float distance = Vector2.Distance(start, end);
        int steps = Mathf.Max(1, Mathf.CeilToInt(distance));
        float radius = thickness * 0.5f;
        for (int i = 0; i <= steps; i++)
            Circle(Vector2.Lerp(start, end, i / (float)steps), Mathf.CeilToInt(radius), colour);
    }

    private void DrawTextCentered(string text, int y, int scale, Color32 colour)
        => DrawText(text, (Width - TextWidth(text, scale)) / 2, y, scale, colour);

    private static int TextWidth(string text, int scale) => text.Length * 6 * scale - scale;

    private void DrawText(string text, int x, int y, int scale, Color32 colour)
    {
        foreach (char raw in text)
        {
            string glyph = Glyph(char.ToUpperInvariant(raw));
            for (int row = 0; row < 7; row++)
                for (int column = 0; column < 5; column++)
                    if (glyph[row * 5 + column] == '1')
                        Fill(x + column * scale, y + (6 - row) * scale, scale, scale, colour);
            x += 6 * scale;
        }
    }

    private static string Glyph(char character) => character switch
    {
        'A' => "01110100011000111111100011000110001", 'B' => "11110100011000111110100011000111110",
        'C' => "01111100001000010000100001000001111", 'D' => "11110100011000110001100011000111110",
        'E' => "11111100001111010000100001000011111", 'F' => "11111100001111010000100001000010000",
        'G' => "01111100001000010111100011000101111", 'H' => "10001100011000111111100011000110001",
        'I' => "11111001000010000100001000010011111", 'J' => "00111000100001000010000101001001100",
        'K' => "10001100101010011000101001001010001", 'L' => "10000100001000010000100001000011111",
        'M' => "10001110111010110101100011000110001", 'N' => "10001110011010110101100111000110001",
        'O' => "01110100011000110001100011000101110", 'P' => "11110100011000111110100001000010000",
        'Q' => "01110100011000110001101011001001101", 'R' => "11110100011000111110101001001010001",
        'S' => "01111100000111000001000011111011110", 'T' => "11111001000010000100001000010000100",
        'U' => "10001100011000110001100011000101110", 'V' => "10001100011000110001100010101000100",
        'W' => "10001100011000110101101011010101010", 'X' => "10001010100010000100001000101010001",
        'Y' => "10001100010101000100001000010000100", 'Z' => "11111000010001000100010001000011111",
        '0' => "01110100011001110101110011000101110", '1' => "00100011000010000100001000010001110",
        '2' => "01110100010000100010001000100011111", '3' => "11110000010000101110000010000111110",
        '4' => "00010001100101010010111110001000010", '5' => "11111100001111000001000011000101110",
        '6' => "00110010001000011110100011000101110", '7' => "11111000010001000100010000100001000",
        '8' => "01110100011000101110100011000101110", '9' => "01110100011000101111000010000101100",
        '/' => "00001000100010000100010001000010000", ' ' => "00000000000000000000000000000000000",
        _ => "00000000000000000000000000000000000"
    };
}

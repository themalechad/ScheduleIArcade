using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

// Smoke tests for the embedded Doom engine artifact. Uses only reflection on the built DLL, the
// same contract the mod's EmbeddedDoomBridge uses.
string root = FindRepoRoot();
string enginePath = args.Length > 0 ? args[0] : Path.Combine(root, "Build", "Release", "Doom", "ScheduleIArcade.Doom.dll");
string wadDir = args.Length > 1 ? args[1] : Path.Combine(root, "Build", "third_party", "freedoom-0.13.0");
string outDir = args.Length > 2 ? args[2] : Path.Combine(root, "Build", "doom-smoke");
Directory.CreateDirectory(outDir);

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
    if (!ok) failures++;
}

Console.WriteLine($"engine: {enginePath}");
Check(File.Exists(enginePath), "engine DLL exists");
Assembly asm = Assembly.LoadFrom(enginePath);
Type? runtimeType = asm.GetType("ScheduleIArcade.Doom.EmbeddedDoomRuntime");
Check(runtimeType != null, "EmbeddedDoomRuntime type is exported");
int api = (int)runtimeType!.GetField("ApiVersion")!.GetValue(null)!;
Check(api == 1, $"engine ApiVersion == 1 (got {api})");

// Contract: every member the MOD's bridge binds by name (parsed from its real source file) must
// exist on the built engine, so the reflection wiring cannot silently drift.
string bridgeSource = File.ReadAllText(Path.Combine(root, "src", "ScheduleIArcade", "Services", "EmbeddedDoomBridge.cs"));
var bound = System.Text.RegularExpressions.Regex.Matches(bridgeSource, @"t\.Get(Property|Method)\(""(\w+)""\)")
    .Select(m => (kind: m.Groups[1].Value, name: m.Groups[2].Value)).ToList();
foreach (var (kind, name) in bound)
{
    bool exists = kind == "Property" ? runtimeType.GetProperty(name) != null : runtimeType.GetMethod(name) != null;
    Check(exists, $"mod bridge binds {kind} '{name}' and the engine exports it");
}
Check(bound.Count >= 10, $"bridge binding list parsed ({bound.Count} members)");
var ctor = runtimeType.GetConstructor(new[] { typeof(string), typeof(string) });
Check(ctor != null, "engine constructor (iwadPath, dataDirectory) matches the mod's Activator call");

foreach (string wad in new[] { "freedoom1.wad", "freedoom2.wad" })
{
    Console.WriteLine($"\n== {wad}");
    string wadPath = Path.Combine(wadDir, wad);
    string saveDir = Path.Combine(outDir, "data-" + Path.GetFileNameWithoutExtension(wad));
    var sw = Stopwatch.StartNew();
    var doom = new Doom(Activator.CreateInstance(runtimeType, wadPath, saveDir)!);
    Console.WriteLine($"      content load {sw.ElapsedMilliseconds} ms, frame {doom.Width}x{doom.Height}");
    Check(doom.Width == 640 && doom.Height == 400, "renders 640x400 frames");

    var frame = new byte[doom.Width * doom.Height * 4];

    // 1. Attract mode: title + demo playback must produce changing, non-blank frames.
    var hashes = new HashSet<ulong>();
    int distinctColorsMax = 0;
    // Advance is called at 35 Hz like the game loop (it caps catch-up at 6 tics per call). The
    // title picture is static for ~5 s; Doom II-style titles hold ~11 s; the demo that follows must show moving gameplay.
    bool rendered = true;
    for (int i = 0; i < 10; i++)
    {
        for (int t = 0; t < 70; t++) doom.Advance(1.0 / 35);
        rendered &= doom.RenderInto(frame);
        hashes.Add(Hash(frame));
        distinctColorsMax = Math.Max(distinctColorsMax, DistinctColors(frame));
    }
    Check(rendered, $"20 s of attract mode rendered every sample (state {doom.State})");
    Check(hashes.Count >= 4, $"attract-mode frames change over time ({hashes.Count}/10 distinct samples)");
    Check(distinctColorsMax > 32, $"frames are real imagery, not blank ({distinctColorsMax} colours)");
    Check(frame[3] == 255, "alpha is opaque");
    WritePng(Path.Combine(outDir, Path.GetFileNameWithoutExtension(wad) + "-attract.png"), frame, doom.Width, doom.Height);

    // 2. Input: open the menu and start a new game with keyboard events only.
    Tap(doom, "Escape");
    for (int i = 0; i < 4 && doom.State != "Game"; i++)
        Tap(doom, "Enter");
    for (int i = 0; i < 70 && doom.State != "Game"; i++)
        doom.Advance(1.0 / 35);
    doom.Advance(1.5); // finish the screen wipe
    Check(doom.State == "Game", $"Escape/Enter menu navigation starts a game (state {doom.State})");

    Check(doom.TryGetPlayer(out float x0, out float y0, out double a0), "player exists in the level");
    doom.SetKey("Up", true);
    for (int i = 0; i < 40; i++) doom.Advance(1.0 / 35);
    doom.SetKey("Up", false);
    doom.TryGetPlayer(out float x1, out float y1, out _);
    double moved = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
    Check(moved > 64, $"holding Up moves the player ({moved:0} map units)");

    doom.SetKey("Left", true);
    for (int i = 0; i < 20; i++) doom.Advance(1.0 / 35);
    doom.SetKey("Left", false);
    doom.TryGetPlayer(out _, out _, out double a1);
    Check(Math.Abs(a1 - a0) > 0.2, $"holding Left turns the player ({a0:0.00} -> {a1:0.00} rad)");

    doom.DrainSounds();
    doom.SetKey("LControl", true);
    for (int i = 0; i < 12; i++) doom.Advance(1.0 / 35);
    doom.SetKey("LControl", false);
    var sounds = doom.DrainSounds();
    Check(sounds.Count > 0, $"firing raises sound events ({sounds.Count}: {string.Join(",", sounds.Take(4).Select(s => $"sfx{s.sfx} g{s.gain:0.00}"))})");
    if (sounds.Count > 0)
    {
        float[]? pcm = doom.GetSamples(sounds[0].sfx, out int rate);
        Check(pcm != null && pcm.Length > 100 && rate > 0, $"sound effect PCM decoded ({pcm?.Length ?? 0} samples @ {rate} Hz)");
    }

    doom.RenderInto(frame);
    WritePng(Path.Combine(outDir, Path.GetFileNameWithoutExtension(wad) + "-ingame.png"), frame, doom.Width, doom.Height);

    // 3. Cost per tic + render (the mod runs this on Unity's main thread).
    sw.Restart();
    int tics = 0;
    for (int i = 0; i < 70; i++)
    {
        tics += doom.Advance(1.0 / 35);
        doom.RenderInto(frame);
    }
    double perFrame = sw.Elapsed.TotalMilliseconds / 70;
    Console.WriteLine($"      {perFrame:0.00} ms per tic+render+convert ({tics} tics)");
    Check(perFrame < 12, "fast enough for the main thread (< 12 ms per 35 Hz frame)");

    // Every Doom key name in the mod's cabinet key map (parsed from its source) must be accepted.
    string gameSource = File.ReadAllText(Path.Combine(root, "src", "ScheduleIArcade", "Games", "EmbeddedDoomArcadeGame.cs"));
    var mapped = System.Text.RegularExpressions.Regex.Matches(gameSource, @"\(KeyCode\.\w+, ""(\w+)""\)")
        .Select(m => m.Groups[1].Value).Distinct().ToList();
    var rejected = mapped.Where(k => !doom.SetKey(k, false)).ToList();
    Check(mapped.Count >= 20 && rejected.Count == 0, $"all {mapped.Count} cabinet key-map names are valid Doom keys (rejected: {string.Join(",", rejected)})");

    Check(doom.IsRunning && doom.LastError.Length == 0, $"engine still healthy (error='{doom.LastError}')");
    doom.Dispose();
    Check(doom.Advance(1.0) == 0 && !doom.IsRunning, "dispose stops the session");
}

Console.WriteLine(failures == 0 ? "\nDOOM SMOKE TESTS PASSED" : $"\nDOOM SMOKE TESTS FAILED ({failures})");
return failures == 0 ? 0 : 1;

static void Tap(Doom doom, string key)
{
    doom.SetKey(key, true);
    for (int i = 0; i < 3; i++) doom.Advance(1.0 / 35);
    doom.SetKey(key, false);
    for (int i = 0; i < 6; i++) doom.Advance(1.0 / 35);
}

static ulong Hash(byte[] data)
{
    ulong h = 1469598103934665603UL;
    for (int i = 0; i < data.Length; i += 7) { h ^= data[i]; h *= 1099511628211UL; }
    return h;
}

static int DistinctColors(byte[] rgba)
{
    var set = new HashSet<int>();
    for (int i = 0; i < rgba.Length; i += 4 * 13) set.Add(rgba[i] | rgba[i + 1] << 8 | rgba[i + 2] << 16);
    return set.Count;
}

static void WritePng(string path, byte[] bottomUpRgba, int w, int h)
{
    using var raw = new MemoryStream();
    for (int y = h - 1; y >= 0; y--)
    {
        raw.WriteByte(0);
        raw.Write(bottomUpRgba, y * w * 4, w * 4);
    }
    using var z = new MemoryStream();
    using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) raw.WriteTo(zs);
    using var fs = File.Create(path);
    fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    void Chunk(string type, byte[] data)
    {
        var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); fs.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); fs.Write(t); fs.Write(data);
        uint crc = Crc(t.Concat(data).ToArray()); var c = BitConverter.GetBytes(crc); Array.Reverse(c); fs.Write(c);
    }
    var ihdr = new byte[13];
    var wb = BitConverter.GetBytes(w); Array.Reverse(wb); Array.Copy(wb, 0, ihdr, 0, 4);
    var hb = BitConverter.GetBytes(h); Array.Reverse(hb); Array.Copy(hb, 0, ihdr, 4, 4);
    ihdr[8] = 8; ihdr[9] = 6;
    Chunk("IHDR", ihdr);
    Chunk("IDAT", z.ToArray());
    Chunk("IEND", Array.Empty<byte>());
}

static uint Crc(byte[] data)
{
    uint c = 0xFFFFFFFF;
    foreach (byte b in data)
    {
        c ^= b;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
    }
    return ~c;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ScheduleIArcade.Doom"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

/// <summary>Reflection wrapper with the same member names the mod's bridge binds to.</summary>
sealed class Doom
{
    private readonly object _o;
    private readonly Type _t;
    public Doom(object o) { _o = o; _t = o.GetType(); }
    T Get<T>(string name) => (T)_t.GetProperty(name)!.GetValue(_o)!;
    public int Width => Get<int>("Width");
    public int Height => Get<int>("Height");
    public bool IsRunning => Get<bool>("IsRunning");
    public string LastError => Get<string>("LastError");
    public string State => Get<string>("State");
    public int Advance(double s) => (int)_t.GetMethod("Advance")!.Invoke(_o, new object[] { s })!;
    public bool RenderInto(byte[] f) => (bool)_t.GetMethod("RenderInto")!.Invoke(_o, new object[] { f })!;
    public bool SetKey(string k, bool d) => (bool)_t.GetMethod("SetKey")!.Invoke(_o, new object[] { k, d })!;
    public bool TryGetPlayer(out float x, out float y, out double a)
    {
        var p = new object?[] { 0f, 0f, 0.0 };
        bool ok = (bool)_t.GetMethod("TryGetPlayerPosition")!.Invoke(_o, p)!;
        x = (float)p[0]!; y = (float)p[1]!; a = (double)p[2]!;
        return ok;
    }
    public List<(int sfx, float gain, float pan)> DrainSounds()
    {
        var list = new List<(int, float, float)>();
        var m = _t.GetMethod("TryDequeueSound")!;
        while (true)
        {
            var p = new object?[] { 0, 0f, 0f };
            if (!(bool)m.Invoke(_o, p)!) break;
            list.Add(((int)p[0]!, (float)p[1]!, (float)p[2]!));
        }
        return list;
    }
    public float[]? GetSamples(int sfx, out int rate)
    {
        var p = new object?[] { sfx, 0 };
        var r = (float[]?)_t.GetMethod("GetSfxSamples")!.Invoke(_o, p);
        rate = (int)p[1]!;
        return r;
    }
    public void Dispose() => ((IDisposable)_o).Dispose();
}

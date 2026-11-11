// 0.6.0 geometry check against the installed game's level1 (read-only).
//
//   dotnet run -c Release -- "D:\SteamLibrary\steamapps\common\Schedule I"
//
// 1. Parses Map/Hyland Point/Region_Northtown/Arcade (1) from level1 (Unity 2022.3 binary layout,
//    no type trees) and re-measures the building (outer wall colliders, foundation, first floor, roof,
//    corner door and its glass leaves).
// 2. Builds the arcade shell exactly as the mod does (ArcadeLayout.WallPieces / OuterOutline) and the
//    walk-in changes (corner-door seal -> stubs + lintel, cloned leaves swinging away from the player).
// 3. Checks: the interior fills the building (flush within the wall thickness, no walkable cavity, no
//    leftover rooms), single ground floor, closed opaque shell, ONE door set, no vestibule, level
//    threshold, nothing in the door swing, 1.0 m walkways from the door to every machine, doorway clear,
//    ceiling inside the roof; and that the game's own baked NPC navmesh reaches the door so the
//    runtime door link has something to connect to (RoomChecks.cs).
using System.Numerics;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using ScheduleIArcade.Utils;

string gameDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : @"D:\SteamLibrary\steamapps\common\Schedule I";
string level1 = Path.Combine(gameDir, "Schedule I_Data", "level1");
if (!File.Exists(level1))
{
    Console.WriteLine($"level1 not found under {gameDir}");
    return 2;
}

int failures = 0;
void Fail(string message) { failures++; Console.WriteLine("  FAIL " + message); }
var scene = Scene.Load(level1);
long buildingGo = scene.FindByPath("Map/Hyland Point/Region_Northtown/Arcade (1)");
if (buildingGo == 0)
{
    Console.WriteLine("FAIL Arcade (1) not found in level1");
    return 1;
}
if (args.Contains("--dump")) { scene.Dump(scene.TransformOf(buildingGo), 0); return 0; }
long cornerDoor = scene.FindChild(buildingGo, "arcade/CornerDoor");
Console.WriteLine($"Arcade (1) at {V(scene.World(scene.TransformOf(buildingGo)).Translation)}; CornerDoor found={cornerDoor != 0}");

// ---- Room placement (ArcadeLayout, shared with the mod) ------------------------------------------
var roomOrigin = new Vector3(ArcadeLayout.WorldOriginX, ArcadeLayout.WorldOriginY, ArcadeLayout.WorldOriginZ);
var roomRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, ArcadeLayout.RoomYaw * MathF.PI / 180f);
Vector3 RoomToWorld(Vector3 local) => roomOrigin + Vector3.Transform(local, roomRot);
Box RoomBox(string name, Vector3 localCenter, Vector3 size, Quaternion localRot)
    => Box.Oriented(name, RoomToWorld(localCenter), roomRot * localRot, size);

const float foot = ArcadeLayout.FloorWorldY;
const float bandMin = foot + 0.25f, bandMax = foot + 1.85f; // standing capsule body above step height
var nearBoxes = scene.SolidBoxesNear(new Vector3(-48f, 0f, 141.5f), 22f).ToList();
var buildingBoxes = nearBoxes.Where(b => b.MaxY > bandMin && b.MinY < bandMax).ToList();
Console.WriteLine($"Solid box colliders near the Arcade intersecting the player band: {buildingBoxes.Count}");

// ---- Building door measurements -----------------------------------------------------------------
var leaves = scene.SolidBoxesUnder(cornerDoor).Where(b => b.Path.Contains("Glass Door")).ToList();
var seal = scene.SolidBoxesUnder(cornerDoor).First(b => b.Path.EndsWith("arcade/CornerDoor"));
var doorway = Aabb.Of(leaves.SelectMany(l => l.Corners()));
Vector3 buildingDoorCenter = new(doorway.Center.X, doorway.Min.Y, doorway.Center.Z);
Vector3 along = seal.HorizontalLongAxis();
Vector3 buildingCentre = scene.World(scene.TransformOf(buildingGo)).Translation;
Vector3 inward = Flat(buildingCentre - buildingDoorCenter);
inward = Vector3.Normalize(Flat(inward - along * Vector3.Dot(inward, along)));
float doorwayHalf = doorway.HalfExtentAlong(along, buildingDoorCenter);
float doorwayHeight = doorway.Max.Y - doorway.Min.Y;
Console.WriteLine($"Building glass doorway centre {V(buildingDoorCenter)} clear width {doorwayHalf * 2:0.00} m height {doorwayHeight:0.00} m, inward {V(inward)}");
{
    Vector3 layoutDoor = RoomToWorld(new Vector3(ArcadeLayout.DoorX, ArcadeLayout.FloorTop, ArcadeLayout.DoorZ));
    float off = Vector3.Distance(Flat(layoutDoor), Flat(buildingDoorCenter));
    Console.WriteLine($"  ArcadeLayout door centre {V(layoutDoor)} is {off * 1000:0} mm from the measured doorway");
    if (off > 0.02f) Fail("ArcadeLayout.DoorX/DoorZ do not match the measured doorway");
}

var hinges = leaves.Select(l => l.World.Translation).ToList();
var leafCentres = leaves.Select(l => l.WorldCenter).ToList();
{
    float frameHalf = hinges.Max(h => MathF.Abs(Vector3.Dot(Flat(h - buildingDoorCenter), along)));
    var freeEdges = new List<float>();
    for (int i = 0; i < leaves.Count; i++)
    {
        Vector3 arm = Flat(leafCentres[i] - hinges[i]);
        freeEdges.Add(Vector3.Dot(Flat(hinges[i] + arm * 2f - buildingDoorCenter), along));
    }
    float centreGap = MathF.Abs(freeEdges[0] - freeEdges[1]);
    Console.WriteLine($"Closed leaves: hinge edges at +-{frameHalf:0.000} m; free edges meet with a {centreGap * 1000:0} mm gap (35 mm centre stop); hinge stiles reach +-{frameHalf + 0.035f:0.000} m, lining opening +-{ArcadeLayout.DoorLiningHalf:0.000} m.");
    if (centreGap > 0.035f) Fail("centre gap wider than the centre stop");
    if (MathF.Abs(frameHalf - ArcadeLayout.LeafHalfSpan) > 0.01f) Fail("leaf hinge positions differ from ArcadeLayout.LeafHalfSpan");
    if (frameHalf + 0.035f >= ArcadeLayout.DoorLiningHalf) Fail("hinge stiles would hit the wall lining");
}

// ---- Shell (exactly the mod's pieces) -----------------------------------------------------------
var pieces = ArcadeLayout.WallPieces();
var shellBoxes = new List<Box>();
foreach (WallPiece piece in pieces)
{
    var (cx, cy, cz) = piece.Centre;
    var (sx, sy, sz) = piece.Size;
    var (tx, tz) = piece.Wall.Tangent;
    var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(tx, tz));
    shellBoxes.Add(RoomBox("lining " + piece.Name, new Vector3(cx, cy, cz), new Vector3(sx, sy, sz), rot));
}
var machineBoxes = ArcadeLayout.FloorItems()
    .Select(f => RoomBox("item " + f.Id, new Vector3(f.X, ArcadeLayout.FloorTop + 0.5f, f.Z), new Vector3(f.SizeX, 1.0f, f.SizeZ), Quaternion.Identity)).ToList();

// Walk-in: the corner-door seal is split around the doorway; the vanilla leaves are replaced by clones.
var walkBase = buildingBoxes.Where(b => !ReferenceEquals(b.Source, seal.Source) && !leaves.Any(l => ReferenceEquals(l.Source, b.Source))).ToList();
walkBase.AddRange(seal.SplitAroundOpening(doorway).Where(b => b.MaxY > bandMin && b.MinY < bandMax));
walkBase.AddRange(shellBoxes.Where(b => b.MaxY > bandMin && b.MinY < bandMax));
walkBase.AddRange(machineBoxes);

Box LeafAt(int i, float degreesToward, Vector3 side) => leaves[i].RotatedAbout(hinges[i], ChooseSign(hinges[i], leafCentres[i], side, degreesToward), leaves[i].Name + $" ({degreesToward:0} deg)");
var walkIn = new List<Box>(walkBase);
var walkOut = new List<Box>(walkBase);
for (int i = 0; i < leaves.Count; i++)
{
    walkIn.Add(LeafAt(i, 88f, inward));
    walkOut.Add(LeafAt(i, 88f, -inward));
}

Vector3 street = buildingDoorCenter - inward * 2.5f;
Vector3 justInside = buildingDoorCenter + inward * 0.9f;
Vector3 insideRoom = RoomToWorld(new Vector3(0f, 0f, 0f));
if (args.Contains("--debugpath"))
{
    for (float t = 0f; t <= 1.0001f; t += 0.05f)
    {
        Vector3 q = Vector3.Lerp(street, insideRoom, t);
        var near = walkIn.Select(b => (b.Name, d: b.Distance2D(new Vector2(q.X, q.Z)))).Where(x => x.d < 0.35f).OrderBy(x => x.d).Take(3);
        Console.WriteLine($"  {V(q)}: " + string.Join("; ", near.Select(n => $"{n.Name} {n.d:0.00}")));
    }
}
Console.WriteLine($"Path query: street {V(street)} -> just inside {V(justInside)} -> room centre {V(insideRoom)}");
foreach (float radius in new[] { 0.30f, 0.40f })
{
    var vanilla = Grid.FindPath(buildingBoxes, street, justInside, radius);
    var enter = Grid.FindPath(walkIn, street, insideRoom, radius);
    var leave = Grid.FindPath(walkOut, insideRoom, street, radius);
    var step = Grid.FindPath(walkIn, street, justInside, radius);
    Console.WriteLine($"  capsule r={radius:0.00}  vanilla: {(vanilla.Found ? "PATH" : "BLOCKED")} | enter: {(enter.Found ? "PATH" : "BLOCKED")} {enter.Length:0.0} m | leave: {(leave.Found ? "PATH" : "BLOCKED")} | street->just inside {step.Length:0.00} m (straight {Vector3.Distance(Flat(street), Flat(justInside)):0.00} m)");
    if (vanilla.Found) Fail("vanilla building should be sealed");
    if (!enter.Found) Fail("cannot walk from the street into the arcade " + enter.Note);
    if (!leave.Found) Fail("cannot walk out of the arcade " + leave.Note);
    // No vestibule / tunnel: one step through the door is already the arcade floor.
    if (!step.Found || step.Length > Vector3.Distance(Flat(street), Flat(justInside)) * 1.6f) Fail("the way in is not straight through the door");
}

failures += RoomChecks.Run(scene, buildingGo, nearBoxes, roomOrigin, roomRot, shellBoxes, machineBoxes, leaves, hinges, leafCentres, buildingDoorCenter, inward, along, doorwayHeight, level1);
Console.WriteLine(failures == 0 ? "\nGEOMETRY CHECK PASSED" : $"\nGEOMETRY CHECK FAILED ({failures})");
return failures == 0 ? 0 : 1;

static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
static string V(Vector3 v) => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})";
static Quaternion LookRotation(Vector3 forward)
{
    // Unity LookRotation(forward, up) for a horizontal forward == AngleAxis(atan2(x, z), up).
    float yaw = MathF.Atan2(forward.X, forward.Z);
    return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
}
static float ChooseSign(Vector3 hinge, Vector3 leafCenter, Vector3 swingSide, float degrees)
{
    Vector3 arm = Flat(leafCenter - hinge);
    Vector3 positive = Vector3.Transform(arm, Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f));
    return Vector3.Dot(positive, swingSide) >= 0f ? degrees : -degrees;
}

internal sealed record Box(string Name, string Path, object Source, Matrix4x4 World, Vector3 Center, Vector3 Size)
{
    public static Box Oriented(string name, Vector3 position, Quaternion rotation, Vector3 size)
        => new(name, name, new object(), Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position), Vector3.Zero, size);

    public IEnumerable<Vector3> Corners()
    {
        for (int i = 0; i < 8; i++)
        {
            var local = Center + new Vector3(((i & 1) == 0 ? -0.5f : 0.5f) * Size.X, ((i & 2) == 0 ? -0.5f : 0.5f) * Size.Y, ((i & 4) == 0 ? -0.5f : 0.5f) * Size.Z);
            yield return Vector3.Transform(local, World);
        }
    }

    public Vector3 WorldCenter => Vector3.Transform(Center, World);

    /// <summary>True when a world point is inside this (oriented) box.</summary>
    public bool Contains(Vector3 p)
    {
        Matrix4x4.Invert(World, out Matrix4x4 inv);
        Vector3 l = Vector3.Transform(p, inv) - Center;
        return MathF.Abs(l.X) <= Size.X * 0.5f && MathF.Abs(l.Y) <= Size.Y * 0.5f && MathF.Abs(l.Z) <= Size.Z * 0.5f;
    }

    /// <summary>World sample points through the box volume, inset from every face (5 cm grid).</summary>
    public IEnumerable<Vector3> InteriorSamples(float inset)
    {
        int Steps(float size) => Math.Max(2, (int)(size / 0.05f));
        int nx = Steps(Size.X), ny = Steps(Size.Y), nz = Steps(Size.Z);
        Vector3 half = Vector3.Max(Size * 0.5f - new Vector3(inset), Vector3.Zero);
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++)
                for (int k = 0; k <= nz; k++)
                {
                    var local = Center + new Vector3(-half.X + 2 * half.X * i / nx, -half.Y + 2 * half.Y * j / ny, -half.Z + 2 * half.Z * k / nz);
                    yield return Vector3.Transform(local, World);
                }
    }
    Vector3 Axis(int i) => Vector3.TransformNormal(i == 0 ? Vector3.UnitX : i == 1 ? Vector3.UnitY : Vector3.UnitZ, World);
    public float MinY => Corners().Min(c => c.Y);
    public float MaxY => Corners().Max(c => c.Y);

    public Vector3 HorizontalLongAxis()
    {
        Vector3 best = Vector3.UnitX; float bestLen = -1;
        for (int i = 0; i < 3; i++)
        {
            Vector3 a = Axis(i);
            float scale = a.Length();
            Vector3 n = a / scale;
            if (MathF.Abs(n.Y) > 0.5f) continue;
            float len = (i == 0 ? Size.X : i == 1 ? Size.Y : Size.Z) * scale;
            if (len > bestLen) { bestLen = len; best = Vector3.Normalize(new Vector3(n.X, 0, n.Z)); }
        }
        return best;
    }

    int AxisIndex(Vector3 dir)
    {
        int best = 0; float bestDot = -1;
        for (int i = 0; i < 3; i++)
        {
            float d = MathF.Abs(Vector3.Dot(Vector3.Normalize(Axis(i)), Vector3.Normalize(dir)));
            if (d > bestDot) { bestDot = d; best = i; }
        }
        return best;
    }

    /// <summary>Same algorithm as ArcadeWalkInService.OpenCornerDoorCollider.</summary>
    public IEnumerable<Box> SplitAroundOpening(Aabb opening)
    {
        Matrix4x4.Invert(World, out Matrix4x4 inv);
        int alongAxis = AxisIndex(HorizontalLongAxis());
        int upAxis = AxisIndex(Vector3.UnitY);
        float openMin = float.MaxValue, openMax = float.MinValue, openTop = float.MinValue;
        foreach (Vector3 corner in opening.Corners())
        {
            Vector3 l = Vector3.Transform(corner, inv);
            openMin = MathF.Min(openMin, Get(l, alongAxis));
            openMax = MathF.Max(openMax, Get(l, alongAxis));
            openTop = MathF.Max(openTop, Get(l, upAxis));
        }
        float panelMin = Get(Center, alongAxis) - Get(Size, alongAxis) * 0.5f, panelMax = Get(Center, alongAxis) + Get(Size, alongAxis) * 0.5f;
        float bottom = Get(Center, upAxis) - Get(Size, upAxis) * 0.5f, top = Get(Center, upAxis) + Get(Size, upAxis) * 0.5f;
        Box Part(string n, float a0, float a1, float u0, float u1)
        {
            Vector3 c = Center, s = Size;
            c = Set(c, alongAxis, (a0 + a1) * 0.5f); s = Set(s, alongAxis, a1 - a0);
            c = Set(c, upAxis, (u0 + u1) * 0.5f); s = Set(s, upAxis, u1 - u0);
            return this with { Name = n, Source = new object(), Center = c, Size = s };
        }
        yield return Part("corner stub A", panelMin, openMin, bottom, top);
        yield return Part("corner stub B", openMax, panelMax, bottom, top);
        yield return Part("corner lintel", openMin, openMax, MathF.Min(openTop, top), top);
    }

    public Box RotatedAbout(Vector3 hinge, float unityDegrees, string name)
    {
        var r = Matrix4x4.CreateTranslation(-hinge) * Matrix4x4.CreateRotationY(unityDegrees * MathF.PI / 180f) * Matrix4x4.CreateTranslation(hinge);
        return this with { Name = name, Source = new object(), World = World * r };
    }

    static float Get(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
    static Vector3 Set(Vector3 v, int i, float x) => i == 0 ? new(x, v.Y, v.Z) : i == 1 ? new(v.X, x, v.Z) : new(v.X, v.Y, x);

    /// <summary>Signed distance in XZ from p to this box's horizontal footprint (negative inside).</summary>
    public float Distance2D(Vector2 p)
    {
        // Project the box onto XZ using its horizontal axes (all colliders here are upright or
        // rotated about Y; tilted ones are approximated by their footprint AABB).
        var corners = Corners().Select(c => new Vector2(c.X, c.Z)).ToArray();
        var hull = Hull(corners);
        float inside = float.MaxValue; bool isInside = true; float best = float.MaxValue;
        for (int i = 0; i < hull.Length; i++)
        {
            Vector2 a = hull[i], b = hull[(i + 1) % hull.Length];
            Vector2 e = b - a; float t = Math.Clamp(Vector2.Dot(p - a, e) / MathF.Max(1e-6f, e.LengthSquared()), 0, 1);
            best = MathF.Min(best, Vector2.Distance(p, a + e * t));
            float cross = e.X * (p.Y - a.Y) - e.Y * (p.X - a.X);
            if (cross < 0) isInside = false;
            inside = MathF.Min(inside, MathF.Abs(cross) / MathF.Max(1e-6f, e.Length()));
        }
        return isInside ? -inside : best;
    }

    static Vector2[] Hull(Vector2[] pts)
    {
        var p = pts.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        if (p.Length < 3) return p;
        var h = new List<Vector2>();
        float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var pt in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], pt) <= 0) h.RemoveAt(h.Count - 1); h.Add(pt); }
        int lower = h.Count + 1;
        for (int i = p.Length - 2; i >= 0; i--) { while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1); h.Add(p[i]); }
        h.RemoveAt(h.Count - 1);
        return h.ToArray();
    }
}

internal readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    public static Aabb Of(IEnumerable<Vector3> pts) { var a = pts.ToArray(); return new(new(a.Min(p => p.X), a.Min(p => p.Y), a.Min(p => p.Z)), new(a.Max(p => p.X), a.Max(p => p.Y), a.Max(p => p.Z))); }
    public Vector3 Center => (Min + Max) * 0.5f;
    public IEnumerable<Vector3> Corners() { for (int i = 0; i < 8; i++) yield return new((i & 1) == 0 ? Min.X : Max.X, (i & 2) == 0 ? Min.Y : Max.Y, (i & 4) == 0 ? Min.Z : Max.Z); }
    public float HalfExtentAlong(Vector3 along, Vector3 center)
    {
        float m = 0;
        for (int i = 0; i < 4; i++) { var c = new Vector3((i & 1) == 0 ? Min.X : Max.X, center.Y, (i & 2) == 0 ? Min.Z : Max.Z); m = MathF.Max(m, MathF.Abs(Vector3.Dot(c - center, along))); }
        return m;
    }
}

internal static class Grid
{
    public readonly record struct Result(bool Found, float Length, float MinClearance, string Note);

    public static Result FindPath(List<Box> boxes, Vector3 start, Vector3 goal, float radius)
    {
        const float cell = 0.05f;
        float minX = MathF.Min(start.X, goal.X) - 6f, maxX = MathF.Max(start.X, goal.X) + 6f;
        float minZ = MathF.Min(start.Z, goal.Z) - 6f, maxZ = MathF.Max(start.Z, goal.Z) + 6f;
        int w = (int)((maxX - minX) / cell), h = (int)((maxZ - minZ) / cell);
        var clearance = new float[w * h];
        var nearby = boxes.ToArray();
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                var p = new Vector2(minX + i * cell, minZ + j * cell);
                float c = float.MaxValue;
                foreach (Box b in nearby) c = MathF.Min(c, b.Distance2D(p));
                clearance[j * w + i] = c;
            }
        int Idx(Vector3 v) => (int)((v.Z - minZ) / cell) * w + (int)((v.X - minX) / cell);
        int s = Idx(start), g = Idx(goal);
        if (clearance[s] < radius) return new(false, 0, 0, "(start blocked)");
        if (clearance[g] < radius) return new(false, 0, 0, "(goal blocked)");
        var prev = new int[w * h]; Array.Fill(prev, -1); prev[s] = s;
        var queue = new Queue<int>(); queue.Enqueue(s);
        int[] dx = { 1, -1, 0, 0 }, dz = { 0, 0, 1, -1 };
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (cur == g) break;
            int ci = cur % w, cj = cur / w;
            for (int k = 0; k < 4; k++)
            {
                int ni = ci + dx[k], nj = cj + dz[k];
                if (ni < 0 || nj < 0 || ni >= w || nj >= h) continue;
                int n = nj * w + ni;
                if (prev[n] != -1 || clearance[n] < radius) continue;
                prev[n] = cur; queue.Enqueue(n);
            }
        }
        if (prev[g] == -1)
        {
            // Name the obstacle closest to the straight line, for diagnosis.
            var mid = new Vector2((start.X + goal.X) * 0.5f, (start.Z + goal.Z) * 0.5f);
            string blocker = boxes.OrderBy(b => b.Distance2D(mid)).First().Name;
            return new(false, 0, 0, $"(nearest obstacle on the straight route: {blocker})");
        }
        float length = 0, minClear = float.MaxValue;
        for (int cur = g; cur != s; cur = prev[cur]) { length += cell; minClear = MathF.Min(minClear, clearance[cur]); }
        return new(true, length, minClear, "");
    }
}

internal sealed class Scene
{
    private readonly AssetsFile _f;
    private readonly AssetsFileReader _r;
    private readonly Dictionary<long, AssetFileInfo> _info;
    private readonly Dictionary<long, string> _name = new();
    private readonly Dictionary<long, List<long>> _comps = new();
    private readonly Dictionary<long, bool> _active = new();
    private readonly Dictionary<long, (long go, Quaternion q, Vector3 p, Vector3 s, List<long> ch, long father)> _tr = new();
    private readonly Dictionary<long, long> _goTr = new();

    private Scene(AssetsFile f)
    {
        _f = f; _r = f.Reader;
        _info = f.Metadata.AssetInfos.ToDictionary(a => a.PathId);
        foreach (var a in f.Metadata.AssetInfos.Where(a => a.TypeId == 1))
        {
            var rd = At(a); int n = rd.ReadInt32(); var comps = new List<long>();
            for (int i = 0; i < n; i++) comps.Add(PPtr(rd));
            rd.ReadUInt32(); _name[a.PathId] = Str(rd); rd.ReadUInt16(); _active[a.PathId] = rd.ReadByte() != 0; _comps[a.PathId] = comps;
        }
        foreach (var a in f.Metadata.AssetInfos.Where(a => a.TypeId == 4 || a.TypeId == 224))
        {
            var rd = At(a); long go = PPtr(rd);
            var q = new Quaternion(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            var p = new Vector3(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            var s = new Vector3(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            int n = rd.ReadInt32(); var ch = new List<long>(); for (int i = 0; i < n; i++) ch.Add(PPtr(rd));
            _tr[a.PathId] = (go, q, p, s, ch, PPtr(rd)); _goTr[go] = a.PathId;
        }
    }

    public static Scene Load(string path) => new(new AssetsManager().LoadAssetsFile(path, false).file);

    AssetsFileReader At(AssetFileInfo a) { _r.Position = a.GetAbsoluteByteOffset(_f); return _r; }
    static long PPtr(AssetsFileReader rd) { rd.ReadInt32(); return rd.ReadInt64(); }
    static string Str(AssetsFileReader rd) { int n = rd.ReadInt32(); var s = Encoding.UTF8.GetString(rd.ReadBytes(n)); rd.Align(); return s; }

    public long TransformOf(long go) => _goTr[go];

    /// <summary>Diagnostic: hierarchy with world TRS, box colliders and component type ids.</summary>
    public void Dump(long t, int depth)
    {
        var x = _tr[t];
        var w = World(t);
        Matrix4x4.Decompose(w, out Vector3 sc, out Quaternion q, out Vector3 tr);
        float yaw = MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.Y * q.Y + q.X * q.X)) * 180f / MathF.PI;
        var types = _comps[x.go].Where(_info.ContainsKey).Select(c => _info[c].TypeId).ToList();
        Console.WriteLine($"{new string(' ', depth * 2)}{_name.GetValueOrDefault(x.go)} {(_active[x.go] ? "" : "[inactive] ")}pos ({tr.X:0.000},{tr.Y:0.000},{tr.Z:0.000}) yaw {yaw:0.0} scale ({sc.X:0.###},{sc.Y:0.###},{sc.Z:0.###}) types [{string.Join(",", types)}]");
        foreach (var b in Boxes(t))
        {
            var a = Aabb.Of(b.Corners());
            Console.WriteLine($"{new string(' ', depth * 2)}  BOX x {a.Min.X:0.000}..{a.Max.X:0.000} y {a.Min.Y:0.000}..{a.Max.Y:0.000} z {a.Min.Z:0.000}..{a.Max.Z:0.000} (local size {b.Size.X:0.###},{b.Size.Y:0.###},{b.Size.Z:0.###})");
        }
        foreach (long c in x.ch) if (_tr.ContainsKey(c)) Dump(c, depth + 1);
    }

    public Matrix4x4 World(long t)
    {
        var x = _tr[t];
        // Unity TRS with a left-handed basis: the numeric rotation matrix is identical, so world
        // positions come out exactly as Unity reports them.
        var m = Matrix4x4.CreateScale(x.s) * Matrix4x4.CreateFromQuaternion(x.q) * Matrix4x4.CreateTranslation(x.p);
        return x.father != 0 && _tr.ContainsKey(x.father) ? m * World(x.father) : m;
    }

    string Path(long t) { var x = _tr[t]; string n = _name.GetValueOrDefault(x.go, "?"); return x.father != 0 && _tr.ContainsKey(x.father) ? Path(x.father) + "/" + n : n; }

    public long FindByPath(string path) => _name.Where(kv => _goTr.ContainsKey(kv.Key) && Path(_goTr[kv.Key]) == path).Select(kv => kv.Key).FirstOrDefault();

    public long FindChild(long go, string relative)
    {
        long t = _goTr[go];
        foreach (string part in relative.Split('/'))
        {
            long next = _tr[t].ch.FirstOrDefault(c => _tr.ContainsKey(c) && _name.GetValueOrDefault(_tr[c].go) == part);
            if (next == 0) return 0;
            t = next;
        }
        return _tr[t].go;
    }

    bool ActiveInHierarchy(long t) { for (; t != 0 && _tr.ContainsKey(t); t = _tr[t].father) if (!_active[_tr[t].go]) return false; return true; }

    IEnumerable<Box> Boxes(long t)
    {
        var x = _tr[t];
        if (!ActiveInHierarchy(t)) yield break;
        foreach (long cp in _comps[x.go])
        {
            if (!_info.TryGetValue(cp, out var a) || a.TypeId != 65) continue;
            var rd = At(a); PPtr(rd); PPtr(rd); rd.ReadUInt32(); rd.ReadUInt32(); rd.ReadInt32();
            bool trigger = rd.ReadByte() != 0; rd.ReadByte(); rd.Align(); bool enabled = rd.ReadByte() != 0; rd.Align();
            var size = new Vector3(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            var center = new Vector3(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            if (trigger || !enabled) continue;
            string path = Path(t);
            yield return new Box(path.Replace("Map/Hyland Point/Region_Northtown/", ""), path, a, World(t), center, size);
        }
    }

    public IEnumerable<Box> SolidBoxesUnder(long go)
    {
        var stack = new Stack<long>(); stack.Push(_goTr[go]);
        while (stack.Count > 0) { long t = stack.Pop(); foreach (var b in Boxes(t)) yield return b; foreach (var c in _tr[t].ch) if (_tr.ContainsKey(c)) stack.Push(c); }
    }

    public IEnumerable<Box> SolidBoxesNear(Vector3 center, float radius)
    {
        foreach (long t in _tr.Keys)
        {
            var w = World(t).Translation;
            if (MathF.Abs(w.X - center.X) > radius + 10 || MathF.Abs(w.Z - center.Z) > radius + 10) continue;
            foreach (var b in Boxes(t))
            {
                var c = b.WorldCenter;
                if (MathF.Abs(c.X - center.X) <= radius && MathF.Abs(c.Z - center.Z) <= radius && b.Size.Length() < 200f)
                    yield return b;
            }
        }
    }
}

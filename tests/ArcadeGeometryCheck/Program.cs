// Walk-in entrance geometry check against the installed game's level1 (read-only).
//
//   dotnet run -c Release -- "D:\SteamLibrary\steamapps\common\Schedule I"
//
// 1. Parses Map/Hyland Point/Region_Northtown/Arcade (1) from level1 (Unity 2022.3 binary layout,
//    no type trees) and collects every solid BoxCollider near the building.
// 2. Builds the arcade room's generated walls from the same constants RawArcadeSceneService uses.
// 3. Applies the ArcadeWalkInService changes (corner-door seal -> stubs + lintel, glass leaves
//    swung out, vestibule walls, room leaves swung in) using the same formulas.
// 4. Runs a grid search for a standing player capsule from the street to inside the room for the
//    vanilla building and for the walk-in building.
using System.Numerics;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

string gameDir = args.Length > 0 ? args[0] : @"D:\SteamLibrary\steamapps\common\Schedule I";
string level1 = Path.Combine(gameDir, "Schedule I_Data", "level1");
if (!File.Exists(level1))
{
    Console.WriteLine($"level1 not found under {gameDir}");
    return 2;
}

int failures = 0;
var scene = Scene.Load(level1);
long buildingGo = scene.FindByPath("Map/Hyland Point/Region_Northtown/Arcade (1)");
if (buildingGo == 0)
{
    Console.WriteLine("FAIL Arcade (1) not found in level1");
    return 1;
}
long cornerDoor = scene.FindChild(buildingGo, "arcade/CornerDoor");
Console.WriteLine($"Arcade (1) at {V(scene.World(scene.TransformOf(buildingGo)).Translation)}; CornerDoor found={cornerDoor != 0}");

const float foot = -3.90f;     // Foundation top (measured)
const float bandMin = foot + 0.25f, bandMax = foot + 1.85f; // standing capsule body above step height
var buildingBoxes = scene.SolidBoxesNear(new Vector3(-48f, 0f, 141.5f), 22f)
    .Where(b => b.MaxY > bandMin && b.MinY < bandMax).ToList();
Console.WriteLine($"Solid box colliders near the Arcade intersecting the player band: {buildingBoxes.Count}");

// --- Room (RawArcadeSceneService constants) --------------------------------------------------
const float FXs = 1.22f, FZs = 1.34f;
// Must match ScheduleIArcade.Utils.ArcadeDimensions (verified against the source in RoomChecks).
const float CeilingH = 3.40f, WallH = 3.48f, FloorTop = 0.043f;
static float FX(float x) => x * 1.22f;
static float FZ(float z) => z * 1.34f;
var roomOrigin = new Vector3(-48.4f, -3.8f, 141.4f);
var roomRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI); // yaw 180
Vector3 RoomToWorld(Vector3 local) => roomOrigin + Vector3.Transform(local, roomRot);
Box RoomBox(string name, Vector3 localCenter, Vector3 size, float yawDeg = 0f)
{
    // Same numeric quaternion convention as Unity (Hamilton product, q v q*): world = parent * local.
    var rot = roomRot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDeg * MathF.PI / 180f);
    return Box.Oriented(name, RoomToWorld(localCenter), rot, size);
}
Box Segment(string name, Vector3 a, Vector3 b, float height, float thickness, float extra)
{
    Vector3 d = b - a;
    float length = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
    float yaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
    return RoomBox(name, (a + b) * 0.5f + new Vector3(0, height * 0.5f, 0), new Vector3(thickness, height, length + extra), yaw);
}

var chamferA = new Vector3(FX(-2.67f), 0, FZ(1.56f));
var chamferB = new Vector3(FX(-0.84f), 0, FZ(3.06f));
Vector3 tangent = Vector3.Normalize(chamferB - chamferA);
Vector3 roomInwardLocal = new(tangent.Z, 0, -tangent.X);
const float doorT = 0.53f, doorWidth = 1.90f, doorHeight = 2.18f;
Vector3 doorCenterLocal = Vector3.Lerp(chamferA, chamferB, doorT);
Vector3 doorStart = doorCenterLocal - tangent * (doorWidth * 0.5f);
Vector3 doorEnd = doorCenterLocal + tangent * (doorWidth * 0.5f);
var roomBoxes = new List<Box>
{
    RoomBox("room west wall", new Vector3(FX(-2.67f), WallH * 0.5f, FZ(-0.72f)), new Vector3(0.18f * FXs, WallH, 4.72f * FZs)),
    Segment("room chamfer before door", chamferA, doorStart, WallH, 0.18f * FXs, 0.12f),
    Segment("room chamfer after door", doorEnd, chamferB, WallH, 0.18f * FXs, 0.12f),
    RoomBox("room north wall", new Vector3((FX(-0.84f) + FX(2.67f)) * 0.5f, WallH * 0.5f, FZ(3.06f)), new Vector3(FX(2.67f) - FX(-0.84f), WallH, 0.18f * FZs)),
    RoomBox("room south shell", new Vector3(0, WallH * 0.5f, FZ(-3.02f)), new Vector3(5.34f * FXs, WallH, 0.16f * FZs)),
    RoomBox("room east shell", new Vector3(FX(2.64f), WallH * 0.5f, 0), new Vector3(0.16f * FXs, WallH, 6.12f * FZs)),
};
Vector3 roomDoorCenter = RoomToWorld(doorCenterLocal);
Vector3 roomInward = Flat(Vector3.Transform(roomInwardLocal, roomRot));
Box RoomLeaf(string name, Vector3 hingeLocal, Vector3 towardCenterLocal, bool open, bool intoRoom = false)
{
    Vector3 dirLocal = open ? (intoRoom ? roomInwardLocal : -roomInwardLocal) : towardCenterLocal; // open leaves swing away from the player
    Vector3 a = hingeLocal, b = hingeLocal + dirLocal * (doorWidth * 0.5f);
    return Segment(name, a, b, doorHeight, 0.09f, 0f);
}

// --- Building door measurements ---------------------------------------------------------------
var leaves = scene.SolidBoxesUnder(cornerDoor).Where(b => b.Path.Contains("Glass Door")).ToList();
var seal = scene.SolidBoxesUnder(cornerDoor).First(b => b.Path.EndsWith("arcade/CornerDoor"));
var doorway = Aabb.Of(leaves.SelectMany(l => l.Corners()));
Vector3 buildingDoorCenter = new(doorway.Center.X, doorway.Min.Y, doorway.Center.Z);
Vector3 along = seal.HorizontalLongAxis();
Vector3 inward = Flat(roomDoorCenter - buildingDoorCenter);
inward = Vector3.Normalize(Flat(inward - along * Vector3.Dot(inward, along)));
float depth = Vector3.Dot(roomDoorCenter - buildingDoorCenter, inward);
float doorwayHalf = doorway.HalfExtentAlong(along, buildingDoorCenter);
float doorwayHeight = doorway.Max.Y - doorway.Min.Y;
Console.WriteLine($"Building glass doorway centre {V(buildingDoorCenter)} clear width {doorwayHalf * 2:0.00} m height {doorwayHeight:0.00} m");
Console.WriteLine($"Room chamfer door centre {V(roomDoorCenter)}; vestibule depth {depth:0.00} m; tangential offset {Vector3.Dot(roomDoorCenter - buildingDoorCenter, along):0.00} m");

// Walk-in modifications.
var walkIn = buildingBoxes.Where(b => !ReferenceEquals(b.Source, seal.Source) && !leaves.Any(l => ReferenceEquals(l.Source, b.Source))).ToList();
walkIn.AddRange(seal.SplitAroundOpening(doorway));
// 0.4.1: each leaf is a clone placed in the vanilla "Door" pose; its origin (== the Collider
// child's origin) is the hinge edge. Opening swings away from the player: inward when entering,
// outward when leaving. Both variants are path-checked below.
var hinges = leaves.Select(l => l.World.Translation).ToList();
var leafCentres = leaves.Select(l => l.WorldCenter).ToList();
var walkInOut = new List<Box>(walkIn);
for (int i = 0; i < leaves.Count; i++)
{
    walkIn.Add(leaves[i].RotatedAbout(hinges[i], ChooseSign(hinges[i], leafCentres[i], inward, 88f), leaves[i].Name + " (open inward)"));
    walkInOut.Add(leaves[i].RotatedAbout(hinges[i], ChooseSign(hinges[i], leafCentres[i], -inward, 88f), leaves[i].Name + " (open outward)"));
}
{
    // Closed fit: the leaves meet in the middle and their hinge edges sit on the opening edges.
    float frameHalf = hinges.Max(h => MathF.Abs(Vector3.Dot(Flat(h - buildingDoorCenter), along)));
    var freeEdges = new List<float>();
    for (int i = 0; i < leaves.Count; i++)
    {
        Vector3 arm = Flat(leafCentres[i] - hinges[i]);
        freeEdges.Add(Vector3.Dot(Flat(hinges[i] + arm * 2f - buildingDoorCenter), along));
    }
    float centreGap = MathF.Abs(freeEdges[0] - freeEdges[1]);
    float asym = MathF.Abs(MathF.Abs(Vector3.Dot(Flat(hinges[0] - buildingDoorCenter), along)) - MathF.Abs(Vector3.Dot(Flat(hinges[1] - buildingDoorCenter), along)));
    float openingHalf = doorway.HalfExtentAlong(along, buildingDoorCenter);
    Console.WriteLine($"Closed leaves: hinge edges at +-{frameHalf:0.000} m (opening half {openingHalf:0.000}, asymmetry {asym * 1000:0} mm); free edges meet with a {centreGap * 1000:0} mm gap. Jambs overlap each hinge edge by 20 mm; centre stop 35 mm wide.");
    if (centreGap > 0.035f) { failures++; Console.WriteLine("  FAIL centre gap wider than the centre stop"); }
    if (asym > 0.02f) { failures++; Console.WriteLine("  FAIL leaves offset from the opening"); }
    if (frameHalf > openingHalf + 0.005f) { failures++; Console.WriteLine("  FAIL hinge edge outside the measured opening"); }
}
float roomHalf = doorWidth * 0.5f;
float halfWidth = MathF.Max(doorwayHalf, roomHalf) + 0.12f;
var vestRot = LookRotation(inward);
Box Vest(string name, Vector3 local, Vector3 size) => Box.Oriented(name, buildingDoorCenter + Vector3.Transform(local, vestRot), vestRot, size);
const float wallT = 0.12f, start = 0.04f;
float panelTop = seal.MaxY;
float vestHeight = Math.Clamp(panelTop - buildingDoorCenter.Y - 0.10f, 2.55f, CeilingH);
float len = depth - start + 0.12f;
Console.WriteLine($"Vestibule: height {vestHeight:0.00} m (ceiling slab top y {buildingDoorCenter.Y + vestHeight + 0.10f:0.00}) under the corner panel top y {panelTop:0.00}");
if (buildingDoorCenter.Y + vestHeight + 0.10f > panelTop + 0.001f) { failures++; Console.WriteLine("  FAIL vestibule pokes through the corner panel"); }
foreach (var list in new[] { walkIn, walkInOut })
{
    list.Add(Vest("vestibule wall left", new Vector3(-halfWidth - wallT * 0.5f, vestHeight * 0.5f, start + len * 0.5f), new Vector3(wallT, vestHeight, len)));
    list.Add(Vest("vestibule wall right", new Vector3(halfWidth + wallT * 0.5f, vestHeight * 0.5f, start + len * 0.5f), new Vector3(wallT, vestHeight, len)));
}

var vanilla = new List<Box>(buildingBoxes);
vanilla.AddRange(roomBoxes);
vanilla.Add(RoomLeaf("room leaf A (closed)", doorStart, tangent, false));
vanilla.Add(RoomLeaf("room leaf B (closed)", doorEnd, -tangent, false));
walkIn.AddRange(roomBoxes);
walkIn.Add(RoomLeaf("room leaf A (open)", doorStart, tangent, true));
walkIn.Add(RoomLeaf("room leaf B (open)", doorEnd, -tangent, true));
walkInOut.AddRange(roomBoxes);
walkInOut.Add(RoomLeaf("room leaf A (open into room)", doorStart, tangent, true, intoRoom: true));
walkInOut.Add(RoomLeaf("room leaf B (open into room)", doorEnd, -tangent, true, intoRoom: true));

foreach (Box b in walkIn.Where(b => b.Name.StartsWith("corner")))
{
    var a = Aabb.Of(b.Corners());
    Console.WriteLine($"  {b.Name}: y {a.Min.Y:0.00}..{a.Max.Y:0.00}  x {a.Min.X:0.00}..{a.Max.X:0.00} z {a.Min.Z:0.00}..{a.Max.Z:0.00}");
}
// Only colliders that intersect the standing player's body band can block walking.
vanilla = vanilla.Where(b => b.MaxY > bandMin && b.MinY < bandMax).ToList();
walkIn = walkIn.Where(b => b.MaxY > bandMin && b.MinY < bandMax).ToList();
walkInOut = walkInOut.Where(b => b.MaxY > bandMin && b.MinY < bandMax).ToList();

Vector3 street = buildingDoorCenter - inward * 2.5f;
Vector3 insideRoom = roomDoorCenter + roomInward * 1.2f;
Console.WriteLine($"Path query: street {V(street)} -> inside room {V(insideRoom)}");

foreach (float radius in new[] { 0.30f, 0.40f })
{
    var vanillaResult = Grid.FindPath(vanilla, street, insideRoom, radius);
    var walkInResult = Grid.FindPath(walkIn, street, insideRoom, radius);
    Console.WriteLine($"  capsule r={radius:0.00} m  vanilla: {(vanillaResult.Found ? "PATH" : "BLOCKED")} {vanillaResult.Note}");
    Console.WriteLine($"  capsule r={radius:0.00} m  walk-in: {(walkInResult.Found ? "PATH" : "BLOCKED")} length {walkInResult.Length:0.0} m, min clearance {walkInResult.MinClearance:0.00} m {walkInResult.Note}");
    if (vanillaResult.Found) { failures++; Console.WriteLine("  FAIL vanilla building should be sealed (that is why the old build teleported)"); }
    if (!walkInResult.Found) { failures++; Console.WriteLine("  FAIL walk-in entrance is not walkable"); }
    var outResult = Grid.FindPath(walkInOut, insideRoom, street, radius);
    Console.WriteLine($"  capsule r={radius:0.00} m  leaving (leaves swung the other way): {(outResult.Found ? "PATH" : "BLOCKED")} length {outResult.Length:0.0} m {outResult.Note}");
    if (!outResult.Found) { failures++; Console.WriteLine("  FAIL leaving the arcade is blocked"); }
}

// The vestibule must stay inside the building and must not cut into the room interior.
var interior = new Aabb(new Vector3(-52.40f, -10, 135.65f), new Vector3(-43.65f, 10, 147.40f));
foreach (Box b in walkIn.Where(b => b.Name.StartsWith("vestibule")))
{
    var a = Aabb.Of(b.Corners());
    bool insideBuilding = a.Min.X >= interior.Min.X - 0.3f && a.Max.X <= interior.Max.X + 0.3f && a.Min.Z >= interior.Min.Z - 0.3f && a.Max.Z <= interior.Max.Z + 0.3f;
    Console.WriteLine($"  {b.Name}: x {a.Min.X:0.00}..{a.Max.X:0.00} z {a.Min.Z:0.00}..{a.Max.Z:0.00} insideBuilding={insideBuilding}");
    if (!insideBuilding) failures++;
}
float rise = (roomOrigin.Y + 0.043f) - foot;
Console.WriteLine($"  vestibule ramp: rise {rise:0.000} m over {depth:0.00} m = {MathF.Atan2(rise, depth) * 180f / MathF.PI:0.0} deg");

failures += RoomChecks.Run(scene, buildingGo, roomOrigin, roomRot, CeilingH, WallH, FloorTop, rise, depth);
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

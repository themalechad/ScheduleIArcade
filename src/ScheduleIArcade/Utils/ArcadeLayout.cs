namespace ScheduleIArcade.Utils;

internal enum LayoutKind
{
    /// <summary>One of the packaged arcade machines (moved to this spot at load).</summary>
    Machine,
    /// <summary>A floor-standing fixture built or reused at runtime (prize counter).</summary>
    Floor
}

/// <summary>
/// One reserved spot of the floor plan in room-local metres (room root space, +Y up). The footprint
/// is axis-aligned in room space.
/// </summary>
internal sealed class LayoutSlot
{
    public LayoutSlot(string id, LayoutKind kind, float x, float z, float yaw, float sizeX, float sizeZ, float maxHeight, params string[] candidates)
    {
        Id = id;
        Kind = kind;
        X = x;
        Z = z;
        Yaw = yaw;
        SizeX = sizeX;
        SizeZ = sizeZ;
        MaxHeight = maxHeight;
        Candidates = candidates;
    }

    public string Id { get; }
    public LayoutKind Kind { get; }
    public float X { get; }
    public float Z { get; }
    /// <summary>Machines: exact model yaw. Fixtures: facing (the counter's front faces -X at -90).</summary>
    public float Yaw { get; }
    public float SizeX { get; }
    public float SizeZ { get; }
    public float MaxHeight { get; }
    /// <summary>Vanilla object names tried in order (exact name, or name followed by " (n)").</summary>
    public string[] Candidates { get; }
    public float MinX => X - SizeX * 0.5f;
    public float MaxX => X + SizeX * 0.5f;
    public float MinZ => Z - SizeZ * 0.5f;
    public float MaxZ => Z + SizeZ * 0.5f;
}

/// <summary>An opening (window or door) cut into one interior wall lining, in that wall's 2D frame.</summary>
internal readonly record struct WallOpening(float AlongMin, float AlongMax, float Bottom, float Top, string Kind);

/// <summary>One solid box of a wall lining between two "along" coordinates and two heights (room local).</summary>
internal readonly record struct WallPiece(WallLining Wall, string Name, float A0, float A1, float Y0, float Y1, bool Skirting)
{
    /// <summary>Box centre (room local) half a thickness behind the inner face.</summary>
    public (float x, float y, float z) Centre
    {
        get
        {
            var (tx, tz) = Wall.Tangent;
            var (nx, nz) = Wall.Inward;
            float mid = (A0 + A1) * 0.5f;
            return (Wall.AX + tx * mid - nx * Wall.Thickness * 0.5f, (Y0 + Y1) * 0.5f, Wall.AZ + tz * mid - nz * Wall.Thickness * 0.5f);
        }
    }

    /// <summary>Box size: x = thickness (across), y = height, z = length (along the wall).</summary>
    public (float x, float y, float z) Size => (Wall.Thickness, Y1 - Y0, A1 - A0);
}

/// <summary>
/// One straight interior wall lining. It runs from (AX, AZ) to (BX, BZ) along its inner face (room
/// local metres); the solid layer extends Thickness metres from that face outwards (away from the
/// room) so it fills the space up to the vanilla building's own wall. "Along" coordinates of the
/// openings are metres from A towards B.
/// </summary>
internal sealed class WallLining
{
    public WallLining(string id, float ax, float az, float bx, float bz, float thickness, params WallOpening[] openings)
    {
        Id = id;
        AX = ax; AZ = az; BX = bx; BZ = bz;
        Thickness = thickness;
        Openings = openings;
    }

    public string Id { get; }
    public float AX { get; }
    public float AZ { get; }
    public float BX { get; }
    public float BZ { get; }
    public float Thickness { get; }
    public WallOpening[] Openings { get; }
    public float Length => MathF.Sqrt((BX - AX) * (BX - AX) + (BZ - AZ) * (BZ - AZ));
    /// <summary>Unit direction A -> B.</summary>
    public (float x, float z) Tangent => ((BX - AX) / Length, (BZ - AZ) / Length);
    /// <summary>Unit normal pointing into the room (the walls run counter-clockwise seen from above).</summary>
    public (float x, float z) Inward => (-Tangent.z, Tangent.x);
}

/// <summary>
/// 0.6.0 arcade: the WHOLE ground floor of the vanilla Northtown "Arcade (1)" building.
///
/// Every number below was measured read-only from Schedule I 0.4.7f12 level1 (BoxColliders and the
/// static-batch sub-mesh bounds of Map/Hyland Point/Region_Northtown/Arcade (1)); see
/// tests/ArcadeGeometryCheck, which re-measures them from the installed game and fails if any drifts.
///
/// World facts (metres):
///   * Foundation top / sidewalk at the corner door: y = -3.90 (the game's navmesh there is -3.87..-3.90).
///   * Ground-floor storey: walls y -3.90..-0.40; the upper storey's walls start at y -0.40; roof y 3.30.
///   * Outer wall faces: west x -52.50 (BlankWall/DoorWall), east x -43.50 (WindowWall 2-4),
///     north z 147.50 (BlankWall 3-5), south z 135.50 (WindowWall, WindowWall 1).
///   * The innermost vanilla plane (window glass / navy panels) is 0.15 m inside the outer face:
///     x -52.40 / -43.65, z 147.40 / 135.65.
///   * Ground-floor window glass: y -3.25..-1.05, 2.5 m wide, one per 3 m bay, centred in the bay.
///   * CornerDoor: 45-degree chamfer at the south-east corner, centre line wz - wx = 182.071,
///     0.2 m thick; glass/door plane on its inner face (wz - wx = 182.212); the glass door pair is
///     2.10 m wide (hinges +-1.05 m from the centre (-45.107, 137.106)), 2.10 m tall; sidelights and
///     transom glass span +-1.871 m along the chamfer, y -3.25..-1.05.
///
/// The room root sits at the building centre (-48, FloorWorldY - FloorTop, 141.5) with yaw 180, so
/// room local x = -(world x + 48) and local z = 141.5 - world z:
///   -X = the street-side EAST window wall, +X = the blank WEST wall (back door behind it),
///   -Z = the blank NORTH wall, +Z = the street-side SOUTH window wall, entrance on the -X/+Z chamfer.
///
///   +Z  south windows   [   window   ]  [   window   ]
///      /door                    s
///     /           . . .     Pac-Man T          | counter
///   -X windows                  s              | + register
///      |      air                              |
///      |      hockey                           | pinball 04
///      |                                       | pinball 03
///      |                                       | pinball 02
///   -Z [Doom 1][Doom 2]      [ TV ]            | pinball 01
/// </summary>
internal static class ArcadeLayout
{
    // ---- Placement of the room root ---------------------------------------------------------------
    public const float WorldOriginX = -48.0f;
    public const float WorldOriginZ = 141.5f;
    public const float RoomYaw = 180f;
    /// <summary>Foundation top: the sidewalk outside the corner door and the arcade floor are the same plane.</summary>
    public const float FloorWorldY = -3.90f;
    /// <summary>Top of the walkable floor in room-local metres.</summary>
    public const float FloorTop = 0.043f;
    public const float WorldOriginY = FloorWorldY - FloorTop;
    /// <summary>Where the vanilla building's upper storey begins (its window walls start here).</summary>
    public const float FirstFloorWorldY = -0.40f;
    public const float RoofWorldY = 3.30f;

    // ---- Ceiling ----------------------------------------------------------------------------------
    /// <summary>Ceiling underside (room local). World y = -0.50: below the first-floor line.</summary>
    public const float CeilingY = FloorTop + ArcadeDimensions.InteriorCeilingHeight;
    /// <summary>Top of the solid ceiling slab = the first-floor line (world y -0.40).</summary>
    public const float CeilingTopY = FirstFloorWorldY - WorldOriginY;

    // ---- Inner faces of the interior wall lining (room local) -------------------------------------
    // Each lining is a single solid layer from the vanilla wall's innermost plane (+2 cm) inwards,
    // so vanilla wall + lining is one 0.27..0.30 m thick wall (outer face to inner face) with no cavity.
    /// <summary>Street-side east window wall (world x -43.77).</summary>
    public const float InnerWest = -4.23f;
    /// <summary>Blank west wall (world x -52.20).</summary>
    public const float InnerEast = 4.20f;
    /// <summary>Blank north wall (world z 147.22).</summary>
    public const float InnerSouth = -5.72f;
    /// <summary>Street-side south window wall (world z 135.77).</summary>
    public const float InnerNorth = 5.73f;
    /// <summary>Chamfer inner face: z - x = ChamferC (world wz - wx = 182.382, 0.22 m inside the CornerDoor centre line).</summary>
    public const float ChamferC = 7.118f;
    public const float ChamferAX = InnerWest, ChamferAZ = InnerWest + ChamferC;
    public const float ChamferBX = InnerNorth - ChamferC, ChamferBZ = InnerNorth;

    /// <summary>Lining thickness per wall (from the inner face outwards).</summary>
    public const float LiningWindowWall = 0.10f, LiningWest = 0.18f, LiningNorth = 0.16f, LiningChamfer = 0.10f;

    /// <summary>Window glass: local y 0.693..2.893 (world -3.25..-1.05).</summary>
    public const float WindowBottom = -3.25f - WorldOriginY, WindowTop = -1.05f - WorldOriginY;
    /// <summary>Door + transom opening top (the transom glass ends with the windows).</summary>
    public const float DoorOpeningTop = WindowTop;
    /// <summary>The cloned glass leaves are 2.10 m tall (world -3.90..-1.80), hinged 1.05 m either side of the centre.</summary>
    public const float LeafHeight = 2.10f, LeafHalfSpan = 1.05f;
    /// <summary>Lining opening half-width around the door: clear of the hinge stiles (reach 1.085 m).</summary>
    public const float DoorLiningHalf = 1.10f;
    /// <summary>Sidelight + transom glass half-span along the chamfer (vanilla CornerDoor mesh).</summary>
    public const float GlassHalfSpan = 1.871f;

    /// <summary>Doorway centre on the vanilla glass/door plane (world (-45.107, 137.106)).</summary>
    public const float DoorX = -2.893f, DoorZ = 4.394f;
    /// <summary>Unit normal into the room at the door, and the unit tangent along the chamfer (A -> B).</summary>
    public static readonly (float x, float z) DoorInward = (0.70710678f, -0.70710678f);
    public static readonly (float x, float z) ChamferTangent = (0.70710678f, 0.70710678f);

    /// <summary>The five interior wall linings, counter-clockwise seen from above (room +Y up).</summary>
    public static readonly WallLining[] Walls = BuildWalls();

    private static WallLining[] BuildWalls()
    {
        // Window bays in room coordinates. -X wall (world east): world z 138.75..141.25,
        // 141.75..144.25, 144.75..147.25 -> local z 0.25..2.75, -2.75..-0.25, -5.75..-3.25.
        // +Z wall (world south): world x -49.25..-46.75 and -52.25..-49.75 -> local x -1.25..1.25, 1.75..4.25.
        // The room is to the left of every A->B (Inward = (-t.z, t.x)).
        var walls = new List<WallLining>();

        // -X wall (street windows), from the chamfer corner A (z 2.863) down to the -Z corner.
        {
            float ax = InnerWest, az = ChamferAZ, bx = InnerWest, bz = InnerSouth;
            walls.Add(new WallLining("street wall east (-X)", ax, az, bx, bz, LiningWindowWall,
                Window(az, 2.75f, 0.25f), Window(az, -0.25f, -2.75f), Window(az, -3.25f, -5.75f, clipMax: az - bz)));
        }
        // -Z wall (blank north), from -X to +X.
        walls.Add(new WallLining("blank wall north (-Z)", InnerWest, InnerSouth, InnerEast, InnerSouth, LiningNorth));
        // +X wall (blank west, back door behind it), from -Z to +Z.
        walls.Add(new WallLining("blank wall west (+X)", InnerEast, InnerSouth, InnerEast, InnerNorth, LiningWest));
        // +Z wall (street windows), from +X to the chamfer corner B.
        {
            float ax = InnerEast, bx = ChamferBX;
            walls.Add(new WallLining("street wall south (+Z)", ax, InnerNorth, bx, InnerNorth, LiningWindowWall,
                Window(ax, 4.25f, 1.75f), Window(ax, 1.25f, -1.25f)));
        }
        // Chamfer (entrance) from B back to A: door + transom in the middle, sidelights either side.
        {
            float bx = ChamferBX, bz = ChamferBZ, ax = ChamferAX, az = ChamferAZ;
            var lining = new WallLining("entrance chamfer", bx, bz, ax, az, LiningChamfer);
            float len = lining.Length;
            // Door centre projected on this wall: along = distance from B along (A - B).
            var (tx, tz) = lining.Tangent;
            float centre = (DoorX - bx) * tx + (DoorZ - bz) * tz;
            walls.Add(new WallLining(lining.Id, bx, bz, ax, az, LiningChamfer,
                new WallOpening(MathF.Max(0f, centre - GlassHalfSpan), centre - DoorLiningHalf, WindowBottom, WindowTop, "sidelight"),
                new WallOpening(centre - DoorLiningHalf, centre + DoorLiningHalf, FloorTop, DoorOpeningTop, "door"),
                new WallOpening(centre + DoorLiningHalf, MathF.Min(len, centre + GlassHalfSpan), WindowBottom, WindowTop, "sidelight")));
        }
        return walls.ToArray();
    }

    /// <summary>
    /// A window between two room coordinates on a wall that starts at <paramref name="start"/> and runs
    /// towards decreasing coordinates (both window walls do): along = start - coordinate.
    /// </summary>
    private static WallOpening Window(float start, float from, float to, float clipMin = 0f, float clipMax = float.PositiveInfinity)
    {
        float a = start - from, b = start - to;
        float min = MathF.Max(MathF.Min(a, b), clipMin), max = MathF.Min(MathF.Max(a, b), clipMax);
        return new WallOpening(min, max, WindowBottom, WindowTop, "window");
    }

    /// <summary>
    /// Splits every lining around its openings into solid boxes (full-height pieces between openings,
    /// sill pieces below and header pieces above each opening). Piece ends run one lining thickness
    /// into the neighbouring walls so every corner is closed. Shared by the room builder and the
    /// headless geometry check.
    /// </summary>
    public static List<WallPiece> WallPieces()
    {
        var pieces = new List<WallPiece>();
        float bottom = FloorTop - 0.02f, top = CeilingTopY;
        foreach (WallLining wall in Walls)
        {
            string id = wall.Id.Replace(' ', '_');
            var openings = wall.Openings.Where(o => o.AlongMax - o.AlongMin > 0.01f).OrderBy(o => o.AlongMin).ToList();
            float cursor = -wall.Thickness;
            foreach (WallOpening o in openings)
            {
                if (o.AlongMin - cursor > 0.005f)
                    pieces.Add(new WallPiece(wall, id + "_" + pieces.Count, cursor, o.AlongMin, bottom, top, true));
                if (o.Bottom - bottom > 0.005f)
                    pieces.Add(new WallPiece(wall, id + "_" + pieces.Count + "_below_" + o.Kind, o.AlongMin, o.AlongMax, bottom, o.Bottom, true));
                if (top - o.Top > 0.005f)
                    pieces.Add(new WallPiece(wall, id + "_" + pieces.Count + "_above_" + o.Kind, o.AlongMin, o.AlongMax, o.Top, top, false));
                cursor = o.AlongMax;
            }
            if (wall.Length + wall.Thickness - cursor > 0.005f)
                pieces.Add(new WallPiece(wall, id + "_" + pieces.Count, cursor, wall.Length + wall.Thickness, bottom, top, true));
        }
        return pieces;
    }

    /// <summary>Outline pushed out to the lining's outer faces (floor and ceiling slabs run into the walls).</summary>
    public static (float x, float z)[] OuterOutline()
    {
        float w = InnerWest - LiningWindowWall, e = InnerEast + LiningWest, s = InnerSouth - LiningNorth, n = InnerNorth + LiningWindowWall;
        float c = ChamferC + LiningChamfer * 1.41421356f;
        return new[] { (w, s), (e, s), (e, n), (n - c, n), (w, w + c) };
    }

    /// <summary>Interior floor outline (counter-clockwise), room local.</summary>
    public static readonly (float x, float z)[] FloorOutline =
    {
        (InnerWest, InnerSouth), (InnerEast, InnerSouth), (InnerEast, InnerNorth), (ChamferBX, ChamferBZ), (ChamferAX, ChamferAZ),
    };

    /// <summary>True when (x, z) is inside the interior outline shrunk by <paramref name="margin"/>.</summary>
    public static bool IsInsideInterior(float x, float z, float margin = 0f)
    {
        if (x < InnerWest + margin || x > InnerEast - margin || z < InnerSouth + margin || z > InnerNorth - margin)
            return false;
        // Room side of the chamfer: z - x <= ChamferC - margin * sqrt(2).
        return z - x <= ChamferC - margin * 1.41421356f;
    }

    /// <summary>Half of the minimum walkway width: a 1.0 m wide body must fit between items and walls.</summary>
    public const float MinWalkwayRadius = ArcadeDimensions.MinWalkwayClearance * 0.5f;

    // ---- Floor plan ----------------------------------------------------------------------------------
    public static readonly LayoutSlot[] Machines =
    {
        // Pinball bank: four side by side against the blank +X wall, playfields facing the -X aisle.
        // 01/02 have the backbox at model +X (yaw 0 puts it on the wall); 03/04 are the same model
        // turned 180 degrees in the package.
        new("Pinball Machine 01", LayoutKind.Machine, 3.71f, -5.00f, 0f, 0.86f, 0.50f, 1.4f),
        new("Pinball Machine 02", LayoutKind.Machine, 3.71f, -4.30f, 0f, 0.86f, 0.50f, 1.4f),
        new("Pinball Machine 03", LayoutKind.Machine, 3.71f, -3.60f, 180f, 0.86f, 0.50f, 1.4f),
        new("Pinball Machine 04", LayoutKind.Machine, 3.71f, -2.90f, 180f, 0.86f, 0.50f, 1.4f),
        // Doom upright pair side by side against the blank -Z wall, screens facing +Z.
        new("Arcade Machine 01", LayoutKind.Machine, -3.20f, -5.44f, 90f, 0.46f, 0.40f, 1.6f),
        new("Arcade Machine 02", LayoutKind.Machine, -2.50f, -5.44f, -90f, 0.46f, 0.40f, 1.6f),
        // Open floor: cocktail table (with its two end stools) near the entrance, air hockey mid-room.
        new("Table Top", LayoutKind.Machine, 1.00f, 2.00f, 90f, 0.92f, 1.62f, 0.7f),
        new("Air Hockey", LayoutKind.Machine, -1.50f, -1.90f, 0f, 0.90f, 1.80f, 1.0f),
    };

    /// <summary>Pac-Man end stools (part of the table model), room space.</summary>
    public static readonly LayoutSlot[] TableStools =
    {
        new("Pac-Man stool A", LayoutKind.Machine, 1.00f, 3.05f, 0f, 0.30f, 0.30f, 0.3f),
        new("Pac-Man stool B", LayoutKind.Machine, 1.00f, 0.95f, 0f, 0.30f, 0.30f, 0.3f),
    };

    /// <summary>The generated Arcade TV console (Pong + Snake), against the -Z wall, screen facing +Z.</summary>
    public static readonly LayoutSlot Tv = new("Arcade TV", LayoutKind.Machine, 0.20f, -5.40f, 180f, 1.46f, 0.62f, 1.9f);

    /// <summary>Prize counter against the blank +X wall, its front facing the room (-X).</summary>
    public static readonly LayoutSlot PrizeCounter = new("prize counter", LayoutKind.Floor, 3.80f, 1.80f, -90f, 0.70f, 1.80f, 1.15f,
        "Counter", "Frontdesk", "Desk");

    /// <summary>Register on the counter (visual only).</summary>
    public static readonly string[] RegisterCandidates = { "Cash Register", "CashCounter", "cash register", "cashcounter" };

    /// <summary>Resources.Load path tried for the counter when no unbatched scene counter is loaded.</summary>
    public const string PrizeCounterResource = "storage/displaycabinet/DisplayCabinet_Built";

    /// <summary>Ceiling light fixtures (room local x, z); each also carries a point light.</summary>
    public static readonly (float x, float z)[] CeilingLights =
    {
        (-2.40f, -3.60f), (1.60f, -3.60f), (-2.40f, 0.20f), (1.60f, 0.20f), (-0.40f, 3.40f), (2.80f, 3.40f),
    };
    public static readonly string[] CeilingLightCandidates = { "Fluorescent_Light", "Light_Fluorescent_Light_LOD0" };
    public static readonly string[] CeilingLightResources = { "lights/ledlight/LEDLight", "lights/halogenlight/HalogenLight" };

    public static LayoutSlot? Machine(string modelName)
    {
        foreach (LayoutSlot slot in Machines)
            if (string.Equals(slot.Id, modelName, StringComparison.OrdinalIgnoreCase))
                return slot;
        return null;
    }

    /// <summary>Every floor footprint (machines, stools, TV, counter).</summary>
    public static IEnumerable<LayoutSlot> FloorItems()
    {
        foreach (LayoutSlot s in Machines) yield return s;
        foreach (LayoutSlot s in TableStools) yield return s;
        yield return Tv;
        yield return PrizeCounter;
    }

    /// <summary>
    /// Where a player stands to use each item (room local), 0.55 m out from its front. Every one must
    /// be reachable from the door by a 1.0 m wide walkway (checked by tests/ArcadeGeometryCheck).
    /// </summary>
    public static IEnumerable<(string id, float x, float z)> PlayPositions()
    {
        foreach (LayoutSlot s in Machines)
        {
            if (s.Id.StartsWith("Pinball", StringComparison.Ordinal))
                yield return (s.Id, s.MinX - 0.55f, s.Z);
            else if (s.Id.StartsWith("Arcade Machine", StringComparison.Ordinal))
                yield return (s.Id, s.X, s.MaxZ + 0.55f);
            else if (s.Id == "Air Hockey")
            {
                yield return (s.Id + " (end -Z)", s.X, s.MinZ - 0.55f);
                yield return (s.Id + " (end +Z)", s.X, s.MaxZ + 0.55f);
            }
        }
        foreach (LayoutSlot s in TableStools)
            yield return (s.Id, s.X, s.Z + (s.Z > Machines.First(m => m.Id == "Table Top").Z ? 0.70f : -0.70f));
        yield return (Tv.Id, Tv.X, Tv.MaxZ + 0.55f);
        yield return (PrizeCounter.Id, PrizeCounter.MinX - 0.55f, PrizeCounter.Z);
    }

    /// <summary>The walk-in doorway: centre on the door plane and its inward normal.</summary>
    public static (float x, float z, float nx, float nz) Doorway() => (DoorX, DoorZ, DoorInward.x, DoorInward.z);

    /// <summary>Room local (x, z) -> world (x, z).</summary>
    public static (float x, float z) ToWorld(float x, float z) => (WorldOriginX - x, WorldOriginZ - z);

    /// <summary>World (x, z) -> room local (x, z).</summary>
    public static (float x, float z) ToLocal(float wx, float wz) => (WorldOriginX - wx, WorldOriginZ - wz);
}

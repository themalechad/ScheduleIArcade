namespace ScheduleIArcade.Utils;

internal enum LayoutKind
{
    /// <summary>One of the packaged arcade machines (moved to this spot at load).</summary>
    Machine,
    /// <summary>Vanilla prop standing on the floor; gets one simple box collider.</summary>
    Floor,
    /// <summary>Vanilla prop on top of another floor prop (no collider).</summary>
    OnTop,
    /// <summary>Vanilla prop mounted on a wall (no collider).</summary>
    Wall,
    /// <summary>Vanilla prop hanging from the ceiling (no collider).</summary>
    Ceiling
}

/// <summary>
/// One reserved spot of the floor plan in room-local metres (room root space, +Y up). The footprint
/// is axis-aligned in room space; vanilla props are rotated/scaled at runtime to fit inside it.
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
    /// <summary>Machines: exact model yaw. Props: preferred facing (wall props face away from the wall).</summary>
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

/// <summary>
/// 0.4.1 arcade floor plan, shared by the room builder, the vanilla-prop dresser and the headless
/// geometry check. Room-local axes: -X is the pinball wall, +X the counter wall, -Z the south wall
/// (Doom cabinets), +Z the north wall (TV), and the entrance is on the -X/+Z chamfer.
///
///   +Z  north wall:  [clock]   [  TV  ]  [jukebox]
///        chamfer door   chairs  chairs
///   pin 04 |                               | counter
///   pin 03 |        Pac-Man                | + register
///   pin 02 |        table      air hockey  |
///   pin 01 |                               | prize shelf
///   -Z  south wall:  [Doom 1][Doom 2] [bin]
/// </summary>
internal static class ArcadeLayout
{
    /// <summary>Inner wall faces (room-local), measured from the generated room shell.</summary>
    public const float InnerWest = -3.147f;
    public const float InnerEast = 3.123f;
    public const float InnerSouth = -3.94f;
    public const float InnerNorth = 3.98f;
    /// <summary>Chamfer (entrance wall) from (ChamferAX, ChamferAZ) to (ChamferBX, ChamferBZ), wall centre line.</summary>
    public const float ChamferAX = -2.67f * 1.22f, ChamferAZ = 1.56f * 1.34f, ChamferBX = -0.84f * 1.22f, ChamferBZ = 3.06f * 1.34f;

    public static readonly LayoutSlot[] Machines =
    {
        // Pinball bank: all four side by side along the west wall, playfields facing the aisle.
        new("Pinball Machine 01", LayoutKind.Machine, -2.67f, -3.40f, -180f, 0.86f, 0.50f, 1.4f),
        new("Pinball Machine 02", LayoutKind.Machine, -2.67f, -2.70f, -180f, 0.86f, 0.50f, 1.4f),
        // 03/04 are the same model turned 180 degrees in the package, so yaw 0 faces the aisle too.
        new("Pinball Machine 03", LayoutKind.Machine, -2.67f, -2.00f, 0f, 0.86f, 0.50f, 1.4f),
        new("Pinball Machine 04", LayoutKind.Machine, -2.67f, -1.30f, 0f, 0.86f, 0.50f, 1.4f),
        // Doom upright pair side by side against the south wall, screens facing north.
        new("Arcade Machine 01", LayoutKind.Machine, -0.95f, -3.62f, 90f, 0.46f, 0.40f, 1.6f),
        new("Arcade Machine 02", LayoutKind.Machine, -0.25f, -3.62f, -90f, 0.46f, 0.40f, 1.6f),
        // Open floor: cocktail table (with its two end stools) and air hockey.
        new("Table Top", LayoutKind.Machine, -0.50f, 0.00f, 90f, 0.92f, 1.62f, 0.7f),
        new("Air Hockey", LayoutKind.Machine, 1.62f, -1.10f, 0f, 0.90f, 1.80f, 1.0f),
    };

    /// <summary>Pac-Man end stools (part of the table model), room space.</summary>
    public static readonly LayoutSlot[] TableStools =
    {
        new("Pac-Man stool A", LayoutKind.Machine, -0.50f, 1.05f, 0f, 0.30f, 0.30f, 0.3f),
        new("Pac-Man stool B", LayoutKind.Machine, -0.50f, -1.05f, 0f, 0.30f, 0.30f, 0.3f),
    };

    /// <summary>The generated Arcade TV console (not moved).</summary>
    public static readonly LayoutSlot Tv = new("Arcade TV", LayoutKind.Machine, 1.32f, 3.71f, 0f, 1.46f, 0.62f, 1.9f);

    public static readonly LayoutSlot[] Furniture =
    {
        // Prize / change counter on the east wall with a till on top.
        new("prize counter", LayoutKind.Floor, 2.77f, 1.75f, -90f, 0.70f, 1.80f, 1.15f, "Counter", "Ornate Desk", "Frontdesk"),
        new("counter till", LayoutKind.OnTop, 2.80f, 1.75f, -90f, 0.45f, 0.45f, 0.40f, "CashCounter", "Cash Register"),
        new("prize shelf", LayoutKind.Floor, 2.85f, -3.45f, -90f, 0.50f, 0.90f, 2.0f, "Shop Rack", "WallMountedShelf_Built", "StorageRack_Small", "StorageRack_Medium"),
        new("drink fridge", LayoutKind.Floor, 2.80f, 3.45f, 180f, 0.65f, 0.75f, 2.1f, "Display Fridge", "Vending Machine", "Jukebox", "Jukebox_Built"),
        // TV lounge chairs facing the screen.
        new("TV chair 1", LayoutKind.Floor, 0.95f, 2.75f, 0f, 0.60f, 0.60f, 1.2f, "Chair_Business", "Chair", "Armchair"),
        new("TV chair 2", LayoutKind.Floor, 1.70f, 2.75f, 0f, 0.60f, 0.60f, 1.2f, "Chair_Business", "Chair", "Armchair"),
        // Bins and a plant.
        new("bin by the door", LayoutKind.Floor, -2.85f, 1.75f, 90f, 0.50f, 0.50f, 1.1f, "Trash Can Prop", "TrashCan_Built", "SmallTrashCan_Built", "Rubbish bin"),
        new("bin by the uprights", LayoutKind.Floor, 0.45f, -3.62f, 0f, 0.50f, 0.50f, 1.1f, "Trash Can Prop", "TrashCan_Built", "SmallTrashCan_Built", "Rubbish bin"),
        new("plant", LayoutKind.Floor, -2.85f, 1.05f, 0f, 0.50f, 0.50f, 1.6f, "Plant1", "Plant2 in pot", "Flowerpot", "Planter"),
        // Wall and ceiling dressing.
        new("wall clock", LayoutKind.Wall, -0.30f, InnerNorth, 180f, 0.45f, 0.10f, 2.55f, "Clock", "WallClock_Built"),
        new("sign over the counter", LayoutKind.Wall, InnerEast, 1.75f, -90f, 0.10f, 1.40f, 2.90f, "MetalSign_Built"),
        new("ceiling fan (table)", LayoutKind.Ceiling, -0.50f, 0.00f, 0f, 1.40f, 1.40f, 0.9f, "CeilingFan"),
        new("ceiling fan (air hockey)", LayoutKind.Ceiling, 1.62f, -1.10f, 0f, 1.40f, 1.40f, 0.9f, "CeilingFan"),
        new("tube light 1", LayoutKind.Ceiling, -2.20f, -2.35f, 0f, 1.40f, 0.40f, 0.35f, "Fluorescent_Light", "Light_Fluorescent_Light_LOD0"),
        new("tube light 2", LayoutKind.Ceiling, 1.80f, -2.20f, 0f, 1.40f, 0.40f, 0.35f, "Fluorescent_Light", "Light_Fluorescent_Light_LOD0"),
        new("tube light 3", LayoutKind.Ceiling, -1.90f, 0.90f, 0f, 1.40f, 0.40f, 0.35f, "Fluorescent_Light", "Light_Fluorescent_Light_LOD0"),
        new("tube light 4", LayoutKind.Ceiling, 1.80f, 1.40f, 0f, 1.40f, 0.40f, 0.35f, "Fluorescent_Light", "Light_Fluorescent_Light_LOD0"),
    };

    public static LayoutSlot? Machine(string modelName)
    {
        foreach (LayoutSlot slot in Machines)
            if (string.Equals(slot.Id, modelName, StringComparison.OrdinalIgnoreCase))
                return slot;
        return null;
    }

    /// <summary>Main aisles that must stay at least 1.0 m wide (slot/wall ids).</summary>
    public static readonly (string a, string b)[] Aisles =
    {
        ("Pinball Machine 01", "Arcade Machine 01"),
        ("Pinball Machine 01", "Table Top"),
        ("Pinball Machine 02", "Table Top"),
        ("Pinball Machine 03", "Table Top"),
        ("Pinball Machine 04", "Table Top"),
        ("Table Top", "Air Hockey"),
        ("Air Hockey", "east wall"),
        ("Air Hockey", "Arcade Machine 02"),
        ("Air Hockey", "prize counter"),
        ("Air Hockey", "prize shelf"),
        ("Pac-Man stool A", "TV chair 1"),
        ("Table Top", "TV chair 1"),
    };

    /// <summary>The walk-in doorway: centre of the room door on the chamfer and its inward normal.</summary>
    public static (float x, float z, float nx, float nz) Doorway(float doorT = 0.53f)
    {
        float x = ChamferAX + (ChamferBX - ChamferAX) * doorT;
        float z = ChamferAZ + (ChamferBZ - ChamferAZ) * doorT;
        float tx = ChamferBX - ChamferAX, tz = ChamferBZ - ChamferAZ;
        float len = MathF.Sqrt(tx * tx + tz * tz);
        return (x, z, tz / len, -tx / len);
    }
}

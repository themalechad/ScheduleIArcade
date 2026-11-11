using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ScheduleIArcade.Utils;

/// <summary>
/// 0.4.1 room checks: raised ceiling stays inside the Arcade building shell, one flat floor
/// collider meets the vestibule ramp without a lip, cabinet colliders are simple footprint boxes
/// with >= 1.0 m walkways on the main aisles, and no capsule / non-convex mesh colliders remain.
/// Prop positions and bounds come from the packaged raw mesh (dist/.../arcade_runtime_meshes.bin).
/// </summary>
internal static class RoomChecks
{
    private const float FXs = 1.22f, FZs = 1.34f;
    private static float FX(float x) => x * FXs;
    private static float FZ(float z) => z * FZs;

    public static int Run(Scene scene, long buildingGo, Vector3 roomOrigin, Quaternion roomRot, float ceilingH, float wallH, float floorTop, float rampRise, float rampDepth)
    {
        int failures = 0;
        string repo = FindRepoRoot();
        Console.WriteLine();
        Console.WriteLine("Room checks (0.4.1):");

        // --- Source constants and collider audit ----------------------------------------------
        string dims = File.ReadAllText(Path.Combine(repo, "src", "ScheduleIArcade", "Utils", "ArcadeDimensions.cs"));
        float srcCeiling = ReadConst(dims, "InteriorCeilingHeight"), srcWall = ReadConst(dims, "InteriorWallHeight");
        Console.WriteLine($"  ArcadeDimensions: ceiling {srcCeiling:0.00} m, walls {srcWall:0.00} m");
        if (MathF.Abs(srcCeiling - ceilingH) > 1e-4f || MathF.Abs(srcWall - wallH) > 1e-4f) { failures++; Console.WriteLine("  FAIL check constants differ from ArcadeDimensions.cs"); }

        string raw = File.ReadAllText(Path.Combine(repo, "src", "ScheduleIArcade", "Services", "RawArcadeSceneService.cs"));
        int meshColliders = Regex.Matches(raw, @"AddComponent<MeshCollider>").Count;
        int convex = Regex.Matches(raw, @"convex\s*=\s*true").Count;
        int capsules = Regex.Matches(raw, @"CapsuleCollider").Count;
        bool safetyFloor = raw.Contains("RawArcade_SafetyFloor");
        Console.WriteLine($"  colliders in the room builder: MeshCollider x{meshColliders} (convex x{convex}), CapsuleCollider x{capsules}, old safety floor {(safetyFloor ? "present" : "removed")}");
        if (meshColliders != 1 || convex != 1) { failures++; Console.WriteLine("  FAIL expected exactly one (convex, walkable floor) MeshCollider"); }
        if (capsules != 0) { failures++; Console.WriteLine("  FAIL capsule colliders remain"); }
        if (safetyFloor) { failures++; Console.WriteLine("  FAIL the stepped safety floor is still built"); }

        // --- Ceiling inside the host building -----------------------------------------------------
        long roofGo = scene.FindChild(buildingGo, "arcade/Roof");
        float roofY = roofGo != 0 ? scene.World(scene.TransformOf(roofGo)).Translation.Y : float.NaN;
        long foundation = scene.FindChild(buildingGo, "arcade/Foundation");
        float ceilingWorld = roomOrigin.Y + ceilingH;
        float collisionTop = roomOrigin.Y + ceilingH + 0.24f;
        float wallTop = roomOrigin.Y + wallH;
        Console.WriteLine($"  ceiling plane y {ceilingWorld:0.00} (room floor y {roomOrigin.Y + floorTop:0.00}, clear height {ceilingH - floorTop:0.00} m); walls to y {wallTop:0.00}; collision cap to y {collisionTop:0.00}; host roof y {roofY:0.00}");
        if (float.IsNaN(roofY) || collisionTop > roofY - 0.10f || wallTop > roofY - 0.10f) { failures++; Console.WriteLine("  FAIL ceiling/walls reach the building roof"); }

        // Room footprint corners inside the measured building interior.
        var outline = new[] { new Vector3(FX(-2.67f), 0, FZ(-3.02f)), new Vector3(FX(-2.67f), 0, FZ(1.56f)), new Vector3(FX(-0.84f), 0, FZ(3.06f)), new Vector3(FX(2.64f), 0, FZ(3.06f)), new Vector3(FX(2.64f), 0, FZ(-3.02f)) };
        foreach (Vector3 c in outline)
        {
            Vector3 w = roomOrigin + Vector3.Transform(c, roomRot);
            bool inside = w.X >= -52.40f - 0.05f && w.X <= -43.65f + 0.05f && w.Z >= 135.65f - 0.05f && w.Z <= 147.40f + 0.05f;
            if (!inside) { failures++; Console.WriteLine($"  FAIL room corner {w} outside the building interior"); }
        }
        Console.WriteLine($"  room outline inside building interior x -52.40..-43.65, z 135.65..147.40: {(failures == 0 ? "yes" : "see failures")}");

        // --- Floor / ramp lip -------------------------------------------------------------------
        float angle = MathF.Atan2(rampRise, rampDepth);
        float rampTopAtDoor = rampRise * 0.5f - 0.05f + 0.05f / MathF.Cos(angle) + (rampDepth * 0.5f) * MathF.Tan(angle);
        float lip = (rampTopAtDoor - rampRise) * 1000f;
        Console.WriteLine($"  vestibule ramp meets the single floor slab (top {floorTop:0.000}) with a {lip:0.0} mm step; slab edge = door line");
        if (MathF.Abs(lip) > 5f) { failures++; Console.WriteLine("  FAIL lip between ramp and floor"); }

        // --- 0.4.1 floor plan (ArcadeLayout, shared with the mod) -----------------------------------
        var props = ReadProps(Path.Combine(repo, "dist", "Mods", "ScheduleIArcade", "Assets", "RawScene", "arcade_runtime_meshes.bin"));
        var floor = new List<(string name, float minX, float maxX, float minZ, float maxZ, char glyph)>();
        static char GlyphFor(string id) => id.StartsWith("Pinball") ? 'P' : id.StartsWith("Arcade Machine") ? 'D' : id == "Table Top" ? 'T'
            : id.StartsWith("Pac-Man stool") ? 's' : id == "Air Hockey" ? 'H' : id == "Arcade TV" ? 'V' : id.Contains("counter") ? 'C'
            : id.Contains("shelf") ? 'S' : id.Contains("fridge") ? 'F' : id.Contains("chair") ? 'h' : id.Contains("bin") ? 'b' : id.Contains("plant") ? 'p' : '?';
        foreach (LayoutSlot slot in ArcadeLayout.Machines.Concat(ArcadeLayout.TableStools).Append(ArcadeLayout.Tv).Concat(ArcadeLayout.Furniture.Where(f => f.Kind == LayoutKind.Floor)))
            floor.Add((slot.Id, slot.MinX, slot.MaxX, slot.MinZ, slot.MaxZ, GlyphFor(slot.Id)));

        // Machine slots must contain the real model footprint (packaged mesh bounds turned by the slot yaw).
        foreach (LayoutSlot slot in ArcadeLayout.Machines)
        {
            var p = props.FirstOrDefault(x => x.name == slot.Id);
            if (p.name == null) { failures++; Console.WriteLine($"  FAIL {slot.Id} not in the mesh package"); continue; }
            float r = slot.Yaw * MathF.PI / 180f;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            Vector2 body = slot.Id == "Table Top" ? new Vector2(0.75f, 0.50f) : slot.Id == "Air Hockey" ? new Vector2(0.47f, 0.92f) : new Vector2(float.NaN, 0);
            for (int i = 0; i < 4; i++)
            {
                float lx = float.IsNaN(body.X) ? ((i & 1) == 0 ? p.min.X : p.max.X) : ((i & 1) == 0 ? -body.X : body.X) - (slot.Id == "Table Top" ? 0.05f : 0f);
                float lz = float.IsNaN(body.X) ? ((i & 2) == 0 ? p.min.Z : p.max.Z) : ((i & 2) == 0 ? -body.Y : body.Y);
                float wx = slot.X + lx * MathF.Cos(r) + lz * MathF.Sin(r), wz = slot.Z - lx * MathF.Sin(r) + lz * MathF.Cos(r);
                minX = MathF.Min(minX, wx); maxX = MathF.Max(maxX, wx); minZ = MathF.Min(minZ, wz); maxZ = MathF.Max(maxZ, wz);
            }
            bool fits = minX >= slot.MinX - 0.08f && maxX <= slot.MaxX + 0.08f && minZ >= slot.MinZ - 0.08f && maxZ <= slot.MaxZ + 0.08f;
            Console.WriteLine($"  {slot.Id,-20} model x {minX:0.00}..{maxX:0.00} z {minZ:0.00}..{maxZ:0.00} (collider slot x {slot.MinX:0.00}..{slot.MaxX:0.00} z {slot.MinZ:0.00}..{slot.MaxZ:0.00}){(fits ? "" : "  <-- model exceeds slot")}");
            if (!fits) { failures++; Console.WriteLine("  FAIL machine model larger than its collider slot"); }
            // Model must be inside the room (walls are 2 cm tolerance: cabinets stand against them).
            if (minX < ArcadeLayout.InnerWest - 0.02f || maxX > ArcadeLayout.InnerEast + 0.02f || minZ < ArcadeLayout.InnerSouth - 0.02f || maxZ > ArcadeLayout.InnerNorth + 0.02f)
            { failures++; Console.WriteLine($"  FAIL {slot.Id} model pokes through a wall"); }
        }

        // Every floor footprint inside the room (rectangle + chamfer half-plane) and no overlaps.
        var (dx, dz, nx, nz) = ArcadeLayout.Doorway();
        float chamferHalfThickness = 0.18f * FXs * 0.5f;
        bool InsideRoom(float x, float z)
        {
            if (x < ArcadeLayout.InnerWest - 0.02f || x > ArcadeLayout.InnerEast + 0.02f || z < ArcadeLayout.InnerSouth - 0.02f || z > ArcadeLayout.InnerNorth + 0.07f)
                return false;
            return (x - ArcadeLayout.ChamferAX) * nx + (z - ArcadeLayout.ChamferAZ) * nz >= chamferHalfThickness - 0.02f;
        }
        foreach (var f in floor)
        {
            bool ok = InsideRoom(f.minX, f.minZ) && InsideRoom(f.minX, f.maxZ) && InsideRoom(f.maxX, f.minZ) && InsideRoom(f.maxX, f.maxZ);
            if (!ok) { failures++; Console.WriteLine($"  FAIL {f.name} footprint leaves the room"); }
        }
        for (int i = 0; i < floor.Count; i++)
            for (int j = i + 1; j < floor.Count; j++)
            {
                var a = floor[i]; var b = floor[j];
                bool overlap = a.minX < b.maxX - 0.005f && b.minX < a.maxX - 0.005f && a.minZ < b.maxZ - 0.005f && b.minZ < a.maxZ - 0.005f;
                bool tableAndStool = (a.name == "Table Top" && b.name.StartsWith("Pac-Man stool")) || (b.name == "Table Top" && a.name.StartsWith("Pac-Man stool"));
                if (overlap && !tableAndStool) { failures++; Console.WriteLine($"  FAIL {a.name} overlaps {b.name}"); }
            }
        Console.WriteLine($"  floor items: {floor.Count} footprints inside the room, no overlaps");

        // Main aisles >= 1.0 m.
        float Gap(string a, string b)
        {
            var A = floor.First(x => x.name == a);
            if (b == "east wall") return ArcadeLayout.InnerEast - A.maxX;
            var B = floor.First(x => x.name == b);
            float gx = MathF.Max(0f, MathF.Max(A.minX - B.maxX, B.minX - A.maxX));
            float gz = MathF.Max(0f, MathF.Max(A.minZ - B.maxZ, B.minZ - A.maxZ));
            return MathF.Sqrt(gx * gx + gz * gz);
        }
        foreach (var (a, b) in ArcadeLayout.Aisles)
        {
            float g = Gap(a, b);
            Console.WriteLine($"  walkway {a} <-> {b}: {g:0.00} m");
            if (g < 1.0f - 1e-3f) { failures++; Console.WriteLine("  FAIL walkway narrower than 1.0 m"); }
        }

        // Doorway: a 1.2 m wide, 2.0 m deep corridor straight in from the room door is clear.
        int blocked = 0;
        for (float s = 0f; s <= 2.0f; s += 0.05f)
            for (float w = -0.6f; w <= 0.6f; w += 0.05f)
            {
                float x = dx + nx * s + (-nz) * w, z = dz + nz * s + nx * w;
                foreach (var f in floor)
                    if (x > f.minX && x < f.maxX && z > f.minZ && z < f.maxZ) { blocked++; Console.WriteLine($"  FAIL doorway corridor blocked by {f.name}"); goto next; }
                next:;
            }
        if (blocked > 0) failures++;
        Console.WriteLine($"  doorway corridor (1.2 m x 2.0 m inward from the room door at ({dx:0.00},{dz:0.00})): {(blocked == 0 ? "clear" : "BLOCKED")}");

        // Wall / ceiling props stay on their walls and under the ceiling.
        foreach (LayoutSlot slot in ArcadeLayout.Furniture.Where(f => f.Kind is LayoutKind.Wall or LayoutKind.Ceiling))
        {
            bool ok = slot.Kind == LayoutKind.Ceiling
                ? slot.MinX >= ArcadeLayout.InnerWest && slot.MaxX <= ArcadeLayout.InnerEast && slot.MinZ >= ArcadeLayout.InnerSouth && slot.MaxZ <= ArcadeLayout.InnerNorth && slot.MaxHeight < ceilingH - floorTop - 2.0f
                : slot.MaxHeight + 0.45f < ceilingH && (MathF.Abs(slot.Z - ArcadeLayout.InnerNorth) < 0.01f || MathF.Abs(slot.X - ArcadeLayout.InnerEast) < 0.01f || MathF.Abs(slot.Z - ArcadeLayout.InnerSouth) < 0.01f || MathF.Abs(slot.X - ArcadeLayout.InnerWest) < 0.01f);
            if (!ok) { failures++; Console.WriteLine($"  FAIL {slot.Id} slot is off its wall/ceiling"); }
        }

        // Top-down sketch, 0.25 m cells. North (+Z, TV wall) at the top, west (-X, pinball wall) left.
        Console.WriteLine("  Floor plan (north up, 0.25 m cells): P pinball, D Doom upright, T Pac-Man table, s stool, H air hockey,");
        Console.WriteLine("  V TV, C prize counter, S prize shelf, F fridge/jukebox, h chair, b bin, p plant, / entrance chamfer, * door");
        for (float z = ArcadeLayout.InnerNorth - 0.125f; z > ArcadeLayout.InnerSouth; z -= 0.25f)
        {
            var line = new StringBuilder("  |");
            for (float x = ArcadeLayout.InnerWest + 0.125f; x < ArcadeLayout.InnerEast; x += 0.25f)
            {
                char c = '.';
                float side = (x - ArcadeLayout.ChamferAX) * nx + (z - ArcadeLayout.ChamferAZ) * nz;
                if (side < 0f) c = ' ';
                else if (side < 0.25f) c = MathF.Abs((x - dx) * (-nz) + (z - dz) * nx) < 0.95f ? '*' : '/';
                foreach (var f in floor)
                    if (x > f.minX && x < f.maxX && z > f.minZ && z < f.maxZ) { c = f.glyph; break; }
                line.Append(c);
            }
            Console.WriteLine(line.Append('|').ToString());
        }
        return failures;
    }

    private static float ReadConst(string source, string name)
    {
        Match m = Regex.Match(source, name + @"\s*=\s*([0-9.]+)f");
        return m.Success ? float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : float.NaN;
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "ScheduleIArcade")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? Directory.GetCurrentDirectory();
    }

    private static List<(string name, Vector3 pos, float yaw, Vector3 min, Vector3 max)> ReadProps(string path)
    {
        var list = new List<(string, Vector3, float, Vector3, Vector3)>();
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(8);
        int version = r.ReadInt32(), count = r.ReadInt32();
        string S() { int n = r.ReadInt32(); return Encoding.UTF8.GetString(r.ReadBytes(n)); }
        for (int m = 0; m < count; m++)
        {
            string name = S(); S(); S(); S();
            Vector3 pos = Vector3.Zero; float yaw = 0;
            if (version >= 2) { pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); yaw = r.ReadSingle(); }
            int vc = r.ReadInt32();
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = 0; i < vc; i++) { var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            r.ReadBytes(vc * 12 + vc * 8);
            int ic = r.ReadInt32();
            r.ReadBytes(ic * 4);
            list.Add((name, pos, yaw, min, max));
        }
        return list;
    }

    private static float PolyDistance(Vector2[] a, Vector2[] b)
    {
        static float SegDist(Vector2 p, Vector2 s0, Vector2 s1)
        {
            Vector2 e = s1 - s0;
            float t = Math.Clamp(Vector2.Dot(p - s0, e) / MathF.Max(1e-9f, e.LengthSquared()), 0f, 1f);
            return Vector2.Distance(p, s0 + e * t);
        }
        static bool Inside(Vector2 p, Vector2[] poly)
        {
            bool sign = false, first = true;
            for (int i = 0; i < poly.Length; i++)
            {
                Vector2 e = poly[(i + 1) % poly.Length] - poly[i];
                float c = e.X * (p.Y - poly[i].Y) - e.Y * (p.X - poly[i].X);
                if (first) { sign = c >= 0; first = false; }
                else if ((c >= 0) != sign) return false;
            }
            return true;
        }
        if (a.Any(p => Inside(p, b)) || b.Any(p => Inside(p, a)))
            return 0f;
        float best = float.MaxValue;
        foreach (Vector2 p in a)
            for (int i = 0; i < b.Length; i++) best = MathF.Min(best, SegDist(p, b[i], b[(i + 1) % b.Length]));
        foreach (Vector2 p in b)
            for (int i = 0; i < a.Length; i++) best = MathF.Min(best, SegDist(p, a[i], a[(i + 1) % a.Length]));
        return best;
    }
}

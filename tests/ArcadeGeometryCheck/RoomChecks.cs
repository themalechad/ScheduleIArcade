using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

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

        // --- Cabinet colliders and walkways --------------------------------------------------------
        var props = ReadProps(Path.Combine(repo, "dist", "Mods", "ScheduleIArcade", "Assets", "RawScene", "arcade_runtime_meshes.bin"));
        var boxes = new List<(string name, Vector2[] poly)>();
        void AddBox(string name, Vector2 centre, float yawDeg, Vector2 localCentre, Vector2 size)
        {
            float r = yawDeg * MathF.PI / 180f;
            // Unity yaw: local (x, z) -> world (x cos + z sin, -x sin + z cos).
            Vector2 Rot(Vector2 v) => new(v.X * MathF.Cos(r) + v.Y * MathF.Sin(r), -v.X * MathF.Sin(r) + v.Y * MathF.Cos(r));
            var pts = new[] { new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f) }
                .Select(c => centre + Rot(localCentre + c * size)).ToArray();
            boxes.Add((name, pts));
        }
        foreach (var p in props)
        {
            Vector2 pos = new(p.pos.X, p.pos.Z);
            if (p.name == "Table Top") pos.X = -0.27f;
            if (p.name == "Air Hockey") pos.X = 1.67f;
            Vector2 bc = new((p.min.X + p.max.X) * 0.5f, (p.min.Z + p.max.Z) * 0.5f);
            Vector2 bs = new(p.max.X - p.min.X, p.max.Z - p.min.Z);
            if (p.name.StartsWith("Pinball Machine")) AddBox(p.name, pos, p.yaw, bc, bs - new Vector2(0.08f, 0.08f));
            else if (p.name == "Table Top")
            {
                AddBox("Pac-Man table", pos, p.yaw, new Vector2(-0.05f, 0f), new Vector2(1.42f, 0.92f));
                AddBox("Pac-Man stool A", pos, p.yaw, new Vector2(-1.05f, 0f), new Vector2(0.30f, 0.30f));
                AddBox("Pac-Man stool B", pos, p.yaw, new Vector2(1.05f, 0f), new Vector2(0.30f, 0.30f));
            }
            else if (p.name == "Air Hockey") AddBox("Air hockey", pos, p.yaw, Vector2.Zero, new Vector2(0.90f, 1.80f));
            else if (p.name.StartsWith("Arcade Machine")) AddBox(p.name, pos, p.yaw, bc, bs - new Vector2(0.12f, 0.12f));
            // Crane Game / Vending Machine are hidden and have no collider.
        }
        AddBox("Arcade TV", new Vector2(1.32f, 3.70f), 0f, new Vector2(0f, 0.01f), new Vector2(1.46f, 0.62f));
        float westInner = FX(-2.67f) + 0.18f * FXs * 0.5f, eastInner = FX(2.64f) - 0.16f * FXs * 0.5f;
        float southInner = FZ(-3.02f) + 0.16f * FZs * 0.5f, northInner = FZ(3.06f) - 0.18f * FZs * 0.5f;
        boxes.Add(("west wall", new[] { new Vector2(westInner - 0.2f, southInner), new Vector2(westInner, southInner), new Vector2(westInner, FZ(1.56f)), new Vector2(westInner - 0.2f, FZ(1.56f)) }));
        boxes.Add(("east wall", new[] { new Vector2(eastInner, southInner), new Vector2(eastInner + 0.2f, southInner), new Vector2(eastInner + 0.2f, northInner), new Vector2(eastInner, northInner) }));

        float Gap(string a, string b) => PolyDistance(boxes.First(x => x.name == a).poly, boxes.First(x => x.name == b).poly);
        var aisles = new (string a, string b)[]
        {
            ("Pac-Man table", "Air hockey"),
            ("Air hockey", "east wall"),
            ("Pac-Man table", "Pinball Machine 01"),
            ("Pac-Man table", "Pinball Machine 02"),
        };
        foreach (var (a, b) in aisles)
        {
            float g = Gap(a, b);
            Console.WriteLine($"  walkway {a} <-> {b}: {g:0.00} m");
            if (g < 1.0f - 1e-3f) { failures++; Console.WriteLine("  FAIL walkway narrower than 1.0 m"); }
        }
        // Report any other pair closer than 1 m (side-by-side machines are expected).
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                float g = PolyDistance(boxes[i].poly, boxes[j].poly);
                if (g < 1.0f && !aisles.Any(x => (x.a == boxes[i].name && x.b == boxes[j].name)))
                    Console.WriteLine($"    (info) {boxes[i].name} <-> {boxes[j].name}: {g:0.00} m {(g <= 0f ? "touching/back-to-back" : "")}");
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

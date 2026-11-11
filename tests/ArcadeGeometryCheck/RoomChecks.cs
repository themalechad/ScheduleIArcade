using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using ScheduleIArcade.Utils;

/// <summary>
/// 0.6.0 room checks: the whole ground floor of Arcade (1) is the arcade, one solid closed shell, one
/// door set opening straight into the room at a level threshold, nothing in the swing, 1.0 m walkways,
/// and the game's own NPC navmesh reaches the door.
/// </summary>
internal static class RoomChecks
{
    private static int _failures;
    private static void Fail(string m) { _failures++; Console.WriteLine("  FAIL " + m); }

    public static int Run(Scene scene, long buildingGo, List<Box> nearBoxes, Vector3 roomOrigin, Quaternion roomRot,
        List<Box> shellBoxes, List<Box> machineBoxes, List<Box> leaves, List<Vector3> hinges, List<Vector3> leafCentres,
        Vector3 doorCenter, Vector3 inward, Vector3 along, float doorwayHeight, string level1)
    {
        _failures = 0;
        string repo = FindRepoRoot();
        Vector3 ToWorld(float x, float y, float z) => roomOrigin + Vector3.Transform(new Vector3(x, y, z), roomRot);
        Vector3 ToLocal(Vector3 w) => Vector3.Transform(w - roomOrigin, Quaternion.Inverse(roomRot));

        // ---- 1. Building measurements vs ArcadeLayout ------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("Building (re-measured from level1):");
        var building = scene.SolidBoxesUnder(buildingGo).ToList();
        bool Ground(Box b) => b.MinY < -3.8f && b.MaxY > -0.5f && b.MaxY < -0.3f;
        var ground = building.Where(Ground).Where(b => b.Path.Contains("Wall") || b.Path.Contains("CornerDoor")).ToList();
        float outerWest = ground.Where(b => Aabb.Of(b.Corners()).Max.X < -52f).Min(b => Aabb.Of(b.Corners()).Min.X);
        float innerWest = ground.Where(b => Aabb.Of(b.Corners()).Max.X < -52f).Max(b => Aabb.Of(b.Corners()).Max.X);
        float outerEast = ground.Where(b => Aabb.Of(b.Corners()).Min.X > -43.8f).Max(b => Aabb.Of(b.Corners()).Max.X);
        float innerEast = ground.Where(b => Aabb.Of(b.Corners()).Min.X > -43.8f).Min(b => Aabb.Of(b.Corners()).Min.X);
        float outerNorth = ground.Where(b => Aabb.Of(b.Corners()).Min.Z > 147f).Max(b => Aabb.Of(b.Corners()).Max.Z);
        float innerNorth = ground.Where(b => Aabb.Of(b.Corners()).Min.Z > 147f).Min(b => Aabb.Of(b.Corners()).Min.Z);
        float outerSouth = ground.Where(b => Aabb.Of(b.Corners()).Max.Z < 136f && !b.Path.Contains("CornerDoor")).Min(b => Aabb.Of(b.Corners()).Min.Z);
        float innerSouth = ground.Where(b => Aabb.Of(b.Corners()).Max.Z < 136f && !b.Path.Contains("CornerDoor")).Max(b => Aabb.Of(b.Corners()).Max.Z);
        float foundationTop = building.Where(b => b.Path.EndsWith("arcade/Foundation")).Max(b => b.MaxY);
        float firstFloor = building.Where(b => b.Path.Contains("Upper")).Min(b => b.MinY);
        long roofGo = scene.FindChild(buildingGo, "arcade/Roof");
        float roofY = roofGo != 0 ? scene.World(scene.TransformOf(roofGo)).Translation.Y : float.NaN;
        Console.WriteLine($"  wall colliders: west x {outerWest:0.00}..{innerWest:0.00}, east x {innerEast:0.00}..{outerEast:0.00}, north z {innerNorth:0.00}..{outerNorth:0.00}, south z {outerSouth:0.00}..{innerSouth:0.00}");
        Console.WriteLine($"  foundation top y {foundationTop:0.000}; upper storey from y {firstFloor:0.000}; roof y {roofY:0.00}");
        if (MathF.Abs(foundationTop - ArcadeLayout.FloorWorldY) > 0.005f) Fail("floor is not the foundation/sidewalk height");
        if (MathF.Abs(firstFloor - ArcadeLayout.FirstFloorWorldY) > 0.01f) Fail("first-floor line differs from ArcadeLayout");

        // ---- 2. Interior fills the building, flush within the wall thickness ---------------------------
        Console.WriteLine("Interior bounds (lining inner face / lining outer face / vanilla inner collider face):");
        var (lw, _) = ArcadeLayout.ToWorld(ArcadeLayout.InnerEast, 0f);
        var (lwo, _) = ArcadeLayout.ToWorld(ArcadeLayout.InnerEast + ArcadeLayout.LiningWest, 0f);
        var (le, _) = ArcadeLayout.ToWorld(ArcadeLayout.InnerWest, 0f);
        var (leo, _) = ArcadeLayout.ToWorld(ArcadeLayout.InnerWest - ArcadeLayout.LiningWindowWall, 0f);
        var (_, ln) = ArcadeLayout.ToWorld(0f, ArcadeLayout.InnerSouth);
        var (_, lno) = ArcadeLayout.ToWorld(0f, ArcadeLayout.InnerSouth - ArcadeLayout.LiningNorth);
        var (_, ls) = ArcadeLayout.ToWorld(0f, ArcadeLayout.InnerNorth);
        var (_, lso) = ArcadeLayout.ToWorld(0f, ArcadeLayout.InnerNorth + ArcadeLayout.LiningWindowWall);
        var sides = new (string name, float inner, float liningOuter, float vanillaInner, float vanillaOuter, float sign)[]
        {
            ("west (blank, back door behind)", lw, lwo, -52.40f, outerWest, -1f),
            ("east (street windows)", le, leo, innerEast, outerEast, 1f),
            ("north (blank)", ln, lno, innerNorth, outerNorth, 1f),
            ("south (street windows)", ls, lso, innerSouth, outerSouth, -1f),
        };
        foreach (var s in sides)
        {
            float cavity = (s.vanillaInner - s.liningOuter) * s.sign; // >0 = hidden gap between the vanilla wall and the lining
            float thickness = MathF.Abs(s.vanillaOuter - s.inner);
            Console.WriteLine($"  {s.name,-32} inner {s.inner:0.000}, lining outer {s.liningOuter:0.000}, vanilla inner {s.vanillaInner:0.000}: cavity {cavity * 1000:0} mm, total wall {thickness:0.00} m");
            if (cavity > 0.05f) Fail($"{s.name}: walkable/visible cavity between the lining and the building wall");
            if (thickness < 0.2f || thickness > 0.36f) Fail($"{s.name}: wall is not a single 0.2-0.35 m layer");
        }
        {
            // Chamfer: vanilla CornerDoor inner face (centre line + 0.1) vs lining outer face.
            float centreLine = Vector3.Dot(Flat(doorCenter), inward);
            Vector3 aLocal = new(ArcadeLayout.ChamferAX, 0, ArcadeLayout.ChamferAZ);
            float liningInner = Vector3.Dot(Flat(ToWorld(aLocal.X, 0, aLocal.Z)), inward);
            float liningOuter = liningInner - ArcadeLayout.LiningChamfer;
            Console.WriteLine($"  chamfer (entrance): leaf/glass plane to lining inner face {liningInner - centreLine:0.000} m (no vestibule: < 0.30 m), lining outer face {(liningOuter - centreLine) * 1000:0} mm behind the glass plane");
            if (liningInner - centreLine > 0.30f) Fail("the room starts more than 0.3 m behind the door: vestibule");
            if (liningOuter - centreLine < 0.015f) Fail("chamfer lining would hit the glass/door plane");
        }
        // Building inner floor = inner rectangle minus the outside triangle cut off by the corner door.
        float leg = innerEast - (innerSouth - 182.212f);
        float buildingArea = (innerEast - innerWest) * (innerNorth - innerSouth) - 0.5f * leg * leg;
        float interiorArea = PolyArea(ArcadeLayout.FloorOutline.Select(p => new Vector2(p.x, p.z)).ToArray());
        Console.WriteLine($"  interior floor {interiorArea:0.0} m^2 of the building's {buildingArea:0.0} m^2 inner floor = {interiorArea / buildingArea * 100f:0}% (the rest is the wall lining)");
        if (interiorArea / buildingArea < 0.92f) Fail("interior does not fill the building (leftover rooms/space)");
        var strays = building.Where(b => b.Size.Length() < 6f && !Ground(b) && !b.Path.Contains("Foundation") && !b.Path.Contains("Upper") && !b.Path.Contains("CornerDoor") && !b.Path.Contains("Industrial"))
            .Where(b => { var l = ToLocal(b.WorldCenter); return ArcadeLayout.IsInsideInterior(l.X, l.Z) && b.WorldCenter.Y < -0.4f; }).ToList();
        Console.WriteLine($"  vanilla solid colliders inside the interior (the mod hides these at runtime): {strays.Count}{(strays.Count > 0 ? " - " + string.Join(", ", strays.Select(s => s.Name)) : "")}");

        // ---- 3. Single ground floor, ceiling inside the roof ------------------------------------------
        float ceilingUnder = ArcadeLayout.WorldOriginY + ArcadeLayout.CeilingY, ceilingTop = ArcadeLayout.WorldOriginY + ArcadeLayout.CeilingTopY;
        Console.WriteLine($"Ceiling: underside y {ceilingUnder:0.00}, slab top y {ceilingTop:0.00} (first floor {firstFloor:0.00}, roof {roofY:0.00}); clear height {ceilingUnder - foundationTop:0.00} m; walls to y {shellBoxes.Max(b => b.MaxY):0.00}");
        if (ceilingTop > firstFloor + 0.005f) Fail("ceiling pokes into the upper storey");
        if (shellBoxes.Max(b => b.MaxY) > firstFloor + 0.005f) Fail("walls poke into the upper storey");
        if (float.IsNaN(roofY) || ceilingTop > roofY - 1f) Fail("ceiling reaches the roof");
        if (ArcadeLayout.FloorItems().Any(f => f.MaxHeight > ArcadeDimensions.InteriorCeilingHeight - 0.5f)) Fail("an item reaches the ceiling (no upper access)");
        string shellSource = File.ReadAllText(Path.Combine(repo, "src", "ScheduleIArcade", "Services", "ArcadeShellBuilder.cs"));
        string rawSource = File.ReadAllText(Path.Combine(repo, "src", "ScheduleIArcade", "Services", "RawArcadeSceneService.cs"));
        string walkSource = File.ReadAllText(Path.Combine(repo, "src", "ScheduleIArcade", "Services", "ArcadeWalkInService.cs"));
        foreach (string word in new[] { "Stair", "stair", "UpperFloor", "SecondFloor" })
            if (shellSource.Contains(word) || rawSource.Contains(word)) Fail("an upper floor / stairs is built: " + word);

        // ---- 4. Closed opaque shell, no gaps ----------------------------------------------------------
        int holes = 0, samples = 0;
        var pieces = ArcadeLayout.WallPieces();
        foreach (WallLining wall in ArcadeLayout.Walls)
        {
            for (float a = 0.01f; a < wall.Length - 0.01f; a += 0.05f)
            {
                for (float y = ArcadeLayout.FloorTop + 0.01f; y < ArcadeLayout.CeilingTopY - 0.01f; y += 0.05f)
                {
                    samples++;
                    bool opening = wall.Openings.Any(o => a > o.AlongMin && a < o.AlongMax && y > o.Bottom && y < o.Top);
                    bool solid = pieces.Any(p => p.Wall == wall && a >= p.A0 - 0.001f && a <= p.A1 + 0.001f && y >= p.Y0 - 0.001f && y <= p.Y1 + 0.001f);
                    if (!opening && !solid) holes++;
                }
            }
        }
        float minThick = pieces.Min(p => p.Size.x);
        Console.WriteLine($"Shell: {pieces.Count} solid wall pieces (min thickness {minThick:0.00} m, opaque both sides), {samples} wall samples, {holes} hole(s); floor and ceiling are closed slabs over the outer outline");
        if (holes > 0) Fail("hole(s) in the wall lining");
        if (minThick < 0.10f) Fail("one-sided / paper-thin wall piece");
        for (int i = 0; i < ArcadeLayout.Walls.Length; i++)
        {
            var a = ArcadeLayout.Walls[i];
            var b = ArcadeLayout.Walls[(i + 1) % ArcadeLayout.Walls.Length];
            if (MathF.Abs(a.BX - b.AX) > 1e-3f || MathF.Abs(a.BZ - b.AZ) > 1e-3f) Fail($"gap between {a.Id} and {b.Id}");
        }
        if (!Regex.IsMatch(shellSource, @"SetFloat\(""_Cull"", 0f\)")) Fail("shell materials are not double-sided");
        if (Regex.IsMatch(shellSource, @"PrimitiveType\.Quad")) Fail("shell uses one-sided quads");
        if (!Regex.IsMatch(shellSource, @"Prism\(""ScheduleIArcade_Ceiling"", outer, ArcadeLayout\.CeilingY, ArcadeLayout\.CeilingTopY")) Fail("ceiling is not a closed slab");
        // Every window opening sits on a vanilla ground-floor window bay (glass 2.5 m, y -3.25..-1.05).
        var bays = building.Where(b => Ground(b) && Regex.IsMatch(b.Path, @"/WindowWall( \(\d\))?$")).Select(b => b.WorldCenter).ToList();
        int windows = 0;
        foreach (WallLining wall in ArcadeLayout.Walls)
            foreach (WallOpening o in wall.Openings.Where(o => o.Kind == "window"))
            {
                windows++;
                var (tx, tz) = wall.Tangent;
                float mid = (o.AlongMin + o.AlongMax) * 0.5f;
                Vector3 c = ToWorld(wall.AX + tx * mid, 0, wall.AZ + tz * mid);
                float best = bays.Min(b => Vector3.Distance(Flat(b), Flat(c)));
                // Clipped corner windows are off-centre by the clip; the opening must still lie on the bay.
                if (best > 1.6f) Fail($"window opening on {wall.Id} at {V(c)} is not on a vanilla window bay");
            }
        Console.WriteLine($"  {windows} window openings over {bays.Count} vanilla ground-floor window bays (glass made clear at runtime)");

        // ---- 5. Exactly one entrance door set, no vestibule --------------------------------------------
        int doorPairs = Regex.Matches(walkSource, @"new DoorPair\(").Count;
        bool vestibule = walkSource.Contains("BuildVestibule") || walkSource.Contains("ARCADE_WALKIN_VESTIBULE") || walkSource.Contains("\"ARCADE DOOR\"");
        bool innerDoor = rawSource.Contains("DoubleDoor") || rawSource.Contains("RoomDoorHinge");
        int doorOpenings = ArcadeLayout.Walls.Sum(w => w.Openings.Count(o => o.Kind == "door"));
        Console.WriteLine($"Door sets: {doorPairs} walk-in door pair(s), {doorOpenings} door opening(s), inner room door {(innerDoor ? "PRESENT" : "none")}, vestibule {(vestibule ? "PRESENT" : "none")}");
        if (doorPairs != 1 || doorOpenings != 1) Fail("not exactly one entrance door set");
        if (vestibule || innerDoor) Fail("vestibule / second door still built");

        // ---- 6. Level threshold, nothing in the swing arc -------------------------------------------------
        float carpetTop = ArcadeLayout.WorldOriginY + ArcadeLayout.FloorTop + 0.003f;
        float leafBottom = leaves.Min(l => l.MinY) + 0.006f;
        Console.WriteLine($"Threshold: sidewalk/foundation y {foundationTop:0.000}, collider floor y {ArcadeLayout.WorldOriginY + ArcadeLayout.FloorTop:0.000}, carpet top y {carpetTop:0.000}, lifted leaf bottom y {leafBottom:0.000} ({(leafBottom - carpetTop) * 1000:0.0} mm clear); ramp {(shellSource.Contains("Ramp") || walkSource.Contains("Ramp") ? "PRESENT" : "none")}");
        if (MathF.Abs(ArcadeLayout.WorldOriginY + ArcadeLayout.FloorTop - foundationTop) > 0.005f) Fail("floor not level with the sidewalk");
        if (leafBottom <= carpetTop) Fail("door leaves scrape the floor");
        if (shellSource.Contains("Ramp") || walkSource.Contains("Ramp")) Fail("a ramp is still built");
        var swingObstacles = new List<Box>(shellBoxes);
        swingObstacles.AddRange(machineBoxes);
        swingObstacles.AddRange(nearBoxes.Where(b => !leaves.Any(l => ReferenceEquals(l.Source, b.Source)) && !b.Path.EndsWith("arcade/CornerDoor") && !b.Path.Contains("Foundation")));
        int hits = 0;
        var hitNames = new HashSet<string>();
        for (int i = 0; i < leaves.Count; i++)
        {
            foreach (Vector3 side in new[] { inward, -inward })
            {
                for (float deg = 0f; deg <= 88f; deg += 4f)
                {
                    Box leaf = leaves[i].RotatedAbout(hinges[i], ChooseSign(hinges[i], leafCentres[i], side, deg), "leaf");
                    foreach (Vector3 p in leaf.InteriorSamples(0.012f))
                    {
                        if (p.Y < leafBottom - 0.001f) continue;
                        foreach (Box o in swingObstacles)
                        {
                            if (o.Contains(p))
                            {
                                hits++;
                                hitNames.Add(o.Name);
                                break;
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine($"Door swing (both leaves, 0..88 deg both ways): {hits} sample(s) inside geometry{(hitNames.Count > 0 ? ": " + string.Join(", ", hitNames) : "")}");
        if (hits > 0) Fail("geometry in the door swing arc");

        // ---- 7. Walkways >= 1.0 m from the door to every machine; doorway clear ----------------------------
        var (dx, dz, nx, nz) = ArcadeLayout.Doorway();
        int blocked = 0;
        for (float s = 0.3f; s <= 2.0f; s += 0.05f)
            for (float w = -0.6f; w <= 0.6f; w += 0.05f)
            {
                float x = dx + nx * s + (-nz) * w, z = dz + nz * s + nx * w;
                if (ArcadeLayout.FloorItems().Any(f => x > f.MinX && x < f.MaxX && z > f.MinZ && z < f.MaxZ)) blocked++;
            }
        Console.WriteLine($"Doorway corridor (1.2 m x 2.0 m straight in from the door): {(blocked == 0 ? "clear" : "BLOCKED")}");
        if (blocked > 0) Fail("doorway corridor blocked");
        var walk = new Walkway(ArcadeLayout.MinWalkwayRadius);
        var startLocal = (x: dx + nx * 0.9f, z: dz + nz * 0.9f);
        foreach (var (id, px, pz) in ArcadeLayout.PlayPositions())
        {
            var r = walk.Reach(startLocal, (px, pz));
            Console.WriteLine($"  1.0 m walkway door -> {id,-24} {(r.found ? $"OK ({r.length:0.0} m)" : "BLOCKED")}");
            if (!r.found) Fail("no 1.0 m walkway to " + id);
        }

        // ---- 8. The game's NPC navmesh reaches the door -----------------------------------------------------
        NavMeshCheck(level1, doorCenter, inward);

        // ---- Floor plan sketch ----------------------------------------------------------------------------
        Console.WriteLine("Floor plan (room local, 0.25 m cells; top = +Z street-side south wall, left = -X street-side east wall):");
        Console.WriteLine("  P pinball, D Doom, T Pac-Man table, s stool, H air hockey, V TV, C prize counter, * door, / chamfer wall, w window");
        for (float z = ArcadeLayout.InnerNorth - 0.125f; z > ArcadeLayout.InnerSouth; z -= 0.25f)
        {
            var line = new StringBuilder("  ");
            line.Append(z > 0.25f && z < 2.75f || z > -2.75f && z < -0.25f || z > -5.75f && z < -3.25f ? 'w' : '|');
            for (float x = ArcadeLayout.InnerWest + 0.125f; x < ArcadeLayout.InnerEast; x += 0.25f)
            {
                char c = '.';
                if (!ArcadeLayout.IsInsideInterior(x, z)) c = ' ';
                else if (!ArcadeLayout.IsInsideInterior(x, z, 0.25f) && z - x > ArcadeLayout.ChamferC - 0.4f)
                    c = MathF.Abs((x - dx) * (-nz) + (z - dz) * nx) < 1.1f ? '*' : '/';
                foreach (LayoutSlot f in ArcadeLayout.FloorItems())
                    if (x > f.MinX && x < f.MaxX && z > f.MinZ && z < f.MaxZ) { c = Glyph(f.Id); break; }
                line.Append(c);
            }
            Console.WriteLine(line.Append('|').ToString());
        }
        return _failures;
    }

    private static char Glyph(string id) => id.StartsWith("Pinball") ? 'P' : id.StartsWith("Arcade Machine") ? 'D' : id == "Table Top" ? 'T'
        : id.StartsWith("Pac-Man stool") ? 's' : id == "Air Hockey" ? 'H' : id == "Arcade TV" ? 'V' : id.Contains("counter") ? 'C' : '?';

    /// <summary>Reads the game's baked Humanoid navmesh and checks the sidewalk end of the door link.</summary>
    private static void NavMeshCheck(string level1, Vector3 doorCenter, Vector3 inward)
    {
        string dataDir = Path.GetDirectoryName(level1)!;
        var polys = new List<(Vector3[] verts, string surface)>();
        foreach (string file in new[] { "level1", "sharedassets1.assets" })
        {
            string path = Path.Combine(dataDir, file);
            if (!File.Exists(path)) continue;
            var am = new AssetsManager();
            var inst = am.LoadAssetsFile(path, false);
            var f = inst.file;
            foreach (var info in f.Metadata.AssetInfos.Where(a => a.TypeId == 238))
            {
                var r = f.Reader;
                r.Position = info.GetAbsoluteByteOffset(f);
                int nameLen = r.ReadInt32();
                string name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));
                r.Align();
                if (!name.Contains("Humanoid") || name.Contains("Big") || name.Contains("Tutorial")) continue;
                int tiles = r.ReadInt32();
                for (int t = 0; t < tiles; t++)
                {
                    int len = r.ReadInt32();
                    byte[] data = r.ReadBytes(len);
                    r.Align();
                    r.ReadBytes(16); // Hash128
                    polys.AddRange(Tile(data).Select(p => (p, name)));
                }
            }
            am.UnloadAll();
            if (polys.Count > 0) break;
        }
        if (polys.Count == 0)
        {
            Console.WriteLine("NPC navmesh: the game's Humanoid NavMeshData was not found in level1/sharedassets1 (offline check skipped).");
            return;
        }
        Vector3 sidewalk = doorCenter - inward * 0.75f;
        Vector3 insideEnd = doorCenter + inward * 0.85f;
        bool OnMesh(Vector3 p) => polys.Any(q => MathF.Abs(q.verts.Average(v => v.Y) - p.Y) < 0.5f && InPoly(new Vector2(p.X, p.Z), q.verts.Select(v => new Vector2(v.X, v.Z)).ToArray()));
        bool streetOn = OnMesh(sidewalk);
        bool insideVanilla = OnMesh(insideEnd);
        var insideBuilding = polys.Where(q => q.verts.All(v => v.X > -52.2f && v.X < -43.8f && v.Z > 135.85f && v.Z < 147.2f && v.Y < -3f) && q.verts.Average(v => v.Z - v.X) > 182.5f).Count();
        Console.WriteLine($"NPC navmesh ({polys[0].surface}, {polys.Count} polygons parsed): door-link sidewalk end {V(sidewalk)} {(streetOn ? "is on the game's navmesh" : "is NOT on the game's navmesh")}; arcade end {V(insideEnd)} {(insideVanilla ? "already on vanilla navmesh" : "not on vanilla navmesh (the mod bakes the arcade floor at runtime)")}; vanilla polygons inside the arcade: {insideBuilding}");
        if (!streetOn) Fail("the door link's sidewalk end does not land on the game's navmesh");
    }

    /// <summary>Unity's Detour tile ("VAND" v16): header 72 bytes, verts, then 32-byte polys.</summary>
    private static List<Vector3[]> Tile(byte[] d)
    {
        var result = new List<Vector3[]>();
        if (d.Length <= 72 || d[0] != (byte)'V') return result;
        int polyCount = BitConverter.ToInt32(d, 20), vertCount = BitConverter.ToInt32(d, 24);
        float bminX = BitConverter.ToSingle(d, 44), bminZ = BitConverter.ToSingle(d, 52), bmaxX = BitConverter.ToSingle(d, 56), bmaxZ = BitConverter.ToSingle(d, 64);
        if (bmaxX < -60f || bminX > -36f || bmaxZ < 128f || bminZ > 155f) return result;
        var verts = new Vector3[vertCount];
        for (int i = 0; i < vertCount; i++)
            verts[i] = new Vector3(BitConverter.ToSingle(d, 72 + i * 12), BitConverter.ToSingle(d, 76 + i * 12), BitConverter.ToSingle(d, 80 + i * 12));
        int polyBase = 72 + vertCount * 12;
        for (int p = 0; p < polyCount; p++)
        {
            int o = polyBase + p * 32;
            if (o + 32 > d.Length) break;
            int n = d[o + 28];
            if (n < 3 || n > 6) continue;
            var poly = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                int vi = BitConverter.ToUInt16(d, o + k * 2);
                if (vi >= vertCount) { poly = null!; break; }
                poly[k] = verts[vi];
            }
            if (poly != null) result.Add(poly);
        }
        return result;
    }

    private static bool InPoly(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    private static float PolyArea(Vector2[] p)
    {
        float a = 0;
        for (int i = 0; i < p.Length; i++) { var q = p[(i + 1) % p.Length]; a += p[i].X * q.Y - q.X * p[i].Y; }
        return MathF.Abs(a) * 0.5f;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    private static string V(Vector3 v) => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})";

    private static float ChooseSign(Vector3 hinge, Vector3 leafCenter, Vector3 swingSide, float degrees)
    {
        Vector3 arm = Flat(leafCenter - hinge);
        Vector3 positive = Vector3.Transform(arm, Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f));
        return Vector3.Dot(positive, swingSide) >= 0f ? degrees : -degrees;
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "ScheduleIArcade")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? Directory.GetCurrentDirectory();
    }

    /// <summary>Grid search over the interior (room local) keeping a body of the given radius clear of walls and items.</summary>
    private sealed class Walkway
    {
        private const float Cell = 0.05f;
        private readonly int _w, _h;
        private readonly bool[] _free;

        public Walkway(float radius)
        {
            _w = (int)((ArcadeLayout.InnerEast - ArcadeLayout.InnerWest) / Cell) + 1;
            _h = (int)((ArcadeLayout.InnerNorth - ArcadeLayout.InnerSouth) / Cell) + 1;
            _free = new bool[_w * _h];
            var items = ArcadeLayout.FloorItems().ToList();
            for (int j = 0; j < _h; j++)
                for (int i = 0; i < _w; i++)
                {
                    float x = ArcadeLayout.InnerWest + i * Cell, z = ArcadeLayout.InnerSouth + j * Cell;
                    bool ok = ArcadeLayout.IsInsideInterior(x, z, radius);
                    if (ok)
                        foreach (LayoutSlot f in items)
                        {
                            float gx = MathF.Max(0f, MathF.Max(f.MinX - x, x - f.MaxX)), gz = MathF.Max(0f, MathF.Max(f.MinZ - z, z - f.MaxZ));
                            if (gx * gx + gz * gz < radius * radius) { ok = false; break; }
                        }
                    _free[j * _w + i] = ok;
                }
        }

        public (bool found, float length) Reach((float x, float z) from, (float x, float z) to)
        {
            int Idx(float x, float z) => (int)MathF.Round((z - ArcadeLayout.InnerSouth) / Cell) * _w + (int)MathF.Round((x - ArcadeLayout.InnerWest) / Cell);
            int s = Idx(from.x, from.z), g = Idx(to.x, to.z);
            if (s < 0 || s >= _free.Length || g < 0 || g >= _free.Length || !_free[s] || !_free[g]) return (false, 0f);
            var dist = new int[_free.Length];
            Array.Fill(dist, -1);
            dist[s] = 0;
            var q = new Queue<int>();
            q.Enqueue(s);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                if (c == g) return (true, dist[c] * Cell);
                int ci = c % _w, cj = c / _w;
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int ni = ci + di, nj = cj + dj;
                    if (ni < 0 || nj < 0 || ni >= _w || nj >= _h) continue;
                    int n = nj * _w + ni;
                    if (!_free[n] || dist[n] >= 0) continue;
                    dist[n] = dist[c] + 1;
                    q.Enqueue(n);
                }
            }
            return (false, 0f);
        }
    }
}

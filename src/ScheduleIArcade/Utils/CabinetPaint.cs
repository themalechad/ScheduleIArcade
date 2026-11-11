namespace ScheduleIArcade.Utils;

/// <summary>Painted part of a cabinet: one flat, softly grimed colour cell of the paint atlas.</summary>
internal enum PaintPart
{
    Body = 0,
    BodyDark = 1,
    Trim = 2,
    Bezel = 3,
    Glass = 4,
    Marquee = 5,
    ControlDeck = 6,
    Button = 7,
    Metal = 8,
    Seat = 9,
    Underside = 10,
    Playfield = 11,
    Accent = 12,
}

internal enum CabinetKind
{
    Upright,
    Pinball,
    Cocktail,
    AirHockey,
}

/// <summary>
/// Schedule I style cabinet finishes, replacing the old neon pixel-art skins: each cabinet keeps its
/// low-poly model but every triangle is assigned a flat painted part (body, trim, bezel, deck, glass,
/// metal ...) that samples one cell of a small atlas of soft colours with subtle grime. The UV islands
/// of the original texture are ignored, so nothing paints over a screen opening.
///
/// Unity-free (float arrays in, part ids / UVs / RGBA bytes out) so it is unit-tested headlessly;
/// RawArcadeSceneService turns the result into meshes, a texture and a URP Lit material.
/// </summary>
internal static class CabinetPaint
{
    public const int CellPixels = 32;
    public const int AtlasCells = 4;
    public const int AtlasPixels = CellPixels * AtlasCells;

    public readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>Colour scheme for one cabinet (soft, slightly desaturated, like the game's props).</summary>
    public sealed class Scheme
    {
        public Scheme(string name, Rgb body, Rgb accent)
        {
            Name = name;
            Colors[PaintPart.Body] = body;
            Colors[PaintPart.BodyDark] = Shade(body, 0.62f);
            Colors[PaintPart.Trim] = new Rgb(34, 34, 36);
            Colors[PaintPart.Bezel] = new Rgb(38, 38, 40);
            Colors[PaintPart.Glass] = new Rgb(12, 13, 14);
            Colors[PaintPart.Marquee] = new Rgb(214, 204, 178);
            Colors[PaintPart.ControlDeck] = new Rgb(58, 58, 60);
            Colors[PaintPart.Button] = accent;
            Colors[PaintPart.Metal] = new Rgb(140, 142, 144);
            Colors[PaintPart.Seat] = new Rgb(120, 42, 38);
            Colors[PaintPart.Underside] = new Rgb(24, 24, 25);
            Colors[PaintPart.Playfield] = new Rgb(40, 52, 70);
            Colors[PaintPart.Accent] = accent;
        }

        public string Name { get; }
        public Dictionary<PaintPart, Rgb> Colors { get; } = new();
    }

    public static Rgb Shade(Rgb c, float k) => new((byte)Math.Clamp(c.R * k, 0f, 255f), (byte)Math.Clamp(c.G * k, 0f, 255f), (byte)Math.Clamp(c.B * k, 0f, 255f));

    /// <summary>Muted brick red (Freedoom 1), muted olive (Freedoom 2), dusty blue (pinball), mustard (Pac-Man), off-white (air hockey).</summary>
    public static Scheme SchemeFor(string modelName)
    {
        if (modelName.EndsWith("02", StringComparison.Ordinal) && modelName.StartsWith("Arcade Machine", StringComparison.Ordinal))
            return new Scheme("doom2", new Rgb(92, 104, 70), new Rgb(176, 64, 52));
        if (modelName.StartsWith("Arcade Machine", StringComparison.Ordinal))
            return new Scheme("doom1", new Rgb(128, 58, 48), new Rgb(196, 160, 72));
        if (modelName.StartsWith("Pinball", StringComparison.Ordinal))
            return new Scheme("pinball", new Rgb(70, 92, 128), new Rgb(196, 150, 64));
        if (modelName == "Table Top")
            return new Scheme("pacman", new Rgb(190, 152, 64), new Rgb(170, 60, 52));
        if (modelName == "Air Hockey")
        {
            var s = new Scheme("airhockey", new Rgb(196, 194, 186), new Rgb(70, 100, 150));
            s.Colors[PaintPart.Playfield] = new Rgb(210, 214, 214);
            return s;
        }
        return new Scheme("cabinet", new Rgb(120, 120, 118), new Rgb(170, 60, 52));
    }

    /// <summary>Atlas cell (column, row) of a part.</summary>
    public static (int col, int row) Cell(PaintPart part) => ((int)part % AtlasCells, (int)part / AtlasCells);

    /// <summary>
    /// RGBA32 atlas (row 0 = bottom, Unity's texture convention). Each cell is its part colour with a
    /// soft vertical grime gradient (darker towards v = 0, the bottom of a projected face), faint
    /// low-frequency mottling and a few specks. Deterministic for a given seed.
    /// </summary>
    public static byte[] BuildAtlas(Scheme scheme, int seed)
    {
        var px = new byte[AtlasPixels * AtlasPixels * 4];
        foreach (PaintPart part in Enum.GetValues(typeof(PaintPart)))
        {
            Rgb c = scheme.Colors.TryGetValue(part, out Rgb value) ? value : new Rgb(128, 128, 128);
            var (col, row) = Cell(part);
            for (int y = 0; y < CellPixels; y++)
            {
                for (int x = 0; x < CellPixels; x++)
                {
                    float v = (y + 0.5f) / CellPixels;
                    float grime = 0.86f + 0.14f * MathF.Min(1f, v * 1.6f);
                    float mottle = 1f + 0.035f * (Hash(seed, (int)part, x / 6, y / 6) - 0.5f) * 2f;
                    float speck = Hash(seed + 7, (int)part, x, y) > 0.985f ? 0.78f : 1f;
                    float k = grime * mottle * speck;
                    int gx = col * CellPixels + x, gy = row * CellPixels + y;
                    int i = (gy * AtlasPixels + gx) * 4;
                    px[i] = (byte)Math.Clamp(c.R * k, 0f, 255f);
                    px[i + 1] = (byte)Math.Clamp(c.G * k, 0f, 255f);
                    px[i + 2] = (byte)Math.Clamp(c.B * k, 0f, 255f);
                    px[i + 3] = 255;
                }
            }
        }
        return px;
    }

    private static float Hash(int seed, int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)(seed * 374761393 + a * 668265263 + b * 2147483647 + c * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>
    /// Per-triangle part ids. <paramref name="v"/> holds xyz triplets in the model's local metres,
    /// <paramref name="t"/> the triangle indices. For uprights <paramref name="frontSign"/> is the sign
    /// of the model X axis the screen faces (-1 cabinet 01, +1 cabinet 02); other kinds detect it.
    /// </summary>
    public static PaintPart[] Classify(CabinetKind kind, float[] v, int[] t, float frontSign = -1f)
    {
        int triCount = t.Length / 3;
        var parts = new PaintPart[triCount];
        float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
        for (int i = 0; i < v.Length; i += 3)
        {
            minX = MathF.Min(minX, v[i]); maxX = MathF.Max(maxX, v[i]);
            minY = MathF.Min(minY, v[i + 1]); maxY = MathF.Max(maxY, v[i + 1]);
        }
        float height = MathF.Max(1e-4f, maxY - minY);
        int[] island = IslandIds(v, t, out var islandMin, out var islandMax);

        // Pinball: the backbox end is where the tall head is.
        float headSign = 1f;
        if (kind == CabinetKind.Pinball)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i < v.Length; i += 3)
                if (v[i + 1] > minY + height * 0.8f) { sum += v[i]; n++; }
            headSign = n > 0 && sum < 0 ? -1f : 1f;
        }

        for (int k = 0; k < triCount; k++)
        {
            int a = t[k * 3] * 3, b = t[k * 3 + 1] * 3, c = t[k * 3 + 2] * 3;
            float e1x = v[b] - v[a], e1y = v[b + 1] - v[a + 1], e1z = v[b + 2] - v[a + 2];
            float e2x = v[c] - v[a], e2y = v[c + 1] - v[a + 1], e2z = v[c + 2] - v[a + 2];
            float nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12f) { parts[k] = PaintPart.Body; continue; }
            nx /= len; ny /= len; nz /= len;
            float cx = (v[a] + v[b] + v[c]) / 3f, cy = (v[a + 1] + v[b + 1] + v[c + 1]) / 3f, cz = (v[a + 2] + v[b + 2] + v[c + 2]) / 3f;
            float h = (cy - minY) / height;
            int isl = island[k];
            float islandSize = Diag(islandMin[isl], islandMax[isl]);
            parts[k] = kind switch
            {
                CabinetKind.Upright => Upright(nx, ny, nz, cx, cz, h, frontSign, islandSize),
                CabinetKind.Pinball => Pinball(nx, ny, nz, cx, h, headSign, islandMin[isl], islandMax[isl], maxX - minX),
                CabinetKind.Cocktail => Cocktail(nx, ny, nz, cx, cy, islandSize, islandMin[isl], islandMax[isl]),
                _ => AirHockey(nx, ny, nz, cy, islandMin[isl], islandMax[isl]),
            };
        }
        return parts;
    }

    private static float Diag((float x, float y, float z) a, (float x, float y, float z) b)
    {
        float dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Upright (Doom) cabinet, measured on the packaged model (01: screen faces -X, height 1.446 m):
    /// marquee front h > 0.87, screen panel 0.54..0.86 with the CRT recess |z| &lt; 0.18, control deck
    /// facing up around h 0.5, coin door below.
    /// </summary>
    private static PaintPart Upright(float nx, float ny, float nz, float cx, float cz, float h, float front, float islandSize)
    {
        float facing = nx * front;
        if (islandSize < 0.09f && h > 0.42f && h < 0.62f)
            return PaintPart.Button;
        if (ny < -0.7f)
            return PaintPart.Underside;
        if (ny > 0.7f && h > 0.95f)
            return PaintPart.BodyDark;
        if (MathF.Abs(nz) > 0.5f && MathF.Abs(nz) < 0.8f && facing > 0.4f && MathF.Abs(cz) > 0.24f)
            return PaintPart.Trim; // T-moulding strips along the front edges
        if (facing > 0.3f || (ny > 0.45f && h > 0.45f && h < 0.58f))
        {
            if (h > 0.87f)
                return PaintPart.Marquee;
            if (h >= 0.535f && MathF.Abs(cz) < 0.18f && h <= 0.80f)
                return PaintPart.Glass; // CRT recess and its bevel wings: black, the live picture sits on top
            if (h >= 0.535f)
                return PaintPart.Bezel;
            if (ny > 0.45f)
                return PaintPart.ControlDeck;
            if (h < 0.06f)
                return PaintPart.BodyDark;
            return PaintPart.Body;
        }
        if (facing < -0.5f)
            return PaintPart.BodyDark;
        return PaintPart.Body;
    }

    private static PaintPart Pinball(float nx, float ny, float nz, float cx, float h, float headSign, (float x, float y, float z) imin, (float x, float y, float z) imax, float length)
    {
        float ix = imax.x - imin.x, iz = imax.z - imin.z;
        // Legs and small hardware: thin islands.
        if (ix < 0.09f && iz < 0.09f)
            return PaintPart.Metal;
        bool head = cx * headSign > length * 0.5f - 0.13f && h > 0.55f;
        if (head)
        {
            // Backglass faces the player (towards -headSign); the rest of the head is dark body.
            if (nx * -headSign > 0.6f)
                return PaintPart.Marquee;
            return PaintPart.BodyDark;
        }
        if (ny < -0.7f)
            return PaintPart.Underside;
        if (ny > 0.8f)
            return h > 0.5f ? PaintPart.Playfield : PaintPart.Trim;
        if (MathF.Abs(nz) > 0.7f)
            return PaintPart.Body;
        if (nx * -headSign > 0.6f)
            return PaintPart.BodyDark; // front (coin door end)
        return PaintPart.Trim;
    }

    private static PaintPart Cocktail(float nx, float ny, float nz, float cx, float cy, float islandSize, (float x, float y, float z) imin, (float x, float y, float z) imax)
    {
        // Stools at both ends (|x| > 0.85 m): red seat on a metal post.
        if (MathF.Abs(cx) > 0.86f)
            return ny > 0.6f && cy > -0.12f ? PaintPart.Seat : (cy > -0.09f && MathF.Abs(ny) < 0.6f ? PaintPart.Seat : PaintPart.Metal);
        if (islandSize < 0.08f && cy > 0.0f)
            return PaintPart.Button;
        if (ny > 0.8f)
        {
            if (cy > 0.295f)
                return PaintPart.Glass; // smoked top glass
            if (cy > 0.28f)
                return PaintPart.Glass; // monitor under the glass
            if (cy > 0.2f)
                return PaintPart.Trim;
            return PaintPart.ControlDeck;
        }
        if (ny < -0.7f)
            return PaintPart.Underside;
        if (cy > 0.24f)
            return PaintPart.Trim; // rim around the top
        if (cy < -0.24f)
            return PaintPart.BodyDark; // kick base
        return PaintPart.Body;
    }

    private static PaintPart AirHockey(float nx, float ny, float nz, float cy, (float x, float y, float z) imin, (float x, float y, float z) imax)
    {
        float ix = imax.x - imin.x, iz = imax.z - imin.z, iy = imax.y - imin.y;
        // Score arch tubes and legs: long thin islands.
        if ((ix < 0.09f && iz < 0.09f) || (iy > 0.3f && imin.y > -0.1f && ix < 0.15f))
            return PaintPart.Metal;
        if (cy > 0.30f)
            return PaintPart.BodyDark; // score display box
        if (ny > 0.8f && cy > -0.06f && cy < -0.01f)
            return PaintPart.Playfield;
        if (ny > 0.8f)
            return PaintPart.Trim; // rails
        if (ny < -0.7f)
            return PaintPart.Underside;
        if (cy < -0.55f)
            return PaintPart.BodyDark;
        return MathF.Abs(nz) > 0.7f ? PaintPart.Accent : PaintPart.Body;
    }

    /// <summary>Connected components (shared index or shared position) per triangle, with their bounds.</summary>
    public static int[] IslandIds(float[] v, int[] t, out (float x, float y, float z)[] min, out (float x, float y, float z)[] max)
    {
        int n = v.Length / 3;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Root(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        void Union(int a, int b) { a = Root(a); b = Root(b); if (a != b) parent[a] = b; }
        var byPos = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < n; i++)
        {
            var key = ((int)MathF.Round(v[i * 3] * 10000f), (int)MathF.Round(v[i * 3 + 1] * 10000f), (int)MathF.Round(v[i * 3 + 2] * 10000f));
            if (byPos.TryGetValue(key, out int other)) Union(i, other); else byPos[key] = i;
        }
        for (int k = 0; k + 2 < t.Length; k += 3) { Union(t[k], t[k + 1]); Union(t[k + 1], t[k + 2]); }
        var ids = new Dictionary<int, int>();
        var result = new int[t.Length / 3];
        var mins = new List<(float, float, float)>();
        var maxs = new List<(float, float, float)>();
        for (int k = 0; k < result.Length; k++)
        {
            int r = Root(t[k * 3]);
            if (!ids.TryGetValue(r, out int id))
            {
                id = ids[r] = mins.Count;
                mins.Add((float.MaxValue, float.MaxValue, float.MaxValue));
                maxs.Add((float.MinValue, float.MinValue, float.MinValue));
            }
            result[k] = id;
            for (int j = 0; j < 3; j++)
            {
                int vi = t[k * 3 + j] * 3;
                var (a, b, c) = mins[id];
                mins[id] = (MathF.Min(a, v[vi]), MathF.Min(b, v[vi + 1]), MathF.Min(c, v[vi + 2]));
                var (d, e, f) = maxs[id];
                maxs[id] = (MathF.Max(d, v[vi]), MathF.Max(e, v[vi + 1]), MathF.Max(f, v[vi + 2]));
            }
        }
        min = mins.ToArray();
        max = maxs.ToArray();
        return result;
    }

    /// <summary>
    /// Unshared, painted geometry: every triangle gets its own three vertices whose UVs fall inside its
    /// part's atlas cell (planar box projection of the model bounds, inset by one pixel so bilinear
    /// filtering never bleeds between cells). Returns new positions, source-vertex map and UVs.
    /// </summary>
    public static (float[] positions, int[] sourceIndex, float[] uvs, int[] triangles) Unshare(float[] v, int[] t, PaintPart[] parts)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < v.Length; i += 3)
        {
            minX = MathF.Min(minX, v[i]); maxX = MathF.Max(maxX, v[i]);
            minY = MathF.Min(minY, v[i + 1]); maxY = MathF.Max(maxY, v[i + 1]);
            minZ = MathF.Min(minZ, v[i + 2]); maxZ = MathF.Max(maxZ, v[i + 2]);
        }
        float sx = MathF.Max(1e-4f, maxX - minX), sy = MathF.Max(1e-4f, maxY - minY), sz = MathF.Max(1e-4f, maxZ - minZ);
        int triCount = t.Length / 3;
        var pos = new float[triCount * 9];
        var src = new int[triCount * 3];
        var uv = new float[triCount * 6];
        var tri = new int[triCount * 3];
        float inset = 1.5f / CellPixels;
        for (int k = 0; k < triCount; k++)
        {
            int a = t[k * 3] * 3, b = t[k * 3 + 1] * 3, c = t[k * 3 + 2] * 3;
            float nx = MathF.Abs((v[b + 1] - v[a + 1]) * (v[c + 2] - v[a + 2]) - (v[b + 2] - v[a + 2]) * (v[c + 1] - v[a + 1]));
            float ny = MathF.Abs((v[b + 2] - v[a + 2]) * (v[c] - v[a]) - (v[b] - v[a]) * (v[c + 2] - v[a + 2]));
            float nz = MathF.Abs((v[b] - v[a]) * (v[c + 1] - v[a + 1]) - (v[b + 1] - v[a + 1]) * (v[c] - v[a]));
            var (col, row) = Cell(parts[k]);
            for (int j = 0; j < 3; j++)
            {
                int si = t[k * 3 + j];
                int vi = si * 3;
                int o = k * 3 + j;
                pos[o * 3] = v[vi]; pos[o * 3 + 1] = v[vi + 1]; pos[o * 3 + 2] = v[vi + 2];
                src[o] = si;
                float px = (v[vi] - minX) / sx, py = (v[vi + 1] - minY) / sy, pz = (v[vi + 2] - minZ) / sz;
                // Box projection: faces facing up/down use (x, z); sides use (horizontal, y) so the
                // grime gradient (darker at v = 0) ends up at the bottom of the cabinet.
                float u, w;
                if (ny >= nx && ny >= nz) { u = px; w = pz; }
                else if (nx >= nz) { u = pz; w = py; }
                else { u = px; w = py; }
                u = inset + Math.Clamp(u, 0f, 1f) * (1f - 2f * inset);
                w = inset + Math.Clamp(w, 0f, 1f) * (1f - 2f * inset);
                uv[o * 2] = (col + u) / AtlasCells;
                uv[o * 2 + 1] = (row + w) / AtlasCells;
                tri[o] = o;
            }
        }
        return (pos, src, uv, tri);
    }
}

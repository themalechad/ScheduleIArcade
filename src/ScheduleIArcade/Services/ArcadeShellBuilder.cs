using MelonLoader;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Resolves the interior surface materials: live Schedule I materials first (cloned at runtime,
/// never modified in place), then simple in-code textures in the same style (<see cref="SurfacePatterns"/>).
/// None of the 0.5.0 room textures are used any more.
/// </summary>
internal sealed class ArcadeSurfaceMaterials : IDisposable
{
    private readonly List<UnityEngine.Object> _created = new();

    public Material Floor { get; private set; } = null!;
    public Material Wall { get; private set; } = null!;
    public Material Ceiling { get; private set; } = null!;
    public Material Skirting { get; private set; } = null!;
    /// <summary>UV metres-per-repeat for each surface (live textures differ from the fallbacks).</summary>
    public Vector2 FloorUv { get; private set; } = Vector2.one;
    public Vector2 WallUv { get; private set; } = Vector2.one;
    public Vector2 CeilingUv { get; private set; } = Vector2.one;
    public string Summary { get; private set; } = string.Empty;

    public void Resolve()
    {
        GameMaterialLibrary.Reset();
        var notes = new List<string>();

        Material? carpet = GameMaterialLibrary.Find("Carpet", "Carpet1", "carpet crimson mat", "carpet red mat");
        if (carpet != null)
        {
            Floor = Clone(carpet, "ScheduleIArcade_M_Floor_" + carpet.name);
            FloorUv = new Vector2(2f, 2f);
            notes.Add("floor=live '" + carpet.name + "'");
        }
        else
        {
            Floor = Procedural(SurfacePatterns.Kind.Carpet, "ScheduleIArcade_M_FloorCarpet", 0.08f);
            FloorUv = Vector2.one * SurfacePatterns.MetresPerTile(SurfacePatterns.Kind.Carpet);
            notes.Add("floor=procedural carpet");
        }

        // Wallpaper_B* is a light patterned paper with a wainscot band at the bottom of the texture;
        // one texture repeat spans the full 3.5 m wall height and the clone is tinted dark.
        Material? paper = GameMaterialLibrary.Find("Wallpaper_B2", "Wallpaper_B1", "Wallpaper_B");
        if (paper != null && (paper.HasProperty("_Color") || paper.HasProperty("_BaseColor")))
        {
            Wall = Clone(paper, "ScheduleIArcade_M_Wall_" + paper.name);
            var dark = new Color(0.30f, 0.30f, 0.36f, 1f);
            foreach (string p in new[] { "_Color", "_BaseColor", "_Mat1_Color", "_Mat2_Color" })
                if (Wall.HasProperty(p)) Wall.SetColor(p, new Color(dark.r, dark.g, dark.b, Wall.GetColor(p).a));
            if (Wall.HasProperty("_EmissionColor")) Wall.SetColor("_EmissionColor", Wall.GetColor("_EmissionColor") * 0.25f);
            WallUv = new Vector2(ArcadeDimensions.InteriorWallHeight, ArcadeDimensions.InteriorWallHeight);
            notes.Add("walls=live '" + paper.name + "' (dark tint)");
        }
        else
        {
            Wall = Procedural(SurfacePatterns.Kind.Wallpaper, "ScheduleIArcade_M_WallDarkPaper", 0.12f);
            WallUv = Vector2.one * SurfacePatterns.MetresPerTile(SurfacePatterns.Kind.Wallpaper);
            notes.Add("walls=procedural dark wallpaper");
        }

        Material? ceiling = GameMaterialLibrary.Find("Ceiling", "ceiling_0");
        if (ceiling != null)
        {
            Ceiling = Clone(ceiling, "ScheduleIArcade_M_Ceiling_" + ceiling.name);
            CeilingUv = new Vector2(1.2f, 1.2f);
            notes.Add("ceiling=live '" + ceiling.name + "'");
        }
        else
        {
            Ceiling = Procedural(SurfacePatterns.Kind.CeilingTiles, "ScheduleIArcade_M_CeilingTiles", 0.10f);
            CeilingUv = Vector2.one * SurfacePatterns.MetresPerTile(SurfacePatterns.Kind.CeilingTiles);
            notes.Add("ceiling=procedural tiles");
        }

        Material? skirting = GameMaterialLibrary.Find("Skirting", "Trim");
        Skirting = skirting != null ? Clone(skirting, "ScheduleIArcade_M_Skirting") : Solid("ScheduleIArcade_M_Skirting", new Color(0.12f, 0.12f, 0.13f, 1f), 0.2f);
        notes.Add(skirting != null ? "skirting=live '" + skirting.name + "'" : "skirting=procedural");

        // Every shell surface is a closed solid; render both faces anyway so no face is ever see-through.
        foreach (Material m in new[] { Floor, Wall, Ceiling, Skirting })
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
        Summary = string.Join(", ", notes);
    }

    private Material Clone(Material source, string name)
    {
        var m = new Material(source) { name = name };
        _created.Add(m);
        return m;
    }

    public Material Solid(string name, Color color, float smoothness, Color? emission = null)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(shader) { name = name };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        if (emission.HasValue && m.HasProperty("_EmissionColor"))
        {
            m.SetColor("_EmissionColor", emission.Value);
            m.EnableKeyword("_EMISSION");
        }
        _created.Add(m);
        return m;
    }

    private Material Procedural(SurfacePatterns.Kind kind, string name, float smoothness)
    {
        byte[] px = SurfacePatterns.Generate(kind);
        var texture = new Texture2D(SurfacePatterns.Size, SurfacePatterns.Size, TextureFormat.RGBA32, true)
        {
            name = name + "_T",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8
        };
        texture.LoadRawTextureData(px);
        texture.Apply(true, false);
        _created.Add(texture);
        Material m = Solid(name, Color.white, smoothness);
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", texture);
        return m;
    }

    public void Dispose()
    {
        foreach (UnityEngine.Object o in _created)
            if (o != null) UnityEngine.Object.Destroy(o);
        _created.Clear();
    }
}

/// <summary>
/// Builds the interior shell over the whole ground floor of the Arcade building from
/// <see cref="ArcadeLayout"/>: one flat floor level with the sidewalk, one solid lining layer per wall
/// (real thickness, so opaque from both sides) with the window and door openings left clear, and a
/// solid ceiling slab sealed into the walls at the first-floor line. No second shell, no vestibule.
/// </summary>
internal sealed class ArcadeShellBuilder
{
    public const string FloorName = "ARCADE_SHELL_FLOOR";
    public const string FloorColliderName = "ARCADE_SHELL_FLOOR_COLLIDER";
    public const string CeilingName = "ARCADE_SHELL_CEILING";
    public const string WallPrefix = "ARCADE_SHELL_WALL_";
    public const string SkirtingPrefix = "ARCADE_SHELL_SKIRTING_";

    private readonly List<Mesh> _meshes;
    private readonly ArcadeSurfaceMaterials _materials;

    public ArcadeShellBuilder(List<Mesh> meshes, ArcadeSurfaceMaterials materials)
    {
        _meshes = meshes;
        _materials = materials;
    }

    /// <summary>The walkable floor mesh (room local), also used as the NavMesh build source.</summary>
    public Mesh? FloorMesh { get; private set; }

    public void Build(Transform room)
    {
        // Outline pushed out to the lining's outer faces so floor and ceiling run under/into the walls.
        var outer = OuterOutline();

        // Floor: carpet 3 mm above the foundation (no step: the door leaves clear it), plus one flat
        // collider whose top is exactly the floor plane (= sidewalk at the threshold).
        FloorMesh = Prism("ScheduleIArcade_Floor", outer, ArcadeLayout.FloorTop - 0.02f, ArcadeLayout.FloorTop + 0.003f, _materials.FloorUv);
        Visual(room, FloorName, FloorMesh, _materials.Floor);
        var floorCollider = new GameObject(FloorColliderName);
        floorCollider.transform.SetParent(room, false);
        var floorBox = floorCollider.AddComponent<BoxCollider>();
        floorBox.center = new Vector3((ArcadeLayout.InnerWest + ArcadeLayout.InnerEast) * 0.5f, ArcadeLayout.FloorTop - 0.15f, (ArcadeLayout.InnerSouth + ArcadeLayout.InnerNorth) * 0.5f);
        floorBox.size = new Vector3(ArcadeLayout.InnerEast - ArcadeLayout.InnerWest, 0.30f, ArcadeLayout.InnerNorth - ArcadeLayout.InnerSouth);

        // Ceiling: a solid slab from the ceiling underside up to the first-floor line.
        Mesh ceiling = Prism("ScheduleIArcade_Ceiling", outer, ArcadeLayout.CeilingY, ArcadeLayout.CeilingTopY, _materials.CeilingUv);
        GameObject ceilingGo = Visual(room, CeilingName, ceiling, _materials.Ceiling);
        var ceilingBox = ceilingGo.AddComponent<BoxCollider>();
        ceilingBox.center = new Vector3((ArcadeLayout.InnerWest + ArcadeLayout.InnerEast) * 0.5f, (ArcadeLayout.CeilingY + ArcadeLayout.CeilingTopY) * 0.5f, (ArcadeLayout.InnerSouth + ArcadeLayout.InnerNorth) * 0.5f);
        ceilingBox.size = new Vector3(ArcadeLayout.InnerEast - ArcadeLayout.InnerWest, ArcadeLayout.CeilingTopY - ArcadeLayout.CeilingY, ArcadeLayout.InnerNorth - ArcadeLayout.InnerSouth);

        int pieces = 0;
        foreach (WallPiece piece in ArcadeLayout.WallPieces())
            pieces += Piece(room, piece.Wall, piece.Name, piece.A0, piece.A1, piece.Y0, piece.Y1, piece.Skirting);
        MelonLogger.Msg($"Arcade shell: whole ground floor {ArcadeLayout.InnerEast - ArcadeLayout.InnerWest:0.00} x {ArcadeLayout.InnerNorth - ArcadeLayout.InnerSouth:0.00} m, {pieces} solid wall pieces around {ArcadeLayout.Walls.Sum(w => w.Openings.Length)} openings, ceiling {ArcadeDimensions.InteriorCeilingHeight:0.00} m clear (world y {ArcadeLayout.WorldOriginY + ArcadeLayout.CeilingY:0.00}); {_materials.Summary}.");
    }

    private static List<Vector2> OuterOutline() => ArcadeLayout.OuterOutline().Select(p => new Vector2(p.x, p.z)).ToList();

    private int Piece(Transform room, WallLining wall, string name, float a0, float a1, float y0, float y1, bool skirting)
    {
        var (tx, tz) = wall.Tangent;
        var (nx, nz) = wall.Inward;
        float mid = (a0 + a1) * 0.5f;
        // Box centre: half a thickness behind the inner face.
        float cx = wall.AX + tx * mid - nx * wall.Thickness * 0.5f;
        float cz = wall.AZ + tz * mid - nz * wall.Thickness * 0.5f;
        var go = new GameObject(WallPrefix + name);
        go.transform.SetParent(room, false);
        go.transform.localPosition = new Vector3(cx, (y0 + y1) * 0.5f, cz);
        go.transform.localRotation = Quaternion.LookRotation(new Vector3(tx, 0f, tz), Vector3.up);
        var size = new Vector3(wall.Thickness, y1 - y0, a1 - a0);
        Mesh mesh = WallBox(size, a0, y0, _materials.WallUv);
        _meshes.Add(mesh);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = _materials.Wall;
        go.AddComponent<BoxCollider>().size = size;

        if (skirting && y0 <= ArcadeLayout.FloorTop)
        {
            const float h = 0.10f, d = 0.02f;
            var sk = new GameObject(SkirtingPrefix + name);
            sk.transform.SetParent(room, false);
            float clampedA0 = Mathf.Max(a0, 0f), clampedA1 = Mathf.Min(a1, wall.Length);
            if (clampedA1 - clampedA0 > 0.02f)
            {
                float m2 = (clampedA0 + clampedA1) * 0.5f;
                sk.transform.localPosition = new Vector3(wall.AX + tx * m2 + nx * d * 0.5f, ArcadeLayout.FloorTop + h * 0.5f, wall.AZ + tz * m2 + nz * d * 0.5f);
                sk.transform.localRotation = go.transform.localRotation;
                Mesh skMesh = MetricBoxMesh.Create(new Vector3(d, h, clampedA1 - clampedA0), "ScheduleIArcade_Skirting");
                skMesh.vertices = skMesh.vertices.Select(v => Vector3.Scale(v, new Vector3(d, h, clampedA1 - clampedA0))).ToArray();
                skMesh.RecalculateBounds();
                _meshes.Add(skMesh);
                sk.AddComponent<MeshFilter>().sharedMesh = skMesh;
                sk.AddComponent<MeshRenderer>().sharedMaterial = _materials.Skirting;
            }
        }
        return 1;
    }

    /// <summary>
    /// Box of the given size (x = thickness, y = height, z = length) whose wallpaper UVs are continuous
    /// across pieces: U = metres along the wall, V = metres above the floor, divided by the repeat size.
    /// </summary>
    private static Mesh WallBox(Vector3 size, float along0, float y0, Vector2 metresPerRepeat)
    {
        Mesh mesh = MetricBoxMesh.Create(size, "ScheduleIArcade_WallPiece");
        Vector3[] v = mesh.vertices;
        Vector3[] n = mesh.normals;
        var uv = new Vector2[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            Vector3 p = Vector3.Scale(v[i], size);
            v[i] = p;
            float along = along0 + p.z + size.z * 0.5f;
            float up = y0 - ArcadeLayout.FloorTop + p.y + size.y * 0.5f;
            uv[i] = Mathf.Abs(n[i].y) > 0.5f
                ? new Vector2(along / metresPerRepeat.x, p.x / metresPerRepeat.y)
                : Mathf.Abs(n[i].z) > 0.5f
                    ? new Vector2(p.x / metresPerRepeat.x, up / metresPerRepeat.y)
                    : new Vector2((n[i].x > 0f ? -along : along) / metresPerRepeat.x, up / metresPerRepeat.y);
        }
        mesh.vertices = v;
        mesh.uv = uv;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private GameObject Visual(Transform room, string name, Mesh mesh, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(room, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        return go;
    }

    /// <summary>Closed convex prism over a counter-clockwise outline (top and bottom caps + sides).</summary>
    private Mesh Prism(string name, List<Vector2> outline, float yBottom, float yTop, Vector2 metresPerRepeat)
    {
        int n = outline.Count;
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        // Caps.
        int topStart = vertices.Count;
        foreach (Vector2 p in outline) { vertices.Add(new Vector3(p.x, yTop, p.y)); uvs.Add(new Vector2(p.x / metresPerRepeat.x, p.y / metresPerRepeat.y)); }
        int bottomStart = vertices.Count;
        foreach (Vector2 p in outline) { vertices.Add(new Vector3(p.x, yBottom, p.y)); uvs.Add(new Vector2(p.x / metresPerRepeat.x, p.y / metresPerRepeat.y)); }
        for (int i = 1; i + 1 < n; i++)
        {
            // Outline is CCW seen from above (+Y); Unity front faces are clockwise from the viewer.
            tris.AddRange(new[] { topStart, topStart + i + 1, topStart + i });
            tris.AddRange(new[] { bottomStart, bottomStart + i, bottomStart + i + 1 });
        }
        for (int i = 0; i < n; i++)
        {
            Vector2 a = outline[i], b = outline[(i + 1) % n];
            int s = vertices.Count;
            float len = Vector2.Distance(a, b);
            vertices.Add(new Vector3(a.x, yBottom, a.y)); uvs.Add(new Vector2(0f, 0f));
            vertices.Add(new Vector3(b.x, yBottom, b.y)); uvs.Add(new Vector2(len / metresPerRepeat.x, 0f));
            vertices.Add(new Vector3(b.x, yTop, b.y)); uvs.Add(new Vector2(len / metresPerRepeat.x, (yTop - yBottom) / metresPerRepeat.y));
            vertices.Add(new Vector3(a.x, yTop, a.y)); uvs.Add(new Vector2(0f, (yTop - yBottom) / metresPerRepeat.y));
            tris.AddRange(new[] { s, s + 2, s + 1, s, s + 3, s + 2 });
        }
        var mesh = new Mesh { name = name };
        mesh.vertices = vertices.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        _meshes.Add(mesh);
        return mesh;
    }
}

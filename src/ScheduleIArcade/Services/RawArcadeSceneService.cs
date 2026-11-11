using MelonLoader;
using ScheduleIArcade.Core;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Builds the arcade inside the whole ground floor of the vanilla Arcade building:
///   * the interior shell (floor / wall lining / ceiling) from <see cref="ArcadeShellBuilder"/>;
///   * the packaged cabinet MODELS (Doom uprights, Pac-Man cocktail table, pinball bank, air hockey)
///     from the small runtime mesh package, repainted in Schedule I's flat style (<see cref="CabinetPaint"/>);
///   * screens fitted to each cabinet's measured opening, simple title decals, the TV console and lights.
/// The package's old room shell ("Enviorment"), crane game and vending machine are not built, and none
/// of the 0.5.0 room/cabinet textures are loaded.
/// </summary>
internal sealed class RawArcadeSceneService : IDisposable
{
    private static readonly byte[] ExpectedMagic =
    {
        (byte)'S', (byte)'1', (byte)'A', (byte)'R', (byte)'A', (byte)'W', (byte)'0', (byte)'1'
    };

    private readonly List<Mesh> _meshes = new();
    private readonly List<Material> _materials = new();
    private readonly List<Texture2D> _textures = new();
    private readonly ArcadeSurfaceMaterials _surfaces = new();
    private readonly Dictionary<string, (float[] v, int[] t, PaintPart[] parts)> _painted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Static 3D flipper meshes baked on the pinball playfield (machines 01/02; 03/04 mirrored).</summary>
    private static readonly (Vector3 min, Vector3 max) PinballStaticFlippers = (new Vector3(-0.385f, -0.035f, -0.09f), new Vector3(-0.315f, 0.005f, 0.09f));

    private const float FloorTopY = ArcadeLayout.FloorTop;

    public bool IsAvailable => ModPaths.Exists(ModPaths.RawSceneMeshPath);

    /// <summary>Walkable floor mesh of the last build (room local), for the NPC NavMesh.</summary>
    public Mesh? FloorMesh { get; private set; }

    public bool TryPopulate(GameObject root, out Transform? spawnMarker, out Transform? exitMarker)
    {
        spawnMarker = null;
        exitMarker = null;
        if (!IsAvailable)
            return false;

        try
        {
            _surfaces.Resolve();
            var shell = new ArcadeShellBuilder(_meshes, _surfaces);
            shell.Build(root.transform);
            FloorMesh = shell.FloorMesh;

            using var stream = new MemoryStream(ModFiles.ReadAllBytes(ModPaths.Find(ModPaths.RawSceneMeshPath)) ?? Array.Empty<byte>());
            using var reader = new BinaryReader(stream);
            ValidateHeader(reader);
            int version = reader.ReadInt32();
            if (version != 1 && version != 2)
                throw new InvalidDataException($"Unsupported raw arcade mesh version {version}.");
            int meshCount = reader.ReadInt32();
            if (meshCount <= 0 || meshCount > 64)
                throw new InvalidDataException($"Invalid raw arcade mesh count {meshCount}.");
            for (int i = 0; i < meshCount; i++)
                ReadAndCreateMesh(reader, root.transform, version);
            if (stream.Position != stream.Length)
                MelonLogger.Warning($"Raw arcade mesh package has {stream.Length - stream.Position} unread byte(s).");

            CreateInteractiveArcadeTv(root.transform);
            CreatePacmanTableScreen(root.transform);
            CreateActualDoorMarkers(root.transform, out spawnMarker, out exitMarker);
            try
            {
                CreateCabinetScreens(root.transform);
                CreatePinballTableScreens(root.transform);
                CreateAirHockeyScreen(root.transform);
                CreateDecals(root.transform);
            }
            catch (Exception screenEx)
            {
                MelonLogger.Warning($"Arcade room loaded, but cabinet screen setup failed: {screenEx.Message}");
            }
            CreateInteriorLights(root.transform);

            Physics.SyncTransforms();
            MelonLogger.Msg("Arcade machines placed over the whole ground floor and repainted in Schedule I's flat style (no 0.5.0 room or cabinet textures are used).");
            return spawnMarker != null && exitMarker != null;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"Failed to build the arcade room: {ex}");
            Dispose();
            return false;
        }
    }

    private static void ValidateHeader(BinaryReader reader)
    {
        byte[] actual = reader.ReadBytes(ExpectedMagic.Length);
        if (actual.Length != ExpectedMagic.Length)
            throw new EndOfStreamException("Raw arcade mesh package ended before its header.");
        for (int i = 0; i < ExpectedMagic.Length; i++)
            if (actual[i] != ExpectedMagic[i])
                throw new InvalidDataException("Raw arcade mesh package has an invalid header.");
    }

    private void ReadAndCreateMesh(BinaryReader reader, Transform parent, int packageVersion)
    {
        string modelName = ReadString(reader);
        ReadString(reader); // albedo (unused since 0.6.0)
        ReadString(reader); // normal
        ReadString(reader); // emission
        Vector3 authoredPosition = Vector3.zero;
        float authoredYaw = 0f;
        if (packageVersion >= 2)
        {
            authoredPosition = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            authoredYaw = reader.ReadSingle();
        }

        int vertexCount = reader.ReadInt32();
        if (vertexCount <= 0 || vertexCount > 200000)
            throw new InvalidDataException($"Invalid vertex count {vertexCount} for '{modelName}'.");
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        for (int i = 0; i < vertexCount; i++)
            normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        reader.BaseStream.Seek(vertexCount * 8L, SeekOrigin.Current); // uvs (unused since 0.6.0)
        int indexCount = reader.ReadInt32();
        if (indexCount <= 0 || indexCount > 600000 || indexCount % 3 != 0)
            throw new InvalidDataException($"Invalid index count {indexCount} for '{modelName}'.");
        var triangles = new int[indexCount];
        for (int i = 0; i < indexCount; i++)
        {
            int index = reader.ReadInt32();
            if (index < 0 || index >= vertexCount)
                throw new InvalidDataException($"Mesh '{modelName}' contains out-of-range index {index}.");
            triangles[i] = index;
        }

        LayoutSlot? slot = ArcadeLayout.Machine(modelName);
        if (slot == null)
            return; // old room shell, crane game, vending machine: not part of the 0.6.0 arcade

        if (packageVersion == 1)
        {
            Vector3 c = Vector3.zero;
            foreach (Vector3 p in vertices) c += p;
            c /= vertices.Length;
            for (int i = 0; i < vertices.Length; i++) vertices[i] -= c;
        }

        bool isAirHockey = modelName.Equals("Air Hockey", StringComparison.OrdinalIgnoreCase);
        bool isArcadeCabinet = modelName.StartsWith("Arcade Machine", StringComparison.OrdinalIgnoreCase);
        bool isPinballMachine = modelName.StartsWith("Pinball Machine", StringComparison.OrdinalIgnoreCase);
        bool isPacmanCocktail = modelName.Equals("Table Top", StringComparison.OrdinalIgnoreCase);

        var splitParts = new List<(string name, int[] triangles)>();
        if (isPinballMachine)
        {
            // The 2D table draws and animates its own flippers; hide the frozen 3D pair.
            Vector3 fMin = PinballStaticFlippers.min, fMax = PinballStaticFlippers.max;
            var boxes = new[]
            {
                ("static flippers", fMin, fMax),
                ("static flippers (mirrored)", new Vector3(-fMax.x, fMin.y, -fMax.z), new Vector3(-fMin.x, fMax.y, -fMin.z)),
            };
            triangles = MeshIslands.RemoveIslandsInside(vertices, triangles, boxes, out _);
        }
        else if (isAirHockey)
        {
            // Overhead score arch and the loose mallets/puck are separate pieces the session hides.
            triangles = MeshIslands.Split(vertices, triangles, island =>
                island.min.y > -0.12f && island.max.y > 0.30f ? "AirHockey_Scoreboard"
                : (island.max - island.min).magnitude < 0.16f && island.min.y > -0.06f && island.max.y < 0.03f ? "AirHockey_TablePieces"
                : null, splitParts);
        }

        string runtimeName = modelName;
        if (isArcadeCabinet)
        {
            string suffix = modelName.Substring("Arcade Machine".Length).Trim();
            if (!int.TryParse(suffix, out int cabinetNumber) || cabinetNumber <= 0)
                cabinetNumber = 1;
            runtimeName = Constants.CabinetPrefix + cabinetNumber.ToString("00");
        }
        else if (isPacmanCocktail)
            runtimeName = Constants.CabinetPrefix + "PACMAN";
        else if (isAirHockey)
            runtimeName = Constants.CabinetPrefix + "AIRHOCKEY";

        var go = new GameObject(runtimeName);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(slot.X, authoredPosition.y + (packageVersion >= 2 ? 0f : FloorTopY), slot.Z);
        go.transform.localRotation = Quaternion.Euler(0f, slot.Yaw, 0f);

        CabinetKind kind = isArcadeCabinet ? CabinetKind.Upright : isPinballMachine ? CabinetKind.Pinball : isPacmanCocktail ? CabinetKind.Cocktail : CabinetKind.AirHockey;
        float frontSign = isArcadeCabinet && modelName.EndsWith("02", StringComparison.Ordinal) ? 1f : -1f;
        float[] flat = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++) { flat[i * 3] = vertices[i].x; flat[i * 3 + 1] = vertices[i].y; flat[i * 3 + 2] = vertices[i].z; }
        Material material = CreatePaintMaterial(modelName);

        Mesh mesh = PaintedMesh(modelName, kind, frontSign, flat, normals, triangles, out PaintPart[] parts);
        _painted[modelName] = (flat, triangles, parts);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;

        foreach (var (partName, partTriangles) in splitParts)
        {
            var part = new GameObject(partName);
            part.transform.SetParent(go.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = PaintedMesh(partName, kind, frontSign, flat, normals, partTriangles, out _);
            part.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        Bounds bounds = mesh.bounds;
        float floorLocalY = FloorTopY - go.transform.localPosition.y;
        if (isPinballMachine)
            AddFootprintCollider(go.transform, "ARCADE_COLLIDER_PINBALL", bounds.center, new Vector2(bounds.size.x - 0.08f, bounds.size.z - 0.08f), floorLocalY, ArcadeDimensions.PinballColliderHeight);
        else if (isPacmanCocktail)
        {
            AddFootprintCollider(go.transform, "ARCADE_COLLIDER_PACMAN_TABLE", new Vector3(-0.05f, 0f, 0f), new Vector2(1.42f, 0.92f), floorLocalY, 0.66f);
            AddFootprintCollider(go.transform, "ARCADE_COLLIDER_PACMAN_STOOL_A", new Vector3(-1.05f, 0f, 0f), new Vector2(0.30f, 0.30f), floorLocalY, 0.30f);
            AddFootprintCollider(go.transform, "ARCADE_COLLIDER_PACMAN_STOOL_B", new Vector3(1.05f, 0f, 0f), new Vector2(0.30f, 0.30f), floorLocalY, 0.30f);
        }
        else if (isAirHockey)
            AddFootprintCollider(go.transform, "ARCADE_COLLIDER_AIRHOCKEY", Vector3.zero, new Vector2(0.90f, 1.80f), floorLocalY, 1.0f);
        else
            AddFootprintCollider(go.transform, "ARCADE_COLLIDER_CABINET", bounds.center, new Vector2(bounds.size.x - 0.12f, bounds.size.z - 0.12f), floorLocalY, Mathf.Min(bounds.max.y - floorLocalY, 1.6f));
    }

    private Mesh PaintedMesh(string name, CabinetKind kind, float frontSign, float[] flat, Vector3[] normals, int[] triangles, out PaintPart[] parts)
    {
        parts = CabinetPaint.Classify(kind, flat, triangles, frontSign);
        var (pos, src, uv, tri) = CabinetPaint.Unshare(flat, triangles, parts);
        int n = src.Length;
        var v = new Vector3[n];
        var nn = new Vector3[n];
        var uu = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            nn[i] = normals[src[i]];
            uu[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
        }
        var mesh = new Mesh { name = "ScheduleIArcade_Painted_" + name };
        if (n > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = v;
        mesh.normals = nn;
        mesh.uv = uu;
        mesh.triangles = tri;
        mesh.RecalculateBounds();
        _meshes.Add(mesh);
        return mesh;
    }

    private Material CreatePaintMaterial(string modelName)
    {
        CabinetPaint.Scheme scheme = CabinetPaint.SchemeFor(modelName);
        var texture = new Texture2D(CabinetPaint.AtlasPixels, CabinetPaint.AtlasPixels, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_Paint_" + scheme.Name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            anisoLevel = 4
        };
        texture.LoadRawTextureData(CabinetPaint.BuildAtlas(scheme, modelName.GetHashCode() & 0xFFFF));
        texture.Apply(false, false);
        _textures.Add(texture);
        Material material = LitMaterial("ScheduleIArcade_M_Paint_" + modelName, Color.white, 0.22f);
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        return material;
    }

    private Material LitMaterial(string name, Color color, float smoothness, Color? emission = null)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null)
            throw new InvalidOperationException("Could not find a lit shader for the arcade.");
        var material = new Material(shader) { name = name };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
        if (emission.HasValue && material.HasProperty("_EmissionColor"))
        {
            material.SetColor("_EmissionColor", emission.Value);
            material.EnableKeyword("_EMISSION");
        }
        _materials.Add(material);
        return material;
    }

    /// <summary>Simple upright box collider from the walkable floor up to <paramref name="height"/>.</summary>
    private static void AddFootprintCollider(Transform parent, string name, Vector3 center, Vector2 footprint, float floorLocalY, float height)
    {
        var proxy = new GameObject(name);
        proxy.transform.SetParent(parent, false);
        var box = proxy.AddComponent<BoxCollider>();
        box.center = new Vector3(center.x, floorLocalY + height * 0.5f, center.z);
        box.size = new Vector3(Mathf.Max(0.18f, footprint.x), height, Mathf.Max(0.18f, footprint.y));
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 4096)
            throw new InvalidDataException($"Invalid raw arcade string length {length}.");
        if (length == 0)
            return string.Empty;
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Raw arcade mesh package ended inside a string.");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private void CreateActualDoorMarkers(Transform parent, out Transform spawnMarker, out Transform exitMarker)
    {
        var (dx, dz, nx, nz) = ArcadeLayout.Doorway();
        Vector3 door = new(dx, FloorTopY, dz);
        Vector3 inward = new(nx, 0f, nz);
        var spawn = new GameObject(Constants.SpawnMarkerName);
        spawn.transform.SetParent(parent, false);
        spawn.transform.localPosition = door + inward * 1.2f + Vector3.up * 0.08f;
        spawn.transform.localRotation = Quaternion.LookRotation(inward, Vector3.up);
        spawnMarker = spawn.transform;
        var exit = new GameObject(Constants.ExitMarkerName);
        exit.transform.SetParent(parent, false);
        exit.transform.localPosition = door + inward * 0.6f + Vector3.up * 1.45f;
        exitMarker = exit.transform;
    }

    /// <summary>True when a point in the room root's local space is inside the arcade interior.</summary>
    public static bool IsInsideInteriorLocal(Vector3 local)
    {
        if (local.y < -0.6f || local.y > ArcadeLayout.CeilingTopY + 0.2f)
            return false;
        return ArcadeLayout.IsInsideInterior(local.x, local.z);
    }

    /// <summary>
    /// The Arcade TV console (Pong + Snake), restyled in muted Schedule I colours and standing against
    /// the blank -Z wall facing the room. Built facing local -Z, then turned by the slot yaw.
    /// </summary>
    private void CreateInteractiveArcadeTv(Transform parent)
    {
        LayoutSlot slot = ArcadeLayout.Tv;
        var tv = new GameObject(Constants.CabinetPrefix + "TV");
        tv.transform.SetParent(parent, false);
        tv.transform.localPosition = new Vector3(slot.X, FloorTopY + 0.001f, slot.Z);
        tv.transform.localRotation = Quaternion.Euler(0f, slot.Yaw, 0f);

        Material body = LitMaterial("ScheduleIArcade_M_TvBody", new Color(0.24f, 0.21f, 0.18f, 1f), 0.2f);
        Material bezel = LitMaterial("ScheduleIArcade_M_TvBezel", new Color(0.07f, 0.07f, 0.075f, 1f), 0.3f);
        Material trim = LitMaterial("ScheduleIArcade_M_TvTrim", new Color(0.45f, 0.45f, 0.44f, 1f), 0.35f);
        Material control = LitMaterial("ScheduleIArcade_M_TvControl", new Color(0.55f, 0.20f, 0.16f, 1f), 0.25f);

        CreateTvPiece("ArcadeTv_Base", tv.transform, new Vector3(0f, 0.10f, 0.01f), new Vector3(1.46f, 0.20f, 0.62f), bezel, true);
        CreateTvPiece("ArcadeTv_Console", tv.transform, new Vector3(0f, 0.47f, 0.04f), new Vector3(1.34f, 0.64f, 0.50f), body, true);
        CreateTvPiece("ArcadeTv_ControlShelf", tv.transform, new Vector3(0f, 0.80f, -0.19f), new Vector3(1.28f, 0.11f, 0.38f), trim, true);
        CreateTvPiece("ArcadeTv_ScreenBezel", tv.transform, new Vector3(0f, 1.32f, 0.01f), new Vector3(1.58f, 0.96f, 0.22f), bezel, true);
        CreateTvPiece("ArcadeTv_TopTrim", tv.transform, new Vector3(0f, 1.81f, 0.01f), new Vector3(1.58f, 0.03f, 0.22f), trim, false);
        CreateTvPiece("ArcadeTv_ControlLeft", tv.transform, new Vector3(-0.24f, 0.87f, -0.39f), new Vector3(0.16f, 0.06f, 0.16f), control, false);
        CreateTvPiece("ArcadeTv_ControlRight", tv.transform, new Vector3(0.24f, 0.87f, -0.39f), new Vector3(0.16f, 0.06f, 0.16f), control, false);

        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = Constants.ScreenPrefix + "TV";
        screen.transform.SetParent(tv.transform, false);
        screen.transform.localPosition = new Vector3(0f, 1.32f, -0.1055f);
        screen.transform.localRotation = Quaternion.identity;
        screen.transform.localScale = new Vector3(1.35f, 0.73f, 1f);
        Renderer? screenRenderer = screen.GetComponent<Renderer>();
        if (screenRenderer != null)
            screenRenderer.sharedMaterial = CreateAttractScreenMaterial("ScheduleIArcade_M_TvStandby", "ARCADE TV", "$10 PLAY", new Color32(120, 200, 210, 255), new Color32(220, 120, 110, 255));
    }

    private static void CreateTvPiece(string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material material, bool keepCollider)
    {
        var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
        piece.name = name;
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = localPosition;
        piece.transform.localScale = localScale;
        Renderer? renderer = piece.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;
        if (!keepCollider)
        {
            Collider? collider = piece.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);
        }
    }

    /// <summary>
    /// Doom screens: the playable picture fills the measured CRT opening at 4:3 (the engine's 640x400
    /// framebuffer is shown at 4:3 as on an original CRT), centred in the opening just in front of the
    /// recessed glass. The repainted cabinet keeps the rest of the opening black (bezel matched); no
    /// decorative art is painted anywhere on the screen face.
    /// </summary>
    private void CreateCabinetScreens(Transform root)
    {
        for (int index = 1; index <= 2; index++)
        {
            string suffix = index.ToString("00");
            Transform? cabinet = FindDeepChild(root, Constants.CabinetPrefix + suffix);
            if (cabinet == null)
                continue;
            float front = index == 2 ? 1f : -1f;
            CrtOpening.Fit fit = _painted.TryGetValue("Arcade Machine " + suffix, out var data)
                ? CrtOpening.Measure(data.v, data.t, front)
                : CrtOpening.Default();
            var (n, up) = CrtOpening.Basis(front);
            Vector3 normal = new(n.x, n.y, n.z);
            Vector3 upAxis = new(up.x, up.y, up.z);
            GameObject screen = CreateScreenQuad(Constants.ScreenPrefix + suffix, cabinet);
            screen.transform.localPosition = normal * (fit.GlassDepth + 0.006f) + upAxis * fit.CentreV + Vector3.forward * fit.CentreU;
            screen.transform.localRotation = Quaternion.LookRotation(normal, Vector3.up);
            // Negative X: the quad's visible face points along the face normal (same convention as 0.4.x).
            screen.transform.localScale = new Vector3(-fit.Width, fit.Height, 1f);
            MelonLogger.Msg($"Doom cabinet {index}: CRT opening {(fit.UMax - fit.UMin) * 100f:0.0} x {(fit.VMax - fit.VMin) * 100f:0.0} cm ({(fit.Measured ? "measured on the mesh" : "package default")}); 4:3 picture {fit.Width * 100f:0.0} x {fit.Height * 100f:0.0} cm.");

            var renderer = screen.GetComponent<Renderer>();
            if (renderer != null)
            {
                DoomCabinetEdition edition = index == 1 ? DoomCabinetEdition.Doom1 : DoomCabinetEdition.Doom2;
                bool doomReady = EmbeddedDoomBridge.IsReady(edition, out _);
                renderer.sharedMaterial = CreateAttractScreenMaterial(
                    "ScheduleIArcade_M_CabinetScreen" + suffix,
                    doomReady ? (index == 1 ? "FREEDOOM 1" : "FREEDOOM 2") : "OUT OF ORDER",
                    doomReady ? "$10 PLAY" : "NO CHARGE",
                    doomReady ? new Color32(230, 120, 70, 255) : new Color32(120, 170, 200, 255),
                    doomReady ? new Color32(230, 200, 120, 255) : new Color32(170, 200, 220, 255));
            }
        }
    }

    private void CreatePacmanTableScreen(Transform root)
    {
        Transform? table = FindDeepChild(root, Constants.CabinetPrefix + "PACMAN");
        if (table == null)
            return;
        // Fitted to the table's monitor face under the glass (measured on the mesh, see PacmanScreenLayout).
        var screen = CreateScreenQuad(Constants.ScreenPrefix + "PACMAN", table);
        screen.transform.localPosition = PacmanScreenLayout.LocalCentre;
        screen.transform.localRotation = PacmanScreenLayout.LocalRotation(seatAtPositiveX: true);
        screen.transform.localScale = new Vector3(PacmanScreenLayout.Width, PacmanScreenLayout.Height, 1f);
        Renderer? renderer = screen.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = CreateAttractScreenMaterial("ScheduleIArcade_M_PacmanCocktailScreen", "PAC-MAN", "$10 PLAY", new Color32(240, 210, 80, 255), new Color32(90, 130, 230, 255));
    }

    private void CreatePinballTableScreens(Transform root)
    {
        for (int index = 1; index <= 4; index++)
        {
            Transform? machine = FindDeepChild(root, "Pinball Machine " + index.ToString("00"));
            if (machine == null)
                continue;
            GameObject screen = CreateScreenQuad(Constants.ScreenPrefix + "PINBALL" + index.ToString("00"), machine);
            bool mirroredGlass = index >= 3;
            Vector3 glassNormal = mirroredGlass ? new Vector3(0.148f, 0.989f, 0f) : new Vector3(-0.148f, 0.989f, 0f);
            var (centreX, centreZ) = Games.Pinball.PinballPlayfieldMapping.QuadCentre();
            if (mirroredGlass)
            {
                centreX = -centreX;
                centreZ = -centreZ;
            }
            float planeY = 0.012f + (mirroredGlass ? -1f : 1f) * 0.1497f * (centreX - (mirroredGlass ? 0.10f : -0.10f));
            screen.transform.localPosition = new Vector3(centreX, planeY, centreZ) + glassNormal * 0.009f;
            Quaternion glassRotation = Quaternion.LookRotation(glassNormal, Vector3.right);
            if (mirroredGlass)
                glassRotation *= Quaternion.AngleAxis(180f, Vector3.forward);
            screen.transform.localRotation = glassRotation;
            var (glassWidth, glassLength) = Games.Pinball.PinballPlayfieldMapping.QuadSize();
            screen.transform.localScale = new Vector3(-glassWidth, glassLength, 1f);
            Renderer? renderer = screen.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = CreateAttractScreenMaterial("ScheduleIArcade_M_PinballScreen" + index, "SPACE CADET", "$10 PLAY", new Color32(110, 170, 220, 255), new Color32(230, 200, 110, 255));
        }
    }

    private void CreateAirHockeyScreen(Transform root)
    {
        Transform? table = FindDeepChild(root, Constants.CabinetPrefix + "AIRHOCKEY");
        if (table == null)
            return;
        GameObject screen = CreateScreenQuad(Constants.ScreenPrefix + "AIRHOCKEY", table);
        screen.transform.localPosition = new Vector3(0f, -0.022f, 0f);
        screen.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.right);
        screen.transform.localScale = new Vector3(1.72f, 0.86f, 1f);
        Renderer? renderer = screen.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = CreateAttractScreenMaterial("ScheduleIArcade_M_AirHockeyScreen", "AIR HOCKEY", "2 PLAYERS $10", new Color32(220, 90, 90, 255), new Color32(90, 130, 220, 255));
    }

    /// <summary>Simple title plates on the painted marquee / backglass panels and a sign over the counter.</summary>
    private void CreateDecals(Transform root)
    {
        int placed = 0;
        foreach (var (model, runtime, title, ink) in new[]
        {
            ("Arcade Machine 01", Constants.CabinetPrefix + "01", "DOOM", new Color32(120, 36, 30, 255)),
            ("Arcade Machine 02", Constants.CabinetPrefix + "02", "DOOM II", new Color32(70, 84, 46, 255)),
            ("Pinball Machine 01", "Pinball Machine 01", "PINBALL", new Color32(48, 66, 100, 255)),
            ("Pinball Machine 02", "Pinball Machine 02", "PINBALL", new Color32(48, 66, 100, 255)),
            ("Pinball Machine 03", "Pinball Machine 03", "PINBALL", new Color32(48, 66, 100, 255)),
            ("Pinball Machine 04", "Pinball Machine 04", "PINBALL", new Color32(48, 66, 100, 255)),
        })
        {
            Transform? target = FindDeepChild(root, runtime);
            if (target == null || !_painted.TryGetValue(model, out var data))
                continue;
            if (PlaceDecalOnPart(target, data.v, data.t, data.parts, PaintPart.Marquee, title, ink))
                placed++;
        }

        // "PRIZES" sign on the blank +X wall above the counter.
        LayoutSlot counter = ArcadeLayout.PrizeCounter;
        var sign = GameObject.CreatePrimitive(PrimitiveType.Quad);
        sign.name = "ARCADE_DECAL_PRIZES";
        UnityEngine.Object.Destroy(sign.GetComponent<Collider>());
        sign.transform.SetParent(root, false);
        sign.transform.localPosition = new Vector3(ArcadeLayout.InnerEast - 0.012f, 2.25f, counter.Z);
        sign.transform.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
        sign.transform.localScale = new Vector3(1.2f, 0.3f, 1f);
        sign.GetComponent<Renderer>().sharedMaterial = DecalMaterial("PRIZES", new Color32(214, 204, 178, 255), new Color32(120, 40, 34, 255));
        MelonLogger.Msg($"Arcade decals: {placed} cabinet title plate(s) and the PRIZES sign.");
    }

    private bool PlaceDecalOnPart(Transform target, float[] v, int[] t, PaintPart[] parts, PaintPart wanted, string text, Color32 ink)
    {
        Vector3 normal = Vector3.zero;
        var points = new List<Vector3>();
        for (int k = 0; k < parts.Length; k++)
        {
            if (parts[k] != wanted)
                continue;
            Vector3 a = V(v, t[k * 3]), b = V(v, t[k * 3 + 1]), c = V(v, t[k * 3 + 2]);
            Vector3 cross = Vector3.Cross(b - a, c - a);
            normal += cross; // area weighted
            points.Add(a); points.Add(b); points.Add(c);
        }
        if (points.Count < 3 || normal.sqrMagnitude < 1e-10f)
            return false;
        normal.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, normal);
        if (right.sqrMagnitude < 1e-6f)
            return false;
        right.Normalize();
        Vector3 upAxis = Vector3.Cross(normal, right).normalized;
        float r0 = float.MaxValue, r1 = float.MinValue, u0 = float.MaxValue, u1 = float.MinValue, d = float.MinValue;
        foreach (Vector3 p in points)
        {
            r0 = Mathf.Min(r0, Vector3.Dot(p, right)); r1 = Mathf.Max(r1, Vector3.Dot(p, right));
            u0 = Mathf.Min(u0, Vector3.Dot(p, upAxis)); u1 = Mathf.Max(u1, Vector3.Dot(p, upAxis));
            d = Mathf.Max(d, Vector3.Dot(p, normal));
        }
        float width = (r1 - r0) * 0.86f, height = Mathf.Min((u1 - u0) * 0.6f, width * 0.3f);
        if (width < 0.1f || height < 0.03f)
            return false;
        var decal = GameObject.CreatePrimitive(PrimitiveType.Quad);
        decal.name = "ARCADE_DECAL_" + text.Replace(' ', '_');
        UnityEngine.Object.Destroy(decal.GetComponent<Collider>());
        decal.transform.SetParent(target, false);
        decal.transform.localPosition = right * ((r0 + r1) * 0.5f) + upAxis * ((u0 + u1) * 0.5f) + normal * (d + 0.004f);
        decal.transform.localRotation = Quaternion.LookRotation(-normal, upAxis);
        decal.transform.localScale = new Vector3(width, height, 1f);
        decal.GetComponent<Renderer>().sharedMaterial = DecalMaterial(text, new Color32(214, 204, 178, 255), ink);
        return true;
    }

    private static Vector3 V(float[] v, int i) => new(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);

    /// <summary>Plain plate: cream background, thin border and block lettering (soft, no neon).</summary>
    private Material DecalMaterial(string text, Color32 background, Color32 ink)
    {
        const int width = 128, height = 32;
        var pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = background;
        for (int x = 0; x < width; x++) { pixels[x + width] = ink; pixels[x + (height - 2) * width] = ink; }
        for (int y = 1; y < height - 1; y++) { pixels[y * width + 1] = ink; pixels[y * width + width - 2] = ink; }
        int scale = Mathf.Max(1, Mathf.Min(3, (width - 12) / Mathf.Max(1, text.Length * 6)));
        int textWidth = text.Length * 6 * scale - scale;
        int x0 = (width - textWidth) / 2, y0 = (height - 7 * scale) / 2;
        for (int c = 0; c < text.Length; c++)
        {
            string glyph = PixelFont.Glyph(char.ToUpperInvariant(text[c]));
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < 5; col++)
                    if (glyph[row * 5 + col] == '1')
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                                pixels[(y0 + (6 - row) * scale + sy) * width + x0 + (c * 6 + col) * scale + sx] = ink;
        }
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "ScheduleIArcade_Decal_" + text, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        _textures.Add(texture);
        Material m = LitMaterial("ScheduleIArcade_M_Decal_" + text, Color.white, 0.25f, new Color(0.18f, 0.18f, 0.18f, 1f));
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", texture);
        if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", texture);
        return m;
    }

    /// <summary>A screen quad with a thin trigger box (aim target) instead of the primitive's MeshCollider.</summary>
    private static GameObject CreateScreenQuad(string name, Transform parent)
    {
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = name;
        screen.transform.SetParent(parent, false);
        Collider? meshCollider = screen.GetComponent<Collider>();
        if (meshCollider != null)
            UnityEngine.Object.DestroyImmediate(meshCollider);
        var trigger = screen.AddComponent<BoxCollider>();
        trigger.size = new Vector3(1f, 1f, 0.02f);
        trigger.isTrigger = true;
        return screen;
    }

    private Material CreateAttractScreenMaterial(string materialName, string title, string subtitle, Color32 accent, Color32 accent2)
    {
        Shader? shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null)
            throw new InvalidOperationException("Could not find a shader for arcade screens.");
        var material = new Material(shader) { name = materialName };
        _materials.Add(material);
        Texture2D texture = ArcadeAttractScreenFactory.Create("ScheduleIArcade_" + materialName, 128, 72, title, subtitle, accent, accent2);
        _textures.Add(texture);
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        if (material.HasProperty("_EmissionMap"))
        {
            material.SetTexture("_EmissionMap", texture);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_EMISSION");
        }
        return material;
    }

    /// <summary>Warm white ceiling lighting so the interior reads through the street windows.</summary>
    private static void CreateInteriorLights(Transform parent)
    {
        Color warm = new(1.00f, 0.93f, 0.82f, 1f);
        foreach (var (x, z) in ArcadeLayout.CeilingLights)
        {
            var go = new GameObject("ArcadeInteriorLight");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(x, ArcadeLayout.CeilingY - 0.35f, z);
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 5.5f;
            light.intensity = 1.5f;
            light.color = warm;
            light.shadows = LightShadows.None;
        }
    }

    private static Transform? FindDeepChild(Transform root, string name)
    {
        if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            return root;
        int childCount = root.childCount;
        for (int i = 0; i < childCount; i++)
        {
            Transform? result = FindDeepChild(root.GetChild(i), name);
            if (result != null)
                return result;
        }
        return null;
    }

    public void Dispose()
    {
        foreach (Material material in _materials)
            if (material != null) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        foreach (Texture2D texture in _textures)
            if (texture != null) UnityEngine.Object.Destroy(texture);
        _textures.Clear();
        foreach (Mesh mesh in _meshes)
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
        _meshes.Clear();
        _painted.Clear();
        _surfaces.Dispose();
        FloorMesh = null;
    }
}

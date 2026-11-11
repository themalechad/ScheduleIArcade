using MelonLoader;
using ScheduleIArcade.Core;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Loads the user's supplied arcade FBX after it has been converted offline into a very small
/// runtime mesh format. Unity player builds cannot import FBX files directly, so this lets the
/// test DLL use the real scene geometry/textures without requiring the user to install Unity Editor.
/// </summary>
internal sealed class RawArcadeSceneService : IDisposable
{
    private static readonly byte[] ExpectedMagic =
    {
        (byte)'S', (byte)'1', (byte)'A', (byte)'R', (byte)'A', (byte)'W', (byte)'0', (byte)'1'
    };

    private readonly List<Mesh> _meshes = new();
    private readonly List<Material> _materials = new();
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Material> _materialByModel = new(StringComparer.OrdinalIgnoreCase);

    // Top-down proportions taken from the real Northtown Arcade roof screenshot. Only the ROOM
    // shell/floor are reshaped. Arcade machines and props are moved rigidly to their corresponding
    // positions so their meshes are never stretched/warped again.
    private const float FootprintScaleX = 1.22f;
    private const float FootprintScaleZ = 1.34f;

    // The real building is a five-sided footprint with a north-west diagonal/chamfer.
    private const float SourceWest = -2.611f;
    private const float SourceEast = 2.611f;
    private const float SourceSouth = -2.997f;
    private const float SourceNorth = 2.997f;
    private const float ChamferStartZ = 1.55f;
    private const float NorthWestX = -0.80f;

    // User-directed M2.37 layout from the second annotated screenshot. The screenshot made the
    // intended wall relationships clear:
    //   green  = pinball pair on the long WEST wall
    //   orange = upright arcade cabinet on the SOUTH/pixel-art wall
    //   blue   = Pac-Man cocktail table left of center
    //   red    = double doors cut into the NORTH-WEST CHAMFER wall itself
    // Props are re-centered onto their own pivots before these transforms are applied, so moving
    // them never makes them orbit around the original FBX scene origin.
    private static readonly Vector3 RequestedPinballCenter = new(-2.67f, 0.745f, 0.05f);
    private const float RequestedPinballYaw = 180f;
    private static readonly Vector3 RequestedArcadeCabinetCenter = new(-2.00f, 0.779f, -3.58f);
    private const float RequestedArcadeCabinetYaw = 90f;
    private static readonly Vector3 RequestedPacmanCenter = new(0.00f, 0.317f, 0.00f);
    private const float RequestedPacmanYaw = 90f;
    private const float RequestedChamferDoorT = 0.53f;
    private const float RequestedDoorWidth = 1.90f;
    private const float RequestedDoorHeight = 2.18f;
    private float _doorT = RequestedChamferDoorT;

    // Raised interior (0.4.1): ceiling plane, generated wall height and wall centre, room-local metres.
    private const float CeilingY = ArcadeDimensions.InteriorCeilingHeight;
    private const float WallHeight = ArcadeDimensions.InteriorWallHeight;
    private const float WallCenterY = WallHeight * 0.5f;
    /// <summary>Top of the walkable floor (the visible checkerboard plane).</summary>
    private const float FloorTopY = 0.043f;


    /// <summary>
    /// Environment islands removed at load (source-mesh coordinates, before the footprint stretch):
    /// the free-standing white floor-to-ceiling pipe and the old black panelled door in the back
    /// corner beside the Street Fighter art, plus the dark window pair above that door. Only whole
    /// connected pieces that lie entirely inside a box are removed; the room shell is untouched.
    /// </summary>
    private static readonly (string label, Vector3 min, Vector3 max)[] EnvironmentClutter =
    {
        ("back-corner pipe", new Vector3(2.37f, -0.02f, -2.88f), new Vector3(2.54f, 2.74f, -2.70f)),
        ("back-corner panelled door", new Vector3(2.43f, -0.01f, -2.84f), new Vector3(2.61f, 1.97f, -1.76f)),
        ("windows above the back-corner door", new Vector3(2.47f, 1.94f, -2.19f), new Vector3(2.59f, 2.60f, -1.11f)),
    };

    /// <summary>Static 3D flipper meshes baked on the pinball playfield (machines 01/02; 03/04 mirrored).</summary>
    private static readonly (Vector3 min, Vector3 max) PinballStaticFlippers = (new Vector3(-0.385f, -0.035f, -0.09f), new Vector3(-0.315f, 0.005f, 0.09f));

    public bool IsAvailable => ModPaths.Exists(ModPaths.RawSceneMeshPath);

    public bool TryPopulate(GameObject root, out Transform? spawnMarker, out Transform? exitMarker)
    {
        spawnMarker = null;
        exitMarker = null;

        if (!IsAvailable)
            return false;

        try
        {
            LoadOptionalBlenderLayout();
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

            MelonLogger.Msg($"Reshaped ONLY the room shell to the real five-sided Arcade footprint (north-west chamfer, x={FootprintScaleX:0.00}, z={FootprintScaleZ:0.00}); arcade machines remain rigid and undistorted.");
            if (version >= 2)
                MelonLogger.Msg("Loaded Blender-authored arcade prop transforms from raw scene package v2.");
            else
                MelonLogger.Msg("Applied corrected M2.37 fallback layout: pinballs=green long-wall row, upright cabinet=orange pixel-art wall, Pac-Man=blue lane, double door=red chamfer wall.");

            // Do not use the downloaded Environment mesh itself as collision. It contains
            // decorative geometry (including the small center prop shown in testing) and a
            // triangle collider there can wedge the CharacterController between nearby machines.
            // Build a simple room shell instead: reliable floor + walls, with the real door opening
            // left clear. Visual geometry is unchanged.
            CreateEnvironmentCollisionShell(root.transform);

            if (stream.Position != stream.Length)
                MelonLogger.Warning($"Raw arcade mesh package has {stream.Length - stream.Position} unread byte(s).");

            // The supplied FBX intentionally leaves two sides open. Close those sides with
            // collidable walls using a clean slice of the original environment texture atlas so
            // the runtime test room is a complete interior instead of looking out into the sky.
            CreateMissingWalls(root.transform);
            CreateStreetFighterDoorInfill(root.transform);

            // Add a visible checkerboard overlay floor that is slightly larger than the source
            // floor and the generated edge strips. This hides any remaining seams and gives the
            // room a clearer arcade-style floor instead of reusing the wall texture.
            CreateCheckerboardOverlayFloor(root.transform);

            // One flat, seamless walkable floor collider at the visible floor plane.
            CreateWalkableFloor(root.transform);

            // Replace the old single-door setup with a purpose-built double entrance on the
            // blank wall requested in testing. The entire room is later rotated so entering
            // while facing north-west preserves that heading into the arcade.
            CreateInteriorPolish(root.transform);
            CreateInteractiveArcadeTv(root.transform);
            CreatePacmanTableScreen(root.transform);
            CreateActualDoorMarkers(root.transform, out spawnMarker, out exitMarker);

            // The downloaded scene has one actual upright arcade cabinet. Turn that cabinet into
            // cabinet 01 and put the animated test framebuffer directly over its screen area.
            // A helper-screen failure must never throw away the entire imported room.
            try
            {
                CreateCabinetScreens(root.transform);
                CreatePinballTableScreens(root.transform);
                CreateAirHockeyScreen(root.transform);
            }
            catch (Exception screenEx)
            {
                MelonLogger.Warning($"Real arcade room loaded, but cabinet screen setup failed: {screenEx.Message}");
            }

            // A couple of local lights ensure the textured room is readable even though this room
            // is moved into an isolated pocket away from the normal map lighting setup.
            CreateInteriorLights(root.transform);

            Physics.SyncTransforms();
            MelonLogger.Msg($"Loaded real arcade FBX geometry/textures from runtime asset package ({meshCount} meshes).");
            return spawnMarker != null && exitMarker != null;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"Failed to load raw arcade scene assets: {ex}");
            Dispose();
            return false;
        }
    }

    private void LoadOptionalBlenderLayout()
    {
        _doorT = RequestedChamferDoorT;
        string layoutPath = ModPaths.Find(ModPaths.RawSceneLayoutPath);
        if (!ModPaths.Exists(layoutPath))
            return;

        try
        {
            foreach (string rawLine in ModFiles.ReadAllLines(layoutPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (key.Equals("door_t", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                {
                    _doorT = Mathf.Clamp(parsed, 0.36f, 0.64f);
                }
            }

            MelonLogger.Msg($"Blender layout settings loaded: chamfer door t={_doorT:0.000}.");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not read optional Blender layout file '{ModPaths.RawSceneLayoutPath}': {ex.Message}");
            _doorT = RequestedChamferDoorT;
        }
    }

    private static void ValidateHeader(BinaryReader reader)
    {
        byte[] actual = reader.ReadBytes(ExpectedMagic.Length);
        if (actual.Length != ExpectedMagic.Length)
            throw new EndOfStreamException("Raw arcade mesh package ended before its header.");

        for (int i = 0; i < ExpectedMagic.Length; i++)
        {
            if (actual[i] != ExpectedMagic[i])
                throw new InvalidDataException("Raw arcade mesh package has an invalid header.");
        }
    }

    private void ReadAndCreateMesh(BinaryReader reader, Transform parent, int packageVersion)
    {
        string modelName = ReadString(reader);
        string albedoName = ReadString(reader);
        string normalName = ReadString(reader);
        string emissionName = ReadString(reader);
        bool isEnvironment = modelName.Equals("Enviorment", StringComparison.OrdinalIgnoreCase);

        Vector3 authoredPosition = Vector3.zero;
        float authoredYaw = 0f;
        if (packageVersion >= 2)
        {
            authoredPosition = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            authoredYaw = reader.ReadSingle();
        }

        // Windows treats Arcade_base_texture.jpg and arcade_base_texture.jpg as the same file.
        // Use the uniquely renamed Air Hockey copies so Street Fighter art can never be assigned
        // to the air-hockey table on Windows.
        if (modelName.Equals("Air Hockey", StringComparison.OrdinalIgnoreCase))
        {
            albedoName = "air_hockey_base_texture.jpg";
            normalName = "air_hockey_base_texture_NRM.jpg";
            emissionName = "air_hockey_base_texture_emission.jpg";
        }

        int vertexCount = reader.ReadInt32();
        if (vertexCount <= 0 || vertexCount > 200000)
            throw new InvalidDataException($"Invalid vertex count {vertexCount} for '{modelName}'.");

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];

        for (int i = 0; i < vertexCount; i++)
            vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        for (int i = 0; i < vertexCount; i++)
            normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        for (int i = 0; i < vertexCount; i++)
            uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());

        Vector3 sourceCenter = CalculateVertexBoundsCenter(vertices);

        if (isEnvironment)
        {
            // Only architecture is deformed to the real five-sided building footprint.
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = MapPointToRealArcadeFootprint(vertices[i]);
        }
        else if (packageVersion == 1)
        {
            // Legacy raw packages contain scene-space prop vertices. Give every prop a sane local
            // pivot before applying the corrected fallback layout. Blender package v2 already
            // exports prop vertices around their own origins and supplies position/yaw separately.
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] -= sourceCenter;
        }

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

        bool isAirHockey = modelName.Equals("Air Hockey", StringComparison.OrdinalIgnoreCase);
        var splitParts = new List<(string name, int[] triangles)>();
        if (isEnvironment)
        {
            // Boxes were measured in source coordinates; south of the chamfer the footprint stretch
            // is a plain scale, so the stretched boxes match the stretched vertices exactly.
            triangles = MeshIslands.RemoveIslandsInside(vertices, triangles, EnvironmentClutter.Select(c => (c.label, MapBox(c.min), MapBox(c.max))).ToArray(), out string removed);
            if (removed.Length > 0)
                MelonLogger.Msg($"Removed environment clutter: {removed}.");
        }
        else if (modelName.StartsWith("Pinball Machine", StringComparison.OrdinalIgnoreCase))
        {
            // The 2D table draws and animates its own flippers; the dark static 3D flippers on the
            // cabinet model would show a second, frozen pair.
            Vector3 fMin = PinballStaticFlippers.min, fMax = PinballStaticFlippers.max;
            var boxes = new[]
            {
                ("static flippers", fMin, fMax),
                ("static flippers (mirrored)", new Vector3(-fMax.x, fMin.y, -fMax.z), new Vector3(-fMin.x, fMax.y, -fMin.z)),
            };
            triangles = MeshIslands.RemoveIslandsInside(vertices, triangles, boxes, out string removed);
            if (removed.Length > 0)
                MelonLogger.Msg($"{modelName}: hid the baked 3D flipper meshes ({removed}).");
        }
        else if (modelName.Equals("Table Top", StringComparison.OrdinalIgnoreCase))
        {
            int flipped = PacmanLogoFix.FlipLogoReadingAxis(ref vertices, ref normals, ref uvs, triangles);
            if (flipped > 0)
                MelonLogger.Msg($"Pac-Man table: un-mirrored both PAC-MAN side logos ({flipped} triangle(s)).");
        }
        else if (isAirHockey)
        {
            // Split the overhead score arch/display and the loose mallets + puck off the table body so
            // the top-down session camera can see the play surface and the drawn pieces are not doubled.
            triangles = MeshIslands.Split(vertices, triangles, island =>
                island.min.y > -0.12f && island.max.y > 0.30f ? "AirHockey_Scoreboard"
                : (island.max - island.min).magnitude < 0.16f && island.min.y > -0.06f && island.max.y < 0.03f ? "AirHockey_TablePieces"
                : null, splitParts);
        }

        var mesh = new Mesh { name = "ScheduleIArcade_" + modelName };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        if (isEnvironment)
            mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        _meshes.Add(mesh);

        bool isArcadeCabinet = modelName.StartsWith("Arcade Machine", StringComparison.OrdinalIgnoreCase);
        bool isPinballMachine = modelName.StartsWith("Pinball Machine", StringComparison.OrdinalIgnoreCase);
        bool isPacmanCocktail = modelName.Equals("Table Top", StringComparison.OrdinalIgnoreCase);
        string runtimeName = modelName;
        if (isArcadeCabinet)
        {
            int cabinetNumber = 1;
            string suffix = modelName.Substring("Arcade Machine".Length).Trim();
            if (!int.TryParse(suffix, out cabinetNumber) || cabinetNumber <= 0)
                cabinetNumber = 1;
            runtimeName = Constants.CabinetPrefix + cabinetNumber.ToString("00");
        }
        else if (isPacmanCocktail)
        {
            runtimeName = Constants.CabinetPrefix + "PACMAN";
        }
        else if (isAirHockey)
        {
            runtimeName = Constants.CabinetPrefix + "AIRHOCKEY";
        }

        var go = new GameObject(runtimeName);
        go.transform.SetParent(parent, false);

        if (!isEnvironment)
        {
            if (packageVersion >= 2)
            {
                // 0.4.1 floor plan (ArcadeLayout): pinball bank, Doom pair, open-floor tables.
                LayoutSlot? slot = ArcadeLayout.Machine(modelName);
                if (slot != null)
                {
                    authoredPosition.x = slot.X;
                    authoredPosition.z = slot.Z;
                    authoredYaw = slot.Yaw;
                }
                go.transform.localPosition = authoredPosition;
                go.transform.localRotation = Quaternion.Euler(0f, authoredYaw, 0f);
            }
            else
            {
                Vector3 targetCenter = MapPointToRealArcadeFootprint(sourceCenter);
                float yaw = 0f;

                if (modelName.Equals("Pinball Machine", StringComparison.OrdinalIgnoreCase))
                {
                    targetCenter = RequestedPinballCenter;
                    yaw = RequestedPinballYaw;
                }
                else if (modelName.Equals("Arcade Machine", StringComparison.OrdinalIgnoreCase))
                {
                    targetCenter = RequestedArcadeCabinetCenter;
                    yaw = RequestedArcadeCabinetYaw;
                }
                else if (modelName.Equals("Table Top", StringComparison.OrdinalIgnoreCase))
                {
                    targetCenter = RequestedPacmanCenter;
                    yaw = RequestedPacmanYaw;
                }

                go.transform.localPosition = targetCenter;
                go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            }
        }

        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;

        var renderer = go.AddComponent<MeshRenderer>();
        Material material = CreateMaterial(modelName, albedoName, normalName, emissionName);
        renderer.sharedMaterial = material;
        _materialByModel[modelName] = material;

        foreach (var (partName, partTriangles) in splitParts)
        {
            var partMesh = new Mesh { name = "ScheduleIArcade_" + partName };
            partMesh.vertices = vertices;
            partMesh.normals = normals;
            partMesh.uv = uvs;
            partMesh.triangles = partTriangles;
            partMesh.RecalculateBounds();
            _meshes.Add(partMesh);
            var part = new GameObject(partName);
            part.transform.SetParent(go.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = partMesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        bool isFakeVending = modelName.Equals("Vendind Machine", StringComparison.OrdinalIgnoreCase);
        bool isOutOfOrderCoinMachine = modelName.Equals("Crane Game", StringComparison.OrdinalIgnoreCase);
        if (isFakeVending || isOutOfOrderCoinMachine)
        {
            renderer.enabled = false;
        }
        else if (!isEnvironment)
        {
            Bounds bounds = mesh.bounds;
            float floorLocalY = FloorTopY - go.transform.localPosition.y;
            if (isPinballMachine)
            {
                // One inset footprint box, 1.0 m tall: no capsules, no legs/plunger/backbox detail.
                AddFootprintCollider(go.transform, "ARCADE_COLLIDER_PINBALL", bounds.center, new Vector2(bounds.size.x - 0.08f, bounds.size.z - 0.08f), floorLocalY, ArcadeDimensions.PinballColliderHeight);
            }
            else if (isPacmanCocktail)
            {
                // Table body only (x -0.80..0.70 in the mesh) plus one small box per end stool.
                AddFootprintCollider(go.transform, "ARCADE_COLLIDER_PACMAN_TABLE", new Vector3(-0.05f, 0f, 0f), new Vector2(1.42f, 0.92f), floorLocalY, 0.66f);
                AddFootprintCollider(go.transform, "ARCADE_COLLIDER_PACMAN_STOOL_A", new Vector3(-1.05f, 0f, 0f), new Vector2(0.30f, 0.30f), floorLocalY, 0.30f);
                AddFootprintCollider(go.transform, "ARCADE_COLLIDER_PACMAN_STOOL_B", new Vector3(1.05f, 0f, 0f), new Vector2(0.30f, 0.30f), floorLocalY, 0.30f);
            }
            else
            {
                const float horizontalInset = 0.06f;
                float height = Mathf.Min(bounds.max.y - floorLocalY, 1.6f);
                if (isAirHockey)
                {
                    // Table body only (x +-0.47, z +-0.92); the score arch overhangs it.
                    AddFootprintCollider(go.transform, "ARCADE_COLLIDER_AIRHOCKEY", Vector3.zero, new Vector2(0.90f, 1.80f), floorLocalY, 1.0f);
                }
                else
                {
                    AddFootprintCollider(go.transform, "ARCADE_COLLIDER_" + (isArcadeCabinet ? "CABINET" : "PROP"), bounds.center,
                        new Vector2(bounds.size.x - horizontalInset * 2f, bounds.size.z - horizontalInset * 2f), floorLocalY, height);
                }
            }
        }
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

    private static Vector3 MapBox(Vector3 sourcePoint) => new(sourcePoint.x * FootprintScaleX, sourcePoint.y, sourcePoint.z * FootprintScaleZ);

    private static Vector3 CalculateVertexBoundsCenter(Vector3[] vertices)
    {
        if (vertices.Length == 0)
            return Vector3.zero;

        Vector3 min = vertices[0];
        Vector3 max = vertices[0];
        for (int i = 1; i < vertices.Length; i++)
        {
            min = Vector3.Min(min, vertices[i]);
            max = Vector3.Max(max, vertices[i]);
        }
        return (min + max) * 0.5f;
    }

    private static int[] RemoveLegacyFbxDoorTriangles(Vector3[] vertices, int[] triangles)
    {
        // The old single gray door is baked into the FBX environment mesh rather than exported as
        // a separate GameObject. Remove only the triangles occupying that doorway section, then
        // the generated purple wall below covers the opening. This disables the asset itself
        // instead of simply drawing a second door in front of it.
        var kept = new List<int>(triangles.Length);
        int removed = 0;

        float minX = FX(2.02f);
        float minZ = FZ(1.20f);
        float maxZ = FZ(3.10f);

        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]];
            Vector3 b = vertices[triangles[i + 1]];
            Vector3 c = vertices[triangles[i + 2]];
            Vector3 center = (a + b + c) / 3f;

            bool legacyDoor = center.x >= minX
                && center.z >= minZ
                && center.z <= maxZ
                && center.y >= -0.05f
                && center.y <= 2.65f;

            if (legacyDoor)
            {
                removed += 3;
                continue;
            }

            kept.Add(triangles[i]);
            kept.Add(triangles[i + 1]);
            kept.Add(triangles[i + 2]);
        }

        if (removed > 0)
            MelonLogger.Msg($"Disabled baked FBX single-door geometry ({removed / 3} triangle(s) removed from the environment mesh).");

        return kept.ToArray();
    }

    private static Vector3 MapPointToRealArcadeFootprint(Vector3 point)
    {
        float mappedX = point.x;
        if (point.z > ChamferStartZ)
        {
            float zT = Mathf.InverseLerp(ChamferStartZ, SourceNorth, point.z);
            float westAtZ = Mathf.Lerp(SourceWest, NorthWestX, zT);
            float xT = Mathf.InverseLerp(SourceWest, SourceEast, point.x);
            mappedX = Mathf.Lerp(westAtZ, SourceEast, xT);
        }

        return new Vector3(mappedX * FootprintScaleX, point.y, point.z * FootprintScaleZ);
    }

    private static Vector3 CalculateRigidPropOffset(Vector3 sourceCenter)
    {
        Vector3 targetCenter = MapPointToRealArcadeFootprint(sourceCenter);
        return new Vector3(targetCenter.x - sourceCenter.x, 0f, targetCenter.z - sourceCenter.z);
    }

    private static float FX(float sourceX) => sourceX * FootprintScaleX;
    private static float FZ(float sourceZ) => sourceZ * FootprintScaleZ;
    private static float FSX(float sourceWidth) => sourceWidth * FootprintScaleX;
    private static float FSZ(float sourceDepth) => sourceDepth * FootprintScaleZ;

    private void CreateEnvironmentCollisionShell(Transform parent)
    {
        // Simple continuous collision shell. The old east-side single-door opening is closed;
        // the usable entrance is now the generated double door on the requested wall.
        CreateInvisibleSolid(
            "RawArcade_Collision_Ceiling",
            parent,
            new Vector3(FX(-0.06f), CeilingY + 0.12f, FZ(0.08f)),
            new Vector3(FSX(6.20f), 0.24f, FSZ(7.05f)));

        CreateInvisibleSolid(
            "RawArcade_Collision_South",
            parent,
            new Vector3(0f, WallCenterY, FZ(-3.02f)),
            new Vector3(FSX(5.34f), WallHeight, FSZ(0.16f)));

        CreateInvisibleSolid(
            "RawArcade_Collision_East",
            parent,
            new Vector3(FX(2.64f), WallCenterY, 0f),
            new Vector3(FSX(0.16f), WallHeight, FSZ(6.12f)));
    }

    private static void CreateInvisibleSolid(string name, Transform parent, Vector3 localPosition, Vector3 localScale)
    {
        var solid = GameObject.CreatePrimitive(PrimitiveType.Cube);
        solid.name = name;
        solid.transform.SetParent(parent, false);
        solid.transform.localPosition = localPosition;
        solid.transform.localScale = localScale;

        var renderer = solid.GetComponent<Renderer>();
        if (renderer != null)
            renderer.enabled = false;
    }

    private Material CreateMaterial(string modelName, string albedoName, string normalName, string emissionName)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Unlit/Texture");
        if (shader == null)
            throw new InvalidOperationException("Could not find a usable shader for the raw arcade scene.");

        var material = new Material(shader) { name = "ScheduleIArcade_M_" + modelName };
        _materials.Add(material);

        Texture2D? albedo = LoadTexture(albedoName);
        if (albedo != null)
        {
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", albedo);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", albedo);
        }

        Texture2D? normal = LoadTexture(normalName);
        if (normal != null && material.HasProperty("_BumpMap"))
        {
            material.SetTexture("_BumpMap", normal);
            material.EnableKeyword("_NORMALMAP");
        }

        Texture2D? emission = LoadTexture(emissionName);
        if (emission != null && material.HasProperty("_EmissionMap"))
        {
            material.SetTexture("_EmissionMap", emission);
            // Keep the pack's emissive artwork visible without blowing out the 512px source maps.
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", new Color(0.45f, 0.45f, 0.45f, 1f));
            material.EnableKeyword("_EMISSION");
        }

        // Most downloaded room meshes are single-sided. Disable back-face culling so the player
        // can see the inside of the walls even if FBX winding differs from Unity's convention.
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0f);
        if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 0f);

        return material;
    }

    private Texture2D? LoadTexture(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        if (_textures.TryGetValue(fileName, out Texture2D? cached) && cached != null)
            return cached;

        string path = ModPaths.Find(Path.Combine(ModPaths.RawSceneTexturesRoot, fileName));
        byte[]? bytes = ModFiles.ReadAllBytes(path);
        if (bytes == null || bytes.Length == 0)
        {
            MelonLogger.Warning($"Raw arcade texture is missing: {path}");
            return null;
        }

        var texture = new Texture2D(2, 2) { name = "ScheduleIArcade_" + Path.GetFileNameWithoutExtension(fileName) };
        if (!ImageConversion.LoadImage(texture, bytes, false))
        {
            UnityEngine.Object.Destroy(texture);
            MelonLogger.Warning($"Unity could not decode raw arcade texture: {path}");
            return null;
        }

        texture.wrapMode = TextureWrapMode.Repeat;
        // The source pack is only 512x512 for most props and 1024x1024 for the room, so we cannot
        // invent extra source detail. Trilinear filtering + high anisotropy keeps those original
        // pixels as crisp as possible on angled cabinet/floor surfaces without downscaling them.
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 16;
        _textures[fileName] = texture;
        return texture;
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

    private void CreateMissingWalls(Transform parent)
    {
        Material? wallMaterial = CreateWallFillMaterial();

        // Long west wall: pinballs sit along this wall in the green-marked row.
        CreateMissingWall(
            "RawArcade_Wall_West_Lower",
            parent,
            new Vector3(FX(-2.67f), WallCenterY, FZ(-0.72f)),
            new Vector3(FSX(0.18f), WallHeight, FSZ(4.72f)),
            wallMaterial);

        // The RED annotation is on the diagonal/chamfer wall, not the north or west wall. Cut a
        // real double-door opening directly into that diagonal face.
        Vector3 chamferA = new(FX(-2.67f), 0f, FZ(1.56f));
        Vector3 chamferB = new(FX(-0.84f), 0f, FZ(3.06f));
        Vector3 delta = chamferB - chamferA;
        float chamferLength = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
        Vector3 tangent = delta / chamferLength;
        Vector3 doorCenter = Vector3.Lerp(chamferA, chamferB, _doorT);
        float halfDoor = RequestedDoorWidth * 0.5f;
        Vector3 doorStart = doorCenter - tangent * halfDoor;
        Vector3 doorEnd = doorCenter + tangent * halfDoor;

        CreateWallSegment(
            "RawArcade_Wall_Chamfer_BeforeDoor",
            parent,
            chamferA,
            doorStart,
            WallHeight,
            FSX(0.18f),
            wallMaterial);
        CreateWallSegment(
            "RawArcade_Wall_Chamfer_AfterDoor",
            parent,
            doorEnd,
            chamferB,
            WallHeight,
            FSX(0.18f),
            wallMaterial);
        CreateWallSegmentAtY(
            "RawArcade_Wall_Chamfer_DoorLintel",
            parent,
            doorStart,
            doorEnd,
            RequestedDoorHeight + (WallHeight - RequestedDoorHeight) * 0.5f,
            WallHeight - RequestedDoorHeight,
            FSX(0.18f),
            wallMaterial);

        CreateDoubleDoorOnChamfer(parent, doorCenter, tangent, RequestedDoorWidth, RequestedDoorHeight);

        // North wall is now solid again.
        float northZ = FZ(3.06f);
        float northLeft = FX(-0.84f);
        float northRight = FX(2.67f);
        CreateMissingWall(
            "RawArcade_Wall_North_Full",
            parent,
            new Vector3((northLeft + northRight) * 0.5f, WallCenterY, northZ),
            new Vector3(northRight - northLeft, WallHeight, FSZ(0.18f)),
            wallMaterial);

        // The imported shell's south and east walls stop at ~2.66 m. Continue them up to the raised
        // ceiling with the same atlas wall finish (visual only; the invisible shell collides).
        if (wallMaterial != null)
        {
            const float bandBottom = 2.58f;
            float bandHeight = WallHeight - bandBottom;
            CreateDecorativeCuboid("RawArcade_Wall_South_Upper", parent,
                new Vector3(FX(-0.015f), bandBottom + bandHeight * 0.5f, FZ(-2.995f)),
                new Vector3(FSX(5.32f), bandHeight, 0.05f), Quaternion.identity, wallMaterial);
            CreateDecorativeCuboid("RawArcade_Wall_East_Upper", parent,
                new Vector3(FX(2.56f), bandBottom + bandHeight * 0.5f, FZ(0.02f)),
                new Vector3(0.05f, bandHeight, FSZ(6.10f)), Quaternion.identity, wallMaterial);
        }
    }

    private void CreateStreetFighterDoorInfill(Transform parent)
    {
        // The source door was a separate east-wall component that also filled a real opening in
        // the background mesh. Moving it to the right exposed that former left-hand opening.
        // Restore the exact old aperture with a finished, atlas-compatible wall panel and frame;
        // the moved door and the Street Fighter mural remain independent and unmirrored.
        Material? fill = CreateWallFillMaterial();
        if (fill == null)
            return;

        // A flush wall patch only: the dark frame that used to outline it read as a second door.
        Vector3 center = new(FX(2.52f) - 0.055f, 0.99f, FZ(2.332f));
        Vector3 panelSize = new(FSX(0.12f), 1.93f, FSZ(1.04f));
        CreateDecorativeCuboid("RawArcade_StreetFighter_OldDoorInfill", parent, center, panelSize, Quaternion.identity, fill);

        // Continuous wall where the back-corner door, its windows and the pipe were removed.
        CreateDecorativeCuboid("RawArcade_BackCorner_WallPatch", parent,
            new Vector3(FX(2.545f), 1.30f, FZ(-2.33f)),
            new Vector3(0.04f, 2.60f, FSZ(1.28f)), Quaternion.identity, fill);
    }

    private void CreateWallSegment(string name, Transform parent, Vector3 localA, Vector3 localB, float height, float thickness, Material? material)
    {
        Vector3 delta = localB - localA;
        float length = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = name;
        wall.transform.SetParent(parent, false);
        wall.transform.localPosition = (localA + localB) * 0.5f + Vector3.up * (height * 0.5f);
        wall.transform.localRotation = Quaternion.Euler(0f, Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg, 0f);
        wall.transform.localScale = new Vector3(thickness, height, length + 0.12f);

        var renderer = wall.GetComponent<Renderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;
    }

    private void CreateWallSegmentAtY(string name, Transform parent, Vector3 localA, Vector3 localB, float yCenter, float height, float thickness, Material? material)
    {
        Vector3 delta = localB - localA;
        float length = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = name;
        wall.transform.SetParent(parent, false);
        wall.transform.localPosition = (localA + localB) * 0.5f + Vector3.up * yCenter;
        wall.transform.localRotation = Quaternion.Euler(0f, Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg, 0f);
        wall.transform.localScale = new Vector3(thickness, height, length + 0.04f);

        var renderer = wall.GetComponent<Renderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;
    }

    private void CreateMissingWall(string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material? material)
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = name;
        wall.transform.SetParent(parent, false);
        wall.transform.localPosition = localPosition;
        wall.transform.localScale = localScale;

        var renderer = wall.GetComponent<Renderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;
    }

    private void CreateFloorEdgeExtensions(Transform parent, Material? material)
    {
        // Original visible floor bounds are approximately X=-2.611..2.611 and
        // Z=-2.997..2.997. These low-profile strips overlap the imported floor by a few
        // centimetres and continue underneath the generated walls. This removes the visible
        // floor/wall seam without extending through the real +X exit doorway.
        CreateFloorEdge(
            "RawArcade_Floor_West_Extension",
            parent,
            new Vector3(-2.78f, -0.015f, 0f),
            new Vector3(0.44f, 0.06f, 6.56f),
            material);

        CreateFloorEdge(
            "RawArcade_Floor_North_Extension",
            parent,
            new Vector3(0f, -0.015f, 3.17f),
            new Vector3(5.96f, 0.06f, 0.44f),
            material);
    }

    private void CreateFloorEdge(string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material? material)
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = name;
        floor.transform.SetParent(parent, false);
        floor.transform.localPosition = localPosition;
        floor.transform.localScale = localScale;

        var renderer = floor.GetComponent<Renderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;
    }

    private void CreateCheckerboardOverlayFloor(Transform parent)
    {
        var overlay = new GameObject("RawArcade_CheckerboardFloor");
        overlay.transform.SetParent(parent, false);

        // Exact five-sided floor outline matching the exterior roof shape. Coordinates are scaled
        // here directly; the room root stays at unit scale so cabinet meshes cannot be distorted.
        Vector3[] vertices =
        {
            new Vector3(FX(-2.86f), 0.043f, FZ(-3.25f)),
            new Vector3(FX(-2.86f), 0.043f, FZ( 1.67f)),
            new Vector3(FX(-0.88f), 0.043f, FZ( 3.25f)),
            new Vector3(FX( 2.86f), 0.043f, FZ( 3.25f)),
            new Vector3(FX( 2.86f), 0.043f, FZ(-3.25f)),
        };

        float minX = FX(-2.86f);
        float maxX = FX(2.86f);
        float minZ = FZ(-3.25f);
        float maxZ = FZ(3.25f);
        Vector2[] uv = new Vector2[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            // About 55 cm per tile: large enough to read cleanly instead of turning into noisy
            // high-frequency checks at normal gameplay distance.
            uv[i] = new Vector2((vertices[i].x - minX) / (maxX - minX) * 1.55f, (vertices[i].z - minZ) / (maxZ - minZ) * 2.0f);

        int[] triangles =
        {
            0, 1, 2,
            0, 2, 3,
            0, 3, 4
        };

        var mesh = new Mesh { name = "ScheduleIArcade_CheckerboardFootprint" };
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        _meshes.Add(mesh);

        var filter = overlay.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        var renderer = overlay.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = CreateCheckerboardFloorMaterial();
        // Visual only: the walkable floor is the single slab from CreateWalkableFloor.
    }

    /// <summary>
    /// ONE flat floor collider whose top is exactly the visible floor plane: a convex five-sided
    /// slab (the room's wall outline), so there are no seams or steps, and its chamfer edge stops
    /// at the door line so the walk-in vestibule ramp meets it without a lip.
    /// </summary>
    private void CreateWalkableFloor(Transform parent)
    {
        Vector2[] outline =
        {
            new(FX(-2.67f), FZ(-3.02f)),
            new(FX(-2.67f), FZ(1.56f)),
            new(FX(-0.84f), FZ(3.06f)),
            new(FX(2.64f), FZ(3.06f)),
            new(FX(2.64f), FZ(-3.02f)),
        };
        const float thickness = 0.60f;
        var vertices = new Vector3[outline.Length * 2];
        for (int i = 0; i < outline.Length; i++)
        {
            vertices[i] = new Vector3(outline[i].x, FloorTopY, outline[i].y);
            vertices[i + outline.Length] = new Vector3(outline[i].x, FloorTopY - thickness, outline[i].y);
        }
        var triangles = new List<int>();
        int n = outline.Length;
        for (int i = 1; i + 1 < n; i++)
        {
            triangles.AddRange(new[] { 0, i, i + 1 });
            triangles.AddRange(new[] { n, n + i + 1, n + i });
        }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            triangles.AddRange(new[] { i, n + i, j });
            triangles.AddRange(new[] { j, n + i, n + j });
        }
        var mesh = new Mesh { name = "ScheduleIArcade_WalkableFloor" };
        mesh.vertices = vertices;
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        _meshes.Add(mesh);

        var floor = new GameObject("RawArcade_WalkableFloor");
        floor.transform.SetParent(parent, false);
        var collider = floor.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;
        collider.convex = true;
    }

    private Material CreateCheckerboardFloorMaterial()
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Texture");
        if (shader == null)
            throw new InvalidOperationException("Could not find a shader for checkerboard floor material.");

        var material = new Material(shader) { name = "ScheduleIArcade_M_CheckerboardFloor" };
        _materials.Add(material);

        const int size = 128;
        const int cells = 8;
        int cellSize = size / cells;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "ScheduleIArcade_T_CheckerboardFloor",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 8
        };

        Color midnight = new Color(0.018f, 0.024f, 0.038f, 1f);
        Color slate = new Color(0.12f, 0.145f, 0.18f, 1f);
        Color grout = new Color(0.008f, 0.010f, 0.016f, 1f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool first = (((x / cellSize) + (y / cellSize)) & 1) == 0;
                int tileX = x % cellSize;
                int tileY = y % cellSize;
                Color tile = first ? midnight : slate;
                if (tileX <= 1 || tileY <= 1)
                    tile = grout;
                else if (tileX == 2 || tileY == 2)
                    tile = Color.Lerp(tile, Color.white, 0.055f);
                else if (tileX >= cellSize - 2 || tileY >= cellSize - 2)
                    tile = Color.Lerp(tile, Color.black, 0.10f);
                texture.SetPixel(x, y, tile);
            }
        }
        texture.Apply(true, false);
        _textures[texture.name] = texture;

        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.04f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.30f);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.30f);

        Vector2 tiling = Vector2.one;
        if (material.HasProperty("_BaseMap")) material.SetTextureScale("_BaseMap", tiling);
        if (material.HasProperty("_MainTex")) material.SetTextureScale("_MainTex", tiling);
        return material;
    }

    private void CreateSealedCeiling(Transform parent)
    {
        var ceiling = new GameObject("RawArcade_SealedInteriorCeiling");
        ceiling.transform.SetParent(parent, false);

        const float y = CeilingY;
        Vector3[] vertices =
        {
            new(FX(-2.67f), y, FZ(-3.02f)),
            new(FX(-2.67f), y, FZ( 1.56f)),
            new(FX(-0.84f), y, FZ( 3.06f)),
            new(FX( 2.64f), y, FZ( 3.06f)),
            new(FX( 2.64f), y, FZ(-3.02f))
        };

        float minX = FX(-2.67f);
        float maxX = FX(2.64f);
        float minZ = FZ(-3.02f);
        float maxZ = FZ(3.06f);
        Vector2[] uv = new Vector2[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            uv[i] = new Vector2(
                (vertices[i].x - minX) / (maxX - minX) * 2.7f,
                (vertices[i].z - minZ) / (maxZ - minZ) * 3.4f);
        }

        // Reverse the floor winding so the cap faces down into the room.
        int[] triangles =
        {
            0, 2, 1,
            0, 3, 2,
            0, 4, 3
        };

        var mesh = new Mesh { name = "ScheduleIArcade_SealedInteriorCeiling" };
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        _meshes.Add(mesh);

        var filter = ceiling.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        var renderer = ceiling.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = CreateAcousticCeilingMaterial();
        // Collision comes from the invisible ceiling box just above this plane.
    }

    private Material CreateAcousticCeilingMaterial()
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Texture");
        if (shader == null)
            throw new InvalidOperationException("Could not find a shader for the sealed arcade ceiling.");

        var material = new Material(shader) { name = "ScheduleIArcade_M_AcousticCeiling" };
        _materials.Add(material);

        // Procedural approximation of Schedule I's native Ceiling1 treatment: four-by-four
        // acoustic panels, grey channels, and restrained deterministic speckle.
        const int size = 128;
        const int cells = 4;
        int cellSize = size / cells;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "ScheduleIArcade_T_AcousticCeiling",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8
        };

        Color panel = new(0.72f, 0.72f, 0.69f, 1f);
        Color channel = new(0.16f, 0.17f, 0.18f, 1f);
        Color speckle = new(0.40f, 0.41f, 0.40f, 1f);
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                int localX = px % cellSize;
                int localY = py % cellSize;
                Color pixel = localX <= 1 || localY <= 1 ? channel : panel;
                int hash = unchecked(px * 73856093 ^ py * 19349663 ^ (px + py) * 83492791);
                if (localX > 3 && localY > 3 && (hash & 63) == 0)
                    pixel = speckle;
                texture.SetPixel(px, py, pixel);
            }
        }
        texture.Apply(true, false);
        _textures[texture.name] = texture;

        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.18f);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.18f);
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        return material;
    }

    private void CreateInteriorPolish(Transform parent)
    {
        CreateSealedCeiling(parent);

        Material baseboard = CreateSolidMaterial(
            "ScheduleIArcade_M_Baseboard",
            new Color(0.022f, 0.032f, 0.052f, 1f),
            0.24f,
            0.42f);
        Material crown = CreateSolidMaterial(
            "ScheduleIArcade_M_CrownTrim",
            new Color(0.075f, 0.09f, 0.13f, 1f),
            0.12f,
            0.34f);
        // The ripped vanilla Arcade uses red and white emissive materials. Match that established
        // identity inside rather than introducing an unrelated nightclub palette.
        Material whiteNeon = CreateSolidMaterial(
            "ScheduleIArcade_M_NeonWhite",
            new Color(0.62f, 0.62f, 0.60f, 1f),
            0.05f,
            0.48f,
            new Color(0.50f, 0.50f, 0.50f, 1f));
        Material redNeon = CreateSolidMaterial(
            "ScheduleIArcade_M_NeonRed",
            new Color(0.52f, 0.045f, 0.055f, 1f),
            0.05f,
            0.48f,
            new Color(2.00f, 0.255f, 0.255f, 1f));
        Vector3 southWest = new(FX(-2.67f), 0f, FZ(-3.02f));
        Vector3 southEast = new(FX(2.64f), 0f, FZ(-3.02f));
        Vector3 northEast = new(FX(2.64f), 0f, FZ(3.06f));
        Vector3 northWest = new(FX(-0.84f), 0f, FZ(3.06f));
        Vector3 chamferWest = new(FX(-2.67f), 0f, FZ(1.56f));

        Vector3 tangent = (northWest - chamferWest).normalized;
        Vector3 doorCenter = Vector3.Lerp(chamferWest, northWest, _doorT);
        Vector3 doorStart = doorCenter - tangent * (RequestedDoorWidth * 0.5f);
        Vector3 doorEnd = doorCenter + tangent * (RequestedDoorWidth * 0.5f);

        // Dark baseboards ground the walls and conceal the remaining imported/generated seams.
        CreateTrimSegment("Arcade_Baseboard_South", parent, southWest, southEast, 0.095f, 0.19f, 0.075f, baseboard);
        CreateTrimSegment("Arcade_Baseboard_East", parent, southEast, northEast, 0.095f, 0.19f, 0.075f, baseboard);
        CreateTrimSegment("Arcade_Baseboard_North", parent, northWest, northEast, 0.095f, 0.19f, 0.075f, baseboard);
        CreateTrimSegment("Arcade_Baseboard_West", parent, southWest, chamferWest, 0.095f, 0.19f, 0.075f, baseboard);
        CreateTrimSegment("Arcade_Baseboard_Chamfer_A", parent, chamferWest, doorStart, 0.095f, 0.19f, 0.075f, baseboard);
        CreateTrimSegment("Arcade_Baseboard_Chamfer_B", parent, doorEnd, northWest, 0.095f, 0.19f, 0.075f, baseboard);

        // Crown trim visually closes the ceiling line. Thin native-style red/white emissive strips
        // below it provide arcade identity without overpowering cabinet art or wall texture.
        CreatePerimeterBand("Arcade_Crown", parent, southWest, southEast, northEast, northWest, chamferWest, CeilingY - 0.055f, 0.12f, 0.085f, crown, crown);
        CreatePerimeterBand("Arcade_Neon", parent, southWest, southEast, northEast, northWest, chamferWest, CeilingY - 0.195f, 0.030f, 0.065f, redNeon, whiteNeon);

        Vector3[] corners = { southWest, southEast, northEast, northWest, chamferWest };
        for (int i = 0; i < corners.Length; i++)
        {
            CreateDecorativeCuboid(
                $"Arcade_CornerColumn_{i + 1}",
                parent,
                corners[i] + Vector3.up * ((CeilingY - 0.04f) * 0.5f),
                new Vector3(0.105f, CeilingY - 0.04f, 0.105f),
                Quaternion.identity,
                crown);
        }

        // Illumination comes from invisible point lights. No hanging or rectangular fixture mesh
        // is generated, leaving the acoustic-tile ceiling clean.
    }

    private void CreatePerimeterBand(
        string prefix,
        Transform parent,
        Vector3 southWest,
        Vector3 southEast,
        Vector3 northEast,
        Vector3 northWest,
        Vector3 chamferWest,
        float yCenter,
        float height,
        float depth,
        Material warmSide,
        Material coolSide)
    {
        CreateTrimSegment(prefix + "_South", parent, southWest, southEast, yCenter, height, depth, warmSide);
        CreateTrimSegment(prefix + "_East", parent, southEast, northEast, yCenter, height, depth, warmSide);
        CreateTrimSegment(prefix + "_North", parent, northWest, northEast, yCenter, height, depth, coolSide);
        CreateTrimSegment(prefix + "_West", parent, southWest, chamferWest, yCenter, height, depth, coolSide);
        CreateTrimSegment(prefix + "_Chamfer", parent, chamferWest, northWest, yCenter, height, depth, coolSide);
    }

    private static void CreateTrimSegment(
        string name,
        Transform parent,
        Vector3 localA,
        Vector3 localB,
        float yCenter,
        float height,
        float depth,
        Material material)
    {
        Vector3 delta = localB - localA;
        float length = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
        if (length <= 0.02f)
            return;

        Quaternion rotation = Quaternion.Euler(0f, Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg, 0f);
        CreateDecorativeCuboid(
            name,
            parent,
            (localA + localB) * 0.5f + Vector3.up * yCenter,
            new Vector3(depth, height, length + 0.035f),
            rotation,
            material);
    }

    private static void CreateDecorativeCuboid(
        string name,
        Transform parent,
        Vector3 localPosition,
        Vector3 localScale,
        Quaternion localRotation,
        Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = localRotation;
        go.transform.localScale = localScale;

        Renderer? renderer = go.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;

        Collider? collider = go.GetComponent<Collider>();
        if (collider != null)
            UnityEngine.Object.Destroy(collider);
    }

    private Material CreateSolidMaterial(
        string name,
        Color color,
        float metallic,
        float smoothness,
        Color? emission = null)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null)
            throw new InvalidOperationException($"Could not find a shader for decorative material '{name}'.");

        var material = new Material(shader) { name = name };
        _materials.Add(material);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
        if (emission.HasValue && material.HasProperty("_EmissionColor"))
        {
            material.SetColor("_EmissionColor", emission.Value);
            material.EnableKeyword("_EMISSION");
        }
        return material;
    }

    private Material? CreateWallFillMaterial()
    {
        if (!_materialByModel.TryGetValue("Enviorment", out Material? environmentMaterial) || environmentMaterial == null)
            return null;

        var material = new Material(environmentMaterial) { name = "ScheduleIArcade_M_MissingWallFill" };
        _materials.Add(material);

        // Use the large clean purple section from the ORIGINAL 1024x1024 environment atlas.
        // This avoids stretching the entire atlas (door/posters/etc.) across a generated wall.
        var scale = new Vector2(0.45f, 0.30f);
        var offset = new Vector2(0.00f, 0.00f);
        if (material.HasProperty("_BaseMap"))
        {
            material.SetTextureScale("_BaseMap", scale);
            material.SetTextureOffset("_BaseMap", offset);
        }
        if (material.HasProperty("_MainTex"))
        {
            material.SetTextureScale("_MainTex", scale);
            material.SetTextureOffset("_MainTex", offset);
        }
        if (material.HasProperty("_BumpMap"))
        {
            material.SetTextureScale("_BumpMap", scale);
            material.SetTextureOffset("_BumpMap", offset);
        }
        return material;
    }


    private void CreateActualDoorMarkers(Transform parent, out Transform spawnMarker, out Transform exitMarker)
    {
        Vector3 chamferA = new(FX(-2.67f), 0f, FZ(1.56f));
        Vector3 chamferB = new(FX(-0.84f), 0f, FZ(3.06f));
        Vector3 delta = chamferB - chamferA;
        Vector3 tangent = delta.normalized;
        Vector3 inward = new(tangent.z, 0f, -tangent.x);
        Vector3 doorCenter = Vector3.Lerp(chamferA, chamferB, _doorT);

        var spawn = new GameObject(Constants.SpawnMarkerName);
        spawn.transform.SetParent(parent, false);
        spawn.transform.localPosition = doorCenter + inward * 0.95f + Vector3.up * 0.08f;
        spawn.transform.localRotation = Quaternion.Euler(0f, Mathf.Atan2(inward.x, inward.z) * Mathf.Rad2Deg, 0f);
        spawnMarker = spawn.transform;

        var exit = new GameObject(Constants.ExitMarkerName);
        exit.transform.SetParent(parent, false);
        // Keep the invisible interaction point at natural eye level. At knee height it forced
        // players to look down just to use the exit, unlike the machine prompts in the room.
        exit.transform.localPosition = doorCenter + inward * 0.48f + Vector3.up * 1.45f;
        exitMarker = exit.transform;

        CreatePointLight(parent, doorCenter + inward * 0.80f + Vector3.up * 1.75f, 3.6f, 0.80f);
    }

    private void CreateDoubleDoorOnChamfer(Transform parent, Vector3 center, Vector3 tangent, float totalWidth, float height)
    {
        Material doorMaterial = CreateDoorMaterial(new Color(0.075f, 0.095f, 0.13f, 1f), "ScheduleIArcade_M_DoubleDoor");
        Material frameMaterial = CreateDoorMaterial(new Color(0.16f, 0.18f, 0.22f, 1f), "ScheduleIArcade_M_DoubleDoorFrame");
        Material glassMaterial = CreateDoorMaterial(new Color(0.08f, 0.19f, 0.27f, 1f), "ScheduleIArcade_M_DoubleDoorGlass");
        Material exitGlowMaterial = CreateSolidMaterial(
            "ScheduleIArcade_M_DoorExitGlow",
            new Color(0.42f, 0.015f, 0.025f, 1f),
            0.04f,
            0.40f,
            new Color(1.80f, 0.025f, 0.035f, 1f));

        var doorRoot = new GameObject("ARCADE_DOUBLE_DOOR_CHAMFER");
        doorRoot.transform.SetParent(parent, false);
        doorRoot.transform.localPosition = center;
        doorRoot.transform.localRotation = Quaternion.Euler(0f, Mathf.Atan2(tangent.x, tangent.z) * Mathf.Rad2Deg, 0f);

        // Slight panel overlap plus a face-mounted astragal eliminates the bright slit that was
        // visible between the generated leaves from inside the fake arcade.
        const float centerGap = -0.014f;
        float panelWidth = (totalWidth - centerGap) * 0.5f;
        float panelY = height * 0.5f;
        float southZ = -(panelWidth + centerGap) * 0.5f;
        float northZ = +(panelWidth + centerGap) * 0.5f;

        // Each leaf hangs on a real hinge pivot at its outer edge so ArcadeWalkInService can swing
        // it open; the player walks through the opening instead of being teleported.
        // For a chamfer wall local Z runs along the wall and local +X points into the arcade.
        Transform hingeA = CreateHinge(Constants.RoomDoorHingeA, doorRoot.transform, new Vector3(0f, 0f, -totalWidth * 0.5f));
        Transform hingeB = CreateHinge(Constants.RoomDoorHingeB, doorRoot.transform, new Vector3(0f, 0f, totalWidth * 0.5f));
        Vector3 offsetA = new(0f, 0f, totalWidth * 0.5f);
        Vector3 offsetB = new(0f, 0f, -totalWidth * 0.5f);

        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_A", hingeA, offsetA + new Vector3(0f, panelY, southZ), new Vector3(0.09f, height, panelWidth), doorMaterial, true);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_B", hingeB, offsetB + new Vector3(0f, panelY, northZ), new Vector3(0.09f, height, panelWidth), doorMaterial, true);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_CenterSeal", hingeA, offsetA + new Vector3(0.058f, panelY, 0f), new Vector3(0.024f, height - 0.03f, 0.038f), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_A_Window", hingeA, offsetA + new Vector3(0.052f, 1.47f, southZ), new Vector3(0.018f, 0.72f, panelWidth * 0.62f), glassMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_B_Window", hingeB, offsetB + new Vector3(0.052f, 1.47f, northZ), new Vector3(0.018f, 0.72f, panelWidth * 0.62f), glassMaterial, false);

        // Finished commercial-door details: kick plates, push bars, and a lit marker make the
        // entrance read as intentional architecture instead of two plain blocking cubes.
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_A_KickPlate", hingeA, offsetA + new Vector3(0.056f, 0.27f, southZ), new Vector3(0.022f, 0.36f, panelWidth * 0.72f), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_B_KickPlate", hingeB, offsetB + new Vector3(0.056f, 0.27f, northZ), new Vector3(0.022f, 0.36f, panelWidth * 0.72f), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_A_PushBar", hingeA, offsetA + new Vector3(0.080f, 0.95f, southZ), new Vector3(0.055f, 0.070f, panelWidth * 0.58f), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_B_PushBar", hingeB, offsetB + new Vector3(0.080f, 0.95f, northZ), new Vector3(0.055f, 0.070f, panelWidth * 0.58f), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_ExitGlow", doorRoot.transform, new Vector3(0.075f, height + 0.30f, 0f), new Vector3(0.032f, 0.13f, 0.44f), exitGlowMaterial, false);

        const float frame = 0.105f;
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_Frame_A", doorRoot.transform, new Vector3(0.005f, panelY, -totalWidth * 0.5f - frame * 0.5f), new Vector3(0.14f, height + 0.15f, frame), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_Frame_B", doorRoot.transform, new Vector3(0.005f, panelY, totalWidth * 0.5f + frame * 0.5f), new Vector3(0.14f, height + 0.15f, frame), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Chamfer_Frame_Top", doorRoot.transform, new Vector3(0.005f, height + 0.075f, 0f), new Vector3(0.14f, 0.15f, totalWidth + frame * 2f), frameMaterial, false);
    }

    private void CreateDoubleDoorOnWestWall(Transform parent, float wallX, float centerZ, float totalWidth, float height)
    {
        Material doorMaterial = CreateDoorMaterial(new Color(0.075f, 0.095f, 0.13f, 1f), "ScheduleIArcade_M_DoubleDoor");
        Material frameMaterial = CreateDoorMaterial(new Color(0.16f, 0.18f, 0.22f, 1f), "ScheduleIArcade_M_DoubleDoorFrame");
        Material glassMaterial = CreateDoorMaterial(new Color(0.08f, 0.19f, 0.27f, 1f), "ScheduleIArcade_M_DoubleDoorGlass");

        const float centerGap = 0.035f;
        float panelWidth = (totalWidth - centerGap) * 0.5f;
        float panelY = height * 0.5f;
        float southZ = centerZ - (panelWidth + centerGap) * 0.5f;
        float northZ = centerZ + (panelWidth + centerGap) * 0.5f;

        CreateDoorPiece("Arcade_DoubleDoor_West_South", parent, new Vector3(wallX, panelY, southZ), new Vector3(0.09f, height, panelWidth), doorMaterial, true);
        CreateDoorPiece("Arcade_DoubleDoor_West_North", parent, new Vector3(wallX, panelY, northZ), new Vector3(0.09f, height, panelWidth), doorMaterial, true);

        CreateDoorPiece("Arcade_DoubleDoor_West_South_Window", parent, new Vector3(wallX + 0.052f, 1.47f, southZ), new Vector3(0.018f, 0.72f, panelWidth * 0.62f), glassMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_West_North_Window", parent, new Vector3(wallX + 0.052f, 1.47f, northZ), new Vector3(0.018f, 0.72f, panelWidth * 0.62f), glassMaterial, false);

        const float frame = 0.105f;
        CreateDoorPiece("Arcade_DoubleDoor_West_Frame_South", parent, new Vector3(wallX + 0.005f, panelY, centerZ - totalWidth * 0.5f - frame * 0.5f), new Vector3(0.14f, height + 0.15f, frame), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_West_Frame_North", parent, new Vector3(wallX + 0.005f, panelY, centerZ + totalWidth * 0.5f + frame * 0.5f), new Vector3(0.14f, height + 0.15f, frame), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_West_Frame_Top", parent, new Vector3(wallX + 0.005f, height + 0.075f, centerZ), new Vector3(0.14f, 0.15f, totalWidth + frame * 2f), frameMaterial, false);
    }

    private void CreateDoubleDoor(Transform parent, float centerX, float wallZ, float totalWidth, float height)
    {
        Material doorMaterial = CreateDoorMaterial(new Color(0.075f, 0.095f, 0.13f, 1f), "ScheduleIArcade_M_DoubleDoor");
        Material frameMaterial = CreateDoorMaterial(new Color(0.16f, 0.18f, 0.22f, 1f), "ScheduleIArcade_M_DoubleDoorFrame");
        Material glassMaterial = CreateDoorMaterial(new Color(0.08f, 0.19f, 0.27f, 1f), "ScheduleIArcade_M_DoubleDoorGlass");

        const float centerGap = 0.035f;
        float panelWidth = (totalWidth - centerGap) * 0.5f;
        float panelY = height * 0.5f;
        float leftX = centerX - (panelWidth + centerGap) * 0.5f;
        float rightX = centerX + (panelWidth + centerGap) * 0.5f;

        CreateDoorPiece("Arcade_DoubleDoor_Left", parent, new Vector3(leftX, panelY, wallZ), new Vector3(panelWidth, height, 0.09f), doorMaterial, true);
        CreateDoorPiece("Arcade_DoubleDoor_Right", parent, new Vector3(rightX, panelY, wallZ), new Vector3(panelWidth, height, 0.09f), doorMaterial, true);

        // Narrow blue-tinted windows make the two individual leaves unmistakable from inside.
        CreateDoorPiece("Arcade_DoubleDoor_Left_Window", parent, new Vector3(leftX, 1.47f, wallZ - 0.052f), new Vector3(panelWidth * 0.62f, 0.72f, 0.018f), glassMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Right_Window", parent, new Vector3(rightX, 1.47f, wallZ - 0.052f), new Vector3(panelWidth * 0.62f, 0.72f, 0.018f), glassMaterial, false);

        const float frame = 0.105f;
        CreateDoorPiece("Arcade_DoubleDoor_Frame_Left", parent, new Vector3(centerX - totalWidth * 0.5f - frame * 0.5f, panelY, wallZ - 0.005f), new Vector3(frame, height + 0.15f, 0.14f), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Frame_Right", parent, new Vector3(centerX + totalWidth * 0.5f + frame * 0.5f, panelY, wallZ - 0.005f), new Vector3(frame, height + 0.15f, 0.14f), frameMaterial, false);
        CreateDoorPiece("Arcade_DoubleDoor_Frame_Top", parent, new Vector3(centerX, height + 0.075f, wallZ - 0.005f), new Vector3(totalWidth + frame * 2f, 0.15f, 0.14f), frameMaterial, false);
    }

    private static Transform CreateHinge(string name, Transform parent, Vector3 localPosition)
    {
        var hinge = new GameObject(name);
        hinge.transform.SetParent(parent, false);
        hinge.transform.localPosition = localPosition;
        return hinge.transform;
    }

    /// <summary>
    /// True when a point given in the room root's local space is inside the five-sided interior
    /// (the walk-in vestibule outside the chamfer door is excluded).
    /// </summary>
    public static bool IsInsideInteriorLocal(Vector3 local)
    {
        if (local.y < -0.6f || local.y > CeilingY + 0.2f)
            return false;
        if (local.x < FX(-2.67f) || local.x > FX(2.64f) || local.z < FZ(-3.02f) || local.z > FZ(3.06f))
            return false;

        Vector3 chamferA = new(FX(-2.67f), 0f, FZ(1.56f));
        Vector3 chamferB = new(FX(-0.84f), 0f, FZ(3.06f));
        Vector3 tangent = chamferB - chamferA;
        Vector3 inward = new(tangent.z, 0f, -tangent.x);
        return Vector3.Dot(local - chamferA, inward) >= 0f;
    }

    private static void CreateDoorPiece(string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material material, bool keepCollider)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = localScale;

        Renderer? renderer = go.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;

        if (!keepCollider)
        {
            Collider? collider = go.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);
        }
    }

    private Material CreateDoorMaterial(Color color, string name)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null)
            throw new InvalidOperationException("Could not find a shader for the arcade double door.");

        var material = new Material(shader) { name = name };
        _materials.Add(material);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.15f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.32f);
        return material;
    }

    /// <summary>
    /// Builds a dedicated floor-standing TV console against the otherwise-clear north wall.
    /// Its back edge ends at the inner wall face (z ~= 4.01), leaving the aisle and every imported
    /// machine unobstructed. The cabinet/screen marker names connect it to CabinetRegistryService.
    /// </summary>
    private void CreateInteractiveArcadeTv(Transform parent)
    {
        var tv = new GameObject(Constants.CabinetPrefix + "TV");
        tv.transform.SetParent(parent, false);
        tv.transform.localPosition = new Vector3(1.32f, 0.044f, 3.70f);

        Material body = CreateDoorMaterial(new Color(0.025f, 0.035f, 0.065f, 1f), "ScheduleIArcade_M_TvBody");
        Material bezel = CreateDoorMaterial(new Color(0.075f, 0.09f, 0.14f, 1f), "ScheduleIArcade_M_TvBezel");
        Material accent = CreateDoorMaterial(new Color(0.05f, 0.55f, 0.85f, 1f), "ScheduleIArcade_M_TvAccent");
        Material control = CreateDoorMaterial(new Color(0.75f, 0.08f, 0.22f, 1f), "ScheduleIArcade_M_TvControl");

        // A compact console/stand supports the screen at natural eye height. Colliders are kept
        // on the base, body, control shelf and bezel so both the television and screen raycast.
        CreateTvPiece("ArcadeTv_Base", tv.transform, new Vector3(0f, 0.10f, 0.01f), new Vector3(1.46f, 0.20f, 0.62f), body, true);
        CreateTvPiece("ArcadeTv_Console", tv.transform, new Vector3(0f, 0.47f, 0.04f), new Vector3(1.34f, 0.64f, 0.50f), body, true);
        CreateTvPiece("ArcadeTv_ControlShelf", tv.transform, new Vector3(0f, 0.80f, -0.19f), new Vector3(1.28f, 0.11f, 0.38f), bezel, true);
        CreateTvPiece("ArcadeTv_ScreenBezel", tv.transform, new Vector3(0f, 1.32f, 0.01f), new Vector3(1.58f, 0.96f, 0.22f), bezel, true);
        CreateTvPiece("ArcadeTv_TopAccent", tv.transform, new Vector3(0f, 1.84f, 0.01f), new Vector3(1.44f, 0.055f, 0.24f), accent, false);
        CreateTvPiece("ArcadeTv_ControlLeft", tv.transform, new Vector3(-0.24f, 0.87f, -0.39f), new Vector3(0.16f, 0.06f, 0.16f), control, false);
        CreateTvPiece("ArcadeTv_ControlRight", tv.transform, new Vector3(0.24f, 0.87f, -0.39f), new Vector3(0.16f, 0.06f, 0.16f), control, false);

        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = Constants.ScreenPrefix + "TV";
        screen.transform.SetParent(tv.transform, false);
        screen.transform.localPosition = new Vector3(0f, 1.32f, -0.1055f);
        // The TV front is local -Z. This material is rendered double-sided; keeping the quad's
        // U axis unrotated means the player sees normal text instead of a mirrored framebuffer.
        screen.transform.localRotation = Quaternion.identity;
        screen.transform.localScale = new Vector3(1.35f, 0.73f, 1f);

        Renderer? screenRenderer = screen.GetComponent<Renderer>();
        if (screenRenderer != null)
            screenRenderer.sharedMaterial = CreateTvStandbyMaterial();

        MelonLogger.Msg("Pre-placed interactive Arcade TV flush against north wall: Pong + Snake, E to interact.");
    }

    private static void CreateTvPiece(
        string name,
        Transform parent,
        Vector3 localPosition,
        Vector3 localScale,
        Material material,
        bool keepCollider)
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

    private Material CreateTvStandbyMaterial()
    {
        return CreateAttractScreenMaterial(
            "ScheduleIArcade_M_TvStandby",
            "__arcade_tv_standby",
            "ARCADE TV",
            "$10 PLAY",
            new Color32(22, 230, 255, 255),
            new Color32(255, 55, 155, 255));
    }

    private void CreateCabinetScreens(Transform root)
    {
        for (int index = 1; index <= 2; index++)
        {
            string suffix = index.ToString("00");
            Transform? cabinet = FindDeepChild(root, Constants.CabinetPrefix + suffix);
            if (cabinet == null)
                continue;

            Renderer? cabinetRenderer = cabinet.GetComponent<Renderer>();
            if (cabinetRenderer != null)
                cabinetRenderer.sharedMaterial = CreateDoomCabinetMaterial(index);

            GameObject screen = CreateScreenQuad(Constants.ScreenPrefix + suffix, cabinet);
            // The imported CRT face is a shallow upward tilt, not a vertical plane. Match that
            // physical glass angle and sit just above it so the full playable image remains
            // flush and cannot disappear behind the source mesh's fake monitor geometry.
            bool isCabinetTwo = index == 2;
            Vector3 faceNormal = isCabinetTwo
                ? new Vector3(0.937f, 0.348f, 0f)
                : new Vector3(-0.937f, 0.348f, 0f);
            Vector3 faceCenter = new Vector3(isCabinetTwo ? 0.075f : -0.075f, 0.277f, 0f);
            screen.transform.localPosition = faceCenter + faceNormal * 0.009f;
            screen.transform.localRotation = Quaternion.LookRotation(faceNormal, Vector3.up);
            // The mesh's front-facing U direction is reversed on both authored split cabinets.
            screen.transform.localScale = new Vector3(-0.35f, 0.245f, 1f);

            var renderer = screen.GetComponent<Renderer>();
            if (renderer != null)
            {
                DoomCabinetEdition edition = index == 1 ? DoomCabinetEdition.Doom1 : DoomCabinetEdition.Doom2;
                bool doomReady = EmbeddedDoomBridge.IsReady(edition, out _);
                renderer.sharedMaterial = CreateAttractScreenMaterial(
                    "ScheduleIArcade_M_CabinetScreen" + suffix,
                    "__arcade_cabinet_standby_" + suffix,
                    doomReady ? (index == 1 ? "FREEDOOM 1" : "FREEDOOM 2") : "OUT OF ORDER",
                    doomReady ? "$10 PLAY" : "NO CHARGE",
                    doomReady
                        ? (index == 1 ? new Color32(255, 79, 28, 255) : new Color32(255, 58, 58, 255))
                        : new Color32(60, 190, 255, 255),
                    doomReady
                        ? (index == 1 ? new Color32(255, 200, 86, 255) : new Color32(255, 185, 185, 255))
                        : new Color32(155, 230, 255, 255));
            }

            // Keep the collider: aiming at either monitor resolves to that cabinet parent.
        }
    }

    private Material CreateDoomCabinetMaterial(int index)
    {
        bool sequel = index == 2;
        string textureKey = "__arcade_doom_cabinet_" + index;
        Material material = CreateUnlitTextureMaterial(string.Empty, "ScheduleIArcade_M_DoomCabinet" + index);
        Texture2D texture = DoomCabinetTextureFactory.Create(
            "ScheduleIArcade_DoomCabinet" + index,
            sequel);
        _textures[textureKey] = texture;
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        return material;
    }

    private void CreatePacmanTableScreen(Transform root)
    {
        Transform? table = FindDeepChild(root, Constants.CabinetPrefix + "PACMAN");
        if (table == null)
            return;

        MeshFilter? filter = table.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null)
            return;

        // The printed monitor inset of the table-top texture, measured from the mesh UVs:
        // x -0.381..0.143 (0.524 m along the table) and z -0.228..0.198 (0.426 m across), top y 0.299.
        // The 224 x 288 portrait picture is fitted inside it at native aspect: 0.524 m tall along the
        // table axis, 0.408 m wide, centred, so the maze sits inside the bezel with a slim border.
        var screen = CreateScreenQuad(Constants.ScreenPrefix + "PACMAN", table);
        screen.transform.localPosition = PacmanScreenLayout.LocalCentre;
        screen.transform.localRotation = PacmanScreenLayout.LocalRotation(seatAtPositiveX: true);
        screen.transform.localScale = new Vector3(PacmanScreenLayout.Width, PacmanScreenLayout.Height, 1f);

        Renderer? renderer = screen.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = CreateAttractScreenMaterial(
                "ScheduleIArcade_M_PacmanCocktailScreen",
                "__arcade_pacman_standby",
                "PAC-MAN",
                "$10 PLAY",
                new Color32(255, 224, 42, 255),
                new Color32(52, 110, 255, 255));

        MelonLogger.Msg("Pre-placed Pac-Man cocktail table is interactive: aim at its top and press E to play.");
    }

    private void CreatePinballTableScreens(Transform root)
    {
        for (int index = 1; index <= 4; index++)
        {
            Transform? machine = FindDeepChild(root, "Pinball Machine " + index.ToString("00"));
            MeshFilter? filter = machine?.GetComponent<MeshFilter>();
            if (machine == null || filter == null || filter.sharedMesh == null)
                continue;

            string suffix = "PINBALL" + index.ToString("00");
            GameObject screen = CreateScreenQuad(Constants.ScreenPrefix + suffix, machine);

            // The imported machine pair 3/4 is mirrored across its local X axis. Its sloped
            // playfield glass therefore has the opposite normal and centre from machines 1/2.
            // Fit to each measured plane instead of hovering one shared screen above the art.
            bool mirroredGlass = index >= 3;
            Vector3 glassNormal = mirroredGlass
                ? new Vector3(0.148f, 0.989f, 0f)
                : new Vector3(-0.148f, 0.989f, 0f);
            // Fit inside the measured inner rails/apron (see PinballPlayfieldMapping): the whole
            // simulated table including flippers and drain is visible above the front apron, and
            // the side-rail model no longer overlaps the ball's lanes.
            var (centreX, centreZ) = Games.Pinball.PinballPlayfieldMapping.QuadCentre();
            if (mirroredGlass)
            {
                centreX = -centreX;
                centreZ = -centreZ;
            }
            // Playfield plane through (-0.10, 0.012) with the measured 8.5 degree slope.
            float planeY = 0.012f + (mirroredGlass ? -1f : 1f) * 0.1497f * (centreX - (mirroredGlass ? 0.10f : -0.10f));
            Vector3 glassCenter = new Vector3(centreX, planeY, centreZ);
            screen.transform.localPosition = glassCenter + glassNormal * 0.009f;
            Quaternion glassRotation = Quaternion.LookRotation(glassNormal, Vector3.right);
            // Preserve the measured glass normal, then rotate only the 3/4 texture frame around
            // that normal. Their source UV basis is upside down even though their glass plane is
            // mirrored correctly, so this makes the playable view upright without lifting it.
            if (mirroredGlass)
                glassRotation *= Quaternion.AngleAxis(180f, Vector3.forward);
            screen.transform.localRotation = glassRotation;
            var (glassWidth, glassLength) = Games.Pinball.PinballPlayfieldMapping.QuadSize();

            // The shared U axis keeps text and score layout identical across all four tables.
            screen.transform.localScale = new Vector3(-glassWidth, glassLength, 1f);

            Renderer? renderer = screen.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = CreateAttractScreenMaterial(
                    "ScheduleIArcade_M_PinballScreen" + index,
                    "__arcade_pinball_standby_" + index,
                    "SPACE CADET",
                    "$10 PLAY",
                    new Color32(82, 207, 255, 255),
                    new Color32(255, 218, 96, 255));
        }

        MelonLogger.Msg("Four Space Cadet pinball displays were fitted inside the inner rails, above the apron, at the table's native aspect.");
    }

    private void CreateAirHockeyScreen(Transform root)
    {
        Transform? table = FindDeepChild(root, Constants.CabinetPrefix + "AIRHOCKEY");
        if (table == null)
            return;
        // Inner rails measured from the mesh: x +-0.437, z +-0.865, surface y -0.04. The play
        // texture is landscape (length along the table), shown to a top-down camera.
        GameObject screen = CreateScreenQuad(Constants.ScreenPrefix + "AIRHOCKEY", table);
        screen.transform.localPosition = new Vector3(0f, -0.022f, 0f);
        screen.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.right);
        screen.transform.localScale = new Vector3(1.72f, 0.86f, 1f);
        Renderer? renderer = screen.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = CreateAttractScreenMaterial(
                "ScheduleIArcade_M_AirHockeyScreen",
                "__arcade_airhockey_standby",
                "AIR HOCKEY",
                "2 PLAYERS $10",
                new Color32(235, 60, 80, 255),
                new Color32(70, 140, 255, 255));
        MelonLogger.Msg("Air hockey table is interactive: two players, first to 7 (local keyboard: P1 WASD, P2 arrows).");
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

    private Material CreateUnlitTextureMaterial(string textureName, string materialName)
    {
        Shader? shader = Shader.Find("Unlit/Texture");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            throw new InvalidOperationException("Could not find a shader for arcade helper material.");

        var material = new Material(shader) { name = materialName };
        _materials.Add(material);
        Texture2D? texture = LoadTexture(textureName);
        if (texture != null)
        {
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        }
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        return material;
    }

    private Material CreateAttractScreenMaterial(
        string materialName,
        string textureKey,
        string title,
        string subtitle,
        Color32 accent,
        Color32 accent2)
    {
        Material material = CreateUnlitTextureMaterial(string.Empty, materialName);
        Texture2D texture = ArcadeAttractScreenFactory.Create(
            "ScheduleIArcade_" + textureKey.TrimStart('_'),
            128,
            72,
            title,
            subtitle,
            accent,
            accent2);
        _textures[textureKey] = texture;
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_EmissionMap"))
        {
            material.SetTexture("_EmissionMap", texture);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_EMISSION");
        }
        return material;
    }

    private static void CreateInteriorLights(Transform parent)
    {
        // Bright local arcade lighting: short-range point fixtures and colour washes brighten
        // machines and walls without adding the long roof bars the room no longer uses.
        Color warm = new(1.00f, 0.88f, 0.72f, 1f);
        Color softWhite = new(0.83f, 0.90f, 1.00f, 1f);
        Color cyan = new(0.08f, 0.82f, 1.00f, 1f);
        Color magenta = new(1.00f, 0.12f, 0.48f, 1f);
        float lampY = CeilingY - 0.35f;
        CreatePointLight(parent, new Vector3(FX(-1.80f), lampY, FZ(-1.85f)), 6.0f, 2.10f, warm);
        CreatePointLight(parent, new Vector3(FX(1.45f), lampY, FZ(-1.65f)), 6.0f, 2.10f, warm);
        CreatePointLight(parent, new Vector3(FX(-1.55f), lampY, FZ(1.85f)), 6.0f, 2.10f, softWhite);
        CreatePointLight(parent, new Vector3(FX(1.50f), lampY, FZ(1.85f)), 6.0f, 2.10f, softWhite);
        CreatePointLight(parent, new Vector3(FX(-2.34f), 1.45f, FZ(-0.20f)), 3.2f, 1.10f, cyan);
        CreatePointLight(parent, new Vector3(FX(2.20f), 1.45f, FZ(0.90f)), 3.2f, 1.10f, magenta);
    }

    private static void CreatePointLight(Transform parent, Vector3 localPosition, float range, float intensity)
    {
        CreatePointLight(parent, localPosition, range, intensity, Color.white);
    }

    private static void CreatePointLight(Transform parent, Vector3 localPosition, float range, float intensity, Color color)
    {
        var go = new GameObject("ArcadeInteriorLight");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = range;
        light.intensity = intensity;
        light.color = color;
        light.shadows = LightShadows.None;
    }

    private static Transform? FindDeepChild(Transform root, string name)
    {
        if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            return root;

        // IL2CPP Transform enumeration yields Il2CppSystem.Object in this game build.
        // Using childCount/GetChild avoids an InvalidCastException from foreach (Transform child in root).
        int childCount = root.childCount;
        for (int i = 0; i < childCount; i++)
        {
            Transform child = root.GetChild(i);
            Transform? result = FindDeepChild(child, name);
            if (result != null)
                return result;
        }

        return null;
    }

    public void Dispose()
    {
        foreach (Material material in _materials)
        {
            if (material != null)
                UnityEngine.Object.Destroy(material);
        }
        _materials.Clear();
        _materialByModel.Clear();

        foreach (Texture2D texture in _textures.Values)
        {
            if (texture != null)
                UnityEngine.Object.Destroy(texture);
        }
        _textures.Clear();

        foreach (Mesh mesh in _meshes)
        {
            if (mesh != null)
                UnityEngine.Object.Destroy(mesh);
        }
        _meshes.Clear();
    }
}

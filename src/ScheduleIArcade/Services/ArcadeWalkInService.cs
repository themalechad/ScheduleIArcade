using MelonLoader;
using S1API.Entities;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Makes the arcade a physically continuous walk-in shop inside the real Northtown Arcade
/// building. There is no teleport anywhere: the player walks through the building's own glass
/// corner doors, a short vestibule and the room's double door.
///
/// Measured from Schedule I 0.4.7f12 level1 (Map/Hyland Point/Region_Northtown/Arcade (1)):
///   * building interior x -52.40..-43.65, z 135.65..147.40, floor (Foundation) top y = -3.90;
///   * entrance = arcade/CornerDoor, a 45-degree chamfer panel whose single BoxCollider
///     (0.2 x 4.24 x 3.5 m) seals the whole chamfer, holding two "Metal Glass Door (Static)"
///     StaticDoor leaves (each with a 2 m box collider scaled to ~1.05 x 2.1 m);
///   * the room's chamfer double door sits ~1.66 m directly behind it (0.05 m tangential offset).
/// The service replaces the sealing collider with stubs + lintel around the measured doorway,
/// swings the vanilla glass-door models open on hinges, builds an enclosed vestibule, and
/// derives "inside the arcade" from the player's position. Everything is restored on Dispose.
/// </summary>
internal sealed class ArcadeWalkInService : IDisposable
{
    private const string BuildingPath = "Map/Hyland Point/Region_Northtown/Arcade (1)";
    private const float DoorInteractDistance = 2.4f;
    private const float AutoCloseDistance = 4.5f;
    private const float AutoCloseDelay = 6f;
    private const float SwingSeconds = 0.55f;
    private const float VestibuleHeight = 2.55f;

    private readonly ArcadeLocationService _location;
    private readonly ArcadePreferences _preferences;
    private readonly Func<bool> _blockingInteraction;
    private readonly List<DoorPair> _doors = new();
    private readonly List<Action> _restore = new();
    private readonly List<UnityEngine.Object> _created = new();
    private readonly List<Renderer> _interiorOccluders = new();
    private Bounds _buildingFootprint;
    private Vector3 _entrancePoint;
    private bool _occludersHidden;
    private DoorPair? _target;
    private bool _initialized;

    public ArcadeWalkInService(ArcadeLocationService location, ArcadePreferences preferences, Func<bool> blockingInteraction)
    {
        _location = location;
        _preferences = preferences;
        _blockingInteraction = blockingInteraction;
    }

    public bool IsInsideArcade { get; private set; }
    public bool IsWalkInReady => _initialized;

    public void Initialize()
    {
        if (_location.RoomInstance == null)
            return;

        if (!_location.UsingRawScene)
        {
            MelonLogger.Error("Walk-in arcade needs the packaged raw arcade room (Mods/ScheduleIArcade/Assets/RawScene). The fallback room is not connected to the Arcade building and is unreachable without the removed teleport.");
            return;
        }

        try
        {
            Transform? building = FindBuilding();
            Transform? cornerDoor = building?.Find("arcade/CornerDoor");
            if (building == null || cornerDoor == null)
            {
                MelonLogger.Error($"Walk-in arcade: '{BuildingPath}/arcade/CornerDoor' was not found in this game build; the arcade entrance cannot be opened.");
                return;
            }

            var staticDoors = cornerDoor.GetComponentsInChildren<Il2CppScheduleOne.Doors.StaticDoor>(true);
            if (staticDoors.Length == 0)
            {
                MelonLogger.Error("Walk-in arcade: the Arcade corner door has no StaticDoor leaves in this game build.");
                return;
            }

            Transform? roomDoor = FindDeep(_location.RoomInstance.transform, Constants.RoomDoorRootName);
            Transform? hingeA = roomDoor != null ? FindDeep(roomDoor, Constants.RoomDoorHingeA) : null;
            Transform? hingeB = roomDoor != null ? FindDeep(roomDoor, Constants.RoomDoorHingeB) : null;
            if (roomDoor == null || hingeA == null || hingeB == null)
            {
                MelonLogger.Error("Walk-in arcade: the arcade room's hinged double door was not generated.");
                return;
            }

            // The vanilla glass leaves are STATICALLY BATCHED in level1 (their MeshFilters point at
            // "Combined Mesh (root: scene) 24/25"), so moving their transforms would move only the
            // colliders while the visible doors stayed shut. Measure each leaf from its BoxCollider,
            // hide the batched leaf, and swing a replacement built from that measurement with the
            // vanilla door's own materials.
            var measured = new List<(Il2CppScheduleOne.Doors.StaticDoor door, Transform model, BoxCollider box, Bounds bounds)>();
            Bounds doorway = default;
            bool haveDoorway = false;
            foreach (var door in staticDoors)
            {
                Transform model = door.transform.Find("Model") ?? door.transform;
                BoxCollider? box = null;
                foreach (BoxCollider candidate in model.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (candidate != null && !candidate.isTrigger && candidate.enabled)
                    {
                        box = candidate;
                        break;
                    }
                }
                if (box == null)
                    continue;

                Bounds leafBounds = box.bounds;
                if (!haveDoorway) { doorway = leafBounds; haveDoorway = true; }
                else doorway.Encapsulate(leafBounds);
                measured.Add((door, model, box, leafBounds));
            }

            if (!haveDoorway || measured.Count == 0)
            {
                MelonLogger.Error("Walk-in arcade: could not measure the Arcade glass door leaves.");
                return;
            }

            Vector3 roomDoorCenter = roomDoor.position;
            Vector3 buildingDoorCenter = new(doorway.center.x, doorway.min.y, doorway.center.z);
            Vector3 along = AlongAxis(cornerDoor);
            Vector3 inward = Flat(roomDoorCenter - buildingDoorCenter);
            inward = Flat(inward - along * Vector3.Dot(inward, along)).normalized;
            float depth = Vector3.Dot(roomDoorCenter - buildingDoorCenter, inward);
            if (depth < 0.6f || depth > 4f)
            {
                MelonLogger.Error($"Walk-in arcade: room door is {depth:0.00} m behind the building door (expected ~1.7 m); refusing to build a broken entrance.");
                return;
            }

            // Only now, after every measurement passed, touch the vanilla building.
            var buildingLeaves = new List<Leaf>();
            Vector3 doorwayCenterForHandles = doorway.center;
            foreach (var (door, model, box, leafBounds) in measured)
            {
                DisableVanillaDoorInteraction(door);
                HideVanillaLeaf(model);
                BoxCollider replacementCollider = BuildGlassLeaf(box, model, doorwayCenterForHandles, out Transform replacement);
                buildingLeaves.Add(new Leaf(replacement, leafBounds, new Collider[] { replacementCollider }));
            }

            float doorwayHalfWidth = HalfExtentAlong(doorway, along, buildingDoorCenter);
            float doorwayHeight = doorway.max.y - doorway.min.y;
            float roomDoorHalfWidth = Vector3.Distance(Flat(hingeA.position), Flat(hingeB.position)) * 0.5f;

            OpenCornerDoorCollider(cornerDoor, doorway);

            foreach (Leaf leaf in buildingLeaves)
                leaf.ConfigureSwing(buildingDoorCenter, along, -inward, 88f); // vanilla glass doors swing out to the street
            var entrance = new DoorPair("ARCADE ENTRANCE", buildingDoorCenter + Vector3.up * 1.2f, buildingLeaves);

            var roomLeaves = new List<Leaf>
            {
                Leaf.ForHinge(hingeA, roomDoorCenter, -inward, 90f),
                Leaf.ForHinge(hingeB, roomDoorCenter, -inward, 90f)
            };
            var room = new DoorPair("ARCADE DOOR", roomDoorCenter + Vector3.up * 1.2f, roomLeaves);
            _doors.Add(entrance);
            _doors.Add(room);

            float floorRise = _location.RoomInstance.transform.position.y + 0.043f - buildingDoorCenter.y;
            BuildVestibule(cornerDoor, buildingDoorCenter, along, inward, depth, Mathf.Max(doorwayHalfWidth, roomDoorHalfWidth) + 0.12f, doorwayHalfWidth, doorwayHeight, floorRise);

            CollectInteriorOccluders(building, buildingDoorCenter);

            _initialized = true;
            MelonLogger.Msg($"Walk-in arcade ready: Arcade glass doors at {buildingDoorCenter} (opening {doorwayHalfWidth * 2f:0.00} m x {doorwayHeight:0.00} m), vestibule {depth:0.00} m deep, room door {roomDoorHalfWidth * 2f:0.00} m wide. No teleport is used.");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"Walk-in arcade setup failed; restoring the vanilla building: {ex}");
            Dispose();
        }
    }

    public void Tick()
    {
        Player? player = Player.Local;
        if (player == null)
        {
            IsInsideArcade = false;
            return;
        }

        Vector3 position = player.Position;
        IsInsideArcade = _location.IsPointInsideRoom(position);
        if (!_initialized)
            return;

        float dt = Time.deltaTime;
        _target = null;
        Camera? camera = Camera.main;
        if (camera != null && !_blockingInteraction())
        {
            float bestDot = 0.86f;
            foreach (DoorPair door in _doors)
            {
                Vector3 toDoor = door.InteractPoint - camera.transform.position;
                if (Flat(door.InteractPoint - position).magnitude > DoorInteractDistance || toDoor.sqrMagnitude < 0.01f)
                    continue;
                float dot = Vector3.Dot(camera.transform.forward, toDoor.normalized);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    _target = door;
                }
            }
        }

        if (_target != null && Input.GetKeyDown(KeyCode.E))
        {
            // Never swing a closing leaf through the player standing in the doorway.
            bool inSwing = Flat(_target.InteractPoint - position).magnitude < 1.15f;
            if (!_target.WantOpen || !inSwing)
                _target.Toggle();
        }

        UpdateInteriorOccluders(position);

        foreach (DoorPair door in _doors)
        {
            float distance = Flat(door.InteractPoint - position).magnitude;
            if (door.WantOpen && distance > AutoCloseDistance && !IsInsideArcade)
            {
                door.AwayTimer += dt;
                if (door.AwayTimer > AutoCloseDelay)
                    door.WantOpen = false;
            }
            else
            {
                door.AwayTimer = 0f;
            }

            door.Animate(dt / SwingSeconds);
        }
    }

    public void OnGUI()
    {
        if (_target == null || _blockingInteraction())
            return;

        ArcadePromptUi.DrawCard(
            _target.Label,
            _target.WantOpen ? "PRESS [E]  CLOSE DOOR" : "PRESS [E]  OPEN DOOR",
            _target.WantOpen ? "Doors close by themselves once you walk away." : "Walk in: the arcade is part of the Arcade building.",
            132f,
            new Color(0.95f, 0.35f, 0.38f, 1f));
    }

    public void Dispose()
    {
        foreach (DoorPair door in _doors)
            door.ResetToClosed();
        _doors.Clear();
        _target = null;
        _interiorOccluders.Clear();
        _occludersHidden = false;

        for (int i = _restore.Count - 1; i >= 0; i--)
        {
            try { _restore[i](); }
            catch { }
        }
        _restore.Clear();

        foreach (UnityEngine.Object created in _created)
        {
            if (created != null)
                UnityEngine.Object.Destroy(created);
        }
        _created.Clear();
        _initialized = false;
        IsInsideArcade = false;
    }

    /// <summary>
    /// The building contains two renderer-only boxes ("arcade/Cube" and "arcade/Block", scaled
    /// 6 x 4.5 x 3.71 like the 12 x 9 m Foundation) that fake the interior when seen through the
    /// windows. They have no colliders, but they would hide the vestibule when the doors are open,
    /// so they are hidden only while the player is at the entrance or inside the building.
    /// </summary>
    private void CollectInteriorOccluders(Transform building, Vector3 entrance)
    {
        _entrancePoint = entrance;
        Transform? foundation = building.Find("arcade/Foundation");
        BoxCollider? foundationBox = foundation != null ? foundation.GetComponent<BoxCollider>() : null;
        _buildingFootprint = foundationBox != null ? foundationBox.bounds : new Bounds(building.position, new Vector3(9f, 1f, 12f));
        _buildingFootprint.Expand(new Vector3(0.6f, 0f, 0.6f));

        foreach (string name in new[] { "arcade/Cube", "arcade/Block" })
        {
            Transform? occluder = building.Find(name);
            Renderer? renderer = occluder != null ? occluder.GetComponent<Renderer>() : null;
            if (renderer == null)
                continue;
            _interiorOccluders.Add(renderer);
            bool original = renderer.enabled;
            Renderer captured = renderer;
            _restore.Add(() => { if (captured != null) captured.enabled = original; });
        }
    }

    private void UpdateInteriorOccluders(Vector3 playerPosition)
    {
        if (_interiorOccluders.Count == 0)
            return;
        Vector3 flat = new(playerPosition.x, _buildingFootprint.center.y, playerPosition.z);
        bool near = _buildingFootprint.Contains(flat) || Flat(playerPosition - _entrancePoint).magnitude < 5f;
        if (near == _occludersHidden)
            return;
        _occludersHidden = near;
        foreach (Renderer renderer in _interiorOccluders)
        {
            if (renderer != null)
                renderer.enabled = !near;
        }
    }

    private static Transform? FindBuilding()
    {
        GameObject? direct = GameObject.Find(BuildingPath);
        if (direct != null)
            return direct.transform;

        // Fallback for a renamed parent: the StaticDoor whose NPCEnterableBuilding is "Arcade" and
        // sits under an "arcade/CornerDoor" hierarchy.
        foreach (var door in UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Doors.StaticDoor>(true))
        {
            try
            {
                if (door == null || door.Building == null || !string.Equals(door.Building.BuildingName, "Arcade", StringComparison.OrdinalIgnoreCase))
                    continue;
                Transform? corner = door.transform.parent;
                if (corner != null && corner.name == "CornerDoor" && corner.parent != null && corner.parent.parent != null)
                    return corner.parent.parent;
            }
            catch
            {
            }
        }
        return null;
    }

    private void DisableVanillaDoorInteraction(Il2CppScheduleOne.Doors.StaticDoor door)
    {
        // The vanilla StaticDoor is a knock-only door (NPC summon menu). The walk-in door replaces
        // that interaction, so hide its prompt and restore it exactly on dispose.
        bool usable = door.Usable;
        bool canKnock = door.CanKnock;
        door.Usable = false;
        door.CanKnock = false;
        _restore.Add(() => { if (door != null) { door.Usable = usable; door.CanKnock = canKnock; } });

        var intObj = door.IntObj;
        if (intObj == null)
            return;
        intObj.SetInteractableState(Il2CppScheduleOne.Interaction.InteractableObject.EInteractableState.Disabled);
        _restore.Add(() => { if (intObj != null) intObj.SetInteractableState(Il2CppScheduleOne.Interaction.InteractableObject.EInteractableState.Default); });
        foreach (Collider collider in intObj.GetComponentsInChildren<Collider>(true))
        {
            if (collider == null || !collider.enabled)
                continue;
            collider.enabled = false;
            Collider captured = collider;
            _restore.Add(() => { if (captured != null) captured.enabled = true; });
        }
    }

    private void HideVanillaLeaf(Transform model)
    {
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !renderer.enabled)
                continue;
            renderer.enabled = false;
            Renderer captured = renderer;
            _restore.Add(() => { if (captured != null) captured.enabled = true; });
        }
        foreach (Collider collider in SolidColliders(model))
        {
            if (!collider.enabled)
                continue;
            collider.enabled = false;
            Collider captured = collider;
            _restore.Add(() => { if (captured != null) captured.enabled = true; });
        }
    }

    /// <summary>
    /// Builds a swinging glass-door leaf that occupies exactly the vanilla leaf's collider box:
    /// metal stiles/rails with the vanilla door's frame material, a glass pane with its glass
    /// material and a handle on the free edge. Returns the leaf's own collider.
    /// </summary>
    private BoxCollider BuildGlassLeaf(BoxCollider source, Transform model, Vector3 doorwayCenter, out Transform root)
    {
        Transform t = source.transform;
        Vector3 scale = t.lossyScale;
        Vector3 extents = new(Mathf.Abs(source.size.x * scale.x), Mathf.Abs(source.size.y * scale.y), Mathf.Abs(source.size.z * scale.z));

        var go = new GameObject("ARCADE_WALKIN_GLASS_LEAF");
        _created.Add(go);
        go.layer = model.gameObject.layer;
        go.transform.position = t.TransformPoint(source.center);
        go.transform.rotation = t.rotation;
        root = go.transform;

        // Identify the leaf's thickness / height / width axes in its own local frame.
        int thicknessAxis = 0, upAxis = 0;
        float thinnest = float.MaxValue, mostVertical = -1f;
        for (int axis = 0; axis < 3; axis++)
        {
            if (extents[axis] < thinnest) { thinnest = extents[axis]; thicknessAxis = axis; }
            Vector3 local = Vector3.zero;
            local[axis] = 1f;
            float vertical = Mathf.Abs(Vector3.Dot(go.transform.TransformDirection(local), Vector3.up));
            if (vertical > mostVertical) { mostVertical = vertical; upAxis = axis; }
        }
        if (upAxis == thicknessAxis)
            thicknessAxis = (upAxis + 1) % 3;
        int widthAxis = 3 - thicknessAxis - upAxis;

        Material frame = MaterialFrom(model, "Door") ?? GameMaterialLibrary.Find("Metal", "Trim") ?? FallbackMaterial(new Color(0.18f, 0.19f, 0.21f, 1f));
        Material glass = MaterialFrom(model, "Glass") ?? frame;
        Material handle = MaterialFrom(model, "DoorHandle") ?? frame;

        float width = extents[widthAxis], height = extents[upAxis], depth = Mathf.Max(0.03f, extents[thicknessAxis]);
        Vector3 Vec(float w, float h, float d)
        {
            Vector3 v = Vector3.zero;
            v[widthAxis] = w;
            v[upAxis] = h;
            v[thicknessAxis] = d;
            return v;
        }
        const float stile = 0.07f, topRail = 0.09f, bottomRail = 0.20f;
        LeafPiece(root, "Stile_A", Vec(-width * 0.5f + stile * 0.5f, 0f, 0f), Vec(stile, height, depth), frame);
        LeafPiece(root, "Stile_B", Vec(width * 0.5f - stile * 0.5f, 0f, 0f), Vec(stile, height, depth), frame);
        LeafPiece(root, "Rail_Top", Vec(0f, height * 0.5f - topRail * 0.5f, 0f), Vec(width - stile * 2f, topRail, depth), frame);
        LeafPiece(root, "Rail_Bottom", Vec(0f, -height * 0.5f + bottomRail * 0.5f, 0f), Vec(width - stile * 2f, bottomRail, depth), frame);
        LeafPiece(root, "Glass", Vec(0f, (bottomRail - topRail) * 0.5f, 0f), Vec(width - stile * 2f, height - topRail - bottomRail, depth * 0.35f), glass);

        // Handle on the free edge (the edge nearer the middle of the double door).
        Vector3 edgeA = root.TransformPoint(Vec(-width * 0.5f, 0f, 0f));
        Vector3 edgeB = root.TransformPoint(Vec(width * 0.5f, 0f, 0f));
        float freeSign = Flat(edgeA - doorwayCenter).sqrMagnitude < Flat(edgeB - doorwayCenter).sqrMagnitude ? -1f : 1f;
        float handleY = -height * 0.5f + 1.05f;
        LeafPiece(root, "Handle_Out", Vec(freeSign * (width * 0.5f - stile - 0.05f), handleY, depth * 0.5f + 0.03f), Vec(0.03f, 0.32f, 0.03f), handle);
        LeafPiece(root, "Handle_In", Vec(freeSign * (width * 0.5f - stile - 0.05f), handleY, -depth * 0.5f - 0.03f), Vec(0.03f, 0.32f, 0.03f), handle);

        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.size = extents;
        return collider;
    }

    private void LeafPiece(Transform parent, string name, Vector3 localCenter, Vector3 size, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter;
        go.layer = parent.gameObject.layer;
        Mesh mesh = MetricBoxMesh.Create(size, "ScheduleIArcade_GlassLeaf_" + name);
        mesh.vertices = ScaleVertices(mesh.vertices, size);
        mesh.RecalculateBounds();
        _created.Add(mesh);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private static Material? MaterialFrom(Transform model, string rendererNamePrefix)
    {
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer != null && renderer.name.StartsWith(rendererNamePrefix, StringComparison.OrdinalIgnoreCase) && renderer.sharedMaterial != null)
                return renderer.sharedMaterial;
        }
        return null;
    }

    /// <summary>Replaces the chamfer panel's single sealing BoxCollider with two side stubs and a
    /// lintel that leave exactly the measured glass-door opening clear.</summary>
    private void OpenCornerDoorCollider(Transform cornerDoor, Bounds doorway)
    {
        BoxCollider? seal = cornerDoor.GetComponent<BoxCollider>();
        if (seal == null || !seal.enabled)
            return;

        int alongAxis = LocalAxisIndex(cornerDoor, AlongAxis(cornerDoor));
        int upAxis = LocalAxisIndex(cornerDoor, Vector3.up);

        // Doorway extent in the panel's local space, from the leaf collider bounds.
        float openMin = float.MaxValue, openMax = float.MinValue, openTop = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new(
                (i & 1) == 0 ? doorway.min.x : doorway.max.x,
                (i & 2) == 0 ? doorway.min.y : doorway.max.y,
                (i & 4) == 0 ? doorway.min.z : doorway.max.z);
            Vector3 local = cornerDoor.InverseTransformPoint(corner);
            openMin = Mathf.Min(openMin, local[alongAxis]);
            openMax = Mathf.Max(openMax, local[alongAxis]);
            openTop = Mathf.Max(openTop, local[upAxis]);
        }

        Vector3 center = seal.center;
        Vector3 size = seal.size;
        float panelMin = center[alongAxis] - size[alongAxis] * 0.5f;
        float panelMax = center[alongAxis] + size[alongAxis] * 0.5f;
        float panelBottom = center[upAxis] - size[upAxis] * 0.5f;
        float panelTop = center[upAxis] + size[upAxis] * 0.5f;

        var holder = new GameObject("ARCADE_WALKIN_CORNERDOOR_COLLIDERS");
        holder.transform.SetParent(cornerDoor, false);
        holder.layer = cornerDoor.gameObject.layer;
        _created.Add(holder);

        void AddBox(float alongMin, float alongMax, float upMin, float upMax)
        {
            if (alongMax - alongMin < 0.01f || upMax - upMin < 0.01f)
                return;
            BoxCollider box = holder.AddComponent<BoxCollider>();
            Vector3 c = center;
            Vector3 s = size;
            c[alongAxis] = (alongMin + alongMax) * 0.5f;
            s[alongAxis] = alongMax - alongMin;
            c[upAxis] = (upMin + upMax) * 0.5f;
            s[upAxis] = upMax - upMin;
            box.center = c;
            box.size = s;
        }

        AddBox(panelMin, openMin, panelBottom, panelTop);
        AddBox(openMax, panelMax, panelBottom, panelTop);
        AddBox(openMin, openMax, Mathf.Min(openTop, panelTop), panelTop);

        seal.enabled = false;
        _restore.Add(() => { if (seal != null) seal.enabled = true; });
    }

    private void BuildVestibule(Transform cornerDoor, Vector3 origin, Vector3 along, Vector3 inward, float depth, float halfWidth, float doorwayHalfWidth, float doorwayHeight, float floorRise)
    {
        var root = new GameObject("ARCADE_WALKIN_VESTIBULE");
        _created.Add(root);
        root.transform.position = origin;
        root.transform.rotation = Quaternion.LookRotation(inward, Vector3.up);
        // Local axes: +Z = into the arcade, +X = along the building chamfer.

        Material wall = GameMaterialLibrary.Find("Plaster1", "BackRoomWall", "Wall", "Walls1")
            ?? cornerDoor.GetComponent<Renderer>()?.sharedMaterial
            ?? FallbackMaterial(new Color(0.55f, 0.55f, 0.58f, 1f));
        Material ceiling = GameMaterialLibrary.Find("Ceiling") ?? wall;
        Material floor = GameMaterialLibrary.Find("Tiles", "Floor", "Concrete") ?? wall;
        Material trim = GameMaterialLibrary.Find("Skirting", "Trim") ?? wall;

        const float wallThickness = 0.12f;
        float start = 0.04f;
        float length = depth - start;
        // Side walls (solid), ceiling (solid) and a gentle ramp from the street floor up to the
        // arcade room floor (solid) so the CharacterController never meets a ledge.
        Piece(root.transform, "Vestibule_Wall_Left", new Vector3(-halfWidth - wallThickness * 0.5f, VestibuleHeight * 0.5f, start + length * 0.5f), new Vector3(wallThickness, VestibuleHeight, length), wall, true);
        Piece(root.transform, "Vestibule_Wall_Right", new Vector3(halfWidth + wallThickness * 0.5f, VestibuleHeight * 0.5f, start + length * 0.5f), new Vector3(wallThickness, VestibuleHeight, length), wall, true);
        Piece(root.transform, "Vestibule_Ceiling", new Vector3(0f, VestibuleHeight + 0.05f, start + length * 0.5f), new Vector3(halfWidth * 2f + wallThickness * 2f, 0.10f, length), ceiling, true);

        float rampLength = Mathf.Sqrt(depth * depth + floorRise * floorRise);
        float rampAngle = Mathf.Atan2(floorRise, depth) * Mathf.Rad2Deg;
        GameObject ramp = Piece(root.transform, "Vestibule_Floor", new Vector3(0f, floorRise * 0.5f - 0.05f, depth * 0.5f), new Vector3(halfWidth * 2f, 0.10f, rampLength + 0.10f), floor, true);
        ramp.transform.localRotation = Quaternion.Euler(-rampAngle, 0f, 0f);

        // Interior lining of the building's chamfer wall around the opening (visual only: the
        // replacement stubs/lintel are the collision) so the vestibule reads as a finished room.
        float liningZ = 0.16f;
        float sideWidth = halfWidth - doorwayHalfWidth;
        if (sideWidth > 0.02f)
        {
            Piece(root.transform, "Vestibule_Lining_Left", new Vector3(-(doorwayHalfWidth + sideWidth * 0.5f), VestibuleHeight * 0.5f, liningZ), new Vector3(sideWidth, VestibuleHeight, 0.03f), wall, false);
            Piece(root.transform, "Vestibule_Lining_Right", new Vector3(doorwayHalfWidth + sideWidth * 0.5f, VestibuleHeight * 0.5f, liningZ), new Vector3(sideWidth, VestibuleHeight, 0.03f), wall, false);
        }
        if (VestibuleHeight - doorwayHeight > 0.02f)
            Piece(root.transform, "Vestibule_Lining_Lintel", new Vector3(0f, (doorwayHeight + VestibuleHeight) * 0.5f, liningZ), new Vector3(doorwayHalfWidth * 2f, VestibuleHeight - doorwayHeight, 0.03f), wall, false);
        Piece(root.transform, "Vestibule_Skirting_Left", new Vector3(-halfWidth + 0.015f, 0.06f + floorRise * 0.5f, start + length * 0.5f), new Vector3(0.03f, 0.12f, length), trim, false);
        Piece(root.transform, "Vestibule_Skirting_Right", new Vector3(halfWidth - 0.015f, 0.06f + floorRise * 0.5f, start + length * 0.5f), new Vector3(0.03f, 0.12f, length), trim, false);

        var lightGo = new GameObject("Vestibule_Light");
        lightGo.transform.SetParent(root.transform, false);
        lightGo.transform.localPosition = new Vector3(0f, VestibuleHeight - 0.25f, depth * 0.5f);
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 3.4f;
        light.intensity = 1.4f;
        light.color = new Color(1f, 0.9f, 0.78f, 1f);
        light.shadows = LightShadows.None;
    }

    private GameObject Piece(Transform parent, string name, Vector3 localPosition, Vector3 size, Material material, bool solid)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        Mesh mesh = MetricBoxMesh.Create(size, "ScheduleIArcade_" + name);
        mesh.vertices = ScaleVertices(mesh.vertices, size);
        mesh.RecalculateBounds();
        _created.Add(mesh);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        if (solid)
        {
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = size;
        }
        return go;
    }

    private static Vector3[] ScaleVertices(Vector3[] vertices, Vector3 size)
    {
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = Vector3.Scale(vertices[i], size);
        return vertices;
    }

    private Material FallbackMaterial(Color color)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { name = "ScheduleIArcade_M_VestibuleFallback" };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        _created.Add(material);
        return material;
    }

    private static Vector3 AlongAxis(Transform panel)
    {
        // The panel axis that is horizontal and longest in world space runs along the chamfer.
        BoxCollider? box = panel.GetComponent<BoxCollider>();
        Vector3 size = box != null ? box.size : Vector3.one;
        Vector3 best = Vector3.right;
        float bestLength = -1f;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 local = Vector3.zero;
            local[axis] = 1f;
            Vector3 world = panel.TransformDirection(local);
            if (Mathf.Abs(world.y) > 0.5f)
                continue;
            float length = size[axis] * panel.lossyScale[axis];
            if (length > bestLength)
            {
                bestLength = length;
                best = Flat(world).normalized;
            }
        }
        return best;
    }

    private static int LocalAxisIndex(Transform panel, Vector3 worldDirection)
    {
        int best = 0;
        float bestDot = -1f;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 local = Vector3.zero;
            local[axis] = 1f;
            float dot = Mathf.Abs(Vector3.Dot(panel.TransformDirection(local).normalized, worldDirection.normalized));
            if (dot > bestDot)
            {
                bestDot = dot;
                best = axis;
            }
        }
        return best;
    }

    private static float HalfExtentAlong(Bounds bounds, Vector3 along, Vector3 center)
    {
        float max = 0f;
        for (int i = 0; i < 4; i++)
        {
            Vector3 corner = new((i & 1) == 0 ? bounds.min.x : bounds.max.x, center.y, (i & 2) == 0 ? bounds.min.z : bounds.max.z);
            max = Mathf.Max(max, Mathf.Abs(Vector3.Dot(corner - center, along)));
        }
        // For a 45-degree leaf the AABB corner overshoots the leaf end by its thickness only.
        return max;
    }

    private static Collider[] SolidColliders(Transform root)
    {
        var result = new List<Collider>();
        foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
        {
            if (collider != null && !collider.isTrigger)
                result.Add(collider);
        }
        return result.ToArray();
    }

    private static bool TryGetBounds(Collider[] colliders, out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        foreach (Collider collider in colliders)
        {
            if (!any) { bounds = collider.bounds; any = true; }
            else bounds.Encapsulate(collider.bounds);
        }
        return any;
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    private static Transform? FindDeep(Transform root, string name)
    {
        if (root.name == name)
            return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform? found = FindDeep(root.GetChild(i), name);
            if (found != null)
                return found;
        }
        return null;
    }

    private sealed class DoorPair
    {
        private readonly List<Leaf> _leaves;

        public DoorPair(string label, Vector3 interactPoint, List<Leaf> leaves)
        {
            Label = label;
            InteractPoint = interactPoint;
            _leaves = leaves;
        }

        public string Label { get; }
        public Vector3 InteractPoint { get; }
        public bool WantOpen { get; set; }
        public float AwayTimer { get; set; }
        private float _open;

        public void Toggle() => WantOpen = !WantOpen;

        public void Animate(float step)
        {
            float target = WantOpen ? 1f : 0f;
            if (Mathf.Approximately(_open, target))
                return;
            _open = Mathf.MoveTowards(_open, target, step);
            // Leaves are non-solid only while swinging so a moving leaf never shoves through the
            // player; fully open or closed they collide normally.
            bool moving = _open > 0.001f && _open < 0.999f;
            float eased = _open * _open * (3f - 2f * _open);
            foreach (Leaf leaf in _leaves)
                leaf.Apply(eased, !moving);
        }

        public void ResetToClosed()
        {
            _open = 0f;
            WantOpen = false;
            foreach (Leaf leaf in _leaves)
                leaf.Apply(0f, true);
        }
    }

    private sealed class Leaf
    {
        private readonly Transform _target;
        private readonly Vector3 _localPosition;
        private readonly Quaternion _localRotation;
        private readonly Collider[] _colliders;
        private readonly Bounds _closedBounds;
        private Vector3 _hinge;
        private float _angle;

        public Leaf(Transform target, Bounds closedBounds, Collider[] colliders)
        {
            _target = target;
            _localPosition = target.localPosition;
            _localRotation = target.localRotation;
            _closedBounds = closedBounds;
            _colliders = colliders;
            _hinge = target.position;
        }

        public static Leaf ForHinge(Transform hinge, Vector3 doorCenter, Vector3 swingSide, float degrees)
        {
            Collider[] colliders = SolidColliders(hinge);
            TryGetBounds(colliders, out Bounds bounds);
            var leaf = new Leaf(hinge, bounds, colliders);
            leaf._hinge = hinge.position;
            leaf._angle = ChooseSign(hinge.position, bounds.center, swingSide, degrees);
            return leaf;
        }

        /// <summary>For a vanilla door leaf: hinge at the leaf end farthest from the doorway
        /// centre, swinging towards <paramref name="swingSide"/>.</summary>
        public void ConfigureSwing(Vector3 doorwayCenter, Vector3 along, Vector3 swingSide, float degrees)
        {
            Vector3 c = _closedBounds.center;
            float sign = Mathf.Sign(Vector3.Dot(Flat(c - doorwayCenter), along));
            float halfSpan = HalfExtentAlong(_closedBounds, along, new Vector3(c.x, doorwayCenter.y, c.z));
            _hinge = new Vector3(c.x, doorwayCenter.y, c.z) + along * (sign * halfSpan);
            _angle = ChooseSign(_hinge, c, swingSide, degrees);
        }

        private static float ChooseSign(Vector3 hinge, Vector3 leafCenter, Vector3 swingSide, float degrees)
        {
            Vector3 arm = Flat(leafCenter - hinge);
            Vector3 positive = Quaternion.AngleAxis(degrees, Vector3.up) * arm;
            return Vector3.Dot(positive, swingSide) >= 0f ? degrees : -degrees;
        }

        public void Apply(float fraction, bool solid)
        {
            if (_target == null)
                return;
            _target.localPosition = _localPosition;
            _target.localRotation = _localRotation;
            if (fraction > 0f)
                _target.RotateAround(_hinge, Vector3.up, _angle * fraction);
            foreach (Collider collider in _colliders)
            {
                if (collider != null)
                    collider.enabled = solid;
            }
        }
    }
}

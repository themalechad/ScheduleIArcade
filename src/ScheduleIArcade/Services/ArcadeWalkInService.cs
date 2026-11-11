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
    private Vector3 _playerPosition;

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
            Material? glass = FindTransparentGlass();
            Transform? sourceLeaf = FindVanillaGlassDoorLeaf(out string sourceName);
            var buildingLeaves = new List<Leaf>();
            int cloned = 0;
            foreach (var (door, model, box, leafBounds) in measured)
            {
                DisableVanillaDoorInteraction(door);
                HideVanillaLeaf(model);
                // The vanilla leaf's own "Door" transform is the exact closed pose and its origin is
                // the hinge edge (the leaf collider is offset half a leaf width from it).
                Transform pose = box.transform.parent != null && box.transform.name == "Collider" ? box.transform.parent : box.transform;
                Leaf? leaf = sourceLeaf != null ? CloneGlassLeaf(sourceLeaf, pose, model.gameObject.layer) : null;
                if (leaf != null)
                    cloned++;
                else
                    leaf = BuildFallbackLeaf(box, pose, model, doorway.center, glass);
                buildingLeaves.Add(leaf);
            }

            float doorwayHalfWidth = HalfExtentAlong(doorway, along, buildingDoorCenter);
            float doorwayHeight = doorway.max.y - doorway.min.y;
            float roomDoorHalfWidth = Vector3.Distance(Flat(hingeA.position), Flat(hingeB.position)) * 0.5f;

            OpenCornerDoorCollider(cornerDoor, doorway);
            ReplaceOpaqueGlass(cornerDoor, glass);
            BuildEntranceFrame(buildingDoorCenter, along, inward, buildingLeaves, doorwayHeight);

            var entrance = new DoorPair("ARCADE ENTRANCE", buildingDoorCenter + Vector3.up * 1.2f, buildingLeaves, inward);
            var roomLeaves = new List<Leaf>
            {
                Leaf.ForHinge(hingeA),
                Leaf.ForHinge(hingeB)
            };
            var room = new DoorPair("ARCADE DOOR", roomDoorCenter + Vector3.up * 1.2f, roomLeaves, inward);
            _doors.Add(entrance);
            _doors.Add(room);
            foreach (DoorPair pair in _doors)
                pair.Native = CreateNativeInteraction(pair, along, Mathf.Max(doorwayHalfWidth, roomDoorHalfWidth) * 2f, staticDoors[0]);

            float floorRise = _location.RoomInstance.transform.position.y + 0.043f - buildingDoorCenter.y;
            float panelTop = cornerDoor.GetComponent<BoxCollider>() is BoxCollider panelBox ? panelBox.bounds.max.y : buildingDoorCenter.y + 3.5f;
            float vestibuleHeight = Mathf.Clamp(panelTop - buildingDoorCenter.y - 0.10f, 2.55f, ArcadeDimensions.InteriorCeilingHeight);
            BuildVestibule(cornerDoor, buildingDoorCenter, along, inward, depth, Mathf.Max(doorwayHalfWidth, roomDoorHalfWidth) + 0.12f, doorwayHalfWidth, doorwayHeight, floorRise, vestibuleHeight, roomDoorHalfWidth, roomDoor);

            CollectInteriorOccluders(building, buildingDoorCenter);
            MelonLogger.Msg(cloned > 0
                ? $"Walk-in entrance: {cloned} leaf/leaves cloned from the vanilla '{sourceName}' (PivotDoor glass door model, transparent glass); vestibule {vestibuleHeight:0.00} m high."
                : $"Walk-in entrance: no unbatched vanilla glass PivotDoor leaf was loaded; built matching glass leaves with {(glass != null ? glass.name : "fallback")} glass.");

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
        _playerPosition = position;
        Camera? camera = Camera.main;
        if (camera != null && !_blockingInteraction())
        {
            float bestDot = 0.86f;
            foreach (DoorPair door in _doors)
            {
                // The native InteractableObject owns the prompt and the E press while it is hovered.
                if (door.NativeHoveredRecently)
                    continue;
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
            RequestToggle(_target);

        UpdateInteriorOccluders(position);

        foreach (DoorPair door in _doors)
        {
            float distance = Flat(door.InteractPoint - position).magnitude;
            if (door.WantOpen && distance > AutoCloseDistance && !IsInsideArcade)
            {
                door.AwayTimer += dt;
                if (door.AwayTimer > AutoCloseDelay)
                {
                    door.WantOpen = false;
                    door.UpdateNativeMessage();
                }
            }
            else
            {
                door.AwayTimer = 0f;
            }

            door.Animate(dt / SwingSeconds);
        }
    }

    /// <summary>One interaction toggles both leaves; opening swings them away from the player.</summary>
    private void RequestToggle(DoorPair door)
    {
        // Native and fallback input can both see the same E press: one toggle per press.
        if (Time.frameCount - door.LastToggleFrame <= 1 || _blockingInteraction())
            return;
        // Never swing a closing leaf through the player standing in the doorway.
        bool inSwing = Flat(door.InteractPoint - _playerPosition).magnitude < 1.15f;
        if (door.WantOpen && inSwing)
            return;
        door.LastToggleFrame = Time.frameCount;
        if (!door.WantOpen)
            door.ChooseSwingAwayFrom(_playerPosition);
        door.WantOpen = !door.WantOpen;
        door.UpdateNativeMessage();
    }

    public void OnGUI()
    {
        if (_target == null || _blockingInteraction())
            return;

        // Fallback prompt only (the native "E  Open" HUD is used whenever it is available).
        ArcadePromptUi.DrawCard(
            _target.Label,
            _target.WantOpen ? "PRESS [E]  CLOSE" : "PRESS [E]  OPEN",
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

    /// <summary>The game's transparent shop-window glass (render queue >= 3000), never the opaque fake.</summary>
    private static Material? FindTransparentGlass()
    {
        Material? named = GameMaterialLibrary.Find("window_glass_material", "Glass", "glass");
        if (named != null && named.renderQueue >= 2450 && !named.name.Contains("opaque", StringComparison.OrdinalIgnoreCase))
            return named;
        try
        {
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || m.renderQueue < 3000)
                    continue;
                string n = m.name;
                if ((n.Contains("glass", StringComparison.OrdinalIgnoreCase) || n.Contains("window", StringComparison.OrdinalIgnoreCase))
                    && !n.Contains("opaque", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("ScheduleIArcade", StringComparison.Ordinal))
                    return m;
            }
        }
        catch
        {
        }
        return named;
    }

    /// <summary>
    /// The leaf ("Door" + handles + glass + collider) of a vanilla "Basic Metal Glass Door" PivotDoor:
    /// the same door model the Arcade's StaticDoor uses, but unbatched (it animates) and fitted with
    /// transparent glass and a black frame. The Northtown hardware store's door is preferred.
    /// </summary>
    private static Transform? FindVanillaGlassDoorLeaf(out string sourceName)
    {
        sourceName = string.Empty;
        Transform? best = null;
        int bestScore = -1;
        try
        {
            foreach (var pivot in UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Doors.PivotDoor>(true))
            {
                if (pivot == null || !pivot.gameObject.name.StartsWith("Basic Metal Glass Door", StringComparison.Ordinal))
                    continue;
                Transform container = pivot.DoorTransform != null ? pivot.DoorTransform : pivot.transform;
                Transform? leaf = container.Find("Door");
                MeshFilter? filter = leaf != null ? leaf.GetComponent<MeshFilter>() : null;
                MeshRenderer? renderer = leaf != null ? leaf.GetComponent<MeshRenderer>() : null;
                if (leaf == null || filter == null || filter.sharedMesh == null || renderer == null)
                    continue;
                if (renderer.isPartOfStaticBatch || filter.sharedMesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal))
                    continue;
                string path = pivot.transform.parent != null ? pivot.transform.parent.name : string.Empty;
                int score = 1;
                if (path.Contains("hardware", StringComparison.OrdinalIgnoreCase)) score += 4;
                Material? frame = renderer.sharedMaterial;
                if (frame != null && frame.name.Contains("black", StringComparison.OrdinalIgnoreCase)) score += 2;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = leaf;
                    sourceName = pivot.gameObject.name + (path.Length > 0 ? " (" + path + ")" : string.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Walk-in entrance: could not search vanilla PivotDoors: {ex.Message}");
        }
        return best;
    }

    /// <summary>Clones the vanilla leaf into the exact closed pose of an Arcade door leaf.</summary>
    private Leaf? CloneGlassLeaf(Transform source, Transform pose, int layer)
    {
        try
        {
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject);
            _created.Add(clone);
            clone.name = "ARCADE_WALKIN_GLASS_LEAF";
            clone.transform.SetParent(null, false);
            clone.transform.position = pose.position;
            clone.transform.rotation = pose.rotation;
            clone.transform.localScale = pose.lossyScale;
            foreach (Transform t in clone.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
            // Keep only plain colliders and renderers; drop any behaviour that came with the leaf.
            foreach (MonoBehaviour behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null)
                    UnityEngine.Object.Destroy(behaviour);
            }
            Collider[] colliders = SolidColliders(clone.transform);
            if (colliders.Length == 0)
            {
                // Same 1.05 x 2.10 x 0.05 leaf box the vanilla door uses (local 2x2x2 at y = +1).
                var box = clone.AddComponent<BoxCollider>();
                box.center = new Vector3(0f, 1f, 0f);
                box.size = new Vector3(2f, 2f, 2f);
                colliders = new Collider[] { box };
            }
            TryGetBounds(colliders, out Bounds bounds);
            return new Leaf(clone.transform, bounds, colliders, pose.position);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Walk-in entrance: cloning the vanilla glass door leaf failed ({ex.Message}); using the built fallback leaf.");
            return null;
        }
    }

    /// <summary>
    /// Fallback leaf in the vanilla leaf's exact pose, mimicking the liked shop door: black metal
    /// stiles/rails, 2 x 3 glass panes in transparent glass and a vertical bar handle.
    /// </summary>
    private Leaf BuildFallbackLeaf(BoxCollider source, Transform pose, Transform model, Vector3 doorwayCenter, Material? glassMaterial)
    {
        Transform t = source.transform;
        Vector3 scale = t.lossyScale;
        Vector3 extents = new(Mathf.Abs(source.size.x * scale.x), Mathf.Abs(source.size.y * scale.y), Mathf.Abs(source.size.z * scale.z));

        var go = new GameObject("ARCADE_WALKIN_GLASS_LEAF");
        _created.Add(go);
        go.layer = model.gameObject.layer;
        go.transform.position = t.TransformPoint(source.center);
        go.transform.rotation = t.rotation;
        Transform root = go.transform;

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

        Material frame = GameMaterialLibrary.Find("postoffice_blacktrim_mat", "gas station black mat") ?? MaterialFrom(model, "Door") ?? FallbackMaterial(new Color(0.03f, 0.03f, 0.035f, 1f));
        Material glass = glassMaterial ?? FallbackMaterial(new Color(0.55f, 0.65f, 0.7f, 1f));
        Material handle = GameMaterialLibrary.Find("metal_mediumgrey_mat") ?? MaterialFrom(model, "DoorHandle") ?? frame;

        float width = extents[widthAxis], height = extents[upAxis], depth = Mathf.Max(0.03f, extents[thicknessAxis]);
        Vector3 Vec(float w, float h, float d)
        {
            Vector3 v = Vector3.zero;
            v[widthAxis] = w;
            v[upAxis] = h;
            v[thicknessAxis] = d;
            return v;
        }
        const float stile = 0.07f, topRail = 0.09f, bottomRail = 0.20f, bar = 0.035f;
        LeafPiece(root, "Stile_A", Vec(-width * 0.5f + stile * 0.5f, 0f, 0f), Vec(stile, height, depth), frame);
        LeafPiece(root, "Stile_B", Vec(width * 0.5f - stile * 0.5f, 0f, 0f), Vec(stile, height, depth), frame);
        LeafPiece(root, "Rail_Top", Vec(0f, height * 0.5f - topRail * 0.5f, 0f), Vec(width - stile * 2f, topRail, depth), frame);
        LeafPiece(root, "Rail_Bottom", Vec(0f, -height * 0.5f + bottomRail * 0.5f, 0f), Vec(width - stile * 2f, bottomRail, depth), frame);
        float glassHeight = height - topRail - bottomRail;
        float glassCentre = (bottomRail - topRail) * 0.5f;
        LeafPiece(root, "Glass", Vec(0f, glassCentre, 0f), Vec(width - stile * 2f, glassHeight, depth * 0.30f), glass);
        // 2 x 3 panes: one vertical muntin and two horizontal muntins.
        LeafPiece(root, "Muntin_V", Vec(0f, glassCentre, 0f), Vec(bar, glassHeight, depth * 0.8f), frame);
        LeafPiece(root, "Muntin_H1", Vec(0f, glassCentre - glassHeight / 6f, 0f), Vec(width - stile * 2f, bar, depth * 0.8f), frame);
        LeafPiece(root, "Muntin_H2", Vec(0f, glassCentre + glassHeight / 6f, 0f), Vec(width - stile * 2f, bar, depth * 0.8f), frame);

        Vector3 edgeA = root.TransformPoint(Vec(-width * 0.5f, 0f, 0f));
        Vector3 edgeB = root.TransformPoint(Vec(width * 0.5f, 0f, 0f));
        float freeSign = Flat(edgeA - doorwayCenter).sqrMagnitude < Flat(edgeB - doorwayCenter).sqrMagnitude ? -1f : 1f;
        float handleY = -height * 0.5f + 1.05f;
        LeafPiece(root, "Handle_Out", Vec(freeSign * (width * 0.5f - stile - 0.06f), handleY, depth * 0.5f + 0.035f), Vec(0.025f, 0.60f, 0.025f), handle);
        LeafPiece(root, "Handle_In", Vec(freeSign * (width * 0.5f - stile - 0.06f), handleY, -depth * 0.5f - 0.035f), Vec(0.025f, 0.60f, 0.025f), handle);

        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.size = extents;
        return new Leaf(root, collider.bounds, new Collider[] { collider }, pose.position);
    }

    /// <summary>Swaps the corner panel's opaque fake-window slot (the transom) for real glass.</summary>
    private void ReplaceOpaqueGlass(Transform cornerDoor, Material? glass)
    {
        if (glass == null)
            return;
        Renderer? renderer = cornerDoor.GetComponent<Renderer>();
        if (renderer == null)
            return;
        Material[] original = renderer.sharedMaterials;
        Material[] replaced = renderer.sharedMaterials;
        int swapped = 0;
        for (int i = 0; i < replaced.Length; i++)
        {
            if (replaced[i] != null && replaced[i].name.Contains("opaque", StringComparison.OrdinalIgnoreCase))
            {
                replaced[i] = glass;
                swapped++;
            }
        }
        if (swapped == 0)
            return;
        renderer.sharedMaterials = replaced;
        _restore.Add(() => { if (renderer != null) renderer.sharedMaterials = original; });
        MelonLogger.Msg($"Walk-in entrance: transom/storefront glass above the doors now uses '{glass.name}' (see-through).");
    }

    /// <summary>
    /// Off-white jambs, head and a centre stop around the leaves: they overlap each leaf edge by a
    /// couple of centimetres, so no street light shows between leaf and frame.
    /// </summary>
    private void BuildEntranceFrame(Vector3 doorCenter, Vector3 along, Vector3 inward, List<Leaf> leaves, float doorwayHeight)
    {
        Material frame = GameMaterialLibrary.Find("WindowFrame", "Trim", "metal_mediumlightgrey_mat") ?? FallbackMaterial(new Color(0.86f, 0.85f, 0.80f, 1f));
        var root = new GameObject("ARCADE_WALKIN_ENTRANCE_FRAME");
        _created.Add(root);
        root.transform.position = doorCenter;
        root.transform.rotation = Quaternion.LookRotation(inward, Vector3.up);
        float half = 0f;
        foreach (Leaf leaf in leaves)
            half = Mathf.Max(half, Mathf.Abs(Vector3.Dot(Flat(leaf.Hinge - doorCenter), along)));
        if (half < 0.3f)
            half = 1.05f;
        float alongSign = Vector3.Dot(root.transform.right, along) >= 0f ? 1f : -1f;
        const float jamb = 0.075f, overlap = 0.02f, depth = 0.22f;
        float height = doorwayHeight + 0.02f;
        Piece(root.transform, "Jamb_A", new Vector3(alongSign * -(half + jamb * 0.5f - overlap), height * 0.5f, 0f), new Vector3(jamb, height + jamb, depth), frame, false);
        Piece(root.transform, "Jamb_B", new Vector3(alongSign * (half + jamb * 0.5f - overlap), height * 0.5f, 0f), new Vector3(jamb, height + jamb, depth), frame, false);
        Piece(root.transform, "Head", new Vector3(0f, height + jamb * 0.5f - overlap, 0f), new Vector3(half * 2f + jamb * 2f, jamb, depth), frame, false);
        // Centre stop (astragal) on the street face covers the meeting-edge gap when closed. It is
        // its own swinging piece on the first leaf's hinge, so it moves exactly with that leaf.
        if (leaves.Count > 0)
        {
            var stop = new GameObject("ARCADE_WALKIN_CENTRE_STOP");
            _created.Add(stop);
            Vector3 size = new(0.035f, height - 0.04f, 0.012f);
            stop.transform.position = doorCenter + Vector3.up * (height * 0.5f) - inward * 0.035f;
            stop.transform.rotation = Quaternion.LookRotation(inward, Vector3.up);
            Mesh mesh = MetricBoxMesh.Create(size, "ScheduleIArcade_EntranceStop");
            mesh.vertices = ScaleVertices(mesh.vertices, size);
            mesh.RecalculateBounds();
            _created.Add(mesh);
            stop.AddComponent<MeshFilter>().sharedMesh = mesh;
            stop.AddComponent<MeshRenderer>().sharedMaterial = GameMaterialLibrary.Find("postoffice_blacktrim_mat", "gas station black mat") ?? frame;
            leaves.Add(new Leaf(stop.transform, new Bounds(stop.transform.position, size), Array.Empty<Collider>(), leaves[0].Hinge));
        }
    }

    /// <summary>
    /// Native Schedule I prompt ("E  Open"/"Close") through the game's own InteractableObject on a
    /// trigger spanning the doorway. Returns null (custom prompt fallback) if this build rejects it.
    /// </summary>
    private NativeDoorInteraction? CreateNativeInteraction(DoorPair pair, Vector3 along, float width, Il2CppScheduleOne.Doors.StaticDoor layerSource)
    {
        try
        {
            var go = new GameObject("ARCADE_WALKIN_DOOR_INTERACTION_" + pair.Label.Replace(' ', '_'));
            _created.Add(go);
            go.SetActive(false);
            go.layer = layerSource.IntObj != null ? layerSource.IntObj.gameObject.layer : layerSource.gameObject.layer;
            go.transform.position = pair.InteractPoint - Vector3.up * 0.15f;
            go.transform.rotation = Quaternion.LookRotation(Vector3.Cross(along, Vector3.up), Vector3.up);
            var trigger = go.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(Mathf.Max(0.8f, width - 0.1f), 2.0f, 0.30f);
            var intObj = go.AddComponent<Il2CppScheduleOne.Interaction.InteractableObject>();
            intObj.MaxInteractionRange = DoorInteractDistance + 0.6f;
            intObj.displayLocationPoint = go.transform;
            var native = new NativeDoorInteraction(intObj);
            intObj.onHovered.AddListener((UnityEngine.Events.UnityAction)new Action(() => native.LastHoveredFrame = Time.frameCount));
            intObj.onInteractStart.AddListener((UnityEngine.Events.UnityAction)new Action(() => RequestToggle(pair)));
            go.SetActive(true);
            intObj.SetMessage("Open");
            return native;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Walk-in entrance: native door prompt unavailable ({ex.Message}); using the arcade prompt.");
            return null;
        }
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

    private void BuildVestibule(Transform cornerDoor, Vector3 origin, Vector3 along, Vector3 inward, float depth, float halfWidth, float doorwayHalfWidth, float doorwayHeight, float floorRise, float height, float roomDoorHalfWidth, Transform roomDoor)
    {
        var root = new GameObject("ARCADE_WALKIN_VESTIBULE");
        _created.Add(root);
        root.transform.position = origin;
        root.transform.rotation = Quaternion.LookRotation(inward, Vector3.up);
        // Local axes: +Z = into the arcade, +X = along the building chamfer.

        // Finish the corridor like the arcade interior it leads into (same wall finish as the room's
        // own chamfer wall), with the game's ceiling, skirting and floor materials.
        Material? roomWall = null;
        Transform? roomShellWall = roomDoor.parent != null ? FindDeep(roomDoor.parent, "RawArcade_Wall_Chamfer_BeforeDoor") : null;
        if (roomShellWall != null && roomShellWall.GetComponent<Renderer>() is Renderer shellRenderer)
            roomWall = shellRenderer.sharedMaterial;
        Material wall = roomWall
            ?? GameMaterialLibrary.Find("Plaster1", "BackRoomWall", "Wall", "Walls1")
            ?? cornerDoor.GetComponent<Renderer>()?.sharedMaterial
            ?? FallbackMaterial(new Color(0.55f, 0.55f, 0.58f, 1f));
        Material ceiling = GameMaterialLibrary.Find("Ceiling") ?? wall;
        Material floor = GameMaterialLibrary.Find("Tiles", "Floor", "Concrete") ?? wall;
        Material trim = GameMaterialLibrary.Find("Skirting", "Trim") ?? wall;

        const float wallThickness = 0.12f;
        float start = 0.04f;
        // Run the side walls 12 cm into the room's chamfer wall so no gap shows at the far end.
        float length = depth - start + 0.12f;
        Piece(root.transform, "Vestibule_Wall_Left", new Vector3(-halfWidth - wallThickness * 0.5f, height * 0.5f, start + length * 0.5f), new Vector3(wallThickness, height, length), wall, true);
        Piece(root.transform, "Vestibule_Wall_Right", new Vector3(halfWidth + wallThickness * 0.5f, height * 0.5f, start + length * 0.5f), new Vector3(wallThickness, height, length), wall, true);
        Piece(root.transform, "Vestibule_Ceiling", new Vector3(0f, height + 0.05f, start + length * 0.5f), new Vector3(halfWidth * 2f + wallThickness * 2f, 0.10f, length), ceiling, true);

        float rampLength = Mathf.Sqrt(depth * depth + floorRise * floorRise);
        float rampAngle = Mathf.Atan2(floorRise, depth) * Mathf.Rad2Deg;
        GameObject ramp = Piece(root.transform, "Vestibule_Floor", new Vector3(0f, floorRise * 0.5f - 0.05f, depth * 0.5f), new Vector3(halfWidth * 2f, 0.10f, rampLength + 0.10f), floor, true);
        ramp.transform.localRotation = Quaternion.Euler(-rampAngle, 0f, 0f);

        // Street-side lining around the building opening (visual only: stubs/lintel collide).
        float liningZ = 0.16f;
        float sideWidth = halfWidth - doorwayHalfWidth;
        if (sideWidth > 0.02f)
        {
            Piece(root.transform, "Vestibule_Lining_Left", new Vector3(-(doorwayHalfWidth + sideWidth * 0.5f), height * 0.5f, liningZ), new Vector3(sideWidth, height, 0.03f), wall, false);
            Piece(root.transform, "Vestibule_Lining_Right", new Vector3(doorwayHalfWidth + sideWidth * 0.5f, height * 0.5f, liningZ), new Vector3(sideWidth, height, 0.03f), wall, false);
        }
        if (height - doorwayHeight > 0.02f)
            Piece(root.transform, "Vestibule_Lining_Lintel", new Vector3(0f, (doorwayHeight + height) * 0.5f, liningZ), new Vector3(doorwayHalfWidth * 2f, height - doorwayHeight, 0.03f), wall, false);

        // Room-side lining: closes the strips between the room door frame and the corridor walls
        // and the band above the room door, flush with the room's chamfer wall face.
        float frameHalf = roomDoorHalfWidth + 0.105f;
        float endZ = depth - 0.115f;
        float roomFloor = floorRise;
        float endSide = halfWidth - frameHalf;
        if (endSide > 0.01f)
        {
            Piece(root.transform, "Vestibule_End_Left", new Vector3(-(frameHalf + endSide * 0.5f), roomFloor + (height - roomFloor) * 0.5f, endZ), new Vector3(endSide, height - roomFloor, 0.03f), wall, false);
            Piece(root.transform, "Vestibule_End_Right", new Vector3(frameHalf + endSide * 0.5f, roomFloor + (height - roomFloor) * 0.5f, endZ), new Vector3(endSide, height - roomFloor, 0.03f), wall, false);
        }
        float roomDoorTop = roomFloor + 2.18f + 0.15f;
        if (height - roomDoorTop > 0.02f)
            Piece(root.transform, "Vestibule_End_Header", new Vector3(0f, (roomDoorTop + height) * 0.5f, endZ), new Vector3(halfWidth * 2f, height - roomDoorTop, 0.03f), wall, false);

        Piece(root.transform, "Vestibule_Skirting_Left", new Vector3(-halfWidth + 0.015f, 0.06f + floorRise * 0.5f, start + length * 0.5f), new Vector3(0.03f, 0.12f, length), trim, false);
        Piece(root.transform, "Vestibule_Skirting_Right", new Vector3(halfWidth - 0.015f, 0.06f + floorRise * 0.5f, start + length * 0.5f), new Vector3(0.03f, 0.12f, length), trim, false);

        var lightGo = new GameObject("Vestibule_Light");
        lightGo.transform.SetParent(root.transform, false);
        lightGo.transform.localPosition = new Vector3(0f, height - 0.30f, depth * 0.5f);
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 3.8f;
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

    private sealed class NativeDoorInteraction
    {
        public NativeDoorInteraction(Il2CppScheduleOne.Interaction.InteractableObject interactable)
        {
            Interactable = interactable;
        }

        public Il2CppScheduleOne.Interaction.InteractableObject Interactable { get; }
        public int LastHoveredFrame { get; set; } = -1000;
    }

    private sealed class DoorPair
    {
        private readonly List<Leaf> _leaves;
        private readonly Vector3 _throughAxis;

        public DoorPair(string label, Vector3 interactPoint, List<Leaf> leaves, Vector3 throughAxis)
        {
            Label = label;
            InteractPoint = interactPoint;
            _leaves = leaves;
            _throughAxis = Flat(throughAxis).normalized;
        }

        public string Label { get; }
        public Vector3 InteractPoint { get; }
        public bool WantOpen { get; set; }
        public float AwayTimer { get; set; }
        public int LastToggleFrame { get; set; } = -1000;
        public NativeDoorInteraction? Native { get; set; }
        public bool NativeHoveredRecently => Native != null && Time.frameCount - Native.LastHoveredFrame <= 2;
        private float _open;

        /// <summary>Both leaves open away from the player (into the side the player is not on).</summary>
        public void ChooseSwingAwayFrom(Vector3 player)
        {
            float side = Vector3.Dot(Flat(player - InteractPoint), _throughAxis);
            Vector3 away = side <= 0f ? _throughAxis : -_throughAxis;
            foreach (Leaf leaf in _leaves)
                leaf.SetSwingSide(away, 88f);
        }

        public void UpdateNativeMessage()
        {
            try { Native?.Interactable.SetMessage(WantOpen ? "Close" : "Open"); }
            catch { }
        }

        public void Animate(float step)
        {
            float target = WantOpen ? 1f : 0f;
            if (Mathf.Approximately(_open, target))
                return;
            _open = Mathf.MoveTowards(_open, target, step);
            // Leaves are non-solid only while swinging so a moving leaf never shoves through the
            // player; fully open or closed they collide normally (their colliders move with them).
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
        private readonly Vector3 _hinge;
        private float _angle = 88f;

        public Leaf(Transform target, Bounds closedBounds, Collider[] colliders, Vector3 hinge)
        {
            _target = target;
            _localPosition = target.localPosition;
            _localRotation = target.localRotation;
            _closedBounds = closedBounds;
            _colliders = colliders;
            _hinge = hinge;
        }

        public Transform Target => _target;
        public Vector3 Hinge => _hinge;

        /// <summary>A room-door leaf: its transform IS the hinge pivot at the frame edge.</summary>
        public static Leaf ForHinge(Transform hinge)
        {
            Collider[] colliders = SolidColliders(hinge);
            TryGetBounds(colliders, out Bounds bounds);
            return new Leaf(hinge, bounds, colliders, hinge.position);
        }

        public void SetSwingSide(Vector3 swingSide, float degrees)
        {
            Vector3 arm = Flat(_closedBounds.center - _hinge);
            Vector3 positive = Quaternion.AngleAxis(degrees, Vector3.up) * arm;
            _angle = Vector3.Dot(positive, swingSide) >= 0f ? degrees : -degrees;
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

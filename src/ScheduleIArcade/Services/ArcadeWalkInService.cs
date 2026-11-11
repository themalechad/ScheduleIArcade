using MelonLoader;
using S1API.Entities;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Makes the arcade a physically continuous walk-in shop: the whole ground floor of the real
/// Northtown Arcade building. There is no teleport and no vestibule: the building's own glass corner
/// door is the ONE entrance, and stepping through it puts the player on the arcade floor.
///
/// Measured from Schedule I 0.4.7f12 level1 (Map/Hyland Point/Region_Northtown/Arcade (1)):
///   * entrance = arcade/CornerDoor, a 45-degree chamfer panel whose single BoxCollider seals the
///     chamfer, holding two "Metal Glass Door (Static)" StaticDoor leaves (statically batched);
///   * floor (Foundation top) y = -3.90, equal to the sidewalk outside the door.
/// The service replaces the sealing collider with stubs + lintel around the measured doorway, hides
/// the batched leaves and swings clones of an unbatched vanilla glass PivotDoor leaf in their exact
/// pose (from either side, away from the player; NPCs open it too), makes the ground-floor window
/// glass clear and derives "inside the arcade" from the player's position. Restored on Dispose.
/// </summary>
internal sealed class ArcadeWalkInService : IDisposable
{
    private const string BuildingPath = "Map/Hyland Point/Region_Northtown/Arcade (1)";
    private const float DoorInteractDistance = 2.4f;
    private const float AutoCloseDistance = 4.5f;
    private const float AutoCloseDelay = 6f;
    private const float SwingSeconds = 0.55f;
    private const float LeafLift = 0.006f;

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
    private DoorPair? _door;
    private float _npcScanTimer;
    private float _npcAwayTimer;
    private bool _openedForNpc;
    private const float NpcOpenDistance = 2.2f;
    private const float NpcCloseDelay = 1.5f;
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

            // The vanilla glass leaves are STATICALLY BATCHED in level1, so moving their transforms
            // would move only the colliders. Measure each leaf from its BoxCollider, hide the batched
            // leaf, and swing a clone of an unbatched vanilla glass PivotDoor leaf in its exact pose.
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

            Vector3 buildingDoorCenter = new(doorway.center.x, doorway.min.y, doorway.center.z);
            Vector3 along = AlongAxis(cornerDoor);
            // Inward = towards the building centre, perpendicular to the chamfer.
            Vector3 inward = Flat(building.position - buildingDoorCenter);
            inward = Flat(inward - along * Vector3.Dot(inward, along)).normalized;

            Material? glass = FindTransparentGlass();
            Transform? sourceLeaf = FindVanillaGlassDoorLeaf(out string sourceName);
            var leaves = new List<Leaf>();
            int cloned = 0;
            foreach (var (door, model, box, leafBounds) in measured)
            {
                DisableVanillaDoorInteraction(door);
                HideVanillaLeaf(model);
                Transform pose = box.transform.parent != null && box.transform.name == "Collider" ? box.transform.parent : box.transform;
                Leaf? leaf = sourceLeaf != null ? CloneGlassLeaf(sourceLeaf, pose, model.gameObject.layer, doorway.center) : null;
                if (leaf != null)
                    cloned++;
                else
                    leaf = BuildFallbackLeaf(box, pose, model, doorway.center, glass);
                leaves.Add(leaf);
            }

            float doorwayHalfWidth = HalfExtentAlong(doorway, along, buildingDoorCenter);
            float doorwayHeight = doorway.max.y - doorway.min.y;

            OpenCornerDoorCollider(cornerDoor, doorway);
            ReplaceOpaqueGlass(cornerDoor, glass);
            BuildEntranceHead(buildingDoorCenter, along, inward, leaves, doorwayHeight);

            _door = new DoorPair("ARCADE ENTRANCE", buildingDoorCenter + Vector3.up * 1.2f, leaves, inward);
            _doors.Add(_door);
            _door.Native = CreateNativeInteraction(_door, along, inward, doorwayHalfWidth * 2f, doorwayHeight, staticDoors[0]);

            CollectInteriorOccluders(building, buildingDoorCenter);
            HideStrayVanillaInsideRoom();
            _initialized = true;
            MelonLogger.Msg(cloned > 0
                ? $"Walk-in entrance: ONE door pair, {cloned} leaf/leaves cloned from the vanilla '{sourceName}' (PivotDoor glass door model, transparent glass, black frame, bar handles)."
                : $"Walk-in entrance: ONE door pair; no unbatched vanilla glass PivotDoor leaf was loaded, built matching glass leaves with {(glass != null ? glass.name : "fallback")} glass.");
            MelonLogger.Msg($"Walk-in arcade ready: the Arcade glass doors at {buildingDoorCenter} (opening {doorwayHalfWidth * 2f:0.00} m x {doorwayHeight:0.00} m) open straight into the arcade floor (level threshold, no vestibule). Interact from either side; NPCs open it too. No teleport is used.");
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
        UpdateNpcDoorSensor(dt);

        foreach (DoorPair door in _doors)
        {
            float distance = Flat(door.InteractPoint - position).magnitude;
            if (door.WantOpen && !_openedForNpc && distance > AutoCloseDistance && !IsInsideArcade)
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

    /// <summary>
    /// Mirrors the vanilla DoorSensor -> DoorController.NPCVicinityEnter behaviour of shop doors: any
    /// NPC (citizens, police, employees) within ~2 m of the doorway on either side opens the door away
    /// from it; once no NPC has been near for a moment, a door the NPCs opened closes again (never
    /// through the player). Uses the game's own NPCManager.NPCRegistry.
    /// </summary>
    private void UpdateNpcDoorSensor(float dt)
    {
        if (_door == null)
            return;
        _npcScanTimer -= dt;
        if (_npcScanTimer > 0f)
            return;
        _npcScanTimer = 0.2f;
        Vector3? nearest = null;
        float best = NpcOpenDistance;
        try
        {
            var registry = Il2CppScheduleOne.NPCs.NPCManager.NPCRegistry;
            if (registry != null)
            {
                for (int i = 0; i < registry.Count; i++)
                {
                    var npc = registry[i];
                    if (npc == null || !npc.gameObject.activeInHierarchy || !npc.isVisible)
                        continue;
                    Vector3 p = npc.transform.position;
                    if (Mathf.Abs(p.y - _door.InteractPoint.y) > 2.5f)
                        continue;
                    float d = Flat(p - _door.InteractPoint).magnitude;
                    if (d < best) { best = d; nearest = p; }
                }
            }
        }
        catch
        {
            return;
        }

        if (nearest.HasValue)
        {
            _npcAwayTimer = 0f;
            if (!_door.WantOpen)
            {
                _door.ChooseSwingAwayFrom(nearest.Value);
                if (_door.InSwing(_playerPosition))
                    _door.ChooseSwingAwayFrom(_playerPosition);
                _door.WantOpen = true;
                _openedForNpc = true;
                _door.UpdateNativeMessage();
            }
        }
        else if (_openedForNpc && _door.WantOpen)
        {
            _npcAwayTimer += 0.2f;
            if (_npcAwayTimer > NpcCloseDelay && !_door.InSwing(_playerPosition))
            {
                _door.WantOpen = false;
                _openedForNpc = false;
                _door.UpdateNativeMessage();
            }
        }
    }

    /// <summary>One interaction toggles both leaves; opening swings them away from the player.</summary>
    private void RequestToggle(DoorPair door)
    {
        // Native and fallback input can both see the same E press: one toggle per press.
        if (Time.frameCount - door.LastToggleFrame <= 1 || _blockingInteraction())
            return;
        // Never swing a closing leaf through the player: only blocked when the player stands inside
        // the leaves' swing arc, so closing from the far side works even pressed against the glass.
        if (door.WantOpen && door.InSwing(_playerPosition))
            return;
        door.LastToggleFrame = Time.frameCount;
        _openedForNpc = false;
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
    /// windows. They have no colliders, but they would hide the arcade when the doors are open,
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
            // Always hide the host building's fake-interior boxes so the storefront glass
            // can show our dressed arcade from the street, not a sealed white volume.
            bool original = renderer.enabled;
            renderer.enabled = false;
            Renderer captured = renderer;
            _restore.Add(() => { if (captured != null) captured.enabled = original; });
        }
        ApplyHostWindowGlass(building, FindTransparentGlass());
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

    /// <summary>
    /// Hides only vanilla Map geometry whose bounds centre lies inside our arcade interior (for
    /// example a stray chair, stool or pipe of the host building poking through our walls). The
    /// building's own fake-interior boxes are handled by the occluder toggle and are skipped; nothing
    /// outside the interior (street facades, signs, other buildings) is touched. Restored on Dispose.
    /// </summary>
    private void HideStrayVanillaInsideRoom()
    {
        if (_location.RoomInstance == null)
            return;
        Transform room = _location.RoomInstance.transform;
        var hidden = new List<string>();
        foreach (MeshRenderer renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
        {
            if (renderer == null || !renderer.enabled)
                continue;
            Transform t = renderer.transform;
            if (t.root == null || t.root.name != "Map" || t.IsChildOf(room))
                continue;
            if (_interiorOccluders.Contains(renderer))
                continue;
            string n = t.gameObject.name;
            if (n == "Cube" || n == "Block")
                continue;
            bool known = IsKnownInteriorStray(t);
            Bounds b = renderer.bounds;
            // Huge objects (terrain, combined batches, the building shell) are never "inside".
            // Floor-to-ceiling pipes are allowed a taller Y if their footprint is thin.
            bool thinUpright = b.size.x <= 0.7f && b.size.z <= 0.7f;
            if (b.size.x > 3f || b.size.z > 3f || b.size.y > (known || thinUpright ? 5.0f : 3.5f))
                continue;
            // Only objects lying ENTIRELY inside the arcade volume (all four footprint corners inside the
            // interior and below the ceiling): exterior facade pieces, the sign, neon and the upper
            // storey are never touched.
            bool inside = true;
            for (int c = 0; c < 4 && inside; c++)
            {
                Vector3 corner = new((c & 1) == 0 ? b.min.x : b.max.x, b.center.y, (c & 2) == 0 ? b.min.z : b.max.z);
                Vector3 l = room.InverseTransformPoint(corner);
                inside = ArcadeLayout.IsInsideInterior(l.x, l.z, -0.05f);
            }
            Vector3 lo = room.InverseTransformPoint(b.min), hi = room.InverseTransformPoint(b.max);
            if (Mathf.Max(lo.y, hi.y) > ArcadeLayout.CeilingY + 0.05f || Mathf.Min(lo.y, hi.y) < ArcadeLayout.FloorTop - 0.3f)
                inside = false;
            if (known && !inside)
            {
                Vector3 local = room.InverseTransformPoint(b.center);
                inside = local.y > 0.05f && local.y < ArcadeLayout.CeilingY && ArcadeLayout.IsInsideInterior(local.x, local.z, 0.05f);
            }
            if (!inside)
                continue;
            renderer.enabled = false;
            MeshRenderer captured = renderer;
            _restore.Add(() => { if (captured != null) captured.enabled = true; });
            foreach (Collider collider in t.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled) continue;
                collider.enabled = false;
                Collider c = collider;
                _restore.Add(() => { if (c != null) c.enabled = true; });
            }
            hidden.Add(VanillaPropLibrary.PathOf(t));
        }
        MelonLogger.Msg(hidden.Count == 0
            ? "Walk-in arcade: no stray vanilla geometry inside the arcade interior."
            : "Walk-in arcade: hid " + hidden.Count + " stray vanilla object(s) inside the arcade interior: " + string.Join("; ", hidden));
    }

    /// <summary>
    /// Named leftovers of the host Northtown Arcade (side industrial door, seating, pipes) that
    /// should be hidden when they poke into our interior. Facade pieces (corner doors, window
    /// walls, neon, signs) are never treated as strays.
    /// </summary>
    private static bool IsKnownInteriorStray(Transform t)
    {
        for (Transform? p = t; p != null; p = p.parent)
        {
            string n = p.gameObject.name;
            if (n is "Map" or "Hyland Point" or "Region_Northtown" or "Arcade (1)")
                break;
            if (n.StartsWith("CornerDoor", StringComparison.Ordinal) || n.StartsWith("WindowWall", StringComparison.Ordinal)
                || n.StartsWith("CornerWindowWall", StringComparison.Ordinal) || n.StartsWith("UpperWindowWall", StringComparison.Ordinal)
                || n.StartsWith("Neon", StringComparison.Ordinal) || n.Contains("Curved Wall Light", StringComparison.Ordinal))
                return false;
            int paren = n.LastIndexOf(" (", StringComparison.Ordinal);
            string b = (paren > 0 && n.EndsWith(")", StringComparison.Ordinal) && int.TryParse(n.Substring(paren + 2, n.Length - paren - 3), out _))
                ? n.Substring(0, paren) : n;
            if (b.Equals("Industrial Metal Door (Static)", StringComparison.Ordinal)
                || b.Equals("Chair", StringComparison.Ordinal) || b.Equals("Chair_Business", StringComparison.Ordinal)
                || b.Equals("Armchair", StringComparison.Ordinal) || b.Equals("Bench", StringComparison.Ordinal)
                || b.IndexOf("stool", StringComparison.OrdinalIgnoreCase) >= 0
                || (b.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0 && b.IndexOf("Pipeline", StringComparison.OrdinalIgnoreCase) < 0))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Replaces opaque panes on the host Arcade's own WindowWall / CornerWindowWall /
    /// UpperWindowWall glass with the game's transparent shop-window material so the street
    /// can see into our interior. Frame materials are left alone. Restored on Dispose.
    /// </summary>
    private void ApplyHostWindowGlass(Transform building, Material? glass)
    {
        if (glass == null)
            return;
        // Ground-floor window walls only ("WindowWall", "WindowWall (1)".."(4)"): their glass slot uses
        // the opaque fake "window_opaque_material_1". The upper storey (UpperWindowWall*,
        // CornerWindowWall) keeps its vanilla glass: there is no arcade up there to look into.
        int swapped = 0;
        foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (renderer == null)
                continue;
            string n = renderer.gameObject.name;
            if (!(n == "WindowWall" || n.StartsWith("WindowWall (", StringComparison.Ordinal)))
                continue;
            Material[] original = renderer.sharedMaterials;
            Material[] replaced = (Material[])original.Clone();
            bool any = false;
            for (int i = 0; i < replaced.Length; i++)
            {
                Material? mat = replaced[i];
                if (mat == null || mat == glass)
                    continue;
                if (mat.name.IndexOf("opaque", StringComparison.OrdinalIgnoreCase) >= 0 || mat.name.IndexOf("OpaqueGlass", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    replaced[i] = glass;
                    any = true;
                }
            }
            if (!any)
                continue;
            renderer.sharedMaterials = replaced;
            MeshRenderer captured = renderer;
            Material[] restore = original;
            _restore.Add(() => { if (captured != null) captured.sharedMaterials = restore; });
            swapped++;
        }
        MelonLogger.Msg(swapped > 0
            ? $"Walk-in arcade: {swapped} ground-floor window wall(s) of Arcade (1) now use the game's clear '{glass.name}' (was window_opaque_material_1), so the lit arcade shows from the street."
            : "Walk-in arcade: no opaque ground-floor window glass found to replace.");
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
        Material? named = GameMaterialLibrary.Find("window_glass_material", "Glass", "glass", "window_glass");
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


    private static bool IsGlassDoorName(string name) =>
        name.StartsWith("Basic Metal Glass Door", StringComparison.Ordinal)
        || name.StartsWith("Metal Glass Door (Static)", StringComparison.Ordinal);

    private static void ConsiderDoorLeaf(Transform? leaf, Transform owner, ref Transform? best, ref int bestScore, ref string sourceName, bool preferred)
    {
        if (leaf == null)
            return;
        MeshFilter? filter = leaf.GetComponent<MeshFilter>();
        MeshRenderer? renderer = leaf.GetComponent<MeshRenderer>();
        if (filter == null || filter.sharedMesh == null || renderer == null)
            return;
        if (renderer.isPartOfStaticBatch || filter.sharedMesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal))
            return;
        string path = VanillaPropLibrary.PathOf(owner);
        if (path.IndexOf("Arcade (1)", StringComparison.Ordinal) >= 0 && path.IndexOf("CornerDoor", StringComparison.Ordinal) >= 0)
            return;
        int score = preferred ? 2 : 1;
        if (path.IndexOf("hardware", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
        if (owner.gameObject.name.StartsWith("Basic Metal Glass Door", StringComparison.Ordinal)) score += 1;
        Material? frame = renderer.sharedMaterial;
        if (frame != null && frame.name.IndexOf("black", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
        if (score > bestScore)
        {
            bestScore = score;
            best = leaf;
            sourceName = owner.gameObject.name + " (" + path + ")";
        }
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
                if (pivot == null || !IsGlassDoorName(pivot.gameObject.name))
                    continue;
                Transform container = pivot.DoorTransform != null ? pivot.DoorTransform : pivot.transform;
                ConsiderDoorLeaf(container.Find("Door") ?? container.Find("Model/Door"), pivot.transform, ref best, ref bestScore, ref sourceName, preferred: true);
            }
            // Also accept unbatched "Metal Glass Door (Static)" / "Basic Metal Glass Door" instances
            // that are not PivotDoors (the arcade's own leaves are batched and will be skipped).
            foreach (Transform root in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (root == null || !IsGlassDoorName(root.gameObject.name))
                    continue;
                Transform? leaf = root.Find("Door") ?? root.Find("Model/Door");
                ConsiderDoorLeaf(leaf, root, ref best, ref bestScore, ref sourceName, preferred: false);
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Walk-in entrance: could not search vanilla PivotDoors: {ex.Message}");
        }
        return best;
    }

    /// <summary>Clones the vanilla leaf into the exact closed pose of an Arcade door leaf.</summary>
    private Leaf? CloneGlassLeaf(Transform source, Transform pose, int layer, Vector3 doorwayCenter)
    {
        try
        {
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject);
            _created.Add(clone);
            clone.name = "ARCADE_WALKIN_GLASS_LEAF";
            clone.transform.SetParent(null, false);
            // Lifted 6 mm so the leaf's bottom edge clears the carpet (floor top is 3 mm above the
            // foundation): nothing on the floor is inside the swing arc.
            clone.transform.position = pose.position + Vector3.up * LeafLift;
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
    /// A 35 mm black stile on the hinge edge of a leaf, outside the leaf (away from the doorway centre).
    /// It rotates about the same hinge with the same angle, so it never collides with the wall lining
    /// (the lining opening is 1.10 m either side of the centre; the stile reaches 1.085 m).
    /// </summary>
    private Leaf AddHingeStile(Leaf leaf, Vector3 doorwayCenter, float height)
    {
        Vector3 away = Flat(leaf.Hinge - doorwayCenter).normalized;
        Vector3 size = new(0.04f, height, 0.035f);
        var go = new GameObject("ARCADE_WALKIN_HINGE_STILE");
        _created.Add(go);
        go.transform.position = new Vector3(leaf.Hinge.x, leaf.ClosedBounds.min.y + height * 0.5f + LeafLift, leaf.Hinge.z) + away * (size.z * 0.5f);
        go.transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        Mesh mesh = MetricBoxMesh.Create(size, "ScheduleIArcade_HingeStile");
        mesh.vertices = ScaleVertices(mesh.vertices, size);
        mesh.RecalculateBounds();
        _created.Add(mesh);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = GameMaterialLibrary.Find("Doors Black", "postoffice_blacktrim_mat", "gas station black mat") ?? FallbackMaterial(new Color(0.03f, 0.03f, 0.035f, 1f));
        // Same closed bounds as its leaf so SetSwingSide picks the same rotation sign.
        return new Leaf(go.transform, leaf.ClosedBounds, Array.Empty<Collider>(), leaf.Hinge);
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
        go.transform.position = t.TransformPoint(source.center) + Vector3.up * LeafLift;
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

        Material frame = GameMaterialLibrary.Find("Doors Black", "postoffice_blacktrim_mat", "gas station black mat") ?? MaterialFrom(model, "Door") ?? FallbackMaterial(new Color(0.03f, 0.03f, 0.035f, 1f));
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
    /// A black head above the leaves (out of the swing arc: it starts at the leaf top) and a centre
    /// stop (astragal) that swings with the first leaf and covers the meeting-edge gap when closed.
    /// The hinge-edge gaps are covered by stiles attached to each leaf (see CloneGlassLeaf).
    /// </summary>
    private void BuildEntranceHead(Vector3 doorCenter, Vector3 along, Vector3 inward, List<Leaf> leaves, float doorwayHeight)
    {
        Material frame = GameMaterialLibrary.Find("Doors Black", "postoffice_blacktrim_mat", "gas station black mat") ?? FallbackMaterial(new Color(0.03f, 0.03f, 0.035f, 1f));
        var root = new GameObject("ARCADE_WALKIN_ENTRANCE_HEAD");
        _created.Add(root);
        root.transform.position = doorCenter;
        root.transform.rotation = Quaternion.LookRotation(inward, Vector3.up);
        float half = 0f;
        foreach (Leaf leaf in leaves)
            half = Mathf.Max(half, Mathf.Abs(Vector3.Dot(Flat(leaf.Hinge - doorCenter), along)));
        if (half < 0.3f)
            half = ArcadeLayout.LeafHalfSpan;
        const float head = 0.06f, depth = 0.12f;
        float bottom = doorwayHeight + 0.012f;
        Piece(root.transform, "Head", new Vector3(0f, bottom + head * 0.5f, 0f), new Vector3(half * 2f + 0.07f, head, depth), frame, false);
        if (leaves.Count > 0)
        {
            var stop = new GameObject("ARCADE_WALKIN_CENTRE_STOP");
            _created.Add(stop);
            Vector3 size = new(0.035f, doorwayHeight - 0.04f, 0.012f);
            stop.transform.position = doorCenter + Vector3.up * (doorwayHeight * 0.5f + 0.006f) - inward * 0.035f;
            stop.transform.rotation = Quaternion.LookRotation(inward, Vector3.up);
            Mesh mesh = MetricBoxMesh.Create(size, "ScheduleIArcade_EntranceStop");
            mesh.vertices = ScaleVertices(mesh.vertices, size);
            mesh.RecalculateBounds();
            _created.Add(mesh);
            stop.AddComponent<MeshFilter>().sharedMesh = mesh;
            stop.AddComponent<MeshRenderer>().sharedMaterial = frame;
            leaves.Add(new Leaf(stop.transform, new Bounds(stop.transform.position, size), Array.Empty<Collider>(), leaves[0].Hinge));
        }
    }

    /// <summary>
    /// Native Schedule I prompt ("E  Open"/"Close") through the game's own InteractableObject on a
    /// trigger spanning the doorway. Returns null (custom prompt fallback) if this build rejects it.
    /// </summary>
    private NativeDoorInteraction? CreateNativeInteraction(DoorPair pair, Vector3 along, Vector3 inward, float width, float height, Il2CppScheduleOne.Doors.StaticDoor layerSource)
    {
        try
        {
            // One thin trigger on EACH face of the door, 0.16 m off the leaf plane and covering the whole
            // door. A standing player's camera is always further from the glass than that (capsule
            // radius), so the interact ray from either side hits that side's trigger before the glass
            // or leaf colliders, and the player's capsule can never be inside it. The vanilla
            // InteractableObject gives the native "E  Open/Close" prompt; both triggers drive one door.
            var native = new NativeDoorInteraction();
            int layer = layerSource.IntObj != null ? layerSource.IntObj.gameObject.layer : layerSource.gameObject.layer;
            Vector3 centre = pair.InteractPoint - Vector3.up * 1.2f + Vector3.up * (height * 0.5f);
            foreach (float side in new[] { -1f, 1f })
            {
                var go = new GameObject("ARCADE_WALKIN_DOOR_INTERACTION_" + (side < 0f ? "STREET" : "INSIDE"));
                _created.Add(go);
                go.SetActive(false);
                go.layer = layer;
                go.transform.position = centre + inward * (0.16f * side);
                go.transform.rotation = Quaternion.LookRotation(inward, Vector3.up);
                var trigger = go.AddComponent<BoxCollider>();
                trigger.isTrigger = true;
                trigger.size = new Vector3(Mathf.Max(0.8f, width), Mathf.Max(1.8f, height), 0.04f);
                var intObj = go.AddComponent<Il2CppScheduleOne.Interaction.InteractableObject>();
                intObj.MaxInteractionRange = DoorInteractDistance + 0.6f;
                intObj.displayLocationPoint = go.transform;
                intObj.onHovered.AddListener((UnityEngine.Events.UnityAction)new Action(() => native.LastHoveredFrame = Time.frameCount));
                intObj.onInteractStart.AddListener((UnityEngine.Events.UnityAction)new Action(() => RequestToggle(pair)));
                go.SetActive(true);
                intObj.SetMessage("Open");
                native.Interactables.Add(intObj);
            }
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
        var material = new Material(shader) { name = "ScheduleIArcade_M_EntranceFallback" };
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
        public List<Il2CppScheduleOne.Interaction.InteractableObject> Interactables { get; } = new();
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

        private float _swingSign = 1f;

        /// <summary>
        /// True when a capsule at <paramref name="p"/> overlaps the region the open leaves sweep while
        /// closing (the half-disc of radius leaf + 0.45 m on the swing side, plus the door plane itself).
        /// </summary>
        public bool InSwing(Vector3 p)
        {
            Vector3 d = Flat(p - InteractPoint);
            float through = Vector3.Dot(d, _throughAxis) * _swingSign;
            Vector3 alongAxis = Vector3.Cross(Vector3.up, _throughAxis);
            float alongOffset = Mathf.Abs(Vector3.Dot(d, alongAxis));
            return through > -0.45f && through < ArcadeLayout.LeafHalfSpan + 0.45f && alongOffset < ArcadeLayout.LeafHalfSpan * 2f + 0.3f;
        }

        /// <summary>Both leaves open away from the player (into the side the player is not on).</summary>
        public void ChooseSwingAwayFrom(Vector3 player)
        {
            float side = Vector3.Dot(Flat(player - InteractPoint), _throughAxis);
            Vector3 away = side <= 0f ? _throughAxis : -_throughAxis;
            _swingSign = side <= 0f ? 1f : -1f;
            foreach (Leaf leaf in _leaves)
                leaf.SetSwingSide(away, 88f);
        }

        public void UpdateNativeMessage()
        {
            if (Native == null)
                return;
            foreach (var interactable in Native.Interactables)
            {
                try { interactable.SetMessage(WantOpen ? "Close" : "Open"); }
                catch { }
            }
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
        public Bounds ClosedBounds => _closedBounds;
        public Vector3 Hinge => _hinge;

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

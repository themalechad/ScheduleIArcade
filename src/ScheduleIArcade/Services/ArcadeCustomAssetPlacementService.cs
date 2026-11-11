using MelonLoader;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Arcade-local pickup/placement for every visible mod-authored machine. The machines stay out of
/// Schedule I's global inventory registry so synthetic network definitions cannot contaminate a
/// save or desync multiplayer. Locally they still behave like normal furniture: targeted pickup,
/// one-item HUD tracking, full-model red/green ghost, floor-grid snap, rotation and collision tests.
/// </summary>
internal sealed class ArcadeCustomAssetPlacementService : IDisposable
{
    private sealed class MovableAsset
    {
        public MovableAsset(Transform root, string displayName, Bounds localBounds)
        {
            Root = root;
            DisplayName = displayName;
            LocalBounds = localBounds;
        }

        public Transform Root { get; }
        public string DisplayName { get; }
        public Bounds LocalBounds { get; }
    }

    private static readonly (string Name, string Label)[] AssetMarkers =
    {
        (Constants.CabinetPrefix + "TV", "Arcade TV"),
        (Constants.CabinetPrefix + "01", "Stand-up Arcade 1"),
        (Constants.CabinetPrefix + "02", "Stand-up Arcade 2"),
        ("Pinball Machine 01", "Pinball Machine 1"),
        ("Pinball Machine 02", "Pinball Machine 2"),
        ("Pinball Machine 03", "Pinball Machine 3"),
        ("Pinball Machine 04", "Pinball Machine 4"),
        (Constants.CabinetPrefix + "PACMAN", "Pac-Man Cocktail Table"),
        (Constants.CabinetPrefix + "AIRHOCKEY", "Air Hockey Table")
    };

    private const float PickupDistance = 2.35f;
    private const float GridSnap = 0.5f;
    private const float FloorY = 0.044f;

    private readonly ArcadeLocationService _location;
    private readonly Func<bool> _isInsideArcade;
    private readonly Func<bool> _isPlaying;
    private readonly Func<bool> _isLookingAtCabinet;
    private readonly List<MovableAsset> _assets = new();

    private MovableAsset? _activeAsset;
    private MovableAsset? _lookedAtAsset;
    public bool IsHoldingAsset => _activeAsset != null;
    private GameObject? _ghost;
    private Material? _validGhostMaterial;
    private Material? _invalidGhostMaterial;
    private Texture2D? _hudIcon;
    private bool _assetsInitialized;
    // OnGUI is invoked from Unity's IMGUI event loop. Calling an IL2CPP-backed delegate from
    // there repeatedly tries to unstrip its closure method in this game build and floods the
    // MelonLoader log while a machine is held. Tick is the safe game-update boundary, so cache
    // that state there and let OnGUI stay completely managed-only.
    private bool _insideArcade;
    private bool _cabinetPromptActive;
    private bool _insideStateFailureLogged;
    private bool _carrying;
    private bool _validPlacement;
    private float _localYaw;
    private Vector3 _pickupPosition;
    private Quaternion _pickupRotation;

    public ArcadeCustomAssetPlacementService(
        ArcadeLocationService location,
        Func<bool> isInsideArcade,
        Func<bool> isPlaying,
        Func<bool> isLookingAtCabinet)
    {
        _location = location;
        _isInsideArcade = isInsideArcade;
        _isPlaying = isPlaying;
        _isLookingAtCabinet = isLookingAtCabinet;
    }

    public void Tick()
    {
        if (_location.RoomInstance == null || !_location.UsingRawScene)
        {
            _insideArcade = false;
            return;
        }

        if (!_assetsInitialized)
            InitializeAssets();
        if (_assets.Count == 0)
            return;

        bool inside;
        try
        {
            inside = _isInsideArcade();
            _insideStateFailureLogged = false;
        }
        catch (Exception ex)
        {
            inside = false;
            if (!_insideStateFailureLogged)
            {
                _insideStateFailureLogged = true;
                MelonLogger.Warning($"Custom arcade furniture paused because the arcade-presence callback was unavailable: {ex.Message}");
            }
        }

        _insideArcade = inside;
        if (!inside)
        {
            _cabinetPromptActive = false;
            _lookedAtAsset = null;
            if (_carrying)
                CancelPlacement();
            return;
        }

        // Keep OnGUI entirely managed-only.  This callback only queries our cabinet service,
        // but caching it here also preserves the no-IL2CPP-callback rule for the GUI boundary.
        _cabinetPromptActive = _isLookingAtCabinet();

        if (_carrying)
        {
            UpdatePlacement();
            return;
        }

        _lookedAtAsset = _isPlaying() ? null : ResolveLookedAtAsset();
        if (_lookedAtAsset != null && Input.GetKeyDown(KeyCode.F))
            PickUp(_lookedAtAsset);
    }

    private void InitializeAssets()
    {
        _assetsInitialized = true;
        if (_location.RoomInstance == null)
            return;

        for (int i = 0; i < AssetMarkers.Length; i++)
        {
            (string marker, string label) = AssetMarkers[i];
            Transform? root = FindDeepChild(_location.RoomInstance.transform, marker);
            if (root == null || !TryCalculateLocalBounds(root, out Bounds localBounds))
                continue;
            _assets.Add(new MovableAsset(root, label, localBounds));
        }

        MelonLogger.Msg($"Custom arcade furniture initialized: {_assets.Count} movable cabinet/machine asset(s), each with HUD tracking and a placement ghost.");
    }

    private MovableAsset? ResolveLookedAtAsset()
    {
        Camera camera = Camera.main;
        if (camera == null)
            return null;

        if (!Physics.Raycast(new Ray(camera.transform.position, camera.transform.forward), out RaycastHit hit, PickupDistance))
            return null;

        Transform? current = hit.collider.transform;
        while (current != null)
        {
            for (int i = 0; i < _assets.Count; i++)
            {
                if (_assets[i].Root == current && current.gameObject.activeInHierarchy)
                    return _assets[i];
            }
            current = current.parent;
        }
        return null;
    }

    private void PickUp(MovableAsset asset)
    {
        if (_location.RoomInstance == null)
            return;

        _activeAsset = asset;
        _pickupPosition = asset.Root.localPosition;
        _pickupRotation = asset.Root.localRotation;
        _localYaw = NormalizeYaw(asset.Root.localEulerAngles.y);
        RebuildGhost(asset);
        if (_ghost == null)
        {
            _activeAsset = null;
            return;
        }

        _carrying = true;
        _lookedAtAsset = null;
        asset.Root.gameObject.SetActive(false);
        _ghost.SetActive(true);
        UpdatePlacementGhost();
        MelonLogger.Msg($"Picked up custom '{asset.DisplayName}' (HUD quantity=1); full-model placement ghost enabled.");
    }

    private void UpdatePlacement()
    {
        if (_activeAsset == null || _ghost == null)
        {
            CancelPlacement();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Q))
            _localYaw = NormalizeYaw(_localYaw - 90f);
        if (Input.GetKeyDown(KeyCode.E))
            _localYaw = NormalizeYaw(_localYaw + 90f);

        UpdatePlacementGhost();

        if (Input.GetMouseButtonDown(0) && _validPlacement)
        {
            Place();
            return;
        }

        if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
            CancelPlacement();
    }

    private void UpdatePlacementGhost()
    {
        if (_ghost == null || _activeAsset == null || _location.RoomInstance == null)
            return;

        Transform room = _location.RoomInstance.transform;
        Camera camera = Camera.main;
        if (camera == null)
            return;

        Vector3 targetWorld = camera.transform.position + camera.transform.forward * 2.65f;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, 5f))
            targetWorld = hit.point;

        Vector3 local = room.InverseTransformPoint(targetWorld);
        local.x = Mathf.Round(local.x / GridSnap) * GridSnap;
        local.z = Mathf.Round(local.z / GridSnap) * GridSnap;
        local = SnapFlushToNearbyWall(_activeAsset, local, _localYaw);
        local.y = FloorY - _activeAsset.LocalBounds.min.y;

        _ghost.transform.localPosition = local;
        _ghost.transform.localRotation = Quaternion.Euler(0f, _localYaw, 0f);
        _validPlacement = IsValidPlacement(_activeAsset, local, _localYaw);
        ApplyGhostMaterial(_validPlacement ? _validGhostMaterial : _invalidGhostMaterial);
    }

    private static Vector3 SnapFlushToNearbyWall(MovableAsset asset, Vector3 rootPosition, float localYaw)
    {
        const float west = -3.185f;
        const float east = 3.185f;
        const float south = -4.016f;
        const float north = 4.016f;
        const float chamferStartZ = 2.077f;
        const float northWestX = -0.976f;
        const float finishedGap = 0.015f;
        const float magnetRange = 0.46f;

        Vector3[] corners = GetFootprintCorners(asset.LocalBounds, rootPosition, localYaw);
        float minX = corners.Min(c => c.x);
        float maxX = corners.Max(c => c.x);
        float minZ = corners.Min(c => c.z);
        float maxZ = corners.Max(c => c.z);

        float bestDistance = float.PositiveInfinity;
        Vector3 bestCorrection = Vector3.zero;

        void Consider(float gap, Vector3 correction)
        {
            float distance = Mathf.Abs(gap);
            if (distance <= magnetRange && distance < bestDistance)
            {
                bestDistance = distance;
                bestCorrection = correction;
            }
        }

        if (rootPosition.z < south + 1.25f)
        {
            float gap = minZ - south;
            Consider(gap, Vector3.forward * (finishedGap - gap));
        }
        if (rootPosition.z > north - 1.25f && rootPosition.x > northWestX - 0.55f)
        {
            float gap = north - maxZ;
            Consider(gap, Vector3.forward * (gap - finishedGap));
        }
        if (rootPosition.x > east - 1.25f)
        {
            float gap = east - maxX;
            Consider(gap, Vector3.right * (gap - finishedGap));
        }
        if (rootPosition.x < west + 1.25f && rootPosition.z < chamferStartZ + 0.55f)
        {
            float gap = minX - west;
            Consider(gap, Vector3.right * (finishedGap - gap));
        }

        // Diagonal north-west wall. Its inward normal points toward (+X,-Z). Moving the root by
        // (finishedGap-currentGap) along that normal puts the closest footprint corner flush.
        Vector2 a = new(west, chamferStartZ);
        Vector2 b = new(northWestX, north);
        Vector2 segment = b - a;
        Vector2 root2 = new(rootPosition.x, rootPosition.z);
        float segmentT = Vector2.Dot(root2 - a, segment) / segment.sqrMagnitude;
        if (segmentT >= -0.15f && segmentT <= 1.15f)
        {
            Vector2 inward = new Vector2(segment.y, -segment.x).normalized;
            float gap = float.PositiveInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                float cornerGap = Vector2.Dot(new Vector2(corners[i].x, corners[i].z) - a, inward);
                gap = Mathf.Min(gap, cornerGap);
            }
            Consider(gap, new Vector3(inward.x, 0f, inward.y) * (finishedGap - gap));
        }

        return rootPosition + bestCorrection;
    }

    private static Vector3[] GetFootprintCorners(Bounds bounds, Vector3 rootPosition, float localYaw)
    {
        Quaternion rotation = Quaternion.Euler(0f, localYaw, 0f);
        return new[]
        {
            rootPosition + rotation * new Vector3(bounds.min.x, 0f, bounds.min.z),
            rootPosition + rotation * new Vector3(bounds.min.x, 0f, bounds.max.z),
            rootPosition + rotation * new Vector3(bounds.max.x, 0f, bounds.min.z),
            rootPosition + rotation * new Vector3(bounds.max.x, 0f, bounds.max.z)
        };
    }

    private bool IsValidPlacement(MovableAsset asset, Vector3 localPosition, float localYaw)
    {
        if (_location.RoomInstance == null)
            return false;

        Bounds bounds = asset.LocalBounds;
        Quaternion localRotation = Quaternion.Euler(0f, localYaw, 0f);
        Vector3[] footprintCorners = GetFootprintCorners(bounds, localPosition, localYaw);
        for (int i = 0; i < footprintCorners.Length; i++)
        {
            if (!IsInsideFinishedFootprint(footprintCorners[i]))
                return false;
        }

        Transform room = _location.RoomInstance.transform;
        Vector3 center = room.TransformPoint(localPosition + localRotation * bounds.center);
        Quaternion rotation = room.rotation * localRotation;
        Vector3 halfExtents = bounds.extents;
        halfExtents.x = Mathf.Max(0.08f, halfExtents.x - 0.025f);
        halfExtents.y = Mathf.Max(0.08f, halfExtents.y - 0.025f);
        halfExtents.z = Mathf.Max(0.08f, halfExtents.z - 0.025f);
        Collider[] overlaps = Physics.OverlapBox(center, halfExtents, rotation, ~0, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider collider = overlaps[i];
            if (collider == null || !collider.enabled || !collider.transform.IsChildOf(room))
                continue;
            if (_ghost != null && collider.transform.IsChildOf(_ghost.transform))
                continue;
            if (IsRoomArchitecture(collider.transform, room))
                continue;
            return false;
        }
        return true;
    }

    private static bool IsInsideFinishedFootprint(Vector3 point)
    {
        const float west = -3.185f;
        const float east = 3.185f;
        const float south = -4.016f;
        const float north = 4.016f;
        const float chamferStartZ = 2.077f;
        const float northWestX = -0.976f;
        const float margin = 0.015f;

        if (point.x < west + margin || point.x > east - margin ||
            point.z < south + margin || point.z > north - margin)
            return false;
        if (point.z <= chamferStartZ)
            return true;

        float t = Mathf.InverseLerp(chamferStartZ, north, point.z);
        float minimumX = Mathf.Lerp(west, northWestX, t);
        return point.x >= minimumX + margin;
    }

    private static bool IsRoomArchitecture(Transform transform, Transform room)
    {
        Transform? current = transform;
        while (current != null && current != room)
        {
            string name = current.name;
            if (name.IndexOf("Floor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Ceiling", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.StartsWith("RawArcade_Collision_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("RawArcade_Wall_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Arcade_Baseboard", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Arcade_Crown", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Arcade_Corner", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Enviorment", StringComparison.OrdinalIgnoreCase))
                return true;
            current = current.parent;
        }
        return false;
    }

    private void Place()
    {
        if (_activeAsset == null || _ghost == null)
            return;

        MovableAsset asset = _activeAsset;
        asset.Root.localPosition = _ghost.transform.localPosition;
        asset.Root.localRotation = _ghost.transform.localRotation;
        asset.Root.gameObject.SetActive(true);
        _ghost.SetActive(false);
        _carrying = false;
        _validPlacement = false;
        _activeAsset = null;
        Physics.SyncTransforms();
        MelonLogger.Msg($"Placed custom '{asset.DisplayName}' at local {asset.Root.localPosition}, yaw={_localYaw:0}; cabinet/game identity preserved.");
    }

    private void CancelPlacement()
    {
        string label = _activeAsset?.DisplayName ?? "custom arcade asset";
        if (_activeAsset != null)
        {
            _activeAsset.Root.localPosition = _pickupPosition;
            _activeAsset.Root.localRotation = _pickupRotation;
            _activeAsset.Root.gameObject.SetActive(true);
        }
        if (_ghost != null)
            _ghost.SetActive(false);

        _carrying = false;
        _validPlacement = false;
        _activeAsset = null;
        Physics.SyncTransforms();
        MelonLogger.Msg($"Cancelled '{label}' placement; restored its previous position.");
    }

    private void RebuildGhost(MovableAsset asset)
    {
        if (_location.RoomInstance == null)
            return;

        if (_validGhostMaterial == null)
            _validGhostMaterial = CreateGhostMaterial(new Color(0.15f, 1f, 0.40f, 0.48f), "ScheduleIArcade_M_ValidPlacementGhost");
        if (_invalidGhostMaterial == null)
            _invalidGhostMaterial = CreateGhostMaterial(new Color(1f, 0.12f, 0.18f, 0.48f), "ScheduleIArcade_M_InvalidPlacementGhost");
        if (_ghost != null)
            UnityEngine.Object.Destroy(_ghost);

        _ghost = UnityEngine.Object.Instantiate(asset.Root.gameObject, _location.RoomInstance.transform, false);
        _ghost.name = "ARCADE_CUSTOM_ASSET_PLACEMENT_GHOST";
        Collider[] colliders = _ghost.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
            colliders[i].enabled = false;
        Light[] lights = _ghost.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < lights.Length; i++)
            lights[i].enabled = false;
        ApplyGhostMaterial(_validGhostMaterial);
        _ghost.SetActive(false);
    }

    private void ApplyGhostMaterial(Material? material)
    {
        if (_ghost == null || material == null)
            return;
        Renderer[] renderers = _ghost.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            renderers[i].sharedMaterial = material;
    }

    private static Material CreateGhostMaterial(Color color, string name)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null)
            throw new InvalidOperationException("Could not find a shader for the custom placement ghost.");

        var material = new Material(shader) { name = name, renderQueue = 3000 };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", 5f);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", 10f);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.EnableKeyword("_ALPHABLEND_ON");
        return material;
    }

    public void OnGUI()
    {
        if (!_insideArcade)
            return;

        if (_lookedAtAsset != null && !_carrying)
        {
            // CabinetInteractionService owns the one combined play/move card for screen-bearing
            // machines. Non-playable tables and pinballs still get this dedicated move prompt.
            if (_cabinetPromptActive)
                return;
            ArcadePromptUi.DrawCard(
                _lookedAtAsset.DisplayName.ToUpperInvariant(),
                "PRESS [F]  PICK UP & MOVE",
                "Close-range only — placement snaps flush to floors and walls.",
                132f,
                new Color(0.18f, 1.00f, 0.42f, 1f));
            return;
        }

        if (!_carrying || _activeAsset == null)
            return;

        ArcadePromptUi.DrawCard(
            "CARRYING  1 × " + _activeAsset.DisplayName.ToUpperInvariant(),
            _validPlacement ? "GREEN GHOST — LEFT CLICK TO PLACE" : "RED GHOST — MOVE TO A CLEAR SPACE",
            "The placement grid snaps the machine flush to the floor or nearby wall.",
            220f,
            _validPlacement ? new Color(0.18f, 1.00f, 0.42f, 1f) : new Color(1.00f, 0.20f, 0.28f, 1f));
        ArcadePromptUi.DrawCard(
            "PLACEMENT CONTROLS",
            "[Q] / [E] ROTATE   •   RIGHT CLICK / ESC CANCEL",
            "The held item remains tracked as one arcade machine.",
            132f,
            new Color(0.10f, 0.75f, 1.00f, 1f));
    }

    private void EnsureHudIcon()
    {
        if (_hudIcon != null)
            return;

        const int size = 48;
        _hudIcon = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_CustomMachineInventoryIcon",
            filterMode = FilterMode.Point
        };
        var pixels = new Color32[size * size];
        Array.Fill(pixels, new Color32(0, 0, 0, 0));
        for (int y = 7; y < 42; y++)
        {
            for (int x = 8; x < 40; x++)
            {
                bool frame = x < 11 || x >= 37 || y < 10 || y >= 39;
                pixels[y * size + x] = frame
                    ? new Color32(22, 28, 45, 255)
                    : new Color32(20, 225, 255, 255);
            }
        }
        for (int y = 15; y < 31; y++)
        {
            for (int x = 14; x < 34; x++)
                pixels[y * size + x] = new Color32(255, 42, 132, 255);
        }
        _hudIcon.SetPixels32(pixels);
        _hudIcon.Apply(false, false);
    }

    private static bool TryCalculateLocalBounds(Transform root, out Bounds localBounds)
    {
        bool initialized = false;
        localBounds = new Bounds(Vector3.zero, Vector3.zero);
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled)
                continue;

            Bounds world = renderer.bounds;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 worldCorner = world.center + Vector3.Scale(world.extents, new Vector3(x, y, z));
                        Vector3 localCorner = root.InverseTransformPoint(worldCorner);
                        if (!initialized)
                        {
                            localBounds = new Bounds(localCorner, Vector3.zero);
                            initialized = true;
                        }
                        else
                        {
                            localBounds.Encapsulate(localCorner);
                        }
                    }
                }
            }
        }
        return initialized;
    }

    private static float NormalizeYaw(float yaw)
    {
        yaw %= 360f;
        return yaw < 0f ? yaw + 360f : yaw;
    }

    private static Transform? FindDeepChild(Transform root, string name)
    {
        if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform? found = FindDeepChild(root.GetChild(i), name);
            if (found != null)
                return found;
        }
        return null;
    }

    public void Dispose()
    {
        if (_carrying)
            CancelPlacement();
        if (_ghost != null)
            UnityEngine.Object.Destroy(_ghost);
        if (_validGhostMaterial != null)
            UnityEngine.Object.Destroy(_validGhostMaterial);
        if (_invalidGhostMaterial != null)
            UnityEngine.Object.Destroy(_invalidGhostMaterial);
        if (_hudIcon != null)
            UnityEngine.Object.Destroy(_hudIcon);

        _assets.Clear();
        _ghost = null;
        _validGhostMaterial = null;
        _invalidGhostMaterial = null;
        _hudIcon = null;
        _activeAsset = null;
        _lookedAtAsset = null;
        _assetsInitialized = false;
    }
}

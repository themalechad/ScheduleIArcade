using MelonLoader;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Makes the pocket arcade participate in Schedule I's native build-placement systems.
///
/// Floor-standing furniture (including the vanilla TV shown in testing) is a GridItem, so wall
/// Surface proxies alone are not enough. This service clones native Tile/Grid structure from an
/// owned property, strips its visuals, assigns the arcade's own stable Grid GUID before Awake, and
/// moves that invisible grid into the arcade. Because the clone preserves Schedule I's real Tile/IndoorTile setup, detection layer,
/// footprint rules, and ParentProperty relationship, vanilla BuildUpdate_Grid can validate/place
/// furniture normally instead of relying on a custom "force green" patch.
///
/// Wall Surface proxies are retained for actual wall-mounted SurfaceItems. All world discovery is
/// one-shot on arcade entry; there are no recurring whole-scene scans.
/// </summary>
internal sealed class ArcadeBuildSurfaceService : IDisposable
{
    private static readonly string[] ArcadeWallColliderNames =
    {
        "RawArcade_Collision_South",
        "RawArcade_Collision_East",
        "RawArcade_Wall_West_Lower",
        "RawArcade_Wall_Chamfer_BeforeDoor",
        "RawArcade_Wall_Chamfer_AfterDoor",
        "RawArcade_Wall_North_Full"
    };

    // Top of the visible checkerboard floor is ~0.043 in raw-scene local coordinates.
    // Keep the vanilla placement tiles just above it so BuildUpdate_Grid's tile ray/detection
    // always wins without making placed furniture visibly float.
    private const float ArcadeGridPlaneY = 0.044f;

    private readonly ArcadeLocationService _location;
    private readonly Func<bool> _isInsideArcade;
    private readonly List<GameObject> _surfaceProxies = new();

    private GameObject? _floorGridProxy;
    private Il2CppScheduleOne.Tiles.Grid? _floorGrid;
    private string _floorGridGuid = string.Empty;
    private readonly List<Il2CppScheduleOne.EntityFramework.GridItem> _placedArcadeItems = new();
    private bool _wasInside;
    private bool _attemptedThisVisit;
    private bool _gridReady;
    private bool _wallsReady;

    public ArcadeBuildSurfaceService(ArcadeLocationService location, Func<bool> isInsideArcade)
    {
        _location = location;
        _isInsideArcade = isInsideArcade;
    }

    public void Tick()
    {
        MaintainPlacedArcadeItems();

        bool inside = _isInsideArcade();
        if (!inside)
        {
            _wasInside = false;
            _attemptedThisVisit = false;
            return;
        }

        if (!_wasInside)
        {
            _wasInside = true;
            _attemptedThisVisit = false;
        }

        if ((_gridReady && _wallsReady) || _attemptedThisVisit)
            return;

        _attemptedThisVisit = true;

        if (!_gridReady)
            _gridReady = TryRegisterArcadeFloorGrid();

        if (!_wallsReady)
            _wallsReady = TryRegisterArcadeWalls();
    }

    private bool TryRegisterArcadeFloorGrid()
    {
        if (_location.RoomInstance == null || !_location.UsingRawScene)
            return false;

        GameObject? staging = null;
        GameObject? clone = null;
        try
        {
            Il2CppScheduleOne.Tiles.Grid? template = FindLargestOwnedGridTemplate(out int sourceTileCount);
            if (template == null || template.gameObject == null)
            {
                MelonLogger.Warning("Arcade building: no loaded Grid belonging to an owned property was available. The TV is a floor GridItem, so it will stay red until an owned property grid is available.");
                return false;
            }

            object? parentProperty = ReflectionUtil.GetMemberValue(template, "ParentProperty");
            Transform? propertyTransform = ReflectionUtil.GetMemberValue(parentProperty, "transform") as Transform;
            string propertyName = ReflectionUtil.GetString(parentProperty, "PropertyName");
            if (parentProperty == null || propertyTransform == null)
            {
                MelonLogger.Warning("Arcade building: owned Grid template did not expose a valid ParentProperty transform; refusing to create a broken placement grid.");
                return false;
            }

            // IMPORTANT: a Grid clone inherits the source grid's serialized _guid. If it is
            // instantiated while active, Grid.Awake() immediately registers that copied GUID and
            // temporarily replaces the real property grid in GUIDManager. Calling RegenerateGUID
            // AFTER Awake only changes the serialized string; it does not update Grid.GUID or the
            // GUIDManager entry. That is why a placed GridItem could initialize and then vanish.
            //
            // Instantiate under an INACTIVE staging parent so Awake cannot run yet. Regenerate the
            // serialized GUID first, prepare the clone, then activate it under the real Property.
            string sourceGuid = ReflectionUtil.GetString(template, "GUID");

            staging = new GameObject("ARCADE_BUILD_GRID_STAGING");
            staging.transform.SetParent(propertyTransform, false);
            staging.SetActive(false);

            clone = UnityEngine.Object.Instantiate(template.gameObject, staging.transform, false);
            clone.name = "ARCADE_BUILD_GRID_NATIVE_PROXY";
            clone.SetActive(false);

            Il2CppScheduleOne.Tiles.Grid? clonedGrid = clone.GetComponent<Il2CppScheduleOne.Tiles.Grid>();
            if (clonedGrid == null)
            {
                UnityEngine.Object.Destroy(clone);
                clone = null;
                MelonLogger.Warning("Arcade building: cloned owned grid did not retain its Grid component.");
                return false;
            }

            // Give the arcade floor its OWN stable GUID before Grid.Awake. We still keep the
            // proxy beneath a real owned Property so vanilla GridItem.InitializeGridItem can
            // resolve a legitimate PropertyCode without adding a synthetic Property to global
            // Schedule I lists.
            bool guidPrepared = ReflectionUtil.TrySetMemberValue(clonedGrid, "_guid", Constants.ArcadeFloorGridGuid)
                || ReflectionUtil.TrySetMemberValue(clonedGrid, "StaticGUID", Constants.ArcadeFloorGridGuid);
            bool propertyPrepared = ReflectionUtil.TrySetMemberValue(clonedGrid, "_parentProperty", parentProperty);
            if (!guidPrepared || !propertyPrepared)
            {
                UnityEngine.Object.Destroy(clone);
                clone = null;
                MelonLogger.Warning($"Arcade building: could not prepare the stable arcade Grid identity before Awake (guid={guidPrepared}, property={propertyPrepared}).");
                return false;
            }

            Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = false;
            }

            Il2CppScheduleOne.Tiles.Tile[] tiles = clone.GetComponentsInChildren<Il2CppScheduleOne.Tiles.Tile>(includeInactive: true);
            if (tiles.Length == 0)
            {
                UnityEngine.Object.Destroy(clone);
                clone = null;
                MelonLogger.Warning("Arcade building: owned Grid clone contained no vanilla Tile components.");
                return false;
            }

            // Hyland Manor provides a wide 8.0 m grid, but it is only about 5.5 m deep. The arcade
            // is roughly 8.2 m deep, leaving a red no-build strip along the north/south walls.
            // Clone complete native tile rows on both ends before Awake so the final grid reaches
            // the floor/wall junction around the entire room.
            int addedPerimeterTiles = ExtendGridRowsToArcadeWalls(clone, clonedGrid, tiles);
            if (addedPerimeterTiles > 0)
                tiles = clone.GetComponentsInChildren<Il2CppScheduleOne.Tiles.Tile>(includeInactive: true);

            // Prepare all cloned tiles before their Awake callbacks run. Clear copied occupancy
            // lists from the source property and explicitly bind every tile to the new Grid.
            for (int i = 0; i < tiles.Length; i++)
            {
                Il2CppScheduleOne.Tiles.Tile tile = tiles[i];
                if (tile == null || tile.gameObject == null)
                    continue;

                ReflectionUtil.TrySetMemberValue(tile, "OwnerGrid", clonedGrid);
                ReflectionUtil.TryInvoke(ReflectionUtil.GetMemberValue(tile, "BuildableOccupants"), "Clear");
                ReflectionUtil.TryInvoke(ReflectionUtil.GetMemberValue(tile, "OccupantTiles"), "Clear");

                Renderer? tileRenderer = tile.GetComponent<Renderer>();
                if (tileRenderer != null)
                    tileRenderer.enabled = false;

                Collider? collider = tile.GetComponent<Collider>();
                if (collider != null)
                {
                    collider.enabled = true;
                    if (collider is BoxCollider tileBox)
                    {
                        Vector3 center = tileBox.center;
                        Vector3 size = tileBox.size;
                        center.y = -0.006f;
                        size.y = 0.012f;
                        tileBox.center = center;
                        tileBox.size = size;
                    }
                }
                else
                {
                    var box = tile.gameObject.AddComponent<BoxCollider>();
                    // Top face lands exactly on the grid transform. With the grid 1 mm above the
                    // visible floor this remains easy to raycast but placed assets do not float.
                    box.center = new Vector3(0f, -0.006f, 0f);
                    box.size = new Vector3(0.49f, 0.012f, 0.49f);
                }
            }

            // Keep the proxy beneath the real owned Property hierarchy so GridItem.GetProperty()
            // succeeds, but place the grid's WORLD transform over the custom arcade floor.
            clone.transform.SetParent(propertyTransform, false);
            clone.transform.rotation = _location.RoomInstance.transform.rotation;
            clone.transform.localScale = Vector3.one;

            Bounds localTileBounds = CalculateTileLocalBounds(clone.transform, tiles);
            Vector3 tileCenter = localTileBounds.center;
            Vector3 desiredTileCenterWorld = _location.RoomInstance.transform.TransformPoint(
                new Vector3(0f, ArcadeGridPlaneY, 0f));
            Vector3 rotatedTileCenter = clone.transform.rotation * tileCenter;
            clone.transform.position = desiredTileCenterWorld - rotatedTileCenter;

            // Disable helper colliders that are not actual Tile colliders before activation.
            Collider[] allColliders = clone.GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < allColliders.Length; i++)
            {
                Collider collider = allColliders[i];
                if (collider == null)
                    continue;
                if (collider.GetComponent<Il2CppScheduleOne.Tiles.Tile>() == null)
                    collider.enabled = false;
            }

            // Now Awake runs with: (1) a unique pre-generated GUID, (2) a real Property ancestor,
            // and (3) correctly rebound Tile.OwnerGrid references.
            clone.SetActive(true);

            // Awake only rehydrates the serialized source-tile coordinate list. The perimeter
            // rows were cloned in code, so they have colliders and draw the grid but are missing
            // from the native coordinate lookup that BuildUpdate_Grid uses for furniture. Rebuild
            // that lookup from *every* active Tile before asking vanilla placement to use it.
            RebuildNativeTileTopology(clonedGrid, tiles);

            object? clonedParentProperty = ReflectionUtil.GetMemberValue(clonedGrid, "ParentProperty");
            string runtimeGuid = ReflectionUtil.GetString(clonedGrid, "GUID");
            if (clonedParentProperty == null
                || !runtimeGuid.Equals(Constants.ArcadeFloorGridGuid, StringComparison.OrdinalIgnoreCase)
                || runtimeGuid.Equals(sourceGuid, StringComparison.OrdinalIgnoreCase))
            {
                UnityEngine.Object.Destroy(clone);
                clone = null;
                MelonLogger.Warning($"Arcade building: proxy Grid failed stable-GUID/property validation (proxyGUID='{runtimeGuid}', expected='{Constants.ArcadeFloorGridGuid}', sourceGUID='{sourceGuid}', property={(clonedParentProperty == null ? "null" : "ok")}).");
                return false;
            }

            _floorGrid = clonedGrid;
            _floorGridGuid = runtimeGuid;
            _floorGridProxy = clone;
            clone = null; // ownership moved to the service

            if (staging != null)
            {
                UnityEngine.Object.Destroy(staging);
                staging = null;
            }

            Physics.SyncTransforms();

            float width = localTileBounds.size.x + 0.5f;
            float depth = localTileBounds.size.z + 0.5f;
            string clonedPropertyName = ReflectionUtil.GetString(clonedParentProperty, "PropertyName");
            MelonLogger.Msg($"Arcade floor building enabled using native owned Grid from '{propertyName}' ({tiles.Length} cloned tiles; source={sourceTileCount}; perimeterAdded={addedPerimeterTiles}; coverage~{width:0.0}m x {depth:0.0}m; ParentProperty='{clonedPropertyName}'; proxyGUID={runtimeGuid}). The grid reaches every finished wall and sits flush with the visible floor; the owned Property is only a vanilla initialization template.");
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Arcade floor-grid registration failed safely: {ex}");
            if (clone != null)
                UnityEngine.Object.Destroy(clone);
            if (staging != null)
                UnityEngine.Object.Destroy(staging);
            return false;
        }
    }

    private bool TryRegisterArcadeWalls()
    {
        if (_location.RoomInstance == null || !_location.UsingRawScene)
            return false;

        try
        {
            Il2CppScheduleOne.Building.Surface? template = FindOwnedWallSurfaceTemplate();
            if (template == null)
            {
                // Floor placement is the priority for the current TV test. Do not treat missing
                // wall surfaces as a floor-building failure.
                return true;
            }

            object? parentProperty = ReflectionUtil.GetMemberValue(template, "ParentProperty");
            Transform? propertyTransform = ReflectionUtil.GetMemberValue(parentProperty, "transform") as Transform;
            if (parentProperty == null || propertyTransform == null)
                return true;

            object? surfaceType = ReflectionUtil.GetMemberValue(template, "SurfaceType");
            object? validFaces = ReflectionUtil.GetMemberValue(template, "ValidFaces");
            int buildLayer = template.gameObject.layer;

            int registered = 0;
            for (int i = 0; i < ArcadeWallColliderNames.Length; i++)
            {
                Transform? wall = FindDeepChild(_location.RoomInstance.transform, ArcadeWallColliderNames[i]);
                if (wall == null)
                    continue;

                // Surface.Awake parses BakedGUID immediately. Adding Surface to an ACTIVE object
                // caused the FormatException seen in M2.26 because BakedGUID was still empty. Build
                // each proxy inactive, set its GUID/property first, then activate it.
                var proxy = new GameObject("ARCADE_BUILD_SURFACE_" + ArcadeWallColliderNames[i]);
                proxy.SetActive(false);
                proxy.layer = buildLayer;
                proxy.transform.SetParent(propertyTransform, false);

                var box = proxy.AddComponent<BoxCollider>();
                box.center = Vector3.zero;
                box.size = Vector3.one;

                Il2CppScheduleOne.Building.Surface surface = proxy.AddComponent<Il2CppScheduleOne.Building.Surface>();
                bool guidPrepared = ReflectionUtil.TrySetMemberValue(surface, "BakedGUID", Guid.NewGuid().ToString());
                bool propertyPrepared = ReflectionUtil.TrySetMemberValue(surface, "ParentProperty", parentProperty)
                    || ReflectionUtil.TrySetMemberValue(surface, "<ParentProperty>k__BackingField", parentProperty);
                if (!guidPrepared || !propertyPrepared)
                {
                    UnityEngine.Object.Destroy(proxy);
                    continue;
                }
                if (surfaceType != null)
                    ReflectionUtil.TrySetMemberValue(surface, "SurfaceType", surfaceType);
                if (validFaces != null)
                    ReflectionUtil.TrySetMemberValue(surface, "ValidFaces", validFaces);

                proxy.transform.position = wall.position;
                proxy.transform.rotation = wall.rotation;
                SetWorldScale(proxy.transform, wall.lossyScale);
                proxy.SetActive(true);

                _surfaceProxies.Add(proxy);
                registered++;
            }

            Physics.SyncTransforms();
            if (registered > 0)
            {
                string propertyName = ReflectionUtil.GetString(parentProperty, "PropertyName");
                MelonLogger.Msg($"Arcade wall building enabled on {registered} GUID-safe Surface proxy/proxies using owned property '{propertyName}'.");
            }
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Arcade wall-surface registration failed safely: {ex.Message}");
            return true; // Never block floor placement because optional wall proxies failed.
        }
    }

    private static Il2CppScheduleOne.Tiles.Grid? FindLargestOwnedGridTemplate(out int tileCount)
    {
        tileCount = 0;
        Il2CppScheduleOne.Tiles.Grid? best = null;

        // One-time scan only. Pick the largest owned indoor grid so its existing tile field is very
        // likely to cover the arcade's roughly 6 x 7 metre floor without scaling tile spacing.
        Il2CppScheduleOne.Tiles.Grid[] grids = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Tiles.Grid>(includeInactive: true);
        for (int i = 0; i < grids.Length; i++)
        {
            Il2CppScheduleOne.Tiles.Grid candidate = grids[i];
            if (candidate == null || candidate.gameObject == null)
                continue;

            object? property = ReflectionUtil.GetMemberValue(candidate, "ParentProperty");
            if (property == null || !ReflectionUtil.GetBool(property, "IsOwned"))
                continue;

            Il2CppScheduleOne.Tiles.Tile[] tiles = candidate.GetComponentsInChildren<Il2CppScheduleOne.Tiles.Tile>(includeInactive: true);
            if (tiles.Length <= tileCount)
                continue;

            bool hasIndoorTile = false;
            for (int t = 0; t < tiles.Length; t++)
            {
                if (tiles[t] is Il2CppScheduleOne.Tiles.IndoorTile)
                {
                    hasIndoorTile = true;
                    break;
                }
            }

            // Prefer an indoor grid, but if all owned grids use the base Tile wrapper still keep
            // the largest candidate as a fallback.
            if (best == null || hasIndoorTile || tileCount == 0)
            {
                best = candidate;
                tileCount = tiles.Length;
            }
        }

        return best;
    }

    private static Bounds CalculateTileLocalBounds(Transform root, Il2CppScheduleOne.Tiles.Tile[] tiles)
    {
        bool initialized = false;
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);

        for (int i = 0; i < tiles.Length; i++)
        {
            Il2CppScheduleOne.Tiles.Tile tile = tiles[i];
            if (tile == null || tile.transform == null)
                continue;

            Vector3 local = root.InverseTransformPoint(tile.transform.position);
            if (!initialized)
            {
                bounds = new Bounds(local, Vector3.zero);
                initialized = true;
            }
            else
            {
                bounds.Encapsulate(local);
            }
        }

        return initialized ? bounds : new Bounds(Vector3.zero, Vector3.one);
    }

    private static int ExtendGridRowsToArcadeWalls(
        GameObject gridRoot,
        Il2CppScheduleOne.Tiles.Grid ownerGrid,
        Il2CppScheduleOne.Tiles.Tile[] sourceTiles)
    {
        if (gridRoot == null || ownerGrid == null || sourceTiles.Length == 0)
            return 0;

        float tileSize = Il2CppScheduleOne.Tiles.Grid.TileSize;
        if (tileSize < 0.20f || tileSize > 1.0f)
            tileSize = 0.50f;

        Transform root = gridRoot.transform;
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;
        for (int i = 0; i < sourceTiles.Length; i++)
        {
            Il2CppScheduleOne.Tiles.Tile tile = sourceTiles[i];
            if (tile == null || tile.transform == null)
                continue;
            float z = root.InverseTransformPoint(tile.transform.position).z;
            minZ = Mathf.Min(minZ, z);
            maxZ = Mathf.Max(maxZ, z);
        }

        if (float.IsInfinity(minZ) || float.IsInfinity(maxZ))
            return 0;

        float currentDepth = maxZ - minZ + tileSize;
        // Sixteen half-metre rows cover 8.0 m: their outer tile edges meet the arcade's ~7.9 m
        // interior depth without pushing the useful snap row behind either wall.
        const float requiredDepth = 8.00f;
        int additionalRows = Mathf.Max(0, Mathf.CeilToInt((requiredDepth - currentDepth) / tileSize));
        if (additionalRows == 0)
            return 0;
        int southRows = additionalRows / 2;
        int northRows = additionalRows - southRows;

        float rowTolerance = tileSize * 0.20f;
        var minRow = new List<Il2CppScheduleOne.Tiles.Tile>();
        var maxRow = new List<Il2CppScheduleOne.Tiles.Tile>();
        for (int i = 0; i < sourceTiles.Length; i++)
        {
            Il2CppScheduleOne.Tiles.Tile tile = sourceTiles[i];
            if (tile == null || tile.transform == null)
                continue;
            float z = root.InverseTransformPoint(tile.transform.position).z;
            if (Mathf.Abs(z - minZ) <= rowTolerance)
                minRow.Add(tile);
            if (Mathf.Abs(z - maxZ) <= rowTolerance)
                maxRow.Add(tile);
        }

        if (minRow.Count == 0 || maxRow.Count == 0)
            return 0;

        int coordinateStepForPositiveZ = DetermineTileCoordinateStep(root, sourceTiles);
        int added = 0;
        added += CloneGridRows(root, ownerGrid, minRow, southRows, -1, coordinateStepForPositiveZ, tileSize, "South");
        added += CloneGridRows(root, ownerGrid, maxRow, northRows, +1, coordinateStepForPositiveZ, tileSize, "North");
        return added;
    }

    private static void RebuildNativeTileTopology(
        Il2CppScheduleOne.Tiles.Grid ownerGrid,
        Il2CppScheduleOne.Tiles.Tile[] tiles)
    {
        if (ownerGrid == null || tiles.Length == 0)
            throw new InvalidOperationException("The arcade native Grid has no tiles to register.");

        ownerGrid.Tiles.Clear();
        ownerGrid.CoordinateTilePairs.Clear();
        int registered = 0;
        for (int i = 0; i < tiles.Length; i++)
        {
            Il2CppScheduleOne.Tiles.Tile tile = tiles[i];
            if (tile == null || tile.gameObject == null)
                continue;

            tile.OwnerGrid = ownerGrid;
            ownerGrid.RegisterTile(tile);
            registered++;
        }

        ownerGrid.ProcessCoordinateDataPairs();
        ownerGrid.SetGridSize();
        if (registered != tiles.Length || ownerGrid.Tiles.Count != registered)
            throw new InvalidOperationException($"Native Grid registration mismatch (registered={registered}, gridTiles={ownerGrid.Tiles.Count}, expected={tiles.Length}).");
    }

    private static int DetermineTileCoordinateStep(Transform root, Il2CppScheduleOne.Tiles.Tile[] tiles)
    {
        for (int i = 0; i < tiles.Length; i++)
        {
            Il2CppScheduleOne.Tiles.Tile a = tiles[i];
            if (a == null)
                continue;
            float az = root.InverseTransformPoint(a.transform.position).z;
            for (int j = i + 1; j < tiles.Length; j++)
            {
                Il2CppScheduleOne.Tiles.Tile b = tiles[j];
                if (b == null || a.x != b.x || a.y == b.y)
                    continue;
                float bz = root.InverseTransformPoint(b.transform.position).z;
                if (Mathf.Abs(bz - az) < 0.20f)
                    continue;
                return (bz - az) * (b.y - a.y) >= 0f ? 1 : -1;
            }
        }
        return 1;
    }

    private static int CloneGridRows(
        Transform gridRoot,
        Il2CppScheduleOne.Tiles.Grid ownerGrid,
        List<Il2CppScheduleOne.Tiles.Tile> sourceRow,
        int rowCount,
        int localZDirection,
        int coordinateStepForPositiveZ,
        float tileSize,
        string side)
    {
        int added = 0;
        for (int row = 1; row <= rowCount; row++)
        {
            for (int i = 0; i < sourceRow.Count; i++)
            {
                Il2CppScheduleOne.Tiles.Tile source = sourceRow[i];
                if (source == null || source.gameObject == null)
                    continue;

                Vector3 sourceLocal = gridRoot.InverseTransformPoint(source.transform.position);
                var tileObject = UnityEngine.Object.Instantiate(source.gameObject, gridRoot, false);
                tileObject.name = $"ARCADE_PERIMETER_TILE_{side}_{row:00}_{i:00}";
                tileObject.transform.localPosition = sourceLocal + Vector3.forward * (localZDirection * row * tileSize);
                tileObject.transform.localRotation = Quaternion.identity;

                Il2CppScheduleOne.Tiles.Tile? tile = tileObject.GetComponent<Il2CppScheduleOne.Tiles.Tile>();
                if (tile == null)
                {
                    UnityEngine.Object.Destroy(tileObject);
                    continue;
                }

                tile.x = source.x;
                tile.y = source.y + localZDirection * coordinateStepForPositiveZ * row;
                tile.AvailableOffset = source.AvailableOffset;
                tile.OwnerGrid = ownerGrid;
                ReflectionUtil.TryInvoke(ReflectionUtil.GetMemberValue(tile, "BuildableOccupants"), "Clear");
                ReflectionUtil.TryInvoke(ReflectionUtil.GetMemberValue(tile, "OccupantTiles"), "Clear");
                added++;
            }
        }
        return added;
    }

    private static Il2CppScheduleOne.Building.Surface? FindOwnedWallSurfaceTemplate()
    {
        var surfaces = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Building.Surface>(includeInactive: true);
        Il2CppScheduleOne.Building.Surface? fallbackOwned = null;

        for (int i = 0; i < surfaces.Length; i++)
        {
            Il2CppScheduleOne.Building.Surface candidate = surfaces[i];
            if (candidate == null || candidate.gameObject == null)
                continue;

            object? property = ReflectionUtil.GetMemberValue(candidate, "ParentProperty");
            if (property == null || !ReflectionUtil.GetBool(property, "IsOwned"))
                continue;

            fallbackOwned ??= candidate;
            string typeName = ReflectionUtil.GetMemberValue(candidate, "SurfaceType")?.ToString() ?? string.Empty;
            if (typeName.Equals("Wall", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return fallbackOwned;
    }

    private static Transform? FindDeepChild(Transform root, string name)
    {
        if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            return root;

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

    private static void SetWorldScale(Transform target, Vector3 desiredWorldScale)
    {
        Transform? parent = target.parent;
        Vector3 parentScale = parent != null ? parent.lossyScale : Vector3.one;
        target.localScale = new Vector3(
            Mathf.Abs(parentScale.x) > 0.0001f ? desiredWorldScale.x / parentScale.x : desiredWorldScale.x,
            Mathf.Abs(parentScale.y) > 0.0001f ? desiredWorldScale.y / parentScale.y : desiredWorldScale.y,
            Mathf.Abs(parentScale.z) > 0.0001f ? desiredWorldScale.z / parentScale.z : desiredWorldScale.z);
    }

    internal void NotifyGridItemCreated(Il2CppScheduleOne.Tiles.Grid? grid, Il2CppScheduleOne.EntityFramework.GridItem? item)
    {
        if (grid == null || item == null || _location.RoomInstance == null || string.IsNullOrWhiteSpace(_floorGridGuid))
            return;

        string guid = ReflectionUtil.GetString(grid, "GUID");
        if (!guid.Equals(_floorGridGuid, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_placedArcadeItems.Contains(item))
            _placedArcadeItems.Add(item);

        DetachPlacedItemFromTemplatePropertyCulling(item);
        KeepPlacedItemInArcade(item);

        string parentName = ReflectionUtil.GetString(ReflectionUtil.GetMemberValue(item, "ParentProperty"), "PropertyName");
        MelonLogger.Msg($"Arcade build: placed GridItem '{item.gameObject.name}' on arcade-owned grid {guid}. Detached it from '{parentName}' culling and retained its visual object inside the custom arcade.");
    }

    private void MaintainPlacedArcadeItems()
    {
        if (_location.RoomInstance == null || _placedArcadeItems.Count == 0)
            return;

        for (int i = _placedArcadeItems.Count - 1; i >= 0; i--)
        {
            Il2CppScheduleOne.EntityFramework.GridItem? item = _placedArcadeItems[i];
            if (item == null || item.gameObject == null)
            {
                _placedArcadeItems.RemoveAt(i);
                continue;
            }

            DetachPlacedItemFromTemplatePropertyCulling(item);
            KeepPlacedItemInArcade(item);
        }
    }

    private static void DetachPlacedItemFromTemplatePropertyCulling(Il2CppScheduleOne.EntityFramework.GridItem item)
    {
        if (item == null)
            return;

        try
        {
            object? parentProperty = ReflectionUtil.GetMemberValue(item, "ParentProperty");
            object? buildables = ReflectionUtil.GetMemberValue(parentProperty, "BuildableItems");
            ReflectionUtil.TryInvoke(buildables, "Remove", item);
        }
        catch
        {
        }

        try
        {
            item.SetCulled(false);
        }
        catch
        {
            ReflectionUtil.TryInvokeWithDefaultBoolValue(item, "SetCulled", false);
        }
    }

    private void KeepPlacedItemInArcade(Il2CppScheduleOne.EntityFramework.GridItem item)
    {
        if (_location.RoomInstance == null || item == null || item.transform == null)
            return;

        Transform room = _location.RoomInstance.transform;
        if (!item.transform.IsChildOf(room))
            item.transform.SetParent(room, true);

        if (!item.gameObject.activeSelf)
            item.gameObject.SetActive(true);
    }

    internal bool IsArcadeGrid(Il2CppScheduleOne.Tiles.Grid? grid)
    {
        if (grid == null || string.IsNullOrWhiteSpace(_floorGridGuid))
            return false;
        return ReflectionUtil.GetString(grid, "GUID").Equals(_floorGridGuid, StringComparison.OrdinalIgnoreCase);
    }

    internal void RestoreNativeArcadePlacementValidity(Il2CppScheduleOne.Building.BuildUpdate_Grid? update)
    {
        if (update == null)
            return;

        // In some rooms BuildUpdate_Grid evaluates its old serialized coordinate cache for one
        // frame after the valid arcade tile was detected. The real closest tile is authoritative:
        // it belongs to our fully registered Grid and vanilla's actual Place path will therefore
        // create the item on that Grid. This stops the red-only ghost without affecting any other
        // property or trying to place outside the native tile field.
        Il2CppScheduleOne.Building.TileIntersection? closest = update._closestIntersection;
        Il2CppScheduleOne.Tiles.Tile? tile = closest?.tile;
        if (tile == null || !IsArcadeGrid(tile.OwnerGrid))
            return;

        update._validPosition = true;
    }

    public void Dispose()
    {
        if (_floorGridProxy != null)
        {
            UnityEngine.Object.Destroy(_floorGridProxy);
            _floorGridProxy = null;
        }

        for (int i = 0; i < _surfaceProxies.Count; i++)
        {
            if (_surfaceProxies[i] != null)
                UnityEngine.Object.Destroy(_surfaceProxies[i]);
        }
        _surfaceProxies.Clear();
        _placedArcadeItems.Clear();
        _floorGrid = null;
        _floorGridGuid = string.Empty;

        _gridReady = false;
        _wallsReady = false;
        _wasInside = false;
        _attemptedThisVisit = false;
    }
}

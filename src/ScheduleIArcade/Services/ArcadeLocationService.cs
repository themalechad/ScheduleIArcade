using MelonLoader;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class ArcadeLocationService : IDisposable
{
    // Ripped Main.unity places the native Arcade building at (-48,-4,141.5) with a 180-degree
    // rotation. This user-supplied point is the usable interior alignment inside that shell.
    private static readonly Vector3 NativeArcadeInteriorPosition = new(-48.4f, -3.8f, 141.4f);
    private const float NativeArcadeInteriorYaw = 180f;

    private readonly AssetBundleService _assets;
    private readonly ArcadePreferences _preferences;
    private readonly RawArcadeSceneService _rawScene = new();

    public ArcadeLocationService(AssetBundleService assets, ArcadePreferences preferences)
    {
        _assets = assets;
        _preferences = preferences;
    }

    public GameObject? RoomInstance { get; private set; }
    public Transform? SpawnMarker { get; private set; }
    public Transform? ExitMarker { get; private set; }
    public bool UsingProceduralFallback { get; private set; }
    public bool UsingRawScene { get; private set; }

    public bool Spawn()
    {
        if (RoomInstance != null)
            return true;

        var prefab = _assets.LoadRoomPrefab();
        if (prefab != null)
        {
            RoomInstance = UnityEngine.Object.Instantiate(
                prefab,
                NativeArcadeInteriorPosition,
                Quaternion.Euler(0f, NativeArcadeInteriorYaw, 0f));
            RoomInstance.name = "ScheduleIArcade_Runtime";
            UsingProceduralFallback = false;
            UsingRawScene = false;

            SpawnMarker = FindDeepChild(RoomInstance.transform, Constants.SpawnMarkerName);
            ExitMarker = FindDeepChild(RoomInstance.transform, Constants.ExitMarkerName);

            if (SpawnMarker == null || ExitMarker == null)
            {
                MelonLogger.Warning("Custom arcade bundle loaded, but ARCADE_SPAWN/ARCADE_EXIT is missing. Falling back to generated test room.");
                UnityEngine.Object.Destroy(RoomInstance);
                RoomInstance = null;
            }
            else
            {
                MelonLogger.Msg($"Arcade room spawned inside the native Arcade building at {NativeArcadeInteriorPosition}.");
                return true;
            }
        }

        if (_rawScene.IsAvailable)
        {
            try
            {
                var root = new GameObject("ScheduleIArcade_RawImportedRoom");
                root.transform.position = NativeArcadeInteriorPosition;
                root.transform.rotation = Quaternion.Euler(0f, NativeArcadeInteriorYaw, 0f);
                if (_rawScene.TryPopulate(root, out Transform? rawSpawn, out Transform? rawExit) && rawSpawn != null && rawExit != null)
                {
                    RoomInstance = root;
                    SpawnMarker = rawSpawn;
                    ExitMarker = rawExit;
                    UsingRawScene = true;
                    UsingProceduralFallback = false;
                            MelonLogger.Msg("Arcade room spawned from the supplied FBX runtime geometry/textures.");
                    return true;
                }

                UnityEngine.Object.Destroy(root);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Raw arcade scene bootstrap failed: {ex}");
            }
        }

        return SpawnProceduralTestRoom();
    }

    private bool SpawnProceduralTestRoom()
    {
        try
        {
            Vector3 origin = _preferences.RoomPosition;
            var root = new GameObject("ScheduleIArcade_ProceduralTestRoom");
            root.transform.position = origin;
            RoomInstance = root;
            UsingProceduralFallback = true;
            UsingRawScene = false;

            // Thick floor plus a second catch floor. This avoids controller tunnelling during
            // teleport and gives the player a safety net if Unity resolves the first frame oddly.
            CreateSolid("Floor", root.transform, new Vector3(0f, -1f, 0f), new Vector3(16f, 2f, 12f));
            CreateSolid("SafetyFloor", root.transform, new Vector3(0f, -4f, 0f), new Vector3(22f, 4f, 18f));
            CreateSolid("Ceiling", root.transform, new Vector3(0f, 4.25f, 0f), new Vector3(16f, 0.5f, 12f));

            // Exterior shell. The south wall is split to leave a doorway around X=0.
            CreateSolid("Wall_North", root.transform, new Vector3(0f, 2f, 6f), new Vector3(16f, 4.5f, 0.5f));
            CreateSolid("Wall_West", root.transform, new Vector3(-8f, 2f, 0f), new Vector3(0.5f, 4.5f, 12f));
            CreateSolid("Wall_East", root.transform, new Vector3(8f, 2f, 0f), new Vector3(0.5f, 4.5f, 12f));
            CreateSolid("Wall_South_Left", root.transform, new Vector3(-4.75f, 2f, -6f), new Vector3(6.5f, 4.5f, 0.5f));
            CreateSolid("Wall_South_Right", root.transform, new Vector3(4.75f, 2f, -6f), new Vector3(6.5f, 4.5f, 0.5f));
            CreateSolid("Door_Header", root.transform, new Vector3(0f, 3.6f, -6f), new Vector3(3f, 1.3f, 0.5f));

            // A few visual pillars/fixtures so collision can be tested while walking around.
            CreateSolid("CenterDivider", root.transform, new Vector3(0f, 1.5f, 1.5f), new Vector3(0.4f, 3f, 3.5f));
            CreateSolid("Counter", root.transform, new Vector3(-5.5f, 0.75f, 3.8f), new Vector3(3f, 1.5f, 1.2f));

            // Spawn just inside the door.
            SpawnMarker = CreateMarker(Constants.SpawnMarkerName, root.transform, new Vector3(0f, 1.25f, -4.8f));
            // Exit interaction point is just inside the same doorway.
            ExitMarker = CreateMarker(Constants.ExitMarkerName, root.transform, new Vector3(0f, 1.25f, -5.25f));

            // Two cabinets prove the registry can handle multiple machines from the start.
            CreateTestCabinet(root.transform, "01", new Vector3(-4.3f, 1.15f, 4.6f), Quaternion.Euler(0f, 180f, 0f));
            CreateTestCabinet(root.transform, "02", new Vector3(4.3f, 1.15f, 4.6f), Quaternion.Euler(0f, 180f, 0f));

            MelonLogger.Warning("Arcade AssetBundle and raw room package are not available. Spawned the procedural TEST room at the configured RoomX/Y/Z; it is NOT connected to the Arcade building's door (the walk-in entrance needs the packaged raw room).");
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"Failed to spawn procedural arcade test room: {ex}");
            Dispose();
            return false;
        }
    }

    private static GameObject CreateSolid(string name, Transform parent, Vector3 localPosition, Vector3 scale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = scale;
        return go;
    }

    private static Transform CreateMarker(string name, Transform parent, Vector3 localPosition)
    {
        var marker = new GameObject(name);
        marker.transform.SetParent(parent, false);
        marker.transform.localPosition = localPosition;
        return marker.transform;
    }

    private static void CreateTestCabinet(Transform parent, string suffix, Vector3 localPosition, Quaternion localRotation)
    {
        var cabinet = new GameObject(Constants.CabinetPrefix + suffix);
        cabinet.transform.SetParent(parent, false);
        cabinet.transform.localPosition = localPosition;
        cabinet.transform.localRotation = localRotation;

        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "CabinetBody";
        body.transform.SetParent(cabinet.transform, false);
        body.transform.localPosition = Vector3.zero;
        body.transform.localScale = new Vector3(1.4f, 2.3f, 1.2f);

        var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
        head.name = "CabinetHead";
        head.transform.SetParent(cabinet.transform, false);
        head.transform.localPosition = new Vector3(0f, 0.65f, -0.3f);
        head.transform.localScale = new Vector3(1.5f, 1.0f, 0.8f);

        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = Constants.ScreenPrefix + suffix;
        screen.transform.SetParent(cabinet.transform, false);
        screen.transform.localPosition = new Vector3(0f, 0.72f, -0.72f);
        screen.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        screen.transform.localScale = new Vector3(0.95f, 0.7f, 1f);

        // Quad collider can steal raycasts from the cabinet body but Resolve() walks parents,
        // so that is fine and makes aiming directly at the screen work naturally.
    }

    /// <summary>
    /// True when a world position is inside the arcade room itself (not the entrance vestibule).
    /// This replaces the old teleport flag: being "in the arcade" is now purely spatial.
    /// </summary>
    public bool IsPointInsideRoom(Vector3 worldPosition)
    {
        if (RoomInstance == null)
            return false;

        Vector3 local = RoomInstance.transform.InverseTransformPoint(worldPosition);
        if (UsingRawScene)
            return RawArcadeSceneService.IsInsideInteriorLocal(local);

        // Procedural/AssetBundle fallback rooms: axis-aligned footprint of their geometry.
        Bounds bounds = new(Vector3.zero, Vector3.zero);
        bool any = false;
        foreach (Renderer renderer in RoomInstance.GetComponentsInChildren<Renderer>())
        {
            if (renderer == null)
                continue;
            if (!any) { bounds = renderer.bounds; any = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        return any && bounds.Contains(worldPosition);
    }

    public void Dispose()
    {
        SpawnMarker = null;
        ExitMarker = null;
        UsingProceduralFallback = false;
        UsingRawScene = false;
        _rawScene.Dispose();
        if (RoomInstance != null)
        {
            UnityEngine.Object.Destroy(RoomInstance);
            RoomInstance = null;
        }
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
}

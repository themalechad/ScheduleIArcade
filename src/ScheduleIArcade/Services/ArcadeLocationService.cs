using MelonLoader;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class ArcadeLocationService : IDisposable
{
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
    public Transform? BackExitMarker { get; private set; }
    public bool UsingProceduralFallback { get; private set; }
    public bool UsingRawScene { get; private set; }
    private bool _entryAnchorPrepared;

    public bool Spawn()
    {
        if (RoomInstance != null)
            return true;

        var prefab = _assets.LoadRoomPrefab();
        if (prefab != null)
        {
            RoomInstance = UnityEngine.Object.Instantiate(prefab, _preferences.RoomPosition, Quaternion.identity);
            RoomInstance.name = "ScheduleIArcade_Runtime";
            UsingProceduralFallback = false;
            UsingRawScene = false;
            _entryAnchorPrepared = false;

            SpawnMarker = FindDeepChild(RoomInstance.transform, Constants.SpawnMarkerName);
            ExitMarker = FindDeepChild(RoomInstance.transform, Constants.ExitMarkerName);
            BackExitMarker = FindDeepChild(RoomInstance.transform, Constants.BackExitMarkerName);

            if (SpawnMarker == null || ExitMarker == null)
            {
                MelonLogger.Warning("Custom arcade bundle loaded, but ARCADE_SPAWN/ARCADE_EXIT is missing. Falling back to generated test room.");
                UnityEngine.Object.Destroy(RoomInstance);
                RoomInstance = null;
            }
            else
            {
                MelonLogger.Msg($"Arcade room spawned from AssetBundle at {_preferences.RoomPosition}.");
                return true;
            }
        }

        if (_rawScene.IsAvailable)
        {
            try
            {
                var root = new GameObject("ScheduleIArcade_RawImportedRoom");
                root.transform.position = _preferences.RoomPosition;
                if (_rawScene.TryPopulate(root, out Transform? rawSpawn, out Transform? rawExit) && rawSpawn != null && rawExit != null)
                {
                    RoomInstance = root;
                    SpawnMarker = rawSpawn;
                    ExitMarker = rawExit;
                    BackExitMarker = FindDeepChild(root.transform, Constants.BackExitMarkerName);
                    UsingRawScene = true;
                    UsingProceduralFallback = false;
                    _entryAnchorPrepared = false;
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
            _entryAnchorPrepared = false;

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

            MelonLogger.Warning("Arcade AssetBundle is not available. Spawned procedural TEST arcade so door/teleport/collision/cabinet gameplay can be tested now.");
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
    /// Moves the procedural pocket interior away from the playable map without putting it in
    /// the sky. The room keeps the same Y elevation as the real Arcade entrance, so even a
    /// physics hiccup cannot turn into a 300-metre fall back to the map.
    /// </summary>
    public bool PrepareForEntry(Vector3 exteriorAnchor)
    {
        if (RoomInstance == null || SpawnMarker == null)
            return false;

        if ((!UsingProceduralFallback && !UsingRawScene) || _entryAnchorPrepared)
            return true;

        // A 1000 m horizontal offset isolates the pocket interior while keeping better physics/render precision than the old 2500 m test offset.
        // We intentionally ignore the old persisted RoomY=300 development preference here.
        // M2.37 moves the entrance onto the diagonal north-west chamfer. The chamfer's inward
        // normal is roughly (+X,-Z) in room-local space. Rotating the room ~177 degrees maps that
        // inward direction to north-west in world space, so the player's preserved exterior yaw
        // still points naturally into the arcade after teleporting through the door.
        if (UsingRawScene)
            RoomInstance.transform.rotation = Quaternion.Euler(0f, 177f, 0f);

        RoomInstance.transform.position = new Vector3(
            exteriorAnchor.x + 1000f,
            exteriorAnchor.y,
            exteriorAnchor.z + 1000f);

        // The room and all primitive colliders moved this frame. Force Unity's physics world to
        // see the new transforms before ArcadeEntranceService teleports the player onto the floor.
        Physics.SyncTransforms();

        _entryAnchorPrepared = true;
        string source = UsingRawScene ? "real textured arcade" : "procedural arcade";
        if (UsingRawScene)
            MelonLogger.Msg($"{source} anchored at {RoomInstance.transform.position} and rotated 135° so the north-wall double-door entry continues the player's NW facing direction.");
        else
            MelonLogger.Msg($"{source} anchored at safe same-elevation pocket position {RoomInstance.transform.position}.");
        return true;
    }

    public void Dispose()
    {
        SpawnMarker = null;
        ExitMarker = null;
        BackExitMarker = null;
        UsingProceduralFallback = false;
        UsingRawScene = false;
        _entryAnchorPrepared = false;
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

using MelonLoader;
using ScheduleIArcade.Utils;
using UnityEngine;
using UnityEngine.AI;

namespace ScheduleIArcade.Services;

/// <summary>
/// Lets NPCs and police walk into and around the arcade with the systems the game already uses.
///
/// Schedule I moves every NPC with a UnityEngine.AI NavMeshAgent on baked NavMeshSurface data, one
/// surface per agent type (Humanoid, Big Humanoid, Employee, Ignore Costs); NPCMovement.SetDestination
/// samples that navmesh (NavMeshUtility.SamplePosition, with a cache) and PursuitBehaviour drives
/// officers through the same SetDestination. The vanilla bake stops at the outside of the sealed
/// Arcade corner door (verified offline from the game's navmesh data by tests/ArcadeGeometryCheck),
/// so this service, for EVERY agent type the game defines:
///   1. builds a NavMeshData for the arcade floor (the shell's floor mesh as walkable source, the wall
///      lining as not-walkable boxes) with NavMeshBuilder and adds it with NavMesh.AddNavMeshData;
///   2. adds a bidirectional NavMeshLink through the doorway joining the sidewalk navmesh and the
///      arcade floor (NavMesh.AddLink); agents traverse it automatically;
///   3. makes every cabinet / the counter a carving NavMeshObstacle;
///   4. clears NavMeshUtility's sample cache so positions inside the arcade resolve to the new floor.
/// The door opening for NPCs is the walk-in door's NPC sensor (ArcadeWalkInService). Everything is
/// removed again on Dispose. This cannot be exercised offline: the results are logged at runtime.
/// </summary>
internal sealed class ArcadeNpcAccessService : IDisposable
{
    private readonly ArcadeLocationService _location;
    private readonly List<NavMeshDataInstance> _data = new();
    private readonly List<NavMeshLinkInstance> _links = new();
    private readonly List<UnityEngine.Object> _created = new();

    public ArcadeNpcAccessService(ArcadeLocationService location) => _location = location;

    public bool Ready { get; private set; }

    public void Initialize()
    {
        GameObject? room = _location.RoomInstance;
        Mesh? floor = _location.FloorMesh;
        if (room == null || floor == null || !_location.UsingRawScene)
        {
            MelonLogger.Msg("Arcade NPC access: no arcade floor mesh; NPC navigation into the arcade is not available.");
            return;
        }

        try
        {
            Transform t = room.transform;
            var (dx, dz, nx, nz) = ArcadeLayout.Doorway();
            Vector3 doorLocal = new(dx, ArcadeLayout.FloorTop, dz);
            Vector3 inLocal = new(nx, 0f, nz);
            Vector3 outside = t.TransformPoint(doorLocal - inLocal * 0.75f);
            Vector3 inside = t.TransformPoint(doorLocal + inLocal * 0.85f);
            Vector3 centre = t.TransformPoint(new Vector3(0f, ArcadeLayout.FloorTop, 0f));
            var bounds = new Bounds(t.TransformPoint(new Vector3(0f, 1.5f, 0f)), new Vector3(14f, 6f, 14f));

            var sources = new Il2CppSystem.Collections.Generic.List<NavMeshBuildSource>();
            var floorSource = new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Mesh,
                sourceObject = floor,
                transform = t.localToWorldMatrix,
                area = 0
            };
            sources.Add(floorSource);
            int walls = 0;
            foreach (BoxCollider box in room.GetComponentsInChildren<BoxCollider>(true))
            {
                if (box == null || box.isTrigger || !box.gameObject.name.StartsWith(ArcadeShellBuilder.WallPrefix, StringComparison.Ordinal))
                    continue;
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = box.size,
                    transform = Matrix4x4.TRS(box.transform.TransformPoint(box.center), box.transform.rotation, Vector3.one),
                    area = 1 // Not Walkable: cuts the floor at the walls, no polygons on the wall tops
                });
                walls++;
            }

            int count = NavMesh.GetSettingsCount();
            var report = new List<string>();
            for (int i = 0; i < count; i++)
            {
                NavMeshBuildSettings settings = NavMesh.GetSettingsByIndex(i);
                int agent = settings.agentTypeID;
                string agentName = NavMesh.GetSettingsNameFromID(agent);
                NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
                if (data == null)
                {
                    report.Add($"{agentName}: bake failed");
                    continue;
                }
                data.name = "ScheduleIArcade_NavMesh_" + agentName;
                _created.Add(data);
                NavMeshDataInstance instance = NavMesh.AddNavMeshData(data);
                if (instance.valid)
                    _data.Add(instance);

                var link = new NavMeshLinkData
                {
                    startPosition = outside,
                    endPosition = inside,
                    width = 1.6f,
                    costModifier = -1f,
                    bidirectional = true,
                    area = 0,
                    agentTypeID = agent
                };
                NavMeshLinkInstance linkInstance = NavMesh.AddLink(link);
                if (linkInstance.valid)
                    _links.Add(linkInstance);

                var filter = new NavMeshQueryFilter { agentTypeID = agent, areaMask = NavMesh.AllAreas };
                bool streetOk = NavMesh.SamplePosition(outside, out NavMeshHit streetHit, 1.0f, filter);
                bool insideOk = NavMesh.SamplePosition(inside, out NavMeshHit insideHit, 1.0f, filter);
                var path = new NavMeshPath();
                bool pathOk = streetOk && NavMesh.CalculatePath(streetHit.position, centre, filter, path);
                report.Add($"{agentName}: floor {(instance.valid ? "added" : "NOT added")}, door link {(linkInstance.valid ? "added" : "NOT added")}, sidewalk end {(streetOk ? "on navmesh" : "off navmesh")}, arcade end {(insideOk ? "on navmesh" : "off navmesh")}, street->centre path {(pathOk ? path.status.ToString() : "none")}");
            }

            int obstacles = 0;
            foreach (BoxCollider box in room.GetComponentsInChildren<BoxCollider>(true))
            {
                if (box == null || box.isTrigger)
                    continue;
                string name = box.gameObject.name;
                if (!name.StartsWith("ARCADE_COLLIDER_", StringComparison.Ordinal) && !name.StartsWith("ArcadeTv_", StringComparison.Ordinal))
                    continue;
                var obstacle = box.gameObject.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.center = box.center;
                obstacle.size = box.size;
                obstacle.carving = true;
                obstacle.carveOnlyStationary = true;
                _created.Add(obstacle);
                obstacles++;
            }

            try { Il2CppScheduleOne.DevUtilities.NavMeshUtility.ClearCache(); }
            catch (Exception ex) { MelonLogger.Warning("Arcade NPC access: could not clear the game's navmesh sample cache: " + ex.Message); }

            Ready = _data.Count > 0 && _links.Count > 0;
            MelonLogger.Msg($"Arcade NPC access: {_data.Count} arcade floor navmesh(es) ({walls} wall cut(s)), {_links.Count} door link(s), {obstacles} carving cabinet obstacle(s).\n  " + string.Join("\n  ", report));
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("Arcade NPC access could not be set up (NPCs keep using the vanilla sidewalk only): " + ex);
            Dispose();
        }
    }

    public void Dispose()
    {
        foreach (NavMeshLinkInstance link in _links)
        {
            try { NavMesh.RemoveLink(link); } catch { }
        }
        _links.Clear();
        foreach (NavMeshDataInstance data in _data)
        {
            try { NavMesh.RemoveNavMeshData(data); } catch { }
        }
        _data.Clear();
        foreach (UnityEngine.Object o in _created)
        {
            if (o != null) UnityEngine.Object.Destroy(o);
        }
        _created.Clear();
        Ready = false;
        try { Il2CppScheduleOne.DevUtilities.NavMeshUtility.ClearCache(); } catch { }
    }
}

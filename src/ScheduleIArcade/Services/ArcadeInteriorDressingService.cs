using MelonLoader;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Re-skins the arcade room's generated surfaces with Schedule I's own loaded materials and adds
/// wall sconces that reuse the Arcade building's own light-fixture meshes. All references are
/// live runtime references to already-loaded game assets: nothing is extracted, written to disk,
/// packaged or committed. Missing names simply keep the mod's procedural materials.
///
/// Material names were taken from the installed 0.4.7f12 sharedassets1.assets (the Main scene's
/// asset file): "Carpet", "Ceiling", "Skirting", "Trim", "arcade red neon on mat",
/// "arcade white neon on mat" (the last two are the Arcade building's exterior neon).
/// </summary>
internal sealed class ArcadeInteriorDressingService : IDisposable
{
    private const string SconceSourcePath = "Map/Hyland Point/Region_Northtown/Arcade (1)/DoorWall/Curved Wall Light";

    private readonly List<UnityEngine.Object> _created = new();

    public void Apply(GameObject room, bool rawScene)
    {
        if (!rawScene)
            return;

        GameMaterialLibrary.Reset();
        Material? floor = GameMaterialLibrary.Find("Carpet", "Carpet1", "Tiles", "Floor");
        Material? ceiling = GameMaterialLibrary.Find("Ceiling");
        Material? skirting = GameMaterialLibrary.Find("Skirting", "Trim");
        Material? trim = GameMaterialLibrary.Find("Trim", "OuterTrim", "Metal_B", "Metal");
        Material? redNeon = GameMaterialLibrary.Find("arcade red neon on mat");
        Material? whiteNeon = GameMaterialLibrary.Find("arcade white neon on mat");

        var applied = new List<string>();
        int count = 0;
        Transform root = room.transform;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            string name = child.name;
            if (name == "RawArcade_CheckerboardFloor" && floor != null)
                count += Planar(child, floor) ? 1 : 0;
            else if (name == "RawArcade_SealedInteriorCeiling" && ceiling != null)
                count += Planar(child, ceiling) ? 1 : 0;
            else if (name.StartsWith("Arcade_Baseboard_", StringComparison.Ordinal) && skirting != null)
                count += Box(child, skirting) ? 1 : 0;
            else if ((name.StartsWith("Arcade_Crown_", StringComparison.Ordinal) || name.StartsWith("Arcade_CornerColumn_", StringComparison.Ordinal)) && trim != null)
                count += Box(child, trim) ? 1 : 0;
            else if (name.StartsWith("Arcade_Neon_", StringComparison.Ordinal))
            {
                bool warm = name.EndsWith("_South", StringComparison.Ordinal) || name.EndsWith("_East", StringComparison.Ordinal);
                Material? neon = warm ? redNeon : whiteNeon;
                if (neon != null)
                    count += Box(child, neon) ? 1 : 0;
            }
        }

        Transform? door = FindDeep(root, Constants.RoomDoorRootName);
        if (door != null && trim != null)
        {
            for (int i = 0; i < door.childCount; i++)
            {
                Transform piece = door.GetChild(i);
                if (piece.name.StartsWith("Arcade_DoubleDoor_Chamfer_Frame_", StringComparison.Ordinal))
                    count += Box(piece, trim) ? 1 : 0;
            }
        }

        if (floor != null) applied.Add("floor=" + floor.name);
        if (ceiling != null) applied.Add("ceiling=" + ceiling.name);
        if (skirting != null) applied.Add("skirting=" + skirting.name);
        if (trim != null) applied.Add("trim=" + trim.name);
        if (redNeon != null) applied.Add("neon=" + redNeon.name);
        int sconces = AddSconces(root);
        MelonLogger.Msg(applied.Count == 0
            ? "Arcade interior: no matching Schedule I materials are loaded; keeping the mod's own materials."
            : $"Arcade interior dressed with {count} surface(s) using live Schedule I materials ({string.Join(", ", applied)}); {sconces} wall sconce(s) reuse the Arcade building's light fixture meshes.");
    }

    private bool Planar(Transform target, Material material)
    {
        MeshFilter? filter = target.GetComponent<MeshFilter>();
        Renderer? renderer = target.GetComponent<Renderer>();
        if (filter == null || filter.sharedMesh == null || renderer == null)
            return false;
        // These meshes are the mod's own; give them metre UVs so tiled game materials keep
        // their native density.
        MetricBoxMesh.ApplyPlanarMetricUv(filter.sharedMesh);
        renderer.sharedMaterial = material;
        return true;
    }

    private bool Box(Transform target, Material material)
    {
        MeshFilter? filter = target.GetComponent<MeshFilter>();
        Renderer? renderer = target.GetComponent<Renderer>();
        if (filter == null || renderer == null)
            return false;
        Mesh mesh = MetricBoxMesh.Create(target.localScale, "ScheduleIArcade_Dressed_" + target.name);
        _created.Add(mesh);
        filter.sharedMesh = mesh;
        renderer.sharedMaterial = material;
        return true;
    }

    private int AddSconces(Transform room)
    {
        GameObject? source = GameObject.Find(SconceSourcePath);
        if (source == null)
        {
            MelonLogger.Msg("Arcade interior: the Arcade building's Curved Wall Light was not found; no fixture sconces added.");
            return 0;
        }

        Transform sourceRoot = source.transform;
        var parts = new List<(Mesh mesh, Material[] materials, Vector3 localPosition, Quaternion localRotation, Vector3 scale)>();
        Vector3 outward = Vector3.zero;
        foreach (MeshRenderer renderer in source.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (renderer == null || renderer.name.EndsWith("_LOD1", StringComparison.Ordinal))
                continue;
            MeshFilter? filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                continue;
            // level1 is statically batched: a batched renderer's MeshFilter holds a world-space
            // "Combined Mesh (root: scene) N" containing many unrelated objects. Reusing it would
            // draw a chunk of the town inside the arcade, so only unbatched meshes are reused.
            if (renderer.isPartOfStaticBatch || filter.sharedMesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal))
            {
                MelonLogger.Msg("Arcade interior: the building's wall-light mesh is statically batched in this game build, so it cannot be reused for interior sconces.");
                return 0;
            }
            Transform t = renderer.transform;
            Quaternion inverse = Quaternion.Inverse(sourceRoot.rotation);
            parts.Add((filter.sharedMesh, renderer.sharedMaterials, inverse * (t.position - sourceRoot.position), inverse * t.rotation, t.lossyScale));
            outward += new Vector3(t.position.x - sourceRoot.position.x, 0f, t.position.z - sourceRoot.position.z);
        }
        if (parts.Count == 0 || outward.sqrMagnitude < 1e-6f)
            return 0;
        outward.Normalize();

        // Room-local wall mounting points (inner wall faces) and the inward normal of each wall.
        var mounts = new (Vector3 local, Vector3 inward)[]
        {
            (new Vector3(0.75f, 2.05f, 3.98f), Vector3.back),
            (new Vector3(2.45f, 2.05f, 3.98f), Vector3.back),
            (new Vector3(-0.75f, 2.05f, -3.93f), Vector3.forward),
            (new Vector3(1.95f, 2.05f, -3.93f), Vector3.forward),
        };

        int created = 0;
        foreach (var (local, inwardLocal) in mounts)
        {
            Vector3 inward = room.TransformDirection(inwardLocal);
            float angle = Vector3.SignedAngle(outward, new Vector3(inward.x, 0f, inward.z), Vector3.up);
            var sconce = new GameObject("Arcade_Sconce_" + (created + 1));
            sconce.transform.SetParent(room, false);
            sconce.transform.position = room.TransformPoint(local) - inward * 0.11f;
            sconce.transform.rotation = Quaternion.AngleAxis(angle, Vector3.up) * sourceRoot.rotation;
            foreach (var part in parts)
            {
                var go = new GameObject("Fixture");
                go.transform.SetParent(sconce.transform, false);
                go.transform.localPosition = part.localPosition;
                go.transform.localRotation = part.localRotation;
                go.transform.localScale = part.scale;
                go.AddComponent<MeshFilter>().sharedMesh = part.mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = part.materials;
            }

            var lightGo = new GameObject("Light");
            lightGo.transform.SetParent(sconce.transform, false);
            lightGo.transform.position = room.TransformPoint(local) + inward * 0.35f;
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 3.2f;
            light.intensity = 0.9f;
            light.color = new Color(1f, 0.86f, 0.70f, 1f);
            light.shadows = LightShadows.None;
            created++;
        }
        return created;
    }

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

    public void Dispose()
    {
        foreach (UnityEngine.Object created in _created)
        {
            if (created != null)
                UnityEngine.Object.Destroy(created);
        }
        _created.Clear();
        GameMaterialLibrary.Reset();
    }
}

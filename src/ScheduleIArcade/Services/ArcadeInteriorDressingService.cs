using MelonLoader;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// 0.6.0 fittings: the prize counter (with a register) and the ceiling light fixtures. All other 0.5.0
/// furniture and decor (benches, tables, stools, vending, fridge, jukebox, signs, plants, fans, chairs,
/// bins, clock ...) is gone. Each fitting is resolved in this order and the source is logged:
///   1. an unbatched vanilla instance in the loaded scene (visual-only copy);
///   2. Resources.Load of a vanilla buildable prefab (read as a template, never instantiated);
///   3. LocalAssets (optional personal pack, absent in the public build);
///   4. a simple procedural build from live game materials / flat colours.
/// </summary>
internal sealed class ArcadeInteriorDressingService : IDisposable
{
    private readonly List<UnityEngine.Object> _created = new();

    /// <summary>Human-readable record of where each fitting came from (also logged).</summary>
    public List<string> Sources { get; } = new();

    public void Apply(GameObject room, bool rawScene)
    {
        if (!rawScene)
            return;
        VanillaPropLibrary.Reset();
        Sources.Clear();
        Transform root = room.transform;
        try { PlacePrizeCounter(root); }
        catch (Exception ex) { MelonLogger.Warning("Arcade prize counter failed: " + ex.Message); }
        try { PlaceCeilingFixtures(root); }
        catch (Exception ex) { MelonLogger.Warning("Arcade light fixtures failed: " + ex.Message); }
        MelonLogger.Msg("Arcade fittings:\n  " + string.Join("\n  ", Sources));
    }

    private void PlacePrizeCounter(Transform room)
    {
        LayoutSlot slot = ArcadeLayout.PrizeCounter;
        float top;
        if (VanillaPropLibrary.TryFind(slot.Candidates, new[] { ArcadeLayout.PrizeCounterResource }, out Transform? source, out List<MeshRenderer>? parts, out string matched, out string origin)
            && source != null && parts != null)
        {
            GameObject copy = VanillaPropLibrary.CopyVisual(source, parts, "Vanilla_prize_counter");
            _created.Add(copy);
            copy.transform.SetParent(room, false);
            Bounds b = FitIntoSlot(copy.transform, room, slot);
            AddCollider(room, "ARCADE_COLLIDER_PROP_prize_counter", slot, b);
            top = b.max.y;
            Sources.Add($"prize counter <- {origin}: {matched} ({b.size.x:0.00} x {b.size.y:0.00} x {b.size.z:0.00} m)");
        }
        else
        {
            top = BuildProceduralCounter(room, slot);
            Sources.Add("prize counter <- substitute: procedural counter (live 'Wood'/'Trim' materials when loaded)");
        }

        if (VanillaPropLibrary.TryFind(ArcadeLayout.RegisterCandidates, Array.Empty<string>(), out Transform? reg, out List<MeshRenderer>? regParts, out string regName, out string regOrigin)
            && reg != null && regParts != null)
        {
            GameObject copy = VanillaPropLibrary.CopyVisual(reg, regParts, "Vanilla_register");
            _created.Add(copy);
            copy.transform.SetParent(room, false);
            var onTop = new LayoutSlot("register", LayoutKind.Floor, slot.X, slot.Z + 0.45f, slot.Yaw, 0.45f, 0.45f, 0.40f);
            Bounds b = FitIntoSlot(copy.transform, room, onTop);
            copy.transform.localPosition += new Vector3(0f, top - b.min.y, 0f);
            Sources.Add($"register <- {regOrigin}: {regName}");
        }
        else
        {
            Sources.Add("register <- not available (no unbatched register loaded; none in Resources) - counter left bare");
        }
    }

    /// <summary>Counter body + top from live game materials (or flat colours); returns the top height.</summary>
    private float BuildProceduralCounter(Transform room, LayoutSlot slot)
    {
        Material body = GameMaterialLibrary.Find("Wood_Floor_B_Red", "Wood_Floor_A", "wood light grey") is Material wood ? wood : Flat("ScheduleIArcade_M_CounterBody", new Color(0.36f, 0.25f, 0.18f, 1f));
        Material top = GameMaterialLibrary.Find("Trim", "metal_mediumgrey_mat", "Metal") is Material trim ? trim : Flat("ScheduleIArcade_M_CounterTop", new Color(0.18f, 0.18f, 0.19f, 1f));
        const float height = 1.02f;
        var root = new GameObject("Procedural_prize_counter");
        _created.Add(root);
        root.transform.SetParent(room, false);
        root.transform.localPosition = new Vector3(slot.X, ArcadeLayout.FloorTop, slot.Z);
        Box(root.transform, "Body", new Vector3(0.02f, (height - 0.04f) * 0.5f, 0f), new Vector3(slot.SizeX - 0.08f, height - 0.04f, slot.SizeZ - 0.04f), body);
        Box(root.transform, "Kick", new Vector3(-(slot.SizeX - 0.08f) * 0.5f + 0.02f, 0.05f, 0f), new Vector3(0.04f, 0.10f, slot.SizeZ - 0.04f), top);
        Box(root.transform, "Top", new Vector3(0f, height - 0.02f, 0f), new Vector3(slot.SizeX, 0.04f, slot.SizeZ), top);
        var b = new Bounds(new Vector3(slot.X, ArcadeLayout.FloorTop + height * 0.5f, slot.Z), new Vector3(slot.SizeX, height, slot.SizeZ));
        AddCollider(room, "ARCADE_COLLIDER_PROP_prize_counter", slot, b);
        return ArcadeLayout.FloorTop + height;
    }

    private void PlaceCeilingFixtures(Transform room)
    {
        bool have = VanillaPropLibrary.TryFind(ArcadeLayout.CeilingLightCandidates, ArcadeLayout.CeilingLightResources, out Transform? source, out List<MeshRenderer>? parts, out string matched, out string origin)
            && source != null && parts != null;
        Material? panel = have ? null : Flat("ScheduleIArcade_M_LightPanel", new Color(0.92f, 0.92f, 0.88f, 1f), new Color(1.6f, 1.55f, 1.4f, 1f));
        Material? housing = have ? null : (GameMaterialLibrary.Find("metal_mediumlightgrey_mat", "Trim") ?? Flat("ScheduleIArcade_M_LightHousing", new Color(0.7f, 0.7f, 0.7f, 1f)));
        int index = 0;
        foreach (var (x, z) in ArcadeLayout.CeilingLights)
        {
            index++;
            if (have)
            {
                GameObject copy = VanillaPropLibrary.CopyVisual(source!, parts!, "Vanilla_ceiling_light_" + index);
                _created.Add(copy);
                copy.transform.SetParent(room, false);
                var slot = new LayoutSlot("light " + index, LayoutKind.Floor, x, z, 0f, 1.30f, 0.40f, 0.30f);
                Bounds b = FitIntoSlot(copy.transform, room, slot);
                copy.transform.localPosition += new Vector3(0f, ArcadeLayout.CeilingY - 0.002f - b.max.y, 0f);
            }
            else
            {
                var fixture = new GameObject("Procedural_ceiling_light_" + index);
                _created.Add(fixture);
                fixture.transform.SetParent(room, false);
                fixture.transform.localPosition = new Vector3(x, ArcadeLayout.CeilingY, z);
                Box(fixture.transform, "Housing", new Vector3(0f, -0.03f, 0f), new Vector3(1.24f, 0.06f, 0.32f), housing!);
                Box(fixture.transform, "Diffuser", new Vector3(0f, -0.065f, 0f), new Vector3(1.16f, 0.012f, 0.24f), panel!);
            }
        }
        Sources.Add(have
            ? $"{index} ceiling light fixtures <- {origin}: {matched}"
            : $"{index} ceiling light fixtures <- substitute: procedural fluorescent panels (no unbatched Fluorescent_Light; Resources lights unavailable)");
    }

    private static Bounds RoomBounds(Transform copy, Transform room)
    {
        bool any = false;
        Bounds result = default;
        foreach (Renderer renderer in copy.GetComponentsInChildren<Renderer>(true))
        {
            Bounds w = renderer.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new((i & 1) == 0 ? w.min.x : w.max.x, (i & 2) == 0 ? w.min.y : w.max.y, (i & 4) == 0 ? w.min.z : w.max.z);
                Vector3 local = room.InverseTransformPoint(corner);
                if (!any) { result = new Bounds(local, Vector3.zero); any = true; }
                else result.Encapsulate(local);
            }
        }
        return result;
    }

    /// <summary>Rotates, scales (down only) and moves a copy onto the floor inside its slot.</summary>
    private static Bounds FitIntoSlot(Transform copy, Transform room, LayoutSlot slot)
    {
        copy.localPosition = Vector3.zero;
        copy.localScale = Vector3.one;
        copy.localRotation = Quaternion.Euler(0f, slot.Yaw, 0f);
        Bounds b = RoomBounds(copy, room);
        if (Mathf.Abs(slot.SizeX - slot.SizeZ) > 0.05f && Mathf.Abs(b.size.x - b.size.z) > 0.05f && (b.size.x > b.size.z) != (slot.SizeX > slot.SizeZ))
        {
            copy.localRotation = Quaternion.Euler(0f, slot.Yaw + 90f, 0f);
            b = RoomBounds(copy, room);
        }
        float scale = Mathf.Min(1f, slot.SizeX / Mathf.Max(0.01f, b.size.x), slot.SizeZ / Mathf.Max(0.01f, b.size.z), slot.MaxHeight / Mathf.Max(0.01f, b.size.y));
        copy.localScale = Vector3.one * scale;
        b = RoomBounds(copy, room);
        copy.localPosition = new Vector3(slot.X - b.center.x, ArcadeLayout.FloorTop - b.min.y, slot.Z - b.center.z);
        return RoomBounds(copy, room);
    }

    private void AddCollider(Transform room, string name, LayoutSlot slot, Bounds b)
    {
        var go = new GameObject(name);
        _created.Add(go);
        go.transform.SetParent(room, false);
        float minX = Mathf.Max(b.min.x, slot.MinX), maxX = Mathf.Min(b.max.x, slot.MaxX);
        float minZ = Mathf.Max(b.min.z, slot.MinZ), maxZ = Mathf.Min(b.max.z, slot.MaxZ);
        float height = Mathf.Clamp(b.max.y - ArcadeLayout.FloorTop, 0.3f, 1.6f);
        var box = go.AddComponent<BoxCollider>();
        box.center = new Vector3((minX + maxX) * 0.5f, ArcadeLayout.FloorTop + height * 0.5f, (minZ + maxZ) * 0.5f);
        box.size = new Vector3(Mathf.Max(0.1f, maxX - minX), height, Mathf.Max(0.1f, maxZ - minZ));
    }

    private void Box(Transform parent, string name, Vector3 localCenter, Vector3 size, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter;
        Mesh mesh = MetricBoxMesh.Create(size, "ScheduleIArcade_Fitting_" + name);
        mesh.vertices = mesh.vertices.Select(v => Vector3.Scale(v, size)).ToArray();
        mesh.RecalculateBounds();
        _created.Add(mesh);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private Material Flat(string name, Color color, Color? emission = null)
    {
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(shader) { name = name };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.25f);
        if (emission.HasValue && m.HasProperty("_EmissionColor"))
        {
            m.SetColor("_EmissionColor", emission.Value);
            m.EnableKeyword("_EMISSION");
        }
        _created.Add(m);
        return m;
    }

    public void Tick()
    {
    }

    public void Dispose()
    {
        foreach (UnityEngine.Object created in _created)
            if (created != null) UnityEngine.Object.Destroy(created);
        _created.Clear();
        GameMaterialLibrary.Reset();
    }
}

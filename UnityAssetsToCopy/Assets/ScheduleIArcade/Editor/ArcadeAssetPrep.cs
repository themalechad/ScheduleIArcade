#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class ArcadeAssetPrep
{
    private const string Root = "Assets/ScheduleIArcade";
    private const string SourceFbx = Root + "/Source/arcade.fbx";
    private const string TextureRoot = Root + "/Textures";
    private const string GeneratedRoot = Root + "/Generated";
    private const string PrefabPath = GeneratedRoot + "/ArcadeRoom.prefab";

    // M2.36 annotated layout defaults. These match the runtime raw-scene layout so the prefab
    // opens in Unity close to what the player will see in game.
    private static readonly Vector3 PinballTarget = new Vector3(0.05f, 0f, 3.30f);
    private static readonly Vector3 ArcadeCabinetTarget = new Vector3(-2.55f, 0f, 1.65f);
    private static readonly Vector3 PacmanTarget = new Vector3(0.70f, 0f, 1.25f);
    private static readonly Vector3 DoorTarget = new Vector3(2.10f, 1.09f, 4.02f);

    [MenuItem("Schedule I Arcade/1 - Prepare Arcade Room")]
    public static void Prepare()
    {
        EnsureFolder("Assets", "ScheduleIArcade");
        EnsureFolder(Root, "Generated");

        ConfigureTextureImporters();
        var materials = BuildMaterials();

        var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourceFbx);
        if (source == null)
            throw new FileNotFoundException("Could not load imported arcade.fbx", SourceFbx);

        var root = new GameObject("ScheduleIArcade_Room");
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
        instance.name = "ArcadeScene";
        instance.transform.SetParent(root.transform, false);

        AssignMaterial(instance, "Enviorment", materials["environment"]);
        AssignMaterial(instance, "Pinball Machine", materials["pinball"]);
        AssignMaterial(instance, "Arcade Machine", materials["arcade"]);
        AssignMaterial(instance, "Table Top", materials["tabletop"]);
        AssignMaterial(instance, "Crane Game", materials["crane"]);
        AssignMaterial(instance, "Air Hockey", materials["airhockey"]);
        AssignMaterial(instance, "Vendind Machine", materials["vending"]);

        // Match the annotated runtime layout before saving the editable prefab.
        ApplyAnnotatedLayout(instance);
        HidePlaceholderMachines(instance);
        AddCollision(instance);
        CreateEditableDoubleDoor(root.transform);
        CreateMarkers(root, instance);
        CreateLayoutGuides(root.transform);

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Schedule I Arcade: generated editable {PrefabPath}. Open the prefab in Prefab Mode. Red=door, green=pinball row, orange=upright cabinets, blue=Pac-Man. Move the real objects visually, keep ARCADE_SPAWN/ARCADE_EXIT at the door, then use Schedule I Arcade > 2 - Build Windows AssetBundle.");
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
    }

    private static void ApplyAnnotatedLayout(GameObject scene)
    {
        MoveRendererCenter(scene, "Pinball Machine", PinballTarget, 0f);
        MoveRendererCenter(scene, "Arcade Machine", ArcadeCabinetTarget, 0f);
        MoveRendererCenter(scene, "Table Top", PacmanTarget, 90f);
    }

    private static void MoveRendererCenter(GameObject scene, string objectName, Vector3 desiredCenter, float yaw)
    {
        var target = FindByName(scene.transform, objectName);
        if (target == null)
        {
            Debug.LogWarning("Could not position FBX object: " + objectName);
            return;
        }

        var renderer = target.GetComponentInChildren<Renderer>(true);
        if (renderer == null)
            return;

        if (Mathf.Abs(yaw) > 0.001f)
        {
            Vector3 pivot = renderer.bounds.center;
            target.RotateAround(pivot, Vector3.up, yaw);
        }

        renderer = target.GetComponentInChildren<Renderer>(true);
        Vector3 center = renderer.bounds.center;
        Vector3 finalCenter = new Vector3(desiredCenter.x, center.y, desiredCenter.z);
        target.position += finalCenter - center;
    }

    private static void HidePlaceholderMachines(GameObject scene)
    {
        var crane = FindByName(scene.transform, "Crane Game");
        if (crane != null) crane.gameObject.SetActive(false);
        var vending = FindByName(scene.transform, "Vendind Machine");
        if (vending != null) vending.gameObject.SetActive(false);
    }

    private static void CreateEditableDoubleDoor(Transform root)
    {
        var doorRoot = new GameObject("ARCADE_DOUBLE_DOOR_EDIT_ME");
        doorRoot.transform.SetParent(root, false);
        doorRoot.transform.localPosition = DoorTarget;

        var frameMat = CreateGuideMaterial("M_EditableDoorFrame", new Color(0.17f, 0.19f, 0.23f, 1f));
        var doorMat = CreateGuideMaterial("M_EditableDoor", new Color(0.07f, 0.09f, 0.13f, 1f));
        var glassMat = CreateGuideMaterial("M_EditableDoorGlass", new Color(0.08f, 0.25f, 0.38f, 1f));

        const float totalWidth = 1.90f;
        const float height = 2.18f;
        const float gap = 0.035f;
        float leaf = (totalWidth - gap) * 0.5f;

        CreateDoorCube("Left", doorRoot.transform, new Vector3(-(leaf + gap) * 0.5f, 0f, 0f), new Vector3(leaf, height, 0.09f), doorMat);
        CreateDoorCube("Right", doorRoot.transform, new Vector3((leaf + gap) * 0.5f, 0f, 0f), new Vector3(leaf, height, 0.09f), doorMat);
        CreateDoorCube("Left_Window", doorRoot.transform, new Vector3(-(leaf + gap) * 0.5f, 0.38f, -0.055f), new Vector3(leaf * 0.62f, 0.72f, 0.018f), glassMat, false);
        CreateDoorCube("Right_Window", doorRoot.transform, new Vector3((leaf + gap) * 0.5f, 0.38f, -0.055f), new Vector3(leaf * 0.62f, 0.72f, 0.018f), glassMat, false);
        CreateDoorCube("Frame_Left", doorRoot.transform, new Vector3(-totalWidth * 0.5f - 0.055f, 0f, 0f), new Vector3(0.11f, height + 0.15f, 0.14f), frameMat, false);
        CreateDoorCube("Frame_Right", doorRoot.transform, new Vector3(totalWidth * 0.5f + 0.055f, 0f, 0f), new Vector3(0.11f, height + 0.15f, 0.14f), frameMat, false);
        CreateDoorCube("Frame_Top", doorRoot.transform, new Vector3(0f, height * 0.5f + 0.075f, 0f), new Vector3(totalWidth + 0.22f, 0.15f, 0.14f), frameMat, false);
    }

    private static void CreateDoorCube(string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material material, bool collider = true)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = localScale;
        go.GetComponent<Renderer>().sharedMaterial = material;
        if (!collider)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }
    }

    private static void CreateLayoutGuides(Transform root)
    {
        var guides = new GameObject("LAYOUT_GUIDES_EDITOR_ONLY");
        guides.tag = "EditorOnly";
        guides.hideFlags = HideFlags.DontSaveInBuild;
        guides.transform.SetParent(root, false);

        CreateGuide(guides.transform, "RED_DOOR_TARGET", DoorTarget + new Vector3(0f, 0f, -0.12f), new Vector3(2.15f, 2.45f, 0.05f), Color.red, "RED - DOOR");
        CreateGuide(guides.transform, "GREEN_PINBALL_ROW", new Vector3(PinballTarget.x, 0.06f, PinballTarget.z), new Vector3(2.15f, 0.10f, 1.45f), Color.green, "GREEN - PINBALL");
        CreateGuide(guides.transform, "ORANGE_ARCADE_CABINETS", new Vector3(ArcadeCabinetTarget.x, 0.95f, ArcadeCabinetTarget.z), new Vector3(1.25f, 1.90f, 1.10f), new Color(1f, 0.45f, 0f, 1f), "ORANGE - CABINETS");
        CreateGuide(guides.transform, "BLUE_PACMAN", new Vector3(PacmanTarget.x, 0.06f, PacmanTarget.z), new Vector3(1.20f, 0.10f, 2.60f), Color.blue, "BLUE - PAC-MAN");
    }

    private static void CreateGuide(Transform parent, string name, Vector3 position, Vector3 scale, Color color, string label)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.tag = "EditorOnly";
        go.hideFlags = HideFlags.DontSaveInBuild;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localScale = scale;
        var collider = go.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
        go.GetComponent<Renderer>().sharedMaterial = CreateGuideMaterial("Guide_" + name, color);

        var text = new GameObject("LABEL_" + label);
        text.tag = "EditorOnly";
        text.hideFlags = HideFlags.DontSaveInBuild;
        text.transform.SetParent(go.transform, false);
        text.transform.localPosition = Vector3.up * 0.75f;
        text.transform.localScale = Vector3.one * 0.35f;
        var tm = text.AddComponent<TextMesh>();
        tm.text = label;
        tm.characterSize = 0.25f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.color = color;
    }

    private static Material CreateGuideMaterial(string name, Color color)
    {
        string path = GeneratedRoot + "/" + name + ".mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
        var mat = existing != null ? existing : new Material(shader) { name = name };
        mat.shader = shader;
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (existing == null) AssetDatabase.CreateAsset(mat, path); else EditorUtility.SetDirty(mat);
        return mat;
    }

    private static Dictionary<string, Material> BuildMaterials()
    {
        return new Dictionary<string, Material>
        {
            ["environment"] = CreateMaterial("M_Environment", "Enviorment_Texture.jpg", "Enviorment_Texture_NRM.jpg", null, null, true),
            ["pinball"] = CreateMaterial("M_Pinball", "Pinball_base_texture.jpg", "Pinball_base_texture_NRM.jpg", "Pinball_base_texture_specular_map.jpg", "Pinball_base_texture_emissive_map.jpg", true),
            ["arcade"] = CreateMaterial("M_ArcadeCabinet", "Arcade_base_texture.jpg", "Arcade_base_texture_NRM.jpg", "Arcade_base_texture_spec_map.jpg", "Arcade_base_texture_emission_map.jpg", true),
            ["tabletop"] = CreateMaterial("M_TableTop", "tabletop_base_texture.jpg", "tabletop_base_texture_NRM.jpg", "tabletop_base_texture_spec_map.jpg", "tabletop_base_texture_emissive_map.jpg", true),
            ["crane"] = CreateMaterial("M_Crane", "crane_game_base_color.jpg", null, "crane_game_base_color_spec_map.jpg", null, true),
            ["airhockey"] = CreateMaterial("M_AirHockey", "arcade_base_texture.jpg", "arcade_base_texture_for_normal_NRM.jpg", "arcade_base_texture_spec_map.jpg", "arcade_base_texture_emmision_map.jpg", true),
            ["vending"] = CreateMaterial("M_Vending", "Vending_machine_texture.jpg", "Vending_machine_texture_for_normal_map_NRM.jpg", "Vending_machine_texture_spec_map_.jpg", null, true),
        };
    }

    private static Material CreateMaterial(string name, string albedo, string normal, string specular, string emission, bool doubleSided)
    {
        string path = GeneratedRoot + "/" + name + ".mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null)
            throw new InvalidOperationException("Could not find a usable Lit/Standard shader.");

        var mat = existing != null ? existing : new Material(shader) { name = name };
        mat.shader = shader;
        mat.renderQueue = (int)RenderQueue.Geometry;
        mat.SetOverrideTag("RenderType", "Opaque");
        mat.doubleSidedGI = doubleSided;

        SetTexture(mat, "_BaseMap", "_MainTex", albedo);
        if (!string.IsNullOrEmpty(normal))
        {
            SetTexture(mat, "_BumpMap", "_BumpMap", normal);
            mat.EnableKeyword("_NORMALMAP");
        }
        if (!string.IsNullOrEmpty(specular))
            SetTexture(mat, "_SpecGlossMap", "_SpecGlossMap", specular);
        if (!string.IsNullOrEmpty(emission))
        {
            SetTexture(mat, "_EmissionMap", "_EmissionMap", emission);
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.white);
            mat.EnableKeyword("_EMISSION");
        }

        // URP _Cull / Standard _Cull: Off = 0. This keeps downloaded single-sided room faces visible
        // from both directions. Collision is still physical; later we can thicken the walls in Blender.
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)CullMode.Off);
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f); // opaque in URP
        if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 0f);     // opaque in Standard

        if (existing == null)
            AssetDatabase.CreateAsset(mat, path);
        else
            EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void SetTexture(Material mat, string primaryProperty, string fallbackProperty, string file)
    {
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TextureRoot + "/" + file);
        if (texture == null)
        {
            Debug.LogWarning("Missing texture " + file);
            return;
        }
        if (mat.HasProperty(primaryProperty)) mat.SetTexture(primaryProperty, texture);
        if (fallbackProperty != primaryProperty && mat.HasProperty(fallbackProperty)) mat.SetTexture(fallbackProperty, texture);
    }

    private static void AssignMaterial(GameObject scene, string objectName, Material material)
    {
        var target = FindByName(scene.transform, objectName);
        if (target == null)
        {
            Debug.LogWarning("Could not find FBX object: " + objectName);
            return;
        }
        foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
        {
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = material;
            renderer.sharedMaterials = mats;
        }
    }

    private static void AddCollision(GameObject scene)
    {
        foreach (var filter in scene.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null || filter.GetComponent<Collider>() != null)
                continue;

            bool environment = filter.name.IndexOf("Enviorment", StringComparison.OrdinalIgnoreCase) >= 0;
            if (environment)
            {
                var mc = filter.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = filter.sharedMesh;
                mc.convex = false;
            }
            else
            {
                var box = filter.gameObject.AddComponent<BoxCollider>();
                box.center = filter.sharedMesh.bounds.center;
                box.size = filter.sharedMesh.bounds.size;
            }
        }
    }

    private static void CreateMarkers(GameObject root, GameObject scene)
    {
        // These are intentionally easy-to-find prefab markers rather than hard-coded world
        // coordinates. Move both markers onto the custom room's real entrance before building.
        // ARCADE_SPAWN should sit slightly inside the room; ARCADE_EXIT should sit at the interior
        // side of the visible exit door so walking to that door produces the exit prompt.
        var spawn = new GameObject("ARCADE_SPAWN");
        spawn.transform.SetParent(root.transform, false);
        spawn.transform.localPosition = DoorTarget + new Vector3(0f, -1.01f, -0.95f);

        var exit = new GameObject("ARCADE_EXIT");
        exit.transform.SetParent(root.transform, false);
        exit.transform.localPosition = DoorTarget + new Vector3(0f, -0.14f, -0.48f);

        var cabinet = FindByName(scene.transform, "Arcade Machine");
        if (cabinet == null)
            return;

        cabinet.name = "ARCADE_CABINET_01";
        var renderer = cabinet.GetComponentInChildren<Renderer>();
        if (renderer == null)
            return;

        Bounds b = renderer.bounds;
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "ARCADE_SCREEN_01";
        screen.transform.SetParent(cabinet, true);

        // First-pass placement only. Open the prefab and move/rotate/scale this quad onto the real screen.
        screen.transform.position = b.center + cabinet.forward * (b.extents.z + 0.01f) + Vector3.up * (b.extents.y * 0.25f);
        screen.transform.rotation = Quaternion.LookRotation(cabinet.forward, Vector3.up);
        screen.transform.localScale = new Vector3(Mathf.Max(0.3f, b.extents.x), Mathf.Max(0.2f, b.extents.y * 0.45f), 1f);

        var screenCollider = screen.GetComponent<Collider>();
        if (screenCollider != null) UnityEngine.Object.DestroyImmediate(screenCollider);

        var screenMat = CreateMaterial("M_ArcadeScreenRuntime", "Bright_map.jpg", null, null, "Bright_map.jpg", false);
        screen.GetComponent<Renderer>().sharedMaterial = screenMat;
    }

    private static Transform FindByName(Transform root, string name)
    {
        if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return root;
        foreach (Transform child in root)
        {
            var result = FindByName(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private static void ConfigureTextureImporters()
    {
        foreach (string path in Directory.GetFiles(TextureRoot, "*.*", SearchOption.TopDirectoryOnly))
        {
            string unityPath = path.Replace('\\', '/');
            if (!(AssetImporter.GetAtPath(unityPath) is TextureImporter importer)) continue;

            string lower = Path.GetFileName(unityPath).ToLowerInvariant();
            importer.textureType = lower.Contains("nrm") || lower.Contains("normal")
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }
    }

    private static void EnsureFolder(string parent, string child)
    {
        string full = parent + "/" + child;
        if (!AssetDatabase.IsValidFolder(full)) AssetDatabase.CreateFolder(parent, child);
    }
}
#endif

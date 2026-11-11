using ScheduleIArcade.Utils;

namespace ScheduleIArcade.Tests;

internal static class LocalAssetTests
{
    public static void ParsesSyntheticGlbTriangle()
    {
        byte[] glb = GlbMeshParser.WriteTriangleGlb();
        Assert.True(glb.Length >= 20, "synthetic GLB should have a header");
        Assert.True(glb[0] == (byte)'g' && glb[1] == (byte)'l' && glb[2] == (byte)'T' && glb[3] == (byte)'F', "GLB magic");
        Assert.True(GlbMeshParser.TryParse(glb, out ParsedGlbMesh mesh, out string error), "parse failed: " + error);
        Assert.True(mesh.VertexCount == 3, "expected 3 vertices, got " + mesh.VertexCount);
        Assert.True(mesh.Indices.Length == 3, "expected 3 indices, got " + mesh.Indices.Length);
        Assert.True(mesh.Normals != null && mesh.Normals.Length == 9, "expected 3 normals");
        Assert.True(mesh.Uvs != null && mesh.Uvs.Length == 6, "expected 3 uvs");
        Assert.True(Near(mesh.Positions[0], 0f) && Near(mesh.Positions[1], 0f) && Near(mesh.Positions[2], 0f), "v0");
        Assert.True(Near(mesh.Positions[3], 1f) && Near(mesh.Positions[4], 0f) && Near(mesh.Positions[5], 0f), "v1");
        Assert.True(Near(mesh.Positions[6], 0f) && Near(mesh.Positions[7], 1f) && Near(mesh.Positions[8], 0f), "v2");
        Assert.True(mesh.Indices[0] == 0 && mesh.Indices[1] == 2 && mesh.Indices[2] == 1, "winding should be flipped");
        Assert.True(Near(mesh.Normals![2], -1f), "normal Z should be flipped to -1");
    }

    public static void RejectsGarbageBytes()
    {
        Assert.True(!GlbMeshParser.TryParse(new byte[] { 1, 2, 3, 4 }, out _, out string error), "tiny garbage should fail");
        Assert.True(!string.IsNullOrWhiteSpace(error), "error should be reported");
        Assert.True(!GlbMeshParser.TryParse(System.Text.Encoding.ASCII.GetBytes("this is not a glb file at all!!!!!!!!"), out _, out _), "ascii garbage should fail");
    }

    public static void ParsesManifestAndLooksUpNames()
    {
        const string json = @"{
          ""version"": ""0.5.0"",
          ""notice"": ""personal pack, do not redistribute"",
          ""entries"": [
            {""kind"": ""texture"", ""names"": [""Ceiling"", ""ceiling_0""], ""file"": ""textures/Ceiling1_Albedo.png""},
            {""kind"": ""texture"", ""names"": [""window_glass_material"", ""Glass""], ""file"": ""textures/unused.png"", ""transparent"": true},
            {""kind"": ""mesh"", ""names"": [""Counter"", ""Ornate Desk""], ""file"": ""meshes/Counter.glb"", ""texture"": ""unused""},
            {""kind"": ""mesh"", ""file"": ""meshes/MissingNames.glb""}
          ]
        }";
        Assert.True(LocalAssetManifest.TryParse(json, out LocalAssetManifest manifest, out string error), "manifest parse failed: " + error);
        Assert.True(manifest.Version == "0.5.0", "version");
        Assert.True(manifest.Entries.Count == 3, "the entry with no names must be skipped, got " + manifest.Entries.Count);
        Assert.True(manifest.Find("texture", "ceiling_0") != null, "alias ceiling_0");
        Assert.True(manifest.Find("texture", "Ceiling")!.File.EndsWith("Ceiling1_Albedo.png", StringComparison.Ordinal), "ceiling file");
        Assert.True(manifest.Find("texture", "Glass")!.Transparent, "glass is transparent");
        Assert.True(manifest.FindAny("mesh", new[] { "Frontdesk", "Counter" })!.File.Contains("Counter.glb"), "mesh lookup");
        Assert.True(manifest.Find("mesh", "MissingNames") == null, "nameless entry skipped");
    }

    public static void ManifestIsGracefulOnBadJson()
    {
        bool ok = LocalAssetManifest.TryParse("{", out _, out string error);
        Assert.True(!ok, "truncated JSON must fail");
        Assert.True(!string.IsNullOrWhiteSpace(error), "error reported");
        ok = LocalAssetManifest.TryParse("{\"entries\":[]}", out LocalAssetManifest empty, out error);
        Assert.True(ok && empty.Entries.Count == 0, "empty entries is a valid pack");
    }

    public static void RepoHasNoRippedLocalAssets()
    {
        string? repo = FindRepoRoot();
        if (repo == null)
        {
            Console.WriteLine("      (skip repo scan: not running from the ScheduleIArcade repo)");
            return;
        }
        var bannedNames = new[]
        {
            "Plaster1_BW_Albedo.png", "Doors_albedo.png", "grocerystore_DoorFrame_AlbedoTransparency.png",
            "Ceiling1_Albedo.png", "Fan_Base_color.png", "Tiles2_basecolor.png", "Wood_Floor_A.png",
            "TrashCan_Built.glb", "Light_Fluorescent_Light_LOD0.glb", "Chair_Business.glb"
        };
        string[] rootsToScan =
        {
            Path.Combine(repo, "src"),
            Path.Combine(repo, "tests"),
            Path.Combine(repo, "Tools"),
            Path.Combine(repo, "dist"),
            Path.Combine(repo, "docs")
        };
        var hits = new List<string>();
        foreach (string root in rootsToScan)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(repo, file).Replace('\\', '/');
                if (rel.Contains("/LocalAssets/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("LocalAssets/", StringComparison.OrdinalIgnoreCase))
                    hits.Add(rel);
                string name = Path.GetFileName(file);
                foreach (string banned in bannedNames)
                {
                    if (string.Equals(name, banned, StringComparison.OrdinalIgnoreCase))
                        hits.Add(rel);
                }
            }
        }
        if (Directory.Exists(Path.Combine(repo, "LocalAssets")))
            hits.Add("LocalAssets/");
        Assert.True(hits.Count == 0, "ripped or LocalAssets files in the public tree: " + string.Join(", ", hits));
    }

    private static string? FindRepoRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6; i++)
        {
            if (File.Exists(Path.Combine(dir, "Tools", "Build-And-Install.ps1"))
                && File.Exists(Path.Combine(dir, "src", "ScheduleIArcade", "Utils", "Constants.cs")))
                return dir;
            string? parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-5f;
}

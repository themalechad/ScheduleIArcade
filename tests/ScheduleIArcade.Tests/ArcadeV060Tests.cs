using System.Text;
using ScheduleIArcade.Utils;

namespace ScheduleIArcade.Tests;

/// <summary>0.6.0: whole-building layout, cabinet repaint, procedural surfaces, Doom CRT fit.</summary>
internal static class ArcadeV060Tests
{
    public static void LayoutFillsTheBuilding()
    {
        // Inner faces sit 0.27-0.33 m inside the vanilla outer faces (one solid wall layer, no cavity).
        var (wWest, _) = ArcadeLayout.ToWorld(ArcadeLayout.InnerEast, 0f);   // +X local = world west
        var (wEast, _) = ArcadeLayout.ToWorld(ArcadeLayout.InnerWest, 0f);
        var (_, wNorth) = ArcadeLayout.ToWorld(0f, ArcadeLayout.InnerSouth);
        var (_, wSouth) = ArcadeLayout.ToWorld(0f, ArcadeLayout.InnerNorth);
        float[] wall = { wWest - (-52.50f), -43.50f - wEast, 147.50f - wNorth, wSouth - 135.50f };
        foreach (float t in wall)
            Assert.True(t >= 0.25f && t <= 0.335f, $"wall thickness {t:0.000} m outside 0.25..0.335");
        // Ceiling at the first-floor line, floor level with the sidewalk.
        Assert.True(MathF.Abs(ArcadeLayout.WorldOriginY + ArcadeLayout.FloorTop - (-3.90f)) < 1e-4f, "floor must be the foundation/sidewalk height");
        Assert.True(MathF.Abs(ArcadeLayout.WorldOriginY + ArcadeLayout.CeilingTopY - (-0.40f)) < 1e-4f, "ceiling slab top must be the first-floor line");
        Assert.True(ArcadeLayout.WorldOriginY + ArcadeLayout.CeilingTopY < ArcadeLayout.RoofWorldY - 1f, "ceiling far below the roof");
        Assert.True(ArcadeLayout.IsInsideInterior(0f, 0f) && !ArcadeLayout.IsInsideInterior(-4.0f, 3.5f), "chamfer corner is outside");
    }

    public static void WallsAreClosedAndOpeningsInRange()
    {
        // Consecutive walls meet (the outline closes) and openings lie within their wall.
        var walls = ArcadeLayout.Walls;
        Assert.True(walls.Length == 5, "five wall linings");
        for (int i = 0; i < walls.Length; i++)
        {
            var a = walls[i];
            var b = walls[(i + 1) % walls.Length];
            float gap = MathF.Sqrt((a.BX - b.AX) * (a.BX - b.AX) + (a.BZ - b.AZ) * (a.BZ - b.AZ));
            Assert.True(gap < 1e-3f, $"{a.Id} -> {b.Id} leaves a {gap:0.000} m gap");
            foreach (var o in a.Openings)
                Assert.True(o.AlongMin >= -1e-4f && o.AlongMax <= a.Length + 1e-4f && o.AlongMax > o.AlongMin && o.Top > o.Bottom, $"{a.Id} opening {o.Kind} out of range");
        }
        int doors = walls.Sum(w => w.Openings.Count(o => o.Kind == "door"));
        Assert.True(doors == 1, "exactly one door opening, got " + doors);
        int windows = walls.Sum(w => w.Openings.Count(o => o.Kind == "window"));
        Assert.True(windows == 5, "five ground-floor windows (three east, two south), got " + windows);
    }

    public static void CabinetPaintIsFlatAndCovered()
    {
        // A unit box: every triangle gets a part, UVs stay inside its atlas cell.
        float[] v = { 0,0,0, 1,0,0, 1,1,0, 0,1,0, 0,0,1, 1,0,1, 1,1,1, 0,1,1 };
        int[] t = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,6,2, 3,7,6, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
        foreach (CabinetKind kind in Enum.GetValues(typeof(CabinetKind)))
        {
            PaintPart[] parts = CabinetPaint.Classify(kind, v, t);
            Assert.True(parts.Length == 12, "one part per triangle");
            var (pos, src, uv, tri) = CabinetPaint.Unshare(v, t, parts);
            Assert.True(tri.Length == 36 && pos.Length == 108 && src.Length == 36, "unshared geometry");
            for (int k = 0; k < 12; k++)
            {
                var (col, row) = CabinetPaint.Cell(parts[k]);
                for (int j = 0; j < 3; j++)
                {
                    float u = uv[(k * 3 + j) * 2] * CabinetPaint.AtlasCells - col, w = uv[(k * 3 + j) * 2 + 1] * CabinetPaint.AtlasCells - row;
                    Assert.True(u > 0f && u < 1f && w > 0f && w < 1f, "uv leaves its paint cell");
                }
            }
        }
        // Soft palette: no saturated neon in any scheme.
        foreach (string model in new[] { "Arcade Machine 01", "Arcade Machine 02", "Pinball Machine 01", "Table Top", "Air Hockey" })
        {
            var scheme = CabinetPaint.SchemeFor(model);
            foreach (var c in scheme.Colors.Values)
            {
                int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
                float saturation = max == 0 ? 0f : (max - min) / (float)max;
                Assert.True(!(saturation > 0.75f && max > 200), $"{model}: neon colour {c}");
            }
            byte[] atlas = CabinetPaint.BuildAtlas(scheme, 7);
            Assert.True(atlas.Length == CabinetPaint.AtlasPixels * CabinetPaint.AtlasPixels * 4, "atlas size");
        }
    }

    public static void SurfacePatternsAreSubdued()
    {
        foreach (SurfacePatterns.Kind kind in Enum.GetValues(typeof(SurfacePatterns.Kind)))
        {
            byte[] px = SurfacePatterns.Generate(kind);
            Assert.True(px.Length == SurfacePatterns.Size * SurfacePatterns.Size * 4, "size");
            double sum = 0; int maxSat = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                sum += (px[i] + px[i + 1] + px[i + 2]) / 3.0;
                int max = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])), min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                maxSat = Math.Max(maxSat, max - min);
                Assert.True(px[i + 3] == 255, "opaque");
            }
            double mean = sum / (px.Length / 4);
            Console.WriteLine($"      {kind}: mean brightness {mean:0}, max channel spread {maxSat}");
            if (kind == SurfacePatterns.Kind.Carpet || kind == SurfacePatterns.Kind.Wallpaper)
                Assert.True(mean < 90, kind + " should be dark");
            else
                Assert.True(mean > 150, "ceiling tiles should be light");
            Assert.True(maxSat < 80, kind + " must not be neon");
        }
    }

    public static void DoomPictureFillsTheMeasuredCrt()
    {
        string? bin = FindPackage();
        if (bin == null)
        {
            Console.WriteLine("      (skip: packaged mesh not found)");
            return;
        }
        foreach (var (name, front) in new[] { ("Arcade Machine 01", -1f), ("Arcade Machine 02", 1f) })
        {
            var (v, t) = ReadModel(bin, name);
            CrtOpening.Fit fit = CrtOpening.Measure(v, t, front);
            Console.WriteLine($"      {name}: opening u {fit.UMin:0.000}..{fit.UMax:0.000} v {fit.VMin:0.000}..{fit.VMax:0.000}, glass depth {fit.GlassDepth:0.000}; picture {fit.Width:0.000} x {fit.Height:0.000} m (measured={fit.Measured})");
            Assert.True(fit.Measured, "the opening must be measured on the mesh");
            Assert.True(MathF.Abs(fit.Width / fit.Height - 4f / 3f) < 1e-3f, "4:3 picture");
            Assert.True(fit.Width >= (fit.UMax - fit.UMin) - 0.01f || fit.Height >= (fit.VMax - fit.VMin) - 0.01f, "picture fills the opening in one axis");
            Assert.True(fit.Width <= fit.UMax - fit.UMin && fit.Height <= fit.VMax - fit.VMin, "picture inside the opening");
            Assert.True(fit.Width > 0.35f * 0.95f, "much bigger than the old hard-coded picture? width " + fit.Width);
            // Nothing painted over the opening: every triangle inside the opening is Glass or Bezel.
            PaintPart[] parts = CabinetPaint.Classify(CabinetKind.Upright, v, t, front);
            var (n, up) = CrtOpening.Basis(front);
            for (int k = 0; k < parts.Length; k++)
            {
                int a = t[k * 3] * 3, b = t[k * 3 + 1] * 3, c = t[k * 3 + 2] * 3;
                float cu = (v[a + 2] + v[b + 2] + v[c + 2]) / 3f;
                float cv = ((v[a] + v[b] + v[c]) * up.x + (v[a + 1] + v[b + 1] + v[c + 1]) * up.y) / 3f;
                float cd = ((v[a] + v[b] + v[c]) * n.x + (v[a + 1] + v[b + 1] + v[c + 1]) * n.y) / 3f;
                bool inOpening = cu > fit.CentreU - fit.Width / 2 && cu < fit.CentreU + fit.Width / 2 && cv > fit.CentreV - fit.Height / 2 && cv < fit.CentreV + fit.Height / 2 && cd > fit.GlassDepth - 0.03f;
                if (inOpening)
                    Assert.True(parts[k] is PaintPart.Glass or PaintPart.Bezel, $"{name}: triangle {k} inside the CRT opening is painted {parts[k]}");
            }
        }
    }

    private static string? FindPackage()
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6; i++)
        {
            string p = Path.Combine(dir, "dist", "Mods", "ScheduleIArcade", "Assets", "RawScene", "arcade_runtime_meshes.bin");
            if (File.Exists(p)) return p;
            string? parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static (float[] v, int[] t) ReadModel(string path, string wanted)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(8);
        int version = r.ReadInt32(), count = r.ReadInt32();
        string S() { int n = r.ReadInt32(); return Encoding.UTF8.GetString(r.ReadBytes(n)); }
        for (int m = 0; m < count; m++)
        {
            string name = S(); S(); S(); S();
            if (version >= 2) r.ReadBytes(16);
            int vc = r.ReadInt32();
            var v = new float[vc * 3];
            for (int i = 0; i < v.Length; i++) v[i] = r.ReadSingle();
            r.ReadBytes(vc * 12 + vc * 8);
            int ic = r.ReadInt32();
            var t = new int[ic];
            for (int i = 0; i < ic; i++) t[i] = r.ReadInt32();
            if (name == wanted) return (v, t);
        }
        throw new Exception(wanted + " not in package");
    }
}

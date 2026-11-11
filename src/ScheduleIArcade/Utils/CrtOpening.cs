namespace ScheduleIArcade.Utils;

/// <summary>
/// Measures the CRT opening of the upright (Doom) cabinet model and fits a classic 4:3 picture into
/// it. The engine renders 640 x 400 (8:5); original CRTs displayed that as 4:3, so the quad is 4:3.
///
/// The opening is the recessed screen panel of the model: the tilted screen-face plane (normal
/// N = (front * 0.937, 0.348, 0)) carries a recessed glass rectangle flanked by two angled bevel
/// wings (normal ~ (front * 0.91, 0.34, +-0.25)). The opening is the bounding rectangle of the glass
/// and the wings measured in the plane (u = model Z across, v = up the tilted face). Unity-free.
/// </summary>
internal static class CrtOpening
{
    public readonly record struct Fit(
        float UMin, float UMax, float VMin, float VMax, float GlassDepth,
        float CentreU, float CentreV, float Width, float Height, bool Measured);

    public const float Aspect = 4f / 3f;
    /// <summary>Picture inset from the opening rim on each side (metres).</summary>
    public const float Margin = 0.004f;

    /// <summary>Screen-face basis for a cabinet whose screen faces model X sign <paramref name="front"/>.</summary>
    public static ((float x, float y, float z) n, (float x, float y, float z) up) Basis(float front)
    {
        // Measured face normal (+-0.937, 0.348, 0): the face tilts back by ~20 degrees.
        (float x, float y, float z) n = (front * 0.937f, 0.348f, 0f);
        (float x, float y, float z) up = (-front * 0.348f, 0.937f, 0f);
        return (n, up);
    }

    /// <summary>Fallback when the mesh cannot be measured: the 0.4.7f12 package's opening.</summary>
    public static Fit Default() => FitInside(-0.175f, 0.175f, 0.067f, 0.404f, 0.166f, measured: false);

    public static Fit Measure(float[] v, int[] t, float front)
    {
        var (n, up) = Basis(front);
        float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
        float glassDepth = float.NaN, glassArea = 0f;
        int wings = 0;
        for (int k = 0; k + 2 < t.Length; k += 3)
        {
            int a = t[k] * 3, b = t[k + 1] * 3, c = t[k + 2] * 3;
            float e1x = v[b] - v[a], e1y = v[b + 1] - v[a + 1], e1z = v[b + 2] - v[a + 2];
            float e2x = v[c] - v[a], e2y = v[c + 1] - v[a + 1], e2z = v[c + 2] - v[a + 2];
            float nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12f) continue;
            nx /= len; ny /= len; nz /= len;
            float dotN = nx * n.x + ny * n.y + nz * n.z;
            bool glass = dotN > 0.995f;
            bool wing = !glass && dotN > 0.94f && MathF.Abs(nz) > 0.15f && MathF.Abs(nz) < 0.4f;
            if (!glass && !wing) continue;
            float tu0 = float.MaxValue, tu1 = float.MinValue, tv0 = float.MaxValue, tv1 = float.MinValue, depth = 0f;
            foreach (int i in new[] { a, b, c })
            {
                float u = v[i + 2];
                float vv = v[i] * up.x + v[i + 1] * up.y + v[i + 2] * up.z;
                tu0 = MathF.Min(tu0, u); tu1 = MathF.Max(tu1, u);
                tv0 = MathF.Min(tv0, vv); tv1 = MathF.Max(tv1, vv);
                depth += (v[i] * n.x + v[i + 1] * n.y + v[i + 2] * n.z) / 3f;
            }
            if (glass)
            {
                // Only the recessed glass inside the panel (|u| < 0.2 and below the marquee).
                if (MathF.Abs(tu0) > 0.2f || MathF.Abs(tu1) > 0.2f || tv1 > 0.45f || tv0 < 0.02f)
                    continue;
                float area = len * 0.5f;
                if (float.IsNaN(glassDepth) || area > glassArea) { glassDepth = depth; glassArea = area; }
            }
            else
            {
                // Only the tall bevel wings beside the glass (skip tiny bevels on the control buttons).
                if (tv1 - tv0 < 0.1f || tv0 < 0.02f)
                    continue;
                wings++;
            }
            uMin = MathF.Min(uMin, tu0); uMax = MathF.Max(uMax, tu1);
            vMin = MathF.Min(vMin, tv0); vMax = MathF.Max(vMax, tv1);
        }
        if (wings < 2 || float.IsNaN(glassDepth) || uMax - uMin < 0.15f || vMax - vMin < 0.15f)
            return Default();
        return FitInside(uMin, uMax, vMin, vMax, glassDepth, measured: true);
    }

    /// <summary>Largest centred 4:3 rectangle inside the opening, minus <see cref="Margin"/>.</summary>
    public static Fit FitInside(float uMin, float uMax, float vMin, float vMax, float glassDepth, bool measured)
    {
        float availW = uMax - uMin - 2f * Margin, availH = vMax - vMin - 2f * Margin;
        float width = MathF.Min(availW, availH * Aspect);
        float height = width / Aspect;
        return new Fit(uMin, uMax, vMin, vMax, glassDepth, (uMin + uMax) * 0.5f, (vMin + vMax) * 0.5f, width, height, measured);
    }
}

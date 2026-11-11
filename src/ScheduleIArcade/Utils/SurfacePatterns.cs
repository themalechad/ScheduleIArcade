namespace ScheduleIArcade.Utils;

/// <summary>
/// In-code fallback textures for the arcade's interior surfaces, used only when the matching live
/// Schedule I material (Carpet / Wallpaper_B* / Ceiling) is not loaded. Flat, softly shaded and
/// lightly grimed like the game's own surfaces; no neon, no checkerboard. Unity-free RGBA32 bytes
/// (row 0 = bottom) so they are unit-tested headlessly. Each texture tiles seamlessly and is meant
/// to cover <see cref="MetresPerTile"/> metres per repeat with metric UVs.
/// </summary>
internal static class SurfacePatterns
{
    public const int Size = 128;

    public enum Kind
    {
        /// <summary>Dark low-pile commercial carpet: muted burgundy-brown with a small speckle.</summary>
        Carpet,
        /// <summary>Dark wallpaper: deep blue-grey with faint vertical stripes.</summary>
        Wallpaper,
        /// <summary>Off-white acoustic ceiling tiles with grey grid channels (2 x 2 tiles per texture).</summary>
        CeilingTiles,
    }

    public static float MetresPerTile(Kind kind) => kind switch
    {
        Kind.Carpet => 1.0f,
        Kind.Wallpaper => 1.0f,
        _ => 1.2f, // two 0.6 m ceiling tiles
    };

    public static (byte r, byte g, byte b) BaseColor(Kind kind) => kind switch
    {
        Kind.Carpet => (74, 40, 40),
        Kind.Wallpaper => (44, 48, 60),
        _ => (196, 194, 186),
    };

    public static byte[] Generate(Kind kind, int seed = 1)
    {
        var px = new byte[Size * Size * 4];
        var (br, bg, bb) = BaseColor(kind);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float k = 1f;
                // Seamless low-frequency mottling (periodic in both axes).
                float mottle = 0.5f * (MathF.Sin(2f * MathF.PI * (x / (float)Size * 2f + Noise(seed, x / 32, y / 32) * 0.2f))
                                     + MathF.Sin(2f * MathF.PI * (y / (float)Size * 3f)));
                switch (kind)
                {
                    case Kind.Carpet:
                        k = 1f + 0.035f * mottle + 0.10f * (Noise(seed, x, y) - 0.5f);
                        if (Noise(seed + 3, x, y) > 0.97f) k *= 1.18f; // lighter fibre fleck
                        break;
                    case Kind.Wallpaper:
                        // Faint pinstripe every 16 px (8 per metre), plus slight mottle.
                        k = 1f + 0.025f * mottle + 0.03f * (Noise(seed, x / 2, y / 2) - 0.5f);
                        if (x % 16 == 0) k *= 1.07f;
                        break;
                    default:
                        // 2 x 2 tiles: 64 px per tile with a 2 px grey channel; pinhole specks.
                        int lx = x % 64, ly = y % 64;
                        if (lx < 2 || ly < 2)
                            k = 0.62f;
                        else
                        {
                            k = 1f + 0.02f * mottle;
                            if (Noise(seed + 9, x, y) > 0.94f) k *= 0.86f;
                        }
                        break;
                }
                int i = (y * Size + x) * 4;
                px[i] = Clamp(br * k);
                px[i + 1] = Clamp(bg * k);
                px[i + 2] = Clamp(bb * k);
                px[i + 3] = 255;
            }
        }
        return px;
    }

    private static byte Clamp(float v) => (byte)Math.Clamp(v, 0f, 255f);

    private static float Noise(int seed, int x, int y)
    {
        unchecked
        {
            uint h = (uint)(seed * 73856093 ^ (x & (Size - 1)) * 19349663 ^ (y & (Size - 1)) * 83492791);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }
    }
}

using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Creates bright, self-contained idle displays for the in-room TV and cabinets.  These do not
/// depend on a texture from the ripped source pack, so an unloaded/near-black source texture can
/// never turn an otherwise usable display into a black rectangle.
/// </summary>
internal static class ArcadeAttractScreenFactory
{
    private static readonly Color32 Background = new(5, 8, 26, 255);
    private static readonly Color32 Panel = new(12, 20, 62, 255);
    private static readonly Color32 White = new(244, 248, 255, 255);

    public static Texture2D Create(string name, int width, int height, string title, string subtitle, Color32 accent, Color32 accent2)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Color32[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool stripe = ((x + y * 2) / 11) % 2 == 0;
                pixels[y * width + x] = stripe ? Background : Panel;
            }
        }

        Fill(pixels, width, height, 2, 2, width - 4, height - 4, accent);
        Fill(pixels, width, height, 5, 5, width - 10, height - 10, Background);
        Fill(pixels, width, height, 8, 10, width - 16, height - 20, Panel);

        // Simple marquee flashes and two broad, visibly colourful game panels make the room
        // read as an active arcade even from the other side of the floor.
        Fill(pixels, width, height, 9, height - 15, width - 18, 3, accent2);
        Fill(pixels, width, height, 13, 16, 18, 18, accent);
        Fill(pixels, width, height, width - 31, 16, 18, 18, accent2);
        Fill(pixels, width, height, 17, 20, 10, 10, Background);
        Fill(pixels, width, height, width - 27, 20, 10, 10, Background);

        int titleScale = title.Length <= 8 ? 3 : 2;
        DrawTextCentered(pixels, width, height, title, height - 30, titleScale, White);
        DrawTextCentered(pixels, width, height, subtitle, 7, 2, accent);

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static void Fill(Color32[] pixels, int width, int height, int x, int y, int fillWidth, int fillHeight, Color32 color)
    {
        int minX = Mathf.Clamp(x, 0, width);
        int maxX = Mathf.Clamp(x + fillWidth, 0, width);
        int minY = Mathf.Clamp(y, 0, height);
        int maxY = Mathf.Clamp(y + fillHeight, 0, height);
        for (int py = minY; py < maxY; py++)
        {
            int row = py * width;
            for (int px = minX; px < maxX; px++)
                pixels[row + px] = color;
        }
    }

    private static void DrawTextCentered(Color32[] pixels, int width, int height, string text, int y, int scale, Color32 color)
        => DrawText(pixels, width, height, text, (width - TextWidth(text, scale)) / 2, y, scale, color);

    private static int TextWidth(string text, int scale) => Mathf.Max(0, text.Length * 4 * scale - scale);

    private static void DrawText(Color32[] pixels, int width, int height, string text, int x, int y, int scale, Color32 color)
    {
        int cursor = x;
        foreach (char raw in text)
        {
            string glyph = Glyph(char.ToUpperInvariant(raw));
            for (int row = 0; row < 5; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    if (glyph[row * 3 + column] == '1')
                        Fill(pixels, width, height, cursor + column * scale, y + (4 - row) * scale, scale, scale, color);
                }
            }
            cursor += 4 * scale;
        }
    }

    private static string Glyph(char character) => character switch
    {
        'A' => "010101111101101", 'B' => "110101110101110", 'C' => "111100100100111",
        'D' => "110101101101110", 'E' => "111100110100111", 'F' => "111100110100100",
        'G' => "111100101101111", 'I' => "111010010010111", 'K' => "101101110101101",
        'L' => "100100100100111", 'M' => "101111111101101", 'N' => "101111111111101",
        'O' => "111101101101111", 'P' => "110101110100100", 'R' => "110101110101101",
        'S' => "111100111001111", 'T' => "111010010010010", 'V' => "101101101101010",
        'Y' => "101101010010010", '0' => "111101101101111", '1' => "010110010010111",
        '2' => "111001111100111", '-' => "000000111000000", ' ' => "000000000000000",
        _ => "000000000000000"
    };
}

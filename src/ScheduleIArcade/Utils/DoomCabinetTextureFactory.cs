using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Builds two original 1990s hell-sci-fi cabinet texture atlases. They are deliberately authored
/// from pixels and simple shapes instead of redistributing any Doom art; each atlas follows the
/// supplied cabinet's UV layout so the entire machine, not just its monitor, gets a themed skin.
/// </summary>
internal static class DoomCabinetTextureFactory
{
    private const int Size = 512;
    private static readonly Color32 Ink = new(10, 8, 7, 255);
    private static readonly Color32 Bone = new(235, 196, 120, 255);
    private static readonly Color32 Orange = new(255, 100, 24, 255);
    private static readonly Color32 Ember = new(204, 34, 18, 255);
    private static readonly Color32 Steel = new(39, 42, 45, 255);
    private static readonly Color32 SteelLight = new(83, 88, 90, 255);

    public static Texture2D Create(string name, bool sequel)
    {
        var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Color32[Size * Size];
        Color32 wash = sequel ? new Color32(24, 37, 35, 255) : new Color32(47, 24, 15, 255);
        FillMetal(pixels, wash, sequel);

        string title = sequel ? "DOOM II" : "DOOM I";
        Color32 glow = sequel ? new Color32(115, 235, 117, 255) : Orange;
        Color32 edge = sequel ? new Color32(36, 123, 61, 255) : Ember;

        // These regions correspond to the source cabinet atlas's marquee, hero/front art,
        // control panel, and side panel. Repeating the art language makes every mapped piece
        // read as one intentionally themed cabinet even on split UV islands.
        DrawMarquee(pixels, 4, 400, 272, 105, title, glow, edge, sequel);
        DrawHeroPanel(pixels, 5, 150, 214, 225, title, glow, edge, sequel);
        DrawControlPanel(pixels, 280, 364, 228, 142, title, glow, edge, sequel);
        DrawSidePanel(pixels, 282, 6, 226, 348, title, glow, edge, sequel);
        DrawMarquee(pixels, 222, 246, 286, 102, title, glow, edge, sequel);

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static void FillMetal(Color32[] pixels, Color32 wash, bool sequel)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int grain = ((x * 17 + y * 31 + (x >> 3) * 13) & 15) - 7;
                bool seam = x % 42 < 2 || y % 54 < 2;
                int r = Mathf.Clamp(wash.r + grain + (sequel ? 0 : (y & 7)), 0, 255);
                int g = Mathf.Clamp(wash.g + grain, 0, 255);
                int b = Mathf.Clamp(wash.b + grain, 0, 255);
                pixels[y * Size + x] = seam ? Steel : new Color32((byte)r, (byte)g, (byte)b, 255);
            }
        }
    }

    private static void DrawMarquee(Color32[] pixels, int x, int y, int width, int height, string title, Color32 glow, Color32 edge, bool sequel)
    {
        Fill(pixels, x, y, width, height, Ink);
        Frame(pixels, x, y, width, height, edge, glow);
        DrawFlames(pixels, x + 8, y + 6, width - 16, height - 12, edge, glow, sequel);
        DrawTextCentered(pixels, title, x + width / 2, y + height / 2 - 22, 7, Ink, 2);
        DrawTextCentered(pixels, title, x + width / 2, y + height / 2 - 21, 7, glow, 0);
    }

    private static void DrawHeroPanel(Color32[] pixels, int x, int y, int width, int height, string title, Color32 glow, Color32 edge, bool sequel)
    {
        Fill(pixels, x, y, width, height, new Color32(18, 12, 11, 255));
        Frame(pixels, x, y, width, height, edge, glow);
        DrawFlames(pixels, x + 5, y + 5, width - 10, height - 10, edge, glow, sequel);
        DrawDemonHelmet(pixels, x + width / 2, y + 112, sequel ? glow : Orange, edge);
        DrawTextCentered(pixels, title, x + width / 2, y + 184, 6, Ink, 2);
        DrawTextCentered(pixels, title, x + width / 2, y + 185, 6, Bone, 0);
        DrawTextCentered(pixels, sequel ? "HELL ON EARTH" : "KNEE DEEP IN HELL", x + width / 2, y + 16, 3, Bone, 0);
        Rivets(pixels, x + 9, y + 9, width - 18, height - 18);
    }

    private static void DrawControlPanel(Color32[] pixels, int x, int y, int width, int height, string title, Color32 glow, Color32 edge, bool sequel)
    {
        Fill(pixels, x, y, width, height, Steel);
        Frame(pixels, x, y, width, height, edge, glow);
        DrawTextCentered(pixels, title, x + width / 2, y + height - 36, 5, Ink, 2);
        DrawTextCentered(pixels, title, x + width / 2, y + height - 35, 5, glow, 0);
        for (int i = 0; i < 6; i++)
        {
            int px = x + 22 + i * 34;
            Fill(pixels, px, y + 32, 20, 20, Ink);
            Fill(pixels, px + 3, y + 35, 14, 14, i % 2 == 0 ? edge : glow);
        }
        DrawTextCentered(pixels, sequel ? "RIP AND TEAR" : "ULTRA VIOLENCE", x + width / 2, y + 8, 3, Bone, 0);
    }

    private static void DrawSidePanel(Color32[] pixels, int x, int y, int width, int height, string title, Color32 glow, Color32 edge, bool sequel)
    {
        Fill(pixels, x, y, width, height, new Color32(18, 19, 20, 255));
        Frame(pixels, x, y, width, height, edge, glow);
        for (int row = 0; row < 5; row++)
        {
            int lineY = y + 31 + row * 57;
            Fill(pixels, x + 13, lineY, width - 26, 2, glow);
            DrawTextCentered(pixels, title, x + width / 2, lineY + 9, 5, glow, 0);
        }
        DrawDemonHelmet(pixels, x + width / 2, y + height / 2, sequel ? glow : Orange, edge);
        Rivets(pixels, x + 8, y + 8, width - 16, height - 16);
    }

    private static void DrawFlames(Color32[] pixels, int x, int y, int width, int height, Color32 ember, Color32 glow, bool sequel)
    {
        for (int i = 0; i < 15; i++)
        {
            int flameX = x + (i * 31 + 9) % width;
            int flameHeight = 18 + ((i * 19 + (sequel ? 11 : 0)) % 54);
            int flameWidth = 8 + (i % 4) * 4;
            Fill(pixels, flameX, y, flameWidth, flameHeight, ember);
            Fill(pixels, flameX + 2, y + 4, Mathf.Max(2, flameWidth - 4), flameHeight - 10, glow);
        }
    }

    private static void DrawDemonHelmet(Color32[] pixels, int centerX, int centerY, Color32 eyeGlow, Color32 edge)
    {
        // Angular original helmet / demon mask: horned silhouette, red visor and steel jaw.
        Fill(pixels, centerX - 62, centerY + 50, 28, 10, edge);
        Fill(pixels, centerX + 34, centerY + 50, 28, 10, edge);
        Fill(pixels, centerX - 46, centerY + 35, 92, 14, SteelLight);
        Fill(pixels, centerX - 56, centerY - 26, 112, 64, Steel);
        Fill(pixels, centerX - 44, centerY - 42, 88, 20, Steel);
        Fill(pixels, centerX - 34, centerY - 51, 68, 12, SteelLight);
        Fill(pixels, centerX - 43, centerY + 4, 36, 18, Ink);
        Fill(pixels, centerX + 7, centerY + 4, 36, 18, Ink);
        Fill(pixels, centerX - 36, centerY + 8, 23, 8, eyeGlow);
        Fill(pixels, centerX + 13, centerY + 8, 23, 8, eyeGlow);
        Fill(pixels, centerX - 30, centerY - 18, 60, 8, edge);
        Fill(pixels, centerX - 28, centerY - 55, 56, 4, Bone);
        for (int tooth = 0; tooth < 6; tooth++)
            Fill(pixels, centerX - 26 + tooth * 10, centerY - 67, 6, 14, Bone);
    }

    private static void Rivets(Color32[] pixels, int x, int y, int width, int height)
    {
        for (int ix = x; ix <= x + width; ix += 20)
        {
            Dot(pixels, ix, y, Bone);
            Dot(pixels, ix, y + height, Bone);
        }
        for (int iy = y + 20; iy < y + height; iy += 20)
        {
            Dot(pixels, x, iy, Bone);
            Dot(pixels, x + width, iy, Bone);
        }
    }

    private static void Dot(Color32[] pixels, int x, int y, Color32 color) => Fill(pixels, x - 2, y - 2, 5, 5, color);

    private static void Frame(Color32[] pixels, int x, int y, int width, int height, Color32 outer, Color32 inner)
    {
        Fill(pixels, x, y, width, 5, outer);
        Fill(pixels, x, y + height - 5, width, 5, outer);
        Fill(pixels, x, y, 5, height, outer);
        Fill(pixels, x + width - 5, y, 5, height, outer);
        Fill(pixels, x + 7, y + 7, width - 14, 2, inner);
    }

    private static void Fill(Color32[] pixels, int x, int y, int width, int height, Color32 color)
    {
        int minX = Mathf.Clamp(x, 0, Size);
        int maxX = Mathf.Clamp(x + width, 0, Size);
        int minY = Mathf.Clamp(y, 0, Size);
        int maxY = Mathf.Clamp(y + height, 0, Size);
        for (int py = minY; py < maxY; py++)
            for (int px = minX; px < maxX; px++)
                pixels[py * Size + px] = color;
    }

    private static void DrawTextCentered(Color32[] pixels, string text, int centerX, int y, int scale, Color32 color, int shadow)
    {
        int width = text.Length * 6 * scale - scale;
        DrawText(pixels, text, centerX - width / 2 + shadow, y - shadow, scale, Ink);
        DrawText(pixels, text, centerX - width / 2, y, scale, color);
    }

    private static void DrawText(Color32[] pixels, string text, int x, int y, int scale, Color32 color)
    {
        int cursor = x;
        foreach (char raw in text)
        {
            string glyph = Glyph(char.ToUpperInvariant(raw));
            for (int row = 0; row < 7; row++)
                for (int column = 0; column < 5; column++)
                    if (glyph[row * 5 + column] == '1')
                        Fill(pixels, cursor + column * scale, y + (6 - row) * scale, scale, scale, color);
            cursor += 6 * scale;
        }
    }

    private static string Glyph(char character) => character switch
    {
        'A' => "01110100011000111111100011000110001", 'D' => "11110100011000110001100011000111110",
        'E' => "11111100001111010000100001000011111", 'F' => "11111100001111010000100001000010000",
        'H' => "10001100011000111111100011000110001", 'I' => "11111001000010000100001000010011111",
        'K' => "10001100101010011000101001001010001", 'L' => "10000100001000010000100001000011111",
        'M' => "10001110111010110101100011000110001", 'N' => "10001110011010110101100111000110001",
        'O' => "01110100011000110001100011000101110", 'P' => "11110100011000111110100001000010000",
        'R' => "11110100011000111110101001001010001", 'T' => "11111001000010000100001000010000100",
        'U' => "10001100011000110001100011000101110", 'V' => "10001100011000110001100010101000100",
        ' ' => "00000000000000000000000000000000000", '1' => "00100011000010000100001000010001110",
        '2' => "01110100010000100010001000100011111", _ => "00000000000000000000000000000000000"
    };
}

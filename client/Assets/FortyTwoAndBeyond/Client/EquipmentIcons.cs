using System;
using System.Collections.Generic;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Small original ship-system glyphs generated once for the HUD.</summary>
    public sealed class EquipmentIcons : IDisposable
    {
        private readonly Dictionary<uint, Texture2D> textures = new Dictionary<uint, Texture2D>();

        /// <returns>Texture2D: The icon for a server item category.</returns>
        public Texture2D ForType(uint type)
        {
            if (!textures.TryGetValue(type, out Texture2D texture))
            {
                texture = Build(type);
                textures.Add(type, texture);
            }
            return texture;
        }

        /// <returns>Texture2D: A transparent thirty-two-pixel system glyph.</returns>
        private static Texture2D Build(uint type)
        {
            const int size = 32;
            var pixels = new Color32[size * size];
            Color32 cyan = new Color32(84, 222, 240, 255);
            Color32 gold = new Color32(252, 186, 91, 255);
            Color32 violet = new Color32(179, 160, 246, 255);
            Color32 coral = new Color32(255, 134, 102, 255);
            Color32 signal = type == 2 ? cyan : type == 7 ? gold : type == 6 ? violet :
                type >= 14 && type <= 16 ? coral : type == 10 ? gold : cyan;
            switch (type)
            {
                case 2: // shield envelope
                    Line(pixels, 16, 3, 27, 9, signal); Line(pixels, 27, 9, 25, 22, signal);
                    Line(pixels, 25, 22, 16, 29, signal); Line(pixels, 16, 29, 7, 22, signal);
                    Line(pixels, 7, 22, 5, 9, signal); Line(pixels, 5, 9, 16, 3, signal);
                    Line(pixels, 9, 16, 23, 16, signal); Line(pixels, 16, 8, 16, 23, signal);
                    break;
                case 7: // reactor core
                    Ring(pixels, 16, 16, 12, signal); Ring(pixels, 16, 16, 7, signal);
                    Box(pixels, 13, 13, 6, 6, signal);
                    Line(pixels, 16, 0, 16, 4, signal); Line(pixels, 16, 28, 16, 31, signal);
                    break;
                case 6: // paired drive nozzles and exhaust
                    Line(pixels, 6, 5, 12, 5, signal); Line(pixels, 20, 5, 26, 5, signal);
                    Box(pixels, 5, 6, 8, 15, signal); Box(pixels, 19, 6, 8, 15, signal);
                    Line(pixels, 7, 23, 9, 29, signal); Line(pixels, 11, 23, 9, 29, signal);
                    Line(pixels, 21, 23, 23, 29, signal); Line(pixels, 25, 23, 23, 29, signal);
                    break;
                case 14: // beam emitter
                    Box(pixels, 3, 12, 13, 8, signal);
                    Line(pixels, 17, 16, 28, 16, signal); Line(pixels, 25, 12, 29, 16, signal);
                    Line(pixels, 29, 16, 25, 20, signal);
                    break;
                case 15: // projectile barrels
                    Box(pixels, 4, 8, 15, 5, signal); Box(pixels, 4, 19, 15, 5, signal);
                    Box(pixels, 22, 9, 3, 3, signal); Box(pixels, 27, 9, 2, 3, signal);
                    Box(pixels, 22, 20, 3, 3, signal); Box(pixels, 27, 20, 2, 3, signal);
                    break;
                case 16: // guided missile
                    Line(pixels, 4, 16, 26, 16, signal); Line(pixels, 26, 16, 18, 8, signal);
                    Line(pixels, 26, 16, 18, 24, signal); Line(pixels, 9, 15, 4, 8, signal);
                    Line(pixels, 9, 17, 4, 24, signal); Box(pixels, 2, 14, 4, 4, signal);
                    break;
                case 11: // active device
                    Ring(pixels, 16, 16, 11, signal); Line(pixels, 16, 7, 16, 25, signal);
                    Line(pixels, 7, 16, 25, 16, signal); Box(pixels, 13, 13, 6, 6, signal);
                    break;
                case 10: // stacked ammunition
                    for (int x = 5; x <= 21; x += 8)
                    { Box(pixels, x, 12, 5, 15, signal); Line(pixels, x, 11, x + 2, 5, signal);
                        Line(pixels, x + 2, 5, x + 4, 11, signal); }
                    break;
                default: // cargo crate
                    Box(pixels, 6, 7, 20, 19, signal); Line(pixels, 6, 13, 26, 13, signal);
                    Line(pixels, 16, 7, 16, 26, signal);
                    break;
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels); texture.Apply(); return texture;
        }

        /// <summary>Set one glyph pixel when it lies inside the tile.</summary>
        private static void Plot(Color32[] pixels, int x, int y, Color32 color)
        { if (x >= 0 && x < 32 && y >= 0 && y < 32) pixels[y * 32 + x] = color; }

        /// <summary>Draw a short straight stroke on a glyph tile.</summary>
        private static void Line(Color32[] pixels, int x0, int y0, int x1, int y1, Color32 color)
        {
            int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
            for (int step = 0; step <= steps; step++)
            {
                float t = steps == 0 ? 0 : (float)step / steps;
                Plot(pixels, Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)),
                    Mathf.RoundToInt(Mathf.Lerp(y0, y1, t)), color);
            }
        }

        /// <summary>Draw a hollow rectangular instrument shape.</summary>
        private static void Box(Color32[] pixels, int x, int y, int width, int height, Color32 color)
        {
            Line(pixels, x, y, x + width - 1, y, color);
            Line(pixels, x, y + height - 1, x + width - 1, y + height - 1, color);
            Line(pixels, x, y, x, y + height - 1, color);
            Line(pixels, x + width - 1, y, x + width - 1, y + height - 1, color);
        }

        /// <summary>Draw an outlined circular instrument shape.</summary>
        private static void Ring(Color32[] pixels, int cx, int cy, int radius, Color32 color)
        {
            for (int degree = 0; degree < 360; degree += 4)
            {
                float angle = degree * Mathf.Deg2Rad;
                Plot(pixels, Mathf.RoundToInt(cx + Mathf.Cos(angle) * radius),
                    Mathf.RoundToInt(cy + Mathf.Sin(angle) * radius), color);
            }
        }

        /// <summary>Release all generated Unity textures.</summary>
        public void Dispose()
        {
            foreach (Texture2D texture in textures.Values) UnityEngine.Object.Destroy(texture);
            textures.Clear();
        }
    }
}

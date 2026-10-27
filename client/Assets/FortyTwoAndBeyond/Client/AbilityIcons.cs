using System.Collections.Generic;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Generate a stable, distinct instrument glyph for each native skill.</summary>
    internal sealed class AbilityIcons
    {
        private readonly Dictionary<int, Texture2D> cache = new Dictionary<int, Texture2D>();

        /// <returns>Texture2D: A cached badge unique to the server skill identifier.</returns>
        public Texture2D ForSkill(int skill)
        {
            if (!cache.TryGetValue(skill, out Texture2D icon))
            {
                icon = Build(skill);
                cache.Add(skill, icon);
            }
            return icon;
        }

        /// <returns>Texture2D: A compact colored glyph with a unique radial signature.</returns>
        private static Texture2D Build(int skill)
        {
            const int size = 48;
            var pixels = new Color32[size * size];
            float hue = Mathf.Repeat(skill * .173f, 1f);
            Color tone = Color.HSVToRGB(hue, .62f, .98f);
            int spokes = 3 + Mathf.Abs(skill) % 5;
            int inner = 7 + Mathf.Abs(skill * 3) % 5;
            float phase = Mathf.Repeat(skill * .137f, 1f) * Mathf.PI * 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - 23.5f, dy = y - 23.5f;
                float radius = Mathf.Sqrt(dx * dx + dy * dy);
                if (radius > 22) continue;
                float angle = Mathf.Atan2(dy, dx) - phase;
                float sector = Mathf.Repeat(angle * spokes / (2f * Mathf.PI), 1f);
                bool ray = radius > inner && radius < 18 &&
                    (sector < .055f || sector > .945f);
                bool core = radius < 3 + skill % 3;
                bool orbit = Mathf.Abs(radius - (12 + skill % 3)) < .9f &&
                    sector > .18f && sector < .82f;
                bool rim = Mathf.Abs(radius - 20) < 1.1f;
                float light = core || ray ? 1f : orbit ? .78f : rim ? .48f : .12f;
                pixels[y * size + x] = new Color(tone.r * light, tone.g * light,
                    tone.b * light, radius < 20 ? .3f + light * .7f : .28f);
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels); texture.Apply();
            return texture;
        }
    }
}

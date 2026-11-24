using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Bright, distinct player colours (no white/grey — they blend into the level). Each player in a lobby gets a unique one while there are enough.</summary>
    public static class PlayerPalette
    {
        public static readonly Color[] Colors =
        {
            Hex(0xFF3B30), // red
            Hex(0xFF7F27), // orange
            Hex(0xFFC233), // gold
            Hex(0xFFF03A), // yellow
            Hex(0xC6FF1A), // chartreuse
            Hex(0x5CFF4D), // green
            Hex(0x00F59B), // mint
            Hex(0x00E5D4), // teal
            Hex(0x22D3FF), // cyan
            Hex(0x3FA9FF), // sky
            Hex(0x2F6BFF), // blue
            Hex(0x7A5CFF), // indigo
            Hex(0xB44DFF), // violet
            Hex(0xE040FB), // purple
            Hex(0xFF2DAA), // magenta
            Hex(0xFF6FB5), // pink
            Hex(0xFF5C7A), // rose
            Hex(0xFF8A65), // coral
            Hex(0xC9B6FF), // lavender
        };

        public static int Count => Colors.Length;

        public static Color Get(int i) => i >= 0 && i < Colors.Length ? Colors[i] : Color.white;

        public static string HexString(int i) => ColorUtility.ToHtmlStringRGB(Get(i));

        /// <summary>
        /// Random team colours that stand well apart: the first two from opposite sides of the colour wheel, a third as far
        /// from both as it can be.
        /// </summary>
        public static byte[] Contrasting(int teams)
        {
            var bold = new List<int>(); // (nothing pale: it has no hue to speak of)
            for (int i = 0; i < Count; i++)
            {
                Color.RGBToHSV(Colors[i], out _, out float s, out _);
                if (s >= 0.6f) bold.Add(i);
            }
            var chosen = new List<int> { bold[Random.Range(0, bold.Count)] };
            while (chosen.Count < teams)
            {
                float best = 0f;
                foreach (int i in bold) best = Mathf.Max(best, Apart(i, chosen));
                var options = bold.FindAll(i => Apart(i, chosen) >= best * 0.8f);
                chosen.Add(options[Random.Range(0, options.Count)]);
            }
            var result = new byte[teams];
            for (int i = 0; i < teams; i++) result[i] = (byte)chosen[i];
            return result;
        }

        /// <summary>How far round the colour wheel (0..0.5) this colour is from the nearest of those.</summary>
        static float Apart(int i, List<int> from)
        {
            Color.RGBToHSV(Colors[i], out float h, out _, out _);
            float nearest = 0.5f;
            foreach (int f in from)
            {
                Color.RGBToHSV(Colors[f], out float o, out _, out _);
                float d = Mathf.Abs(h - o);
                nearest = Mathf.Min(nearest, Mathf.Min(d, 1f - d));
            }
            return nearest;
        }

        static Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}

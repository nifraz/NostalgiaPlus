using System;
using System.Drawing;

namespace NostalgiaPlus.Render
{
    public enum PaletteKind { Magma, Inferno, Viridis, Turbo, NostalgiaRed, Ice, Grey }

    /// <summary>
    /// Colour ramps as 256-entry packed-ARGB lookup tables.
    ///
    /// Magma/Inferno/Viridis are perceptually uniform: equal steps in value read as
    /// equal steps in brightness, so the eye can compare two points in the spectrogram
    /// and be right. The classic Cool Edit ramp is kept as NostalgiaRed - it saturates
    /// early by design, which is charming and imprecise.
    /// </summary>
    public static class Palette
    {
        private struct Stop
        {
            public double P; public int R, G, B;
            public Stop(double p, int r, int g, int b) { P = p; R = r; G = g; B = b; }
        }

        private static readonly Stop[] Viridis = {
            new Stop(0.00, 68,1,84),    new Stop(0.06, 72,21,103),  new Stop(0.13, 72,40,120),
            new Stop(0.19, 69,55,129),  new Stop(0.25, 64,70,136),  new Stop(0.31, 57,85,140),
            new Stop(0.38, 51,99,141),  new Stop(0.44, 45,112,142), new Stop(0.50, 40,125,142),
            new Stop(0.56, 35,138,141), new Stop(0.63, 31,150,139), new Stop(0.69, 32,163,134),
            new Stop(0.75, 41,175,127), new Stop(0.81, 60,187,117), new Stop(0.88, 109,205,89),
            new Stop(0.94, 170,220,50), new Stop(1.00, 253,231,37)
        };

        private static readonly Stop[] Magma = {
            new Stop(0.00, 0,0,4),      new Stop(0.06, 8,6,30),     new Stop(0.13, 20,14,54),
            new Stop(0.19, 35,18,81),   new Stop(0.25, 55,17,108),  new Stop(0.31, 76,17,122),
            new Stop(0.38, 97,25,127),  new Stop(0.44, 118,33,129), new Stop(0.50, 139,41,129),
            new Stop(0.56, 161,48,126), new Stop(0.63, 183,56,120), new Stop(0.69, 205,65,111),
            new Stop(0.75, 225,79,98),  new Stop(0.81, 241,101,86), new Stop(0.88, 251,132,86),
            new Stop(0.94, 254,176,120),new Stop(0.97, 253,205,148),new Stop(1.00, 252,253,191)
        };

        private static readonly Stop[] Inferno = {
            new Stop(0.00, 0,0,4),      new Stop(0.06, 10,7,35),    new Stop(0.13, 24,11,63),
            new Stop(0.19, 42,11,91),   new Stop(0.25, 62,9,113),   new Stop(0.31, 81,15,124),
            new Stop(0.38, 100,24,128), new Stop(0.44, 120,32,128), new Stop(0.50, 140,41,124),
            new Stop(0.56, 160,50,117), new Stop(0.63, 180,60,107), new Stop(0.69, 199,72,94),
            new Stop(0.75, 216,87,78),  new Stop(0.81, 231,105,59), new Stop(0.88, 243,128,37),
            new Stop(0.94, 249,193,31), new Stop(0.97, 251,225,86), new Stop(1.00, 252,255,164)
        };

        private static readonly Stop[] Turbo = {
            new Stop(0.00, 48,18,59),   new Stop(0.05, 60,45,124),  new Stop(0.10, 66,72,174),
            new Stop(0.15, 68,98,211),  new Stop(0.20, 66,124,236), new Stop(0.25, 58,149,250),
            new Stop(0.30, 44,173,250), new Stop(0.35, 30,193,235), new Stop(0.40, 26,209,210),
            new Stop(0.45, 37,221,180), new Stop(0.50, 62,231,148), new Stop(0.55, 96,238,116),
            new Stop(0.60, 133,243,88), new Stop(0.65, 169,246,66), new Stop(0.70, 202,243,53),
            new Stop(0.75, 227,232,50), new Stop(0.80, 246,213,49), new Stop(0.85, 253,186,45),
            new Stop(0.90, 251,153,38), new Stop(0.95, 226,84,17),  new Stop(1.00, 122,4,3)
        };

        private static readonly Stop[] NostalgiaRed = {
            new Stop(0.00, 0,0,0),      new Stop(0.15, 32,0,8),     new Stop(0.30, 96,0,16),
            new Stop(0.45, 160,8,8),    new Stop(0.60, 216,40,0),   new Stop(0.72, 248,96,0),
            new Stop(0.82, 255,160,16), new Stop(0.90, 255,208,64), new Stop(0.96, 255,240,160),
            new Stop(1.00, 255,255,255)
        };

        private static readonly Stop[] Ice = {
            new Stop(0.00, 0,0,8),      new Stop(0.20, 8,24,72),    new Stop(0.40, 12,64,140),
            new Stop(0.60, 22,120,196), new Stop(0.78, 70,182,226), new Stop(0.90, 150,224,240),
            new Stop(1.00, 240,253,255)
        };

        private static readonly Stop[] Grey = {
            new Stop(0.00, 0,0,0), new Stop(1.00, 255,255,255)
        };

        private static Stop[] StopsFor(PaletteKind kind)
        {
            switch (kind)
            {
                case PaletteKind.Viridis: return Viridis;
                case PaletteKind.Inferno: return Inferno;
                case PaletteKind.Turbo: return Turbo;
                case PaletteKind.NostalgiaRed: return NostalgiaRed;
                case PaletteKind.Ice: return Ice;
                case PaletteKind.Grey: return Grey;
                default: return Magma;
            }
        }

        /// <summary>Builds a 256-entry packed-ARGB table (opaque).</summary>
        public static int[] BuildLut(PaletteKind kind)
        {
            Stop[] stops = StopsFor(kind);
            int[] lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                int s = 0;
                while (s < stops.Length - 2 && t > stops[s + 1].P) s++;
                Stop a = stops[s], b = stops[s + 1];
                double span = b.P - a.P;
                double f = span <= 0 ? 0 : (t - a.P) / span;
                if (f < 0) f = 0; else if (f > 1) f = 1;
                int r = (int)Math.Round(a.R + (b.R - a.R) * f);
                int g = (int)Math.Round(a.G + (b.G - a.G) * f);
                int bl = (int)Math.Round(a.B + (b.B - a.B) * f);
                lut[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
            }
            return lut;
        }

        public static Color ColorAt(int[] lut, double t)
        {
            int i = (int)(t * 255.0);
            if (i < 0) i = 0; else if (i > 255) i = 255;
            int v = lut[i];
            return Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        /// <summary>The colour a palette assigns to silence - used as the panel background.</summary>
        public static Color Background(int[] lut) { return ColorAt(lut, 0.0); }

        public static string DisplayName(PaletteKind kind)
        {
            switch (kind)
            {
                case PaletteKind.NostalgiaRed: return "Nostalgia Red";
                default: return kind.ToString();
            }
        }
    }
}

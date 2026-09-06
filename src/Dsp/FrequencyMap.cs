using System;

namespace NostalgiaPlus.Dsp
{
    public enum FreqScale { Linear, Log, Note }

    /// <summary>
    /// Maps display columns to frequency band edges. Rebuilt only when the panel is
    /// resized or the scale changes, then reused for every frame.
    ///
    /// The band-edge model matters: each column owns a frequency *interval*, not a
    /// point sample, so the analyser can integrate all FFT bins that land inside it.
    /// That is what stops the high end from aliasing into noise on a log axis.
    /// </summary>
    public sealed class FrequencyMap
    {
        public readonly FreqScale Scale;
        public readonly int Width;
        public readonly double FMin;
        public readonly double FMax;

        /// <summary>Band edges, Width+1 entries. Column i spans Edges[i]..Edges[i+1].</summary>
        public readonly double[] Edges;
        public readonly double[] Centres;

        public FrequencyMap(FreqScale scale, int width, double fMin, double fMax)
        {
            if (width < 1) width = 1;
            Scale = scale;
            Width = width;
            FMin = fMin;
            FMax = fMax;

            Edges = new double[width + 1];
            Centres = new double[width];
            for (int i = 0; i <= width; i++)
                Edges[i] = PositionToFreq((double)i / width, scale, fMin, fMax);
            for (int i = 0; i < width; i++)
                Centres[i] = Math.Sqrt(Edges[i] * Edges[i + 1]); // geometric centre
        }

        private static double PositionToFreq(double t, FreqScale scale, double fMin, double fMax)
        {
            switch (scale)
            {
                case FreqScale.Linear:
                    return fMin + (fMax - fMin) * t;
                default: // Log and Note are the same mapping; they differ only in gridlines
                    return fMin * Math.Pow(fMax / fMin, t);
            }
        }

        /// <summary>Normalised 0..1 display position of a frequency.</summary>
        public double FreqToPosition(double f)
        {
            if (Scale == FreqScale.Linear)
                return (f - FMin) / (FMax - FMin);
            if (f <= 0) return 0;
            return Math.Log(f / FMin) / Math.Log(FMax / FMin);
        }

        public double FreqToX(double f) { return FreqToPosition(f) * Width; }

        public double XToFreq(double x)
        {
            return PositionToFreq(x / Width, Scale, FMin, FMax);
        }

        // ---- musical helpers ----

        private static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static double MidiToFreq(double midi) { return 440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0); }
        public static double FreqToMidi(double f) { return 69.0 + 12.0 * Math.Log(f / 440.0, 2.0); }

        /// <summary>Nearest note name plus the signed cents deviation from it.</summary>
        public static string DescribeNote(double freq, out double cents)
        {
            cents = 0;
            if (freq <= 0) return "-";
            double midi = FreqToMidi(freq);
            int nearest = (int)Math.Round(midi);
            cents = (midi - nearest) * 100.0;
            int pc = ((nearest % 12) + 12) % 12;
            int octave = (nearest / 12) - 1;
            return NoteNames[pc] + octave.ToString();
        }
    }
}

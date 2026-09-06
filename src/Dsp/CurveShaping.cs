using System;

namespace NostalgiaPlus.Dsp
{
    /// <summary>Which pair of signals the two panes display.</summary>
    public enum ChannelPairMode { LeftRight, MidSide, LeftOnly, RightOnly }

    /// <summary>How the curve is drawn between bins.</summary>
    public enum CurveInterpolation { PeakFlat, LinearSmooth, CubicSpline }

    /// <summary>How hard the curve is smoothed across frequency.</summary>
    public enum FilteringAmount { None, Light, Medium, Strong }

    public static class CurveShaping
    {
        /// <summary>Kernel half-width for each filtering amount.</summary>
        public static int KernelRadius(FilteringAmount amount)
        {
            switch (amount)
            {
                case FilteringAmount.Light: return 1;
                case FilteringAmount.Medium: return 3;
                case FilteringAmount.Strong: return 6;
                default: return 0;
            }
        }

        /// <summary>
        /// Smooths a dB curve across frequency. PeakFlat leaves it untouched so peaks keep
        /// their true height; LinearSmooth is a moving average; CubicSpline additionally
        /// applies a Catmull-Rom pass, which rounds the shoulders without pulling peaks
        /// down as far as a wider box filter would.
        /// </summary>
        public static void Smooth(double[] src, double[] dst, int count,
                                  CurveInterpolation interp, FilteringAmount amount)
        {
            if (count <= 0) return;
            int r = KernelRadius(amount);

            if (interp == CurveInterpolation.PeakFlat || r == 0)
            {
                Array.Copy(src, dst, count);
                if (interp != CurveInterpolation.CubicSpline) return;
            }
            else
            {
                double inv = 1.0 / (2 * r + 1);
                double acc = 0;
                for (int i = -r; i <= r; i++) acc += src[Clamp(i, count)];
                for (int i = 0; i < count; i++)
                {
                    dst[i] = acc * inv;
                    acc -= src[Clamp(i - r, count)];
                    acc += src[Clamp(i + r + 1, count)];
                }
            }

            if (interp == CurveInterpolation.CubicSpline)
            {
                // One Catmull-Rom evaluation at each sample's own position acts as a mild
                // shaping pass: it preserves the samples but softens the joins.
                double prev = dst[0];
                for (int i = 1; i < count - 1; i++)
                {
                    double p0 = prev, p1 = dst[i], p2 = dst[i + 1];
                    prev = dst[i];
                    dst[i] = 0.125 * p0 + 0.75 * p1 + 0.125 * p2;
                }
            }
        }

        private static int Clamp(int i, int n)
        {
            if (i < 0) return 0;
            if (i >= n) return n - 1;
            return i;
        }
    }

    /// <summary>
    /// Per-band minimum, maximum and average over a sliding window, the original
    /// plugin's Extremum traces. Max is a decaying peak hold, min a rising valley hold,
    /// and average an exponential mean - all cheap enough to run every frame and all
    /// reading correctly the instant playback changes.
    /// </summary>
    public sealed class ExtremumTracker
    {
        private double[] _min = new double[0];
        private double[] _max = new double[0];
        private double[] _avg = new double[0];

        public double[] Min { get { return _min; } }
        public double[] Max { get { return _max; } }
        public double[] Average { get { return _avg; } }

        /// <summary>Seconds for the average to follow a step change.</summary>
        public double AverageSeconds = 1.2;
        /// <summary>How fast the held extremes give up their reading.</summary>
        public double HoldDecayDbPerSecond = 14.0;

        public void Resize(int n)
        {
            if (_min.Length == n) return;
            _min = new double[n];
            _max = new double[n];
            _avg = new double[n];
            Reset();
        }

        public void Reset()
        {
            for (int i = 0; i < _min.Length; i++)
            {
                _min[i] = 0.0;
                _max[i] = SpectrumAnalyzer.FloorDb;
                _avg[i] = SpectrumAnalyzer.FloorDb;
            }
        }

        public void Update(double[] db, int count, double dt)
        {
            if (_min.Length < count) Resize(count);
            double drift = HoldDecayDbPerSecond * dt;
            double aCoef = 1.0 - Math.Exp(-dt / Math.Max(0.01, AverageSeconds));

            for (int i = 0; i < count; i++)
            {
                double v = db[i];

                double mx = _max[i] - drift;
                _max[i] = v > mx ? v : mx;

                double mn = _min[i] + drift;
                _min[i] = v < mn ? v : mn;

                _avg[i] += (v - _avg[i]) * aCoef;
            }
        }
    }
}

using System;

namespace NostalgiaPlus.Dsp
{
    /// <summary>
    /// Tracks a rolling dB histogram and derives the floor/ceiling used to map levels
    /// onto the colour ramp.
    ///
    /// A fixed window is why the original renders as a solid red wash: a modern master
    /// sitting near -12 dBFS puts almost every value into the top of the ramp, so
    /// everything saturates and structure disappears. Following percentiles of what is
    /// actually playing keeps the ramp spread across the material at hand.
    /// Movement is slew-limited so the picture does not pump on transients.
    /// </summary>
    public sealed class DynamicRange
    {
        private const int MinDb = -140;
        private const int MaxDb = 12;
        private const int Bins = MaxDb - MinDb;

        private readonly float[] _hist = new float[Bins];
        private double _floor = -100;
        private double _ceiling = -10;
        private bool _primed;

        /// <summary>Percentile taken as the noise floor.</summary>
        public double LowPercentile = 0.25;
        /// <summary>Percentile taken as the ceiling.</summary>
        public double HighPercentile = 0.999;
        /// <summary>Maximum movement in dB per second.</summary>
        public double SlewDbPerSecond = 9.0;
        /// <summary>Headroom added above the measured ceiling.</summary>
        public double CeilingPadDb = 3.0;
        /// <summary>Minimum span so quiet passages do not blow up the contrast.</summary>
        public double MinSpanDb = 35.0;
        /// <summary>
        /// Maximum span. Without this, a mostly-empty top end drags the low percentile
        /// toward the numeric floor and flattens all the contrast out of the picture.
        /// </summary>
        public double MaxSpanDb = 90.0;
        /// <summary>
        /// Ceiling is never tracked below this. Digital silence would otherwise pull the
        /// whole window down to -140 dB and render the dither-free noise floor at full
        /// brightness; pinning the ceiling makes silence read as silence.
        /// </summary>
        public double MinCeilingDb = -55.0;

        public double Floor { get { return _floor; } }
        public double Ceiling { get { return _ceiling; } }

        public void Reset()
        {
            Array.Clear(_hist, 0, Bins);
            _primed = false;
        }

        public void Observe(double[] db, int count, double decay)
        {
            for (int i = 0; i < Bins; i++) _hist[i] *= (float)decay;
            for (int i = 0; i < count; i++)
            {
                int b = (int)(db[i] - MinDb);
                if (b < 0) b = 0; else if (b >= Bins) b = Bins - 1;
                _hist[b] += 1f;
            }
        }

        public void Update(double dtSeconds)
        {
            double total = 0;
            for (int i = 0; i < Bins; i++) total += _hist[i];
            if (total <= 0) return;

            double lowTarget = Percentile(total, LowPercentile);
            double highTarget = Percentile(total, HighPercentile) + CeilingPadDb;

            if (highTarget < MinCeilingDb) highTarget = MinCeilingDb;
            if (highTarget - lowTarget > MaxSpanDb) lowTarget = highTarget - MaxSpanDb;

            if (highTarget - lowTarget < MinSpanDb)
            {
                double mid = (highTarget + lowTarget) * 0.5;
                lowTarget = mid - MinSpanDb * 0.5;
                highTarget = mid + MinSpanDb * 0.5;
            }

            if (!_primed)
            {
                _floor = lowTarget;
                _ceiling = highTarget;
                _primed = true;
                return;
            }

            double maxStep = SlewDbPerSecond * dtSeconds;
            _floor += Clamp(lowTarget - _floor, -maxStep, maxStep);
            _ceiling += Clamp(highTarget - _ceiling, -maxStep, maxStep);
        }

        private double Percentile(double total, double p)
        {
            double want = total * p;
            double acc = 0;
            for (int i = 0; i < Bins; i++)
            {
                acc += _hist[i];
                if (acc >= want) return MinDb + i;
            }
            return MaxDb;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}

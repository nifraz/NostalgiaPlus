using System;

namespace NostalgiaPlus.Dsp
{
    /// <summary>
    /// What the music is doing, as three numbers the view can react to: when a note or
    /// hit lands, how fast they are landing, and whether the sound is bright or dark.
    ///
    /// All three are derived from the spectrum the analyser already produced, so this
    /// costs one pass over the bins per frame and no extra transforms. Nothing here is
    /// a measurement in the sense the meters are - it exists to drive how the picture
    /// looks, and is tuned for that rather than for accuracy.
    /// </summary>
    public sealed class MusicFeatures
    {
        // Onset detection over spectral flux. Rising energy is what a hit sounds like;
        // falling energy is just the previous hit decaying, so only increases count.
        private double[] _prev = new double[0];

        // A short history of flux, for the threshold, and a longer one for the tempo.
        private const int ThresholdWindow = 90;    // ~1.5s at 60fps
        private const int TempoWindow = 512;       // ~8.5s at 60fps
        private readonly double[] _recent = new double[ThresholdWindow];
        private readonly double[] _tempo = new double[TempoWindow];
        private int _recentCount, _tempoHead, _tempoCount;

        private double _sinceOnset = 1.0;
        private double _sinceTempo;
        private double _centroidSmoothed = 0.5;

        /// <summary>Rising spectral energy this frame, in dB per bin.</summary>
        public double Flux { get; private set; }
        /// <summary>True on the frame an onset was accepted.</summary>
        public bool Onset { get; private set; }
        /// <summary>1 at an onset, decaying back to 0. What the view actually animates on.</summary>
        public double Pulse { get; private set; }
        /// <summary>Estimated tempo, or 0 when nothing convincing was found.</summary>
        public double Bpm { get; private set; }
        /// <summary>
        /// Where the energy sits on the display axis, 0 at the bottom and 1 at the top.
        /// Taken over bins rather than hertz, so on a note axis it is a musical centre
        /// of gravity rather than one dominated by the top octave.
        /// </summary>
        public double Centroid { get { return _centroidSmoothed; } }

        /// <summary>How long a pulse takes to fall away. Short enough to feel like a hit.</summary>
        public double PulseDecaySeconds = 0.20;

        /// <summary>Above this many dB per bin of rise, an onset is possible.</summary>
        private const double MinFlux = 0.05;
        /// <summary>Standard deviations above the running mean an onset has to clear.</summary>
        private const double ThresholdSigma = 1.6;
        /// <summary>Two hits closer together than this are one hit. 240 BPM of sixteenths.</summary>
        private const double MinOnsetGap = 0.06;

        public void Reset()
        {
            _prev = new double[0];
            _recentCount = 0;
            _tempoHead = 0;
            _tempoCount = 0;
            _sinceOnset = 1.0;
            _sinceTempo = 0;
            _centroidSmoothed = 0.5;
            Flux = 0; Onset = false; Pulse = 0; Bpm = 0;
        }

        public void Update(double[] db, int count, double dt)
        {
            Onset = false;
            if (db == null || count < 8) return;
            if (dt <= 0) dt = 1.0 / 60.0;
            if (dt > 0.5) dt = 0.5;

            if (_prev.Length != count)
            {
                _prev = new double[count];
                for (int i = 0; i < count; i++) _prev[i] = db[i];
                return;                       // no flux from the first frame
            }

            double rise = 0, weight = 0, energy = 0;
            for (int i = 0; i < count; i++)
            {
                double v = db[i];
                double d = v - _prev[i];
                if (d > 0) rise += d;
                _prev[i] = v;

                // Linear amplitude for the centroid; dB would let the noise floor,
                // which occupies most of the axis, drag the answer to the middle.
                double amp = v <= SpectrumAnalyzer.FloorDb ? 0 : Math.Pow(10.0, v / 20.0);
                energy += amp;
                weight += amp * i;
            }
            Flux = rise / count;

            if (energy > 1e-12)
            {
                double c = weight / energy / Math.Max(1, count - 1);
                // Smoothed hard: this drives colour, and a hue that jitters per frame
                // reads as a fault rather than as a response.
                double k = 1.0 - Math.Exp(-dt / 0.45);
                _centroidSmoothed += (c - _centroidSmoothed) * k;
            }

            PushTempo(Flux);
            DetectOnset(dt);

            Pulse *= Math.Exp(-dt / Math.Max(0.02, PulseDecaySeconds));
            if (Onset) Pulse = 1.0;

            _sinceTempo += dt;
            if (_sinceTempo >= 0.5) { _sinceTempo = 0; Bpm = EstimateBpm(); }
        }

        private void DetectOnset(double dt)
        {
            _sinceOnset += dt;

            // Adaptive threshold: quiet passages should still register their own hits,
            // and a loud one should not fire on every frame.
            double mean = 0;
            for (int i = 0; i < _recentCount; i++) mean += _recent[i];
            if (_recentCount > 0) mean /= _recentCount;

            double var = 0;
            for (int i = 0; i < _recentCount; i++)
            {
                double d = _recent[i] - mean;
                var += d * d;
            }
            double sd = _recentCount > 1 ? Math.Sqrt(var / (_recentCount - 1)) : 0;
            double threshold = Math.Max(MinFlux, mean + ThresholdSigma * sd);

            if (_recentCount >= ThresholdWindow / 3 &&
                Flux > threshold && _sinceOnset >= MinOnsetGap)
            {
                Onset = true;
                _sinceOnset = 0;
            }

            // The threshold history includes onsets: leaving them out would let a steady
            // beat lower its own bar until every frame qualified.
            if (_recentCount < ThresholdWindow) _recent[_recentCount++] = Flux;
            else
            {
                Array.Copy(_recent, 1, _recent, 0, ThresholdWindow - 1);
                _recent[ThresholdWindow - 1] = Flux;
            }
        }

        private void PushTempo(double flux)
        {
            _tempo[_tempoHead] = flux;
            _tempoHead = (_tempoHead + 1) % TempoWindow;
            if (_tempoCount < TempoWindow) _tempoCount++;
        }

        /// <summary>
        /// Autocorrelation of the flux signal, which finds a periodicity whether or not
        /// individual onsets were accepted - more forgiving than timing the gaps between
        /// detected hits, where one missed beat doubles the answer.
        ///
        /// Run twice a second rather than per frame: it is a few hundred thousand
        /// multiplies, and tempo does not change between frames.
        /// </summary>
        private double EstimateBpm()
        {
            if (_tempoCount < TempoWindow / 2) return 0;

            // Oldest first, mean removed - a DC offset correlates with everything.
            int n = _tempoCount;
            var x = new double[n];
            int start = (_tempoHead - n + TempoWindow) % TempoWindow;
            double mean = 0;
            for (int i = 0; i < n; i++) { x[i] = _tempo[(start + i) % TempoWindow]; mean += x[i]; }
            mean /= n;
            for (int i = 0; i < n; i++) x[i] -= mean;

            // Frame lags for 60..200 BPM, assuming the 60fps the views run at.
            const double Fps = 60.0;
            int minLag = (int)(Fps * 60.0 / 200.0);
            int maxLag = (int)(Fps * 60.0 / 60.0);
            if (maxLag >= n) maxLag = n - 1;
            if (minLag < 2 || minLag >= maxLag) return 0;

            double best = 0, bestLag = 0, total = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double sum = 0;
                for (int i = 0; i + lag < n; i++) sum += x[i] * x[i + lag];
                sum /= (n - lag);
                total += Math.Abs(sum);
                if (sum > best) { best = sum; bestLag = lag; }
            }
            if (bestLag <= 0 || best <= 0) return 0;

            // A peak that is not meaningfully above the average correlation is not a
            // tempo, it is noise with a maximum.
            double average = total / (maxLag - minLag + 1);
            if (best < average * 2.0) return 0;

            return 60.0 * Fps / bestLag;
        }
    }
}

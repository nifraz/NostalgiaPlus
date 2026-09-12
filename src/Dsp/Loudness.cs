using System;

namespace NostalgiaPlus.Dsp
{
    /// <summary>Direct-form-II biquad, one instance per channel per stage.</summary>
    internal sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public Biquad(double b0, double b1, double b2, double a1, double a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
        }

        public double Process(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        public void Reset() { _z1 = 0; _z2 = 0; }
    }

    /// <summary>
    /// Loudness and stereo metering to ITU-R BS.1770-4.
    ///
    /// Two K-weighting stages (a ~+4 dB high shelf and a ~38 Hz high-pass) run per
    /// channel, then mean square is integrated over sliding windows: 400 ms for
    /// momentary, 3 s for short-term. True peak uses 4x polyphase oversampling, which
    /// catches inter-sample peaks that a plain sample-peak reading misses entirely -
    /// the usual reason a "0 dBFS" master still clips a converter.
    ///
    /// Correlation and balance come from the unweighted signal over ~400 ms.
    ///
    /// Integrated loudness and loudness range are gated measures over the whole
    /// programme: momentary blocks for the first, three-second blocks for the second,
    /// each passed through an absolute -70 LUFS gate and then a relative one. They are
    /// the figures you quote - momentary and short term only tell you about now.
    /// </summary>
    public sealed class LoudnessMeter
    {
        // Published BS.1770 coefficients, exact at 48 kHz.
        private const double ShelfF0 = 1681.974450955533;
        private const double ShelfGainDb = 3.999843853973347;
        private const double ShelfQ = 0.7071752369554196;
        private const double HpF0 = 38.13547087602444;
        private const double HpQ = 0.5003270373238773;

        private Biquad _shelfL, _shelfR, _hpL, _hpR;
        private double _sampleRate;

        private double[] _msWindow;      // per-sample weighted mean square, ring
        private int _msWrite;
        private int _msFilled;
        private int _momentaryLen;
        private int _shortLen;
        private double _sumMomentary;
        private double _sumShort;

        // running sums maintained incrementally over the short-term window
        private double _sumCross, _sumLL, _sumRR;
        private double[] _corrL, _corrR;
        private int _corrWrite, _corrLen, _corrFilled;

        // --- gated loudness over the whole programme ---
        // Block loudness goes into a histogram rather than a list. Gating needs a
        // second pass over every block there has ever been, and a 0.1 LU histogram
        // answers that in fixed memory however long the track - or the stream - runs.
        private const int HistBins = 751;              // -70.0 to +5.0 LUFS
        private const double HistLo = -70.0, HistStep = 0.1;
        private readonly int[] _histM = new int[HistBins];   // 400 ms blocks, 75% overlap
        private readonly int[] _histS = new int[HistBins];   // 3 s blocks, 2/3 overlap
        private int _blockStep, _blockCountdown;
        private int _longStep, _longCountdown;
        private bool _gatesDirty;

        /// <summary>Above this a lossy encoder will clip even though the samples did not.</summary>
        public const double OverThresholdDb = -1.0;
        private static readonly double OverLevel = Math.Pow(10.0, OverThresholdDb / 20.0);
        /// <summary>
        /// Excursions closer together than this are one event. Without it a loud master
        /// that simply sits near the ceiling counts an over on every half cycle, and the
        /// number says nothing about how often it actually happened.
        /// </summary>
        private const double OverHoldoffSeconds = 0.200;
        private int _overHoldSamples, _overHoldoff;
        private long _clockSamples;

        private double _truePeak;
        private double _peakDecayPerSample;
        private double[] _tpHistL, _tpHistR;
        private int _tpPos;

        private static readonly double[][] Phases = BuildPolyphase();

        public double MomentaryLufs { get; private set; }
        public double ShortTermLufs { get; private set; }
        public double TruePeakDb { get; private set; }
        public double CrestDb { get; private set; }
        public double Correlation { get; private set; }
        /// <summary>-1 fully left, 0 centred, +1 fully right.</summary>
        public double Balance { get; private set; }
        /// <summary>Gated loudness since the last reset - the figure you quote.</summary>
        public double IntegratedLufs { get; private set; }
        /// <summary>
        /// Loudness range in LU: the spread between the 10th and 95th percentile of the
        /// three-second blocks that clear the gate. How much the track moves, as opposed
        /// to how loud it is.
        /// </summary>
        public double LoudnessRange { get; private set; }
        /// <summary>True-peak excursions past -1 dBTP since the last reset.</summary>
        public int Overs { get; private set; }
        /// <summary>When the last one happened, in seconds of audio since the reset.</summary>
        public double LastOverSeconds { get; private set; }

        public LoudnessMeter()
        {
            MomentaryLufs = -70; ShortTermLufs = -70; TruePeakDb = -70;
            CrestDb = 0; Correlation = 0; Balance = 0;
            IntegratedLufs = -70; LoudnessRange = 0;
        }

        public void Configure(double sampleRate)
        {
            if (sampleRate <= 0) sampleRate = 48000;
            if (_sampleRate == sampleRate && _shelfL != null) return;
            _sampleRate = sampleRate;

            double sb0, sb1, sb2, sa1, sa2;
            double hb0, hb1, hb2, ha1, ha2;

            if (Math.Abs(sampleRate - 48000.0) < 0.5)
            {
                // BS.1770-4 publishes these directly for 48 kHz. Deriving them instead
                // lands about 0.2 dB off, so prefer the exact values at the rate that
                // matters most - shared-mode WASAPI mixes are almost always 48 kHz.
                sb0 = 1.53512485958697; sb1 = -2.69169618940638; sb2 = 1.19839281085285;
                sa1 = -1.69065929318241; sa2 = 0.73248077421585;
                hb0 = 1.0; hb1 = -2.0; hb2 = 1.0;
                ha1 = -1.99004745483398; ha2 = 0.99007225036621;
            }
            else
            {
                HighShelf(sampleRate, ShelfF0, ShelfGainDb, ShelfQ, out sb0, out sb1, out sb2, out sa1, out sa2);
                HighPass(sampleRate, HpF0, HpQ, out hb0, out hb1, out hb2, out ha1, out ha2);
            }

            _shelfL = new Biquad(sb0, sb1, sb2, sa1, sa2);
            _shelfR = new Biquad(sb0, sb1, sb2, sa1, sa2);
            _hpL = new Biquad(hb0, hb1, hb2, ha1, ha2);
            _hpR = new Biquad(hb0, hb1, hb2, ha1, ha2);

            _momentaryLen = (int)(sampleRate * 0.400);
            _shortLen = (int)(sampleRate * 3.000);
            _msWindow = new double[_shortLen];
            _msWrite = 0; _msFilled = 0; _sumMomentary = 0; _sumShort = 0;

            _corrLen = (int)(sampleRate * 0.400);
            _corrL = new double[_corrLen];
            _corrR = new double[_corrLen];
            _corrWrite = 0; _corrFilled = 0;
            _sumCross = 0; _sumLL = 0; _sumRR = 0;

            // A block every 100 ms and every second: 400 ms blocks overlapping by 75%
            // and 3 s blocks by two thirds, which is what the two measures are defined on.
            _blockStep = Math.Max(1, (int)(sampleRate * 0.100));
            _longStep = Math.Max(1, (int)(sampleRate * 1.000));
            _blockCountdown = _blockStep;
            _longCountdown = _longStep;
            Array.Clear(_histM, 0, HistBins);
            Array.Clear(_histS, 0, HistBins);
            _overHoldSamples = Math.Max(1, (int)(sampleRate * OverHoldoffSeconds));
            _overHoldoff = 0;
            _clockSamples = 0; Overs = 0; LastOverSeconds = 0;
            IntegratedLufs = -70; LoudnessRange = 0;

            _tpHistL = new double[8];
            _tpHistR = new double[8];
            _tpPos = 0;
            _truePeak = 0;
            _peakDecayPerSample = Math.Pow(10.0, -(2.0 / 20.0) / sampleRate); // ~2 dB/s
        }

        public void Reset()
        {
            if (_shelfL == null) return;
            _shelfL.Reset(); _shelfR.Reset(); _hpL.Reset(); _hpR.Reset();
            Array.Clear(_msWindow, 0, _msWindow.Length);
            _msWrite = 0; _msFilled = 0; _sumMomentary = 0; _sumShort = 0;
            Array.Clear(_corrL, 0, _corrLen); Array.Clear(_corrR, 0, _corrLen);
            _corrWrite = 0; _corrFilled = 0; _sumCross = 0; _sumLL = 0; _sumRR = 0;
            _truePeak = 0;
            Array.Clear(_histM, 0, HistBins);
            Array.Clear(_histS, 0, HistBins);
            _blockCountdown = _blockStep; _longCountdown = _longStep;
            _clockSamples = 0; _overHoldoff = 0; Overs = 0; LastOverSeconds = 0;
            IntegratedLufs = -70; LoudnessRange = 0;
        }

        public void Process(double[] left, double[] right, int count)
        {
            if (_shelfL == null || count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                double l = left[i], r = right[i];

                // --- K-weighted mean square, summed across channels (G = 1.0 each) ---
                double kl = _hpL.Process(_shelfL.Process(l));
                double kr = _hpR.Process(_shelfR.Process(r));
                double ms = kl * kl + kr * kr;

                if (_msFilled == _shortLen)
                {
                    _sumShort -= _msWindow[_msWrite];
                    int mi = _msWrite - _momentaryLen;
                    if (mi < 0) mi += _shortLen;
                    _sumMomentary -= _msWindow[mi];
                }
                else
                {
                    int mi = _msWrite - _momentaryLen;
                    if (mi >= 0) _sumMomentary -= _msWindow[mi];
                }

                _msWindow[_msWrite] = ms;
                _sumShort += ms;
                _sumMomentary += ms;
                _msWrite++;
                if (_msWrite >= _shortLen) _msWrite = 0;
                if (_msFilled < _shortLen) _msFilled++;

                // A block is only taken once its window is genuinely full, or the first
                // few would be quiet by accident and drag the gated mean down.
                if (--_blockCountdown <= 0)
                {
                    _blockCountdown = _blockStep;
                    if (_msFilled >= _momentaryLen)
                    { AddBlock(_histM, _sumMomentary / _momentaryLen); _gatesDirty = true; }
                }
                if (--_longCountdown <= 0)
                {
                    _longCountdown = _longStep;
                    if (_msFilled >= _shortLen)
                    { AddBlock(_histS, _sumShort / _shortLen); _gatesDirty = true; }
                }

                // --- correlation / balance on the unweighted signal ---
                if (_corrFilled == _corrLen)
                {
                    double ol = _corrL[_corrWrite], orr = _corrR[_corrWrite];
                    _sumCross -= ol * orr; _sumLL -= ol * ol; _sumRR -= orr * orr;
                }
                _corrL[_corrWrite] = l; _corrR[_corrWrite] = r;
                _sumCross += l * r; _sumLL += l * l; _sumRR += r * r;
                _corrWrite++;
                if (_corrWrite >= _corrLen) _corrWrite = 0;
                if (_corrFilled < _corrLen) _corrFilled++;

                // --- true peak, 4x oversampled ---
                _tpHistL[_tpPos] = l; _tpHistR[_tpPos] = r;
                _tpPos = (_tpPos + 1) & 7;
                double localPeak = 0;
                for (int p = 0; p < Phases.Length; p++)
                {
                    double[] h = Phases[p];
                    double al = 0, ar = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int idx = (_tpPos + k) & 7;
                        al += _tpHistL[idx] * h[k];
                        ar += _tpHistR[idx] * h[k];
                    }
                    double m = Math.Max(Math.Abs(al), Math.Abs(ar));
                    if (m > localPeak) localPeak = m;
                }
                _truePeak *= _peakDecayPerSample;
                if (localPeak > _truePeak) _truePeak = localPeak;

                // One over per moment rather than per sample: a clipped passage is one
                // event to the ear and several thousand to a naive counter.
                if (_overHoldoff > 0) _overHoldoff--;
                if (localPeak > OverLevel && _overHoldoff == 0)
                {
                    Overs++;
                    LastOverSeconds = _clockSamples / _sampleRate;
                    _overHoldoff = _overHoldSamples;
                }
                _clockSamples++;
            }

            if (_gatesDirty)
            {
                _gatesDirty = false;
                double unused;
                IntegratedLufs = Gated(_histM, 10.0, out unused);
                double gate;
                Gated(_histS, 20.0, out gate);
                // The spread of what is left, not of everything: without the gate a
                // single silent passage sets the bottom of the range.
                double hi = Percentile(_histS, gate, 0.95);
                double lo = Percentile(_histS, gate, 0.10);
                LoudnessRange = (hi > -300 && lo > -300) ? Math.Max(0.0, hi - lo) : 0.0;
            }

            int mCount = Math.Min(_msFilled, _momentaryLen);
            if (mCount > 0)
                MomentaryLufs = -0.691 + 10.0 * Math.Log10(Math.Max(1e-14, _sumMomentary / mCount));
            if (_msFilled > 0)
                ShortTermLufs = -0.691 + 10.0 * Math.Log10(Math.Max(1e-14, _sumShort / _msFilled));

            TruePeakDb = 20.0 * Math.Log10(Math.Max(1e-9, _truePeak));

            if (_corrFilled > 0)
            {
                double denom = Math.Sqrt(_sumLL * _sumRR);
                Correlation = denom > 1e-12 ? _sumCross / denom : 0.0;
                double rmsL = Math.Sqrt(_sumLL / _corrFilled);
                double rmsR = Math.Sqrt(_sumRR / _corrFilled);
                double tot = rmsL + rmsR;
                Balance = tot > 1e-9 ? (rmsR - rmsL) / tot : 0.0;

                double rms = Math.Sqrt((_sumLL + _sumRR) / (2.0 * _corrFilled));
                CrestDb = rms > 1e-9 ? 20.0 * Math.Log10(Math.Max(1e-9, _truePeak) / rms) : 0.0;
            }
        }

        // ---- gating ----

        /// <summary>
        /// One block's loudness into the histogram. Anything under -70 LUFS is silence
        /// as far as the measure is concerned and is dropped here, which is the absolute
        /// gate - so everything in the histogram has already passed it.
        /// </summary>
        private static void AddBlock(int[] hist, double meanSquare)
        {
            if (meanSquare <= 0) return;
            double l = -0.691 + 10.0 * Math.Log10(meanSquare);
            if (l < HistLo) return;
            int i = (int)Math.Floor((l - HistLo) / HistStep + 0.5);
            if (i < 0) i = 0; else if (i >= HistBins) i = HistBins - 1;
            hist[i]++;
        }

        /// <summary>
        /// The gated mean: the mean of every block, then the mean again of the blocks
        /// that clear a threshold that many LU below it. Two passes, because the
        /// threshold cannot be known until the first pass has run.
        /// </summary>
        private static double Gated(int[] hist, double relativeLu, out double threshold)
        {
            double first = MeanAbove(hist, double.NegativeInfinity);
            if (first <= -300) { threshold = -300; return -70.0; }
            threshold = first - relativeLu;
            double second = MeanAbove(hist, threshold);
            return second <= -300 ? -70.0 : second;
        }

        /// <summary>
        /// Mean loudness of the blocks above a threshold. Averaged as power and
        /// converted back, never as decibels - the mean of two dB figures is not the
        /// dB of their mean, and the difference is the whole measurement.
        /// </summary>
        private static double MeanAbove(int[] hist, double aboveLufs)
        {
            double sum = 0; long n = 0;
            for (int i = 0; i < HistBins; i++)
            {
                if (hist[i] == 0) continue;
                double l = HistLo + i * HistStep;
                if (l <= aboveLufs) continue;
                sum += hist[i] * Math.Pow(10.0, (l + 0.691) / 10.0);
                n += hist[i];
            }
            return n == 0 ? -300.0 : -0.691 + 10.0 * Math.Log10(sum / n);
        }

        /// <summary>Loudness at a percentile of the blocks above a threshold.</summary>
        private static double Percentile(int[] hist, double aboveLufs, double p)
        {
            long total = 0;
            for (int i = 0; i < HistBins; i++)
            {
                if (hist[i] == 0) continue;
                if (HistLo + i * HistStep <= aboveLufs) continue;
                total += hist[i];
            }
            if (total == 0) return -300.0;

            long want = (long)Math.Ceiling(total * p);
            if (want < 1) want = 1;
            long run = 0;
            for (int i = 0; i < HistBins; i++)
            {
                if (hist[i] == 0) continue;
                double l = HistLo + i * HistStep;
                if (l <= aboveLufs) continue;
                run += hist[i];
                if (run >= want) return l;
            }
            return -300.0;
        }

        // ---- filter design (RBJ cookbook; reproduces the published a-coefficients) ----

        private static void HighShelf(double fs, double f0, double gainDb, double q,
                                      out double b0, out double b1, out double b2,
                                      out double a1, out double a2)
        {
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cosw = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * q);
            double sqrtA = Math.Sqrt(A);

            double nb0 = A * ((A + 1) + (A - 1) * cosw + 2 * sqrtA * alpha);
            double nb1 = -2 * A * ((A - 1) + (A + 1) * cosw);
            double nb2 = A * ((A + 1) + (A - 1) * cosw - 2 * sqrtA * alpha);
            double na0 = (A + 1) - (A - 1) * cosw + 2 * sqrtA * alpha;
            double na1 = 2 * ((A - 1) - (A + 1) * cosw);
            double na2 = (A + 1) - (A - 1) * cosw - 2 * sqrtA * alpha;

            b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0;
            a1 = na1 / na0; a2 = na2 / na0;
        }

        private static void HighPass(double fs, double f0, double q,
                                     out double b0, out double b1, out double b2,
                                     out double a1, out double a2)
        {
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cosw = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * q);

            double na0 = 1 + alpha;
            // BS.1770 specifies this stage with b = [1, -2, 1] rather than the
            // unity-gain RBJ normalisation; keep the spec form.
            b0 = 1.0; b1 = -2.0; b2 = 1.0;
            a1 = (-2 * cosw) / na0;
            a2 = (1 - alpha) / na0;
        }

        /// <summary>4-phase polyphase interpolator, 8 taps per phase (windowed sinc).</summary>
        private static double[][] BuildPolyphase()
        {
            const int phases = 4;
            const int taps = 8;
            var result = new double[phases][];
            for (int p = 0; p < phases; p++)
            {
                var h = new double[taps];
                double sum = 0;
                for (int k = 0; k < taps; k++)
                {
                    double t = (k - (taps / 2 - 1)) - p / (double)phases;
                    double s = (Math.Abs(t) < 1e-9) ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
                    // Blackman window across the tap span
                    double wpos = (k + p / (double)phases) / (taps - 1.0);
                    if (wpos < 0) wpos = 0; else if (wpos > 1) wpos = 1;
                    double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * wpos) + 0.08 * Math.Cos(4 * Math.PI * wpos);
                    h[k] = s * w;
                    sum += h[k];
                }
                if (Math.Abs(sum) > 1e-12)
                    for (int k = 0; k < taps; k++) h[k] /= sum;
                result[p] = h;
            }
            return result;
        }
    }
}

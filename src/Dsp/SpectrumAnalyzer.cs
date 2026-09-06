using System;

namespace NostalgiaPlus.Dsp
{
    public enum AnalysisQuality { Fast, Balanced, High }
    public enum BandAggregate { Peak, Energy }

    /// <summary>
    /// Multi-resolution spectrum analyser.
    ///
    /// A single FFT size forces one compromise across the whole band: big enough to
    /// separate bass notes means smeared transients up top, and vice versa. This runs
    /// several sizes concurrently over the same instant and stitches them, so bin
    /// density roughly tracks the log frequency axis instead of fighting it.
    ///
    /// At 48 kHz the Balanced profile resolves ~2.9 Hz below 300 Hz - about one
    /// semitone at 50 Hz - while keeping 47 Hz bins above 3 kHz where transients live.
    ///
    /// <see cref="ComputeStereo"/> returns both channels for the cost of one transform
    /// by packing left and right into a single complex FFT.
    /// </summary>
    public sealed class SpectrumAnalyzer
    {
        private sealed class SubBand
        {
            public Fft Transform;
            public int N;
            public double LoHz;
            public double HiHz;
            public double[] Window;
            public double WindowGain;
            public double[] Re, Im;
            public double[] MagL, MagR;
            public double[] FrameL, FrameR;
            public double BinWidth;
        }

        private SubBand[] _bands = new SubBand[0];
        private double[] _snapL = new double[0];
        private double[] _snapR = new double[0];
        private int _maxN;
        private double _sampleRate = 48000;
        private WindowType _window = WindowType.Hann;
        private AnalysisQuality _quality = AnalysisQuality.Balanced;

        /// <summary>Width of the crossover blend, as a ratio either side of the corner.</summary>
        private const double BlendRatio = 1.35;

        public const double FloorDb = -140.0;

        public double SampleRate { get { return _sampleRate; } }
        public int LargestFft { get { return _maxN; } }

        /// <summary>Pane labels for a pair mode, outer pane first.</summary>
        public static string[] PaneLabels(ChannelPairMode mode)
        {
            switch (mode)
            {
                case ChannelPairMode.MidSide: return new string[] { "MID", "SIDE" };
                case ChannelPairMode.LeftOnly: return new string[] { "LEFT", "LEFT" };
                case ChannelPairMode.RightOnly: return new string[] { "RIGHT", "RIGHT" };
                default: return new string[] { "L", "R" };
            }
        }

        /// <summary>Single-channel modes render one full-width pane instead of two.</summary>
        public static int PaneCount(ChannelPairMode mode)
        {
            return (mode == ChannelPairMode.LeftOnly || mode == ChannelPairMode.RightOnly) ? 1 : 2;
        }

        public string DescribeResolution()
        {
            if (_bands.Length == 0) return "-";
            string s = "";
            for (int i = 0; i < _bands.Length; i++)
            {
                if (i > 0) s += " / ";
                s += (_bands[i].N / 1024 > 0 ? (_bands[i].N / 1024) + "K" : _bands[i].N.ToString());
            }
            return s;
        }

        public void Configure(double sampleRate, AnalysisQuality quality, WindowType window)
        {
            if (sampleRate <= 0) sampleRate = 48000;
            if (_sampleRate == sampleRate && _quality == quality && _window == window && _bands.Length > 0)
                return;

            _sampleRate = sampleRate;
            _quality = quality;
            _window = window;

            double nyq = sampleRate * 0.5;
            int[] sizes;
            double[] corners; // upper edge of each band except the last, which runs to Nyquist

            switch (quality)
            {
                case AnalysisQuality.Fast:
                    sizes = new int[] { 4096 };
                    corners = new double[0];
                    break;
                case AnalysisQuality.High:
                    sizes = new int[] { 32768, 8192, 2048, 512 };
                    corners = new double[] { 250, 2000, 8000 };
                    break;
                default:
                    sizes = new int[] { 16384, 4096, 1024 };
                    corners = new double[] { 300, 3000 };
                    break;
            }

            var bands = new SubBand[sizes.Length];
            _maxN = 0;
            for (int i = 0; i < sizes.Length; i++)
            {
                var b = new SubBand();
                b.N = sizes[i];
                b.Transform = new Fft(b.N);
                b.LoHz = (i == 0) ? 0.0 : corners[i - 1];
                b.HiHz = (i == sizes.Length - 1) ? nyq : corners[i];
                double gain;
                b.Window = WindowFunctions.Build(window, b.N, out gain);
                b.WindowGain = gain;
                b.Re = new double[b.N];
                b.Im = new double[b.N];
                b.MagL = new double[b.N / 2 + 1];
                b.MagR = new double[b.N / 2 + 1];
                b.FrameL = new double[b.N];
                b.FrameR = new double[b.N];
                b.BinWidth = sampleRate / b.N;
                bands[i] = b;
                if (b.N > _maxN) _maxN = b.N;
            }

            _bands = bands;
            _snapL = new double[_maxN];
            _snapR = new double[_maxN];
        }

        /// <summary>
        /// Single-channel analysis. Fills outDb (one entry per display column) with dBFS
        /// levels for the current instant.
        /// </summary>
        public bool Compute(SampleRing ring, ChannelMode channel, FrequencyMap map,
                            double[] outDb, BandAggregate aggregate, double tiltDbPerOctave)
        {
            if (_bands.Length == 0 || map == null || outDb == null) return false;
            if (!ring.Snapshot(_snapL, _maxN, channel)) return false;

            for (int i = 0; i < _bands.Length; i++)
            {
                SubBand b = _bands[i];
                Array.Copy(_snapL, _maxN - b.N, b.FrameL, 0, b.N);
                b.Transform.MagnitudeReal(b.FrameL, b.Window, b.WindowGain, b.Re, b.Im, b.MagL);
            }

            Project(map, outDb, null, aggregate, tiltDbPerOctave);
            return true;
        }

        /// <summary>
        /// Dual-channel analysis for the mirrored fullscreen view. One complex FFT per
        /// sub-band yields both channels.
        /// </summary>
        public bool ComputeStereo(SampleRing ring, FrequencyMap map,
                                  double[] outLeftDb, double[] outRightDb,
                                  BandAggregate aggregate, double tiltDbPerOctave)
        {
            return ComputeStereo(ring, map, outLeftDb, outRightDb, aggregate,
                                 tiltDbPerOctave, ChannelPairMode.LeftRight);
        }

        /// <summary>
        /// Dual-channel analysis for whichever pair of signals is on display. Mid/Side is
        /// derived before the transform, so it costs exactly what L/R costs - the paired
        /// FFT does not care which two real signals it is handed.
        /// </summary>
        public bool ComputeStereo(SampleRing ring, FrequencyMap map,
                                  double[] outLeftDb, double[] outRightDb,
                                  BandAggregate aggregate, double tiltDbPerOctave,
                                  ChannelPairMode mode)
        {
            if (_bands.Length == 0 || map == null || outLeftDb == null || outRightDb == null) return false;
            if (!ring.SnapshotStereo(_snapL, _snapR, _maxN)) return false;

            switch (mode)
            {
                case ChannelPairMode.MidSide:
                    for (int i = 0; i < _maxN; i++)
                    {
                        double l = _snapL[i], r = _snapR[i];
                        _snapL[i] = (l + r) * 0.5;   // mid
                        _snapR[i] = (l - r) * 0.5;   // side
                    }
                    break;
                case ChannelPairMode.LeftOnly:
                    Array.Copy(_snapL, _snapR, _maxN);
                    break;
                case ChannelPairMode.RightOnly:
                    Array.Copy(_snapR, _snapL, _maxN);
                    break;
            }

            for (int i = 0; i < _bands.Length; i++)
            {
                SubBand b = _bands[i];
                Array.Copy(_snapL, _maxN - b.N, b.FrameL, 0, b.N);
                Array.Copy(_snapR, _maxN - b.N, b.FrameR, 0, b.N);
                b.Transform.MagnitudeRealPair(b.FrameL, b.FrameR, b.Window, b.WindowGain,
                                              b.Re, b.Im, b.MagL, b.MagR);
            }

            Project(map, outLeftDb, outRightDb, aggregate, tiltDbPerOctave);
            return true;
        }

        private void Project(FrequencyMap map, double[] outA, double[] outB,
                             BandAggregate aggregate, double tiltDbPerOctave)
        {
            int w = Math.Min(map.Width, outA.Length);
            if (outB != null) w = Math.Min(w, outB.Length);

            for (int x = 0; x < w; x++)
            {
                double f0 = map.Edges[x];
                double f1 = map.Edges[x + 1];
                double fc = map.Centres[x];

                double tilt = 0.0;
                if (tiltDbPerOctave != 0.0 && fc > 0)
                    tilt = tiltDbPerOctave * Math.Log(fc / 1000.0, 2.0);

                outA[x] = ToDb(BlendedMagnitude(f0, f1, fc, aggregate, false), tilt);
                if (outB != null)
                    outB[x] = ToDb(BlendedMagnitude(f0, f1, fc, aggregate, true), tilt);
            }
        }

        private static double ToDb(double mag, double tilt)
        {
            double db = mag > 1e-12 ? 20.0 * Math.Log10(mag) + tilt : FloorDb;
            return db < FloorDb ? FloorDb : db;
        }

        private double BlendedMagnitude(double f0, double f1, double fc,
                                        BandAggregate aggregate, bool right)
        {
            // Locate the band owning fc, and blend with a neighbour inside the crossover.
            int idx = _bands.Length - 1;
            for (int i = 0; i < _bands.Length; i++)
            {
                if (fc < _bands[i].HiHz) { idx = i; break; }
            }

            double primary = BandMagnitude(_bands[idx], f0, f1, fc, aggregate, right);

            // Blend upward across the corner between idx and idx+1.
            if (idx + 1 < _bands.Length)
            {
                double corner = _bands[idx].HiHz;
                double lo = corner / BlendRatio;
                if (fc > lo)
                {
                    double t = Math.Log(fc / lo, 2.0) / Math.Log(BlendRatio, 2.0);
                    if (t > 1) t = 1;
                    double other = BandMagnitude(_bands[idx + 1], f0, f1, fc, aggregate, right);
                    primary = primary * (1 - t) + other * t;
                }
            }
            // Blend downward across the corner between idx-1 and idx.
            if (idx - 1 >= 0)
            {
                double corner = _bands[idx - 1].HiHz;
                double hi = corner * BlendRatio;
                if (fc < hi)
                {
                    double t = Math.Log(hi / fc, 2.0) / Math.Log(BlendRatio, 2.0);
                    if (t > 1) t = 1;
                    double other = BandMagnitude(_bands[idx - 1], f0, f1, fc, aggregate, right);
                    primary = primary * (1 - t) + other * t;
                }
            }
            return primary;
        }

        private static double BandMagnitude(SubBand b, double f0, double f1, double fc,
                                            BandAggregate aggregate, bool right)
        {
            double[] mag = right ? b.MagR : b.MagL;
            int half = b.N / 2;
            int k0 = (int)Math.Ceiling(f0 / b.BinWidth);
            int k1 = (int)Math.Floor(f1 / b.BinWidth);
            if (k0 < 0) k0 = 0;
            if (k1 > half) k1 = half;

            if (k1 >= k0)
            {
                if (aggregate == BandAggregate.Energy)
                {
                    double p = 0;
                    for (int k = k0; k <= k1; k++) p += mag[k] * mag[k];
                    return Math.Sqrt(p);
                }
                double m = 0;
                for (int k = k0; k <= k1; k++) { if (mag[k] > m) m = mag[k]; }
                return m;
            }

            // Column narrower than one bin: interpolate at the centre frequency so the
            // low end reads as a smooth ridge rather than a staircase.
            double pos = fc / b.BinWidth;
            int i0 = (int)Math.Floor(pos);
            if (i0 < 0) i0 = 0;
            if (i0 >= half) return mag[half];
            int i1 = i0 + 1;
            if (i1 > half) i1 = half;
            double frac = pos - i0;
            return mag[i0] * (1 - frac) + mag[i1] * frac;
        }
    }
}

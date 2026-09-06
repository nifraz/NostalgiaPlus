using System;

namespace NostalgiaPlus.Dsp
{
    public enum WindowType { Hann, Hamming, BlackmanHarris, Nuttall, Gaussian, Rectangular }

    /// <summary>
    /// Iterative radix-2 Cooley-Tukey FFT with precomputed bit-reversal and twiddle
    /// tables. One instance per transform size; instances are reused every frame so
    /// nothing is allocated in the audio or render path.
    /// </summary>
    public sealed class Fft
    {
        private readonly int _n;
        private readonly int _levels;
        private readonly int[] _rev;
        private readonly double[] _cos;
        private readonly double[] _sin;

        public int Size { get { return _n; } }

        public Fft(int n)
        {
            if (n < 2 || (n & (n - 1)) != 0)
                throw new ArgumentException("FFT size must be a power of two");
            _n = n;
            _levels = 0;
            for (int t = n; t > 1; t >>= 1) _levels++;

            _rev = new int[n];
            for (int i = 0; i < n; i++)
            {
                int x = i, r = 0;
                for (int b = 0; b < _levels; b++) { r = (r << 1) | (x & 1); x >>= 1; }
                _rev[i] = r;
            }

            _cos = new double[n / 2];
            _sin = new double[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                double a = -2.0 * Math.PI * i / n;
                _cos[i] = Math.Cos(a);
                _sin[i] = Math.Sin(a);
            }
        }

        /// <summary>In-place forward transform. re/im must both be Size long.</summary>
        public void Forward(double[] re, double[] im)
        {
            int n = _n;
            for (int i = 0; i < n; i++)
            {
                int j = _rev[i];
                if (j > i)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }

            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1;
                int step = n / size;
                for (int i = 0; i < n; i += size)
                {
                    for (int j = i, k = 0; j < i + half; j++, k += step)
                    {
                        int l = j + half;
                        double wr = _cos[k], wi = _sin[k];
                        double tr = re[l] * wr - im[l] * wi;
                        double ti = re[l] * wi + im[l] * wr;
                        re[l] = re[j] - tr; im[l] = im[j] - ti;
                        re[j] += tr;        im[j] += ti;
                    }
                }
            }
        }

        /// <summary>
        /// Magnitude spectrum of a real windowed signal, in linear amplitude,
        /// normalised so a full-scale sine reads 1.0 regardless of window or size.
        /// Only bins 0..n/2 are meaningful for real input.
        /// </summary>
        public void MagnitudeReal(double[] input, double[] window, double windowGain,
                                  double[] scratchRe, double[] scratchIm, double[] outMag)
        {
            int n = _n;
            for (int i = 0; i < n; i++) { scratchRe[i] = input[i] * window[i]; scratchIm[i] = 0.0; }
            Forward(scratchRe, scratchIm);

            // 2/N compensates the one-sided spectrum; windowGain undoes window attenuation.
            double scale = 2.0 / (n * windowGain);
            int half = n / 2;
            for (int i = 0; i <= half; i++)
            {
                double m = Math.Sqrt(scratchRe[i] * scratchRe[i] + scratchIm[i] * scratchIm[i]) * scale;
                outMag[i] = m;
            }
            outMag[0] *= 0.5;
            outMag[half] *= 0.5;
        }

        /// <summary>
        /// Magnitude spectra of TWO real signals from a SINGLE complex transform.
        ///
        /// Both channels are real, so the left channel is packed into the real part and
        /// the right into the imaginary part. Because a real signal has a Hermitian
        /// spectrum, the two are separable afterwards:
        ///     X[k] = (Z[k] + conj(Z[N-k])) / 2      (left)
        ///     Y[k] = (Z[k] - conj(Z[N-k])) / 2j     (right)
        /// Stereo therefore costs one FFT, not two - which is what keeps the dual-channel
        /// fullscreen view inside its frame budget at high resolutions.
        ///
        /// Scaling matches <see cref="MagnitudeReal"/>: a full-scale sine reads 1.0.
        /// </summary>
        public void MagnitudeRealPair(double[] left, double[] right, double[] window, double windowGain,
                                      double[] scratchRe, double[] scratchIm,
                                      double[] outLeft, double[] outRight)
        {
            int n = _n;
            for (int i = 0; i < n; i++)
            {
                double w = window[i];
                scratchRe[i] = left[i] * w;
                scratchIm[i] = right[i] * w;
            }
            Forward(scratchRe, scratchIm);

            // The 0.5 from the separation cancels one factor of the one-sided 2/N.
            double scale = 1.0 / (n * windowGain);
            int half = n / 2;
            for (int k = 0; k <= half; k++)
            {
                int m = (n - k) & (n - 1); // N-k, wrapping k=0 to index 0
                double ar = scratchRe[k], ai = scratchIm[k];
                double br = scratchRe[m], bi = scratchIm[m];

                double lr = ar + br, li = ai - bi;
                double rr = ai + bi, ri = ar - br;

                outLeft[k] = Math.Sqrt(lr * lr + li * li) * scale;
                outRight[k] = Math.Sqrt(rr * rr + ri * ri) * scale;
            }
            outLeft[0] *= 0.5; outLeft[half] *= 0.5;
            outRight[0] *= 0.5; outRight[half] *= 0.5;
        }
    }

    public static class WindowFunctions
    {
        /// <summary>Builds a window and returns its coherent gain (mean value).</summary>
        public static double[] Build(WindowType type, int n, out double coherentGain)
        {
            double[] w = new double[n];
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double x = (double)i / (n - 1);
                double v;
                switch (type)
                {
                    case WindowType.Rectangular:
                        v = 1.0;
                        break;
                    case WindowType.Hamming:
                        v = 0.54 - 0.46 * Math.Cos(2 * Math.PI * x);
                        break;
                    case WindowType.BlackmanHarris:
                        v = 0.35875
                          - 0.48829 * Math.Cos(2 * Math.PI * x)
                          + 0.14128 * Math.Cos(4 * Math.PI * x)
                          - 0.01168 * Math.Cos(6 * Math.PI * x);
                        break;
                    case WindowType.Nuttall:
                        v = 0.355768
                          - 0.487396 * Math.Cos(2 * Math.PI * x)
                          + 0.144232 * Math.Cos(4 * Math.PI * x)
                          - 0.012604 * Math.Cos(6 * Math.PI * x);
                        break;
                    case WindowType.Gaussian:
                        {
                            double sigma = 0.4;
                            double d = (i - (n - 1) / 2.0) / (sigma * (n - 1) / 2.0);
                            v = Math.Exp(-0.5 * d * d);
                        }
                        break;
                    default: // Hann
                        v = 0.5 - 0.5 * Math.Cos(2 * Math.PI * x);
                        break;
                }
                w[i] = v;
                sum += v;
            }
            coherentGain = sum / n;
            return w;
        }
    }
}

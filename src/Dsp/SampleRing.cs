using System;

namespace NostalgiaPlus.Dsp
{
    public enum ChannelMode { Mid, Left, Right, Side }

    /// <summary>
    /// Lock-guarded ring of recent stereo samples. One writer (the capture thread),
    /// one reader (the render timer). Contention is negligible - the reader takes a
    /// snapshot roughly 60x/sec - so a plain lock is cheaper than getting a
    /// lock-free scheme subtly wrong.
    /// </summary>
    public sealed class SampleRing
    {
        private readonly object _gate = new object();
        private float[] _left;
        private float[] _right;
        private int _capacity;
        private int _write;
        private long _total;

        public SampleRing(int capacity)
        {
            _capacity = NextPow2(capacity);
            _left = new float[_capacity];
            _right = new float[_capacity];
        }

        public long TotalFrames { get { lock (_gate) { return _total; } } }

        private static int NextPow2(int v)
        {
            int n = 2;
            while (n < v) n <<= 1;
            return n;
        }

        /// <summary>Append interleaved frames. Extra channels beyond the first two are ignored.</summary>
        public void Write(float[] interleaved, int offsetSamples, int frameCount, int channels)
        {
            if (frameCount <= 0) return;
            lock (_gate)
            {
                int mask = _capacity - 1;
                int w = _write;
                for (int i = 0; i < frameCount; i++)
                {
                    int b = offsetSamples + i * channels;
                    float l = interleaved[b];
                    float r = channels > 1 ? interleaved[b + 1] : l;
                    _left[w] = l;
                    _right[w] = r;
                    w = (w + 1) & mask;
                }
                _write = w;
                _total += frameCount;
            }
        }

        /// <summary>
        /// Copies the most recent <paramref name="count"/> frames into dest, oldest first,
        /// collapsed to one channel according to <paramref name="mode"/>.
        /// Returns false if the ring has not yet seen that many frames.
        /// </summary>
        public bool Snapshot(double[] dest, int count, ChannelMode mode)
        {
            if (count > _capacity) return false;
            lock (_gate)
            {
                if (_total < count) return false;
                int mask = _capacity - 1;
                int start = (_write - count) & mask;
                for (int i = 0; i < count; i++)
                {
                    int idx = (start + i) & mask;
                    float l = _left[idx];
                    float r = _right[idx];
                    switch (mode)
                    {
                        case ChannelMode.Left:  dest[i] = l; break;
                        case ChannelMode.Right: dest[i] = r; break;
                        case ChannelMode.Side:  dest[i] = (l - r) * 0.5; break;
                        default:                dest[i] = (l + r) * 0.5; break;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Copies the most recent frames into separate left and right buffers under a
        /// single lock, so both channels are guaranteed to describe the same instant.
        /// </summary>
        public bool SnapshotStereo(double[] destLeft, double[] destRight, int count)
        {
            if (count > _capacity) return false;
            lock (_gate)
            {
                if (_total < count) return false;
                int mask = _capacity - 1;
                int start = (_write - count) & mask;
                for (int i = 0; i < count; i++)
                {
                    int idx = (start + i) & mask;
                    destLeft[i] = _left[idx];
                    destRight[i] = _right[idx];
                }
                return true;
            }
        }

        /// <summary>
        /// Reads frames starting at absolute position <paramref name="fromFrame"/> so a
        /// consumer can process every sample exactly once - required for metering, where
        /// gaps or double-counting would corrupt the integration.
        /// If the requested start has already been overwritten, reading resumes at the
        /// oldest frame still held.
        /// </summary>
        public int ReadRange(long fromFrame, double[] destLeft, double[] destRight,
                             int maxFrames, out long nextFrame)
        {
            lock (_gate)
            {
                long oldest = _total - _capacity;
                if (oldest < 0) oldest = 0;
                if (fromFrame < oldest) fromFrame = oldest;

                long available = _total - fromFrame;
                if (available <= 0) { nextFrame = _total; return 0; }

                int count = (int)Math.Min(available, maxFrames);
                if (count > destLeft.Length) count = destLeft.Length;
                if (count > destRight.Length) count = destRight.Length;

                int mask = _capacity - 1;
                int start = (int)((_write - (_total - fromFrame)) & mask);
                for (int i = 0; i < count; i++)
                {
                    int idx = (start + i) & mask;
                    destLeft[i] = _left[idx];
                    destRight[i] = _right[idx];
                }
                nextFrame = fromFrame + count;
                return count;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_left, 0, _capacity);
                Array.Clear(_right, 0, _capacity);
                _write = 0;
                _total = 0;
            }
        }

        public void Resize(int capacity)
        {
            lock (_gate)
            {
                _capacity = NextPow2(capacity);
                _left = new float[_capacity];
                _right = new float[_capacity];
                _write = 0;
                _total = 0;
            }
        }
    }
}

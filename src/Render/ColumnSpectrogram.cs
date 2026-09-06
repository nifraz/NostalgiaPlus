using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NostalgiaPlus.Render
{
    /// <summary>
    /// Spectrogram with frequency on the vertical axis and time on the horizontal,
    /// stored as a circular bitmap of columns.
    ///
    /// Same trick as <see cref="SpectrogramBuffer"/> rotated a quarter turn: one column
    /// is written per frame and a head index moves, so painting is two blits and cost is
    /// O(height) rather than O(width x height).
    ///
    /// Row 0 of the bitmap is the TOP of the display, and callers pass intensities
    /// indexed by increasing frequency, so the write flips them - low frequencies end up
    /// at the bottom, which is how everyone reads a spectrogram.
    /// </summary>
    public sealed class ColumnSpectrogram : IDisposable
    {
        private Bitmap _bmp;
        private int _width;   // time
        private int _height;  // frequency
        private int _head;    // column holding the newest slice
        private int[] _scratch = new int[0];

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }

        public ColumnSpectrogram(int width, int height) { Resize(width, height); }

        public void Resize(int width, int height)
        {
            if (width < 1) width = 1;
            if (height < 1) height = 1;
            if (_bmp != null && _width == width && _height == height) return;

            if (_bmp != null) _bmp.Dispose();
            _width = width;
            _height = height;
            _bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            _scratch = new int[height];
            _head = 0;
        }

        public void Clear(int argb)
        {
            if (_bmp == null) return;
            BitmapData bd = _bmp.LockBits(new Rectangle(0, 0, _width, _height),
                                          ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int[] row = new int[_width];
                for (int i = 0; i < _width; i++) row[i] = argb;
                for (int y = 0; y < _height; y++)
                    Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), _width);
            }
            finally { _bmp.UnlockBits(bd); }
            _head = 0;
        }

        /// <summary>
        /// Writes one time slice. <paramref name="values"/> holds 0..1 intensities indexed
        /// by increasing frequency; index 0 lands at the bottom of the display.
        /// </summary>
        public void PushColumn(double[] values, int count, int[] lut)
        {
            if (_bmp == null) return;
            if (count > _height) count = _height;

            _head = (_head - 1 + _width) % _width;

            int floorColour = lut[0];
            for (int i = 0; i < count; i++)
            {
                double v = values[i];
                int idx = (int)(v * 255.0 + 0.5);
                if (idx < 0) idx = 0; else if (idx > 255) idx = 255;
                _scratch[_height - 1 - i] = lut[idx];   // flip: low frequency at the bottom
            }
            for (int i = count; i < _height; i++) _scratch[_height - 1 - i] = floorColour;

            // Locking a single column hands back a tightly packed height-sized buffer.
            BitmapData bd = _bmp.LockBits(new Rectangle(_head, 0, 1, _height),
                                          ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                if (bd.Stride == 4)
                {
                    Marshal.Copy(_scratch, 0, bd.Scan0, _height);
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                        Marshal.WriteInt32(bd.Scan0, y * bd.Stride, _scratch[y]);
                }
            }
            finally { _bmp.UnlockBits(bd); }
        }

        /// <summary>
        /// Paints the ring into <paramref name="dest"/>. With
        /// <paramref name="newestOnRight"/> false the newest column sits at the left edge
        /// and history runs right; true mirrors it, which is what the left-hand channel
        /// needs so both halves meet at "now" in the centre of the screen.
        /// </summary>
        public void Draw(Graphics g, Rectangle dest, bool newestOnRight)
        {
            Draw(g, dest, newestOnRight, InterpolationMode.NearestNeighbor);
        }

        /// <summary>
        /// Nearest-neighbour keeps the 1:1 case exact; the bloom path passes Bilinear
        /// because it draws the ring straight into a small bitmap.
        /// </summary>
        public void Draw(Graphics g, Rectangle dest, bool newestOnRight, InterpolationMode mode)
        {
            if (_bmp == null || dest.Width <= 0 || dest.Height <= 0) return;

            InterpolationMode oldInterp = g.InterpolationMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.InterpolationMode = mode;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            GraphicsState state = null;
            Rectangle target = dest;
            if (newestOnRight)
            {
                // Mirror about the destination's right edge.
                state = g.Save();
                g.TranslateTransform(dest.Right, 0);
                g.ScaleTransform(-1, 1);
                target = new Rectangle(0, dest.Y, dest.Width, dest.Height);
            }

            int firstCount = _width - _head;
            if (firstCount > 0)
            {
                g.DrawImage(_bmp,
                    new Rectangle(target.X, target.Y, firstCount, target.Height),
                    new Rectangle(_head, 0, firstCount, _height),
                    GraphicsUnit.Pixel);
            }
            if (_head > 0)
            {
                g.DrawImage(_bmp,
                    new Rectangle(target.X + firstCount, target.Y, _head, target.Height),
                    new Rectangle(0, 0, _head, _height),
                    GraphicsUnit.Pixel);
            }

            if (state != null) g.Restore(state);
            g.InterpolationMode = oldInterp;
            g.PixelOffsetMode = oldOffset;
        }

        public void Dispose()
        {
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
        }
    }

    /// <summary>
    /// Rolling min/max envelope, one entry per rendered column, so the waveform lane
    /// shares an exact time axis with the spectrogram above it.
    /// </summary>
    public sealed class WaveformRing
    {
        private float[] _min;
        private float[] _max;
        private int _capacity;
        private int _head;

        public int Capacity { get { return _capacity; } }

        public WaveformRing(int capacity) { Resize(capacity); }

        public void Resize(int capacity)
        {
            if (capacity < 1) capacity = 1;
            if (_min != null && _capacity == capacity) return;
            _capacity = capacity;
            _min = new float[capacity];
            _max = new float[capacity];
            _head = 0;
        }

        public void Clear()
        {
            Array.Clear(_min, 0, _capacity);
            Array.Clear(_max, 0, _capacity);
            _head = 0;
        }

        public void Push(float lo, float hi)
        {
            _head = (_head - 1 + _capacity) % _capacity;
            _min[_head] = lo;
            _max[_head] = hi;
        }

        /// <summary>Column i counted backwards from "now" (0 = newest).</summary>
        public void Get(int age, out float lo, out float hi)
        {
            int idx = (_head + age) % _capacity;
            lo = _min[idx];
            hi = _max[idx];
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NostalgiaPlus.Render
{
    /// <summary>
    /// Scrolling spectrogram held as a circular bitmap.
    ///
    /// The usual way to scroll a spectrogram - shift every pixel up one row, then draw
    /// the new row - costs a full-surface copy every frame, which is what pins the
    /// original plugin around 23fps at this window size. Here the bitmap is a ring:
    /// each frame writes exactly one row and moves a head index, and painting is two
    /// blits regardless of how tall the panel is. Cost per frame is O(width), not
    /// O(width x height).
    /// </summary>
    public sealed class SpectrogramBuffer : IDisposable
    {
        private Bitmap _bmp;
        private int _width;
        private int _height;
        private int _head;      // buffer row holding the newest line
        private int _filled;
        private readonly int[] _rowScratch;

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }

        public SpectrogramBuffer(int width, int height)
        {
            _rowScratch = new int[8192];
            Resize(width, height);
        }

        public void Resize(int width, int height)
        {
            if (width < 1) width = 1;
            if (height < 1) height = 1;
            if (_bmp != null && _width == width && _height == height) return;

            if (_bmp != null) _bmp.Dispose();
            _width = width;
            _height = height;
            _bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            _head = 0;
            _filled = 0;
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
                    System.Runtime.InteropServices.Marshal.Copy(
                        row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), _width);
            }
            finally { _bmp.UnlockBits(bd); }
            _head = 0;
            _filled = 0;
        }

        /// <summary>
        /// Pushes one new line. <paramref name="values"/> holds 0..1 intensities, one
        /// per column; they are mapped through <paramref name="lut"/>.
        /// </summary>
        public void PushRow(double[] values, int count, int[] lut)
        {
            if (_bmp == null) return;
            if (count > _width) count = _width;

            _head = (_head - 1 + _height) % _height;
            if (_filled < _height) _filled++;

            int[] scratch = _rowScratch.Length >= _width ? _rowScratch : new int[_width];
            for (int x = 0; x < count; x++)
            {
                double v = values[x];
                int idx = (int)(v * 255.0 + 0.5);
                if (idx < 0) idx = 0; else if (idx > 255) idx = 255;
                scratch[x] = lut[idx];
            }
            for (int x = count; x < _width; x++) scratch[x] = lut[0];

            BitmapData bd = _bmp.LockBits(new Rectangle(0, _head, _width, 1),
                                          ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(scratch, 0, bd.Scan0, _width); }
            finally { _bmp.UnlockBits(bd); }
        }

        /// <summary>
        /// Paints the ring so the newest line sits at the top of <paramref name="dest"/>
        /// and time runs downward. Two blits, no per-pixel work.
        /// </summary>
        public void Draw(Graphics g, Rectangle dest)
        {
            if (_bmp == null) return;

            InterpolationMode oldInterp = g.InterpolationMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            int firstCount = _height - _head;      // rows from _head to the bottom of the buffer
            if (firstCount > 0)
            {
                g.DrawImage(_bmp,
                    new Rectangle(dest.X, dest.Y, dest.Width, firstCount),
                    new Rectangle(0, _head, _width, firstCount),
                    GraphicsUnit.Pixel);
            }
            if (_head > 0)
            {
                g.DrawImage(_bmp,
                    new Rectangle(dest.X, dest.Y + firstCount, dest.Width, _head),
                    new Rectangle(0, 0, _width, _head),
                    GraphicsUnit.Pixel);
            }

            g.InterpolationMode = oldInterp;
            g.PixelOffsetMode = oldOffset;
        }

        public void Dispose()
        {
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
        }
    }
}

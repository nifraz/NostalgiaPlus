using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using NostalgiaPlus.Dsp;

namespace NostalgiaPlus.Render
{
    /// <summary>How the instantaneous curve is drawn.</summary>
    public enum CurveStyle { Line, Bars, Led }

    /// <summary>Pattern drawn behind the curve.</summary>
    public enum GraphBackground { Plain, Lines, Grid, Chessboard }

    /// <summary>Bundled so the draw call does not need a dozen positional arguments.</summary>
    public sealed class CurveDrawOptions
    {
        public CurveStyle Style = CurveStyle.Line;
        public int BarSize = 6;
        public int LedSegment = 5;
        public bool ShowMax = true, ShowMin, ShowAvg;
        public bool SolidFill = true;
        public GraphBackground Background = GraphBackground.Lines;
        public bool ShowDbScale = true;
        public Font LabelFont;
        public double Alpha = 1.0;
    }

    /// <summary>
    /// One channel's display: a spectrum curve strip beside its own spectrogram, sharing
    /// a vertical frequency axis with low notes at the bottom.
    ///
    /// Both the docked panel and the fullscreen view lay out two of these side by side,
    /// so the two modes render through exactly the same code and cannot drift apart.
    /// New columns enter against the curve and age away from it, which keeps the
    /// instantaneous trace next to the slice that produced it.
    /// </summary>
    public sealed class ChannelPane : IDisposable
    {
        private ColumnSpectrogram _sg;
        private Bitmap _off, _glow;

        private double[] _raw = new double[0];      // straight from the analyser
        private double[] _shaped = new double[0];   // after smoothing
        private double[] _display = new double[0];  // after ballistics
        private double[] _intensity = new double[0];
        private readonly ExtremumTracker _ext = new ExtremumTracker();

        public Rectangle Bounds { get; private set; }
        public Rectangle CurveRect { get; private set; }
        public Rectangle SpectroRect { get; private set; }
        public bool CurveOnLeft { get; private set; }
        public string Label = "";

        /// <summary>Frequency bins, one per pixel row of the spectrogram.</summary>
        public int Bins { get { return SpectroRect.Height; } }

        public double[] Raw { get { return _raw; } }
        public double[] Display { get { return _display; } }
        public ExtremumTracker Extremes { get { return _ext; } }

        public void Layout(Rectangle bounds, int curveWidth, bool curveOnLeft, int[] lut)
        {
            if (bounds.Width < 8) bounds.Width = 8;
            if (bounds.Height < 8) bounds.Height = 8;
            if (curveWidth < 0) curveWidth = 0;
            if (curveWidth > bounds.Width - 16) curveWidth = Math.Max(0, bounds.Width - 16);

            Bounds = bounds;
            CurveOnLeft = curveOnLeft;

            int specW = Math.Max(1, bounds.Width - curveWidth);
            if (curveOnLeft)
            {
                CurveRect = new Rectangle(bounds.X, bounds.Y, curveWidth, bounds.Height);
                SpectroRect = new Rectangle(bounds.X + curveWidth, bounds.Y, specW, bounds.Height);
            }
            else
            {
                SpectroRect = new Rectangle(bounds.X, bounds.Y, specW, bounds.Height);
                CurveRect = new Rectangle(bounds.X + specW, bounds.Y, curveWidth, bounds.Height);
            }

            if (_sg == null) _sg = new ColumnSpectrogram(SpectroRect.Width, SpectroRect.Height);
            else _sg.Resize(SpectroRect.Width, SpectroRect.Height);
            if (lut != null) _sg.Clear(lut[0]);

            DisposeOffscreen();
            EnsureArrays(SpectroRect.Height);
        }

        private void EnsureArrays(int n)
        {
            if (_raw.Length == n) return;
            _raw = new double[n];
            _shaped = new double[n];
            _display = new double[n];
            _intensity = new double[n];
            for (int i = 0; i < n; i++) _display[i] = SpectrumAnalyzer.FloorDb;
            _ext.Resize(n);
        }

        private void DisposeOffscreen()
        {
            if (_off != null) { _off.Dispose(); _off = null; }
            if (_glow != null) { _glow.Dispose(); _glow = null; }
        }

        public void Reset(int[] lut)
        {
            _ext.Reset();
            for (int i = 0; i < _display.Length; i++) _display[i] = SpectrumAnalyzer.FloorDb;
            if (_sg != null && lut != null) _sg.Clear(lut[0]);
        }

        /// <summary>Shapes the frame, runs ballistics, and updates the min/max/average traces.</summary>
        public void Update(double dt, CurveInterpolation interp, FilteringAmount filter,
                           double attackMs, double releaseMs)
        {
            int n = _raw.Length;
            if (n == 0) return;

            CurveShaping.Smooth(_raw, _shaped, n, interp, filter);

            double a = 1.0 - Math.Exp(-dt / Math.Max(0.001, attackMs / 1000.0));
            double r = 1.0 - Math.Exp(-dt / Math.Max(0.001, releaseMs / 1000.0));
            for (int i = 0; i < n; i++)
            {
                double v = _shaped[i], c = _display[i];
                _display[i] = c + (v - c) * (v > c ? a : r);
            }
            _ext.Update(_shaped, n, dt);
        }

        /// <summary>Appends one time slice to the spectrogram.</summary>
        public void PushColumn(double floorDb, double ceilDb, int[] lut)
        {
            int n = _raw.Length;
            if (_sg == null || n == 0) return;
            double span = ceilDb - floorDb;
            if (span < 1) span = 1;
            double inv = 1.0 / span;
            for (int i = 0; i < n; i++)
            {
                double t = (_raw[i] - floorDb) * inv;
                _intensity[i] = t < 0 ? 0 : (t > 1 ? 1 : t);
            }
            _sg.PushColumn(_intensity, n, lut);
        }

        // ---------------- painting ----------------

        /// <summary>
        /// Draws the spectrogram, optionally with bloom. The glow is built from an
        /// offscreen composite rather than the ring bitmap: the ring stores columns
        /// rotated by a head index, so blurring it directly would bleed the newest
        /// column into the oldest.
        /// </summary>
        public void DrawSpectrogram(Graphics g, int[] lut, bool glow)
        {
            if (_sg == null || SpectroRect.Width <= 0) return;

            // Newest sits against the curve: on the far side when the curve is on the left.
            bool newestOnRight = !CurveOnLeft;

            if (!glow)
            {
                _sg.Draw(g, SpectroRect, newestOnRight);
                return;
            }

            if (_off == null || _off.Width != SpectroRect.Width || _off.Height != SpectroRect.Height)
            {
                if (_off != null) _off.Dispose();
                _off = new Bitmap(SpectroRect.Width, SpectroRect.Height, PixelFormat.Format32bppPArgb);
            }
            using (var go = Graphics.FromImage(_off))
            {
                go.Clear(Palette.Background(lut));
                _sg.Draw(go, new Rectangle(0, 0, SpectroRect.Width, SpectroRect.Height), newestOnRight);
            }
            g.DrawImageUnscaled(_off, SpectroRect.X, SpectroRect.Y);

            int gw = Math.Max(8, SpectroRect.Width / 8);
            int gh = Math.Max(8, SpectroRect.Height / 8);
            if (_glow == null || _glow.Width != gw || _glow.Height != gh)
            {
                if (_glow != null) _glow.Dispose();
                _glow = new Bitmap(gw, gh, PixelFormat.Format32bppArgb);
            }
            using (var gg = Graphics.FromImage(_glow))
            using (var attr = new ImageAttributes())
            {
                gg.CompositingMode = CompositingMode.SourceCopy;
                gg.Clear(Color.Transparent);
                // RGB boosted, alpha taken from luminance with a threshold, so only bright
                // material contributes and dark areas are left untouched.
                var m = new ColorMatrix(new float[][] {
                    new float[] { 1.6f, 0,    0,    0.55f, 0 },
                    new float[] { 0,    1.6f, 0,    0.55f, 0 },
                    new float[] { 0,    0,    1.6f, 0.55f, 0 },
                    new float[] { 0,    0,    0,    0f,    0 },
                    new float[] { 0,    0,    0,   -0.38f, 1 }
                });
                attr.SetColorMatrix(m);
                gg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                gg.DrawImage(_off, new Rectangle(0, 0, gw, gh),
                             0, 0, _off.Width, _off.Height, GraphicsUnit.Pixel, attr);
            }
            var old = g.InterpolationMode;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(_glow, SpectroRect, 0, 0, gw, gh, GraphicsUnit.Pixel);
            g.InterpolationMode = old;
        }

        /// <summary>
        /// Draws the curve strip. Amplitude grows from the pane's outer edge toward the
        /// spectrogram, so the trace points at the column it produced.
        /// </summary>
        public void DrawCurve(Graphics g, int[] lut, double floorDb, double ceilDb, CurveDrawOptions o)
        {
            if (CurveRect.Width <= 2 || _display.Length == 0) return;
            double span = Math.Max(1, ceilDb - floorDb);

            int baseX = CurveOnLeft ? CurveRect.Left : CurveRect.Right;
            int dir = CurveOnLeft ? 1 : -1;
            float amp = CurveRect.Width;

            Color hi = Palette.ColorAt(lut, 0.85);
            Color lo = Palette.ColorAt(lut, 0.35);

            DrawBackground(g, floorDb, ceilDb, span, baseX, dir, amp, o);

            var old = g.SmoothingMode;
            g.SmoothingMode = o.Style == CurveStyle.Line ? SmoothingMode.AntiAlias : SmoothingMode.None;

            if (o.Style == CurveStyle.Line)
                DrawLineStyle(g, baseX, dir, amp, floorDb, span, hi, lo, o.SolidFill);
            else
                DrawBarStyle(g, baseX, dir, amp, floorDb, span, lut, o.Style, o.BarSize, o.LedSegment);

            if (o.ShowMax) DrawTrace(g, _ext.Max, baseX, dir, amp, floorDb, span,
                                     Color.FromArgb(180, 255, 255, 255));
            if (o.ShowAvg) DrawTrace(g, _ext.Average, baseX, dir, amp, floorDb, span,
                                     Color.FromArgb(170, 130, 200, 255));
            if (o.ShowMin) DrawTrace(g, _ext.Min, baseX, dir, amp, floorDb, span,
                                     Color.FromArgb(140, 120, 120, 140));

            g.SmoothingMode = old;
        }

        /// <summary>dB step chosen so the strip carries roughly four to six lines.</summary>
        private static double DbStep(double span)
        {
            return span > 80 ? 20 : (span > 40 ? 12 : 6);
        }

        private void DrawBackground(Graphics g, double floorDb, double ceilDb, double span,
                                    int baseX, int dir, float amp, CurveDrawOptions o)
        {
            if (o.Background == GraphBackground.Chessboard)
            {
                int cell = Math.Max(8, CurveRect.Width / 6);
                using (var dark = new SolidBrush(Color.FromArgb((int)(26 * o.Alpha), 255, 255, 255)))
                    for (int y = 0; y * cell < CurveRect.Height; y++)
                        for (int x = 0; x * cell < CurveRect.Width; x++)
                        {
                            if (((x + y) & 1) == 0) continue;
                            int px = CurveRect.X + x * cell, py = CurveRect.Y + y * cell;
                            g.FillRectangle(dark, px, py,
                                            Math.Min(cell, CurveRect.Right - px),
                                            Math.Min(cell, CurveRect.Bottom - py));
                        }
                return;
            }
            if (o.Background == GraphBackground.Plain) return;

            double step = DbStep(span);
            bool labels = o.ShowDbScale && o.LabelFont != null && CurveRect.Width >= 58;

            using (var pen = new Pen(Color.FromArgb((int)(34 * o.Alpha), 255, 255, 255)))
            using (var brush = new SolidBrush(Color.FromArgb((int)(130 * o.Alpha), 215, 215, 222)))
            {
                for (double d = Math.Ceiling(floorDb / step) * step; d <= ceilDb; d += step)
                {
                    // dB maps along the amplitude axis, which runs horizontally here.
                    float x = XFor(d, baseX, dir, amp, floorDb, span);
                    g.DrawLine(pen, x, CurveRect.Top, x, CurveRect.Bottom);
                    if (labels)
                    {
                        string t = d.ToString("0");
                        SizeF sz = g.MeasureString(t, o.LabelFont);
                        float lx = x - sz.Width / 2;
                        if (lx < CurveRect.Left) lx = CurveRect.Left;
                        if (lx + sz.Width > CurveRect.Right) lx = CurveRect.Right - sz.Width;
                        g.DrawString(t, o.LabelFont, brush, lx, CurveRect.Bottom - sz.Height - 1);
                    }
                }

                if (o.Background == GraphBackground.Grid)
                {
                    int rows = 8;
                    for (int i = 1; i < rows; i++)
                    {
                        int y = CurveRect.Top + CurveRect.Height * i / rows;
                        g.DrawLine(pen, CurveRect.Left, y, CurveRect.Right, y);
                    }
                }
            }
        }

        /// <summary>
        /// Time ticks along the spectrogram. Age runs away from the curve, so under
        /// Mirror the two panes count outward in opposite directions.
        /// </summary>
        public void DrawTimeMarks(Graphics g, Font font, double rowsPerSecond, double alpha)
        {
            if (rowsPerSecond <= 0 || SpectroRect.Width < 40 || font == null) return;
            double visible = SpectroRect.Width / rowsPerSecond;
            double step = 1;
            double[] choices = { 1, 2, 5, 10, 15, 30, 60, 120 };
            for (int i = 0; i < choices.Length; i++)
            {
                step = choices[i];
                if (visible / step <= 7) break;
            }

            using (var pen = new Pen(Color.FromArgb((int)(30 * alpha), 255, 255, 255)))
            using (var brush = new SolidBrush(Color.FromArgb((int)(120 * alpha), 215, 215, 222)))
                for (double t = step; t < visible; t += step)
                {
                    int off = (int)(t * rowsPerSecond);
                    int x = CurveOnLeft ? SpectroRect.Left + off : SpectroRect.Right - 1 - off;
                    if (x < SpectroRect.Left || x >= SpectroRect.Right) continue;
                    g.DrawLine(pen, x, SpectroRect.Top, x, SpectroRect.Bottom);
                    string label = "-" + t.ToString("0") + "s";
                    SizeF sz = g.MeasureString(label, font);
                    g.DrawString(label, font, brush, x - sz.Width / 2, SpectroRect.Bottom - sz.Height - 2);
                }
        }

        private float XFor(double db, int baseX, int dir, float amp, double floorDb, double span)
        {
            double t = (db - floorDb) / span;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return (float)(baseX + dir * t * amp);
        }

        private void DrawLineStyle(Graphics g, int baseX, int dir, float amp,
                                   double floorDb, double span, Color hi, Color lo, bool solidFill)
        {
            int n = _display.Length;
            var pts = new PointF[n + 2];
            for (int y = 0; y < n; y++)
            {
                int i = n - 1 - y;      // top of the pane is the highest frequency
                pts[y] = new PointF(XFor(_display[i], baseX, dir, amp, floorDb, span),
                                    CurveRect.Y + y);
            }
            pts[n] = new PointF(baseX, CurveRect.Y + n - 1);
            pts[n + 1] = new PointF(baseX, CurveRect.Y);

            if (solidFill)
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(pts);
                    var grad = new Rectangle(CurveRect.X, CurveRect.Y,
                                             Math.Max(1, CurveRect.Width), 1);
                    Color a = CurveOnLeft ? Color.FromArgb(190, hi) : Color.FromArgb(25, lo);
                    Color b = CurveOnLeft ? Color.FromArgb(25, lo) : Color.FromArgb(190, hi);
                    using (var fill = new LinearGradientBrush(grad, a, b, LinearGradientMode.Horizontal))
                        g.FillPath(fill, path);
                }

            var line = new PointF[n];
            Array.Copy(pts, line, n);
            using (var pen = new Pen(Color.FromArgb(235, hi), 1.3f))
                g.DrawLines(pen, line);
        }

        private void DrawBarStyle(Graphics g, int baseX, int dir, float amp,
                                  double floorDb, double span, int[] lut,
                                  CurveStyle style, int barSize, int ledGap)
        {
            int n = _display.Length;
            if (barSize < 2) barSize = 2;
            int bars = Math.Max(1, n / barSize);

            for (int b = 0; b < bars; b++)
            {
                int from = b * barSize;
                int to = Math.Min(n, from + barSize);
                double peak = SpectrumAnalyzer.FloorDb;
                for (int i = from; i < to; i++) if (_display[i] > peak) peak = _display[i];

                double t = (peak - floorDb) / span;
                if (t < 0) t = 0; else if (t > 1) t = 1;

                int yTop = CurveRect.Y + (n - to);
                int h = Math.Max(1, to - from - 1);
                int len = (int)(t * amp);
                if (len < 1) continue;

                using (var brush = new SolidBrush(Palette.ColorAt(lut, 0.35 + 0.6 * t)))
                {
                    if (style == CurveStyle.Bars)
                    {
                        int x = CurveOnLeft ? baseX : baseX - len;
                        g.FillRectangle(brush, x, yTop, len, h);
                    }
                    else
                    {
                        // LED: fixed-pitch segments along the amplitude axis.
                        int seg = Math.Max(3, ledGap);
                        for (int p = 0; p + seg <= len; p += seg + 2)
                        {
                            int x = CurveOnLeft ? baseX + p : baseX - p - seg;
                            g.FillRectangle(brush, x, yTop, seg, h);
                        }
                    }
                }
            }
        }

        private void DrawTrace(Graphics g, double[] values, int baseX, int dir, float amp,
                               double floorDb, double span, Color colour)
        {
            int n = Math.Min(values.Length, _display.Length);
            if (n < 2) return;
            var pts = new PointF[n];
            for (int y = 0; y < n; y++)
            {
                int i = n - 1 - y;
                pts[y] = new PointF(XFor(values[i], baseX, dir, amp, floorDb, span),
                                    CurveRect.Y + y);
            }
            using (var pen = new Pen(colour, 1f))
                g.DrawLines(pen, pts);
        }

        public void Dispose()
        {
            if (_sg != null) { _sg.Dispose(); _sg = null; }
            DisposeOffscreen();
        }
    }
}

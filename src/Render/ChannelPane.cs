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
        /// <summary>Pixels to drop scales by so they clear a status line or overlay bar.</summary>
        public int TopInset;
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
        private Bitmap _glowSrc, _glow;
        // Graphics contexts are tied to their bitmap and cost real time to construct;
        // four per frame showed up as both cost and frame-to-frame jitter.
        private Graphics _glowSrcG, _glowG;
        private ImageAttributes _glowAttr;
        // Reused every frame: at 1080 rows these were ~9 KB allocations per trace per
        // pane per frame, which is pure GC pressure on the UI thread.
        private PointF[] _curveBuf = new PointF[0];
        private PointF[] _traceBuf = new PointF[0];

        private double[] _raw = new double[0];      // straight from the analyser
        private double[] _shaped = new double[0];   // after smoothing
        private double[] _display = new double[0];  // after ballistics
        private double[] _intensity = new double[0];
        private readonly ExtremumTracker _ext = new ExtremumTracker();

        // dB history parallel to the spectrogram columns. The bitmap only stores
        // palette colours, and the floor/ceiling move, so levels cannot be recovered
        // from it - without this, hovering over a column from ten seconds ago reads
        // today's spectrum.
        private float[] _hist = new float[0];
        private int _histHead, _histCols, _histBins;

        public Rectangle Bounds { get; private set; }
        public Rectangle CurveRect { get; private set; }
        public Rectangle SpectroRect { get; private set; }
        /// <summary>
        /// Strip reserved for the time and dB scales, empty when they are drawn over
        /// the image instead. Carved off this pane's own bounds, so the spectrogram and
        /// the curve both shrink by it and nothing is ever printed on top of data.
        /// </summary>
        public Rectangle LaneRect { get; private set; }
        public bool CurveOnLeft { get; private set; }
        public string Label = "";

        /// <summary>Frequency bins, one per pixel row of the spectrogram.</summary>
        public int Bins { get { return SpectroRect.Height; } }

        public double[] Raw { get { return _raw; } }
        public double[] Display { get { return _display; } }
        public ExtremumTracker Extremes { get { return _ext; } }

        public void Layout(Rectangle bounds, int curveWidth, bool curveOnLeft, int[] lut)
        {
            Layout(bounds, curveWidth, curveOnLeft, lut, 0, true);
        }

        /// <summary>
        /// Lays the pane out, optionally carving <paramref name="laneHeight"/> pixels off
        /// one end for the reserved scale strip.
        /// </summary>
        public void Layout(Rectangle bounds, int curveWidth, bool curveOnLeft, int[] lut,
                           int laneHeight, bool laneAtTop)
        {
            if (bounds.Width < 8) bounds.Width = 8;
            if (bounds.Height < 8) bounds.Height = 8;
            if (curveWidth < 0) curveWidth = 0;
            if (curveWidth > bounds.Width - 16) curveWidth = Math.Max(0, bounds.Width - 16);

            Bounds = bounds;
            CurveOnLeft = curveOnLeft;

            // Never let the strip eat the image: on a short docked panel the scales are
            // worth less than the pixels they would cost.
            if (laneHeight < 0) laneHeight = 0;
            if (laneHeight > bounds.Height / 4) laneHeight = 0;

            Rectangle body = bounds;
            if (laneHeight > 0)
            {
                LaneRect = laneAtTop
                    ? new Rectangle(bounds.X, bounds.Y, bounds.Width, laneHeight)
                    : new Rectangle(bounds.X, bounds.Bottom - laneHeight, bounds.Width, laneHeight);
                body = laneAtTop
                    ? new Rectangle(bounds.X, bounds.Y + laneHeight, bounds.Width, bounds.Height - laneHeight)
                    : new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - laneHeight);
            }
            else LaneRect = Rectangle.Empty;

            int specW = Math.Max(1, body.Width - curveWidth);
            if (curveOnLeft)
            {
                CurveRect = new Rectangle(body.X, body.Y, curveWidth, body.Height);
                SpectroRect = new Rectangle(body.X + curveWidth, body.Y, specW, body.Height);
            }
            else
            {
                SpectroRect = new Rectangle(body.X, body.Y, specW, body.Height);
                CurveRect = new Rectangle(body.X + specW, body.Y, curveWidth, body.Height);
            }

            if (_sg == null) _sg = new ColumnSpectrogram(SpectroRect.Width, SpectroRect.Height);
            else _sg.Resize(SpectroRect.Width, SpectroRect.Height);
            if (lut != null) _sg.Clear(lut[0]);

            DisposeOffscreen();
            EnsureArrays(SpectroRect.Height);

            _histCols = SpectroRect.Width;
            _histBins = SpectroRect.Height;
            int cells = _histCols * _histBins;
            if (_hist.Length != cells) _hist = new float[cells];
            for (int i = 0; i < cells; i++) _hist[i] = (float)SpectrumAnalyzer.FloorDb;
            _histHead = 0;
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
            if (_glowSrcG != null) { _glowSrcG.Dispose(); _glowSrcG = null; }
            if (_glowG != null) { _glowG.Dispose(); _glowG = null; }
            if (_glowSrc != null) { _glowSrc.Dispose(); _glowSrc = null; }
            if (_glow != null) { _glow.Dispose(); _glow = null; }
            if (_glowAttr != null) { _glowAttr.Dispose(); _glowAttr = null; }
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
            Update(dt, interp, filter, attackMs, releaseMs,
                   _ext.HoldDecayDbPerSecond, _ext.AverageSeconds);
        }

        public void Update(double dt, CurveInterpolation interp, FilteringAmount filter,
                           double attackMs, double releaseMs,
                           double holdDecayDbPerSec, double averageSeconds)
        {
            _ext.HoldDecayDbPerSecond = holdDecayDbPerSec;
            _ext.AverageSeconds = averageSeconds;
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

            if (_histCols > 0 && _hist.Length == _histCols * _histBins)
            {
                _histHead = (_histHead - 1 + _histCols) % _histCols;
                int b = _histHead * _histBins;
                int m = Math.Min(n, _histBins);
                for (int i = 0; i < m; i++) _hist[b + i] = (float)_raw[i];
            }
        }

        /// <summary>
        /// Level at a past column. <paramref name="age"/> is columns back from the
        /// newest slice; bin 0 is the lowest frequency.
        /// </summary>
        public bool TryHistory(int age, int bin, out double db)
        {
            db = SpectrumAnalyzer.FloorDb;
            if (_histCols <= 0 || age < 0 || age >= _histCols) return false;
            if (bin < 0 || bin >= _histBins) return false;
            if (_hist.Length != _histCols * _histBins) return false;
            db = _hist[((_histHead + age) % _histCols) * _histBins + bin];
            return true;
        }

        /// <summary>Columns back from "now" for a pixel, following this pane's direction.</summary>
        public int AgeAt(int x)
        {
            return CurveOnLeft ? x - SpectroRect.Left : SpectroRect.Right - 1 - x;
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
            _sg.Draw(g, SpectroRect, newestOnRight);
            if (!glow) return;

            // The bloom source is drawn straight into a small bitmap rather than
            // compositing at full size and downscaling afterwards. A high-quality
            // downscale costs in proportion to *source* pixels, and measured at roughly
            // two thirds of all paint time; scaling on the way in costs destination
            // pixels instead. Aliasing from the cheap filter is irrelevant because the
            // result is deliberately blurred on the way back up.
            int gw = Math.Max(8, SpectroRect.Width / 8);
            int gh = Math.Max(8, SpectroRect.Height / 8);
            if (_glow == null || _glow.Width != gw || _glow.Height != gh)
            {
                if (_glowSrcG != null) _glowSrcG.Dispose();
                if (_glowG != null) _glowG.Dispose();
                if (_glowSrc != null) _glowSrc.Dispose();
                if (_glow != null) _glow.Dispose();
                _glowSrc = new Bitmap(gw, gh, PixelFormat.Format32bppPArgb);
                _glow = new Bitmap(gw, gh, PixelFormat.Format32bppArgb);
                _glowSrcG = Graphics.FromImage(_glowSrc);
                _glowSrcG.CompositingMode = CompositingMode.SourceCopy;
                _glowG = Graphics.FromImage(_glow);
                _glowG.CompositingMode = CompositingMode.SourceCopy;
            }

            // 1. ring straight into a small bitmap - destination-sized work
            _glowSrcG.Clear(Palette.Background(lut));
            _sg.Draw(_glowSrcG, new Rectangle(0, 0, gw, gh), newestOnRight,
                     System.Drawing.Drawing2D.InterpolationMode.Bilinear);

            if (_glowAttr == null)
            {
                _glowAttr = new ImageAttributes();
                _glowAttr.SetColorMatrix(new ColorMatrix(new float[][] {
                    new float[] { 1.6f, 0,    0,    0.55f, 0 },
                    new float[] { 0,    1.6f, 0,    0.55f, 0 },
                    new float[] { 0,    0,    1.6f, 0.55f, 0 },
                    new float[] { 0,    0,    0,    0f,    0 },
                    new float[] { 0,    0,    0,   -0.38f, 1 }
                }));
            }

            // 2. threshold and luminance-to-alpha while the image is still small. Doing
            // this during the upscale instead costs a matrix multiply per *destination*
            // pixel - around 800k per pane per frame - and measured as the bulk of the
            // remaining bloom cost.
            _glowG.Clear(Color.Transparent);
            _glowG.DrawImage(_glowSrc, new Rectangle(0, 0, gw, gh),
                             0, 0, gw, gh, GraphicsUnit.Pixel, _glowAttr);

            // 3. plain upscale: one blit, no per-pixel maths.
            //
            // This blit is what bloom now costs: roughly a million alpha-blended
            // destination pixels at 1080p, about 12ms of a 24ms frame. Premultiplying
            // the layer and dropping to HighSpeed compositing quality were both tried
            // and measured no better, because the cost is the blend itself rather than
            // the filter or the pixel format. Blending fewer pixels is the only lever
            // left, and there is no general way to know which ones matter - so Glow
            // stays the first switch to reach for when frames drop.
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

            // Reference traces are thin and static; antialiasing a polyline with one
            // point per pixel row costs several milliseconds per frame for no real gain.
            if (o.ShowMax || o.ShowAvg || o.ShowMin)
            {
                g.SmoothingMode = SmoothingMode.None;
                if (o.ShowMax) DrawTrace(g, _ext.Max, baseX, dir, amp, floorDb, span,
                                         Color.FromArgb(180, 255, 255, 255));
                if (o.ShowAvg) DrawTrace(g, _ext.Average, baseX, dir, amp, floorDb, span,
                                         Color.FromArgb(170, 130, 200, 255));
                if (o.ShowMin) DrawTrace(g, _ext.Min, baseX, dir, amp, floorDb, span,
                                         Color.FromArgb(140, 120, 120, 140));
            }

            g.SmoothingMode = old;
        }

        /// <summary>
        /// Paints the reserved scale strip's ground. Drawn before the scales themselves
        /// so their labels sit on a flat surface rather than on the spectrogram.
        /// </summary>
        // Space claimed by the unit captions, so the numeric labels can step around
        // them instead of printing on top.
        private RectangleF _dbUnit, _timeUnit;

        public void DrawScaleLane(Graphics g, double alpha)
        {
            DrawScaleLane(g, alpha, null, null, null);
        }

        /// <summary>
        /// Paints the scale strip and names each scale's unit at the end it is measured
        /// from: level from the graph's baseline, time from the live edge. Either name
        /// may be null to leave that scale unlabelled.
        /// </summary>
        public void DrawScaleLane(Graphics g, double alpha, Font font,
                                  string levelUnit, string timeUnit)
        {
            _dbUnit = RectangleF.Empty;
            _timeUnit = RectangleF.Empty;
            if (LaneRect.Height <= 0 || alpha <= 0.004) return;
            using (var bg = new SolidBrush(Color.FromArgb((int)(255 * alpha), 13, 13, 16)))
                g.FillRectangle(bg, LaneRect);
            // Hairline on the edge facing the image, so the strip reads as a ruler
            // against the picture rather than as a gap in it.
            int edge = LaneAtTop ? LaneRect.Bottom - 1 : LaneRect.Top;
            using (var line = new Pen(Color.FromArgb((int)(60 * alpha), 255, 255, 255)))
                g.DrawLine(line, LaneRect.Left, edge, LaneRect.Right - 1, edge);

            if (font == null) return;
            using (var unit = new SolidBrush(Color.FromArgb((int)(190 * alpha), 150, 200, 245)))
            {
                // Level is measured from the baseline the bars grow out of, so its name
                // goes there; time is counted back from the live edge, so its name goes
                // against the newest column.
                if (levelUnit != null && CurveRect.Width >= 40)
                {
                    SizeF sz = g.MeasureString(levelUnit, font);
                    float x = CurveOnLeft ? CurveRect.Left + 2 : CurveRect.Right - sz.Width - 2;
                    float y = LaneTextY(sz);
                    g.DrawString(levelUnit, font, unit, x, y);
                    _dbUnit = new RectangleF(x - 3, y, sz.Width + 6, sz.Height);
                }
                if (timeUnit != null && SpectroRect.Width >= 60)
                {
                    SizeF sz = g.MeasureString(timeUnit, font);
                    float x = CurveOnLeft ? SpectroRect.Left + 2 : SpectroRect.Right - sz.Width - 2;
                    float y = LaneTextY(sz);
                    g.DrawString(timeUnit, font, unit, x, y);
                    _timeUnit = new RectangleF(x - 3, y, sz.Width + 6, sz.Height);
                }
            }
        }

        private bool LaneAtTop { get { return LaneRect.Top <= Bounds.Top; } }

        /// <summary>Vertical extent of a tick drawn inside the scale strip.</summary>
        private void LaneTick(out int y0, out int y1)
        {
            int len = Math.Max(3, LaneRect.Height / 4);
            if (LaneAtTop) { y0 = LaneRect.Bottom - 1 - len; y1 = LaneRect.Bottom - 1; }
            else { y0 = LaneRect.Top; y1 = LaneRect.Top + len; }
        }

        private float LaneTextY(SizeF sz)
        {
            int len = Math.Max(3, LaneRect.Height / 4);
            float free = LaneRect.Height - len;
            return LaneAtTop ? LaneRect.Top + (free - sz.Height) / 2f
                             : LaneRect.Top + len + (free - sz.Height) / 2f;
        }

        /// <summary>
        /// dB step chosen so the labels land roughly 55px apart. A fixed step gave a
        /// 45%-wide graph the same four labels as a narrow one, which wastes most of
        /// the space it was given.
        /// </summary>
        private static double DbStep(double span, int pixels)
        {
            double[] steps = { 3, 6, 12, 20, 30, 40 };
            if (pixels <= 0 || span <= 0) return 12;
            for (int i = 0; i < steps.Length; i++)
                if (pixels * steps[i] / span >= 55.0) return steps[i];
            return steps[steps.Length - 1];
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

            double step = DbStep(span, CurveRect.Width);
            bool labels = o.ShowDbScale && o.LabelFont != null &&
                          CurveRect.Width >= (LaneRect.Height > 0 ? 34 : 58);

            using (var pen = new Pen(Color.FromArgb((int)(34 * o.Alpha), 255, 255, 255)))
            using (var brush = new SolidBrush(Color.FromArgb((int)(215 * o.Alpha), 235, 235, 242)))
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

                        if (LaneRect.Height > 0)
                        {
                            // The strip is the scale's own space: no chip behind the
                            // text, and nothing of the graph hidden by it.
                            int t0, t1;
                            LaneTick(out t0, out t1);
                            using (var tick = new Pen(Color.FromArgb((int)(110 * o.Alpha), 255, 255, 255)))
                                g.DrawLine(tick, x, t0, x, t1);
                            // The unit caption owns its corner; a number printed over it
                            // would read as neither.
                            if (_dbUnit.Width == 0 ||
                                lx + sz.Width < _dbUnit.Left || lx > _dbUnit.Right)
                                g.DrawString(t, o.LabelFont, brush, lx, LaneTextY(sz));
                        }
                        else
                        {
                            // Overlaid: the high-frequency end is the quiet end of most
                            // material, so the scale sits there rather than competing
                            // with the bass.
                            float ly = CurveRect.Top + 3 + o.TopInset;
                            using (var chip = new SolidBrush(Color.FromArgb((int)(170 * o.Alpha), 8, 8, 11)))
                                g.FillRectangle(chip, lx - 2, ly - 1, sz.Width + 4, sz.Height + 1);
                            g.DrawString(t, o.LabelFont, brush, lx, ly);
                        }
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
        public void DrawTimeMarks(Graphics g, Font font, double rowsPerSecond, double alpha, int topInset)
        {
            if (rowsPerSecond <= 0 || SpectroRect.Width < 40 || font == null) return;
            double visible = SpectroRect.Width / rowsPerSecond;
            // Aim for a mark every 85px or so rather than a fixed count: a 1900px
            // spectrogram and a 300px one should not carry the same seven labels.
            double step = 120;
            double[] choices = { 1, 2, 5, 10, 15, 30, 60, 120 };
            for (int i = 0; i < choices.Length; i++)
                if (choices[i] * rowsPerSecond >= 85.0) { step = choices[i]; break; }

            using (var pen = new Pen(Color.FromArgb((int)(30 * alpha), 255, 255, 255)))
            using (var brush = new SolidBrush(Color.FromArgb((int)(215 * alpha), 235, 235, 242)))
            using (var chip = new SolidBrush(Color.FromArgb((int)(170 * alpha), 8, 8, 11)))
            using (var tickPen = new Pen(Color.FromArgb((int)(110 * alpha), 255, 255, 255)))
                for (double t = step; t < visible; t += step)
                {
                    int off = (int)(t * rowsPerSecond);
                    int x = CurveOnLeft ? SpectroRect.Left + off : SpectroRect.Right - 1 - off;
                    if (x < SpectroRect.Left || x >= SpectroRect.Right) continue;
                    g.DrawLine(pen, x, SpectroRect.Top, x, SpectroRect.Bottom);
                    string label = "-" + t.ToString("0") + "s";
                    SizeF sz = g.MeasureString(label, font);
                    float lx = x - sz.Width / 2;
                    if (lx < SpectroRect.Left) lx = SpectroRect.Left;
                    if (lx + sz.Width > SpectroRect.Right) lx = SpectroRect.Right - sz.Width;
                    if (LaneRect.Height > 0)
                    {
                        int t0, t1;
                        LaneTick(out t0, out t1);
                        g.DrawLine(tickPen, x, t0, x, t1);
                        if (_timeUnit.Width == 0 ||
                            lx + sz.Width < _timeUnit.Left || lx > _timeUnit.Right)
                            g.DrawString(label, font, brush, lx, LaneTextY(sz));
                    }
                    else
                    {
                        float ly = SpectroRect.Top + 3 + topInset;
                        g.FillRectangle(chip, lx - 2, ly - 1, sz.Width + 4, sz.Height + 1);
                        g.DrawString(label, font, brush, lx, ly);
                    }
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
            if (_curveBuf.Length < n + 2) _curveBuf = new PointF[n + 2];
            PointF[] pts = _curveBuf;
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

            using (var pen = new Pen(Color.FromArgb(235, hi), 1.3f))
                g.DrawLines(pen, SubArray(pts, n));
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
            if (_traceBuf.Length < n) _traceBuf = new PointF[n];
            PointF[] pts = _traceBuf;
            for (int y = 0; y < n; y++)
            {
                int i = n - 1 - y;
                pts[y] = new PointF(XFor(values[i], baseX, dir, amp, floorDb, span),
                                    CurveRect.Y + y);
            }
            using (var pen = new Pen(colour, 1f))
                g.DrawLines(pen, SubArray(pts, n));
        }

        private PointF[] _exact = new PointF[0];

        /// <summary>DrawLines needs an exactly sized array; this keeps one around.</summary>
        private PointF[] SubArray(PointF[] src, int n)
        {
            if (_exact.Length != n) _exact = new PointF[n];
            Array.Copy(src, _exact, n);
            return _exact;
        }

        public void Dispose()
        {
            if (_sg != null) { _sg.Dispose(); _sg = null; }
            DisposeOffscreen();
        }
    }
}

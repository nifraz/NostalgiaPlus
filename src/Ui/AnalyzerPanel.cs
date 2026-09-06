using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NostalgiaPlus.Audio;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Render;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// The docked panel: frequency horizontal, time vertical, newest at the top.
    ///
    /// Both channels are shown - a shared curve pane carrying left and right, then two
    /// stacked spectrogram lanes, left above right. A short wide strip has no room for a
    /// vertical frequency axis, so this keeps its original shape rather than adopting the
    /// fullscreen layout.
    /// </summary>
    public sealed class AnalyzerPanel : UserControl
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private struct GridLine
        {
            public double Freq;
            public string Label;
            public bool Major;
            public GridLine(double f, string label, bool major) { Freq = f; Label = label; Major = major; }
        }

        private readonly Settings _settings;
        private readonly string _storageDir;
        private readonly SpectrumAnalyzer _analyzer = new SpectrumAnalyzer();
        private readonly DynamicRange _range = new DynamicRange();
        private readonly object _gate = new object();

        private LoopbackCapture _capture;
        private SpectrogramBuffer _sgramL, _sgramR;
        private FrequencyMap _map;
        private int[] _lut;

        private double[] _dbL = new double[0], _dbR = new double[0];
        private double[] _smoothL = new double[0], _smoothR = new double[0];
        private double[] _peakL = new double[0], _peakR = new double[0];
        private double[] _curveL = new double[0], _curveR = new double[0];
        private double[] _curvePeakL = new double[0], _curvePeakR = new double[0];
        private double[] _intensity = new double[0];

        private Thread _worker;
        private volatile bool _running;
        private volatile bool _frozen;
        private volatile bool _invalidatePending;
        private volatile bool _paused;
        private int _scrollTick;

        private Rectangle _curveRect, _rulerRect, _sgramLRect, _sgramRRect, _barRect;

        private Font _font, _fontSmall;
        private double _fps, _lastAnalysisMs;
        private double _displayFloor = -95, _displayCeiling = -5;
        private int _mouseX = -1, _mouseY = -1;
        private bool _mouseIn;
        private FullscreenView _fullscreen;
        private bool _ownsCapture = true;

        /// <summary>Supplies {title, artist, album} for the fullscreen overlay.</summary>
        public NowPlayingProvider NowPlaying { get; set; }

        private const int RulerHeight = 17;
        private const int ColorBarWidth = 46;
        private const int LaneGap = 2;

        public AnalyzerPanel(Settings settings, string storageDir)
        {
            _settings = settings;
            _storageDir = storageDir;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            // Without Selectable the control never takes focus, and ProcessCmdKey never
            // runs - which is why F11 and Space appeared to do nothing.
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.Black;
            _font = new Font("Segoe UI", 8f);
            _fontSmall = new Font("Segoe UI", 7f);
            _lut = Palette.BuildLut(_settings.Palette);

            var menu = new ContextMenuStrip();
            menu.Opening += delegate
            {
                MenuFactory.Populate(menu, _settings, new MenuFactory.Options
                {
                    IsFullscreen = false,
                    ScrollPixels = Math.Max(1, _sgramLRect.Height),
                    IsFrozen = delegate { return _frozen; },
                    ToggleFreeze = delegate { _frozen = !_frozen; Invalidate(); },
                    ToggleFullscreen = ToggleFullscreen,
                    Changed = OnSettingsChanged
                });
            };
            ContextMenuStrip = menu;
        }

        private void OnSettingsChanged(bool rebuildGeometry)
        {
            _lut = Palette.BuildLut(_settings.Palette);
            _range.Reset();
            if (rebuildGeometry) RebuildGeometry();
            _settings.Save(_storageDir);
            Invalidate();
        }

        // ---------------- lifecycle ----------------

        /// <summary>
        /// Supplies an externally owned capture instead of opening one. Used by the render
        /// harness to drive the panel from synthetic audio, and available in production if
        /// two views should ever share a single stream.
        /// </summary>
        public void UseCapture(LoopbackCapture capture)
        {
            _capture = capture;
            _ownsCapture = false;
        }

        public void StartCapture()
        {
            if (_capture == null) { _capture = new LoopbackCapture(); _ownsCapture = true; }
            if (_ownsCapture && _settings.UseLoopback && !_capture.IsRunning) _capture.Start();

            if (!_running)
            {
                _running = true;
                _worker = new Thread(WorkerLoop);
                _worker.IsBackground = true;
                _worker.Name = "NostalgiaPlus.Analysis";
                _worker.Start();
            }
        }

        public void StopCapture()
        {
            _running = false;
            Thread t = _worker;
            if (t != null && t.IsAlive) t.Join(1000);
            _worker = null;
            if (_capture != null && _ownsCapture) { _capture.Stop(); _capture.Dispose(); }
            _capture = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopCapture();
                if (_sgramL != null) { _sgramL.Dispose(); _sgramL = null; }
                if (_sgramR != null) { _sgramR.Dispose(); _sgramR = null; }
                if (_font != null) _font.Dispose();
                if (_fontSmall != null) _fontSmall.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Re-primes auto-ranging so a new track is scaled on its own material.</summary>
        public void NotifyTrackChanged()
        {
            _range.Reset();
            FullscreenView fs = _fullscreen;
            if (fs != null && !fs.IsDisposed)
            {
                try { fs.BeginInvoke((MethodInvoker)fs.RefreshNowPlaying); } catch { }
            }
        }

        /// <summary>
        /// Opens the mirrored stereo view on this panel's monitor, or closes it if it is
        /// already up. The panel's own analysis pauses while it is covered.
        /// </summary>
        public void ToggleFullscreen()
        {
            if (_fullscreen != null && !_fullscreen.IsDisposed) { _fullscreen.Close(); return; }
            if (_capture == null) return;

            var view = new FullscreenView(_settings, _storageDir, _capture, NowPlaying);
            view.FormClosed += delegate
            {
                _paused = false;
                _fullscreen = null;
                _range.Reset();
                try { Invalidate(); } catch { }
            };
            _fullscreen = view;
            _paused = true;
            view.ShowOn(this);
        }

        // ---------------- geometry ----------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildGeometry();
        }

        private void RebuildGeometry()
        {
            lock (_gate)
            {
                int w = Math.Max(1, ClientSize.Width);
                int h = Math.Max(1, ClientSize.Height);
                int barW = _settings.ShowColorBar ? ColorBarWidth : 0;
                int plotW = Math.Max(1, w - barW);

                int curveH = _settings.ShowCurve ? (int)(h * _settings.CurveRatio) : 0;
                if (curveH > h - RulerHeight - 24) curveH = Math.Max(0, h - RulerHeight - 24);
                int sgramY = curveH + RulerHeight;
                int sgramH = Math.Max(2, h - sgramY);
                int laneH = Math.Max(1, (sgramH - LaneGap) / 2);

                _curveRect = new Rectangle(0, 0, plotW, curveH);
                _rulerRect = new Rectangle(0, curveH, plotW, RulerHeight);
                _sgramLRect = new Rectangle(0, sgramY, plotW, laneH);
                _sgramRRect = new Rectangle(0, sgramY + laneH + LaneGap, plotW,
                                            Math.Max(1, h - (sgramY + laneH + LaneGap)));
                _barRect = new Rectangle(plotW, 0, barW, h);

                double nyq = (_capture != null ? _capture.SampleRate : 48000) * 0.5;
                double fMax = Math.Min(_settings.FMax, nyq);
                double fMin = _settings.Scale == FreqScale.Linear ? Math.Max(0, _settings.FMin)
                                                                 : Math.Max(10.0, _settings.FMin);
                _map = new FrequencyMap(_settings.Scale, plotW, fMin, fMax);

                if (_sgramL == null) _sgramL = new SpectrogramBuffer(plotW, _sgramLRect.Height);
                else _sgramL.Resize(plotW, _sgramLRect.Height);
                if (_sgramR == null) _sgramR = new SpectrogramBuffer(plotW, _sgramRRect.Height);
                else _sgramR.Resize(plotW, _sgramRRect.Height);
                _sgramL.Clear(_lut[0]);
                _sgramR.Clear(_lut[0]);

                EnsureArrays(plotW);
            }
        }

        private void EnsureArrays(int n)
        {
            if (_dbL.Length == n) return;
            _dbL = new double[n]; _dbR = new double[n];
            _smoothL = new double[n]; _smoothR = new double[n];
            _peakL = new double[n]; _peakR = new double[n];
            _curveL = new double[n]; _curveR = new double[n];
            _curvePeakL = new double[n]; _curvePeakR = new double[n];
            _intensity = new double[n];
            for (int i = 0; i < n; i++)
            {
                _smoothL[i] = SpectrumAnalyzer.FloorDb; _smoothR[i] = SpectrumAnalyzer.FloorDb;
                _peakL[i] = SpectrumAnalyzer.FloorDb; _peakR[i] = SpectrumAnalyzer.FloorDb;
            }
        }

        // ---------------- analysis thread ----------------

        private void WorkerLoop()
        {
            timeBeginPeriod(1);
            var sw = Stopwatch.StartNew();
            double last = sw.Elapsed.TotalSeconds;
            var frameTimer = new Stopwatch();
            try
            {
                while (_running)
                {
                    int fps = _settings.TargetFps;
                    if (fps < 10) fps = 10; else if (fps > 120) fps = 120;
                    double target = 1.0 / fps;

                    double now = sw.Elapsed.TotalSeconds;
                    double dt = now - last;
                    last = now;
                    if (dt > 0.25) dt = 0.25;

                    frameTimer.Restart();
                    try { AnalyseOnce(dt); }
                    catch { /* never let a transient render error kill the thread */ }
                    frameTimer.Stop();
                    _lastAnalysisMs = frameTimer.Elapsed.TotalMilliseconds;
                    if (dt > 0) _fps = _fps * 0.9 + (1.0 / dt) * 0.1;

                    if (!_invalidatePending && IsHandleCreated)
                    {
                        _invalidatePending = true;
                        try { BeginInvoke((MethodInvoker)RequestRepaint); }
                        catch { _invalidatePending = false; }
                    }

                    int sleep = (int)((target - (sw.Elapsed.TotalSeconds - now)) * 1000.0);
                    if (sleep > 0) Thread.Sleep(sleep);
                }
            }
            finally { timeEndPeriod(1); }
        }

        private void RequestRepaint()
        {
            _invalidatePending = false;
            Invalidate();
        }

        private void AnalyseOnce(double dt)
        {
            if (_paused) return;

            FrequencyMap map = _map;
            LoopbackCapture cap = _capture;
            if (map == null || cap == null || _sgramL == null || _sgramR == null) return;

            _analyzer.Configure(cap.SampleRate, _settings.Quality, _settings.Window);

            int n = map.Width;
            if (_dbL.Length < n) return;

            if (!_analyzer.ComputeStereo(cap.Ring, map, _dbL, _dbR,
                                         _settings.Aggregate, _settings.TiltDbPerOctave))
                return;

            double aCoef = 1.0 - Math.Exp(-dt / Math.Max(0.001, _settings.AttackMs / 1000.0));
            double rCoef = 1.0 - Math.Exp(-dt / Math.Max(0.001, _settings.ReleaseMs / 1000.0));
            double peakDrop = _settings.PeakDecayDbPerSec * dt;

            for (int i = 0; i < n; i++)
            {
                double v = _dbL[i], c = _smoothL[i];
                c += (v - c) * (v > c ? aCoef : rCoef);
                _smoothL[i] = c;
                double p = _peakL[i] - peakDrop;
                _peakL[i] = c > p ? c : p;

                v = _dbR[i]; c = _smoothR[i];
                c += (v - c) * (v > c ? aCoef : rCoef);
                _smoothR[i] = c;
                p = _peakR[i] - peakDrop;
                _peakR[i] = c > p ? c : p;
            }

            double floorDb, ceilDb;
            if (_settings.AdaptiveRange)
            {
                _range.Observe(_dbL, n, 0.94);
                _range.Observe(_dbR, n, 1.0);
                _range.Update(dt);
                floorDb = _range.Floor; ceilDb = _range.Ceiling;
            }
            else { floorDb = _settings.FloorDb; ceilDb = _settings.CeilingDb; }
            if (ceilDb - floorDb < 1) ceilDb = floorDb + 1;
            double inv = 1.0 / (ceilDb - floorDb);

            bool push = !_frozen;
            int div = _settings.ScrollDivider;
            if (div > 1)
            {
                _scrollTick++;
                if (_scrollTick < div) push = false; else _scrollTick = 0;
            }

            lock (_gate)
            {
                if (push)
                {
                    for (int i = 0; i < n; i++)
                    {
                        double t = (_dbL[i] - floorDb) * inv;
                        _intensity[i] = t < 0 ? 0 : (t > 1 ? 1 : t);
                    }
                    _sgramL.PushRow(_intensity, n, _lut);
                    for (int i = 0; i < n; i++)
                    {
                        double t = (_dbR[i] - floorDb) * inv;
                        _intensity[i] = t < 0 ? 0 : (t > 1 ? 1 : t);
                    }
                    _sgramR.PushRow(_intensity, n, _lut);
                }
                Array.Copy(_smoothL, _curveL, n);
                Array.Copy(_smoothR, _curveR, n);
                Array.Copy(_peakL, _curvePeakL, n);
                Array.Copy(_peakR, _curvePeakR, n);
                _displayFloor = floorDb;
                _displayCeiling = ceilDb;
            }
        }

        // ---------------- painting ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Palette.Background(_lut));

            lock (_gate)
            {
                if (_sgramL != null && _sgramLRect.Height > 0) _sgramL.Draw(g, _sgramLRect);
                if (_sgramR != null && _sgramRRect.Height > 0) _sgramR.Draw(g, _sgramRRect);
            }

            FrequencyMap map = _map;
            if (map == null) return;

            List<GridLine> lines = BuildGridLines(map);

            if (_settings.ShowGrid) DrawGridOverSpectrogram(g, map, lines);
            DrawLaneLabels(g);
            if (_settings.ShowCurve && _curveRect.Height > 4) DrawCurve(g, map, lines);
            DrawRuler(g, map, lines);
            if (_settings.ShowColorBar && _barRect.Width > 0) DrawColorBar(g);
            if (_settings.ShowStatus) DrawStatus(g);
            if (_settings.ShowHud && _mouseIn) DrawHud(g, map);
        }

        private List<GridLine> BuildGridLines(FrequencyMap map)
        {
            var list = new List<GridLine>();
            if (map.Scale == FreqScale.Note)
            {
                double pxPerOctave = map.Width / Math.Log(map.FMax / map.FMin, 2.0);
                bool semitones = pxPerOctave > 150;
                for (int midi = 12; midi <= 132; midi++)
                {
                    double f = FrequencyMap.MidiToFreq(midi);
                    if (f < map.FMin || f > map.FMax) continue;
                    bool isC = (midi % 12) == 0;
                    if (!isC && !semitones) continue;
                    list.Add(new GridLine(f, isC ? "C" + ((midi / 12) - 1) : null, isC));
                }
            }
            else if (map.Scale == FreqScale.Log)
            {
                int[] mult = { 1, 2, 3, 5 };
                for (int dec = 1; dec <= 100000; dec *= 10)
                    for (int m = 0; m < mult.Length; m++)
                    {
                        double f = dec * mult[m];
                        if (f < map.FMin || f > map.FMax) continue;
                        bool major = mult[m] == 1;
                        list.Add(new GridLine(f, major ? FormatHz(f) : null, major));
                    }
            }
            else
            {
                double step = map.FMax > 30000 ? 5000 : 2000;
                for (double f = 0; f <= map.FMax; f += step)
                {
                    if (f < map.FMin) continue;
                    list.Add(new GridLine(f, FormatHz(f), (Math.Round(f / step) % 5) == 0));
                }
            }
            return list;
        }

        private static string FormatHz(double f)
        {
            if (f <= 0) return "0";
            if (f >= 1000) return (f / 1000.0).ToString(f % 1000 == 0 ? "0" : "0.#") + "k";
            return f.ToString("0");
        }

        private void DrawGridOverSpectrogram(Graphics g, FrequencyMap map, List<GridLine> lines)
        {
            int top = _sgramLRect.Top, bottom = _sgramRRect.Bottom;
            if (bottom <= top) return;
            using (var major = new Pen(Color.FromArgb(46, 255, 255, 255)))
            using (var minor = new Pen(Color.FromArgb(20, 255, 255, 255)))
            {
                foreach (GridLine gl in lines)
                {
                    int x = (int)Math.Round(map.FreqToX(gl.Freq));
                    if (x < 0 || x >= _sgramLRect.Width) continue;
                    g.DrawLine(gl.Major ? major : minor, x, top, x, bottom);
                }
            }

            double rowsPerSecond = (double)_settings.TargetFps / Math.Max(1, _settings.ScrollDivider);
            if (rowsPerSecond <= 0) return;
            using (var timePen = new Pen(Color.FromArgb(30, 255, 255, 255)))
            {
                for (int s = 1; s * rowsPerSecond < _sgramLRect.Height; s++)
                {
                    int dy = (int)(s * rowsPerSecond);
                    g.DrawLine(timePen, 0, _sgramLRect.Top + dy, _sgramLRect.Width, _sgramLRect.Top + dy);
                    g.DrawLine(timePen, 0, _sgramRRect.Top + dy, _sgramRRect.Width, _sgramRRect.Top + dy);
                }
            }
        }

        private void DrawLaneLabels(Graphics g)
        {
            if (!_settings.ShowLabels) return;
            using (var back = new SolidBrush(Color.FromArgb(150, 8, 8, 10)))
            using (var brush = new SolidBrush(Color.FromArgb(220, 240, 240, 245)))
            using (var divider = new Pen(Color.FromArgb(90, 255, 255, 255)))
            {
                g.FillRectangle(back, 2, _sgramLRect.Top + 2, 15, 13);
                g.DrawString("L", _fontSmall, brush, 3, _sgramLRect.Top + 1);
                g.FillRectangle(back, 2, _sgramRRect.Top + 2, 15, 13);
                g.DrawString("R", _fontSmall, brush, 3, _sgramRRect.Top + 1);
                g.DrawLine(divider, 0, _sgramRRect.Top - 1, _sgramRRect.Width, _sgramRRect.Top - 1);
            }
        }

        private void DrawCurve(Graphics g, FrequencyMap map, List<GridLine> lines)
        {
            Rectangle r = _curveRect;
            using (var bg = new SolidBrush(Color.FromArgb(255, 12, 12, 14)))
                g.FillRectangle(bg, r);

            double floorDb, ceilDb;
            lock (_gate) { floorDb = _displayFloor; ceilDb = _displayCeiling; }
            double span = Math.Max(1, ceilDb - floorDb);

            using (var pen = new Pen(Color.FromArgb(38, 255, 255, 255)))
            using (var brush = new SolidBrush(Color.FromArgb(130, 220, 220, 220)))
            {
                double stepDb = span > 80 ? 20 : (span > 40 ? 12 : 6);
                double startDb = Math.Ceiling(floorDb / stepDb) * stepDb;
                for (double d = startDb; d <= ceilDb; d += stepDb)
                {
                    int y = r.Bottom - (int)((d - floorDb) / span * r.Height);
                    if (y < r.Top || y > r.Bottom) continue;
                    g.DrawLine(pen, r.Left, y, r.Right, y);
                    if (_settings.ShowLabels)
                        g.DrawString(d.ToString("0") + " dB", _fontSmall, brush, r.Right - 46, y - 12);
                }
            }

            using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255)))
                foreach (GridLine gl in lines)
                {
                    if (!gl.Major) continue;
                    int x = (int)Math.Round(map.FreqToX(gl.Freq));
                    if (x < 0 || x >= r.Width) continue;
                    g.DrawLine(pen, x, r.Top, x, r.Bottom);
                }

            int n = Math.Min(map.Width, _curveL.Length);
            if (n < 2) return;

            var ptsL = new PointF[n];
            var ptsR = new PointF[n];
            var peakL = new PointF[n];
            var peakR = new PointF[n];
            lock (_gate)
            {
                for (int i = 0; i < n; i++)
                {
                    ptsL[i] = new PointF(i, (float)YFor(_curveL[i], floorDb, span, r));
                    ptsR[i] = new PointF(i, (float)YFor(_curveR[i], floorDb, span, r));
                    peakL[i] = new PointF(i, (float)YFor(_curvePeakL[i], floorDb, span, r));
                    peakR[i] = new PointF(i, (float)YFor(_curvePeakR[i], floorDb, span, r));
                }
            }

            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color hi = Palette.ColorAt(_lut, 0.85);
            Color lo = Palette.ColorAt(_lut, 0.35);
            Color rightColour = Color.FromArgb(225, 150, 210, 255);

            // Left is the filled body; right is drawn as a contrasting line over it, so
            // the two channels stay distinguishable without doubling the ink.
            using (var path = new GraphicsPath())
            {
                var poly = new PointF[n + 2];
                Array.Copy(ptsL, poly, n);
                poly[n] = new PointF(n - 1, r.Bottom);
                poly[n + 1] = new PointF(0, r.Bottom);
                path.AddPolygon(poly);
                using (var fill = new LinearGradientBrush(
                           new Rectangle(r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height)),
                           Color.FromArgb(170, hi), Color.FromArgb(30, lo), LinearGradientMode.Vertical))
                    g.FillPath(fill, path);
            }

            if (_settings.PeakHold)
                using (var p = new Pen(Color.FromArgb(70, 255, 255, 255)))
                {
                    g.DrawLines(p, peakL);
                    g.DrawLines(p, peakR);
                }

            using (var penL = new Pen(Color.FromArgb(240, hi), 1.2f))
                g.DrawLines(penL, ptsL);
            using (var penR = new Pen(rightColour, 1.2f))
                g.DrawLines(penR, ptsR);

            if (_settings.ShowLabels)
                using (var bl = new SolidBrush(Color.FromArgb(230, hi)))
                using (var br = new SolidBrush(rightColour))
                {
                    // Sit below the status line rather than under it.
                    float ly = r.Top + (_settings.ShowStatus ? 15 : 1);
                    g.DrawString("L", _fontSmall, bl, r.Left + 3, ly);
                    g.DrawString("R", _fontSmall, br, r.Left + 14, ly);
                }

            g.SmoothingMode = old;
        }

        private static double YFor(double db, double floorDb, double span, Rectangle r)
        {
            double t = (db - floorDb) / span;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return r.Bottom - t * r.Height;
        }

        private void DrawRuler(Graphics g, FrequencyMap map, List<GridLine> lines)
        {
            Rectangle r = _rulerRect;
            using (var bg = new SolidBrush(Color.FromArgb(255, 18, 18, 20)))
                g.FillRectangle(bg, r);
            using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255)))
            using (var brush = new SolidBrush(Color.FromArgb(205, 225, 225, 228)))
            {
                foreach (GridLine gl in lines)
                {
                    int x = (int)Math.Round(map.FreqToX(gl.Freq));
                    if (x < 0 || x >= r.Width) continue;
                    g.DrawLine(pen, x, r.Bottom - (gl.Major ? 6 : 3), x, r.Bottom);
                    if (gl.Label != null && _settings.ShowLabels)
                    {
                        SizeF sz = g.MeasureString(gl.Label, _fontSmall);
                        float lx = x - sz.Width / 2;
                        if (lx < 0) lx = 0;
                        if (lx + sz.Width > r.Width) lx = r.Width - sz.Width;
                        g.DrawString(gl.Label, _fontSmall, brush, lx, r.Top - 1);
                    }
                }
            }
        }

        private void DrawColorBar(Graphics g)
        {
            Rectangle r = _barRect;
            using (var bg = new SolidBrush(Color.FromArgb(255, 14, 14, 16)))
                g.FillRectangle(bg, r);

            int barX = r.Left + 6, barW = 12, top = r.Top + 12, bot = r.Bottom - 12;
            if (bot <= top) return;
            for (int y = top; y < bot; y++)
            {
                double t = 1.0 - (double)(y - top) / (bot - top);
                using (var b = new SolidBrush(Palette.ColorAt(_lut, t)))
                    g.FillRectangle(b, barX, y, barW, 1);
            }
            using (var outline = new Pen(Color.FromArgb(60, 255, 255, 255)))
                g.DrawRectangle(outline, barX, top, barW, bot - top);

            double floorDb, ceilDb;
            lock (_gate) { floorDb = _displayFloor; ceilDb = _displayCeiling; }
            using (var brush = new SolidBrush(Color.FromArgb(190, 225, 225, 228)))
            {
                g.DrawString(ceilDb.ToString("0"), _fontSmall, brush, barX + barW + 2, top - 4);
                g.DrawString(floorDb.ToString("0"), _fontSmall, brush, barX + barW + 2, bot - 10);
            }
        }

        private void DrawStatus(Graphics g)
        {
            string src = _capture == null ? "no source" : _capture.Status;
            string text = string.Format("{0}  |  {1}  |  {2:0} fps  |  {3:0.0} ms  |  {4}{5}",
                src, _analyzer.DescribeResolution(), _fps, _lastAnalysisMs,
                _settings.Preset, _frozen ? "  |  FROZEN" : "");
            using (var brush = new SolidBrush(Color.FromArgb(140, 210, 210, 215)))
                g.DrawString(text, _fontSmall, brush, 4, 2);
        }

        private void DrawHud(Graphics g, FrequencyMap map)
        {
            if (_mouseX < 0 || _mouseX >= map.Width) return;

            double freq = map.XToFreq(_mouseX);
            double cents;
            string note = FrequencyMap.DescribeNote(freq, out cents);

            double dbL = double.NaN, dbR = double.NaN;
            lock (_gate)
            {
                if (_mouseX < _curveL.Length) { dbL = _curveL[_mouseX]; dbR = _curveR[_mouseX]; }
            }

            string time = "";
            Rectangle lane = _sgramLRect.Contains(_mouseX, _mouseY) ? _sgramLRect
                           : (_sgramRRect.Contains(_mouseX, _mouseY) ? _sgramRRect : Rectangle.Empty);
            if (lane != Rectangle.Empty)
            {
                double rowsPerSecond = (double)_settings.TargetFps / Math.Max(1, _settings.ScrollDivider);
                if (rowsPerSecond > 0)
                    time = string.Format("  ·  -{0:0.00}s", (_mouseY - lane.Top) / rowsPerSecond);
            }

            string text = string.Format("{0}  ·  {1}{2:+0;-0}c  ·  L {3:0.1}  R {4:0.1} dB{5}",
                FormatHzPrecise(freq), note, cents, dbL, dbR, time);

            using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255)))
                g.DrawLine(pen, _mouseX, 0, _mouseX, ClientSize.Height);

            SizeF sz = g.MeasureString(text, _font);
            float bx = _mouseX + 10;
            float by = Math.Max(2, _mouseY - sz.Height - 8);
            if (bx + sz.Width + 8 > map.Width) bx = _mouseX - sz.Width - 12;

            using (var back = new SolidBrush(Color.FromArgb(215, 10, 10, 12)))
                g.FillRectangle(back, bx - 4, by - 2, sz.Width + 8, sz.Height + 4);
            using (var border = new Pen(Color.FromArgb(70, 255, 255, 255)))
                g.DrawRectangle(border, bx - 4, by - 2, sz.Width + 8, sz.Height + 4);
            using (var brush = new SolidBrush(Color.FromArgb(240, 240, 240, 245)))
                g.DrawString(text, _font, brush, bx, by);
        }

        private static string FormatHzPrecise(double f)
        {
            if (f >= 1000) return (f / 1000.0).ToString("0.00") + " kHz";
            return f.ToString("0.0") + " Hz";
        }

        // ---------------- interaction ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused) { try { Focus(); } catch { } }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouseX = e.X; _mouseY = e.Y; _mouseIn = true;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouseIn = false;
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space) { _frozen = !_frozen; Invalidate(); return true; }
            if (keyData == Keys.F11) { ToggleFullscreen(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}

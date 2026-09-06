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
    /// Borderless fullscreen mirrored stereo view.
    ///
    /// Frequency runs vertically (low at the bottom), time horizontally. Layout, outside
    /// in: a spectrum curve pinned to each screen edge, then that channel's spectrogram,
    /// then a narrow label gutter where the two meet.
    ///
    /// New columns enter at the outer edges, beside the curves, and age toward the
    /// centre - so each channel's newest slice sits directly against its own curve, and
    /// the oldest data of both channels meets in the middle.
    /// </summary>
    public sealed class FullscreenView : Form
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private readonly Settings _settings;
        private readonly string _storageDir;
        private readonly LoopbackCapture _capture;
        private readonly NowPlayingProvider _nowPlaying;

        private readonly SpectrumAnalyzer _analyzer = new SpectrumAnalyzer();
        private readonly LoudnessMeter _meter = new LoudnessMeter();
        private readonly DynamicRange _range = new DynamicRange();
        private readonly object _gate = new object();

        private ColumnSpectrogram _sgL, _sgR;
        private WaveformRing _wfL, _wfR;
        private FrequencyMap _map;
        private int[] _lut;

        private double[] _dbL = new double[0], _dbR = new double[0];
        private double[] _smoothL = new double[0], _smoothR = new double[0];
        private double[] _curveL = new double[0], _curveR = new double[0];
        private double[] _intensity = new double[0];
        private double[] _chunkL = new double[8192], _chunkR = new double[8192];
        private long _meterCursor = -1;

        private Thread _worker;
        private volatile bool _running;
        private volatile bool _frozen;
        private volatile bool _invalidatePending;
        private int _scrollTick;

        private Rectangle _curveLeftRect, _sgLeftRect, _gutterRect, _sgRightRect, _curveRightRect;
        private Rectangle _waveLeftRect, _waveRightRect;
        private int _sgramH;

        private Font _fontBig, _fontMid, _fontSmall, _fontTiny;
        private double _fps, _analysisMs;
        private double _displayFloor = -95, _displayCeiling = -5;
        private string _title = "", _artist = "", _album = "";
        private DateTime _hintUntil;

        public FullscreenView(Settings settings, string storageDir,
                              LoopbackCapture capture, NowPlayingProvider nowPlaying)
        {
            _settings = settings;
            _storageDir = storageDir;
            _capture = capture;
            _nowPlaying = nowPlaying;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _fontBig = new Font("Segoe UI Light", 22f);
            _fontMid = new Font("Segoe UI", 11f);
            _fontSmall = new Font("Segoe UI", 8.5f);
            _fontTiny = new Font("Segoe UI", 7.5f);
            _lut = Palette.BuildLut(_settings.Palette);
            _hintUntil = DateTime.UtcNow.AddSeconds(4);

            var menu = new ContextMenuStrip();
            menu.Opening += delegate
            {
                MenuFactory.Populate(menu, _settings, new MenuFactory.Options
                {
                    IsFullscreen = true,
                    ScrollPixels = Math.Max(1, _sgLeftRect.Width),
                    IsFrozen = delegate { return _frozen; },
                    ToggleFreeze = delegate { _frozen = !_frozen; Invalidate(); },
                    ToggleFullscreen = delegate { Close(); },
                    Changed = OnSettingsChanged
                });
            };
            ContextMenuStrip = menu;
        }

        public void ShowOn(Control anchor)
        {
            Screen screen = anchor != null && anchor.IsHandleCreated
                ? Screen.FromControl(anchor)
                : Screen.PrimaryScreen;
            ShowAt(screen.Bounds, true);
        }

        /// <summary>
        /// Opens at explicit bounds. Kept separate from <see cref="ShowOn"/> so the render
        /// harness can drive the real view offscreen instead of taking over a display.
        /// </summary>
        public void ShowAt(Rectangle bounds, bool activate)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            Show();
            if (activate) Activate();
            RefreshNowPlaying();
            StartWorker();
        }

        private void OnSettingsChanged(bool rebuildGeometry)
        {
            _lut = Palette.BuildLut(_settings.Palette);
            lock (_gate) { _range.Reset(); }
            if (rebuildGeometry) RebuildGeometry();
            if (_storageDir != null) _settings.Save(_storageDir);
            Invalidate();
        }

        // ---------------- lifecycle ----------------

        private void StartWorker()
        {
            if (_running) return;
            RebuildGeometry();
            _running = true;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "NostalgiaPlus.Fullscreen";
            _worker.Start();
        }

        private void StopWorker()
        {
            _running = false;
            Thread t = _worker;
            if (t != null && t.IsAlive) t.Join(1200);
            _worker = null;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopWorker();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopWorker();
                if (_sgL != null) { _sgL.Dispose(); _sgL = null; }
                if (_sgR != null) { _sgR.Dispose(); _sgR = null; }
                if (_fontBig != null) _fontBig.Dispose();
                if (_fontMid != null) _fontMid.Dispose();
                if (_fontSmall != null) _fontSmall.Dispose();
                if (_fontTiny != null) _fontTiny.Dispose();
            }
            base.Dispose(disposing);
        }

        public void RefreshNowPlaying()
        {
            if (_nowPlaying != null)
            {
                try
                {
                    string[] info = _nowPlaying();
                    if (info != null && info.Length >= 3)
                    {
                        _title = info[0] ?? "";
                        _artist = info[1] ?? "";
                        _album = info[2] ?? "";
                    }
                }
                catch { }
            }
            lock (_gate) { _range.Reset(); _meter.Reset(); }
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
                int w = Math.Max(8, ClientSize.Width);
                int h = Math.Max(8, ClientSize.Height);

                int waveH = _settings.FsShowWaveform ? Math.Max(56, h / 10) : 0;
                int sgramH = Math.Max(1, h - waveH);

                int curveW = _settings.FsCurveWidth;
                if (curveW < 40) curveW = 40;
                if (curveW > w / 4) curveW = w / 4;
                int gutterW = _settings.FsGutterWidth;
                if (gutterW < 0) gutterW = 0;
                if (gutterW > 90) gutterW = 90;

                int sgW = Math.Max(1, (w - 2 * curveW - gutterW) / 2);

                _sgramH = sgramH;
                _curveLeftRect = new Rectangle(0, 0, curveW, sgramH);
                _sgLeftRect = new Rectangle(curveW, 0, sgW, sgramH);
                _gutterRect = new Rectangle(curveW + sgW, 0, gutterW, sgramH);
                _sgRightRect = new Rectangle(curveW + sgW + gutterW, 0, sgW, sgramH);
                int rightCurveX = _sgRightRect.Right;
                _curveRightRect = new Rectangle(rightCurveX, 0, Math.Max(1, w - rightCurveX), sgramH);

                // Waveform lanes sit directly beneath their own spectrogram and share its
                // time axis exactly, so a transient lines up with the column that made it.
                _waveLeftRect = new Rectangle(_sgLeftRect.X, sgramH, sgW, waveH);
                _waveRightRect = new Rectangle(_sgRightRect.X, sgramH, sgW, waveH);

                double nyq = (_capture != null ? _capture.SampleRate : 48000) * 0.5;
                double fMax = Math.Min(_settings.FMax, nyq);
                double fMin = _settings.Scale == FreqScale.Linear
                    ? Math.Max(0, _settings.FMin) : Math.Max(10.0, _settings.FMin);

                // Map width is the vertical frequency resolution of the display.
                _map = new FrequencyMap(_settings.Scale, sgramH, fMin, fMax);

                if (_sgL == null) _sgL = new ColumnSpectrogram(sgW, sgramH); else _sgL.Resize(sgW, sgramH);
                if (_sgR == null) _sgR = new ColumnSpectrogram(sgW, sgramH); else _sgR.Resize(sgW, sgramH);
                _sgL.Clear(_lut[0]);
                _sgR.Clear(_lut[0]);

                int cap = sgW + 4;
                if (_wfL == null) _wfL = new WaveformRing(cap); else _wfL.Resize(cap);
                if (_wfR == null) _wfR = new WaveformRing(cap); else _wfR.Resize(cap);

                EnsureArrays(sgramH);
            }
        }

        private void EnsureArrays(int n)
        {
            if (_dbL.Length == n) return;
            _dbL = new double[n]; _dbR = new double[n];
            _smoothL = new double[n]; _smoothR = new double[n];
            _curveL = new double[n]; _curveR = new double[n];
            _intensity = new double[n];
            for (int i = 0; i < n; i++)
            {
                _smoothL[i] = SpectrumAnalyzer.FloorDb;
                _smoothR[i] = SpectrumAnalyzer.FloorDb;
            }
        }

        // ---------------- analysis ----------------

        private void WorkerLoop()
        {
            timeBeginPeriod(1);
            var sw = Stopwatch.StartNew();
            double last = sw.Elapsed.TotalSeconds;
            var frame = new Stopwatch();
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

                    frame.Restart();
                    try { AnalyseOnce(dt); } catch { }
                    frame.Stop();
                    _analysisMs = frame.Elapsed.TotalMilliseconds;
                    if (dt > 0) _fps = _fps * 0.9 + (1.0 / dt) * 0.1;

                    if (!_invalidatePending && IsHandleCreated)
                    {
                        _invalidatePending = true;
                        try { BeginInvoke((MethodInvoker)delegate { _invalidatePending = false; Invalidate(); }); }
                        catch { _invalidatePending = false; }
                    }

                    int sleep = (int)((target - (sw.Elapsed.TotalSeconds - now)) * 1000.0);
                    if (sleep > 0) Thread.Sleep(sleep);
                }
            }
            finally { timeEndPeriod(1); }
        }

        private void AnalyseOnce(double dt)
        {
            FrequencyMap map = _map;
            LoopbackCapture cap = _capture;
            if (map == null || cap == null || _sgL == null || _sgR == null) return;

            _analyzer.Configure(cap.SampleRate, _settings.Quality, _settings.Window);
            _meter.Configure(cap.SampleRate);

            int n = map.Width;
            if (_dbL.Length < n) return;

            if (_meterCursor < 0) _meterCursor = Math.Max(0, cap.Ring.TotalFrames - 1024);
            long next;
            int got = cap.Ring.ReadRange(_meterCursor, _chunkL, _chunkR, _chunkL.Length, out next);
            _meterCursor = next;
            float loL = 0, hiL = 0, loR = 0, hiR = 0;
            if (got > 0)
            {
                _meter.Process(_chunkL, _chunkR, got);
                for (int i = 0; i < got; i++)
                {
                    float a = (float)_chunkL[i], b = (float)_chunkR[i];
                    if (a < loL) loL = a; if (a > hiL) hiL = a;
                    if (b < loR) loR = b; if (b > hiR) hiR = b;
                }
            }

            if (!_analyzer.ComputeStereo(cap.Ring, map, _dbL, _dbR,
                                         _settings.Aggregate, _settings.TiltDbPerOctave))
                return;

            double aCoef = 1.0 - Math.Exp(-dt / Math.Max(0.001, _settings.AttackMs / 1000.0));
            double rCoef = 1.0 - Math.Exp(-dt / Math.Max(0.001, _settings.ReleaseMs / 1000.0));
            for (int i = 0; i < n; i++)
            {
                double v = _dbL[i], c = _smoothL[i];
                _smoothL[i] = c + (v - c) * (v > c ? aCoef : rCoef);
                v = _dbR[i]; c = _smoothR[i];
                _smoothR[i] = c + (v - c) * (v > c ? aCoef : rCoef);
            }

            // One shared range across both channels, so equal colour means equal level.
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
                    _sgL.PushColumn(_intensity, n, _lut);

                    for (int i = 0; i < n; i++)
                    {
                        double t = (_dbR[i] - floorDb) * inv;
                        _intensity[i] = t < 0 ? 0 : (t > 1 ? 1 : t);
                    }
                    _sgR.PushColumn(_intensity, n, _lut);

                    _wfL.Push(loL, hiL);
                    _wfR.Push(loR, hiR);
                }
                Array.Copy(_smoothL, _curveL, n);
                Array.Copy(_smoothR, _curveR, n);
                _displayFloor = floorDb;
                _displayCeiling = ceilDb;
            }
        }

        // ---------------- painting ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Palette.Background(_lut));

            FrequencyMap map = _map;
            if (map == null) return;

            lock (_gate)
            {
                // Newest column against the outer edge; history ages toward the centre.
                if (_sgL != null) _sgL.Draw(g, _sgLeftRect, false);
                if (_sgR != null) _sgR.Draw(g, _sgRightRect, true);
            }

            if (_settings.FsShowGrid) DrawGrid(g, map);
            DrawCurves(g, map);
            if (_settings.FsShowWaveform && _waveLeftRect.Height > 0) DrawWaveforms(g);
            if (_settings.FsShowOverlays) DrawOverlays(g);
            DrawHint(g);
        }

        private int FreqToY(FrequencyMap map, double f)
        {
            double idx = map.FreqToX(f);            // 0 = lowest frequency
            return _sgramH - 1 - (int)Math.Round(idx);
        }

        private void BuildGridLines(FrequencyMap map, List<double> freqs, List<string> labels)
        {
            if (map.Scale == FreqScale.Linear)
            {
                for (double f = 2000; f <= map.FMax; f += 2000)
                { freqs.Add(f); labels.Add((f / 1000).ToString("0") + "k"); }
            }
            else
            {
                for (int midi = 12; midi <= 132; midi += 12)
                {
                    double f = FrequencyMap.MidiToFreq(midi);
                    if (f < map.FMin || f > map.FMax) continue;
                    freqs.Add(f); labels.Add("C" + ((midi / 12) - 1));
                }
            }
        }

        private void DrawGrid(Graphics g, FrequencyMap map)
        {
            var freqs = new List<double>();
            var labels = new List<string>();
            BuildGridLines(map, freqs, labels);

            // The gutter is the only place labels can live now that the edges hold curves.
            if (_gutterRect.Width > 0)
                using (var bg = new SolidBrush(Color.FromArgb(255, 12, 12, 15)))
                    g.FillRectangle(bg, _gutterRect);

            using (var pen = new Pen(Color.FromArgb(38, 255, 255, 255)))
            using (var gutterPen = new Pen(Color.FromArgb(70, 255, 255, 255)))
            using (var brush = new SolidBrush(Color.FromArgb(185, 232, 232, 238)))
            {
                // Keep gutter labels clear of the overlay bar, which sits over the
                // same centre column.
                int labelFloor = _settings.FsShowOverlays ? 84 : 0;
                for (int i = 0; i < freqs.Count; i++)
                {
                    int y = FreqToY(map, freqs[i]);
                    if (y < 0 || y >= _sgramH) continue;
                    g.DrawLine(pen, 0, y, ClientSize.Width, y);

                    if (_gutterRect.Width >= 22 && y >= labelFloor)
                    {
                        g.DrawLine(gutterPen, _gutterRect.Left, y, _gutterRect.Right, y);
                        SizeF sz = g.MeasureString(labels[i], _fontTiny);
                        float lx = _gutterRect.Left + (_gutterRect.Width - sz.Width) / 2;
                        g.DrawString(labels[i], _fontTiny, brush, lx, y - 13);
                    }
                }
            }

            if (_gutterRect.Width > 0)
                using (var edge = new Pen(Color.FromArgb(45, 255, 255, 255)))
                {
                    g.DrawLine(edge, _gutterRect.Left, 0, _gutterRect.Left, _sgramH);
                    g.DrawLine(edge, _gutterRect.Right - 1, 0, _gutterRect.Right - 1, _sgramH);
                }
        }

        private void DrawCurves(Graphics g, FrequencyMap map)
        {
            int n = _sgramH;
            if (n < 2 || _curveL.Length < n) return;

            var left = new PointF[n + 2];
            var right = new PointF[n + 2];
            float ampL = _curveLeftRect.Width;
            float ampR = _curveRightRect.Width;
            int leftBase = _curveLeftRect.Left;            // baseline at the screen edge
            int rightBase = _curveRightRect.Right;         // baseline at the screen edge

            lock (_gate)
            {
                double floorDb = _displayFloor, ceilDb = _displayCeiling;
                double span = Math.Max(1, ceilDb - floorDb);
                for (int y = 0; y < n; y++)
                {
                    int i = n - 1 - y;                     // display top = highest frequency
                    double tl = (_curveL[i] - floorDb) / span;
                    if (tl < 0) tl = 0; else if (tl > 1) tl = 1;
                    double tr = (_curveR[i] - floorDb) / span;
                    if (tr < 0) tr = 0; else if (tr > 1) tr = 1;
                    left[y] = new PointF((float)(leftBase + tl * ampL), y);
                    right[y] = new PointF((float)(rightBase - tr * ampR), y);
                }
            }
            left[n] = new PointF(leftBase, n - 1); left[n + 1] = new PointF(leftBase, 0);
            right[n] = new PointF(rightBase, n - 1); right[n + 1] = new PointF(rightBase, 0);

            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color hi = Palette.ColorAt(_lut, 0.85);
            Color lo = Palette.ColorAt(_lut, 0.4);

            using (var pathL = new GraphicsPath())
            using (var pathR = new GraphicsPath())
            {
                pathL.AddPolygon(left);
                pathR.AddPolygon(right);
                using (var fill = new LinearGradientBrush(
                        new Rectangle(_curveLeftRect.X, 0, Math.Max(1, _curveLeftRect.Width), 1),
                        Color.FromArgb(210, hi), Color.FromArgb(40, lo), LinearGradientMode.Horizontal))
                    g.FillPath(fill, pathL);
                using (var fill = new LinearGradientBrush(
                        new Rectangle(_curveRightRect.X, 0, Math.Max(1, _curveRightRect.Width), 1),
                        Color.FromArgb(40, lo), Color.FromArgb(210, hi), LinearGradientMode.Horizontal))
                    g.FillPath(fill, pathR);
            }

            using (var pen = new Pen(Color.FromArgb(235, hi), 1.3f))
            {
                g.DrawLines(pen, Trim(left, n));
                g.DrawLines(pen, Trim(right, n));
            }

            using (var edge = new Pen(Color.FromArgb(50, 255, 255, 255)))
            {
                g.DrawLine(edge, _curveLeftRect.Right, 0, _curveLeftRect.Right, _sgramH);
                g.DrawLine(edge, _curveRightRect.Left, 0, _curveRightRect.Left, _sgramH);
            }

            g.SmoothingMode = old;
        }

        private static PointF[] Trim(PointF[] src, int n)
        {
            var outp = new PointF[n];
            Array.Copy(src, outp, n);
            return outp;
        }

        private void DrawWaveforms(Graphics g)
        {
            using (var bg = new SolidBrush(Color.FromArgb(255, 10, 10, 12)))
                g.FillRectangle(bg, 0, _waveLeftRect.Y, ClientSize.Width, _waveLeftRect.Height);

            Color c = Palette.ColorAt(_lut, 0.86);
            using (var pen = new Pen(Color.FromArgb(215, c)))
            using (var mid = new Pen(Color.FromArgb(45, 255, 255, 255)))
            {
                int midY = _waveLeftRect.Y + _waveLeftRect.Height / 2;
                float half = _waveLeftRect.Height / 2f - 2;
                g.DrawLine(mid, 0, midY, ClientSize.Width, midY);

                lock (_gate)
                {
                    // Same direction as the spectrogram above: newest at the outer edge.
                    for (int a = 0; a < _waveLeftRect.Width; a++)
                    {
                        int x = _waveLeftRect.X + a;
                        float lo, hi;
                        _wfL.Get(a, out lo, out hi);
                        g.DrawLine(pen, x, midY - hi * half, x, midY - lo * half);
                    }
                    for (int a = 0; a < _waveRightRect.Width; a++)
                    {
                        int x = _waveRightRect.Right - 1 - a;
                        float lo, hi;
                        _wfR.Get(a, out lo, out hi);
                        g.DrawLine(pen, x, midY - hi * half, x, midY - lo * half);
                    }
                }
            }
        }

        private void DrawOverlays(Graphics g)
        {
            int barH = 78;
            using (var grad = new LinearGradientBrush(new Rectangle(0, 0, ClientSize.Width, barH),
                       Color.FromArgb(190, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Vertical))
                g.FillRectangle(grad, 0, 0, ClientSize.Width, barH);

            using (var w = new SolidBrush(Color.FromArgb(240, 245, 245, 248)))
            using (var d = new SolidBrush(Color.FromArgb(170, 200, 200, 210)))
            {
                if (_title.Length > 0) g.DrawString(_title, _fontBig, w, 16, 6);
                string sub = _artist;
                if (_album.Length > 0) sub += (sub.Length > 0 ? "  ·  " : "") + _album;
                if (sub.Length > 0) g.DrawString(sub, _fontMid, d, 18, 44);
            }

            double mLufs, sLufs, tp, crest, corr, bal;
            lock (_gate)
            {
                mLufs = _meter.MomentaryLufs; sLufs = _meter.ShortTermLufs;
                tp = _meter.TruePeakDb; crest = _meter.CrestDb;
                corr = _meter.Correlation; bal = _meter.Balance;
            }

            using (var lbl = new SolidBrush(Color.FromArgb(150, 190, 190, 200)))
            using (var val = new SolidBrush(Color.FromArgb(240, 245, 245, 248)))
            using (var warn = new SolidBrush(Color.FromArgb(255, 255, 120, 90)))
            {
                string[] names = { "LUFS-M", "LUFS-S", "TRUE PK", "CREST" };
                string[] vals = {
                    mLufs.ToString("0.0"), sLufs.ToString("0.0"),
                    tp.ToString("0.0"), crest.ToString("0.0")
                };
                int x = ClientSize.Width - 16;
                for (int i = names.Length - 1; i >= 0; i--)
                {
                    SizeF vs = g.MeasureString(vals[i], _fontMid);
                    SizeF ls = g.MeasureString(names[i], _fontTiny);
                    float colW = Math.Max(vs.Width, ls.Width) + 18;
                    x -= (int)colW;
                    g.DrawString(names[i], _fontTiny, lbl, x, 8);
                    g.DrawString(vals[i], _fontMid, (i == 2 && tp > -1.0) ? warn : val, x, 22);
                }
            }

            int mw = 210;
            int mx = ClientSize.Width / 2 - mw / 2;
            DrawMeterBar(g, mx, 14, mw, "CORRELATION", corr,
                         corr < 0 ? Color.FromArgb(255, 120, 90) : Palette.ColorAt(_lut, 0.8));
            DrawMeterBar(g, mx, 44, mw, "BALANCE", bal, Palette.ColorAt(_lut, 0.65));
        }

        private void DrawMeterBar(Graphics g, int x, int y, int w, string label,
                                  double value, Color colour)
        {
            using (var track = new SolidBrush(Color.FromArgb(120, 40, 40, 46)))
                g.FillRectangle(track, x, y + 9, w, 5);
            using (var centre = new Pen(Color.FromArgb(90, 255, 255, 255)))
                g.DrawLine(centre, x + w / 2, y + 7, x + w / 2, y + 16);

            double t = (value + 1.0) / 2.0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            int px = x + (int)(t * w);

            int from = Math.Min(px, x + w / 2);
            int width = Math.Abs(px - (x + w / 2));
            using (var fill = new SolidBrush(Color.FromArgb(210, colour)))
                g.FillRectangle(fill, from, y + 9, Math.Max(1, width), 5);
            using (var knob = new SolidBrush(Color.FromArgb(255, 250, 250, 252)))
                g.FillRectangle(knob, px - 1, y + 6, 2, 11);

            using (var lbl = new SolidBrush(Color.FromArgb(140, 190, 190, 200)))
                g.DrawString(label, _fontTiny, lbl, x, y - 4);
            using (var val = new SolidBrush(Color.FromArgb(220, 240, 240, 245)))
            {
                string s = value.ToString("+0.00;-0.00; 0.00");
                SizeF sz = g.MeasureString(s, _fontTiny);
                g.DrawString(s, _fontTiny, val, x + w - sz.Width, y - 4);
            }
        }

        private void DrawHint(Graphics g)
        {
            if (DateTime.UtcNow > _hintUntil) return;
            string text = "Right-click for options    Esc exit    Space freeze    W waveform    O overlays    G grid    P palette    " +
                          _analyzer.DescribeResolution() + "    " + _fps.ToString("0") + " fps  " +
                          _analysisMs.ToString("0.0") + " ms";
            SizeF sz = g.MeasureString(text, _fontSmall);
            float x = (ClientSize.Width - sz.Width) / 2;
            float y = ClientSize.Height - sz.Height - 18;
            using (var back = new SolidBrush(Color.FromArgb(200, 8, 8, 10)))
                g.FillRectangle(back, x - 12, y - 6, sz.Width + 24, sz.Height + 12);
            using (var b = new SolidBrush(Color.FromArgb(225, 235, 235, 240)))
                g.DrawString(text, _fontSmall, b, x, y);
        }

        // ---------------- input ----------------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                case Keys.F11:
                    Close();
                    return true;
                case Keys.Space:
                    _frozen = !_frozen; Invalidate(); return true;
                case Keys.W:
                    _settings.FsShowWaveform = !_settings.FsShowWaveform;
                    OnSettingsChanged(true); return true;
                case Keys.O:
                    _settings.FsShowOverlays = !_settings.FsShowOverlays;
                    OnSettingsChanged(false); return true;
                case Keys.G:
                    _settings.FsShowGrid = !_settings.FsShowGrid;
                    OnSettingsChanged(false); return true;
                case Keys.P:
                    {
                        var vals = (PaletteKind[])Enum.GetValues(typeof(PaletteKind));
                        int i = Array.IndexOf(vals, _settings.Palette);
                        _settings.Palette = vals[(i + 1) % vals.Length];
                        OnSettingsChanged(false);
                        _hintUntil = DateTime.UtcNow.AddSeconds(1.5);
                        return true;
                    }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}

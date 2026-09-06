using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NostalgiaPlus.Audio;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Render;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// Fullscreen view: two side-by-side per-channel panes rendered through the same
    /// <see cref="StereoScope"/> the docked panel uses, plus waveform lanes, meters and
    /// a row of quick buttons.
    ///
    /// Immersive mode adds bloom and fades every label, meter and button while you are
    /// not touching anything.
    /// </summary>
    public sealed class FullscreenView : Form
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private sealed class QuickButton
        {
            public string Name;
            public Func<string> Value;
            public Action Cycle;
            public Rectangle Rect;
        }

        private readonly Settings _settings;
        private readonly string _storageDir;
        private readonly LoopbackCapture _capture;
        private readonly NowPlayingProvider _nowPlaying;

        private readonly StereoScope _scope = new StereoScope();
        private readonly LoudnessMeter _meter = new LoudnessMeter();
        private readonly object _gate = new object();

        private WaveformRing _wfA, _wfB;
        private int[] _lut;
        private double[] _chunkL = new double[8192], _chunkR = new double[8192];
        private long _meterCursor = -1;

        private Thread _worker;
        private volatile bool _running;
        private volatile bool _frozen;
        private volatile bool _invalidatePending;

        private Rectangle _scopeRect, _waveARect, _waveBRect;
        private readonly List<QuickButton> _buttons = new List<QuickButton>();

        private Font _fontBig, _fontMid, _fontSmall, _fontTiny;
        private double _fps, _analysisMs, _paintMs;
        private string _title = "", _artist = "", _album = "";
        private DateTime _hintUntil, _lastActivity = DateTime.UtcNow, _infoUntil = DateTime.MinValue;
        private bool _cursorHidden;
        private readonly HoverInfo _hover = new HoverInfo();
        private bool _mouseDown, _dragged;

        private const double IdleHoldSeconds = 3.0;
        private const double IdleFadeSeconds = 1.5;
        private const int ButtonBarHeight = 30;

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
            _scope.SetPalette(_lut);
            _hintUntil = DateTime.UtcNow.AddSeconds(4);

            BuildButtons();

            var menu = new ContextMenuStrip();
            menu.Opening += delegate
            {
                Touch();
                MenuFactory.Populate(menu, _settings, new MenuFactory.Options
                {
                    IsFullscreen = true,
                    ScrollPixels = _scope.Panes.Length > 0 ? _scope.Panes[0].SpectroRect.Width : 1,
                    IsFrozen = delegate { return _frozen; },
                    ToggleFreeze = delegate { _frozen = !_frozen; Invalidate(); },
                    ToggleFullscreen = delegate { Close(); },
                    ToggleImmersive = ToggleImmersive,
                    Changed = OnSettingsChanged
                });
            };
            ContextMenuStrip = menu;
        }

        // ---------------- quick buttons ----------------

        private static T Next<T>(T current)
        {
            var vals = (T[])Enum.GetValues(typeof(T));
            int i = Array.IndexOf(vals, current);
            return vals[(i + 1) % vals.Length];
        }

        private void BuildButtons()
        {
            _buttons.Clear();
            Add("CHANNELS", delegate { return _settings.PairMode.ToString(); },
                delegate { _settings.PairMode = Next(_settings.PairMode); OnSettingsChanged(true); });
            Add("SCALE", delegate { return _settings.Scale.ToString(); },
                delegate
                {
                    _settings.Scale = Next(_settings.Scale);
                    if (_settings.Scale == FreqScale.Linear) { _settings.FMin = 0; _settings.FMax = 22050; }
                    else { _settings.FMin = 25; _settings.FMax = 18000; }
                    _settings.Preset = Preset.Custom;
                    OnSettingsChanged(true);
                });
            Add("RESOLUTION", delegate { return _settings.Quality.ToString(); },
                delegate { _settings.Quality = Next(_settings.Quality); _settings.Preset = Preset.Custom; OnSettingsChanged(false); });
            Add("STYLE", delegate { return _settings.Style.ToString(); },
                delegate { _settings.Style = Next(_settings.Style); OnSettingsChanged(false); });
            Add("PALETTE", delegate { return Palette.DisplayName(_settings.Palette); },
                delegate { _settings.Palette = Next(_settings.Palette); _settings.Preset = Preset.Custom; OnSettingsChanged(false); });
            Add("TILT", delegate { return _settings.TiltDbPerOctave.ToString("0.0") + " dB/oct"; },
                delegate
                {
                    double[] t = { 0, 1.5, 3.0, 4.5, 6.0 };
                    int i = 0;
                    for (int k = 0; k < t.Length; k++) if (Math.Abs(t[k] - _settings.TiltDbPerOctave) < 0.01) i = k;
                    _settings.TiltDbPerOctave = t[(i + 1) % t.Length];
                    _settings.Preset = Preset.Custom;
                    OnSettingsChanged(false);
                });
            Add("CONTRAST", delegate { return (_settings.Contrast * 100).ToString("0") + "%"; },
                delegate
                {
                    double[] c = { 0.15, 0.25, 0.40, 0.55, 0.70, 0.82 };
                    int i = 0;
                    for (int k = 0; k < c.Length; k++) if (Math.Abs(c[k] - _settings.Contrast) < 0.01) i = k;
                    _settings.Contrast = c[(i + 1) % c.Length];
                    OnSettingsChanged(false);
                });
            Add("GRAPH", delegate { return _settings.CurveWidthPct + "%"; },
                delegate
                {
                    int[] p = { 0, 8, 12, 18, 25, 33, 45 };
                    int i = 0;
                    for (int k = 0; k < p.Length; k++) if (p[k] == _settings.CurveWidthPct) i = k;
                    _settings.CurveWidthPct = p[(i + 1) % p.Length];
                    OnSettingsChanged(true);
                });
            Add("MIRROR", delegate { return _settings.MirrorLeftPane ? "On" : "Off"; },
                delegate { _settings.MirrorLeftPane = !_settings.MirrorLeftPane; OnSettingsChanged(true); });
            Add("SPEED", delegate { return "1/" + _settings.ScrollDivider; },
                delegate
                {
                    int[] d = { 1, 2, 4, 8 };
                    int i = 0;
                    for (int k = 0; k < d.Length; k++) if (d[k] == _settings.ScrollDivider) i = k;
                    _settings.ScrollDivider = d[(i + 1) % d.Length];
                    OnSettingsChanged(false);
                });
        }

        private void Add(string name, Func<string> value, Action cycle)
        {
            var b = new QuickButton();
            b.Name = name; b.Value = value; b.Cycle = cycle;
            _buttons.Add(b);
        }

        // ---------------- lifecycle ----------------

        public void ShowOn(Control anchor)
        {
            Screen screen = anchor != null && anchor.IsHandleCreated
                ? Screen.FromControl(anchor) : Screen.PrimaryScreen;
            ShowAt(screen.Bounds, true);
        }

        public void ShowAt(Rectangle bounds, bool activate)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            Show();
            if (activate) Activate();
            RefreshNowPlaying();
            if (_running) return;
            RebuildGeometry();
            _running = true;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "NostalgiaPlus.Fullscreen";
            _worker.Start();
        }

        private void OnSettingsChanged(bool rebuildGeometry)
        {
            _lut = Palette.BuildLut(_settings.Palette);
            _scope.SetPalette(_lut);
            lock (_gate) { _scope.ResetRange(); }
            if (rebuildGeometry) RebuildGeometry();
            if (_storageDir != null) _settings.Save(_storageDir);
            Touch();
            Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _running = false;
            Thread t = _worker;
            if (t != null && t.IsAlive) t.Join(1200);
            _worker = null;
            ShowCursorIfHidden();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _running = false;
                ShowCursorIfHidden();
                _scope.Dispose();
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
                        _title = info[0] ?? ""; _artist = info[1] ?? ""; _album = info[2] ?? "";
                    }
                }
                catch { }
            }
            _infoUntil = DateTime.UtcNow.AddSeconds(6);
            lock (_gate) { _scope.ResetRange(); _meter.Reset(); }
        }

        // ---------------- idle fading ----------------

        private void Touch() { _lastActivity = DateTime.UtcNow; ShowCursorIfHidden(); }

        private void ShowCursorIfHidden()
        {
            if (_cursorHidden) { _cursorHidden = false; try { Cursor.Show(); } catch { } }
        }

        private double FurnitureAlpha()
        {
            if (!_settings.FsImmersive || !_settings.FsAutoHide) return 1.0;
            double idle = (DateTime.UtcNow - _lastActivity).TotalSeconds;
            if (idle <= IdleHoldSeconds) return 1.0;
            if (idle >= IdleHoldSeconds + IdleFadeSeconds) return 0.0;
            return 1.0 - (idle - IdleHoldSeconds) / IdleFadeSeconds;
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
                int w = Math.Max(16, ClientSize.Width);
                int h = Math.Max(16, ClientSize.Height);

                int waveH = 0;
                if (_settings.FsShowWaveform && _settings.WaveHeightPct > 0)
                    waveH = Math.Max(24, h * Math.Min(40, _settings.WaveHeightPct) / 100);

                _scopeRect = new Rectangle(0, 0, w, Math.Max(16, h - waveH));
                double sr = _capture != null ? _capture.SampleRate : 48000;
                _scope.Layout(_scopeRect, _settings, sr);

                ChannelPane[] panes = _scope.Panes;
                _waveARect = waveH > 0 && panes.Length > 0
                    ? new Rectangle(panes[0].SpectroRect.X, _scopeRect.Bottom, panes[0].SpectroRect.Width, waveH)
                    : Rectangle.Empty;
                _waveBRect = waveH > 0 && panes.Length > 1
                    ? new Rectangle(panes[1].SpectroRect.X, _scopeRect.Bottom, panes[1].SpectroRect.Width, waveH)
                    : Rectangle.Empty;

                int capA = Math.Max(8, _waveARect.Width + 4);
                if (_wfA == null) _wfA = new WaveformRing(capA); else _wfA.Resize(capA);
                int capB = Math.Max(8, _waveBRect.Width + 4);
                if (_wfB == null) _wfB = new WaveformRing(capB); else _wfB.Resize(capB);

                LayoutButtons(w, h, waveH);
            }
        }

        private void LayoutButtons(int w, int h, int waveH)
        {
            int total = 0;
            var widths = new int[_buttons.Count];
            using (var g = CreateGraphics())
                for (int i = 0; i < _buttons.Count; i++)
                {
                    string t = _buttons[i].Name + "  " + _buttons[i].Value();
                    widths[i] = (int)g.MeasureString(t, _fontTiny).Width + 22;
                    total += widths[i] + 6;
                }
            int x = (w - total) / 2;
            int y = h - waveH - ButtonBarHeight - 8;
            for (int i = 0; i < _buttons.Count; i++)
            {
                _buttons[i].Rect = new Rectangle(x, y, widths[i], ButtonBarHeight);
                x += widths[i] + 6;
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
            LoopbackCapture cap = _capture;
            if (cap == null) return;
            _meter.Configure(cap.SampleRate);

            if (_meterCursor < 0) _meterCursor = Math.Max(0, cap.Ring.TotalFrames - 1024);
            long next;
            int got = cap.Ring.ReadRange(_meterCursor, _chunkL, _chunkR, _chunkL.Length, out next);
            _meterCursor = next;

            float loA = 0, hiA = 0, loB = 0, hiB = 0;
            if (got > 0)
            {
                _meter.Process(_chunkL, _chunkR, got);
                bool ms = _settings.PairMode == ChannelPairMode.MidSide;
                for (int i = 0; i < got; i++)
                {
                    double l = _chunkL[i], r = _chunkR[i];
                    float a = (float)(ms ? (l + r) * 0.5 : l);
                    float b = (float)(ms ? (l - r) * 0.5 : r);
                    if (a < loA) loA = a; if (a > hiA) hiA = a;
                    if (b < loB) loB = b; if (b > hiB) hiB = b;
                }
            }

            lock (_gate)
            {
                if (!_scope.Analyse(cap, _settings, dt, _frozen)) return;
                if (!_frozen)
                {
                    if (_wfA != null) _wfA.Push(loA, hiA);
                    if (_wfB != null) _wfB.Push(loB, hiB);
                }
            }
        }

        // ---------------- painting ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Stopwatch.StartNew();
            PaintFrame(e.Graphics);
            t.Stop();
            _paintMs = _paintMs * 0.9 + t.Elapsed.TotalMilliseconds * 0.1;
        }

        private void PaintFrame(Graphics g)
        {
            // GDI+ antialiased text is by far the most expensive thing drawn per frame
            // once the spectrogram is a blit. Grid-fit rendering is several times faster
            // and, at these sizes on a dark ground, indistinguishable.
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(Palette.Background(_lut));
            // The OSD switch gates every drawn annotation; gridlines stay because they
            // are part of reading the image rather than chrome on top of it.
            double furniture = _settings.FsShowOsd ? FurnitureAlpha() : 0.0;
            if (furniture <= 0.001 && !_cursorHidden)
            {
                _cursorHidden = true;
                try { Cursor.Hide(); } catch { }
            }

            bool glow = _settings.FsImmersive && _settings.FsGlow;
            lock (_gate)
            {
                _scope.DrawPanes(g, _settings, glow, _fontTiny, furniture,
                                 _settings.FsShowOverlays ? 84 : 0);
                if (_settings.FsShowGrid && furniture > 0.004)
                    _scope.DrawGrid(g, _settings, _fontTiny, furniture,
                                    _settings.FsShowOverlays ? 84 : 0);
                _scope.DrawPaneLabels(g, _fontSmall, furniture, _settings.FsShowOverlays ? 84 : 0);
            }

            if (_settings.ShowHud && _hover.Active && _settings.FsShowOsd)
            {
                lock (_gate)
                {
                    _scope.DrawHover(g, _hover, _settings, _fontSmall, _fontTiny);
                }
            }
            if (_settings.FsShowWaveform && _waveARect.Height > 0) DrawWaveforms(g);
            if (_settings.FsShowOverlays && _settings.FsShowOsd) DrawOverlays(g, furniture);
            if (furniture > 0.004 && _settings.FsShowOsd) DrawButtons(g, furniture);
            if (_settings.FsShowOsd) DrawHint(g);
        }

        private void DrawWaveforms(Graphics g)
        {
            using (var bg = new SolidBrush(Color.FromArgb(255, 10, 10, 12)))
                g.FillRectangle(bg, 0, _waveARect.Y, ClientSize.Width, _waveARect.Height);

            Color c = Palette.ColorAt(_lut, 0.86);
            using (var pen = new Pen(Color.FromArgb(215, c)))
            using (var mid = new Pen(Color.FromArgb(45, 255, 255, 255)))
            {
                int midY = _waveARect.Y + _waveARect.Height / 2;
                float half = _waveARect.Height / 2f - 2;
                g.DrawLine(mid, 0, midY, ClientSize.Width, midY);
                lock (_gate)
                {
                    ChannelPane[] panes = _scope.Panes;
                    if (panes.Length > 0)
                        DrawWave(g, pen, _wfA, _waveARect, midY, half, panes[0].CurveOnLeft);
                    if (_waveBRect.Width > 0 && panes.Length > 1)
                        DrawWave(g, pen, _wfB, _waveBRect, midY, half, panes[1].CurveOnLeft);
                }
            }
        }

        private PointF[] _wavePoly = new PointF[0];

        private void DrawWave(Graphics g, Pen pen, WaveformRing ring, Rectangle r,
                              int midY, float half, bool curveOnLeft)
        {
            if (ring == null || r.Width <= 0) return;
            // Match the spectrogram above: newest sits against that pane's curve.
            bool newestOnRight = !curveOnLeft;
            int w = r.Width;

            // One filled polygon rather than a DrawLine per column: the per-column loop
            // was around 900 GDI+ calls per pane per frame.
            // Exact size: FillPolygon takes the whole array, so a "grow only" buffer
            // would force a copy into a fresh one every frame and undo the reuse.
            if (_wavePoly.Length != w * 2) _wavePoly = new PointF[w * 2];
            for (int a = 0; a < w; a++)
            {
                int x = newestOnRight ? r.Right - 1 - a : r.X + a;
                float lo, hi;
                ring.Get(a, out lo, out hi);
                _wavePoly[a] = new PointF(x, midY - hi * half);
                _wavePoly[w * 2 - 1 - a] = new PointF(x, midY - lo * half);
            }
            using (var brush = new SolidBrush(pen.Color))
                g.FillPolygon(brush, _wavePoly);
        }

        private void DrawButtons(Graphics g, double alpha)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                QuickButton b = _buttons[i];
                if (b.Rect.Width <= 0) continue;
                using (var back = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(190, 18, 18, 22), alpha)))
                using (var edge = new Pen(StereoScope.FadeColor(Color.FromArgb(70, 255, 255, 255), alpha)))
                {
                    g.FillRectangle(back, b.Rect);
                    g.DrawRectangle(edge, b.Rect);
                }
                using (var nameBrush = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(140, 190, 190, 200), alpha)))
                using (var valBrush = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(235, 245, 245, 250), alpha)))
                {
                    g.DrawString(b.Name, _fontTiny, nameBrush, b.Rect.X + 8, b.Rect.Y + 2);
                    g.DrawString(b.Value(), _fontTiny, valBrush, b.Rect.X + 8, b.Rect.Y + 14);
                }
            }
        }

        private void DrawOverlays(Graphics g, double alpha)
        {
            double infoAlpha = alpha;
            if (DateTime.UtcNow < _infoUntil) infoAlpha = 1.0;
            if (alpha <= 0.004 && infoAlpha <= 0.004) return;

            int barH = 78;
            using (var grad = new LinearGradientBrush(new Rectangle(0, 0, ClientSize.Width, barH),
                       StereoScope.FadeColor(Color.FromArgb(190, 0, 0, 0), Math.Max(alpha, infoAlpha)),
                       Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Vertical))
                g.FillRectangle(grad, 0, 0, ClientSize.Width, barH);

            if (infoAlpha > 0.004)
                using (var w = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(240, 245, 245, 248), infoAlpha)))
                using (var d = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(170, 200, 200, 210), infoAlpha)))
                {
                    if (_title.Length > 0) g.DrawString(_title, _fontBig, w, 16, 6);
                    string sub = _artist;
                    if (_album.Length > 0) sub += (sub.Length > 0 ? "  ·  " : "") + _album;
                    if (sub.Length > 0) g.DrawString(sub, _fontMid, d, 18, 44);
                }

            if (alpha <= 0.004) return;

            double mLufs, sLufs, tp, crest, corr, bal;
            lock (_gate)
            {
                mLufs = _meter.MomentaryLufs; sLufs = _meter.ShortTermLufs;
                tp = _meter.TruePeakDb; crest = _meter.CrestDb;
                corr = _meter.Correlation; bal = _meter.Balance;
            }

            using (var lbl = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(150, 190, 190, 200), alpha)))
            using (var val = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(240, 245, 245, 248), alpha)))
            using (var warn = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(255, 255, 120, 90), alpha)))
            {
                string[] names = { "LUFS-M", "LUFS-S", "TRUE PK", "CREST" };
                string[] vals = { mLufs.ToString("0.0"), sLufs.ToString("0.0"),
                                  tp.ToString("0.0"), crest.ToString("0.0") };
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

            int mw = 210, mx = ClientSize.Width / 2 - mw / 2;
            DrawMeterBar(g, mx, 14, mw, "CORRELATION", corr,
                         corr < 0 ? Color.FromArgb(255, 120, 90) : Palette.ColorAt(_lut, 0.8), alpha);
            DrawMeterBar(g, mx, 44, mw, "BALANCE", bal, Palette.ColorAt(_lut, 0.65), alpha);
        }

        private void DrawMeterBar(Graphics g, int x, int y, int w, string label,
                                  double value, Color colour, double alpha)
        {
            using (var track = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(120, 40, 40, 46), alpha)))
                g.FillRectangle(track, x, y + 9, w, 5);
            using (var centre = new Pen(StereoScope.FadeColor(Color.FromArgb(90, 255, 255, 255), alpha)))
                g.DrawLine(centre, x + w / 2, y + 7, x + w / 2, y + 16);

            double t = (value + 1.0) / 2.0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            int px = x + (int)(t * w);
            int from = Math.Min(px, x + w / 2);
            int width = Math.Abs(px - (x + w / 2));
            using (var fill = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(210, colour), alpha)))
                g.FillRectangle(fill, from, y + 9, Math.Max(1, width), 5);
            using (var knob = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(255, 250, 250, 252), alpha)))
                g.FillRectangle(knob, px - 1, y + 6, 2, 11);
            using (var lbl = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(140, 190, 190, 200), alpha)))
                g.DrawString(label, _fontTiny, lbl, x, y - 4);
            using (var val = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(220, 240, 240, 245), alpha)))
            {
                string s = value.ToString("+0.00;-0.00; 0.00");
                SizeF sz = g.MeasureString(s, _fontTiny);
                g.DrawString(s, _fontTiny, val, x + w - sz.Width, y - 4);
            }
        }

        private void DrawHint(Graphics g)
        {
            if (DateTime.UtcNow > _hintUntil) return;
            string text = (_settings.FsImmersive ? "IMMERSIVE   " : "") +
                          "Click a button to cycle it    Right-click for all options    " +
                          "I immersive    H hide OSD    Esc exit    Space freeze    " +
                          _scope.Analyzer.DescribeResolution() + "    " +
                          _fps.ToString("0") + " fps  " + _analysisMs.ToString("0.0") + " ms dsp  " +
                          _paintMs.ToString("0.0") + " ms paint";
            SizeF sz = g.MeasureString(text, _fontSmall);
            float x = (ClientSize.Width - sz.Width) / 2;
            float y = 92;
            using (var back = new SolidBrush(Color.FromArgb(200, 8, 8, 10)))
                g.FillRectangle(back, x - 12, y - 6, sz.Width + 24, sz.Height + 12);
            using (var b = new SolidBrush(Color.FromArgb(225, 235, 235, 240)))
                g.DrawString(text, _fontSmall, b, x, y);
        }

        // ---------------- input ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hover.Cursor = e.Location;
            _hover.Active = true;
            if (_mouseDown &&
                (Math.Abs(e.X - _hover.Origin.X) > 4 || Math.Abs(e.Y - _hover.Origin.Y) > 4))
            {
                _dragged = true;
                _hover.Measuring = true;
            }
            Touch();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover.Active = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _mouseDown = false;
            // A click freezes so a moment can be read without it scrolling away; a drag
            // is a measurement and leaves its result on screen until the next press.
            if (!_dragged) { _frozen = !_frozen; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Touch();
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < _buttons.Count; i++)
                if (_buttons[i].Rect.Contains(e.Location)) { _buttons[i].Cycle(); return; }
            _mouseDown = true;
            _dragged = false;
            _hover.Origin = e.Location;
            _hover.Measuring = false;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Touch();
            switch (keyData)
            {
                case Keys.Escape:
                case Keys.F11: Close(); return true;
                case Keys.Space: _frozen = !_frozen; Invalidate(); return true;
                case Keys.I: ToggleImmersive(); return true;
                case Keys.W:
                    _settings.FsShowWaveform = !_settings.FsShowWaveform;
                    OnSettingsChanged(true); return true;
                case Keys.O:
                    _settings.FsShowOverlays = !_settings.FsShowOverlays;
                    OnSettingsChanged(false); return true;
                case Keys.G:
                    _settings.FsShowGrid = !_settings.FsShowGrid;
                    OnSettingsChanged(false); return true;
                case Keys.H:
                    _settings.FsShowOsd = !_settings.FsShowOsd;
                    OnSettingsChanged(false); return true;
                case Keys.B:
                    _settings.Style = Next(_settings.Style);
                    OnSettingsChanged(false); return true;
                case Keys.C:
                    _settings.PairMode = Next(_settings.PairMode);
                    OnSettingsChanged(true); return true;
                case Keys.M:
                    _settings.MirrorLeftPane = !_settings.MirrorLeftPane;
                    OnSettingsChanged(true); return true;
                case Keys.P:
                    _settings.Palette = Next(_settings.Palette);
                    _settings.Preset = Preset.Custom;
                    OnSettingsChanged(false);
                    _hintUntil = DateTime.UtcNow.AddSeconds(1.5);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// Entering immersive mode also applies the Immersive preset: the visual treatment
        /// cannot rescue a linear axis at the lowest resolution, which is what makes dense
        /// music render as an undifferentiated wall.
        /// </summary>
        public void ToggleImmersive()
        {
            if (_settings.FsImmersive)
            {
                _settings.FsImmersive = false;
                _settings.Preset = Preset.Custom;
            }
            else _settings.ApplyPreset(Preset.Immersive);
            _hintUntil = DateTime.UtcNow.AddSeconds(3);
            OnSettingsChanged(true);
        }
    }
}

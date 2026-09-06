using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NostalgiaPlus.Audio;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Render;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// The docked panel. Two side-by-side per-channel panes - each a spectrum curve
    /// beside its own spectrogram, sharing a vertical frequency axis - with a note gutter
    /// between them. Identical rendering to the fullscreen view, via <see cref="StereoScope"/>.
    /// </summary>
    public sealed class AnalyzerPanel : UserControl
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private readonly Settings _settings;
        private readonly string _storageDir;
        private readonly StereoScope _scope = new StereoScope();
        private readonly object _gate = new object();

        private LoopbackCapture _capture;
        private int[] _lut;

        private Thread _worker;
        private volatile bool _running;
        private volatile bool _frozen;
        private volatile bool _invalidatePending;
        private volatile bool _paused;
        private bool _ownsCapture = true;

        private Rectangle _barRect;
        private Font _font, _fontSmall;
        private double _fps, _lastAnalysisMs;
        private int _mouseX = -1, _mouseY = -1;
        private bool _mouseIn;
        private FullscreenView _fullscreen;

        /// <summary>Supplies {title, artist, album} for the fullscreen overlay.</summary>
        public NowPlayingProvider NowPlaying { get; set; }

        /// <summary>Raised when the user picks a docked height; the plugin owns the host control.</summary>
        public Action<int> DockHeightRequested { get; set; }

        private const int ColorBarWidth = 40;

        public AnalyzerPanel(Settings settings, string storageDir)
        {
            _settings = settings;
            _storageDir = storageDir;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.Black;
            _font = new Font("Segoe UI", 8f);
            _fontSmall = new Font("Segoe UI", 7f);
            _lut = Palette.BuildLut(_settings.Palette);
            _scope.SetPalette(_lut);

            var menu = new ContextMenuStrip();
            menu.Opening += delegate
            {
                MenuFactory.Populate(menu, _settings, new MenuFactory.Options
                {
                    IsFullscreen = false,
                    ScrollPixels = _scope.Panes.Length > 0 ? _scope.Panes[0].SpectroRect.Width : 1,
                    IsFrozen = delegate { return _frozen; },
                    ToggleFreeze = delegate { _frozen = !_frozen; Invalidate(); },
                    ToggleFullscreen = ToggleFullscreen,
                    SetDockHeight = delegate(int px)
                    {
                        if (DockHeightRequested != null) DockHeightRequested(px);
                    },
                    Changed = OnSettingsChanged
                });
            };
            ContextMenuStrip = menu;
        }

        private void OnSettingsChanged(bool rebuildGeometry)
        {
            _lut = Palette.BuildLut(_settings.Palette);
            _scope.SetPalette(_lut);
            _scope.ResetRange();
            if (rebuildGeometry) RebuildGeometry();
            _settings.Save(_storageDir);
            Invalidate();
        }

        // ---------------- lifecycle ----------------

        /// <summary>Supplies an externally owned capture instead of opening one.</summary>
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
                _scope.Dispose();
                if (_font != null) _font.Dispose();
                if (_fontSmall != null) _fontSmall.Dispose();
            }
            base.Dispose(disposing);
        }

        public void NotifyTrackChanged()
        {
            _scope.ResetRange();
            FullscreenView fs = _fullscreen;
            if (fs != null && !fs.IsDisposed)
            {
                try { fs.BeginInvoke((MethodInvoker)fs.RefreshNowPlaying); } catch { }
            }
        }

        public void ToggleFullscreen()
        {
            if (_fullscreen != null && !_fullscreen.IsDisposed) { _fullscreen.Close(); return; }
            if (_capture == null) return;

            var view = new FullscreenView(_settings, _storageDir, _capture, NowPlaying);
            view.FormClosed += delegate
            {
                _paused = false;
                _fullscreen = null;
                _scope.ResetPanes();
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
                int w = Math.Max(16, ClientSize.Width);
                int h = Math.Max(16, ClientSize.Height);
                int barW = _settings.ShowColorBar ? ColorBarWidth : 0;
                _barRect = new Rectangle(w - barW, 0, barW, h);

                double sr = _capture != null ? _capture.SampleRate : 48000;
                _scope.Layout(new Rectangle(0, 0, Math.Max(16, w - barW), h), _settings, sr);
            }
        }

        // ---------------- analysis ----------------

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
                    if (!_paused)
                    {
                        try { lock (_gate) { _scope.Analyse(_capture, _settings, dt, _frozen); } }
                        catch { /* a transient frame error must not kill the thread */ }
                    }
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

        private void RequestRepaint() { _invalidatePending = false; Invalidate(); }

        // ---------------- painting ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Palette.Background(_lut));

            lock (_gate)
            {
                _scope.DrawPanes(g, _settings, false, _fontSmall, 1.0,
                                 _settings.ShowStatus ? 14 : 0);
                if (_settings.ShowGrid) _scope.DrawGrid(g, _settings, _fontSmall, 1.0, _settings.ShowStatus ? 16 : 0);
                // Keep the channel labels clear of the status line.
                if (_settings.ShowLabels) _scope.DrawPaneLabels(g, _fontSmall, 1.0, _settings.ShowStatus ? 14 : 0);
            }

            if (_settings.ShowColorBar && _barRect.Width > 0) DrawColorBar(g);
            if (_settings.ShowStatus) DrawStatus(g);
            if (_settings.ShowHud && _mouseIn)
                _scope.DrawHover(g, new Point(_mouseX, _mouseY), _settings, _font, _fontSmall);
        }

        private void DrawColorBar(Graphics g)
        {
            Rectangle r = _barRect;
            using (var bg = new SolidBrush(Color.FromArgb(255, 14, 14, 16)))
                g.FillRectangle(bg, r);

            int barX = r.Left + 5, barW = 11, top = r.Top + 12, bot = r.Bottom - 12;
            if (bot <= top) return;
            for (int y = top; y < bot; y++)
            {
                double t = 1.0 - (double)(y - top) / (bot - top);
                using (var b = new SolidBrush(Palette.ColorAt(_lut, t)))
                    g.FillRectangle(b, barX, y, barW, 1);
            }
            using (var outline = new Pen(Color.FromArgb(60, 255, 255, 255)))
                g.DrawRectangle(outline, barX, top, barW, bot - top);
            using (var brush = new SolidBrush(Color.FromArgb(190, 225, 225, 228)))
            {
                g.DrawString(_scope.CeilingDb.ToString("0"), _fontSmall, brush, barX + barW + 1, top - 4);
                g.DrawString(_scope.FloorDb.ToString("0"), _fontSmall, brush, barX + barW + 1, bot - 10);
            }
        }

        private void DrawStatus(Graphics g)
        {
            string src = _capture == null ? "no source" : _capture.Status;
            string text = string.Format("{0}  |  {1}  |  {2}  |  {3:0} fps  |  {4:0.0} ms  |  {5}{6}",
                src, _scope.Analyzer.DescribeResolution(), _settings.PairMode, _fps,
                _lastAnalysisMs, _settings.Preset, _frozen ? "  |  FROZEN" : "");
            using (var brush = new SolidBrush(Color.FromArgb(140, 210, 210, 215)))
                g.DrawString(text, _fontSmall, brush, 4, 2);
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

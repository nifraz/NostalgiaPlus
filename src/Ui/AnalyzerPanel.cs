using System;
using System.Diagnostics;
using System.Drawing;
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
        private readonly QuickBar _quick = new QuickBar();
        private Font _font, _fontSmall;
        private double _fps, _lastAnalysisMs;
        private readonly HoverInfo _hover = new HoverInfo();
        private bool _mouseDown, _dragged;
        private FullscreenView _fullscreen;
        private Bitmap _barCache;
        private int[] _barCacheLut;

        /// <summary>Host player access, handed on to the fullscreen view.</summary>
        public PlayerBridge Player { get; set; }

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
            RebuildFonts();
            _quick.Build(_settings, OnSettingsChanged);
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
                    StorageDir = _storageDir,
                    Owner = this,
                    Changed = OnSettingsChanged
                });
            };
            ContextMenuStrip = menu;
        }

        /// <summary>All text scales from one setting; see the fullscreen view.</summary>
        private void RebuildFonts()
        {
            float b = _settings.LabelFontSize;
            if (b < 5f) b = 5f; else if (b > 20f) b = 20f;
            if (_font != null) _font.Dispose();
            if (_fontSmall != null) _fontSmall.Dispose();
            _fontSmall = new Font("Segoe UI", b);
            _font = new Font("Segoe UI", b + 1.5f);
        }

        private void OnSettingsChanged(bool rebuildGeometry)
        {
            RebuildFonts();
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
                if (_barCache != null) { _barCache.Dispose(); _barCache = null; }
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

            var view = new FullscreenView(_settings, _storageDir, _capture, Player);
            view.FormClosed += delegate
            {
                _paused = false;
                _fullscreen = null;
                // Both views share one Settings, so anything changed while fullscreen was
                // up applies here too - including layout and palette. Only resetting the
                // panes left the panel drawing with stale geometry until the next resize.
                try { OnSettingsChanged(true); } catch { }
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

                // The quick bar takes reserved space off the bottom rather than floating
                // over the spectrogram, so nothing it covers is ever lost.
                Rectangle quickBar;
                Rectangle area = QuickBar.Reserve(new Rectangle(0, 0, Math.Max(16, w - barW), h),
                                                 _settings, _fontSmall, out quickBar);
                _quick.Layout(quickBar, _fontSmall, _settings.QuickBarCompact);

                double sr = _capture != null ? _capture.SampleRate : 48000;
                _scope.Layout(area, _settings, sr);
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
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(Palette.Background(_lut));

            // The status line is chrome over the image, so it starts below the scale
            // strip; the pane insets stay relative to the image, which already does.
            int chromeTop, inset;
            lock (_gate)
            {
                chromeTop = _scope.ChromeTop;
                inset = _settings.ShowStatus ? 14 : 0;
                _scope.DrawPanes(g, _settings, false, _fontSmall, 1.0, inset);
                if (_settings.ShowGrid)
                    _scope.DrawGrid(g, _settings, _fontSmall, 1.0,
                                    _settings.ShowStatus ? chromeTop + 16 : 0);
                // Keep the channel labels clear of the status line.
                if (_settings.ShowLabels) _scope.DrawPaneLabels(g, _fontSmall, 1.0, inset);
            }

            if (_settings.ShowColorBar && _barRect.Width > 0) DrawColorBar(g);
            _quick.Draw(g, _fontSmall, 1.0, _settings.QuickBarCompact);
            if (_settings.ShowStatus) DrawStatus(g, chromeTop);
            if (_settings.ShowHud && _hover.Active)
                _scope.DrawHover(g, _hover, _settings, _font, _fontSmall);
        }

        private void DrawColorBar(Graphics g)
        {
            Rectangle r = _barRect;
            using (var bg = new SolidBrush(Color.FromArgb(255, 14, 14, 16)))
                g.FillRectangle(bg, r);

            int barX = r.Left + 5, barW = 11, top = r.Top + 12, bot = r.Bottom - 12;
            if (bot <= top) return;

            // The ramp only changes when the palette or the height does, so render it
            // once instead of allocating a brush and filling a rectangle per pixel row.
            int barH = bot - top;
            if (_barCache == null || _barCache.Height != barH || _barCacheLut != _lut)
            {
                if (_barCache != null) _barCache.Dispose();
                _barCache = new Bitmap(1, barH);
                for (int y = 0; y < barH; y++)
                    _barCache.SetPixel(0, y, Palette.ColorAt(_lut, 1.0 - (double)y / barH));
                _barCacheLut = _lut;
            }
            g.DrawImage(_barCache, new Rectangle(barX, top, barW, barH),
                        0, 0, 1, barH, GraphicsUnit.Pixel);
            using (var outline = new Pen(Color.FromArgb(60, 255, 255, 255)))
                g.DrawRectangle(outline, barX, top, barW, bot - top);
            using (var brush = new SolidBrush(Color.FromArgb(190, 225, 225, 228)))
            {
                g.DrawString(_scope.CeilingDb.ToString("0"), _fontSmall, brush, barX + barW + 1, top - 4);
                g.DrawString(_scope.FloorDb.ToString("0"), _fontSmall, brush, barX + barW + 1, bot - 10);
            }
        }

        private void DrawStatus(Graphics g, int top)
        {
            string src = _capture == null ? "no source" : _capture.Status;
            string text = string.Format("{0}  |  {1}  |  {2}  |  {3:0} fps  |  {4:0.0} ms  |  {5}{6}",
                src, _scope.Analyzer.DescribeResolution(), _settings.PairMode, _fps,
                _lastAnalysisMs, _settings.Preset, _frozen ? "  |  FROZEN" : "");
            using (var brush = new SolidBrush(Color.FromArgb(140, 210, 210, 215)))
                g.DrawString(text, _fontSmall, brush, 4, top + 2);
        }

        // ---------------- interaction ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused) { try { Focus(); } catch { } }
            if (e.Button != MouseButtons.Left) return;
            // A press on a button is that button's, not the start of a measurement.
            if (_quick.Contains(e.Location)) return;
            _mouseDown = true;
            _dragged = false;
            _hover.Origin = e.Location;
            _hover.Measuring = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_quick.Click(e.Location)) { _mouseDown = false; Invalidate(); return; }
            _mouseDown = false;
            if (!_dragged) { _frozen = !_frozen; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_quick.SetHot(e.Location)) Invalidate();
            _hover.Cursor = e.Location;
            // No readout while the pointer is on the bar: there is no spectrum under it.
            _hover.Active = !_quick.Contains(e.Location);
            if (_mouseDown &&
                (Math.Abs(e.X - _hover.Origin.X) > 4 || Math.Abs(e.Y - _hover.Origin.Y) > 4))
            {
                _dragged = true;
                _hover.Measuring = true;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _quick.SetHot(new Point(-1, -1));
            _hover.Active = false;
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

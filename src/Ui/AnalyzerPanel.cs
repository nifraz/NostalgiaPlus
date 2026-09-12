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
    /// between them. Identical rendering to the fullscreen view, via <see cref="StereoScope"/>,
    /// and the same waveform lanes, centre deck and immersive treatment through
    /// <see cref="BottomBand"/> and <see cref="Immersion"/> - every setting means the
    /// same thing in both, so switching between them changes the size and nothing else.
    /// </summary>
    public sealed class AnalyzerPanel : UserControl
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private readonly Settings _settings;
        private readonly string _storageDir;
        private readonly StereoScope _scope = new StereoScope();
        // The same two the fullscreen view uses. The docked panel used to have neither,
        // which made it a different instrument rather than a smaller one.
        private readonly BottomBand _band = new BottomBand();
        private readonly Immersion _imm = new Immersion();
        private readonly object _gate = new object();

        private LoopbackCapture _capture;
        private int[] _lut;

        private Thread _worker;
        private volatile bool _running;
        private volatile bool _frozen;
        /// <summary>Player position when the image was frozen; -1 when it is live.</summary>
        private int _frozenAtMs = -1;
        private volatile bool _invalidatePending;
        private volatile bool _paused;
        private bool _ownsCapture = true;

        private Rectangle _barRect;
        private DateTime _infoUntil = DateTime.MinValue;
        private readonly QuickBar _quick = new QuickBar();
        private Font _font, _fontSmall;
        private double _fps, _lastAnalysisMs;
        private readonly HoverInfo _hover = new HoverInfo();
        private bool _mouseDown, _dragged;
        private FullscreenView _fullscreen;
        private Bitmap _barCache;
        private int[] _barCacheLut;

        private PlayerBridge _player;

        /// <summary>Host player access, shared with the deck and the fullscreen view.</summary>
        public PlayerBridge Player
        {
            get { return _player; }
            set
            {
                _player = value;
                // Handed over after construction, so the deck would otherwise sit empty
                // until the next track change - which on a paused player never comes.
                if (value != null) _band.SetTrack(value.SafeInfo());
            }
        }

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
                    ToggleSnapshot = ToggleSnapshot,
                    HasSnapshot = delegate { lock (_gate) { return _scope.HasSnapshot; } },
                    ToggleFreeze = delegate { SetFrozen(!_frozen); },
                    ToggleFullscreen = ToggleFullscreen,
                    ToggleImmersive = ToggleImmersive,
                    SetDockHeight = delegate(int px)
                    {
                        if (DockHeightRequested != null) DockHeightRequested(px);
                    },
                    StorageDir = _storageDir,
                    Owner = this,
                    SkinColour = Player == null ? null : Player.SkinColour,
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
                _band.Dispose();
                _imm.Dispose();
                if (_barCache != null) { _barCache.Dispose(); _barCache = null; }
                if (_font != null) _font.Dispose();
                if (_fontSmall != null) _fontSmall.Dispose();
            }
            base.Dispose(disposing);
        }

        public void NotifyTrackChanged()
        {
            _scope.ResetRange();
            // The docked deck shows what is playing too, so it needs telling.
            if (Player != null) _band.SetTrack(Player.SafeInfo());
            _band.Reset();
            _infoUntil = DateTime.UtcNow.AddSeconds(6);
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

                // Bottom-up: the band first, then the quick bar out of what is left,
                // then the panes out of the remainder. Everything here takes reserved
                // space rather than floating over the spectrogram, so nothing any of it
                // covers is ever lost.
                var view = new Rectangle(0, 0, Math.Max(16, w - barW), h);
                int bandH = BottomBand.HeightFor(_settings, h);

                Rectangle quickBar;
                Rectangle area = QuickBar.Reserve(
                    new Rectangle(view.X, view.Y, view.Width, Math.Max(16, h - bandH)),
                    _settings, _fontSmall, out quickBar);

                double sr = _capture != null ? _capture.SampleRate : 48000;
                _scope.Layout(area, _settings, sr);

                Rectangle gut = _scope.GutterRect;
                if (_settings.QuickBarSplit && gut.Width > 0)
                    _quick.Layout(quickBar, _fontSmall, _settings.QuickBarCompact, gut.Left, gut.Right);
                else
                    _quick.Layout(quickBar, _fontSmall, _settings.QuickBarCompact);

                _band.Layout(view, bandH, _settings, _scope.Panes, _fontSmall);
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
                        try
                        {
                            // Before the scope: the band consumes the ring from its own
                            // cursor, so the meters see every sample rather than
                            // whatever the FFT happened to take.
                            _band.Feed(_capture, _settings);
                            lock (_gate)
                            {
                                _scope.Analyse(_capture, _settings, dt, _frozen);
                                if (!_frozen && _scope.PushedColumn) _band.PushColumn();
                            }
                        }
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

            double centroid, brightness, pulse;
            ChannelPane[] panes;
            lock (_gate)
            {
                centroid = _scope.Features.Centroid;
                brightness = _scope.CentroidHz;
                pulse = _scope.Features.Pulse;
                panes = _scope.Panes;
            }
            int[] hued = _imm.UpdateHue(_settings, centroid);
            if (hued != null) { _lut = hued; lock (_gate) { _scope.SetPalette(_lut); } }

            var client = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            g.Clear(Settings.Pick(_settings.ColBackground, Palette.Background(_lut)));
            _imm.DrawBackdrop(g, _settings, client, Player, panes);

            double furniture = _settings.ShowOsd ? _imm.FurnitureAlpha(_settings) : 0.0;
            bool glow = _settings.Immersive && _settings.Glow;

            // The status line is chrome over the image, so it starts below the scale
            // strip; the pane insets stay relative to the image, which already does.
            int chromeTop, inset;
            lock (_gate)
            {
                chromeTop = _scope.ChromeTop;
                inset = _settings.ShowStatus ? 14 : 0;
                _scope.DrawPanes(g, _settings, glow, _fontSmall, furniture, inset);
                if (_settings.ShowGrid && furniture > 0.004)
                    _scope.DrawGrid(g, _settings, _fontSmall, furniture,
                                    _settings.ShowStatus ? chromeTop + 16 : 0);
                // Keep the channel labels clear of the status line.
                if (_settings.ShowLabels) _scope.DrawPaneLabels(g, _fontSmall, furniture, inset);
            }

            if (_settings.ShowOsd)
            {
                double infoAlpha = DateTime.UtcNow < _infoUntil ? 1.0 : 0.0;
                lock (_gate)
                    _band.Draw(g, _settings, _fontSmall, _font, furniture, infoAlpha,
                               _lut, Player, _scope.Features, brightness, panes);
            }

            if (_settings.ShowColorBar && _barRect.Width > 0) DrawColorBar(g);
            if (furniture > 0.004 && _settings.ShowOsd)
                _quick.Draw(g, _fontSmall, furniture, _settings.QuickBarCompact);
            if (_settings.ShowStatus && furniture > 0.004) DrawStatus(g, chromeTop);
            if (_settings.ShowHud && _hover.Active && _settings.ShowOsd && furniture > 0.004)
                _scope.DrawHover(g, _hover, _settings, _font, _fontSmall);

            _imm.DrawBeatFlare(g, _settings, client, _lut, pulse);
        }

        private void DrawColorBar(Graphics g)
        {
            Rectangle r = _barRect;
            using (var bg = new SolidBrush(Settings.PickKeepAlpha(
                       _settings.ColPanel, Color.FromArgb(255, 14, 14, 16))))
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
                // Centred on the end each value belongs to, like every other scale here.
                string hi = _scope.CeilingDb.ToString("0"), lo = _scope.FloorDb.ToString("0");
                float th = g.MeasureString(hi, _fontSmall).Height;
                g.DrawString(hi, _fontSmall, brush, barX + barW + 3, top - th / 2);
                g.DrawString(lo, _fontSmall, brush, barX + barW + 3, bot - th / 2);
            }
        }

        private void DrawStatus(Graphics g, int top)
        {
            string src = _capture == null ? "no source" : _capture.Status;
            string text = string.Format("{0}  |  {1}  |  {2}  |  {3:0} fps  |  {4:0.0} ms  |  {5}{6}",
                src, _scope.Analyzer.DescribeResolution(), _settings.PairMode, _fps,
                _lastAnalysisMs, _settings.Preset, _frozen ? "  |  FROZEN" : "");
            using (var brush = new SolidBrush(Settings.PickKeepAlpha(
                       _settings.ColAxisText, Color.FromArgb(140, 210, 210, 215))))
                g.DrawString(text, _fontSmall, brush, 4, top + 2);
        }

        private void ShowHelp()
        {
            MenuFactory.ShowHelp(new MenuFactory.Options
            {
                IsFullscreen = false,
                StorageDir = _storageDir,
                Owner = this,
            }, _settings);
        }

        // ---------------- interaction ----------------


        /// <summary>
        /// Freezing stops the image but not the player, so the position the image's time
        /// axis is measured back from is stamped here. Without it, double-clicking a
        /// column on a frozen image seeks to wherever the track has since got to.
        /// </summary>
        private void SetFrozen(bool frozen)
        {
            if (frozen && !_frozen)
                _frozenAtMs = Player == null ? -1 : Player.SafePosition();
            _frozen = frozen;
            Invalidate();
        }

        /// <summary>
        /// Jump to the moment a column was recorded. The image carries far more detail
        /// than a seek bar does - you can aim at a single hit - so the timeline is worth
        /// making clickable.
        /// </summary>
        private void SeekToImage(Point p)
        {
            if (!_settings.SeekOnImageClick || Player == null || Player.Seek == null) return;
            double back;
            lock (_gate) { if (!_scope.SecondsAgoAt(p, _settings, out back)) return; }

            int from = (_frozen && _frozenAtMs >= 0) ? _frozenAtMs : Player.SafePosition();
            int target = from - (int)(back * 1000.0);
            if (target < 0) target = 0;
            // Landing on the last instant of a track just starts the next one.
            int dur = Player.SafeDuration();
            if (dur > 1000 && target > dur - 1000) target = dur - 1000;
            try { Player.Seek(target); } catch { }
        }

        /// <summary>
        /// Hold the current average spectrum as an amber reference, or drop it. The
        /// comparison it answers - is this brighter than that - is asked and dismissed
        /// with the same key, so one action does both.
        /// </summary>
        public void ToggleSnapshot()
        {
            lock (_gate) { _scope.ToggleSnapshot(); }
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (_quick.Contains(e.Location) || _band.Contains(e.Location)) return;
            SeekToImage(e.Location);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused) { try { Focus(); } catch { } }
            if (e.Button != MouseButtons.Left) return;
            // A press on a button is that button's, not the start of a measurement.
            if (_quick.Contains(e.Location) || _band.Contains(e.Location)) return;
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
            if (_band.Click(e.Location, Player)) { _mouseDown = false; Invalidate(); return; }
            _mouseDown = false;
            if (!_dragged) SetFrozen(!_frozen);
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

        /// <summary>
        /// Entering immersive mode also applies the Immersive preset: the visual
        /// treatment cannot rescue a linear axis at the lowest resolution, which is
        /// what makes dense music render as an undifferentiated wall.
        /// </summary>
        public void ToggleImmersive()
        {
            if (_settings.Immersive)
            {
                _settings.Immersive = false;
                _settings.Preset = Preset.Custom;
            }
            else _settings.ApplyPreset(Preset.Immersive);
            OnSettingsChanged(true);
        }

        /// <summary>
        /// The same keys as the fullscreen view, so the two are not two things to
        /// learn. F11 swaps between them and everything else means what it does there.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            _imm.Touch();
            switch (keyData)
            {
                case Keys.F1: ShowHelp(); return true;
                case Keys.F11: ToggleFullscreen(); return true;
                case Keys.Space: SetFrozen(!_frozen); return true;
                case Keys.A: ToggleSnapshot(); return true;
                case Keys.I: ToggleImmersive(); return true;
                case Keys.W:
                    _settings.ShowWaveform = !_settings.ShowWaveform;
                    OnSettingsChanged(true); return true;
                case Keys.O:
                    _settings.ShowCenterDeck = !_settings.ShowCenterDeck;
                    OnSettingsChanged(true); return true;
                case Keys.G:
                    _settings.ShowGrid = !_settings.ShowGrid;
                    OnSettingsChanged(false); return true;
                case Keys.H:
                    _settings.ShowOsd = !_settings.ShowOsd;
                    OnSettingsChanged(false); return true;
                case Keys.B:
                    _settings.Style = QuickBar.Next(_settings.Style);
                    OnSettingsChanged(false); return true;
                case Keys.C:
                    _settings.PairMode = QuickBar.Next(_settings.PairMode);
                    OnSettingsChanged(true); return true;
                case Keys.M:
                    _settings.MirrorLeftPane = !_settings.MirrorLeftPane;
                    OnSettingsChanged(true); return true;
                case Keys.P:
                    _settings.Palette = QuickBar.Next(_settings.Palette);
                    _settings.Preset = Preset.Custom;
                    OnSettingsChanged(false); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}

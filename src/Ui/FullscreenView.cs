using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

        private readonly Settings _settings;
        private readonly string _storageDir;
        private readonly LoopbackCapture _capture;
        private readonly PlayerBridge _player;

        private readonly StereoScope _scope = new StereoScope();
        // The waveform lanes, the deck and the metering behind them, shared with the
        // docked panel so the two views are the same instrument at different sizes.
        private readonly BottomBand _band = new BottomBand();
        private readonly Immersion _imm = new Immersion();
        private readonly object _gate = new object();

        private int[] _lut;
        // Waveform extremes gathered since the last column was taken.
        private Thread _worker;
        private volatile bool _running;
        private volatile bool _frozen;
        /// <summary>Player position when the image was frozen; -1 when it is live.</summary>
        private int _frozenAtMs = -1;
        private volatile bool _invalidatePending;

        private Rectangle _scopeRect;
        private readonly QuickBar _quick = new QuickBar();
        // Sample pairs for the goniometer, refreshed each frame. Fixed length: the trace
        // wants a consistent number of points however long the capture chunk was.
        private Font _fontMid, _fontSmall, _fontTiny;
        private double _fps, _analysisMs, _paintMs;
        private DateTime _hintUntil, _infoUntil = DateTime.MinValue;
        private bool _cursorHidden;
        private readonly HoverInfo _hover = new HoverInfo();
        private bool _mouseDown, _dragged;


        public FullscreenView(Settings settings, string storageDir,
                              LoopbackCapture capture, PlayerBridge player)
        {
            _settings = settings;
            _storageDir = storageDir;
            _capture = capture;
            _player = player ?? new PlayerBridge();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            RebuildFonts();
            _lut = Palette.BuildLut(_settings.Palette, _imm.HueShift);
            _scope.SetPalette(_lut);
            _hintUntil = DateTime.UtcNow.AddSeconds(4);

            _quick.Build(_settings, OnSettingsChanged);

            var menu = new ContextMenuStrip();
            menu.Opening += delegate
            {
                Touch();
                MenuFactory.Populate(menu, _settings, new MenuFactory.Options
                {
                    IsFullscreen = true,
                    ScrollPixels = _scope.Panes.Length > 0 ? _scope.Panes[0].SpectroRect.Width : 1,
                    IsFrozen = delegate { return _frozen; },
                    ToggleSnapshot = ToggleSnapshot,
                    HasSnapshot = delegate { lock (_gate) { return _scope.HasSnapshot; } },
                    ToggleFreeze = delegate { SetFrozen(!_frozen); },
                    ToggleFullscreen = delegate { Close(); },
                    ToggleImmersive = ToggleImmersive,
                    StorageDir = _storageDir,
                    Owner = this,
                    SkinColour = _player == null ? null : _player.SkinColour,
                    Changed = OnSettingsChanged
                });
            };
            ContextMenuStrip = menu;
        }

        /// <summary>
        /// All text scales from one setting. Sizes are derived rather than independent so
        /// the hierarchy - title, meters, readout, axis - stays intact at any size.
        /// </summary>
        private void RebuildFonts()
        {
            float b = _settings.LabelFontSize;
            if (b < 5f) b = 5f; else if (b > 20f) b = 20f;
            if (_fontMid != null) _fontMid.Dispose();
            if (_fontSmall != null) _fontSmall.Dispose();
            if (_fontTiny != null) _fontTiny.Dispose();
            _fontMid = new Font("Segoe UI", b + 4f);
            _fontSmall = new Font("Segoe UI", b + 1.5f);
            _fontTiny = new Font("Segoe UI", b);
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
            RebuildFonts();
            _lut = Palette.BuildLut(_settings.Palette, _imm.HueShift);
            _scope.SetPalette(_lut);
            lock (_gate) { _scope.ResetRange(); }
            RebuildGeometry();
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
                _band.Dispose();
                _imm.Dispose();
                if (_fontMid != null) _fontMid.Dispose();
                if (_fontSmall != null) _fontSmall.Dispose();
                if (_fontTiny != null) _fontTiny.Dispose();
            }
            base.Dispose(disposing);
        }

        public void RefreshNowPlaying()
        {
            string[] info = _player.SafeInfo();
            if (info != null && info.Length >= 3)
            {
                // Straight to the deck: it is the only thing that shows them now.
                _band.SetTrack(info);
            }
            _infoUntil = DateTime.UtcNow.AddSeconds(6);
            lock (_gate) { _scope.ResetRange(); _band.Reset(); }
        }

        // ---------------- idle fading ----------------

        private void Touch() { _imm.Touch(); ShowCursorIfHidden(); }

        private void ShowCursorIfHidden()
        {
            if (_cursorHidden) { _cursorHidden = false; try { Cursor.Show(); } catch { } }
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

                // One band at the bottom carries both the waveform lanes and the deck,
                // so turning the lanes off does not take the deck with them.
                int bandH = BottomBand.HeightFor(_settings, h);

                // The bar sits between the panes and the waveform lanes, in space of its
                // own: floating it over the spectrogram hid the newest few seconds.
                Rectangle quickBar;
                _scopeRect = QuickBar.Reserve(new Rectangle(0, 0, w, Math.Max(16, h - bandH)),
                                              _settings, _fontTiny, out quickBar);
                double sr = _capture != null ? _capture.SampleRate : 48000;
                _scope.Layout(_scopeRect, _settings, sr);

                // Positioned after Layout, because splitting it needs the gutter, which
                // does not exist until the panes have been placed.
                Rectangle gut = _scope.GutterRect;
                if (_settings.QuickBarSplit && gut.Width > 0)
                    _quick.Layout(quickBar, _fontTiny, _settings.QuickBarCompact, gut.Left, gut.Right);
                else
                    _quick.Layout(quickBar, _fontTiny, _settings.QuickBarCompact);

                _band.Layout(new Rectangle(0, 0, w, h), bandH, _settings, _scope.Panes, _fontTiny);
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
            _band.Feed(cap, _settings);

            lock (_gate)
            {
                if (!_scope.Analyse(cap, _settings, dt, _frozen)) return;
                if (!_frozen && _scope.PushedColumn) _band.PushColumn();
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

            double centroid;
            ChannelPane[] panes;
            lock (_gate) { centroid = _scope.Features.Centroid; panes = _scope.Panes; }
            int[] lut = _imm.UpdateHue(_settings, centroid);
            if (lut != null) { _lut = lut; lock (_gate) { _scope.SetPalette(_lut); } }

            var client = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            g.Clear(Settings.Pick(_settings.ColBackground, Palette.Background(_lut)));
            _imm.DrawBackdrop(g, _settings, client, _player, panes);

            // The OSD switch gates every drawn annotation; gridlines stay because they
            // are part of reading the image rather than chrome on top of it.
            double furniture = _settings.ShowOsd ? _imm.FurnitureAlpha(_settings) : 0.0;
            if (furniture <= 0.001 && !_cursorHidden)
            {
                _cursorHidden = true;
                try { Cursor.Hide(); } catch { }
            }

            bool glow = _settings.Immersive && _settings.Glow;
            int chromeTop;
            double brightness;
            lock (_gate)
            {
                chromeTop = _scope.ChromeTop;
                brightness = _scope.CentroidHz;
                // Nothing is painted over the top of the image, so the panes start at
                // the top of their own bounds and the outer columns label the whole axis.
                _scope.DrawPanes(g, _settings, glow, _fontTiny, furniture, 0);
                if (_settings.ShowGrid && furniture > 0.004)
                    _scope.DrawGrid(g, _settings, _fontTiny, furniture, 0);
                _scope.DrawPaneLabels(g, _fontSmall, furniture, 0);
            }

            // Gated on the same fade as everything else: once it reaches zero the
            // cursor is hidden, and a readout for a pointer you cannot see is noise.
            if (_settings.ShowHud && _hover.Active && _settings.ShowOsd && furniture > 0.004)
                lock (_gate)
                    _scope.DrawHover(g, _hover, _settings, _fontSmall, _fontTiny, furniture);

            if (_settings.ShowOsd)
            {
                // A track change is announced at full strength for a few seconds even
                // when the furniture has faded away, which is the one behaviour of the
                // old corner overlay worth carrying across.
                double infoAlpha = DateTime.UtcNow < _infoUntil ? 1.0 : 0.0;
                lock (_gate)
                    _band.Draw(g, _settings, _fontTiny, _fontMid, furniture, infoAlpha,
                               _lut, _player, _scope.Features, brightness, panes);
            }

            if (furniture > 0.004 && _settings.ShowOsd)
                _quick.Draw(g, _fontTiny, furniture, _settings.QuickBarCompact);

            // Last, over everything: a beat is felt at the edge of vision, not read.
            double pulse;
            lock (_gate) { pulse = _scope.Features.Pulse; }
            _imm.DrawBeatFlare(g, _settings, client, _lut, pulse);

            if (_settings.ShowOsd) DrawHint(g, chromeTop);
        }

        private void DrawHint(Graphics g, int chromeTop)
        {
            if (DateTime.UtcNow > _hintUntil) return;
            string text = (_settings.Immersive ? "IMMERSIVE   " : "") +
                          "Click a button to cycle it    Right-click for all options    " +
                          "I immersive    O deck    A compare    H hide OSD    Esc exit    " +
                          "Space freeze    " +
                          _scope.Analyzer.DescribeResolution() + "    " +
                          _fps.ToString("0") + " fps  " + _analysisMs.ToString("0.0") + " ms dsp  " +
                          _paintMs.ToString("0.0") + " ms paint";
            SizeF sz = g.MeasureString(text, _fontSmall);
            float x = (ClientSize.Width - sz.Width) / 2;
            // Just under the scale strip. It used to clear an overlay bar that is gone.
            float y = chromeTop + 12;
            using (var back = new SolidBrush(Color.FromArgb(200, 8, 8, 10)))
                g.FillRectangle(back, x - 12, y - 6, sz.Width + 24, sz.Height + 12);
            using (var b = new SolidBrush(Color.FromArgb(225, 235, 235, 240)))
                g.DrawString(text, _fontSmall, b, x, y);
        }

        // ---------------- input ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _quick.SetHot(e.Location);
            _hover.Cursor = e.Location;
            // No readout while the pointer is on the bar: there is no spectrum under it.
            _hover.Active = !_quick.Contains(e.Location) && !_band.Contains(e.Location);
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
            _quick.SetHot(new Point(-1, -1));
            _hover.Active = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_quick.Click(e.Location)) { _mouseDown = false; Invalidate(); return; }
            if (_band.Click(e.Location, _player)) { _mouseDown = false; Invalidate(); return; }
            _mouseDown = false;
            // A click freezes so a moment can be read without it scrolling away; a drag
            // is a measurement and leaves its result on screen until the next press.
            if (!_dragged) SetFrozen(!_frozen);
        }


        /// <summary>
        /// Freezing stops the image but not the player, so the position the image's time
        /// axis is measured back from is stamped here. Without it, double-clicking a
        /// column on a frozen image seeks to wherever the track has since got to.
        /// </summary>
        private void SetFrozen(bool frozen)
        {
            if (frozen && !_frozen)
                _frozenAtMs = _player == null ? -1 : _player.SafePosition();
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
            if (!_settings.SeekOnImageClick || _player == null || _player.Seek == null) return;
            double back;
            lock (_gate) { if (!_scope.SecondsAgoAt(p, _settings, out back)) return; }

            int from = (_frozen && _frozenAtMs >= 0) ? _frozenAtMs : _player.SafePosition();
            int target = from - (int)(back * 1000.0);
            if (target < 0) target = 0;
            // Landing on the last instant of a track just starts the next one.
            int dur = _player.SafeDuration();
            if (dur > 1000 && target > dur - 1000) target = dur - 1000;
            try { _player.Seek(target); } catch { }
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
            Touch();
            if (e.Button != MouseButtons.Left) return;
            // A press on a control is that control's, not the start of a measurement.
            if (_quick.Contains(e.Location) || _band.Contains(e.Location)) return;
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
                case Keys.F1:
                    MenuFactory.ShowHelp(new MenuFactory.Options
                    {
                        IsFullscreen = true,
                        StorageDir = _storageDir,
                        Owner = this,
                    }, _settings);
                    return true;
                case Keys.Escape:
                case Keys.F11: Close(); return true;
                case Keys.Space: SetFrozen(!_frozen); return true;
                case Keys.A:
                    ToggleSnapshot();
                    _hintUntil = DateTime.UtcNow.AddSeconds(1.5);
                    return true;
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

        public void ToggleImmersive()
        {
            if (_settings.Immersive)
            {
                _settings.Immersive = false;
                _settings.Preset = Preset.Custom;
            }
            else _settings.ApplyPreset(Preset.Immersive);
            _hintUntil = DateTime.UtcNow.AddSeconds(3);
            OnSettingsChanged(true);
        }
    }
}

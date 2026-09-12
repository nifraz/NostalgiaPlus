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
        private readonly LoudnessMeter _meter = new LoudnessMeter();
        private readonly object _gate = new object();

        private WaveformRing _wfA, _wfB;
        private int[] _lut;
        private double[] _chunkL = new double[8192], _chunkR = new double[8192];
        private long _meterCursor = -1;
        // Waveform extremes gathered since the last column was taken.
        private float _accLoA, _accHiA, _accLoB, _accHiB;

        private Thread _worker;
        private volatile bool _running;
        private volatile bool _frozen;
        /// <summary>Player position when the image was frozen; -1 when it is live.</summary>
        private int _frozenAtMs = -1;
        private volatile bool _invalidatePending;

        private Rectangle _scopeRect, _waveARect, _waveBRect;
        private readonly QuickBar _quick = new QuickBar();
        private readonly CenterDeck _deck = new CenterDeck();
        private readonly DeckInputs _deckInputs = new DeckInputs();
        // Sample pairs for the goniometer, refreshed each frame. Fixed length: the trace
        // wants a consistent number of points however long the capture chunk was.
        private readonly float[] _gonL = new float[1024];
        private readonly float[] _gonR = new float[1024];
        private int _gonCount;

        // Album art, reduced once per track to a small bitmap that upscales into a
        // blur. Decoding base64 and blurring at screen size per frame would cost more
        // than everything else drawn put together.
        private Bitmap _backdrop;
        private string _backdropKey;
        private int _backdropPct = -1;
        private double _hueShift;

        private Font _fontMid, _fontSmall, _fontTiny;
        private double _fps, _analysisMs, _paintMs;
        private DateTime _hintUntil, _lastActivity = DateTime.UtcNow, _infoUntil = DateTime.MinValue;
        private bool _cursorHidden;
        private readonly HoverInfo _hover = new HoverInfo();
        private bool _mouseDown, _dragged;

        private const double IdleHoldSeconds = 3.0;
        private const double IdleFadeSeconds = 1.5;

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
            _lut = Palette.BuildLut(_settings.Palette, _hueShift);
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
            _lut = Palette.BuildLut(_settings.Palette, _hueShift);
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
                _deck.Dispose();
                if (_backdrop != null) { _backdrop.Dispose(); _backdrop = null; }
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
                // A short array is fine - the host may not answer for every field.
                _deck.Title = info[0] ?? "";
                _deck.Artist = info[1] ?? "";
                _deck.Album = info[2] ?? "";
                _deck.Composer = info.Length > 3 ? (info[3] ?? "") : "";
                _deck.Year = info.Length > 4 ? (info[4] ?? "") : "";
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

                // One band at the bottom carries both the waveform lanes and the deck,
                // so turning the lanes off does not take the deck with them.
                int waveH = 0;
                if (_settings.FsShowWaveform && _settings.WaveHeightPct > 0)
                    waveH = Math.Max(24, h * Math.Min(40, _settings.WaveHeightPct) / 100);

                // The band is as tall as the taller of the two things in it. The deck
                // needs real height for its rows to stay usable, and a thin waveform
                // setting should not shrink it - the lanes keep their own height and
                // sit at the bottom of the band.
                int bandH = waveH;
                if (_settings.ShowCenterDeck)
                {
                    int want = Math.Max(CenterDeck.PreferredHeight, _settings.DeckHeightPx);
                    bandH = Math.Max(bandH, Math.Min(h / 3, want));
                }

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

                ChannelPane[] panes = _scope.Panes;
                int waveTop = h - waveH;   // lanes sit at the bottom of the band
                bool lanes = _settings.FsShowWaveform && waveH > 0;
                _waveARect = lanes && panes.Length > 0
                    ? new Rectangle(panes[0].SpectroRect.X, waveTop, panes[0].SpectroRect.Width, waveH)
                    : Rectangle.Empty;
                _waveBRect = lanes && panes.Length > 1
                    ? new Rectangle(panes[1].SpectroRect.X, waveTop, panes[1].SpectroRect.Width, waveH)
                    : Rectangle.Empty;

                // The deck takes the gap the lanes leave in the middle - the two graph
                // strips plus the gutter. With the lanes off it gets a centred slot of
                // its own instead, so the block does not disappear with them.
                Rectangle deck = Rectangle.Empty;
                if (_settings.ShowCenterDeck && bandH > 0)
                {
                    int deckTop = h - bandH;
                    if (panes.Length > 1)
                    {
                        // The gap the lanes leave: the two graph strips plus the gutter.
                        // Measured from the panes rather than the lanes so it is there
                        // even when the lanes are switched off.
                        int gl = Math.Min(panes[0].SpectroRect.Right, panes[1].SpectroRect.Right);
                        int gr = Math.Max(panes[0].SpectroRect.Left, panes[1].SpectroRect.Left);
                        if (gr > gl) deck = new Rectangle(gl, deckTop, gr - gl, bandH);
                    }
                    if (deck.Width == 0)
                    {
                        int dw = Math.Min(700, w - 40);
                        deck = new Rectangle((w - dw) / 2, deckTop, dw, bandH);
                    }
                }
                _deck.Layout(deck, _settings, _fontTiny);

                int capA = Math.Max(8, _waveARect.Width + 4);
                if (_wfA == null) _wfA = new WaveformRing(capA); else _wfA.Resize(capA);
                int capB = Math.Max(8, _waveBRect.Width + 4);
                if (_wfB == null) _wfB = new WaveformRing(capB); else _wfB.Resize(capB);

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

                // Decimated to a fixed count: the goniometer wants a consistent trace
                // length, and drawing every sample of a large chunk would cost far more
                // than it shows.
                int want = Math.Min(_gonL.Length, got);
                int step = Math.Max(1, got / Math.Max(1, want));
                int n = 0;
                for (int i = 0; i < got && n < _gonL.Length; i += step, n++)
                {
                    _gonL[n] = (float)_chunkL[i];
                    _gonR[n] = (float)_chunkR[i];
                }
                _gonCount = n;
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

            // Carried across frames rather than pushed straight away: the spectrogram
            // takes a column only every ScrollDivider frames, and a waveform advancing
            // once per frame covered a different span of time at every speed but 1/1 -
            // so a transient did not line up with the column that produced it. Keeping
            // the extremes here also means no audio is skipped between columns.
            if (loA < _accLoA) _accLoA = loA;
            if (hiA > _accHiA) _accHiA = hiA;
            if (loB < _accLoB) _accLoB = loB;
            if (hiB > _accHiB) _accHiB = hiB;

            lock (_gate)
            {
                if (!_scope.Analyse(cap, _settings, dt, _frozen)) return;
                if (!_frozen && _scope.PushedColumn)
                {
                    if (_wfA != null) _wfA.Push(_accLoA, _accHiA);
                    if (_wfB != null) _wfB.Push(_accLoB, _accHiB);
                    _accLoA = _accHiA = _accLoB = _accHiB = 0;
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

        /// <summary>
        /// Rebuilds the palette when the music's brightness has moved far enough to see.
        ///
        /// Only new spectrogram columns take the new colours - the ones already drawn
        /// keep the hue they were pushed with - so the image ends up carrying its own
        /// recent history in colour as well as in shape.
        /// </summary>
        private void UpdateHue()
        {
            double want = 0;
            if (_settings.ImmColourFollows && _settings.FsImmersive)
            {
                double centre;
                lock (_gate) { centre = _scope.Features.Centroid; }
                want = (centre - 0.5) * 2.0 * _settings.ColourFollowDegrees;
            }
            // A degree either way is invisible; rebuilding the table for it is not free.
            if (Math.Abs(want - _hueShift) < 1.0) return;
            _hueShift = want;
            _lut = Palette.BuildLut(_settings.Palette, _hueShift);
            lock (_gate) { _scope.SetPalette(_lut); }
        }

        /// <summary>
        /// The album art behind everything, blurred and dimmed.
        ///
        /// Drawn as the ground rather than over the top, so it shows through wherever
        /// there is no data - the graph strips, the margins, the quiet parts of the
        /// spectrogram - and is covered wherever there is. That is what makes it read
        /// as ambient light behind the analysis instead of a wash over it.
        /// </summary>
        private void DrawBackdrop(Graphics g)
        {
            int pct = Math.Max(0, Math.Min(60, _settings.BackdropPct));
            if (pct == 0) return;

            string data = _player.SafeArtwork();
            if (string.IsNullOrEmpty(data))
            {
                if (_backdrop != null) { _backdrop.Dispose(); _backdrop = null; }
                _backdropKey = null;
                return;
            }
            if (data != _backdropKey || pct != _backdropPct)
            {
                _backdropKey = data;
                _backdropPct = pct;
                if (_backdrop != null) { _backdrop.Dispose(); _backdrop = null; }
                try
                {
                    byte[] bytes = Convert.FromBase64String(data);
                    using (var ms = new System.IO.MemoryStream(bytes))
                    using (var full = new Bitmap(ms))
                    {
                        // Reduced to 40px and upscaled bilinearly at paint time: a real
                        // blur kernel at 1920x1080 is not worth its cost for something
                        // deliberately out of focus.
                        //
                        // The dimming is baked in here rather than applied during the
                        // upscale. A ColorMatrix costs one matrix multiply per
                        // *destination* pixel - two million of them per frame - and
                        // measured at twenty milliseconds. Applied to the 40x40 source
                        // once per track it is free, and the upscale becomes a plain
                        // premultiplied blend.
                        _backdrop = new Bitmap(40, 40, PixelFormat.Format32bppPArgb);
                        using (var bg = Graphics.FromImage(_backdrop))
                        using (var attr = new ImageAttributes())
                        {
                            var cm = new ColorMatrix();
                            cm.Matrix33 = pct / 100f;
                            attr.SetColorMatrix(cm);
                            bg.CompositingMode = CompositingMode.SourceCopy;
                            bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            bg.DrawImage(full, new Rectangle(0, 0, 40, 40),
                                         0, 0, full.Width, full.Height, GraphicsUnit.Pixel, attr);
                        }
                    }
                }
                catch { _backdrop = null; }
            }
            if (_backdrop == null) return;

            // Only where it can actually be seen. The spectrograms are opaque blits, so
            // every backdrop pixel under one is blended and then thrown away - and they
            // are most of the screen.
            var old = g.InterpolationMode;
            Region clip = g.Clip;
            try
            {
                ChannelPane[] panes;
                lock (_gate) { panes = _scope.Panes; }
                for (int i = 0; i < panes.Length; i++)
                    if (panes[i].SpectroRect.Width > 0) g.ExcludeClip(panes[i].SpectroRect);

                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_backdrop, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                            0, 0, _backdrop.Width, _backdrop.Height, GraphicsUnit.Pixel);
            }
            finally
            {
                g.Clip = clip;
                g.InterpolationMode = old;
            }
        }

        /// <summary>
        /// A flare along the screen edges on each onset.
        ///
        /// Four gradient bars rather than a full-screen vignette: the edges are where
        /// the eye catches movement without being pulled off the analysis, and it is
        /// about a sixth of the pixels a vignette would blend.
        /// </summary>
        private void DrawBeatFlare(Graphics g, double pulse)
        {
            if (pulse <= 0.02) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            // The band recedes as well as fades, so a decaying beat costs progressively
            // less to draw - and reads more like a flare than a light being dimmed.
            int band = (int)(Math.Max(20, Math.Min(48, h / 22)) * (0.35 + 0.65 * pulse));
            if (band < 6) return;
            Color c = Palette.ColorAt(_lut, 0.9);
            int a = (int)(pulse * 90);
            if (a < 2) return;
            Color hot = Color.FromArgb(a, c), gone = Color.FromArgb(0, c);

            using (var top = new LinearGradientBrush(new Rectangle(0, 0, w, band), hot, gone, LinearGradientMode.Vertical))
                g.FillRectangle(top, 0, 0, w, band);
            using (var bot = new LinearGradientBrush(new Rectangle(0, h - band, w, band), gone, hot, LinearGradientMode.Vertical))
                g.FillRectangle(bot, 0, h - band, w, band);
            using (var left = new LinearGradientBrush(new Rectangle(0, 0, band, h), hot, gone, LinearGradientMode.Horizontal))
                g.FillRectangle(left, 0, 0, band, h);
            using (var right = new LinearGradientBrush(new Rectangle(w - band, 0, band, h), gone, hot, LinearGradientMode.Horizontal))
                g.FillRectangle(right, w - band, 0, band, h);
        }

        private void PaintFrame(Graphics g)
        {
            // GDI+ antialiased text is by far the most expensive thing drawn per frame
            // once the spectrogram is a blit. Grid-fit rendering is several times faster
            // and, at these sizes on a dark ground, indistinguishable.
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            UpdateHue();
            g.Clear(Settings.Pick(_settings.ColBackground, Palette.Background(_lut)));
            if (_settings.ImmBackdrop && _settings.FsImmersive) DrawBackdrop(g);
            // The OSD switch gates every drawn annotation; gridlines stay because they
            // are part of reading the image rather than chrome on top of it.
            double furniture = _settings.FsShowOsd ? FurnitureAlpha() : 0.0;
            if (furniture <= 0.001 && !_cursorHidden)
            {
                _cursorHidden = true;
                try { Cursor.Hide(); } catch { }
            }

            bool glow = _settings.FsImmersive && _settings.FsGlow;
            // With the track info and the meters moved into the deck, nothing is painted
            // over the top of the image any more. Both insets go with the bar: the time
            // marks start at the top of the pane again, and the outer frequency labels
            // no longer skip the top 84px of the axis to stay out from under it.
            int chromeTop;
            lock (_gate)
            {
                chromeTop = _scope.ChromeTop;
                _scope.DrawPanes(g, _settings, glow, _fontTiny, furniture, 0);
                if (_settings.ShowGrid && furniture > 0.004)
                    _scope.DrawGrid(g, _settings, _fontTiny, furniture, 0);
                _scope.DrawPaneLabels(g, _fontSmall, furniture, 0);
            }

            // Gated on the same fade as everything else: once it reaches zero the
            // cursor is hidden, and a readout for a pointer you cannot see is noise.
            if (_settings.ShowHud && _hover.Active && _settings.FsShowOsd && furniture > 0.004)
            {
                lock (_gate)
                {
                    _scope.DrawHover(g, _hover, _settings, _fontSmall, _fontTiny, furniture);
                }
            }
            if (_settings.FsShowWaveform && _waveARect.Height > 0) DrawWaveforms(g);
            if (_settings.ShowCenterDeck && _settings.FsShowOsd)
            {
                // A track change is announced at full strength for a few seconds even
                // when the furniture has faded away, which is the one behaviour of the
                // old corner overlay worth carrying across.
                double infoAlpha = DateTime.UtcNow < _infoUntil ? 1.0 : 0.0;
                lock (_gate)
                {
                    _deckInputs.Lut = _lut;
                    _deckInputs.Meter = _meter;
                    _deckInputs.Player = _player;
                    _deckInputs.Features = _scope.Features;
                    _deckInputs.BrightnessHz = _scope.CentroidHz;
                    _deckInputs.GonL = _gonL;
                    _deckInputs.GonR = _gonR;
                    _deckInputs.GonCount = _gonCount;
                    _deck.Draw(g, _settings, _fontTiny, _fontMid, furniture, infoAlpha, _deckInputs);
                }
            }
            if (furniture > 0.004 && _settings.FsShowOsd)
                _quick.Draw(g, _fontTiny, furniture, _settings.QuickBarCompact);
            // Last, over everything: a beat is felt at the edge of vision, not read.
            if (_settings.ImmBeatReactive && _settings.FsImmersive)
            {
                double pulse;
                lock (_gate) { pulse = _scope.Features.Pulse; }
                DrawBeatFlare(g, pulse);
            }
            if (_settings.FsShowOsd) DrawHint(g, chromeTop);
        }

        private void DrawWaveforms(Graphics g)
        {
            using (var bg = new SolidBrush(Settings.PickKeepAlpha(
                       _settings.ColPanel, Color.FromArgb(255, 10, 10, 12))))
                g.FillRectangle(bg, 0, _waveARect.Y, ClientSize.Width, _waveARect.Height);

            Color c = Settings.Pick(_settings.ColWaveform, Palette.ColorAt(_lut, 0.86));
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

        private void DrawHint(Graphics g, int chromeTop)
        {
            if (DateTime.UtcNow > _hintUntil) return;
            string text = (_settings.FsImmersive ? "IMMERSIVE   " : "") +
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
            _hover.Active = !_quick.Contains(e.Location) && !_deck.Contains(e.Location);
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
            if (_deck.Click(e.Location, _player)) { _mouseDown = false; Invalidate(); return; }
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
            if (_quick.Contains(e.Location) || _deck.Contains(e.Location)) return;
            SeekToImage(e.Location);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Touch();
            if (e.Button != MouseButtons.Left) return;
            // A press on a control is that control's, not the start of a measurement.
            if (_quick.Contains(e.Location) || _deck.Contains(e.Location)) return;
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
                    _settings.FsShowWaveform = !_settings.FsShowWaveform;
                    OnSettingsChanged(true); return true;
                case Keys.O:
                    _settings.ShowCenterDeck = !_settings.ShowCenterDeck;
                    OnSettingsChanged(true); return true;
                case Keys.G:
                    _settings.ShowGrid = !_settings.ShowGrid;
                    OnSettingsChanged(false); return true;
                case Keys.H:
                    _settings.FsShowOsd = !_settings.FsShowOsd;
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

using System;
using System.Drawing;
using NostalgiaPlus.Audio;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Render;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// The strip along the bottom of either view: a scrolling waveform lane under each
    /// pane, and the centre deck in the gap they leave between them.
    ///
    /// All of this was fullscreen-only, which made the docked panel a different
    /// instrument rather than a smaller one - no loudness figures, no goniometer, no
    /// transport, and no waveform to line the spectrogram up against. Everything here
    /// needs the same three things in both views: the audio ring, the pane geometry and
    /// the settings. So it lives in one place and each view owns an instance.
    /// </summary>
    public sealed class BottomBand : IDisposable
    {
        private readonly LoudnessMeter _meter = new LoudnessMeter();
        private readonly CenterDeck _deck = new CenterDeck();
        private readonly DeckInputs _inputs = new DeckInputs();

        private WaveformRing _wfA, _wfB;
        private Rectangle _waveA, _waveB, _band;
        private PointF[] _poly = new PointF[0];

        private double[] _chunkL = new double[8192], _chunkR = new double[8192];
        private long _cursor = -1;
        private float _accLoA, _accHiA, _accLoB, _accHiB;

        // Decimated to a fixed count: the goniometer wants a consistent trace length,
        // and drawing every sample of a large chunk would cost far more than it shows.
        private readonly float[] _gonL = new float[1024];
        private readonly float[] _gonR = new float[1024];
        private int _gonCount;

        public LoudnessMeter Meter { get { return _meter; } }
        public CenterDeck Deck { get { return _deck; } }
        /// <summary>True when a lane was actually placed, so the view can skip drawing.</summary>
        public bool HasLanes { get { return _waveA.Height > 0; } }

        /// <summary>
        /// How much height the band wants off the bottom of a view.
        ///
        /// One band carries both the lanes and the deck, so switching the lanes off does
        /// not take the deck with them, and a thin waveform setting does not crush it -
        /// the band is as tall as the taller of the two, and the lanes sit at its foot.
        /// </summary>
        public static int HeightFor(Settings s, int viewHeight)
        {
            int waveH = 0;
            if (s.ShowWaveform && s.WaveHeightPct > 0)
                waveH = Math.Max(24, viewHeight * Math.Min(40, s.WaveHeightPct) / 100);

            int band = waveH;
            if (s.ShowCenterDeck)
            {
                int want = Math.Max(CenterDeck.PreferredHeight, s.DeckHeightPx);
                band = Math.Max(band, Math.Min(viewHeight / 3, want));
            }
            // Never more than the view can spare. A docked panel is a few hundred pixels
            // tall, and a deck that leaves no spectrogram is not a trade anyone wants.
            return Math.Min(band, Math.Max(0, viewHeight - 60));
        }

        /// <summary>
        /// Places the lanes and the deck. Call after the panes have been laid out: the
        /// lanes line up with the spectrograms, and the deck fills the gap between them.
        /// </summary>
        /// <param name="view">The columns the band spans - the client area less any colour bar.</param>
        public void Layout(Rectangle view, int bandH, Settings s, ChannelPane[] panes, Font font)
        {
            _waveA = _waveB = Rectangle.Empty;
            _band = bandH > 0 ? new Rectangle(view.X, view.Bottom - bandH, view.Width, bandH)
                              : Rectangle.Empty;
            if (bandH <= 0) { _deck.Layout(Rectangle.Empty, s, font); Rings(0, 0); return; }

            int waveH = 0;
            if (s.ShowWaveform && s.WaveHeightPct > 0)
                waveH = Math.Min(bandH, Math.Max(24, view.Height * Math.Min(40, s.WaveHeightPct) / 100));

            int waveTop = view.Bottom - waveH;      // lanes sit at the foot of the band
            bool lanes = s.ShowWaveform && waveH > 0 && panes != null;
            if (lanes && panes.Length > 0)
                _waveA = new Rectangle(panes[0].SpectroRect.X, waveTop,
                                       panes[0].SpectroRect.Width, waveH);
            if (lanes && panes.Length > 1)
                _waveB = new Rectangle(panes[1].SpectroRect.X, waveTop,
                                       panes[1].SpectroRect.Width, waveH);

            // The deck takes the gap the lanes leave in the middle - the two graph strips
            // plus the gutter. Measured from the panes rather than the lanes so it is
            // there even when the lanes are switched off. With one pane it gets a centred
            // slot of its own instead, so the block does not disappear with them.
            Rectangle deck = Rectangle.Empty;
            if (s.ShowCenterDeck)
            {
                if (panes != null && panes.Length > 1)
                {
                    int gl = Math.Min(panes[0].SpectroRect.Right, panes[1].SpectroRect.Right);
                    int gr = Math.Max(panes[0].SpectroRect.Left, panes[1].SpectroRect.Left);
                    if (gr > gl) deck = new Rectangle(gl, _band.Y, gr - gl, bandH);
                }
                if (deck.Width == 0)
                {
                    int dw = Math.Min(700, view.Width - 40);
                    if (dw > 0) deck = new Rectangle(view.X + (view.Width - dw) / 2, _band.Y, dw, bandH);
                }
            }
            _deck.Layout(deck, s, font);
            Rings(_waveA.Width, _waveB.Width);
        }

        private void Rings(int a, int b)
        {
            int capA = Math.Max(8, a + 4);
            if (_wfA == null) _wfA = new WaveformRing(capA); else _wfA.Resize(capA);
            int capB = Math.Max(8, b + 4);
            if (_wfB == null) _wfB = new WaveformRing(capB); else _wfB.Resize(capB);
        }

        // ---------------- analysis ----------------

        /// <summary>
        /// Reads the audio the meters and the lanes need. Call once per analysis frame,
        /// before the scope analyses - it consumes the ring from its own cursor, so it
        /// sees every sample rather than whatever the FFT happened to take.
        /// </summary>
        public void Feed(LoopbackCapture cap, Settings s)
        {
            if (cap == null) return;
            _meter.Configure(cap.SampleRate);
            if (_cursor < 0) _cursor = Math.Max(0, cap.Ring.TotalFrames - 1024);

            long next;
            int got = cap.Ring.ReadRange(_cursor, _chunkL, _chunkR, _chunkL.Length, out next);
            _cursor = next;
            if (got <= 0) return;

            _meter.Process(_chunkL, _chunkR, got);

            int want = Math.Min(_gonL.Length, got);
            int step = Math.Max(1, got / Math.Max(1, want));
            int n = 0;
            for (int i = 0; i < got && n < _gonL.Length; i += step, n++)
            {
                _gonL[n] = (float)_chunkL[i];
                _gonR[n] = (float)_chunkR[i];
            }
            _gonCount = n;

            float loA = 0, hiA = 0, loB = 0, hiB = 0;
            bool ms = s.PairMode == ChannelPairMode.MidSide;
            for (int i = 0; i < got; i++)
            {
                double l = _chunkL[i], r = _chunkR[i];
                float a = (float)(ms ? (l + r) * 0.5 : l);
                float b = (float)(ms ? (l - r) * 0.5 : r);
                if (a < loA) loA = a; if (a > hiA) hiA = a;
                if (b < loB) loB = b; if (b > hiB) hiB = b;
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
        }

        /// <summary>Advances the lanes one column. Call only when the scope pushed one.</summary>
        public void PushColumn()
        {
            if (_wfA != null) _wfA.Push(_accLoA, _accHiA);
            if (_wfB != null) _wfB.Push(_accLoB, _accHiB);
            _accLoA = _accHiA = _accLoB = _accHiB = 0;
        }

        /// <summary>Everything that is about one track and not the next.</summary>
        public void Reset() { _meter.Reset(); }

        /// <summary>{title, artist, album, composer, year}; a shorter array is read as far as it goes.</summary>
        public void SetTrack(string[] info)
        {
            if (info == null) return;
            _deck.Title = info.Length > 0 ? (info[0] ?? "") : "";
            _deck.Artist = info.Length > 1 ? (info[1] ?? "") : "";
            _deck.Album = info.Length > 2 ? (info[2] ?? "") : "";
            _deck.Composer = info.Length > 3 ? (info[3] ?? "") : "";
            _deck.Year = info.Length > 4 ? (info[4] ?? "") : "";
        }

        // ---------------- drawing ----------------

        /// <param name="infoAlpha">
        /// Track info only, so a track change announces itself at full strength even
        /// when the rest of the furniture has faded out.
        /// </param>
        public void Draw(Graphics g, Settings s, Font tiny, Font mid, double alpha,
                         double infoAlpha, int[] lut, PlayerBridge player,
                         MusicFeatures features, double brightnessHz, ChannelPane[] panes)
        {
            if (s.ShowWaveform && _waveA.Height > 0) DrawWaveforms(g, s, lut, panes);
            if (!s.ShowCenterDeck) return;

            _inputs.Lut = lut;
            _inputs.Meter = _meter;
            _inputs.Player = player;
            _inputs.Features = features;
            _inputs.BrightnessHz = brightnessHz;
            _inputs.GonL = _gonL;
            _inputs.GonR = _gonR;
            _inputs.GonCount = _gonCount;
            _deck.Draw(g, s, tiny, mid, alpha, infoAlpha, _inputs);
        }

        private void DrawWaveforms(Graphics g, Settings s, int[] lut, ChannelPane[] panes)
        {
            using (var bg = new SolidBrush(Settings.PickKeepAlpha(
                       s.ColPanel, Color.FromArgb(255, 10, 10, 12))))
                g.FillRectangle(bg, _band.X, _waveA.Y, _band.Width, _waveA.Height);

            Color c = Settings.Pick(s.ColWaveform, Palette.ColorAt(lut, 0.86));
            using (var pen = new Pen(Color.FromArgb(215, c)))
            using (var mid = new Pen(Color.FromArgb(45, 255, 255, 255)))
            {
                int midY = _waveA.Y + _waveA.Height / 2;
                float half = _waveA.Height / 2f - 2;
                g.DrawLine(mid, _band.X, midY, _band.Right, midY);
                if (panes != null && panes.Length > 0)
                    DrawWave(g, pen, _wfA, _waveA, midY, half, panes[0].CurveOnLeft);
                if (_waveB.Width > 0 && panes != null && panes.Length > 1)
                    DrawWave(g, pen, _wfB, _waveB, midY, half, panes[1].CurveOnLeft);
            }
        }

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
            if (_poly.Length != w * 2) _poly = new PointF[w * 2];
            for (int a = 0; a < w; a++)
            {
                int x = newestOnRight ? r.Right - 1 - a : r.X + a;
                float lo, hi;
                ring.Get(a, out lo, out hi);
                _poly[a] = new PointF(x, midY - hi * half);
                _poly[w * 2 - 1 - a] = new PointF(x, midY - lo * half);
            }
            using (var brush = new SolidBrush(pen.Color))
                g.FillPolygon(brush, _poly);
        }

        // ---------------- interaction ----------------

        public bool Contains(Point p) { return _deck.Contains(p); }
        public bool Click(Point p, PlayerBridge player) { return _deck.Click(p, player); }

        public void Dispose() { _deck.Dispose(); }
    }
}

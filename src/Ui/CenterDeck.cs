using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Render;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// The block that fills the gap between the two waveform lanes.
    ///
    /// That gap is as wide as the two graph strips plus the centre gutter, and it was
    /// empty. It is also the one place on screen that belongs to neither channel, which
    /// makes it the right home for everything that describes the pair rather than one
    /// side of it: the goniometer, correlation and balance, and the transport.
    ///
    /// Three parts. The goniometer is pinned to the middle of the gap - which is the
    /// middle of the screen, and the axis the whole display is mirrored about - and the
    /// two halves it leaves are equal by construction. What is playing goes in the left
    /// half, what it is doing in the right: metadata reads like a record sleeve, and
    /// the transport, the phase meters and the readouts all describe the sound.
    ///
    /// Whatever does not fit either half is dropped, least important first - the gap
    /// changes width with the graph size setting, so it cannot be assumed roomy.
    ///
    /// The track title and the loudness readouts used to float in the top corners of the
    /// screen, on a gradient bar painted straight over the top of both spectrograms -
    /// which also pushed the outer frequency labels 84px down the axis to stay clear of
    /// it. Here they cost the image nothing at all.
    /// </summary>
    /// <summary>
    /// Everything the deck reads each frame, in one object. As a parameter list this had
    /// reached eleven and every new readout widened it at both ends.
    /// </summary>
    public sealed class DeckInputs
    {
        public int[] Lut;
        public LoudnessMeter Meter;
        public PlayerBridge Player;
        public MusicFeatures Features;
        /// <summary>The spectral centroid resolved to hertz; 0 when there is no map yet.</summary>
        public double BrightnessHz;
        public float[] GonL, GonR;
        public int GonCount;
    }

    public sealed class CenterDeck
    {
        private Rectangle _art, _info, _gonio, _stack, _loud;
        private Rectangle _prev, _play, _next, _seek, _clock, _corr, _bal;
        private int _loudCols, _loudRows, _loudColW;
        /// <summary>
        /// The x range every bar in the player block shares - seek, correlation and
        /// balance. One column, so the three line up instead of each ending wherever
        /// its own caption happened to leave it.
        /// </summary>
        private int _barLeft, _barRight;
        private Bitmap _artwork;
        private string _artworkKey;

        public Rectangle Bounds { get; private set; }
        /// <summary>Placed slots; empty when that element did not fit or is switched off.</summary>
        public Rectangle ArtRect { get { return _art; } }
        public Rectangle InfoRect { get { return _info; } }
        public Rectangle GoniometerRect { get { return _gonio; } }
        public Rectangle StackRect { get { return _stack; } }
        public Rectangle LoudnessRect { get { return _loud; } }
        public Rectangle SeekRect { get { return _seek; } }
        public Rectangle CorrelationRect { get { return _corr; } }
        public Rectangle BalanceRect { get { return _bal; } }
        /// <summary>
        /// The x range the seek bar and both meter bars are drawn in. Empty when the
        /// block is too narrow for a bar at all.
        /// </summary>
        public Rectangle BarColumn
        {
            get
            {
                return _barRight > _barLeft
                     ? Rectangle.FromLTRB(_barLeft, _stack.Y, _barRight, _stack.Bottom)
                     : Rectangle.Empty;
            }
        }

        /// <summary>What is playing. Set by the view when the host reports a track change.</summary>
        public string Title = "", Artist = "", Album = "", Composer = "", Year = "";

        // The gap is only as wide as the two graph strips plus the gutter, which at a
        // 12% graph is around 260px - so the deck stacks vertically rather than laying
        // everything out in a row, and uses the band's full height instead of asking
        // for width it will not get.
        private const int MinStack = 150;
        /// <summary>
        /// Shortest seek bar worth clicking. Below it the transport gives up the shared
        /// row and puts the bar on one of its own.
        ///
        /// Modest on purpose: this is a coarse control now that double-clicking a
        /// spectrogram column seeks to the exact moment, and every pixel it reserves
        /// comes off a readout column.
        /// </summary>
        private const int MinSeek = 60;
        /// <summary>Air either side of the shared bar column.</summary>
        private const int BarGap = 10;
        /// <summary>
        /// Enough for a short title at the label font. Below this the block is all
        /// ellipsis and tells you nothing, so it is dropped instead.
        /// </summary>
        private const int InfoMin = 120;
        /// <summary>
        /// One loudness readout column: a caption over a signed number, at the smallest
        /// label font. The caption is the wide part and it scales with the setting, so a
        /// fixed column would have the columns overlapping at large text.
        /// </summary>
        private const int LoudCol = 56;
        /// <summary>
        /// Past this the transport gains nothing: the seek bar is already long enough
        /// to aim at, and letting it stretch put the clock half a screen from the
        /// buttons it belongs to and gave the meters bars a metre long.
        /// </summary>
        private const int MaxStack = 380;
        private const int Pad = 6;

        /// <summary>
        /// The least the deck can usefully be. Three stacked rows need this much before
        /// the transport buttons stop being clickable; below it the rows collapse to
        /// about 13px, which is smaller than the text they carry. The height actually
        /// used is <see cref="Settings.DeckHeightPx"/>, which this floors.
        /// </summary>
        public const int PreferredHeight = 92;

        public void Layout(Rectangle gap, Settings s) { Layout(gap, s, null); }

        /// <param name="font">
        /// The label font, for measuring the shared bar column. Layout runs before the
        /// panel has a handle, so measurement goes through a screen Graphics; a caller
        /// with no font to lend gets one built from the size setting.
        /// </param>
        public void Layout(Rectangle gap, Settings s, Font font)
        {
            Bounds = gap;
            _art = _info = _gonio = _stack = _loud = Rectangle.Empty;
            _prev = _play = _next = _seek = _clock = _corr = _bal = Rectangle.Empty;
            _loudCols = _loudRows = _loudColW = _barLeft = _barRight = 0;
            if (gap.Width <= 40 || gap.Height <= 24) return;

            Font owned = null;
            if (font == null)
                font = owned = new Font("Segoe UI", Math.Max(5f, Math.Min(20f, s.LabelFontSize)));
            try
            {
                int h = gap.Height - Pad * 2;
                int y = gap.Y + Pad;

                // The goniometer first, and in the middle, because it is the one thing
                // here that belongs to both channels at once - the same reason the deck
                // exists. A fifth of the width: there are two full blocks either side of
                // it now, and it was taking room they need more than it does.
                int square = Math.Min(h, gap.Width / 5);
                if (s.DeckShowGoniometer && square >= 24)
                    // Square, and centred in the band rather than stretched down it.
                    // The plot inside was always circular - it takes the smaller of the
                    // two sides - so a tall box just drew a tall frame around a small
                    // trace, which reads as a bug in the instrument.
                    _gonio = new Rectangle(gap.X + gap.Width / 2 - square / 2,
                                           y + (h - square) / 2, square, square);

                int mid = gap.X + gap.Width / 2;
                int leftEnd = _gonio.Width > 0 ? _gonio.Left - Pad : mid - Pad;
                int rightStart = _gonio.Width > 0 ? _gonio.Right + Pad : mid + Pad;

                LayoutLeft(new Rectangle(gap.X + Pad, y, leftEnd - (gap.X + Pad), h), s);
                LayoutRight(new Rectangle(rightStart, y, gap.Right - Pad - rightStart, h), s, font);
            }
            finally { if (owned != null) owned.Dispose(); }
        }

        /// <summary>Artwork then metadata. The artwork goes first when the text is squeezed.</summary>
        private void LayoutLeft(Rectangle r, Settings s)
        {
            if (r.Width < 40 || r.Height < 20) return;
            int square = (s.DeckShowArtwork && r.Height >= 24)
                       ? Math.Min(r.Height, r.Width / 2) : 0;
            // Half a block of cover art with no room left to say what it is the cover
            // of is the wrong trade every time.
            if (square > 0 && s.DeckShowTrackInfo && r.Width - square - Pad < InfoMin) square = 0;

            int x = r.X;
            if (square >= 24) { _art = new Rectangle(x, r.Y, square, r.Height); x += square + Pad; }
            if (s.DeckShowTrackInfo && r.Right - x >= InfoMin)
                _info = new Rectangle(x, r.Y, r.Right - x, r.Height);
        }

        /// <summary>
        /// The transport block, then the readout grid. The grid sheds columns from the
        /// right until the transport has room to stay usable - the readouts are in
        /// priority order, so brightness and tempo go first and the two LUFS figures
        /// are the last to survive.
        /// </summary>
        private void LayoutRight(Rectangle r, Settings s, Font font)
        {
            if (r.Width < 40 || r.Height < 20) return;
            bool player = s.DeckShowTransport;
            bool corr = s.DeckShowCorrelation, bal = s.DeckShowBalance;
            int meters = (corr ? 1 : 0) + (bal ? 1 : 0);
            bool stack = player || meters > 0;

            int loudCol = LoudCol + (int)Math.Max(0, (s.LabelFontSize - 7f) * 5);
            // A caption over a value, plus air. Three rows at a tall deck height turns
            // nine readouts into three columns instead of five, which is most of the
            // difference between fitting beside the transport and not.
            int cell = (int)(s.LabelFontSize * 4.6f) + 4;
            int rows = Math.Max(2, Math.Min(3, r.Height / Math.Max(1, cell)));
            int cols = (LoudCount(s) + rows - 1) / rows;

            StackMetrics m = stack ? MeasureStack(r.Height, font, player, meters)
                                   : new StackMetrics();
            int reserve = stack ? MinStack + Pad : 0;
            // Measured rather than guessed: what the transport needs to keep controls,
            // bar and clock on one row is three buttons, the longest clock and a bar
            // worth aiming at, and all three depend on the font and the deck's height.
            int want = (stack && player)
                     ? (int)Math.Max(m.Cap, m.Buttons) + BarGap * 2 + MinSeek
                       + (int)Math.Max(m.Val, m.Clock) + Pad
                     : reserve;

            // The single transport row is asked for ahead of the last readout columns -
            // a split row is what this arrangement exists to avoid - but never down to
            // no readouts at all: one column beats a tidier transport.
            while (cols > 1 && r.Width - cols * loudCol < want) cols--;
            while (cols > 0 && r.Width - cols * loudCol < reserve) cols--;

            int x = r.X;
            if (stack)
            {
                int w = r.Width - (cols > 0 ? cols * loudCol + Pad : 0);
                if (w >= 60)
                {
                    _stack = new Rectangle(x, r.Y, w, r.Height);
                    LayoutStack(player, corr, bal, m);
                    x = _stack.Right + Pad;
                }
            }
            if (cols > 0 && r.Right - x >= cols * loudCol)
            {
                _loudCols = cols;
                _loudRows = rows;
                _loudColW = loudCol;
                _loud = new Rectangle(x, r.Y, cols * loudCol, r.Height);
            }
        }

        /// <summary>
        /// The text and button widths the player block's geometry depends on. Measured
        /// once, before the block's own width is decided, because that decision needs
        /// them - the stack cannot be sized against a number it has not worked out yet.
        /// </summary>
        private struct StackMetrics
        {
            public float Cap, Val, Clock;
            public int Btn, Buttons, RowH;
        }

        private static StackMetrics MeasureStack(int height, Font font, bool player, int meters)
        {
            var m = new StackMetrics();
            int rows = (player ? 1 : 0) + meters;
            m.RowH = rows > 0 ? height / rows : 0;
            m.Btn = Math.Max(12, Math.Min(28, m.RowH - 2));
            m.Buttons = player ? (m.Btn + 6) * 3 - 6 : 0;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                if (meters > 0)
                {
                    m.Cap = Math.Max(g.MeasureString("CORR", font).Width,
                                     g.MeasureString("BAL", font).Width);
                    m.Val = g.MeasureString("+0.00", font).Width;
                }
                // The widest clock rather than the current one, so the bars do not
                // shift when a track ticks past ten minutes.
                if (player) m.Clock = g.MeasureString("00:00 / 00:00", font).Width;
            }
            return m;
        }

        /// <summary>How many of the nine readouts are switched on.</summary>
        private static int LoudCount(Settings s)
        {
            return (s.DeckShowLufsM ? 1 : 0) + (s.DeckShowLufsS ? 1 : 0)
                 + (s.DeckShowLufsI ? 1 : 0) + (s.DeckShowLra ? 1 : 0)
                 + (s.DeckShowTruePeak ? 1 : 0) + (s.DeckShowCrest ? 1 : 0)
                 + (s.DeckShowOvers ? 1 : 0) + (s.DeckShowBpm ? 1 : 0)
                 + (s.DeckShowBrightness ? 1 : 0);
        }

        /// <summary>
        /// Three rows, each the full width of the block: the transport with its seek
        /// bar, then whichever of the two correlation meters are switched on.
        ///
        /// The seek bar used to have a row to itself under the buttons. Sharing one row
        /// with them gives that row back, which is what lets the metadata opposite run
        /// to four lines at the same deck height - and it puts all three bars in one
        /// column, so they start and end together instead of each stopping wherever its
        /// own caption left it.
        /// </summary>
        private void LayoutStack(bool player, bool corr, bool bal, StackMetrics m)
        {
            int meters = (corr ? 1 : 0) + (bal ? 1 : 0);
            if (!player && meters == 0) return;

            // One row for the transport when the block is wide enough to hold buttons,
            // a bar worth aiming at and the clock side by side; otherwise the seek bar
            // takes a row of its own, as it used to. Half a seek bar is worse than an
            // extra row - you cannot drop a cursor on thirty pixels.
            int rows = (player ? 1 : 0) + meters;
            int rowH = _stack.Height / rows;
            int btn = Math.Max(12, Math.Min(28, rowH - 2));
            int buttons = player ? (btn + 6) * 3 - 6 : 0;
            float widest = Math.Max(m.Cap, buttons);
            bool oneRow = !player ||
                          _stack.Width - widest - Math.Max(m.Val, m.Clock) - BarGap * 2 >= MinSeek;
            if (!oneRow)
            {
                rows++;                       // the seek bar gets one of its own
                rowH = _stack.Height / rows;
                btn = Math.Max(12, Math.Min(28, rowH - 2));
                buttons = (btn + 6) * 3 - 6;
                widest = meters > 0 ? m.Cap : buttons;
            }
            if (rowH < 10) return;

            _barLeft = _stack.X + (int)widest + BarGap;
            _barRight = _stack.Right - (int)Math.Max(m.Val, oneRow ? m.Clock : 0) - BarGap;
            if (_barRight - _barLeft < 24) { _barLeft = 0; _barRight = 0; }

            int y = _stack.Y;
            if (player)
            {
                int by = y + (rowH - btn) / 2;
                _prev = new Rectangle(_stack.X, by, btn, btn);
                _play = new Rectangle(_stack.X + btn + 6, by, btn, btn);
                _next = new Rectangle(_stack.X + (btn + 6) * 2, by, btn, btn);
                int clockX = oneRow ? _stack.Right - (int)m.Clock : _next.Right + BarGap;
                _clock = new Rectangle(clockX, y, Math.Max(0, _stack.Right - clockX), rowH);
                y += rowH;

                int seekH = Math.Max(5, Math.Min(10, rowH / 3));
                if (oneRow)
                {
                    if (_barRight > _barLeft)
                        _seek = new Rectangle(_barLeft, _prev.Y + (btn - seekH) / 2,
                                              _barRight - _barLeft, seekH);
                }
                else
                {
                    _seek = new Rectangle(_stack.X, y + (rowH - seekH) / 2, _stack.Width, seekH);
                    y += rowH;
                }
            }
            if (corr) { _corr = new Rectangle(_stack.X, y, _stack.Width, rowH); y += rowH; }
            if (bal) _bal = new Rectangle(_stack.X, y, _stack.Width, rowH);
        }

        // ---------------- drawing ----------------

        /// <param name="infoAlpha">
        /// Track info only. A track change announces itself at full strength even when
        /// the rest of the furniture has faded out, which is what the old top-left
        /// overlay did and the one part of it worth keeping.
        /// </param>
        public void Draw(Graphics g, Settings s, Font font, Font mid, double alpha,
                         double infoAlpha, DeckInputs inp)
        {
            if (Bounds.Width <= 0 || inp == null) return;
            if (alpha <= 0.004)
            {
                // Everything else is chrome and stays gone; the announcement is not.
                if (infoAlpha > 0.004 && _info.Width > 0) DrawTrackInfo(g, font, mid, infoAlpha);
                return;
            }
            if (_art.Width > 0) DrawArtwork(g, alpha, inp.Player);
            if (_info.Width > 0) DrawTrackInfo(g, font, mid, Math.Max(alpha, infoAlpha));
            if (_gonio.Width > 0)
                DrawGoniometer(g, alpha, inp.Lut, inp.GonL, inp.GonR, inp.GonCount, font);
            if (_play.Width > 0 || _seek.Width > 0) DrawPlayer(g, font, alpha, inp.Player);
            if (_corr.Width > 0 || _bal.Width > 0) DrawMeters(g, font, alpha, inp.Meter);
            if (_loud.Width > 0) DrawReadouts(g, s, font, mid, alpha, inp);
        }

        private readonly string[] _rowCap = new string[4];
        private readonly string[] _rowVal = new string[4];

        /// <summary>
        /// What is playing, as much of it as the block is tall enough to hold: the
        /// title, then four labelled lines, or two joined pairs, or one.
        ///
        /// The captions earn their width only at the tall sizes. At two lines they
        /// would cost a third of the block, and "composer - artist" over "album - year"
        /// reads clearly enough without them.
        /// </summary>
        private void DrawTrackInfo(Graphics g, Font font, Font mid, double alpha)
        {
            string title = Title ?? "";
            float titleH = title.Length > 0 ? g.MeasureString("Mg", mid).Height : 0;
            float lineH = g.MeasureString("Mg", font).Height;
            if (lineH <= 0) return;

            int room = (int)((_info.Height - titleH) / lineH);
            int n = 0;
            if (room >= 4)
            {
                n = AddRow(n, "COMPOSER", Composer);
                n = AddRow(n, "ARTISTS", Artist);
                n = AddRow(n, "ALBUM", Album);
                n = AddRow(n, "YEAR", Year);
            }
            else if (room >= 2)
            {
                n = AddRow(n, null, Join(Composer, Artist));
                n = AddRow(n, null, Join(Album, Year));
            }
            else if (room >= 1) n = AddRow(n, null, Join(Artist, Album));
            if (title.Length == 0 && n == 0) return;

            float capW = 0;
            for (int i = 0; i < n; i++)
                if (_rowCap[i] != null)
                    capW = Math.Max(capW, g.MeasureString(_rowCap[i], font).Width);
            if (capW > 0) capW += 10;

            float y = _info.Y + Math.Max(0, (_info.Height - (titleH + n * lineH)) / 2f);
            using (var ink = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(240, 245, 245, 248), alpha)))
            using (var dim = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(180, 205, 205, 215), alpha)))
            using (var cap = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(130, 175, 175, 188), alpha)))
            {
                if (title.Length > 0)
                {
                    g.DrawString(Fit(g, title, mid, _info.Width), mid, ink, _info.X, y);
                    y += titleH;
                }
                for (int i = 0; i < n; i++)
                {
                    if (_rowCap[i] != null) g.DrawString(_rowCap[i], font, cap, _info.X, y);
                    g.DrawString(Fit(g, _rowVal[i], font, _info.Width - capW), font, dim,
                                 _info.X + capW, y);
                    y += lineH;
                }
            }
        }

        /// <summary>Queues one metadata row, skipping fields the track does not carry.</summary>
        private int AddRow(int n, string caption, string value)
        {
            if (string.IsNullOrEmpty(value) || n >= _rowCap.Length) return n;
            _rowCap[n] = caption;
            _rowVal[n] = value;
            return n + 1;
        }

        private static string Join(string a, string b)
        {
            bool ha = !string.IsNullOrEmpty(a), hb = !string.IsNullOrEmpty(b);
            if (ha && hb) return a + "  \u00b7  " + b;
            return ha ? a : (hb ? b : null);
        }

        /// <summary>
        /// Trims to fit rather than spilling into the next block. Binary search because
        /// MeasureString is the expensive call here and this runs every frame.
        /// </summary>
        private static string Fit(Graphics g, string text, Font font, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (g.MeasureString(text, font).Width <= maxWidth) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int n = (lo + hi + 1) / 2;
                if (g.MeasureString(text.Substring(0, n) + "...", font).Width <= maxWidth) lo = n;
                else hi = n - 1;
            }
            return lo <= 0 ? "" : text.Substring(0, lo).TrimEnd() + "...";
        }

        /// <summary>
        /// The readouts, two or three rows deep depending on the deck's height, in
        /// priority order so that shedding a column from the right gives up the least
        /// useful number first. Read down each column: the loudness figures first, then
        /// peak and crest, then what the music itself is doing.
        /// </summary>
        private void DrawReadouts(Graphics g, Settings s, Font font, Font mid,
                                  double alpha, DeckInputs inp)
        {
            LoudnessMeter meter = inp.Meter;
            if (meter == null || _loudCols <= 0) return;
            double tp = meter.TruePeakDb;

            using (var lbl = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(150, 190, 190, 200), alpha)))
            using (var val = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(240, 245, 245, 248), alpha)))
            using (var warn = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(255, 255, 120, 90), alpha)))
            {
                int i = 0;
                if (s.DeckShowLufsM)
                    Cell(g, font, mid, lbl, val, i++, "LUFS-M", Db(meter.MomentaryLufs));
                if (s.DeckShowLufsS)
                    Cell(g, font, mid, lbl, val, i++, "LUFS-S", Db(meter.ShortTermLufs));
                if (s.DeckShowLufsI)
                    Cell(g, font, mid, lbl, val, i++, "LUFS-I", Db(meter.IntegratedLufs));
                if (s.DeckShowLra)
                    Cell(g, font, mid, lbl, val, i++, "LRA", meter.LoudnessRange.ToString("0.0"));
                // Anything above -1 dBTP will clip a lossy encoder even though the sample
                // peaks never did, which is the whole reason true peak is measured.
                if (s.DeckShowTruePeak)
                    Cell(g, font, mid, lbl, tp > LoudnessMeter.OverThresholdDb ? warn : val,
                         i++, "TRUE PK", Db(tp));
                if (s.DeckShowCrest)
                    Cell(g, font, mid, lbl, val, i++, "CREST", meter.CrestDb.ToString("0.0"));
                if (s.DeckShowOvers)
                {
                    // When it last happened goes in the caption: the cell has one line for
                    // a number and the count alone does not tell you where to look.
                    string cap = meter.Overs > 0
                        ? "OVERS " + Clock((int)(meter.LastOverSeconds * 1000))
                        : "OVERS";
                    Cell(g, font, mid, lbl, meter.Overs > 0 ? warn : val, i++, cap,
                         meter.Overs.ToString());
                }
                if (s.DeckShowBpm)
                {
                    double bpm = inp.Features == null ? 0 : inp.Features.Bpm;
                    Cell(g, font, mid, lbl, val, i++, "BPM",
                         bpm > 0 ? bpm.ToString("0") : "--");
                }
                if (s.DeckShowBrightness)
                    Cell(g, font, mid, lbl, val, i++, "BRIGHT", Hz(inp.BrightnessHz));
            }
        }

        /// <summary>A level, or a dash when the meter has not settled on one yet.</summary>
        private static string Db(double v)
        {
            return v <= -70.0 ? "--" : v.ToString("0.0");
        }

        /// <summary>Short enough for a 66px column: 440, 2.4k, 14k.</summary>
        private static string Hz(double f)
        {
            if (f <= 0) return "--";
            if (f < 1000) return f.ToString("0");
            double k = f / 1000.0;
            return (k < 10 ? k.ToString("0.0") : k.ToString("0")) + "k";
        }

        /// <summary>One readout: caption over value, filling column-major down each column.</summary>
        private void Cell(Graphics g, Font font, Font mid, Brush lbl, Brush val,
                          int index, string name, string value)
        {
            int rows = Math.Max(1, _loudRows);
            int col = index / rows, row = index % rows;
            if (col >= _loudCols) return;
            int cellH = _loud.Height / rows;
            int x = _loud.X + col * _loudColW;
            int y = _loud.Y + row * cellH;

            SizeF ls = g.MeasureString(name, font);
            // A caption too wide for its column gives up its tail rather than running
            // into the next one: "OVERS 2:14" falls back to "OVERS".
            if (ls.Width > _loudColW - 4)
            {
                int cut = name.LastIndexOf(' ');
                if (cut > 0) { name = name.Substring(0, cut); ls = g.MeasureString(name, font); }
            }
            SizeF vs = g.MeasureString("-00.0", mid);
            float block = ls.Height + vs.Height;
            float top = y + Math.Max(0, (cellH - block) / 2f);
            g.DrawString(name, font, lbl, x, top);
            g.DrawString(value, mid, val, x, top + ls.Height);
        }

        private void DrawArtwork(Graphics g, double alpha, PlayerBridge player)
        {
            string data = player == null ? null : player.SafeArtwork();
            if (!string.IsNullOrEmpty(data) && data != _artworkKey)
            {
                // Decoded once per track, not per frame: the host hands back base64 and
                // turning that into a bitmap sixty times a second would dwarf everything
                // else drawn here.
                _artworkKey = data;
                if (_artwork != null) { _artwork.Dispose(); _artwork = null; }
                try
                {
                    byte[] bytes = Convert.FromBase64String(data);
                    using (var ms = new System.IO.MemoryStream(bytes))
                        _artwork = new Bitmap(ms);
                }
                catch { _artwork = null; }
            }
            else if (string.IsNullOrEmpty(data) && _artworkKey != null)
            {
                _artworkKey = null;
                if (_artwork != null) { _artwork.Dispose(); _artwork = null; }
            }

            using (var frame = new Pen(StereoScope.FadeColor(Color.FromArgb(60, 255, 255, 255), alpha)))
            {
                if (_artwork != null)
                {
                    var old = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    if (alpha >= 0.996) g.DrawImage(_artwork, _art);
                    else
                    {
                        using (var attr = new System.Drawing.Imaging.ImageAttributes())
                        {
                            var cm = new System.Drawing.Imaging.ColorMatrix();
                            cm.Matrix33 = (float)alpha;
                            attr.SetColorMatrix(cm);
                            g.DrawImage(_artwork, _art, 0, 0, _artwork.Width, _artwork.Height,
                                        GraphicsUnit.Pixel, attr);
                        }
                    }
                    g.InterpolationMode = old;
                }
                else
                {
                    using (var back = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(120, 20, 20, 26), alpha)))
                        g.FillRectangle(back, _art);
                }
                g.DrawRectangle(frame, _art);
            }
        }

        /// <summary>
        /// Lissajous plot of the two channels, rotated 45 degrees so mono reads as a
        /// vertical line: a circle is a wide image, a horizontal line is out of phase,
        /// and a lean to one side is a level imbalance.
        /// </summary>
        private void DrawGoniometer(Graphics g, double alpha, int[] lut,
                                    float[] l, float[] r, int count, Font font)
        {
            using (var back = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(200, 10, 10, 13), alpha)))
            using (var frame = new Pen(StereoScope.FadeColor(Color.FromArgb(50, 255, 255, 255), alpha)))
            using (var guide = new Pen(StereoScope.FadeColor(Color.FromArgb(28, 255, 255, 255), alpha)))
            {
                g.FillRectangle(back, _gonio);
                g.DrawRectangle(frame, _gonio);
                // The two diagonals are the L-only and R-only axes; the vertical is mono.
                g.DrawLine(guide, _gonio.Left, _gonio.Top, _gonio.Right, _gonio.Bottom);
                g.DrawLine(guide, _gonio.Right, _gonio.Top, _gonio.Left, _gonio.Bottom);
                g.DrawLine(guide, _gonio.Left + _gonio.Width / 2, _gonio.Top,
                                  _gonio.Left + _gonio.Width / 2, _gonio.Bottom);
            }

            if (l == null || r == null || count < 2) return;

            float cx = _gonio.Left + _gonio.Width / 2f;
            float cy = _gonio.Top + _gonio.Height / 2f;
            float rad = Math.Min(_gonio.Width, _gonio.Height) / 2f - 3;
            // 1/sqrt(2) so a hard-panned full-scale signal reaches the frame rather than
            // spilling out of it.
            const float K = 0.70710678f;

            if (_pts.Length < count) _pts = new PointF[count];
            for (int i = 0; i < count; i++)
            {
                float a = l[i], b = r[i];
                _pts[i] = new PointF(cx + (a - b) * K * rad, cy - (a + b) * K * rad);
            }

            Color c = Palette.ColorAt(lut, 0.80);
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var clip = g.Clip;
            g.SetClip(_gonio);
            using (var pen = new Pen(StereoScope.FadeColor(Color.FromArgb(150, c), alpha)))
            {
                // One polyline rather than a dot per sample: same trace, one GDI+ call.
                if (count > 1) g.DrawLines(pen, Trim(_pts, count));
            }
            g.Clip = clip;
            g.SmoothingMode = oldMode;
        }

        private PointF[] _pts = new PointF[0];
        private PointF[] _trim = new PointF[0];

        private PointF[] Trim(PointF[] src, int count)
        {
            // DrawLines takes the whole array, so it needs one of exactly the right
            // length; kept and reused rather than allocated every frame.
            if (_trim.Length != count) _trim = new PointF[count];
            Array.Copy(src, _trim, count);
            return _trim;
        }

        /// <summary>
        /// Controls, seek bar and clock on one row. The bar sits in the column it
        /// shares with the two meters below, and the clock in the value column to its
        /// right, so all three rows line up down both edges.
        /// </summary>
        private void DrawPlayer(Graphics g, Font font, double alpha, PlayerBridge player)
        {
            int pos = player == null ? 0 : player.SafePosition();
            int dur = player == null ? 0 : player.SafeDuration();
            bool playing = player != null && player.SafeIsPlaying();

            using (var ink = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(215, 235, 235, 242), alpha)))
            using (var dim = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(140, 185, 185, 195), alpha)))
            using (var edge = new Pen(StereoScope.FadeColor(Color.FromArgb(70, 255, 255, 255), alpha)))
            {
                DrawPrev(g, ink, _prev);
                if (playing) DrawPause(g, ink, _play); else DrawPlay(g, ink, _play);
                DrawNext(g, ink, _next);

                string text = Clock(pos) + " / " + (dur > 0 ? Clock(dur) : "--:--");
                SizeF sz = g.MeasureString(text, font);
                // Right-aligned in the value column, where the meters put their numbers.
                if (_clock.Width >= sz.Width)
                    g.DrawString(text, font, dim, _clock.Right - sz.Width,
                                 _clock.Y + (_clock.Height - sz.Height) / 2);

                if (_seek.Width <= 0) return;
                g.DrawRectangle(edge, _seek);
                if (dur > 0)
                {
                    double t = (double)pos / dur;
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    int w = (int)((_seek.Width - 2) * t);
                    if (w > 0)
                        using (var fill = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(200, 235, 235, 242), alpha)))
                            g.FillRectangle(fill, _seek.X + 1, _seek.Y + 1, w, _seek.Height - 1);
                }
            }
        }

        private static string Clock(int ms)
        {
            if (ms < 0) ms = 0;
            int total = ms / 1000;
            return (total / 60).ToString("0") + ":" + (total % 60).ToString("00");
        }

        private static void DrawPlay(Graphics g, Brush b, Rectangle r)
        {
            int i = r.Height / 4;
            g.FillPolygon(b, new Point[] {
                new Point(r.Left + i, r.Top + i),
                new Point(r.Right - i, r.Top + r.Height / 2),
                new Point(r.Left + i, r.Bottom - i)
            });
        }

        private static void DrawPause(Graphics g, Brush b, Rectangle r)
        {
            int i = r.Height / 4, w = Math.Max(2, r.Width / 7);
            g.FillRectangle(b, r.Left + i, r.Top + i, w, r.Height - i * 2);
            g.FillRectangle(b, r.Right - i - w, r.Top + i, w, r.Height - i * 2);
        }

        private static void DrawNext(Graphics g, Brush b, Rectangle r)
        {
            int i = r.Height / 4, w = Math.Max(2, r.Width / 8);
            g.FillPolygon(b, new Point[] {
                new Point(r.Left + i, r.Top + i),
                new Point(r.Right - i - w, r.Top + r.Height / 2),
                new Point(r.Left + i, r.Bottom - i)
            });
            g.FillRectangle(b, r.Right - i - w, r.Top + i, w, r.Height - i * 2);
        }

        private static void DrawPrev(Graphics g, Brush b, Rectangle r)
        {
            int i = r.Height / 4, w = Math.Max(2, r.Width / 8);
            g.FillPolygon(b, new Point[] {
                new Point(r.Right - i, r.Top + i),
                new Point(r.Left + i + w, r.Top + r.Height / 2),
                new Point(r.Right - i, r.Bottom - i)
            });
            g.FillRectangle(b, r.Left + i, r.Top + i, w, r.Height - i * 2);
        }

        /// <summary>
        /// Correlation and balance. They live here rather than at the top of the screen
        /// because they describe the pair of channels, and because up there they landed
        /// on the centre gutter's frequency labels.
        /// </summary>
        private void DrawMeters(Graphics g, Font font, double alpha, LoudnessMeter meter)
        {
            if (meter == null) return;
            // Either rect may be empty - each meter has its own switch. Both are handed
            // the same bar column, which is the whole point: the two bars used to end
            // wherever their own captions left them, so CORR was shorter than BAL.
            double corr = meter.Correlation, bal = meter.Balance;
            DrawBipolar(g, font, alpha, _corr, _barLeft, _barRight, "CORR", corr,
                        corr < 0 ? Color.FromArgb(255, 120, 90) : Color.FromArgb(120, 220, 160));
            DrawBipolar(g, font, alpha, _bal, _barLeft, _barRight, "BAL", bal,
                        Color.FromArgb(150, 190, 240));
        }

        /// <summary>
        /// One meter on one line: name, a centre-zero bar, then the value. The bar's
        /// extent is given rather than measured, so every row in the block shares it.
        /// </summary>
        private static void DrawBipolar(Graphics g, Font font, double alpha, Rectangle r,
                                        int barLeft, int barRight,
                                        string label, double value, Color fill)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            string v = value.ToString("+0.00;-0.00; 0.00");
            using (var lbl = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(135, 180, 180, 190), alpha)))
            using (var val = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(225, 240, 240, 246), alpha)))
            using (var edge = new Pen(StereoScope.FadeColor(Color.FromArgb(55, 255, 255, 255), alpha)))
            using (var bar = new SolidBrush(StereoScope.FadeColor(fill, alpha)))
            {
                SizeF ls = g.MeasureString(label, font);
                SizeF vs = g.MeasureString(v, font);
                g.DrawString(label, font, lbl, r.X, r.Y + (r.Height - ls.Height) / 2);
                g.DrawString(v, font, val, r.Right - vs.Width, r.Y + (r.Height - vs.Height) / 2);

                if (barRight - barLeft < 24) return;
                int barH = Math.Max(4, Math.Min(9, r.Height - 6));
                int barY = r.Y + (r.Height - barH) / 2;
                var track = new Rectangle(barLeft, barY, barRight - barLeft, barH);
                g.DrawRectangle(edge, track);

                double t = value; if (t < -1) t = -1; else if (t > 1) t = 1;
                int mid = track.X + track.Width / 2;
                int end = mid + (int)(t * (track.Width / 2 - 1));
                if (end < mid) g.FillRectangle(bar, end, barY + 1, mid - end, barH - 1);
                else g.FillRectangle(bar, mid, barY + 1, Math.Max(1, end - mid), barH - 1);
                g.DrawLine(edge, mid, barY, mid, barY + barH);
            }
        }

        // ---------------- interaction ----------------

        public bool Contains(Point p)
        {
            return (_prev.Width > 0 && (_prev.Contains(p) || _play.Contains(p) || _next.Contains(p)))
                   || (_seek.Width > 0 && _seek.Contains(p));
        }

        /// <summary>Handles a click on the transport or the seek bar. True if it was ours.</summary>
        public bool Click(Point p, PlayerBridge player)
        {
            if (player == null) return false;
            if (_prev.Width > 0 && _prev.Contains(p)) { player.Do(player.Previous); return true; }
            if (_play.Width > 0 && _play.Contains(p)) { player.Do(player.PlayPause); return true; }
            if (_next.Width > 0 && _next.Contains(p)) { player.Do(player.Next); return true; }
            if (_seek.Width > 0 && _seek.Contains(p) && player.Seek != null)
            {
                int dur = player.SafeDuration();
                if (dur > 0)
                {
                    double t = (double)(p.X - _seek.X) / Math.Max(1, _seek.Width);
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    try { player.Seek((int)(t * dur)); } catch { }
                }
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_artwork != null) { _artwork.Dispose(); _artwork = null; }
            _artworkKey = null;
        }
    }
}

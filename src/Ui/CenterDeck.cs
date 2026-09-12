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
    /// Laid out left to right as artwork, track info, goniometer, transport, loudness.
    /// Whatever does not fit is dropped, least important first, and what remains is
    /// centred - the gap changes width with the graph size setting, so it cannot be
    /// assumed roomy.
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
        private int _loudCols, _loudColW;
        private Bitmap _artwork;
        private string _artworkKey;

        public Rectangle Bounds { get; private set; }
        /// <summary>Placed slots; empty when that element did not fit or is switched off.</summary>
        public Rectangle ArtRect { get { return _art; } }
        public Rectangle InfoRect { get { return _info; } }
        public Rectangle GoniometerRect { get { return _gonio; } }
        public Rectangle StackRect { get { return _stack; } }
        public Rectangle LoudnessRect { get { return _loud; } }

        /// <summary>What is playing. Set by the view when the host reports a track change.</summary>
        public string Title = "", Artist = "", Album = "";

        // The gap is only as wide as the two graph strips plus the gutter, which at a
        // 12% graph is around 260px - so the deck stacks vertically rather than laying
        // everything out in a row, and uses the band's full height instead of asking
        // for width it will not get.
        private const int MinStack = 150;
        /// <summary>
        /// Enough for a short title at the label font. Below this the block is all
        /// ellipsis and tells you nothing, so it is dropped instead.
        /// </summary>
        private const int InfoMin = 120;
        /// <summary>Past this a title is simply long, and the width is better spent elsewhere.</summary>
        private const int InfoWant = 300;
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
        /// Height the deck wants. Four stacked rows need this much before the transport
        /// buttons stop being clickable; below it the rows collapse to about 13px, which
        /// is smaller than the text they carry.
        /// </summary>
        public const int PreferredHeight = 92;

        public void Layout(Rectangle gap, Settings s)
        {
            Bounds = gap;
            _art = _info = _gonio = _stack = _loud = Rectangle.Empty;
            _prev = _play = _next = _seek = _clock = _corr = _bal = Rectangle.Empty;
            _loudCols = _loudColW = 0;
            if (gap.Width <= 40 || gap.Height <= 24) return;

            int h = gap.Height - Pad * 2;
            // A quarter rather than a third: there are two squares here now, and they
            // were eating width the title had no way to get back.
            int square = Math.Min(h, gap.Width / 4);

            bool art = s.DeckShowArtwork;
            bool info = s.DeckShowTrackInfo;
            bool gonio = s.DeckShowGoniometer;
            bool player = s.DeckShowTransport;
            bool corr = s.DeckShowCorrelation;
            bool bal = s.DeckShowBalance;
            int loud = LoudCount(s);
            int loudCol = LoudCol + (int)Math.Max(0, (s.LabelFontSize - 7f) * 5);
            // Two rows deep, so four readouts cost two columns rather than four. The
            // deck is short of width and long on height; this spends the one it has.
            int cols = (loud + 1) / 2;

            // Dropped least important first, and only in an order that actually frees
            // width. The transport and the two meters share one column, so giving up
            // either while the other is there costs a readout and buys nothing - they
            // go together, and last. The goniometer is never dropped: it is the one
            // instrument here, and the only thing that shows the stereo field at all.
            bool stack = player || corr || bal;
            int need = Need(art, info, gonio, stack, cols * loudCol, square);
            if (need > gap.Width && art)
            { art = false; need = Need(art, info, gonio, stack, cols * loudCol, square); }
            // Shed readout columns one at a time rather than all of them: they are in
            // priority order, so the first to go are brightness and tempo and the last
            // to survive are the two LUFS figures.
            while (need > gap.Width && cols > 0)
            { cols--; need = Need(art, info, gonio, stack, cols * loudCol, square); }
            if (need > gap.Width && info)
            { info = false; need = Need(art, info, gonio, stack, cols * loudCol, square); }
            if (need > gap.Width && stack)
            { player = corr = bal = stack = false; need = Need(art, info, gonio, false, cols * loudCol, square); }
            if (need > gap.Width) return;

            int slack = gap.Width - need;

            // The title is the only thing here that loses characters when it is short of
            // room, so it has first claim on the slack; the stack at its minimum is
            // already a usable transport.
            int infoW = 0;
            if (info)
            {
                infoW = InfoMin + Math.Min(Math.Max(0, slack), InfoWant - InfoMin);
                slack -= infoW - InfoMin;
            }
            int stackW = stack ? Math.Min(MaxStack, MinStack + Math.Max(0, slack)) : 0;

            // Whatever the stack and the title decline to take is spread either side, so
            // the deck stays centred in the gap rather than hugging its left edge.
            int used = need + (info ? infoW - InfoMin : 0) + (stack ? stackW - MinStack : 0);
            int x = gap.X + Pad + Math.Max(0, (gap.Width - used) / 2);
            int y = gap.Y + Pad;

            if (art) { _art = new Rectangle(x, y, square, h); x += square + Pad; }
            if (info) { _info = new Rectangle(x, y, infoW, h); x += infoW + Pad; }
            if (gonio) { _gonio = new Rectangle(x, y, square, h); x += square + Pad; }
            if (stack)
            {
                _stack = new Rectangle(x, y, stackW, h);
                LayoutStack(player, corr, bal);
                x += stackW + Pad;
            }
            if (cols > 0)
            {
                _loudCols = cols;
                _loudColW = loudCol;
                _loud = new Rectangle(x, y, cols * loudCol, h);
            }
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

        private static int Need(bool art, bool info, bool gonio, bool stack, int loudW, int square)
        {
            int n = 0, w = 0;
            if (art) { w += square; n++; }
            if (info) { w += InfoMin; n++; }
            if (gonio) { w += square; n++; }
            if (stack) { w += MinStack; n++; }
            if (loudW > 0) { w += loudW; n++; }
            return w + Pad * (n + 1);
        }

        /// <summary>
        /// The transport column: buttons and clock, seek bar, then whichever of the two
        /// correlation meters are switched on, each taking a share of the height rather
        /// than competing for width.
        /// </summary>
        private void LayoutStack(bool player, bool corr, bool bal)
        {
            int h = _stack.Height;
            int meters = (corr ? 1 : 0) + (bal ? 1 : 0);
            int rows = (player ? 2 : 0) + meters;
            if (rows == 0) return;

            // The seek bar and the meter bars are thin; the transport row wants to be
            // square-ish, so it takes the larger share of whatever is going.
            int y = _stack.Y;
            if (player)
            {
                int btn = Math.Max(12, Math.Min(30, h / rows));
                _prev = new Rectangle(_stack.X, y, btn, btn);
                _play = new Rectangle(_stack.X + btn + 6, y, btn, btn);
                _next = new Rectangle(_stack.X + (btn + 6) * 2, y, btn, btn);
                _clock = new Rectangle(_next.Right + 10, y, _stack.Right - _next.Right - 10, btn);
                y += btn + 4;
                int seekH = Math.Max(5, Math.Min(9, h / 10));
                _seek = new Rectangle(_stack.X, y, _stack.Width, seekH);
                y += seekH + 6;
            }
            if (meters > 0)
            {
                int left = Math.Max(10, _stack.Bottom - y);
                int rowH = left / meters;
                if (corr) { _corr = new Rectangle(_stack.X, y, _stack.Width, rowH); y += rowH; }
                if (bal) _bal = new Rectangle(_stack.X, y, _stack.Width, rowH);
            }
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
            if (_seek.Width > 0) DrawPlayer(g, font, alpha, inp.Player);
            if (_corr.Width > 0 || _bal.Width > 0) DrawMeters(g, font, alpha, inp.Meter);
            if (_loud.Width > 0) DrawReadouts(g, s, font, mid, alpha, inp);
        }

        /// <summary>
        /// Title over artist and album, clipped to the slot. Left-aligned against the
        /// artwork so the two read as one block.
        /// </summary>
        private void DrawTrackInfo(Graphics g, Font font, Font mid, double alpha)
        {
            string title = Title ?? "";
            string sub = Artist ?? "";
            if (!string.IsNullOrEmpty(Album)) sub += (sub.Length > 0 ? "  ·  " : "") + Album;
            if (title.Length == 0 && sub.Length == 0) return;

            float titleH = title.Length > 0 ? g.MeasureString("Mg", mid).Height : 0;
            float subH = sub.Length > 0 ? g.MeasureString("Mg", font).Height : 0;
            float y = _info.Y + (_info.Height - (titleH + subH)) / 2f;

            using (var ink = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(240, 245, 245, 248), alpha)))
            using (var dim = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(170, 200, 200, 210), alpha)))
            {
                if (title.Length > 0)
                {
                    g.DrawString(Fit(g, title, mid, _info.Width), mid, ink, _info.X, y);
                    y += titleH;
                }
                if (sub.Length > 0)
                    g.DrawString(Fit(g, sub, font, _info.Width), font, dim, _info.X, y);
            }
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
        /// The readouts, two rows deep, in priority order so that shedding a column from
        /// the right gives up the least useful number first. Paired down each column by
        /// what belongs together: the two live LUFS figures, then the two programme ones,
        /// then peak and crest, then what the music is doing.
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

        /// <summary>One readout: caption over value, filling column-major down each pair.</summary>
        private void Cell(Graphics g, Font font, Font mid, Brush lbl, Brush val,
                          int index, string name, string value)
        {
            int col = index / 2, row = index % 2;
            if (col >= _loudCols) return;
            int cellH = _loud.Height / 2;
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

                string text = Clock(pos) + "  /  " + (dur > 0 ? Clock(dur) : "--:--");
                SizeF sz = g.MeasureString(text, font);
                // Left-aligned against the transport: right-aligning it in a wide stack
                // left the clock stranded a long way from the buttons.
                if (_clock.Width >= sz.Width)
                    g.DrawString(text, font, dim, _clock.X,
                                 _clock.Y + (_clock.Height - sz.Height) / 2);

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
            // Either rect may be empty - each meter has its own switch.
            double corr = meter.Correlation, bal = meter.Balance;
            DrawBipolar(g, font, alpha, _corr, "CORR", corr,
                        corr < 0 ? Color.FromArgb(255, 120, 90) : Color.FromArgb(120, 220, 160));
            DrawBipolar(g, font, alpha, _bal, "BAL", bal, Color.FromArgb(150, 190, 240));
        }

        /// <summary>One meter on one line: name, a centre-zero bar, then the value.</summary>
        private static void DrawBipolar(Graphics g, Font font, double alpha, Rectangle r,
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
                float ty = r.Y + (r.Height - ls.Height) / 2;
                g.DrawString(label, font, lbl, r.X, ty);
                g.DrawString(v, font, val, r.Right - vs.Width, ty);

                int x0 = r.X + (int)ls.Width + 6;
                int x1 = r.Right - (int)vs.Width - 6;
                if (x1 - x0 < 20) return;

                int barH = Math.Max(4, Math.Min(8, r.Height - 6));
                int barY = r.Y + (r.Height - barH) / 2;
                var track = new Rectangle(x0, barY, x1 - x0, barH);
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

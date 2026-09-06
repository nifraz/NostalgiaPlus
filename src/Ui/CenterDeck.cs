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
    /// Laid out left to right as artwork, goniometer, transport, meters. Whatever does
    /// not fit is dropped, least important first, and what remains is centred - the gap
    /// changes width with the graph size setting, so it cannot be assumed roomy.
    /// </summary>
    public sealed class CenterDeck
    {
        private Rectangle _art, _gonio, _stack;
        private Rectangle _prev, _play, _next, _seek, _clock, _corr, _bal;
        private Bitmap _artwork;
        private string _artworkKey;

        public Rectangle Bounds { get; private set; }
        /// <summary>Placed slots; empty when that element did not fit or is switched off.</summary>
        public Rectangle ArtRect { get { return _art; } }
        public Rectangle GoniometerRect { get { return _gonio; } }
        public Rectangle StackRect { get { return _stack; } }

        // The gap is only as wide as the two graph strips plus the gutter, which at a
        // 12% graph is around 260px - so the deck stacks vertically rather than laying
        // everything out in a row, and uses the band's full height instead of asking
        // for width it will not get.
        private const int MinStack = 150;
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
            _art = _gonio = _stack = Rectangle.Empty;
            _prev = _play = _next = _seek = _clock = _corr = _bal = Rectangle.Empty;
            if (gap.Width <= 40 || gap.Height <= 24) return;

            int h = gap.Height - Pad * 2;
            int square = Math.Min(h, gap.Width / 3);

            bool art = s.DeckShowArtwork;
            bool gonio = s.DeckShowGoniometer;
            bool player = s.DeckShowTransport;
            bool meters = s.DeckShowMeters;

            // Dropped least important first. The goniometer goes last: it is the one
            // instrument here, and the only thing that shows the stereo field at all.
            int need = Need(art, gonio, player || meters, square);
            if (need > gap.Width && art) { art = false; need = Need(art, gonio, player || meters, square); }
            if (need > gap.Width && meters) { meters = false; need = Need(art, gonio, player, square); }
            if (need > gap.Width && player) { player = false; need = Need(art, gonio, false, square); }
            if (need > gap.Width) return;

            bool stack = player || meters;
            int slack = gap.Width - need;
            int stackW = stack ? Math.Min(MaxStack, MinStack + Math.Max(0, slack)) : 0;
            // Whatever the stack declines to take is spread either side, so the deck
            // stays centred in the gap rather than hugging its left edge.
            int used = need - (stack ? MinStack : 0) + stackW;
            int x = gap.X + Pad + Math.Max(0, (gap.Width - used) / 2);
            int y = gap.Y + Pad;

            if (art) { _art = new Rectangle(x, y, square, h); x += square + Pad; }
            if (gonio) { _gonio = new Rectangle(x, y, square, h); x += square + Pad; }
            if (stack)
            {
                _stack = new Rectangle(x, y, stackW, h);
                LayoutStack(player, meters);
            }
        }

        private static int Need(bool art, bool gonio, bool stack, int square)
        {
            int n = 0, w = 0;
            if (art) { w += square; n++; }
            if (gonio) { w += square; n++; }
            if (stack) { w += MinStack; n++; }
            return w + Pad * (n + 1);
        }

        /// <summary>
        /// The right-hand column: transport, seek bar, then the two correlation meters,
        /// each taking a share of the height rather than competing for width.
        /// </summary>
        private void LayoutStack(bool player, bool meters)
        {
            int h = _stack.Height;
            int rows = (player ? 2 : 0) + (meters ? 2 : 0);
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
            if (meters)
            {
                int left = Math.Max(10, _stack.Bottom - y);
                int rowH = left / 2;
                _corr = new Rectangle(_stack.X, y, _stack.Width, rowH);
                _bal = new Rectangle(_stack.X, y + rowH, _stack.Width, rowH);
            }
        }

        // ---------------- drawing ----------------

        public void Draw(Graphics g, Settings s, Font font, double alpha, int[] lut,
                         LoudnessMeter meter, PlayerBridge player,
                         float[] gonL, float[] gonR, int gonCount)
        {
            if (Bounds.Width <= 0 || alpha <= 0.004) return;
            if (_art.Width > 0) DrawArtwork(g, alpha, player);
            if (_gonio.Width > 0) DrawGoniometer(g, alpha, lut, gonL, gonR, gonCount, font);
            if (_seek.Width > 0) DrawPlayer(g, font, alpha, player);
            if (_corr.Width > 0) DrawMeters(g, font, alpha, meter);
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

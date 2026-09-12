using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using NostalgiaPlus.Render;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// The immersive treatment: the album art behind everything, the palette drifting
    /// with the music's brightness, a flare on each beat, and the furniture fading out
    /// when nothing is happening.
    ///
    /// None of it is specific to being fullscreen. It lived there because that is where
    /// it was written, which left the docked panel unable to do any of it - so both
    /// views own one of these and the settings mean the same thing in either.
    /// </summary>
    public sealed class Immersion : IDisposable
    {
        private Bitmap _backdrop;
        private string _backdropKey;
        private int _backdropPct = -1;
        private double _hueShift;
        private DateTime _lastActivity = DateTime.UtcNow;

        private const double IdleHoldSeconds = 3.0;
        private const double IdleFadeSeconds = 1.5;

        /// <summary>Degrees the palette is currently rotated by.</summary>
        public double HueShift { get { return _hueShift; } }

        /// <summary>Call on any input, so the furniture comes back.</summary>
        public void Touch() { _lastActivity = DateTime.UtcNow; }

        /// <summary>
        /// How solid the furniture should be: full until the view has been idle a few
        /// seconds, then a short fade to nothing.
        /// </summary>
        public double FurnitureAlpha(Settings s)
        {
            if (!s.Immersive || !s.AutoHide) return 1.0;
            double idle = (DateTime.UtcNow - _lastActivity).TotalSeconds;
            if (idle <= IdleHoldSeconds) return 1.0;
            if (idle >= IdleHoldSeconds + IdleFadeSeconds) return 0.0;
            return 1.0 - (idle - IdleHoldSeconds) / IdleFadeSeconds;
        }

        /// <summary>
        /// Rebuilds the palette when the music's brightness has moved far enough to see.
        /// Returns the new table, or null when nothing changed.
        ///
        /// Only new spectrogram columns take the new colours - the ones already drawn
        /// keep the hue they were pushed with - so the image ends up carrying its own
        /// recent history in colour as well as in shape.
        /// </summary>
        public int[] UpdateHue(Settings s, double centroid)
        {
            double want = 0;
            if (s.ImmColourFollows && s.Immersive)
                want = (centroid - 0.5) * 2.0 * s.ColourFollowDegrees;
            // A degree either way is invisible; rebuilding the table for it is not free.
            if (Math.Abs(want - _hueShift) < 1.0) return null;
            _hueShift = want;
            return Palette.BuildLut(s.Palette, _hueShift);
        }

        /// <summary>
        /// The album art behind everything, blurred and dimmed.
        ///
        /// Drawn as the ground rather than over the top, so it shows through wherever
        /// there is no data - the graph strips, the margins, the quiet parts of the
        /// spectrogram - and is covered wherever there is. That is what makes it read
        /// as ambient light behind the analysis instead of a wash over it.
        /// </summary>
        public void DrawBackdrop(Graphics g, Settings s, Rectangle client,
                                 PlayerBridge player, ChannelPane[] panes)
        {
            if (!s.Immersive || !s.ImmBackdrop || player == null) return;
            int pct = Math.Max(0, Math.Min(60, s.BackdropPct));
            if (pct == 0) return;

            string data = player.SafeArtwork();
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
                if (panes != null)
                    for (int i = 0; i < panes.Length; i++)
                        if (panes[i].SpectroRect.Width > 0) g.ExcludeClip(panes[i].SpectroRect);

                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_backdrop, client, 0, 0, _backdrop.Width, _backdrop.Height,
                            GraphicsUnit.Pixel);
            }
            finally
            {
                g.Clip = clip;
                g.InterpolationMode = old;
            }
        }

        /// <summary>
        /// A flare along the view's edges on each onset.
        ///
        /// Four gradient bars rather than a full-screen vignette: the edges are where
        /// the eye catches movement without being pulled off the analysis, and it is
        /// about a sixth of the pixels a vignette would blend.
        /// </summary>
        public void DrawBeatFlare(Graphics g, Settings s, Rectangle client, int[] lut, double pulse)
        {
            if (!s.Immersive || !s.ImmBeatReactive) return;
            if (pulse <= 0.02) return;
            int w = client.Width, h = client.Height;
            // The band recedes as well as fades, so a decaying beat costs progressively
            // less to draw - and reads more like a flare than a light being dimmed.
            int band = (int)(Math.Max(20, Math.Min(48, h / 22)) * (0.35 + 0.65 * pulse));
            if (band < 6) return;
            Color c = Palette.ColorAt(lut, 0.9);
            int a = (int)(pulse * 90);
            if (a < 2) return;
            Color hot = Color.FromArgb(a, c), gone = Color.FromArgb(0, c);
            int x = client.X, y = client.Y;

            using (var top = new LinearGradientBrush(new Rectangle(x, y, w, band), hot, gone, LinearGradientMode.Vertical))
                g.FillRectangle(top, x, y, w, band);
            using (var bot = new LinearGradientBrush(new Rectangle(x, y + h - band, w, band), gone, hot, LinearGradientMode.Vertical))
                g.FillRectangle(bot, x, y + h - band, w, band);
            using (var left = new LinearGradientBrush(new Rectangle(x, y, band, h), hot, gone, LinearGradientMode.Horizontal))
                g.FillRectangle(left, x, y, band, h);
            using (var right = new LinearGradientBrush(new Rectangle(x + w - band, y, band, h), gone, hot, LinearGradientMode.Horizontal))
                g.FillRectangle(right, x + w - band, y, band, h);
        }

        public void Dispose()
        {
            if (_backdrop != null) { _backdrop.Dispose(); _backdrop = null; }
            _backdropKey = null;
        }
    }
}

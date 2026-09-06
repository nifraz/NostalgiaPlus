using System;
using System.Collections.Generic;
using System.Drawing;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Render;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// The row of one-click cycling buttons along the bottom of the view.
    ///
    /// One builder for both views, for the same reason <see cref="MenuFactory"/> is one
    /// builder: the docked panel and the fullscreen view show the same controls doing
    /// the same thing, and cannot drift apart as options are added.
    ///
    /// The bar occupies reserved space rather than floating over the image. Buttons that
    /// do not fit the available width are dropped from the end, so the order below is
    /// also the priority order.
    /// </summary>
    public sealed class QuickBar
    {
        private sealed class Item
        {
            public string Name;
            public Func<string> Value;
            public Action Cycle;
            public Rectangle Rect;
        }

        private readonly List<Item> _items = new List<Item>();
        private int _hot = -1;

        /// <summary>The strip the buttons were laid out in; empty when the bar is hidden.</summary>
        public Rectangle Bounds { get; private set; }

        /// <summary>
        /// Height the bar needs. Two lines when each button carries its name above its
        /// value, one when compact - which is what makes it usable on a short panel.
        /// </summary>
        public static int HeightFor(Font font, bool compact)
        {
            if (font == null) return 0;
            return compact ? font.Height + 8 : font.Height * 2 + 6;
        }

        /// <summary>
        /// Reserves the bar's strip off the bottom of <paramref name="area"/> and returns
        /// what is left for the panes. Returns the area unchanged when the bar is off or
        /// the view is too short to spare the pixels.
        /// </summary>
        public static Rectangle Reserve(Rectangle area, Settings s, Font font, out Rectangle bar)
        {
            bar = Rectangle.Empty;
            if (!s.ShowQuickButtons || font == null) return area;
            int h = HeightFor(font, s.QuickBarCompact);
            // Below this the bar would be taking more than it gives back.
            if (h <= 0 || area.Height < h * 4) return area;
            bar = new Rectangle(area.X, area.Bottom - h, area.Width, h);
            return new Rectangle(area.X, area.Y, area.Width, area.Height - h);
        }

        /// <summary>
        /// Advances an enum to its next member, wrapping. Shared with the keyboard
        /// shortcuts, which cycle the same settings the buttons do.
        /// </summary>
        public static T Next<T>(T current)
        {
            var vals = (T[])Enum.GetValues(typeof(T));
            int i = Array.IndexOf(vals, current);
            return vals[(i + 1) % vals.Length];
        }

        private static int NextIn(int[] values, int current)
        {
            int i = Array.IndexOf(values, current);
            return values[(i < 0 ? 0 : i + 1) % values.Length];
        }

        private static double NextIn(double[] values, double current)
        {
            int i = 0;
            for (int k = 0; k < values.Length; k++)
                if (Math.Abs(values[k] - current) < 0.01) i = k + 1;
            return values[i % values.Length];
        }

        /// <summary>Rebuilds the action list. Cheap; called whenever settings change.</summary>
        public void Build(Settings s, Action<bool> changed)
        {
            _items.Clear();
            _hot = -1;

            Add("CHANNELS", delegate { return s.PairMode.ToString(); },
                delegate { s.PairMode = Next(s.PairMode); changed(true); });

            Add("SCALE", delegate { return s.Scale.ToString(); },
                delegate
                {
                    s.Scale = Next(s.Scale);
                    // The useful range differs completely between the two mappings, so
                    // switching scale without moving it leaves a near-empty axis.
                    if (s.Scale == FreqScale.Linear) { s.FMin = 0; s.FMax = 22050; }
                    else { s.FMin = 25; s.FMax = 18000; }
                    s.Preset = Preset.Custom;
                    changed(true);
                });

            Add("RESOLUTION", delegate { return s.Quality.ToString(); },
                delegate { s.Quality = Next(s.Quality); s.Preset = Preset.Custom; changed(false); });

            Add("PALETTE", delegate { return Palette.DisplayName(s.Palette); },
                delegate { s.Palette = Next(s.Palette); s.Preset = Preset.Custom; changed(false); });

            Add("STYLE", delegate { return s.Style.ToString(); },
                delegate { s.Style = Next(s.Style); changed(false); });

            Add("TILT", delegate { return s.TiltDbPerOctave.ToString("0.0") + " dB/oct"; },
                delegate
                {
                    s.TiltDbPerOctave = NextIn(new double[] { 0, 1.5, 3.0, 4.5, 6.0 }, s.TiltDbPerOctave);
                    s.Preset = Preset.Custom;
                    changed(false);
                });

            Add("CONTRAST", delegate { return (s.Contrast * 100).ToString("0") + "%"; },
                delegate
                {
                    s.Contrast = NextIn(new double[] { 0.15, 0.25, 0.40, 0.55, 0.70, 0.82 }, s.Contrast);
                    changed(false);
                });

            Add("GRAPH", delegate { return s.CurveWidthPct + "%"; },
                delegate
                {
                    s.CurveWidthPct = NextIn(new int[] { 0, 8, 12, 18, 25, 33, 45 }, s.CurveWidthPct);
                    changed(true);
                });

            Add("SPEED", delegate { return "1/" + s.ScrollDivider; },
                delegate
                {
                    s.ScrollDivider = NextIn(new int[] { 1, 2, 4, 8 }, s.ScrollDivider);
                    changed(false);
                });

            Add("MIRROR", delegate { return s.MirrorLeftPane ? "On" : "Off"; },
                delegate { s.MirrorLeftPane = !s.MirrorLeftPane; changed(true); });
        }

        private void Add(string name, Func<string> value, Action cycle)
        {
            var b = new Item();
            b.Name = name; b.Value = value; b.Cycle = cycle;
            _items.Add(b);
        }

        /// <summary>
        /// Positions the buttons inside the reserved strip, centred, dropping any that
        /// will not fit rather than letting them run off the edge.
        /// </summary>
        public void Layout(Rectangle bar, Font font, bool compact)
        {
            Layout(bar, font, compact, 0, 0);
        }

        /// <summary>
        /// Positions the buttons, optionally splitting them either side of the centre
        /// gutter so the shared frequency axis runs unbroken from top to bottom - a
        /// single centred row sat right across it.
        /// </summary>
        public void Layout(Rectangle bar, Font font, bool compact, int centreLeft, int centreRight)
        {
            Bounds = bar;
            _centreLeft = centreLeft;
            _centreRight = centreRight;
            if (bar.Height <= 0 || font == null)
            {
                for (int i = 0; i < _items.Count; i++) _items[i].Rect = Rectangle.Empty;
                return;
            }

            // Measured against the screen rather than the control: layout runs during a
            // dock resize, before the panel's handle exists, and a bar measured with no
            // Graphics stayed empty until something else forced another resize.
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                LayoutWith(bar, g, font, compact);
        }

        private int _centreLeft, _centreRight;

        private void LayoutWith(Rectangle bar, Graphics g, Font font, bool compact)
        {
            const int Gap = 6;
            var widths = new int[_items.Count];
            int fits = 0, total = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                // Compact buttons show the value alone, so they are measured on it.
                string text = compact
                    ? _items[i].Value()
                    : Wider(g, font, _items[i].Name, _items[i].Value());
                widths[i] = (int)g.MeasureString(text, font).Width + 20;
                if (total + widths[i] + Gap > bar.Width) break;
                total += widths[i] + Gap;
                fits++;
            }
            if (fits > 0) total -= Gap;

            for (int i = fits; i < _items.Count; i++) _items[i].Rect = Rectangle.Empty;
            if (fits == 0) return;

            bool split = _centreRight > _centreLeft
                         && _centreLeft > bar.X && _centreRight < bar.Right;
            if (split)
            {
                // Half the row ends at the gutter's left edge, the rest starts at its
                // right. The split point follows the widths, not the count, so the two
                // halves come out visually even.
                int half = 0, seen = 0;
                for (int i = 0; i < fits; i++)
                {
                    if (seen + widths[i] / 2 > (total + Gap * (fits - 1)) / 2) { half = i; break; }
                    seen += widths[i] + Gap;
                    half = i + 1;
                }
                if (half < 1) half = 1;
                if (half > fits - 1) half = fits - 1;

                int leftW = 0;
                for (int i = 0; i < half; i++) leftW += widths[i] + Gap;
                if (half > 0) leftW -= Gap;

                int lx = _centreLeft - Gap - leftW;
                if (lx < bar.X) lx = bar.X;
                for (int i = 0; i < half; i++)
                {
                    _items[i].Rect = new Rectangle(lx, bar.Y + 1, widths[i], bar.Height - 2);
                    lx += widths[i] + Gap;
                }
                int rx = _centreRight + Gap;
                for (int i = half; i < fits; i++)
                {
                    if (rx + widths[i] > bar.Right) { _items[i].Rect = Rectangle.Empty; continue; }
                    _items[i].Rect = new Rectangle(rx, bar.Y + 1, widths[i], bar.Height - 2);
                    rx += widths[i] + Gap;
                }
                return;
            }

            int x = bar.X + (bar.Width - total) / 2;
            for (int i = 0; i < fits; i++)
            {
                _items[i].Rect = new Rectangle(x, bar.Y + 1, widths[i], bar.Height - 2);
                x += widths[i] + Gap;
            }
        }

        private static string Wider(Graphics g, Font font, string a, string b)
        {
            return g.MeasureString(a, font).Width >= g.MeasureString(b, font).Width ? a : b;
        }

        public void Draw(Graphics g, Font font, double alpha, bool compact)
        {
            if (Bounds.Height <= 0 || font == null || alpha <= 0.004) return;

            // Brushes and pens are built once for the whole bar; the previous per-button
            // allocation was ten of each every frame for no visible difference.
            using (var back = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(190, 18, 18, 22), alpha)))
            using (var hotBack = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(220, 40, 40, 50), alpha)))
            using (var edge = new Pen(StereoScope.FadeColor(Color.FromArgb(70, 255, 255, 255), alpha)))
            using (var hotEdge = new Pen(StereoScope.FadeColor(Color.FromArgb(150, 255, 255, 255), alpha)))
            using (var nameBrush = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(140, 190, 190, 200), alpha)))
            using (var valBrush = new SolidBrush(StereoScope.FadeColor(Color.FromArgb(235, 245, 245, 250), alpha)))
                for (int i = 0; i < _items.Count; i++)
                {
                    Item b = _items[i];
                    if (b.Rect.Width <= 0) continue;
                    bool hot = i == _hot;
                    g.FillRectangle(hot ? hotBack : back, b.Rect);
                    g.DrawRectangle(hot ? hotEdge : edge, b.Rect);

                    if (compact)
                    {
                        // One line: the value is what changes, so it is what shows. The
                        // name still reaches the user through the tooltip-free route of
                        // simply watching it change.
                        string v = b.Value();
                        SizeF sz = g.MeasureString(v, font);
                        g.DrawString(v, font, valBrush,
                                     b.Rect.X + (b.Rect.Width - sz.Width) / 2,
                                     b.Rect.Y + (b.Rect.Height - sz.Height) / 2);
                    }
                    else
                    {
                        g.DrawString(b.Name, font, nameBrush, b.Rect.X + 8, b.Rect.Y + 1);
                        g.DrawString(b.Value(), font, valBrush, b.Rect.X + 8, b.Rect.Y + font.Height);
                    }
                }
        }

        /// <summary>Tracks the button under the pointer. Returns true if it changed.</summary>
        public bool SetHot(Point p)
        {
            int hit = -1;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Rect.Width > 0 && _items[i].Rect.Contains(p)) { hit = i; break; }
            if (hit == _hot) return false;
            _hot = hit;
            return true;
        }

        public bool Contains(Point p)
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Rect.Width > 0 && _items[i].Rect.Contains(p)) return true;
            return false;
        }

        /// <summary>Cycles the button under the point. Returns true if one was hit.</summary>
        public bool Click(Point p)
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Rect.Width > 0 && _items[i].Rect.Contains(p))
                {
                    _items[i].Cycle();
                    return true;
                }
            return false;
        }
    }
}

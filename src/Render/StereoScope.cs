using System;
using System.Collections.Generic;
using System.Drawing;
using NostalgiaPlus.Audio;
using NostalgiaPlus.Dsp;

namespace NostalgiaPlus.Render
{
    /// <summary>Pointer state shared by both views so hover behaves identically.</summary>
    public sealed class HoverInfo
    {
        public Point Cursor;
        public bool Active;
        /// <summary>A measurement from <see cref="Origin"/> to <see cref="Cursor"/> is showing.</summary>
        public bool Measuring;
        public Point Origin;
    }

    /// <summary>
    /// Two side-by-side per-channel panes with a shared note gutter between them, plus
    /// the analysis that feeds them.
    ///
    /// Both the docked panel and the fullscreen view own one of these, so the layout,
    /// the analysis and the drawing are literally the same code in both modes - the
    /// views differ only in their chrome.
    /// </summary>
    public sealed class StereoScope : IDisposable
    {
        private readonly SpectrumAnalyzer _analyzer = new SpectrumAnalyzer();
        private readonly DynamicRange _range = new DynamicRange();
        private ChannelPane[] _panes = new ChannelPane[0];
        private FrequencyMap _map;
        private int[] _lut;
        private int _scrollTick;

        public Rectangle Bounds { get; private set; }
        public Rectangle GutterRect { get; private set; }
        /// <summary>Reserved strips at the far left/right for repeated axis labels.</summary>
        public Rectangle OuterLeftRect { get; private set; }
        public Rectangle OuterRightRect { get; private set; }
        public const int AxisMargin = 30;
        public double FloorDb { get; private set; }
        public double CeilingDb { get; private set; }
        public FrequencyMap Map { get { return _map; } }
        public ChannelPane[] Panes { get { return _panes; } }
        public SpectrumAnalyzer Analyzer { get { return _analyzer; } }

        public StereoScope() { FloorDb = -95; CeilingDb = -5; }

        public void SetPalette(int[] lut) { _lut = lut; }
        public void ResetRange() { _range.Reset(); }

        /// <summary>
        /// Rebuilds pane geometry. Single-channel modes collapse to one full-width pane.
        /// </summary>
        public void Layout(Rectangle bounds, Settings s, double sampleRate)
        {
            Bounds = bounds;

            // Repeating the axis at both screen edges means the labels need their own
            // space; overlaying them on the graph fill is unreadable at these widths.
            int wanted = AxisMargin + (int)Math.Max(0, (s.LabelFontSize - 7f) * 3);
            int margin = (s.ShowOuterLabels && s.ShowAxisLabels && bounds.Width > 6 * wanted)
                       ? wanted : 0;
            OuterLeftRect = new Rectangle(bounds.X, bounds.Y, margin, bounds.Height);
            OuterRightRect = new Rectangle(bounds.Right - margin, bounds.Y, margin, bounds.Height);
            bounds = new Rectangle(bounds.X + margin, bounds.Y,
                                   Math.Max(16, bounds.Width - 2 * margin), bounds.Height);
            int paneCount = SpectrumAnalyzer.PaneCount(s.PairMode);
            int gutter = paneCount == 2 ? Math.Max(0, Math.Min(90, s.FsGutterWidth)) : 0;

            if (_panes.Length != paneCount)
            {
                for (int i = 0; i < _panes.Length; i++) _panes[i].Dispose();
                _panes = new ChannelPane[paneCount];
                for (int i = 0; i < paneCount; i++) _panes[i] = new ChannelPane();
            }

            string[] labels = SpectrumAnalyzer.PaneLabels(s.PairMode);
            int paneW = Math.Max(8, (bounds.Width - gutter) / Math.Max(1, paneCount));

            int pct = Math.Max(0, Math.Min(60, s.CurveWidthPct));
            int curveWidth = paneW * pct / 100;

            // Mirroring the left pane puts both curves and both newest columns against
            // the centre, so history flows outward from the middle.
            bool leftOnLeft = (paneCount == 2 && s.MirrorLeftPane) ? !s.CurveOnLeft : s.CurveOnLeft;

            if (paneCount == 2)
            {
                _panes[0].Layout(new Rectangle(bounds.X, bounds.Y, paneW, bounds.Height),
                                 curveWidth, leftOnLeft, _lut);
                GutterRect = new Rectangle(bounds.X + paneW, bounds.Y, gutter, bounds.Height);
                _panes[1].Layout(new Rectangle(bounds.X + paneW + gutter, bounds.Y,
                                               Math.Max(8, bounds.Right - (bounds.X + paneW + gutter)),
                                               bounds.Height),
                                 curveWidth, s.CurveOnLeft, _lut);
                _panes[0].Label = labels[0];
                _panes[1].Label = labels[1];
            }
            else
            {
                _panes[0].Layout(bounds, curveWidth, s.CurveOnLeft, _lut);
                _panes[0].Label = labels[0];
                GutterRect = Rectangle.Empty;
            }

            double nyq = sampleRate * 0.5;
            double fMax = Math.Min(s.FMax, nyq);
            double fMin = s.Scale == FreqScale.Linear ? Math.Max(0, s.FMin) : Math.Max(10.0, s.FMin);
            _map = new FrequencyMap(s.Scale, Math.Max(1, _panes[0].SpectroRect.Height), fMin, fMax);
        }

        /// <summary>Runs one analysis frame. Returns false if there is not enough audio yet.</summary>
        public bool Analyse(LoopbackCapture cap, Settings s, double dt, bool frozen)
        {
            if (cap == null || _map == null || _panes.Length == 0) return false;
            _analyzer.Configure(cap.SampleRate, s.Quality, s.Window);

            ChannelPane a = _panes[0];
            ChannelPane b = _panes.Length > 1 ? _panes[1] : _panes[0];
            int n = _map.Width;
            if (a.Raw.Length < n) return false;

            if (!_analyzer.ComputeStereo(cap.Ring, _map, a.Raw, b.Raw,
                                         s.Aggregate, s.TiltDbPerOctave, s.PairMode))
                return false;

            a.Update(dt, s.Interp, s.Filter, s.AttackMs, s.ReleaseMs);
            if (!ReferenceEquals(a, b)) b.Update(dt, s.Interp, s.Filter, s.AttackMs, s.ReleaseMs);

            if (s.AdaptiveRange)
            {
                _range.LowPercentile = Math.Max(0.01, Math.Min(0.95, s.Contrast));
                _range.Observe(a.Raw, n, 0.94);
                if (!ReferenceEquals(a, b)) _range.Observe(b.Raw, n, 1.0);
                _range.Update(dt);
                FloorDb = _range.Floor;
                CeilingDb = _range.Ceiling;
            }
            else
            {
                FloorDb = s.FloorDb;
                CeilingDb = s.CeilingDb;
            }
            if (CeilingDb - FloorDb < 1) CeilingDb = FloorDb + 1;

            bool push = !frozen;
            int div = s.ScrollDivider;
            if (div > 1)
            {
                _scrollTick++;
                if (_scrollTick < div) push = false; else _scrollTick = 0;
            }
            if (push)
                for (int i = 0; i < _panes.Length; i++)
                    _panes[i].PushColumn(FloorDb, CeilingDb, _lut);

            return true;
        }

        public void DrawPanes(Graphics g, Settings s, bool glow, Font labelFont, double alpha, int topInset)
        {
            var o = new CurveDrawOptions();
            o.Style = s.Style; o.BarSize = s.BarSize; o.LedSegment = s.LedSegment;
            o.ShowMax = s.ShowMax; o.ShowMin = s.ShowMin; o.ShowAvg = s.ShowAvg;
            o.SolidFill = s.SolidFill; o.Background = s.Background;
            o.ShowDbScale = s.ShowDbScale; o.LabelFont = labelFont; o.Alpha = alpha;
            o.TopInset = topInset;

            double rowsPerSecond = (double)s.TargetFps / Math.Max(1, s.ScrollDivider);

            for (int i = 0; i < _panes.Length; i++)
            {
                _panes[i].DrawSpectrogram(g, _lut, glow);
                if (s.ShowTimeMarks && alpha > 0.004)
                    _panes[i].DrawTimeMarks(g, labelFont, rowsPerSecond, alpha, topInset);
                _panes[i].DrawCurve(g, _lut, FloorDb, CeilingDb, o);
            }
        }

        /// <summary>Gridlines across every pane, with note names in the centre gutter.</summary>
        public void DrawGrid(Graphics g, Settings s, Font labelFont, double alpha, int labelFloorY)
        {
            if (_map == null || _panes.Length == 0) return;
            var freqs = new List<double>();
            var labels = new List<string>();
            var subLabels = new List<string>();
            BuildGridLines(_map, freqs, labels, subLabels, s.LabelMode);
            bool showLabels = s.ShowAxisLabels;

            int h = _panes[0].SpectroRect.Height;
            int top = Bounds.Y;

            if (GutterRect.Width > 0)
                using (var bg = new SolidBrush(FadeColor(Color.FromArgb(255, 12, 12, 15), alpha)))
                    g.FillRectangle(bg, GutterRect);

            // Semitone lines only once there is room for them to read as lines.
            if (s.ShowSemitones && _map.Scale != FreqScale.Linear)
            {
                double pxPerOctave = h / Math.Log(_map.FMax / _map.FMin, 2.0);
                if (pxPerOctave > 96)
                    using (var fine = new Pen(FadeColor(Color.FromArgb(16, 255, 255, 255), alpha)))
                        for (int midi = 12; midi <= 132; midi++)
                        {
                            if (midi % 12 == 0) continue;
                            double f = FrequencyMap.MidiToFreq(midi);
                            if (f < _map.FMin || f > _map.FMax) continue;
                            int y = top + h - 1 - (int)Math.Round(_map.FreqToX(f));
                            if (y < top || y >= top + h) continue;
                            g.DrawLine(fine, Bounds.Left, y, Bounds.Right, y);
                        }
            }

            using (var pen = new Pen(FadeColor(Color.FromArgb(38, 255, 255, 255), alpha)))
            using (var brush = new SolidBrush(FadeColor(Color.FromArgb(185, 232, 232, 238), alpha)))
            {
                for (int i = 0; i < freqs.Count; i++)
                {
                    int y = top + h - 1 - (int)Math.Round(_map.FreqToX(freqs[i]));
                    if (y < top || y >= top + h) continue;
                    g.DrawLine(pen, Bounds.Left, y, Bounds.Right, y);

                    if (!showLabels) continue;
                    if (y < labelFloorY) continue;   // keep clear of the top overlay bar

                    string primary = labels[i];
                    string secondary = subLabels[i];
                    SizeF sz = g.MeasureString(primary, labelFont);
                    float lineH = sz.Height - 2;

                    if (GutterRect.Width >= 22)
                    {
                        g.DrawString(primary, labelFont, brush,
                                     GutterRect.Left + (GutterRect.Width - sz.Width) / 2, y - 13);
                        if (secondary != null)
                        {
                            SizeF s2 = g.MeasureString(secondary, labelFont);
                            g.DrawString(secondary, labelFont, brush,
                                         GutterRect.Left + (GutterRect.Width - s2.Width) / 2,
                                         y - 13 + lineH);
                        }
                    }
                    if (OuterLeftRect.Width > 0)
                    {
                        g.DrawString(primary, labelFont, brush,
                                     OuterLeftRect.Left + (OuterLeftRect.Width - sz.Width) / 2, y - 13);
                        g.DrawString(primary, labelFont, brush,
                                     OuterRightRect.Left + (OuterRightRect.Width - sz.Width) / 2, y - 13);
                        if (secondary != null)
                        {
                            SizeF s2 = g.MeasureString(secondary, labelFont);
                            g.DrawString(secondary, labelFont, brush,
                                         OuterLeftRect.Left + (OuterLeftRect.Width - s2.Width) / 2,
                                         y - 13 + lineH);
                            g.DrawString(secondary, labelFont, brush,
                                         OuterRightRect.Left + (OuterRightRect.Width - s2.Width) / 2,
                                         y - 13 + lineH);
                        }
                    }
                }
            }
        }

        public void DrawPaneLabels(Graphics g, Font font, double alpha, int yOffset)
        {
            if (alpha <= 0.004) return;
            using (var back = new SolidBrush(FadeColor(Color.FromArgb(150, 8, 8, 10), alpha)))
            using (var brush = new SolidBrush(FadeColor(Color.FromArgb(225, 240, 240, 245), alpha)))
                for (int i = 0; i < _panes.Length; i++)
                {
                    Rectangle sr = _panes[i].SpectroRect;
                    string t = _panes[i].Label;
                    SizeF sz = g.MeasureString(t, font);
                    // Park the channel label at the past end - the oldest edge, opposite
                    // the curve - so it never sits on top of the live incoming column.
                    float x = _panes[i].CurveOnLeft ? sr.Right - sz.Width - 8 : sr.Left + 5;
                    g.FillRectangle(back, x - 3, sr.Top + 3 + yOffset, sz.Width + 6, sz.Height);
                    g.DrawString(t, font, brush, x, sr.Top + 2 + yOffset);
                }
        }

        public static void BuildGridLines(FrequencyMap map, List<double> freqs, List<string> labels)
        {
            BuildGridLines(map, freqs, labels, null, AxisLabelMode.Notes);
        }

        /// <summary>
        /// Gridline frequencies plus their labels. A second line is filled in for the
        /// Both mode rather than widening the label, so a narrow gutter still fits.
        /// </summary>
        public static void BuildGridLines(FrequencyMap map, List<double> freqs,
                                          List<string> labels, List<string> subLabels,
                                          AxisLabelMode mode)
        {
            if (map.Scale == FreqScale.Linear)
            {
                for (double f = 2000; f <= map.FMax; f += 2000)
                {
                    freqs.Add(f);
                    labels.Add(FormatShortHz(f));
                    if (subLabels != null) subLabels.Add(null);
                }
                return;
            }

            for (int midi = 12; midi <= 132; midi += 12)
            {
                double f = FrequencyMap.MidiToFreq(midi);
                if (f < map.FMin || f > map.FMax) continue;
                string note = "C" + ((midi / 12) - 1);
                freqs.Add(f);
                if (mode == AxisLabelMode.Frequency)
                {
                    labels.Add(FormatShortHz(f));
                    if (subLabels != null) subLabels.Add(null);
                }
                else
                {
                    labels.Add(note);
                    if (subLabels != null)
                        subLabels.Add(mode == AxisLabelMode.Both ? FormatShortHz(f) : null);
                }
            }
        }

        private static string FormatShortHz(double f)
        {
            if (f >= 10000) return (f / 1000.0).ToString("0") + "k";
            if (f >= 1000) return (f / 1000.0).ToString("0.0") + "k";
            return f.ToString("0");
        }

        public static Color FadeColor(Color c, double a)
        {
            int alpha = (int)Math.Round(c.A * a);
            if (alpha < 0) alpha = 0; else if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        /// <summary>
        /// Draws the hover readout. Both panes share one frequency axis, so the same y is
        /// the same frequency in both - the line is drawn right across and the readout
        /// gives every channel at once, which is the comparison the split layout exists
        /// for. Returns false when the cursor is not over a pane.
        /// </summary>
        public bool DrawHover(Graphics g, HoverInfo h, Settings s, Font font, Font pinFont)
        {
            if (_map == null || _panes.Length == 0 || h == null || !h.Active) return false;
            Point mouse = h.Cursor;

            ChannelPane hit = null;
            for (int i = 0; i < _panes.Length; i++)
                if (_panes[i].Bounds.Contains(mouse)) hit = _panes[i];
            if (hit == null) return false;

            int hgt = hit.SpectroRect.Height;
            int top = hit.SpectroRect.Top;
            int bin = hgt - 1 - (mouse.Y - top);
            if (bin < 0 || bin >= _map.Width) return false;

            double freq = _map.Centres[bin];
            double cents;
            string note = FrequencyMap.DescribeNote(freq, out cents);

            int lineFrom = s.SyncHover ? Bounds.Left : hit.Bounds.Left;
            int lineTo = s.SyncHover ? Bounds.Right : hit.Bounds.Right;
            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255)))
            {
                g.DrawLine(pen, lineFrom, mouse.Y, lineTo, mouse.Y);
                // Mark the sampled instant, and mirror it into the other pane, which
                // under Mirror is the opposite x for the same moment.
                int a = hit.AgeAt(mouse.X);
                if (a > 0)
                    for (int i = 0; i < _panes.Length; i++)
                    {
                        if (!s.SyncHover && !ReferenceEquals(_panes[i], hit)) continue;
                        Rectangle sr = _panes[i].SpectroRect;
                        int mx = _panes[i].CurveOnLeft ? sr.Left + a : sr.Right - 1 - a;
                        if (mx >= sr.Left && mx < sr.Right)
                            g.DrawLine(pen, mx, sr.Top, mx, sr.Bottom);
                    }
            }

            // Mark where the line crosses each pane's curve, so the level is locatable.
            using (var dot = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                for (int i = 0; i < _panes.Length; i++)
                {
                    if (!s.SyncHover && !ReferenceEquals(_panes[i], hit)) continue;
                    if (_panes[i].CurveRect.Width < 3) continue;
                    double lv;
                    if (hit.AgeAt(mouse.X) <= 0 || !_panes[i].TryHistory(hit.AgeAt(mouse.X), bin, out lv))
                        lv = _panes[i].Display[bin];
                    double t = (lv - FloorDb) / Math.Max(1, CeilingDb - FloorDb);
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    int baseX = _panes[i].CurveOnLeft ? _panes[i].CurveRect.Left : _panes[i].CurveRect.Right;
                    int dir = _panes[i].CurveOnLeft ? 1 : -1;
                    int x = (int)(baseX + dir * t * _panes[i].CurveRect.Width);
                    g.FillRectangle(dot, x - 2, mouse.Y - 2, 4, 4);
                }

            // Read the column actually under the cursor rather than the live spectrum,
            // so pointing at something ten seconds back reports what happened then.
            int age = hit.AgeAt(mouse.X);
            bool historic = age > 0;
            var levels = new double[_panes.Length];
            for (int i = 0; i < _panes.Length; i++)
            {
                double v;
                if (!historic || !_panes[i].TryHistory(age, bin, out v)) v = _panes[i].Display[bin];
                levels[i] = v;
            }

            string text = FormatHz(freq) + "   " + note + cents.ToString("+0;-0;+0") + "c";
            for (int i = 0; i < _panes.Length; i++)
                text += "   " + _panes[i].Label + " " + levels[i].ToString("0.0");
            text += " dB";
            if (_panes.Length == 2)
                text += "   d " + (levels[0] - levels[1]).ToString("+0.0;-0.0; 0.0");
            if (historic)
            {
                double rowsPerSecond = (double)s.TargetFps / Math.Max(1, s.ScrollDivider);
                if (rowsPerSecond > 0)
                    text += "   -" + (age / rowsPerSecond).ToString("0.00") + "s";
            }

            SizeF ts = g.MeasureString(text, font);
            float bx = mouse.X + 14;
            float by = mouse.Y - ts.Height - 10;
            if (bx + ts.Width + 10 > Bounds.Right) bx = mouse.X - ts.Width - 16;
            if (by < Bounds.Top + 2) by = mouse.Y + 12;
            using (var back = new SolidBrush(Color.FromArgb(220, 10, 10, 12)))
                g.FillRectangle(back, bx - 5, by - 3, ts.Width + 10, ts.Height + 6);
            using (var border = new Pen(Color.FromArgb(80, 255, 255, 255)))
                g.DrawRectangle(border, bx - 5, by - 3, ts.Width + 10, ts.Height + 6);
            using (var brush = new SolidBrush(Color.FromArgb(245, 240, 240, 245)))
                g.DrawString(text, font, brush, bx, by);

            // Stamp the frequency onto the axis itself so the eye can stay on the image.
            if (s.ShowHoverPin && pinFont != null)
            {
                string pin = note;
                SizeF ps = g.MeasureString(pin, pinFont);
                using (var back = new SolidBrush(Color.FromArgb(235, 245, 245, 250)))
                using (var fg = new SolidBrush(Color.FromArgb(255, 12, 12, 16)))
                {
                    if (GutterRect.Width >= 20)
                    {
                        float gx = GutterRect.Left + (GutterRect.Width - ps.Width) / 2;
                        g.FillRectangle(back, gx - 2, mouse.Y - ps.Height / 2, ps.Width + 4, ps.Height);
                        g.DrawString(pin, pinFont, fg, gx, mouse.Y - ps.Height / 2);
                    }
                    if (OuterLeftRect.Width > 0)
                    {
                        float lx = OuterLeftRect.Left + (OuterLeftRect.Width - ps.Width) / 2;
                        g.FillRectangle(back, lx - 2, mouse.Y - ps.Height / 2, ps.Width + 4, ps.Height);
                        g.DrawString(pin, pinFont, fg, lx, mouse.Y - ps.Height / 2);
                        float rx = OuterRightRect.Left + (OuterRightRect.Width - ps.Width) / 2;
                        g.FillRectangle(back, rx - 2, mouse.Y - ps.Height / 2, ps.Width + 4, ps.Height);
                        g.DrawString(pin, pinFont, fg, rx, mouse.Y - ps.Height / 2);
                    }
                }
            }
            // Harmonic ruler: a line is either a fundamental or somebody's overtone, and
            // this is the quickest way to tell which.
            if (s.ShowHarmonics)
            {
                using (var hp = new Pen(Color.FromArgb(70, 160, 210, 255)))
                using (var hb = new SolidBrush(Color.FromArgb(150, 170, 215, 255)))
                    for (int n = 2; n <= 8; n++)
                    {
                        double hf = freq * n;
                        if (hf > _map.FMax) break;
                        int hy = top + hgt - 1 - (int)Math.Round(_map.FreqToX(hf));
                        if (hy < top || hy >= top + hgt) continue;
                        g.DrawLine(hp, Bounds.Left, hy, Bounds.Right, hy);
                        // Sit inside the reserved axis margin rather than on top of it.
                        float hx = OuterLeftRect.Width > 0 ? OuterLeftRect.Right + 4 : Bounds.Left + 4;
                        g.DrawString("x" + n, pinFont ?? font, hb, hx, hy - 12);
                    }
            }

            // Drag measurement: interval between two points, and how far apart in time.
            if (h.Measuring)
            {
                int obin = hgt - 1 - (h.Origin.Y - hit.SpectroRect.Top);
                if (obin >= 0 && obin < _map.Width)
                {
                    double ofreq = _map.Centres[obin];
                    using (var mp = new Pen(Color.FromArgb(150, 255, 220, 120)))
                    {
                        g.DrawLine(mp, Bounds.Left, h.Origin.Y, Bounds.Right, h.Origin.Y);
                        g.DrawLine(mp, h.Origin.X, h.Origin.Y, mouse.X, mouse.Y);
                    }

                    double semis = 12.0 * Math.Log(freq / ofreq, 2.0);
                    string mtext = semis.ToString("+0.00;-0.00; 0.00") + " st";
                    double rps = (double)s.TargetFps / Math.Max(1, s.ScrollDivider);
                    if (rps > 0)
                    {
                        double dt = Math.Abs(hit.AgeAt(mouse.X) - hit.AgeAt(h.Origin.X)) / rps;
                        mtext += "   " + dt.ToString("0.00") + "s";
                    }
                    mtext += "   " + FormatHz(ofreq) + " -> " + FormatHz(freq);

                    SizeF ms = g.MeasureString(mtext, font);
                    float mx2 = Math.Min(Math.Max(Bounds.Left + 4, (h.Origin.X + mouse.X) / 2f - ms.Width / 2),
                                         Bounds.Right - ms.Width - 6);
                    float my2 = Math.Min(h.Origin.Y, mouse.Y) - ms.Height - 8;
                    if (my2 < Bounds.Top + 2) my2 = Math.Max(h.Origin.Y, mouse.Y) + 8;
                    using (var back = new SolidBrush(Color.FromArgb(225, 24, 20, 8)))
                        g.FillRectangle(back, mx2 - 5, my2 - 3, ms.Width + 10, ms.Height + 6);
                    using (var border = new Pen(Color.FromArgb(140, 255, 220, 120)))
                        g.DrawRectangle(border, mx2 - 5, my2 - 3, ms.Width + 10, ms.Height + 6);
                    using (var brush = new SolidBrush(Color.FromArgb(245, 255, 232, 170)))
                        g.DrawString(mtext, font, brush, mx2, my2);
                }
            }

            return true;
        }

        private static string FormatHz(double f)
        {
            if (f >= 1000) return (f / 1000.0).ToString("0.00") + " kHz";
            return f.ToString("0.0") + " Hz";
        }

        public void ResetPanes()
        {
            for (int i = 0; i < _panes.Length; i++) _panes[i].Reset(_lut);
            _range.Reset();
        }

        public void Dispose()
        {
            for (int i = 0; i < _panes.Length; i++) _panes[i].Dispose();
            _panes = new ChannelPane[0];
        }
    }
}

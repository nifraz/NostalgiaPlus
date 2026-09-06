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
        private readonly MusicFeatures _features = new MusicFeatures();
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
        /// <summary>Height of the reserved scale strip, 0 when the scales are overlaid.</summary>
        public int LaneHeight { get { return _panes.Length == 0 ? 0 : _panes[0].LaneRect.Height; } }
        /// <summary>True when that strip is above the image rather than below it.</summary>
        public bool LaneAtTop
        {
            get { return _panes.Length > 0 && _panes[0].LaneRect.Height > 0
                         && _panes[0].LaneRect.Top <= _panes[0].Bounds.Top; }
        }
        /// <summary>
        /// Where chrome drawn over the image has to start so it does not land on the
        /// scale strip: the strip's height when it is at the top, otherwise zero.
        /// </summary>
        public int ChromeTop { get { return LaneAtTop ? LaneHeight : 0; } }
        /// <summary>
        /// True when the last <see cref="Analyse"/> call appended a spectrogram column.
        /// Anything else that scrolls alongside the spectrograms - the waveform lanes -
        /// has to advance on this and nothing else, or it covers a different span of
        /// time at every scroll speed but 1/1.
        /// </summary>
        public bool PushedColumn { get; private set; }
        public double FloorDb { get; private set; }
        public double CeilingDb { get; private set; }
        public FrequencyMap Map { get { return _map; } }
        public ChannelPane[] Panes { get { return _panes; } }
        public SpectrumAnalyzer Analyzer { get { return _analyzer; } }
        /// <summary>What the music is doing, for the views to react to.</summary>
        public MusicFeatures Features { get { return _features; } }

        public StereoScope() { FloorDb = -95; CeilingDb = -5; }

        public void SetPalette(int[] lut) { _lut = lut; }
        public void ResetRange() { _range.Reset(); _features.Reset(); }

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
            int gutter = paneCount == 2 ? Math.Max(0, Math.Min(90, s.GutterWidth)) : 0;
            int laneH = s.ScaleLaneHeight;
            bool laneTop = s.ScaleLanePos == ScaleLanePosition.Top;

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

            // Both the centre gutter and the outer strips carry the frequency axis, so
            // they span the panes' image area rather than the whole control - otherwise
            // a note label could land beside the scale strip instead of beside its row.
            if (paneCount == 2)
            {
                _panes[0].Layout(new Rectangle(bounds.X, bounds.Y, paneW, bounds.Height),
                                 curveWidth, leftOnLeft, _lut, laneH, laneTop);
                GutterRect = new Rectangle(bounds.X + paneW, bounds.Y, gutter, bounds.Height);
                _panes[1].Layout(new Rectangle(bounds.X + paneW + gutter, bounds.Y,
                                               Math.Max(8, bounds.Right - (bounds.X + paneW + gutter)),
                                               bounds.Height),
                                 curveWidth, s.CurveOnLeft, _lut, laneH, laneTop);
                _panes[0].Label = labels[0];
                _panes[1].Label = labels[1];
            }
            else
            {
                _panes[0].Layout(bounds, curveWidth, s.CurveOnLeft, _lut, laneH, laneTop);
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
            PushedColumn = false;
            if (cap == null || _map == null || _panes.Length == 0) return false;
            _analyzer.Configure(cap.SampleRate, s.Quality, s.Window);

            ChannelPane a = _panes[0];
            ChannelPane b = _panes.Length > 1 ? _panes[1] : _panes[0];
            int n = _map.Width;
            if (a.Raw.Length < n) return false;

            if (!_analyzer.ComputeStereo(cap.Ring, _map, a.Raw, b.Raw,
                                         s.Aggregate, s.TiltDbPerOctave, s.PairMode))
                return false;

            // Cinematic lengthens the fall rather than the rise: hits still arrive
            // sharply, they just take longer to let go, which is what reads as a trail.
            double release = s.ImmCinematic ? s.ReleaseMs * 3.0 : s.ReleaseMs;

            a.Update(dt, s.Interp, s.Filter, s.AttackMs, release,
                     s.PeakDecayDbPerSec, s.AverageSeconds);
            if (!ReferenceEquals(a, b))
                b.Update(dt, s.Interp, s.Filter, s.AttackMs, release,
                         s.PeakDecayDbPerSec, s.AverageSeconds);

            // Driven from one channel's raw spectrum: the two channels of music share
            // their onsets, and running the detector twice would only cost twice.
            _features.Update(a.Raw, n, dt);

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
            int div = s.ImmCinematic ? s.ScrollDivider * 4 : s.ScrollDivider;
            if (div > 1)
            {
                _scrollTick++;
                if (_scrollTick < div) push = false; else _scrollTick = 0;
            }
            if (push)
                for (int i = 0; i < _panes.Length; i++)
                    _panes[i].PushColumn(FloorDb, CeilingDb, _lut);
            PushedColumn = push;

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

            // dBFS rather than dB: these are magnitudes against full scale, and saying
            // so is the difference between a number you can compare across tracks and
            // one you cannot.
            string levelUnit = s.ShowScaleUnits && s.ShowDbScale ? "dBFS" : null;
            string timeUnit = s.ShowScaleUnits && s.ShowTimeMarks ? "now" : null;

            for (int i = 0; i < _panes.Length; i++)
            {
                _panes[i].DrawScaleLane(g, alpha, labelFont, levelUnit, timeUnit);
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
            var major = new List<bool>();
            int axisPixels = _panes[0].SpectroRect.Height;
            BuildGridLines(_map, freqs, labels, subLabels, s.LabelMode, major, axisPixels);
            bool showLabels = s.ShowAxisLabels;

            int h = _panes[0].SpectroRect.Height;
            int top = _panes[0].SpectroRect.Top;
            if (GutterRect.Width > 0)
                using (var bg = new SolidBrush(FadeColor(Color.FromArgb(255, 12, 12, 15), alpha)))
                    g.FillRectangle(bg, GutterRect);

            // After the gutter is filled, not before: the fill covers the whole column
            // and was painting over the caption.
            int unitFloor = 0;
            if (showLabels && s.ShowScaleUnits)
            {
                DrawAxisUnit(g, s, labelFont, alpha, top);
                // With no strip the caption sits on the axis itself, so the rows have to
                // start below it.
                if (_panes[0].LaneRect.Height == 0 && labelFont != null)
                    unitFloor = labelFont.Height + 2;
            }

            // Semitone lines only once there is room for them to read as lines.
            if (s.ShowSemitones && _map.Scale != FreqScale.Linear)
            {
                double pxPerOctave = h / Math.Log(_map.FMax / _map.FMin, 2.0);
                if (pxPerOctave > 96 && freqs.Count < 40)
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
            using (var minorPen = new Pen(FadeColor(Color.FromArgb(20, 255, 255, 255), alpha)))
            using (var brush = new SolidBrush(FadeColor(Color.FromArgb(185, 232, 232, 238), alpha)))
            using (var minorBrush = new SolidBrush(FadeColor(Color.FromArgb(120, 210, 210, 220), alpha)))
            {
                for (int i = 0; i < freqs.Count; i++)
                {
                    int y = top + h - 1 - (int)Math.Round(_map.FreqToX(freqs[i]));
                    if (y < top || y >= top + h) continue;
                    bool isMajor = i >= major.Count || major[i];
                    Pen linePen = isMajor ? pen : minorPen;

                    // Across the image only. It used to run the full width, straight
                    // through the label columns - which is the whole reason the labels
                    // were pushed a line-height above their own row: centred on the row,
                    // the line struck through the text. Stopping it at the panes lets
                    // the labels sit where they belong, and the columns carry a tick
                    // instead, exactly as the time and level scales do.
                    for (int q = 0; q < _panes.Length; q++)
                        g.DrawLine(linePen, _panes[q].Bounds.Left, y, _panes[q].Bounds.Right, y);

                    if (!showLabels) continue;
                    // The overlay bar holds the title on the left and the meters on the
                    // right; the centre gutter is clear of both, so only the outer
                    // columns have to keep out of its way. Applying the floor to all
                    // three cost the top 8% of the axis you read most.
                    bool outerOk = y >= labelFloorY;

                    string primary = labels[i];
                    string secondary = subLabels[i];
                    SolidBrush ink = isMajor ? brush : minorBrush;
                    SizeF sz = g.MeasureString(primary, labelFont);
                    float lineH = sz.Height - 2;

                    // Centred on the row it names. The old placement was a hardcoded
                    // 13px above the line - roughly one line height, so a label pointed
                    // at a row it was not naming, which on a note axis is a couple of
                    // semitones out. Clamped so the end labels stay inside the axis and
                    // clear of the unit caption when that sits on the axis itself.
                    float blockH = secondary != null ? lineH + sz.Height : sz.Height;
                    float ly = y - blockH / 2f;
                    if (ly < top + unitFloor) ly = top + unitFloor;
                    if (ly + blockH > top + h) ly = top + h - blockH;

                    // A tick on the edge of each column that faces the image, so the
                    // column reads as a ruler against the picture.
                    const int Tick = 4;
                    if (GutterRect.Width >= 22)
                    {
                        g.DrawLine(linePen, GutterRect.Left, y, GutterRect.Left + Tick, y);
                        g.DrawLine(linePen, GutterRect.Right - Tick, y, GutterRect.Right, y);
                    }
                    if (OuterLeftRect.Width > 0 && outerOk)
                    {
                        g.DrawLine(linePen, OuterLeftRect.Right - Tick, y, OuterLeftRect.Right, y);
                        g.DrawLine(linePen, OuterRightRect.Left, y, OuterRightRect.Left + Tick, y);
                    }

                    if (GutterRect.Width >= 22)
                    {
                        g.DrawString(primary, labelFont, ink,
                                     GutterRect.Left + (GutterRect.Width - sz.Width) / 2, ly);
                        if (secondary != null)
                        {
                            SizeF s2 = g.MeasureString(secondary, labelFont);
                            g.DrawString(secondary, labelFont, ink,
                                         GutterRect.Left + (GutterRect.Width - s2.Width) / 2,
                                         ly + lineH);
                        }
                    }
                    if (OuterLeftRect.Width > 0 && outerOk)
                    {
                        g.DrawString(primary, labelFont, ink,
                                     OuterLeftRect.Left + (OuterLeftRect.Width - sz.Width) / 2, ly);
                        g.DrawString(primary, labelFont, ink,
                                     OuterRightRect.Left + (OuterRightRect.Width - sz.Width) / 2, ly);
                        if (secondary != null)
                        {
                            SizeF s2 = g.MeasureString(secondary, labelFont);
                            g.DrawString(secondary, labelFont, ink,
                                         OuterLeftRect.Left + (OuterLeftRect.Width - s2.Width) / 2,
                                         ly + lineH);
                            g.DrawString(secondary, labelFont, ink,
                                         OuterRightRect.Left + (OuterRightRect.Width - s2.Width) / 2,
                                         ly + lineH);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Names the frequency axis at the top of every column that carries it. Sits in
        /// the scale strip when there is one, and on a chip at the top of the axis when
        /// there is not - the unit should not vanish just because the strip is off.
        /// </summary>
        private void DrawAxisUnit(Graphics g, Settings s, Font font, double alpha, int axisTop)
        {
            if (font == null || alpha <= 0.004) return;
            string unit = s.LabelMode == AxisLabelMode.Notes && _map.Scale != FreqScale.Linear
                        ? "note" : "Hz";
            SizeF sz = g.MeasureString(unit, font);

            Rectangle lane = _panes[0].LaneRect;
            bool inLane = lane.Height >= sz.Height;
            float y = inLane ? lane.Top + (lane.Height - sz.Height) / 2f : axisTop + 1;

            using (var chip = new SolidBrush(FadeColor(Color.FromArgb(190, 8, 8, 11), alpha)))
            using (var ink = new SolidBrush(FadeColor(Color.FromArgb(190, 150, 200, 245), alpha)))
            {
                DrawUnitIn(g, GutterRect, unit, font, sz, y, inLane, chip, ink);
                DrawUnitIn(g, OuterLeftRect, unit, font, sz, y, inLane, chip, ink);
                DrawUnitIn(g, OuterRightRect, unit, font, sz, y, inLane, chip, ink);
            }
        }

        private static void DrawUnitIn(Graphics g, Rectangle column, string unit, Font font,
                                       SizeF sz, float y, bool inLane, Brush chip, Brush ink)
        {
            if (column.Width < sz.Width + 2) return;
            float x = column.Left + (column.Width - sz.Width) / 2f;
            if (!inLane) g.FillRectangle(chip, x - 2, y - 1, sz.Width + 4, sz.Height + 1);
            g.DrawString(unit, font, ink, x, y);
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
            BuildGridLines(map, freqs, labels, subLabels, mode, null, 0);
        }

        /// <summary>
        /// Gridline frequencies and their labels, at a density that suits the space.
        ///
        /// A fixed one-label-per-octave wastes a 1080px fullscreen axis and crowds a
        /// short docked strip equally badly. The step is chosen so labels land roughly
        /// 30px apart, subdividing the octave musically - octave, tritone, major third,
        /// minor third, whole tone, semitone - rather than at arbitrary intervals.
        /// <paramref name="major"/> receives whether each line is an octave, so octaves
        /// can still be drawn more strongly than the subdivisions between them.
        /// </summary>
        public static void BuildGridLines(FrequencyMap map, List<double> freqs,
                                          List<string> labels, List<string> subLabels,
                                          AxisLabelMode mode, List<bool> major, int axisPixels)
        {
            const double TargetSpacing = 30.0;

            if (map.Scale == FreqScale.Linear)
            {
                double[] steps = { 500, 1000, 2000, 5000, 10000 };
                double step = steps[steps.Length - 1];
                for (int i = 0; i < steps.Length; i++)
                {
                    double px = axisPixels <= 0 ? 0 : axisPixels * steps[i] / Math.Max(1, map.FMax - map.FMin);
                    if (axisPixels <= 0) { step = 2000; break; }
                    if (px >= TargetSpacing) { step = steps[i]; break; }
                }
                for (double f = step; f <= map.FMax; f += step)
                {
                    if (f < map.FMin) continue;
                    freqs.Add(f);
                    labels.Add(FormatShortHz(f));
                    if (subLabels != null) subLabels.Add(null);
                    if (major != null) major.Add(Math.Abs(f % (step * 5)) < 1);
                }
                return;
            }

            double octaves = Math.Log(map.FMax / map.FMin, 2.0);
            double pxPerOctave = (axisPixels <= 0 || octaves <= 0) ? 0 : axisPixels / octaves;
            int[] semitoneSteps = { 12, 6, 4, 3, 2, 1 };
            int stepSemis = 12;
            // Walk from the finest subdivision upward and stop at the first that fits;
            // without the break this kept overwriting with coarser steps and always
            // settled on whole octaves.
            if (pxPerOctave > 0)
                for (int i = semitoneSteps.Length - 1; i >= 0; i--)
                    if (pxPerOctave * semitoneSteps[i] / 12.0 >= TargetSpacing)
                    {
                        stepSemis = semitoneSteps[i];
                        break;
                    }

            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            for (int midi = 12; midi <= 132; midi += stepSemis)
            {
                double f = FrequencyMap.MidiToFreq(midi);
                if (f < map.FMin || f > map.FMax) continue;
                bool isOctave = (midi % 12) == 0;
                string note = names[midi % 12] + ((midi / 12) - 1);
                freqs.Add(f);
                if (major != null) major.Add(isOctave);
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

        /// <summary>
        /// One style for the whole axis. The old rule switched decimals at 10 kHz, so a
        /// linear axis read "8.0k" next to "22k" - two conventions in one column, which
        /// reads as an inconsistency rather than as precision.
        /// </summary>
        private static string FormatShortHz(double f)
        {
            if (f >= 1000) return (f / 1000.0).ToString("0.#") + "k";
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
            return DrawHover(g, h, s, font, pinFont, 1.0);
        }

        /// <summary>
        /// The hover overlay, faded with everything else.
        ///
        /// It used to draw at fixed opacity while the rest of the furniture faded out,
        /// so on an idle immersive screen - where the cursor itself is hidden - the
        /// near-white note pin stayed at full brightness in every axis column with
        /// nothing around it to explain what it was.
        /// </summary>
        public bool DrawHover(Graphics g, HoverInfo h, Settings s, Font font, Font pinFont,
                              double alpha)
        {
            if (_map == null || _panes.Length == 0 || h == null || !h.Active) return false;
            if (alpha <= 0.004) return false;
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
            using (var pen = new Pen(FadeColor(Color.FromArgb(120, 255, 255, 255), alpha)))
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
            using (var dot = new SolidBrush(FadeColor(Color.FromArgb(220, 255, 255, 255), alpha)))
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
            if (by < top + 2) by = mouse.Y + 12;
            using (var back = new SolidBrush(FadeColor(Color.FromArgb(220, 10, 10, 12), alpha)))
                g.FillRectangle(back, bx - 5, by - 3, ts.Width + 10, ts.Height + 6);
            using (var border = new Pen(FadeColor(Color.FromArgb(80, 255, 255, 255), alpha)))
                g.DrawRectangle(border, bx - 5, by - 3, ts.Width + 10, ts.Height + 6);
            using (var brush = new SolidBrush(FadeColor(Color.FromArgb(245, 240, 240, 245), alpha)))
                g.DrawString(text, font, brush, bx, by);

            // Stamp the frequency onto the axis itself so the eye can stay on the image.
            if (s.ShowHoverPin && pinFont != null)
            {
                string pin = note;
                SizeF ps = g.MeasureString(pin, pinFont);
                // Centred on the hovered row, but never past the ends of the axis: at
                // the topmost row the pin was drawn half into the scale strip, where it
                // sat over the time and level labels as an unexplained pale block.
                float py = mouse.Y - ps.Height / 2;
                if (py < top) py = top;
                if (py + ps.Height > top + hgt) py = top + hgt - ps.Height;
                using (var back = new SolidBrush(FadeColor(Color.FromArgb(235, 245, 245, 250), alpha)))
                using (var fg = new SolidBrush(FadeColor(Color.FromArgb(255, 12, 12, 16), alpha)))
                {
                    if (GutterRect.Width >= 20)
                    {
                        float gx = GutterRect.Left + (GutterRect.Width - ps.Width) / 2;
                        g.FillRectangle(back, gx - 2, py, ps.Width + 4, ps.Height);
                        g.DrawString(pin, pinFont, fg, gx, py);
                    }
                    if (OuterLeftRect.Width > 0)
                    {
                        float lx = OuterLeftRect.Left + (OuterLeftRect.Width - ps.Width) / 2;
                        g.FillRectangle(back, lx - 2, py, ps.Width + 4, ps.Height);
                        g.DrawString(pin, pinFont, fg, lx, py);
                        float rx = OuterRightRect.Left + (OuterRightRect.Width - ps.Width) / 2;
                        g.FillRectangle(back, rx - 2, py, ps.Width + 4, ps.Height);
                        g.DrawString(pin, pinFont, fg, rx, py);
                    }
                }
            }
            // Harmonic ruler: a line is either a fundamental or somebody's overtone, and
            // this is the quickest way to tell which.
            if (s.ShowHarmonics)
            {
                using (var hp = new Pen(FadeColor(Color.FromArgb(70, 160, 210, 255), alpha)))
                using (var hb = new SolidBrush(FadeColor(Color.FromArgb(150, 170, 215, 255), alpha)))
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
                    using (var mp = new Pen(FadeColor(Color.FromArgb(150, 255, 220, 120), alpha)))
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
                    if (my2 < top + 2) my2 = Math.Max(h.Origin.Y, mouse.Y) + 8;
                    using (var back = new SolidBrush(FadeColor(Color.FromArgb(225, 24, 20, 8), alpha)))
                        g.FillRectangle(back, mx2 - 5, my2 - 3, ms.Width + 10, ms.Height + 6);
                    using (var border = new Pen(FadeColor(Color.FromArgb(140, 255, 220, 120), alpha)))
                        g.DrawRectangle(border, mx2 - 5, my2 - 3, ms.Width + 10, ms.Height + 6);
                    using (var brush = new SolidBrush(FadeColor(Color.FromArgb(245, 255, 232, 170), alpha)))
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

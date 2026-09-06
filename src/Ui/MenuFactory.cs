using System;
using System.Windows.Forms;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Render;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// Builds the right-click menu for both views.
    ///
    /// Organised so the top level reads as a sequence rather than a pile: what is being
    /// analysed, how it is analysed, how it is drawn, then the view itself and the
    /// actions. Separators mark those four bands. Groups are kept to a similar size -
    /// anything with fewer than three entries is folded into its neighbour rather than
    /// costing a submenu of its own.
    ///
    /// One builder for both views, so the docked panel and the fullscreen view cannot
    /// drift apart as options are added.
    /// </summary>
    public static class MenuFactory
    {
        public sealed class Options
        {
            public bool IsFullscreen;
            public int ScrollPixels;
            public Func<bool> IsFrozen;
            public Action ToggleFreeze;
            public Action ToggleFullscreen;
            public Action ToggleImmersive;
            /// <summary>Docked only: resize the host panel now, rather than next launch.</summary>
            public Action<int> SetDockHeight;
            /// <summary>Called after any change; the flag asks for a geometry rebuild.</summary>
            public Action<bool> Changed;
        }

        public static void Populate(ContextMenuStrip menu, Settings s, Options o)
        {
            menu.Items.Clear();

            // What is being shown
            menu.Items.Add(Presets(s, o));
            menu.Items.Add(Channels(s, o));

            menu.Items.Add(new ToolStripSeparator());

            // How it is measured
            menu.Items.Add(Analysis(s, o));
            menu.Items.Add(Levels(s, o));

            menu.Items.Add(new ToolStripSeparator());

            // How it is drawn
            menu.Items.Add(Graph(s, o));
            menu.Items.Add(Axes(s, o));
            menu.Items.Add(Hover(s, o));
            menu.Items.Add(Colour(s, o));

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(View(s, o));

            menu.Items.Add(new ToolStripSeparator());

            var freeze = new ToolStripMenuItem("Freeze  (Space or click)");
            freeze.Checked = o.IsFrozen != null && o.IsFrozen();
            freeze.Click += delegate { o.ToggleFreeze(); };
            menu.Items.Add(freeze);

            if (o.ToggleFullscreen != null)
            {
                var fs = new ToolStripMenuItem(o.IsFullscreen
                    ? "Exit fullscreen  (Esc)" : "Fullscreen stereo view  (F11)");
                fs.Click += delegate { o.ToggleFullscreen(); };
                menu.Items.Add(fs);
            }
        }

        // ---------------- what is being shown ----------------

        private static ToolStripMenuItem Presets(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("Preset");
            foreach (Preset p in Enum.GetValues(typeof(Preset)))
            {
                if (p == Preset.Custom) continue;
                Preset captured = p;
                var mi = new ToolStripMenuItem(p.ToString());
                mi.Checked = s.Preset == p;
                mi.Click += delegate { s.ApplyPreset(captured); o.Changed(true); };
                m.DropDownItems.Add(mi);
            }
            return m;
        }

        private static ToolStripMenuItem Channels(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("Channels");
            string[] labels = { "Left / Right", "Mid / Side", "Left only", "Right only" };
            var vals = (ChannelPairMode[])Enum.GetValues(typeof(ChannelPairMode));
            for (int i = 0; i < vals.Length; i++)
            {
                ChannelPairMode captured = vals[i];
                var mi = new ToolStripMenuItem(labels[i]);
                mi.Checked = s.PairMode == captured;
                mi.Click += delegate { s.PairMode = captured; o.Changed(true); };
                m.DropDownItems.Add(mi);
            }
            return m;
        }

        // ---------------- how it is measured ----------------

        private static ToolStripMenuItem Analysis(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("Analysis");

            var scale = new ToolStripMenuItem("Frequency scale");
            foreach (FreqScale f in Enum.GetValues(typeof(FreqScale)))
            {
                FreqScale captured = f;
                var mi = new ToolStripMenuItem(f == FreqScale.Note ? "Note (musical)" : f.ToString());
                mi.Checked = s.Scale == f;
                mi.Click += delegate
                {
                    s.Scale = captured;
                    if (captured == FreqScale.Linear) { s.FMin = 0; s.FMax = 22050; }
                    else { s.FMin = 25; s.FMax = 18000; }
                    s.Preset = Preset.Custom;
                    o.Changed(true);
                };
                scale.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(scale);

            var qual = new ToolStripMenuItem("Resolution");
            foreach (AnalysisQuality q in Enum.GetValues(typeof(AnalysisQuality)))
            {
                AnalysisQuality captured = q;
                string label = q == AnalysisQuality.LowLatency ? "Low latency (4K/1K/256)"
                             : q == AnalysisQuality.Fast ? "Fast (4K)"
                             : q == AnalysisQuality.Balanced ? "Balanced (16K/4K/1K)"
                             : "High (32K/8K/2K/512)";
                var mi = new ToolStripMenuItem(label);
                mi.Checked = s.Quality == q;
                mi.Click += delegate { s.Quality = captured; s.Preset = Preset.Custom; o.Changed(false); };
                qual.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(qual);

            var tilt = new ToolStripMenuItem("Spectral tilt");
            double[] tilts = { 0, 1.5, 3.0, 4.5, 6.0 };
            foreach (double tv in tilts)
            {
                double captured = tv;
                var mi = new ToolStripMenuItem(tv == 0 ? "None (flat)" : "+" + tv.ToString("0.0") + " dB/oct");
                mi.Checked = Math.Abs(s.TiltDbPerOctave - tv) < 0.01;
                mi.Click += delegate { s.TiltDbPerOctave = captured; s.Preset = Preset.Custom; o.Changed(false); };
                tilt.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(tilt);

            m.DropDownItems.Add(new ToolStripSeparator());

            var interp = new ToolStripMenuItem("Interpolation");
            foreach (CurveInterpolation ci in Enum.GetValues(typeof(CurveInterpolation)))
            {
                CurveInterpolation captured = ci;
                var mi = new ToolStripMenuItem(ci.ToString());
                mi.Checked = s.Interp == ci;
                mi.Click += delegate { s.Interp = captured; o.Changed(false); };
                interp.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(interp);

            var filt = new ToolStripMenuItem("Smoothing");
            foreach (FilteringAmount fa in Enum.GetValues(typeof(FilteringAmount)))
            {
                FilteringAmount captured = fa;
                var mi = new ToolStripMenuItem(fa.ToString());
                mi.Checked = s.Filter == fa;
                mi.Click += delegate { s.Filter = captured; o.Changed(false); };
                filt.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(filt);

            var agg = new ToolStripMenuItem("Band aggregation");
            foreach (BandAggregate ba in Enum.GetValues(typeof(BandAggregate)))
            {
                BandAggregate captured = ba;
                var mi = new ToolStripMenuItem(ba.ToString());
                mi.Checked = s.Aggregate == ba;
                mi.Click += delegate { s.Aggregate = captured; o.Changed(false); };
                agg.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(agg);

            return m;
        }

        private static ToolStripMenuItem Levels(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("Levels and time");

            AddToggle(m.DropDownItems, "Auto dynamic range", s.AdaptiveRange,
                      delegate { s.AdaptiveRange = !s.AdaptiveRange; o.Changed(false); });

            var contrast = new ToolStripMenuItem("Contrast");
            double[] cs = { 0.15, 0.25, 0.40, 0.55, 0.70, 0.82 };
            string[] names = { "Flattest", "Low", "Medium", "High", "Very high", "Extreme" };
            for (int i = 0; i < cs.Length; i++)
            {
                double captured = cs[i];
                var mi = new ToolStripMenuItem(names[i] + "   (" + (cs[i] * 100).ToString("0") + "% floor)");
                mi.Checked = Math.Abs(s.Contrast - cs[i]) < 0.01;
                mi.Click += delegate { s.Contrast = captured; o.Changed(false); };
                contrast.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(contrast);

            var speed = new ToolStripMenuItem("Scroll speed");
            int[] divs = { 1, 2, 4, 8 };
            foreach (int d in divs)
            {
                int captured = d;
                double secs = o.ScrollPixels * d / (double)Math.Max(1, s.TargetFps);
                var mi = new ToolStripMenuItem(
                    (d == 1 ? "Fast" : d == 2 ? "Medium" : d == 4 ? "Slow" : "Very slow")
                    + string.Format("   (~{0:0}s visible)", secs));
                mi.Checked = s.ScrollDivider == d;
                mi.Click += delegate { s.ScrollDivider = captured; o.Changed(false); };
                speed.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(speed);

            return m;
        }

        // ---------------- how it is drawn ----------------

        private static ToolStripMenuItem Graph(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("Graph");

            var style = new ToolStripMenuItem("Style");
            foreach (CurveStyle cs in Enum.GetValues(typeof(CurveStyle)))
            {
                CurveStyle captured = cs;
                var mi = new ToolStripMenuItem(cs == CurveStyle.Led ? "LED" : cs.ToString());
                mi.Checked = s.Style == cs;
                mi.Click += delegate { s.Style = captured; o.Changed(false); };
                style.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(style);

            var size = new ToolStripMenuItem("Size");
            int[] pcts = { 0, 8, 12, 18, 25, 33, 45 };
            foreach (int pv in pcts)
            {
                int captured = pv;
                string label = pv == 0
                    ? "No graph   (spectrogram only)"
                    : "Graph " + pv + "%   /   spectrogram " + (100 - pv) + "%";
                var mi = new ToolStripMenuItem(label);
                mi.Checked = s.CurveWidthPct == pv;
                mi.Click += delegate { s.CurveWidthPct = captured; o.Changed(true); };
                size.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(size);

            var bg = new ToolStripMenuItem("Background");
            foreach (GraphBackground b in Enum.GetValues(typeof(GraphBackground)))
            {
                GraphBackground captured = b;
                var mi = new ToolStripMenuItem(b == GraphBackground.Lines ? "Horizontal dB lines" : b.ToString());
                mi.Checked = s.Background == b;
                mi.Click += delegate { s.Background = captured; o.Changed(false); };
                bg.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(bg);

            var bar = new ToolStripMenuItem("Bar and LED size");
            int[] sizes = { 3, 6, 10, 16 };
            foreach (int bv in sizes)
            {
                int captured = bv;
                var mi = new ToolStripMenuItem("Bar " + bv + " px");
                mi.Checked = s.BarSize == bv;
                mi.Click += delegate { s.BarSize = captured; o.Changed(false); };
                bar.DropDownItems.Add(mi);
            }
            bar.DropDownItems.Add(new ToolStripSeparator());
            int[] segs = { 3, 5, 8, 12 };
            foreach (int lv in segs)
            {
                int captured = lv;
                var mi = new ToolStripMenuItem("LED segment " + lv + " px");
                mi.Checked = s.LedSegment == lv;
                mi.Click += delegate { s.LedSegment = captured; o.Changed(false); };
                bar.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(bar);

            m.DropDownItems.Add(new ToolStripSeparator());

            AddToggle(m.DropDownItems, "Maximum trace (peak hold)", s.ShowMax,
                      delegate { s.ShowMax = !s.ShowMax; o.Changed(false); });
            AddToggle(m.DropDownItems, "Average trace", s.ShowAvg,
                      delegate { s.ShowAvg = !s.ShowAvg; o.Changed(false); });
            AddToggle(m.DropDownItems, "Minimum trace", s.ShowMin,
                      delegate { s.ShowMin = !s.ShowMin; o.Changed(false); });
            AddToggle(m.DropDownItems, "Solid fill", s.SolidFill,
                      delegate { s.SolidFill = !s.SolidFill; o.Changed(false); });

            m.DropDownItems.Add(new ToolStripSeparator());

            AddToggle(m.DropDownItems, "Graph on the left of each pane", s.CurveOnLeft,
                      delegate { s.CurveOnLeft = !s.CurveOnLeft; o.Changed(true); });
            AddToggle(m.DropDownItems, "Mirror left pane  (sound from the middle)  (M)",
                      s.MirrorLeftPane,
                      delegate { s.MirrorLeftPane = !s.MirrorLeftPane; o.Changed(true); });

            return m;
        }

        private static ToolStripMenuItem Axes(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("Axes and grid");

            bool grid = o.IsFullscreen ? s.FsShowGrid : s.ShowGrid;
            AddToggle(m.DropDownItems, "Gridlines  (G)", grid, delegate
            {
                if (o.IsFullscreen) s.FsShowGrid = !s.FsShowGrid; else s.ShowGrid = !s.ShowGrid;
                o.Changed(false);
            });
            AddToggle(m.DropDownItems, "Semitone gridlines", s.ShowSemitones,
                      delegate { s.ShowSemitones = !s.ShowSemitones; o.Changed(false); });

            m.DropDownItems.Add(new ToolStripSeparator());

            AddToggle(m.DropDownItems, "dB scale on graphs", s.ShowDbScale,
                      delegate { s.ShowDbScale = !s.ShowDbScale; o.Changed(false); });
            AddToggle(m.DropDownItems, "Time markers on spectrograms", s.ShowTimeMarks,
                      delegate { s.ShowTimeMarks = !s.ShowTimeMarks; o.Changed(false); });
            AddToggle(m.DropDownItems, "Note labels at outer edges", s.ShowOuterLabels,
                      delegate { s.ShowOuterLabels = !s.ShowOuterLabels; o.Changed(true); });
            AddToggle(m.DropDownItems, "Channel labels", s.ShowLabels,
                      delegate { s.ShowLabels = !s.ShowLabels; o.Changed(false); });

            return m;
        }

        private static ToolStripMenuItem Hover(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("Hover");
            AddToggle(m.DropDownItems, "Readout", s.ShowHud,
                      delegate { s.ShowHud = !s.ShowHud; o.Changed(false); });
            AddToggle(m.DropDownItems, "Sync across both panes", s.SyncHover,
                      delegate { s.SyncHover = !s.SyncHover; o.Changed(false); });
            AddToggle(m.DropDownItems, "Pin note on the axis", s.ShowHoverPin,
                      delegate { s.ShowHoverPin = !s.ShowHoverPin; o.Changed(false); });
            AddToggle(m.DropDownItems, "Harmonic ruler", s.ShowHarmonics,
                      delegate { s.ShowHarmonics = !s.ShowHarmonics; o.Changed(false); });

            m.DropDownItems.Add(new ToolStripSeparator());
            var note = new ToolStripMenuItem("Click freezes  ·  drag measures");
            note.Enabled = false;
            m.DropDownItems.Add(note);
            return m;
        }

        private static ToolStripMenuItem Colour(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("Colour");
            foreach (PaletteKind k in Enum.GetValues(typeof(PaletteKind)))
            {
                PaletteKind captured = k;
                var mi = new ToolStripMenuItem(Palette.DisplayName(k));
                mi.Checked = s.Palette == k;
                mi.Click += delegate { s.Palette = captured; s.Preset = Preset.Custom; o.Changed(false); };
                m.DropDownItems.Add(mi);
            }
            return m;
        }

        // ---------------- the view itself ----------------

        private static ToolStripMenuItem View(Settings s, Options o)
        {
            var m = new ToolStripMenuItem("View");
            if (o.IsFullscreen)
            {
                AddToggle(m.DropDownItems, "On-screen display  (H)", s.FsShowOsd,
                          delegate { s.FsShowOsd = !s.FsShowOsd; o.Changed(false); });

                if (o.ToggleImmersive != null)
                {
                    var imm = new ToolStripMenuItem("Immersive mode  (I)");
                    imm.Checked = s.FsImmersive;
                    imm.Click += delegate { o.ToggleImmersive(); };
                    m.DropDownItems.Add(imm);
                }
                AddToggle(m.DropDownItems, "Glow", s.FsGlow,
                          delegate { s.FsGlow = !s.FsGlow; o.Changed(false); });
                AddToggle(m.DropDownItems, "Hide labels when idle", s.FsAutoHide,
                          delegate { s.FsAutoHide = !s.FsAutoHide; o.Changed(false); });

                m.DropDownItems.Add(new ToolStripSeparator());

                AddToggle(m.DropDownItems, "Meters and track info  (O)", s.FsShowOverlays,
                          delegate { s.FsShowOverlays = !s.FsShowOverlays; o.Changed(false); });
                AddToggle(m.DropDownItems, "Waveform lanes  (W)", s.FsShowWaveform,
                          delegate { s.FsShowWaveform = !s.FsShowWaveform; o.Changed(true); });

                var wave = new ToolStripMenuItem("Waveform height");
                int[] wpcts = { 5, 10, 15, 22, 30 };
                foreach (int wv in wpcts)
                {
                    int captured = wv;
                    var mi = new ToolStripMenuItem(wv + "%");
                    mi.Checked = s.WaveHeightPct == wv;
                    mi.Click += delegate { s.WaveHeightPct = captured; o.Changed(true); };
                    wave.DropDownItems.Add(mi);
                }
                m.DropDownItems.Add(wave);
            }
            else
            {
                AddToggle(m.DropDownItems, "Colour bar", s.ShowColorBar,
                          delegate { s.ShowColorBar = !s.ShowColorBar; o.Changed(true); });
                AddToggle(m.DropDownItems, "Status line", s.ShowStatus,
                          delegate { s.ShowStatus = !s.ShowStatus; o.Changed(false); });

                m.DropDownItems.Add(new ToolStripSeparator());

                int screenH = Screen.PrimaryScreen.WorkingArea.Height;
                var height = new ToolStripMenuItem("Panel height");
                int[] hpcts = { 25, 33, 50, 66, 75 };
                foreach (int pct in hpcts)
                {
                    int px = screenH * pct / 100;
                    int captured = px;
                    var mi = new ToolStripMenuItem(pct + "%   (" + px + " px)");
                    mi.Checked = Math.Abs(s.DockPanelHeight - px) <= 2;
                    mi.Click += delegate
                    {
                        s.DockPanelHeight = captured;
                        if (o.SetDockHeight != null) o.SetDockHeight(captured);
                        o.Changed(false);
                    };
                    height.DropDownItems.Add(mi);
                }
                m.DropDownItems.Add(height);
            }
            return m;
        }

        private static void AddToggle(ToolStripItemCollection items, string text, bool state, EventHandler onClick)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Checked = state;
            mi.Click += onClick;
            items.Add(mi);
        }
    }
}

using System;
using System.Drawing;
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
    /// Every item carries a tooltip saying what it does and, where it matters, what it
    /// costs. The names alone cannot carry that: "Band aggregation" or "+3 dB/oct" mean
    /// nothing until someone says what they change about the picture.
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
            /// <summary>Where user presets live; null disables them.</summary>
            public string StorageDir;
            /// <summary>Owner for the name prompt.</summary>
            public IWin32Window Owner;
            /// <summary>Host skin colours, for building a theme that matches MusicBee.</summary>
            public Func<int, int, int, int> SkinColour;
            /// <summary>Called after any change; the flag asks for a geometry rebuild.</summary>
            public Action<bool> Changed;
        }

        public static void Populate(ContextMenuStrip menu, Settings s, Options o)
        {
            menu.Items.Clear();
            menu.ShowItemToolTips = true;

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
            freeze.ToolTipText = "Stop the spectrograms scrolling so a moment can be read at leisure.\n"
                                 + "Analysis keeps running; only the picture is held.";
            freeze.Checked = o.IsFrozen != null && o.IsFrozen();
            freeze.Click += delegate { o.ToggleFreeze(); };
            menu.Items.Add(freeze);

            if (o.ToggleFullscreen != null)
            {
                var fs = new ToolStripMenuItem(o.IsFullscreen
                    ? "Exit fullscreen  (Esc)" : "Fullscreen stereo view  (F11)");
                fs.ToolTipText = o.IsFullscreen
                    ? "Return to the docked panel."
                    : "Open the same view full screen, with meters, waveform lanes and track info.";
                fs.Click += delegate { o.ToggleFullscreen(); };
                menu.Items.Add(fs);
            }

            // A submenu does not inherit the context menu's tooltip setting, so every
            // dropdown has to be told separately or its items stay silent.
            EnableTips(menu.Items);
        }

        private static void EnableTips(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                var mi = it as ToolStripMenuItem;
                if (mi == null || !mi.HasDropDownItems) continue;
                mi.DropDown.ShowItemToolTips = true;
                EnableTips(mi.DropDownItems);
            }
        }

        // ---------------- small builders ----------------

        private static ToolStripMenuItem Sub(string text, string tip)
        {
            var m = new ToolStripMenuItem(text);
            m.ToolTipText = tip;
            return m;
        }

        private static void Choice(ToolStripMenuItem parent, string text, string tip,
                                   bool ticked, EventHandler onClick)
        {
            var mi = new ToolStripMenuItem(text);
            mi.ToolTipText = tip;
            mi.Checked = ticked;
            mi.Click += onClick;
            parent.DropDownItems.Add(mi);
        }

        private static void AddToggle(ToolStripItemCollection items, string text, string tip,
                                      bool state, EventHandler onClick)
        {
            var mi = new ToolStripMenuItem(text);
            mi.ToolTipText = tip;
            mi.Checked = state;
            mi.Click += onClick;
            items.Add(mi);
        }

        // ---------------- what is being shown ----------------

        /// <summary>Indexed by <see cref="Preset"/> value, which is why the values are pinned.</summary>
        private static readonly string[] PresetTips =
        {
            "The original plugin's look, without its mapping mistakes:\nred palette, linear axis, no tilt.",
            "General purpose. Musical axis, gentle tilt, auto range.\nA good place to start and adjust from.",
            "Quality control. Flat, wide and linear, so a lossy codec's\nshelf at 16 kHz is obvious rather than hidden by tilt.",
            "For watching rather than measuring: glow, slow scroll,\nand furniture that fades while you are not touching anything.",
            "Your own adjustments. Set automatically whenever you change\nsomething a preset defines.",
            "Voices and their formants: 150 Hz to 9 kHz, mild tilt,\nand enough resolution to separate the harmonics.",
            "Low end only, at the largest transform sizes - a semitone\nat E1 is 2.4 Hz wide and needs them to resolve at all.",
            "Transients. Low latency and fast scroll, because drums are\nabout timing rather than frequency detail.",
            "Measurement: no tilt, no auto range, energy summed, linear\nto Nyquist, so noise floors read at their true level."
        };

        private static ToolStripMenuItem Presets(Settings s, Options o)
        {
            var m = Sub("Preset", "Complete configurations. Picking one replaces the analysis,\n"
                                  + "range, palette and axis settings together.");
            // Explicit order: general purpose first, then focused, then measurement.
            // Immersive is reached through View, which applies this same preset; listing
            // it here as well made it look like two different things.
            Preset[] order = {
                Preset.Studio, Preset.Nostalgia,
                Preset.Vocal, Preset.Bass, Preset.Percussion,
                Preset.QC, Preset.Mastering
            };
            foreach (Preset p in order)
            {
                Preset captured = p;
                Choice(m, p.ToString(), PresetTips[(int)p], s.Preset == p,
                       delegate { s.ApplyPreset(captured); o.Changed(true); });
            }

            if (o.StorageDir == null) return m;

            // Any adjustment turns the preset into Custom, so without somewhere to put it
            // a configuration you actually liked is unrecoverable.
            string[] saved = Settings.ListUserPresets(o.StorageDir);
            if (saved.Length > 0)
            {
                m.DropDownItems.Add(new ToolStripSeparator());
                foreach (string name in saved)
                {
                    string captured = name;
                    Choice(m, name, "Your saved preset. Loads every setting it was saved with.",
                           false, delegate
                           {
                               if (s.LoadUserPreset(o.StorageDir, captured)) o.Changed(true);
                           });
                }
            }

            m.DropDownItems.Add(new ToolStripSeparator());

            var save = new ToolStripMenuItem("Save current as...");
            save.ToolTipText = "Store every current setting under a name of your own, so an\n"
                               + "arrangement you like can be returned to.";
            save.Click += delegate
            {
                string name = NameDialog.Ask(o.Owner, "Save preset",
                                             "Name for these settings:", "My preset");
                if (name == null) return;
                if (s.SaveUserPreset(o.StorageDir, name)) o.Changed(false);
            };
            m.DropDownItems.Add(save);

            if (saved.Length > 0)
            {
                var del = Sub("Delete saved preset", "Remove one of your saved presets. Asks first.");
                foreach (string name in saved)
                {
                    string captured = name;
                    Choice(del, name, "Delete this preset permanently.", false, delegate
                    {
                        if (MessageBox.Show("Delete preset \"" + captured + "\"?", "Nostalgia+",
                                            MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                            == DialogResult.Yes && s.DeleteUserPreset(o.StorageDir, captured))
                            o.Changed(false);
                    });
                }
                m.DropDownItems.Add(del);
            }

            return m;
        }

        private static ToolStripMenuItem Channels(Settings s, Options o)
        {
            var m = Sub("Channels", "What the two panes show. Two panes become one in the\n"
                                    + "single-channel modes, which then get the full width.");
            string[] labels = { "Left / Right", "Mid / Side", "Left only", "Right only" };
            string[] tips =
            {
                "One pane per speaker. The usual view.",
                "Mid is L+R, Side is L-R: what is common to both channels against\n"
                + "what is only in the stereo image. Side showing bass usually means\n"
                + "a mono-compatibility problem.",
                "The left channel alone, across the full width.",
                "The right channel alone, across the full width."
            };
            var vals = (ChannelPairMode[])Enum.GetValues(typeof(ChannelPairMode));
            for (int i = 0; i < vals.Length; i++)
            {
                ChannelPairMode captured = vals[i];
                Choice(m, labels[i], i < tips.Length ? tips[i] : null, s.PairMode == captured,
                       delegate { s.PairMode = captured; o.Changed(true); });
            }
            return m;
        }

        // ---------------- how it is measured ----------------

        private static ToolStripMenuItem Analysis(Settings s, Options o)
        {
            var m = Sub("Analysis", "How the audio is turned into a picture: the frequency axis,\n"
                                    + "the transform sizes, and the shaping applied afterwards.");

            var scale = Sub("Frequency scale", "How frequency is spread along the axis.");
            Choice(scale, "Note (musical)",
                   "Equal space per octave, so every octave is the same height and\n"
                   + "notes line up with gridlines. Almost always the readable choice.",
                   s.Scale == FreqScale.Note,
                   delegate { SetScale(s, o, FreqScale.Note); });
            Choice(scale, "Linear",
                   "Equal space per hertz. Everything below 1 kHz is squeezed into the\n"
                   + "bottom few percent of the axis - but a codec's shelf at the top is\n"
                   + "far easier to see, which is why the QC presets use it.",
                   s.Scale == FreqScale.Linear,
                   delegate { SetScale(s, o, FreqScale.Linear); });
            m.DropDownItems.Add(scale);

            var range = Sub("Frequency range", "Which part of the spectrum fills the axis. Narrowing it\n"
                                               + "spends every pixel on what you actually care about.");
            AddRange(range, s, o, "Full   (20 Hz - 22 kHz)", 20, 22050,
                     "Everything the file can carry.");
            AddRange(range, s, o, "Music   (25 Hz - 18 kHz)", 25, 18000,
                     "Drops the subsonic and the inaudible top, which are mostly\nnoise floor, and gives the rest more room.");
            AddRange(range, s, o, "Bass   (20 - 800 Hz)", 20, 800,
                     "Kick, bass and the low mids, at full height.");
            AddRange(range, s, o, "Mids   (200 Hz - 5 kHz)", 200, 5000,
                     "Where most instruments and voices actually sit.");
            AddRange(range, s, o, "Presence   (1 - 16 kHz)", 1000, 16000,
                     "Consonants, cymbals and air.");
            AddRange(range, s, o, "Top   (5 - 22 kHz)", 5000, 22050,
                     "The codec range. A hard shelf here means a lossy source.");
            m.DropDownItems.Add(range);

            var qual = Sub("Resolution", "Transform sizes. Larger separates close frequencies but smears\n"
                                         + "transients and adds delay; smaller does the reverse. Several\n"
                                         + "sizes run at once and are stitched together across the axis.");
            AnalysisQuality[] order = {
                AnalysisQuality.LowLatency, AnalysisQuality.Fast,
                AnalysisQuality.Balanced, AnalysisQuality.High
            };
            string[] qLabels = {
                "Low latency (4K/1K/256)", "Fast (4K)",
                "Balanced (16K/4K/1K)", "High (32K/8K/2K/512)"
            };
            string[] qTips = {
                "Follows the music tightly. Each band's window ends at now, so its\n"
                + "centre lies half a window in the past - a 32K bass window trails\n"
                + "the treble by about a third of a second. This keeps them together.",
                "One small transform. Cheapest, and 11.7 Hz bins - too coarse to\n"
                + "resolve a semitone anywhere below about 400 Hz.",
                "The usual compromise: bass resolves, transients survive.",
                "Finest frequency detail. Harmonics resolve into separate lines\n"
                + "instead of a smear, at the cost of latency and CPU."
            };
            for (int i = 0; i < order.Length; i++)
            {
                AnalysisQuality captured = order[i];
                Choice(qual, qLabels[i], qTips[i], s.Quality == captured,
                       delegate { s.Quality = captured; s.Preset = Preset.Custom; o.Changed(false); });
            }
            m.DropDownItems.Add(qual);

            var tilt = Sub("Spectral tilt", "Lifts the top of the spectrum before it is drawn. Music falls\n"
                                            + "off roughly 3 dB per octave, so a tilt of about that much makes\n"
                                            + "the whole range use the palette instead of leaving the treble\n"
                                            + "as haze. It changes the picture, never the audio.");
            double[] tilts = { 0, 1.5, 3.0, 4.5, 6.0 };
            string[] tiltTips = {
                "True levels. What a measurement needs, and what makes most music\nlook bottom-heavy.",
                "A light lift.",
                "Matches the average slope of music. The best default for viewing.",
                "Pushes the treble forward; useful for cymbals and air.",
                "Strong. The top dominates - good for finding faint high detail."
            };
            for (int i = 0; i < tilts.Length; i++)
            {
                double captured = tilts[i];
                Choice(tilt, tilts[i] == 0 ? "None (flat)" : "+" + tilts[i].ToString("0.0") + " dB/oct",
                       tiltTips[i], Math.Abs(s.TiltDbPerOctave - tilts[i]) < 0.01,
                       delegate { s.TiltDbPerOctave = captured; s.Preset = Preset.Custom; o.Changed(false); });
            }
            m.DropDownItems.Add(tilt);

            m.DropDownItems.Add(new ToolStripSeparator());

            var win = Sub("Window function", "The shape each block of samples is faded in and out with before\n"
                                             + "the transform. Trades how much a strong tone leaks into its\n"
                                             + "neighbouring bins against how sharp that tone looks.");
            string[] winTips = {
                "The sensible default: narrow peaks, leakage low enough for music.",
                "Slightly narrower main lobe than Hann, slightly worse far leakage.",
                "Lowest leakage here. Use it to see something quiet sitting beside\nsomething loud; peaks look wider in exchange.",
                "Very low leakage, close to Blackman-Harris.",
                "A smooth trade-off between the two extremes.",
                "No window at all. Maximum sharpness, severe leakage - only useful\nfor tones that fit the block exactly, such as test signals."
            };
            var wins = (WindowType[])Enum.GetValues(typeof(WindowType));
            for (int i = 0; i < wins.Length; i++)
            {
                WindowType captured = wins[i];
                Choice(win, wins[i] == WindowType.BlackmanHarris ? "Blackman-Harris" : wins[i].ToString(),
                       i < winTips.Length ? winTips[i] : null, s.Window == captured,
                       delegate { s.Window = captured; s.Preset = Preset.Custom; o.Changed(false); });
            }
            m.DropDownItems.Add(win);

            var interp = Sub("Interpolation", "How the curve is drawn between the analysed points.");
            string[] interpTips = {
                "Straight segments between points. Honest, and slightly jagged.",
                "Rounded corners; easier to watch, marginally less exact.",
                "Heavier rounding, for a calm picture."
            };
            var interps = (CurveInterpolation[])Enum.GetValues(typeof(CurveInterpolation));
            for (int i = 0; i < interps.Length; i++)
            {
                CurveInterpolation captured = interps[i];
                Choice(interp, interps[i].ToString(), i < interpTips.Length ? interpTips[i] : null,
                       s.Interp == captured, delegate { s.Interp = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(interp);

            var filt = Sub("Smoothing", "Averages neighbouring frequency bins together. Quietens the\n"
                                        + "noise between partials; too much and close harmonics merge\n"
                                        + "into a single hump.");
            string[] filtTips = {
                "None. Every bin as measured.",
                "A touch, to settle the noise floor.",
                "Noticeably calmer; fine detail starts to go.",
                "Broad strokes only - the shape of the spectrum, not its lines."
            };
            var filts = (FilteringAmount[])Enum.GetValues(typeof(FilteringAmount));
            for (int i = 0; i < filts.Length; i++)
            {
                FilteringAmount captured = filts[i];
                Choice(filt, filts[i].ToString(), i < filtTips.Length ? filtTips[i] : null,
                       s.Filter == captured, delegate { s.Filter = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(filt);

            var agg = Sub("Band aggregation", "Several transform bins land on each pixel row. This decides\n"
                                              + "which number that row gets.");
            string[] aggTips = {
                "The loudest bin. Keeps narrow tones visible at any zoom level -\nthe right choice for spotting notes.",
                "The bins summed as energy. What a measurement wants: a wide noise\nfloor reads at its true total rather than at one bin of it.",
                "The plain average. Between the two."
            };
            var aggs = (BandAggregate[])Enum.GetValues(typeof(BandAggregate));
            for (int i = 0; i < aggs.Length; i++)
            {
                BandAggregate captured = aggs[i];
                Choice(agg, aggs[i].ToString(), i < aggTips.Length ? aggTips[i] : null,
                       s.Aggregate == captured, delegate { s.Aggregate = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(agg);

            return m;
        }

        private static void SetScale(Settings s, Options o, FreqScale scale)
        {
            s.Scale = scale;
            // The useful range differs completely between the two mappings, so switching
            // without moving it leaves a near-empty axis.
            if (scale == FreqScale.Linear) { s.FMin = 0; s.FMax = 22050; }
            else { s.FMin = 25; s.FMax = 18000; }
            s.Preset = Preset.Custom;
            o.Changed(true);
        }

        private static void AddRange(ToolStripMenuItem parent, Settings s, Options o,
                                     string label, double lo, double hi, string tip)
        {
            Choice(parent, label, tip,
                   Math.Abs(s.FMin - lo) < 1 && Math.Abs(s.FMax - hi) < 1,
                   delegate { s.FMin = lo; s.FMax = hi; s.Preset = Preset.Custom; o.Changed(true); });
        }

        private static ToolStripMenuItem Levels(Settings s, Options o)
        {
            var m = Sub("Levels and time", "Which part of the level range the palette spends itself on,\n"
                                           + "and how fast the picture moves.");

            AddToggle(m.DropDownItems, "Auto dynamic range",
                      "Tracks the loud and quiet ends of what is actually playing and\n"
                      + "keeps the palette across them, so quiet passages stay visible.\n"
                      + "Turn it off to compare two tracks' absolute levels.",
                      s.AdaptiveRange,
                      delegate { s.AdaptiveRange = !s.AdaptiveRange; o.Changed(false); });

            var contrast = Sub("Contrast", "Where the black end of the palette sits, as a percentile of the\n"
                                           + "current spectrum. Raising it blackens more of the noise floor -\n"
                                           + "the main lever against dense music looking like haze.");
            double[] cs = { 0.15, 0.25, 0.40, 0.55, 0.70, 0.82 };
            string[] names = { "Flattest", "Low", "Medium", "High", "Very high", "Extreme" };
            string[] cTips = {
                "Almost everything is coloured. Shows the noise floor and the room.",
                "Gentle. A little of the floor goes black.",
                "A clear picture with the haze removed.",
                "Only the structure survives. Good for following melodic lines.",
                "Just the strong partials.",
                "Peaks alone against black. Striking, and throws detail away."
            };
            for (int i = 0; i < cs.Length; i++)
            {
                double captured = cs[i];
                Choice(contrast, names[i] + "   (" + (cs[i] * 100).ToString("0") + "% floor)", cTips[i],
                       Math.Abs(s.Contrast - cs[i]) < 0.01,
                       delegate { s.Contrast = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(contrast);

            var manual = Sub("Fixed range", "A level window that does not move, so the same colour always\n"
                                            + "means the same dB. Choosing one turns auto range off.");
            AddFixed(manual, s, o, "-60 to 0 dB", -60, 0, "Tight. Only the loud part of the music.");
            AddFixed(manual, s, o, "-80 to 0 dB", -80, 0, "A normal viewing window.");
            AddFixed(manual, s, o, "-95 to -5 dB", -95, -5, "Wide, ignoring the very top.");
            AddFixed(manual, s, o, "-110 to 0 dB", -110, 0, "Wide enough to show a 16-bit noise floor.");
            AddFixed(manual, s, o, "-120 to 0 dB", -120, 0, "Everything, down to the dither.");
            m.DropDownItems.Add(manual);

            m.DropDownItems.Add(new ToolStripSeparator());

            var ball = Sub("Response", "How quickly the curve rises to a new level and how slowly it\n"
                                       + "falls back. Fast reads transients; slow is easier to watch.");
            AddBallistics(ball, s, o, "Instant", 0, 80, "Every transient, and a lot of flicker.");
            AddBallistics(ball, s, o, "Fast", 10, 180, "Percussive detail without the jitter.");
            AddBallistics(ball, s, o, "Normal", 20, 320, "The default balance.");
            AddBallistics(ball, s, o, "Smooth", 40, 600, "Calm. Shows the shape rather than the hits.");
            AddBallistics(ball, s, o, "Slow", 80, 1200, "Almost a moving average.");
            m.DropDownItems.Add(ball);

            var speed = Sub("Scroll speed", "How fast the spectrograms move, and therefore how much history\n"
                                            + "fits on screen at once.");
            int[] divs = { 1, 2, 4, 8 };
            string[] sTips = {
                "One column per frame. The most time detail.",
                "Half speed; twice the history.",
                "Slow enough to read a phrase as a shape.",
                "A whole section at once. Fine timing is averaged away."
            };
            for (int i = 0; i < divs.Length; i++)
            {
                int captured = divs[i];
                double secs = o.ScrollPixels * divs[i] / (double)Math.Max(1, s.TargetFps);
                Choice(speed,
                       (divs[i] == 1 ? "Fast" : divs[i] == 2 ? "Medium" : divs[i] == 4 ? "Slow" : "Very slow")
                       + string.Format("   (~{0:0}s visible)", secs),
                       sTips[i], s.ScrollDivider == captured,
                       delegate { s.ScrollDivider = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(speed);

            var fps = Sub("Frame rate", "Analysis and redraw rate. Higher is smoother and costs CPU;\n"
                                        + "it also sets how much time each spectrogram column covers.");
            int[] rates = { 30, 45, 60, 75, 90, 120 };
            foreach (int r in rates)
            {
                int captured = r;
                Choice(fps, r + " fps",
                       r <= 30 ? "Easy on the CPU. Fine at the slower scroll speeds."
                       : r <= 60 ? "The usual choice; matches most displays."
                       : "Smoother on a high refresh rate display, at more CPU.",
                       s.TargetFps == captured,
                       delegate { s.TargetFps = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(fps);

            return m;
        }

        private static void AddFixed(ToolStripMenuItem parent, Settings s, Options o,
                                     string label, double floor, double ceil, string tip)
        {
            Choice(parent, label, tip,
                   !s.AdaptiveRange && Math.Abs(s.FloorDb - floor) < 0.5 && Math.Abs(s.CeilingDb - ceil) < 0.5,
                   delegate
                   {
                       s.FloorDb = floor; s.CeilingDb = ceil;
                       // A fixed range that auto range immediately overrides would do
                       // nothing at all, so choosing one means choosing fixed.
                       s.AdaptiveRange = false;
                       o.Changed(false);
                   });
        }

        private static void AddBallistics(ToolStripMenuItem parent, Settings s, Options o,
                                          string label, double attack, double release, string tip)
        {
            Choice(parent, label + "   (" + attack.ToString("0") + " / " + release.ToString("0") + " ms)",
                   tip,
                   Math.Abs(s.AttackMs - attack) < 0.5 && Math.Abs(s.ReleaseMs - release) < 0.5,
                   delegate { s.AttackMs = attack; s.ReleaseMs = release; o.Changed(false); });
        }

        // ---------------- how it is drawn ----------------

        private static ToolStripMenuItem Graph(Settings s, Options o)
        {
            var m = Sub("Graph", "The instantaneous spectrum strip beside each spectrogram.");

            var style = Sub("Style", "How the live spectrum is drawn.");
            string[] styleTips = {
                "A continuous trace. The most detail.",
                "Blocks of a fixed width, like a classic analyser.",
                "Segmented blocks, as on hardware meters."
            };
            var styles = (CurveStyle[])Enum.GetValues(typeof(CurveStyle));
            for (int i = 0; i < styles.Length; i++)
            {
                CurveStyle captured = styles[i];
                Choice(style, styles[i] == CurveStyle.Led ? "LED" : styles[i].ToString(),
                       i < styleTips.Length ? styleTips[i] : null, s.Style == captured,
                       delegate { s.Style = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(style);

            var size = Sub("Size", "How each pane's width is divided between the graph and its\n"
                                   + "spectrogram. Every pixel one gains, the other loses.");
            int[] pcts = { 0, 8, 12, 18, 25, 33, 45 };
            foreach (int pv in pcts)
            {
                int captured = pv;
                Choice(size,
                       pv == 0 ? "No graph   (spectrogram only)"
                               : "Graph " + pv + "%   /   spectrogram " + (100 - pv) + "%",
                       pv == 0 ? "All the width to history."
                       : pv <= 12 ? "A narrow strip - enough to see the shape."
                       : pv <= 25 ? "Readable levels with most of the history kept."
                       : "A large graph. Precise to read, at real cost in history.",
                       s.CurveWidthPct == captured,
                       delegate { s.CurveWidthPct = captured; o.Changed(true); });
            }
            m.DropDownItems.Add(size);

            var bg = Sub("Background", "What is drawn behind the curve.");
            string[] bgTips = {
                "Nothing. The cleanest.",
                "Vertical dB lines, so a level can be read off directly.",
                "dB lines plus horizontal divisions.",
                "A chequerboard, as on the old analysers."
            };
            var bgs = (GraphBackground[])Enum.GetValues(typeof(GraphBackground));
            for (int i = 0; i < bgs.Length; i++)
            {
                GraphBackground captured = bgs[i];
                Choice(bg, bgs[i] == GraphBackground.Lines ? "dB lines" : bgs[i].ToString(),
                       i < bgTips.Length ? bgTips[i] : null, s.Background == captured,
                       delegate { s.Background = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(bg);

            var bar = Sub("Bar and LED size", "Block width for the Bars style, segment height for LED.\n"
                                              + "Affects only those two styles.");
            int[] sizes = { 3, 6, 10, 16 };
            foreach (int bv in sizes)
            {
                int captured = bv;
                Choice(bar, "Bar " + bv + " px", null, s.BarSize == captured,
                       delegate { s.BarSize = captured; o.Changed(false); });
            }
            bar.DropDownItems.Add(new ToolStripSeparator());
            int[] segs = { 3, 5, 8, 12 };
            foreach (int lv in segs)
            {
                int captured = lv;
                Choice(bar, "LED segment " + lv + " px", null, s.LedSegment == captured,
                       delegate { s.LedSegment = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(bar);

            m.DropDownItems.Add(new ToolStripSeparator());

            AddToggle(m.DropDownItems, "Maximum trace (peak hold)",
                      "A white line at the loudest level each frequency has reached,\n"
                      + "decaying slowly. Shows a track's spectral envelope.",
                      s.ShowMax, delegate { s.ShowMax = !s.ShowMax; o.Changed(false); });
            AddToggle(m.DropDownItems, "Average trace",
                      "A blue line showing the running average - the tonal balance of\n"
                      + "the material rather than of this instant.",
                      s.ShowAvg, delegate { s.ShowAvg = !s.ShowAvg; o.Changed(false); });
            AddToggle(m.DropDownItems, "Minimum trace",
                      "A grey line at the quietest level seen. Where it stops falling\n"
                      + "is the noise floor.",
                      s.ShowMin, delegate { s.ShowMin = !s.ShowMin; o.Changed(false); });
            AddToggle(m.DropDownItems, "Solid fill",
                      "Fill under the curve instead of drawing the outline alone.",
                      s.SolidFill, delegate { s.SolidFill = !s.SolidFill; o.Changed(false); });

            var decay = Sub("Peak hold decay", "How fast the maximum trace gives up a reading. Slow holds a\n"
                                               + "whole track's envelope; fast follows the music.");
            double[] decays = { 4, 8, 14, 24, 40 };
            foreach (double dv in decays)
            {
                double captured = dv;
                Choice(decay, dv.ToString("0") + " dB/s",
                       dv <= 4 ? "Nearly a permanent high-water mark."
                       : dv <= 14 ? "Holds long enough to compare one passage with the next."
                       : "Falls back quickly; close to the live curve.",
                       Math.Abs(s.PeakDecayDbPerSec - dv) < 0.01,
                       delegate { s.PeakDecayDbPerSec = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(decay);

            var avg = Sub("Average window", "How much history the average trace covers.");
            double[] avgs = { 0.3, 1.2, 3.0, 8.0, 20.0 };
            foreach (double av in avgs)
            {
                double captured = av;
                Choice(avg, av.ToString("0.#") + " s",
                       av <= 0.3 ? "Follows the music closely."
                       : av <= 3.0 ? "The balance of a phrase."
                       : "The balance of a whole section - what a mix decision needs.",
                       Math.Abs(s.AverageSeconds - av) < 0.01,
                       delegate { s.AverageSeconds = captured; o.Changed(false); });
            }
            m.DropDownItems.Add(avg);

            m.DropDownItems.Add(new ToolStripSeparator());

            AddToggle(m.DropDownItems, "Graph on the left of each pane",
                      "Which side of its pane each graph sits on. New spectrogram columns\n"
                      + "always enter against the graph and age away from it, so this also\n"
                      + "sets which way history flows.",
                      s.CurveOnLeft, delegate { s.CurveOnLeft = !s.CurveOnLeft; o.Changed(true); });
            AddToggle(m.DropDownItems, "Mirror left pane  (sound from the middle)  (M)",
                      "Flips the left pane so both channels' newest columns meet at the\n"
                      + "centre and history flows outward - the sound appears to emerge\n"
                      + "from the middle and spread to both edges.",
                      s.MirrorLeftPane,
                      delegate { s.MirrorLeftPane = !s.MirrorLeftPane; o.Changed(true); });

            return m;
        }

        private static ToolStripMenuItem Axes(Settings s, Options o)
        {
            var m = Sub("Axes and grid", "Gridlines, the frequency axis, and the space the time and dB\n"
                                         + "scales are given.");

            AddToggle(m.DropDownItems, "Gridlines  (G)",
                      "Horizontal lines across both panes at the labelled frequencies.\n"
                      + "Octaves are drawn brighter than the divisions between them.",
                      s.ShowGrid, delegate { s.ShowGrid = !s.ShowGrid; o.Changed(false); });
            AddToggle(m.DropDownItems, "Semitone gridlines",
                      "Faint lines at every semitone, drawn only when there is enough\n"
                      + "height for them to read as lines rather than as a wash.",
                      s.ShowSemitones, delegate { s.ShowSemitones = !s.ShowSemitones; o.Changed(false); });

            m.DropDownItems.Add(new ToolStripSeparator());

            AddToggle(m.DropDownItems, "Reserve space for scales",
                      "Give the time and dB scales a strip of their own instead of\n"
                      + "printing them on chips over the image. Costs a little height,\n"
                      + "and makes both legible against dense material.",
                      s.ReserveScaleSpace,
                      delegate { s.ReserveScaleSpace = !s.ReserveScaleSpace; o.Changed(true); });

            var lane = Sub("Scale strip position", "Which end of the panes the reserved strip sits at.");
            Choice(lane, "Top", "Above the image, beside the highest frequencies.",
                   s.ScaleLanePos == ScaleLanePosition.Top,
                   delegate { s.ScaleLanePos = ScaleLanePosition.Top; o.Changed(true); });
            Choice(lane, "Bottom",
                   "Below the image, beside the lowest frequencies - clear of the\ntrack info in the fullscreen view.",
                   s.ScaleLanePos == ScaleLanePosition.Bottom,
                   delegate { s.ScaleLanePos = ScaleLanePosition.Bottom; o.Changed(true); });
            lane.Enabled = s.ReserveScaleSpace;
            m.DropDownItems.Add(lane);

            AddToggle(m.DropDownItems, "Scale units",
                      "Name each scale's unit once, at the end it is measured from:\n"
                      + "dBFS at the graph's baseline, \"now\" at the spectrogram's live\n"
                      + "edge, and Hz or note at the top of the frequency axis. Without\n"
                      + "them the axes are bare numbers.",
                      s.ShowScaleUnits,
                      delegate { s.ShowScaleUnits = !s.ShowScaleUnits; o.Changed(false); });
            AddToggle(m.DropDownItems, "dB scale on graphs",
                      "Level numbers along the graph strips, in dBFS - decibels relative\n"
                      + "to full scale, so 0 is the loudest a sample can be. Label density\n"
                      + "follows the graph's width.",
                      s.ShowDbScale, delegate { s.ShowDbScale = !s.ShowDbScale; o.Changed(false); });
            AddToggle(m.DropDownItems, "Time markers on spectrograms",
                      "How many seconds ago each column was. Counted away from the graph,\n"
                      + "so under Mirror the two panes count outward in opposite directions.",
                      s.ShowTimeMarks, delegate { s.ShowTimeMarks = !s.ShowTimeMarks; o.Changed(false); });

            m.DropDownItems.Add(new ToolStripSeparator());

            AddToggle(m.DropDownItems, "Axis labels",
                      "Note names or frequencies beside the gridlines.",
                      s.ShowAxisLabels, delegate { s.ShowAxisLabels = !s.ShowAxisLabels; o.Changed(true); });
            AddToggle(m.DropDownItems, "Repeat labels at outer edges",
                      "Also print the axis down both outer edges, so you never have to\n"
                      + "track back to the centre to read a row. Costs a margin each side.",
                      s.ShowOuterLabels, delegate { s.ShowOuterLabels = !s.ShowOuterLabels; o.Changed(true); });

            var content = Sub("Label values", "What each gridline is labelled with.");
            Choice(content, "Note names", "C3, E4 and so on. Best for hearing what you are seeing.",
                   s.LabelMode == AxisLabelMode.Notes,
                   delegate { s.LabelMode = AxisLabelMode.Notes; o.Changed(true); });
            Choice(content, "Frequency", "Hertz. Best when you are working to numbers.",
                   s.LabelMode == AxisLabelMode.Frequency,
                   delegate { s.LabelMode = AxisLabelMode.Frequency; o.Changed(true); });
            Choice(content, "Both",
                   "Note name with its frequency beneath, on two lines so a narrow\ngutter still fits.",
                   s.LabelMode == AxisLabelMode.Both,
                   delegate { s.LabelMode = AxisLabelMode.Both; o.Changed(true); });
            m.DropDownItems.Add(content);

            var fonts = Sub("Text size", "Size of every label, scale and readout. The reserved margins\n"
                                         + "and the scale strip grow with it.");
            float[] sizes = { 6f, 7f, 8f, 9f, 11f, 13f };
            string[] fnames = { "Tiny", "Small", "Normal", "Large", "Larger", "Largest" };
            for (int i = 0; i < sizes.Length; i++)
            {
                float captured = sizes[i];
                Choice(fonts, fnames[i] + "   (" + sizes[i].ToString("0") + " pt)",
                       i <= 1 ? "For a short docked panel, where height is the scarce thing."
                       : i <= 3 ? "Comfortable at a normal viewing distance."
                       : "For a television, or a monitor across the room.",
                       Math.Abs(s.LabelFontSize - sizes[i]) < 0.01f,
                       delegate { s.LabelFontSize = captured; o.Changed(true); });
            }
            m.DropDownItems.Add(fonts);

            var gut = Sub("Centre gutter width", "The strip between the two panes that carries the shared\n"
                                                 + "frequency axis. Narrow it for more image, widen it for\n"
                                                 + "two-line labels.");
            int[] gutters = { 0, 22, 34, 50, 70 };
            foreach (int gv in gutters)
            {
                int captured = gv;
                Choice(gut, gv == 0 ? "None   (panes meet)" : gv + " px",
                       gv == 0 ? "The two spectrograms touch. Most image, no centre axis."
                       : gv < 40 ? "Enough for note names."
                       : "Room for note names and frequencies together.",
                       s.GutterWidth == captured,
                       delegate { s.GutterWidth = captured; o.Changed(true); });
            }
            m.DropDownItems.Add(gut);

            AddToggle(m.DropDownItems, "Channel labels",
                      "L and R - or Mid and Side - on each pane, parked at the oldest\n"
                      + "edge so they never sit on the incoming column.",
                      s.ShowLabels, delegate { s.ShowLabels = !s.ShowLabels; o.Changed(false); });

            return m;
        }

        private static ToolStripMenuItem Hover(Settings s, Options o)
        {
            var m = Sub("Hover", "What the pointer tells you about the spot under it.");
            AddToggle(m.DropDownItems, "Readout",
                      "Frequency, note name and cents, and the level in each channel.\n"
                      + "Over the spectrogram it reads the moment you are pointing at,\n"
                      + "not the live spectrum.",
                      s.ShowHud, delegate { s.ShowHud = !s.ShowHud; o.Changed(false); });
            AddToggle(m.DropDownItems, "Sync across both panes",
                      "Extend the crosshair into the other pane and read both channels,\n"
                      + "so the two can be compared at a glance.",
                      s.SyncHover, delegate { s.SyncHover = !s.SyncHover; o.Changed(false); });
            AddToggle(m.DropDownItems, "Pin note on the axis",
                      "Stamp the hovered note onto the frequency axis itself, so your eye\n"
                      + "can stay on the image.",
                      s.ShowHoverPin, delegate { s.ShowHoverPin = !s.ShowHoverPin; o.Changed(false); });
            AddToggle(m.DropDownItems, "Harmonic ruler",
                      "Ghost lines at 2x, 3x, 4x the hovered frequency. A line is either\n"
                      + "a fundamental or somebody's overtone, and this is the quickest\n"
                      + "way to tell which.",
                      s.ShowHarmonics, delegate { s.ShowHarmonics = !s.ShowHarmonics; o.Changed(false); });

            m.DropDownItems.Add(new ToolStripSeparator());
            var note = new ToolStripMenuItem("Click freezes  ·  drag measures");
            note.ToolTipText = "Dragging reports the interval in semitones and the time between\n"
                               + "the two points.";
            note.Enabled = false;
            m.DropDownItems.Add(note);
            return m;
        }

        private static ToolStripMenuItem Colour(Settings s, Options o)
        {
            var m = Sub("Colour", "The spectrogram palette. All but the last are perceptually\n"
                                  + "uniform: equal steps in level look like equal steps in colour,\n"
                                  + "so the picture does not invent contrast that is not in the data.");
            string[] tips = {
                "Black through purple to cream. The best all-round choice.",
                "Black through red to yellow. Warmer, slightly higher contrast.",
                "Dark blue through green to yellow. Colour-blind safe.",
                "Blue, green, red, yellow. The most colourful, and the least uniform -\nit can suggest edges that are not really there.",
                "Cool blues and white. Calm, and good for faint detail.",
                "Greyscale. Nothing between you and the levels.",
                "The original plugin's red ramp, for old times' sake."
            };
            var kinds = (PaletteKind[])Enum.GetValues(typeof(PaletteKind));
            for (int i = 0; i < kinds.Length; i++)
            {
                PaletteKind captured = kinds[i];
                Choice(m, Palette.DisplayName(kinds[i]), i < tips.Length ? tips[i] : null,
                       s.Palette == captured,
                       delegate { s.Palette = captured; s.Preset = Preset.Custom; o.Changed(false); });
            }
            m.DropDownItems.Add(new ToolStripSeparator());
            m.DropDownItems.Add(Elements(s, o));
            return m;
        }

        private static readonly string[] SlotNames = {
            "Background", "Panels and strips", "Gridlines", "Fine gridlines",
            "Axis text", "Unit captions", "Spectrum curve", "Peak trace",
            "Average trace", "Minimum trace", "Hover", "Waveform"
        };

        private static readonly string[] SlotTips = {
            "Behind everything. Normally the darkest entry of the palette.",
            "The scale strip, the centre gutter, the colour bar and the waveform\nlane grounds.",
            "The lines at labelled frequencies, and the level lines on the graph.",
            "The semitone lines and the subdivisions between labelled rows.",
            "Frequency, time and level numbers.",
            "The Hz, dBFS and \"now\" captions.",
            "The instantaneous spectrum. Set this and the graph stops following\nthe palette.",
            "The held maximum, normally white.",
            "The running average, normally blue.",
            "The quietest level seen, normally grey.",
            "The crosshair, the readout and the note pin.",
            "The waveform lanes under each pane."
        };

        /// <summary>
        /// Per-element colours.
        ///
        /// Every slot starts unset, meaning it follows the palette - which is what keeps
        /// the display coherent when the palette changes, and what lets colour keep
        /// following the music in immersive mode. Setting one opts that element out;
        /// it does not freeze the rest.
        ///
        /// Overrides supply hue only: the element keeps the transparency it was drawn
        /// with, because most of this display is deliberately translucent and a picker
        /// only offers opaque colours.
        /// </summary>
        private static ToolStripMenuItem Elements(Settings s, Options o)
        {
            var m = Sub("Elements", "Set the colour of individual parts. Anything left unset follows\n"
                                    + "the palette, including while it is tracking the music.");

            var vals = (ThemeSlot[])Enum.GetValues(typeof(ThemeSlot));
            for (int i = 0; i < vals.Length; i++)
            {
                ThemeSlot captured = vals[i];
                Color c = s.GetSlot(captured);
                var mi = new ToolStripMenuItem(
                    SlotNames[i] + (c.IsEmpty ? "" : "   \u25A0"));
                mi.ToolTipText = (i < SlotTips.Length ? SlotTips[i] + "\n\n" : "")
                                 + (c.IsEmpty ? "Following the palette."
                                              : "Set to #" + ((uint)c.ToArgb()).ToString("X8")
                                                + ". Right-hand entry below clears it.");
                mi.Click += delegate
                {
                    using (var dlg = new ColorDialog())
                    {
                        dlg.FullOpen = true;
                        dlg.AnyColor = true;
                        Color cur = s.GetSlot(captured);
                        if (!cur.IsEmpty) dlg.Color = cur;
                        if (dlg.ShowDialog(o.Owner) != DialogResult.OK) return;
                        s.SetSlot(captured, dlg.Color);
                        o.Changed(false);
                    }
                };
                m.DropDownItems.Add(mi);
            }

            m.DropDownItems.Add(new ToolStripSeparator());

            var reset = new ToolStripMenuItem("Back to the palette");
            reset.ToolTipText = "Clear every override, so all of it follows the palette again.";
            reset.Click += delegate { s.ClearAllSlots(); o.Changed(false); };
            m.DropDownItems.Add(reset);

            if (o.SkinColour != null)
            {
                var skin = new ToolStripMenuItem("Match the MusicBee skin");
                skin.ToolTipText = "Take the background, panel and text colours from the skin\n"
                                   + "MusicBee is using, so the panel sits in the window rather\n"
                                   + "than on it. The spectrogram palette is left alone - it is a\n"
                                   + "measurement scale, not decoration.";
                skin.Click += delegate { s.ThemeFromSkin(o.SkinColour); o.Changed(false); };
                m.DropDownItems.Add(skin);
            }

            if (o.StorageDir == null) return m;

            string[] themes = Settings.ListThemes(o.StorageDir);
            if (themes.Length > 0)
            {
                m.DropDownItems.Add(new ToolStripSeparator());
                foreach (string name in themes)
                {
                    string captured = name;
                    Choice(m, name, "Load this theme. Colours only - nothing else changes.",
                           false, delegate
                           {
                               if (s.LoadTheme(o.StorageDir, captured)) o.Changed(false);
                           });
                }
            }

            var save = new ToolStripMenuItem("Save these colours as...");
            save.ToolTipText = "Store the twelve colours under a name. Themes carry colours only,\n"
                               + "so one can be applied over any preset without dragging that\n"
                               + "preset's analysis settings along.";
            save.Click += delegate
            {
                string name = NameDialog.Ask(o.Owner, "Save theme", "Name for these colours:", "My theme");
                if (name == null) return;
                if (s.SaveTheme(o.StorageDir, name)) o.Changed(false);
            };
            m.DropDownItems.Add(save);

            if (themes.Length > 0)
            {
                var del = Sub("Delete theme", "Remove a saved theme. Asks first.");
                foreach (string name in themes)
                {
                    string captured = name;
                    Choice(del, name, "Delete this theme permanently.", false, delegate
                    {
                        if (MessageBox.Show("Delete theme \"" + captured + "\"?", "Nostalgia+",
                                            MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                            == DialogResult.Yes && s.DeleteTheme(o.StorageDir, captured))
                            o.Changed(false);
                    });
                }
                m.DropDownItems.Add(del);
            }

            return m;
        }

        // ---------------- the view itself ----------------

        private static ToolStripMenuItem View(Settings s, Options o)
        {
            var m = Sub("View", "Chrome: what is shown around the analysis, and how large the\n"
                                + "view itself is.");

            // Shared by both views, so the bar cannot end up on in one and off in the
            // other - which is exactly the kind of drift this menu exists to prevent.
            AddToggle(m.DropDownItems, "Quick action buttons",
                      "A row of one-click buttons along the bottom that cycle the settings\n"
                      + "you change most. They take reserved space rather than covering the\n"
                      + "image, and hide themselves when the view is too short to spare it.",
                      s.ShowQuickButtons,
                      delegate { s.ShowQuickButtons = !s.ShowQuickButtons; o.Changed(true); });
            var compact = new ToolStripMenuItem("Compact buttons");
            compact.ToolTipText = "Show each button's value alone on one line, halving the height\n"
                                  + "the bar costs. For short docked panels.";
            compact.Checked = s.QuickBarCompact;
            compact.Enabled = s.ShowQuickButtons;
            compact.Click += delegate { s.QuickBarCompact = !s.QuickBarCompact; o.Changed(true); };
            m.DropDownItems.Add(compact);

            var split = new ToolStripMenuItem("Split around the centre");
            split.ToolTipText = "Put half the buttons either side of the centre gutter, so the\n"
                                + "shared frequency axis runs unbroken from top to bottom instead\n"
                                + "of being crossed by the row.";
            split.Checked = s.QuickBarSplit;
            split.Enabled = s.ShowQuickButtons;
            split.Click += delegate { s.QuickBarSplit = !s.QuickBarSplit; o.Changed(true); };
            m.DropDownItems.Add(split);

            m.DropDownItems.Add(new ToolStripSeparator());

            if (o.IsFullscreen)
            {
                AddToggle(m.DropDownItems, "On-screen display  (H)",
                          "Master switch for everything drawn on top of the analysis: labels,\n"
                          + "meters, track info and the button bar. Gridlines stay, because\n"
                          + "they are part of reading the image rather than chrome on it.",
                          s.FsShowOsd, delegate { s.FsShowOsd = !s.FsShowOsd; o.Changed(false); });

                if (o.ToggleImmersive != null)
                {
                    var imm = new ToolStripMenuItem("Immersive mode  (I)");
                    imm.ToolTipText = "For watching rather than measuring: bloom on the spectrograms,\n"
                                      + "a musical axis, slower scroll, and every label fading away\n"
                                      + "while you are not touching anything.";
                    imm.Checked = s.FsImmersive;
                    imm.Click += delegate { o.ToggleImmersive(); };
                    m.DropDownItems.Add(imm);
                }
                var imm2 = Sub("Immersion", "How the picture responds to the music itself, rather than\n"
                                            + "just displaying it. All of it needs Immersive mode on.");
                AddToggle(imm2.DropDownItems, "Album art backdrop",
                          "The artwork blurred and dimmed behind everything. Drawn as the\n"
                          + "ground rather than over the top, so it shows through where\n"
                          + "there is no data and is covered where there is - ambient light\n"
                          + "behind the analysis rather than a wash over it.",
                          s.ImmBackdrop,
                          delegate { s.ImmBackdrop = !s.ImmBackdrop; o.Changed(false); });

                var bd = Sub("Backdrop strength", "How far the artwork comes forward.");
                int[] bdp = { 0, 8, 18, 30, 45, 60 };
                foreach (int bv in bdp)
                {
                    int captured = bv;
                    Choice(bd, bv == 0 ? "Off" : bv + "%",
                           bv == 0 ? "No backdrop."
                           : bv <= 18 ? "A suggestion of colour in the empty parts."
                           : bv <= 30 ? "Clearly the album, still behind the analysis."
                           : "Strong. The artwork starts competing with the spectrogram.",
                           s.BackdropPct == captured,
                           delegate { s.BackdropPct = captured; o.Changed(false); });
                }
                bd.Enabled = s.ImmBackdrop;
                imm2.DropDownItems.Add(bd);

                AddToggle(imm2.DropDownItems, "Beat reactive",
                          "The screen edges flare on each onset, detected from rising\n"
                          + "spectral energy. Four gradient bars rather than a full vignette:\n"
                          + "the edge of vision is where a beat is felt without pulling you\n"
                          + "off the analysis.",
                          s.ImmBeatReactive,
                          delegate { s.ImmBeatReactive = !s.ImmBeatReactive; o.Changed(false); });

                AddToggle(imm2.DropDownItems, "Colour follows the music",
                          "Hue shifts with the spectral centroid, so a bright passage and a\n"
                          + "dark one are different colours. Only new spectrogram columns\n"
                          + "take the new hue, so the image carries its own recent history\n"
                          + "in colour as well as in shape.",
                          s.ImmColourFollows,
                          delegate { s.ImmColourFollows = !s.ImmColourFollows; o.Changed(false); });

                var cf = Sub("Colour swing", "How far the hue travels between the darkest and\n"
                                             + "brightest passages.");
                int[] cfd = { 15, 25, 40, 70, 120 };
                foreach (int cv in cfd)
                {
                    int captured = cv;
                    Choice(cf, cv + " degrees",
                           cv <= 25 ? "A tint. You notice it without being able to name it."
                           : cv <= 70 ? "Clearly a different colour between sections."
                           : "Right across the wheel. Dramatic, and further from the\npalette you chose.",
                           s.ColourFollowDegrees == captured,
                           delegate { s.ColourFollowDegrees = captured; o.Changed(false); });
                }
                cf.Enabled = s.ImmColourFollows;
                imm2.DropDownItems.Add(cf);

                AddToggle(imm2.DropDownItems, "Cinematic motion",
                          "Quarter scroll speed and three times the fall time, so the image\n"
                          + "drifts and hits leave trails. Reading exact timings gets harder;\n"
                          + "watching a whole section as one shape gets much easier.",
                          s.ImmCinematic,
                          delegate { s.ImmCinematic = !s.ImmCinematic; o.Changed(false); });
                imm2.Enabled = s.FsImmersive;
                m.DropDownItems.Add(imm2);

                AddToggle(m.DropDownItems, "Glow",
                          "Bloom around bright spectrogram content. The most expensive thing\n"
                          + "drawn each frame - turn it off first if frames are dropping.",
                          s.FsGlow, delegate { s.FsGlow = !s.FsGlow; o.Changed(false); });
                AddToggle(m.DropDownItems, "Hide labels when idle",
                          "Fade the furniture out after a few seconds without input, and bring\n"
                          + "it straight back on the next mouse move or keystroke.",
                          s.FsAutoHide, delegate { s.FsAutoHide = !s.FsAutoHide; o.Changed(false); });

                m.DropDownItems.Add(new ToolStripSeparator());

                AddToggle(m.DropDownItems, "Meters and track info  (O)",
                          "The title bar, and the loudness meters: LUFS momentary and short\n"
                          + "term, true peak and crest factor. Correlation and balance live in\n"
                          + "the centre deck, beside the goniometer they describe.",
                          s.FsShowOverlays, delegate { s.FsShowOverlays = !s.FsShowOverlays; o.Changed(false); });
                AddToggle(m.DropDownItems, "Waveform lanes  (W)",
                          "A scrolling waveform under each pane. It advances on the same tick\n"
                          + "as the spectrogram above it, so a transient sits directly under\n"
                          + "the column that produced it at any scroll speed.",
                          s.FsShowWaveform, delegate { s.FsShowWaveform = !s.FsShowWaveform; o.Changed(true); });

                AddToggle(m.DropDownItems, "Centre deck",
                          "Fills the gap between the two waveform lanes - the one part of\n"
                          + "the screen that belongs to neither channel - with the things\n"
                          + "that describe both: the goniometer, correlation and balance,\n"
                          + "the transport and the artwork.",
                          s.ShowCenterDeck,
                          delegate { s.ShowCenterDeck = !s.ShowCenterDeck; o.Changed(true); });

                var deck = Sub("Centre deck contents", "What the deck shows. Anything that will not fit the gap is\n"
                                                       + "dropped, artwork first and the goniometer last.");
                AddToggle(deck.DropDownItems, "Goniometer",
                          "A Lissajous plot of left against right, rotated so mono reads as\n"
                          + "a vertical line. A circle is a wide image, a horizontal line is\n"
                          + "out of phase, and a lean to one side is a level imbalance.",
                          s.DeckShowGoniometer,
                          delegate { s.DeckShowGoniometer = !s.DeckShowGoniometer; o.Changed(true); });
                AddToggle(deck.DropDownItems, "Correlation and balance",
                          "+1 is mono, 0 is uncorrelated, -1 means the channels cancel and\n"
                          + "the track will not survive being summed to mono.",
                          s.DeckShowMeters,
                          delegate { s.DeckShowMeters = !s.DeckShowMeters; o.Changed(true); });
                AddToggle(deck.DropDownItems, "Transport and position",
                          "Previous, play/pause and next, with elapsed time and a seek bar\n"
                          + "you can click to jump.",
                          s.DeckShowTransport,
                          delegate { s.DeckShowTransport = !s.DeckShowTransport; o.Changed(true); });
                AddToggle(deck.DropDownItems, "Album art",
                          "The current track's artwork. Dropped first when the gap is narrow -\n"
                          + "widen the graph strips to make room for it.",
                          s.DeckShowArtwork,
                          delegate { s.DeckShowArtwork = !s.DeckShowArtwork; o.Changed(true); });
                deck.Enabled = s.ShowCenterDeck;
                m.DropDownItems.Add(deck);

                var wave = Sub("Waveform height", "How tall the bottom band is - the waveform lanes and the\n"
                                                  + "centre deck share it.");
                int[] wpcts = { 5, 10, 15, 22, 30 };
                foreach (int wv in wpcts)
                {
                    int captured = wv;
                    Choice(wave, wv + "%",
                           wv <= 10 ? "A thin strip - enough to see the dynamics."
                           : "Room to read the shape of individual hits.",
                           s.WaveHeightPct == captured,
                           delegate { s.WaveHeightPct = captured; o.Changed(true); });
                }
                m.DropDownItems.Add(wave);
            }
            else
            {
                AddToggle(m.DropDownItems, "Colour bar",
                          "The palette ramp down the right edge, labelled with the current\n"
                          + "floor and ceiling - what each colour in the spectrogram means.",
                          s.ShowColorBar, delegate { s.ShowColorBar = !s.ShowColorBar; o.Changed(true); });
                AddToggle(m.DropDownItems, "Status line",
                          "Source, transform sizes, channel mode, frame rate, analysis time\n"
                          + "and the active preset, along the top.",
                          s.ShowStatus, delegate { s.ShowStatus = !s.ShowStatus; o.Changed(false); });

                m.DropDownItems.Add(new ToolStripSeparator());

                int screenH = Screen.PrimaryScreen.WorkingArea.Height;
                var height = Sub("Panel height", "Resizes the docked panel now, and remembers the choice for the\n"
                                                 + "next launch. Frequency resolution on screen follows this height:\n"
                                                 + "a taller panel resolves more notes.");
                int[] hpcts = { 25, 33, 50, 66, 75 };
                foreach (int pct in hpcts)
                {
                    int px = screenH * pct / 100;
                    int captured = px;
                    Choice(height, pct + "%   (" + px + " px)",
                           pct <= 33 ? "Compact. Octaves only on the axis."
                           : pct <= 50 ? "Enough height for the axis to subdivide the octave."
                           : "Most of the window. Near-fullscreen detail while docked.",
                           Math.Abs(s.DockPanelHeight - px) <= 2,
                           delegate
                           {
                               s.DockPanelHeight = captured;
                               if (o.SetDockHeight != null) o.SetDockHeight(captured);
                               o.Changed(false);
                           });
                }
                m.DropDownItems.Add(height);
            }
            return m;
        }
    }
}

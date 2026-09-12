using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using NostalgiaPlus;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Ui;

class TestHarness
{
    const double Sr = 48000;
    static int _fail = 0;

    static void Main()
    {
        Console.WriteLine("=== Nostalgia+ DSP verification ===\n");
        TestToneAccuracy();
        TestStereoSeparation();
        TestCrossoverContinuity();
        TestNoteNaming();
        TestDynamicRange();
        TestLoudness();
        TestUserPresets();
        TestSettingsPersistence();
        TestLayoutBudgets();
        TestMusicFeatures();
        TestThemes();
        TestMenuActions();
        Console.WriteLine(_fail == 0 ? "\nALL CHECKS PASSED" : "\n" + _fail + " CHECK(S) FAILED");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    static void Check(string label, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label + "   " + detail);
        if (!ok) _fail++;
    }

    static SampleRing FillRing(Func<int, double> gen, int count)
    {
        var ring = new SampleRing(1 << 18);
        var buf = new float[count * 2];
        for (int i = 0; i < count; i++)
        {
            float v = (float)gen(i);
            buf[i * 2] = v;
            buf[i * 2 + 1] = v;
        }
        ring.Write(buf, 0, count, 2);
        return ring;
    }

    static void TestToneAccuracy()
    {
        Console.WriteLine("[tone placement and level]");
        var an = new SpectrumAnalyzer();
        an.Configure(Sr, AnalysisQuality.Balanced, WindowType.Hann);

        // 100 Hz at 0.25 (-12.04 dBFS) and 1000 Hz at 0.5 (-6.02 dBFS)
        var ring = FillRing(i =>
            0.25 * Math.Sin(2 * Math.PI * 100 * i / Sr) +
            0.50 * Math.Sin(2 * Math.PI * 1000 * i / Sr), 40000);

        var map = new FrequencyMap(FreqScale.Note, 1200, 20, 20000);
        var db = new double[1200];
        bool ok = an.Compute(ring, ChannelMode.Mid, map, db, BandAggregate.Peak, 0.0);
        Check("Compute returns data", ok, "");
        if (!ok) return;

        int p1 = PeakNear(db, map, 100);
        int p2 = PeakNear(db, map, 1000);
        double f1 = map.Centres[p1], f2 = map.Centres[p2];

        Check("100 Hz tone lands on the right column", Math.Abs(f1 - 100) < 3.0,
              "found " + f1.ToString("0.0") + " Hz, level " + db[p1].ToString("0.0") + " dB");
        Check("1 kHz tone lands on the right column", Math.Abs(f2 - 1000) < 12.0,
              "found " + f2.ToString("0.0") + " Hz, level " + db[p2].ToString("0.0") + " dB");
        Check("100 Hz level near -12.0 dBFS", Math.Abs(db[p1] + 12.04) < 3.0,
              "got " + db[p1].ToString("0.00") + " dB");
        Check("1 kHz level near -6.0 dBFS", Math.Abs(db[p2] + 6.02) < 3.0,
              "got " + db[p2].ToString("0.00") + " dB");
        Check("relative level between the two tones is ~6 dB",
              Math.Abs((db[p2] - db[p1]) - 6.02) < 1.5,
              "delta " + (db[p2] - db[p1]).ToString("0.00") + " dB");

        // A tone should be a narrow feature, not a smear across the display.
        int wide = 0;
        for (int i = 0; i < db.Length; i++) if (db[i] > db[p2] - 20) wide++;
        Check("tones stay narrow (no leakage smear)", wide < 120, wide + " columns within 20 dB of peak");
        Console.WriteLine();
    }


    static SampleRing FillRingStereo(Func<int, double> genL, Func<int, double> genR, int count)
    {
        var ring = new SampleRing(1 << 18);
        var buf = new float[count * 2];
        for (int i = 0; i < count; i++)
        {
            buf[i * 2] = (float)genL(i);
            buf[i * 2 + 1] = (float)genR(i);
        }
        ring.Write(buf, 0, count, 2);
        return ring;
    }

    static void TestStereoSeparation()
    {
        Console.WriteLine("[stereo separation via paired FFT]");
        var an = new SpectrumAnalyzer();
        an.Configure(Sr, AnalysisQuality.Balanced, WindowType.Hann);

        // Different tone in each channel: 1 kHz at -6 dB left, 3 kHz at -12 dB right.
        var ring = FillRingStereo(
            i => 0.50 * Math.Sin(2 * Math.PI * 1000 * i / Sr),
            i => 0.25 * Math.Sin(2 * Math.PI * 3000 * i / Sr), 40000);

        var map = new FrequencyMap(FreqScale.Note, 1200, 20, 20000);
        var L = new double[1200];
        var R = new double[1200];
        bool ok = an.ComputeStereo(ring, map, L, R, BandAggregate.Peak, 0.0);
        Check("ComputeStereo returns data", ok, "");
        if (!ok) return;

        int l1 = PeakNear(L, map, 1000);
        int r3 = PeakNear(R, map, 3000);
        Check("left channel tone at 1 kHz", Math.Abs(map.Centres[l1] - 1000) < 12 && Math.Abs(L[l1] + 6.02) < 3.0,
              map.Centres[l1].ToString("0.0") + " Hz at " + L[l1].ToString("0.00") + " dB");
        Check("right channel tone at 3 kHz", Math.Abs(map.Centres[r3] - 3000) < 30 && Math.Abs(R[r3] + 12.04) < 3.0,
              map.Centres[r3].ToString("0.0") + " Hz at " + R[r3].ToString("0.00") + " dB");

        // The separation must not leak either tone into the opposite channel.
        int lAt3 = PeakNear(L, map, 3000, 0);
        int rAt1 = PeakNear(R, map, 1000, 0);
        Check("3 kHz does not leak into left", L[lAt3] < L[l1] - 60,
              "left at 3 kHz = " + L[lAt3].ToString("0.0") + " dB (peak " + L[l1].ToString("0.0") + ")");
        Check("1 kHz does not leak into right", R[rAt1] < R[r3] - 60,
              "right at 1 kHz = " + R[rAt1].ToString("0.0") + " dB (peak " + R[r3].ToString("0.0") + ")");

        // A centred mono signal must read identically in both channels.
        var mono = FillRingStereo(i => 0.4 * Math.Sin(2 * Math.PI * 500 * i / Sr),
                                  i => 0.4 * Math.Sin(2 * Math.PI * 500 * i / Sr), 40000);
        an.ComputeStereo(mono, map, L, R, BandAggregate.Peak, 0.0);
        int m = PeakNear(L, map, 500);
        Check("mono source reads equal in both channels", Math.Abs(L[m] - R[m]) < 0.01,
              "L " + L[m].ToString("0.000") + " dB vs R " + R[m].ToString("0.000") + " dB");
        Console.WriteLine();
    }

    static void TestCrossoverContinuity()
    {
        Console.WriteLine("[multi-resolution crossover continuity]");
        var an = new SpectrumAnalyzer();
        an.Configure(Sr, AnalysisQuality.Balanced, WindowType.Hann);

        var rnd = new Random(1234);
        var ring = FillRing(i => (rnd.NextDouble() * 2 - 1) * 0.2, 40000);

        var map = new FrequencyMap(FreqScale.Note, 1200, 20, 20000);
        var db = new double[1200];
        an.Compute(ring, ChannelMode.Mid, map, db, BandAggregate.Energy, 0.0);

        // On white noise, the stitched bands must not leave a visible step at the
        // 300 Hz and 3 kHz corners.
        foreach (double corner in new double[] { 300, 3000 })
        {
            int c = PeakNear(db, map, corner, 0); // nearest column, no search
            double before = Average(db, c - 25, c - 5);
            double after = Average(db, c + 5, c + 25);
            Check("no step at " + corner + " Hz crossover", Math.Abs(before - after) < 6.0,
                  "left " + before.ToString("0.0") + " dB vs right " + after.ToString("0.0") + " dB");
        }
        Console.WriteLine();
    }

    static void TestNoteNaming()
    {
        Console.WriteLine("[musical readout]");
        double cents;
        string n = FrequencyMap.DescribeNote(440.0, out cents);
        Check("440 Hz reads as A4", n == "A4" && Math.Abs(cents) < 0.5, n + " " + cents.ToString("0.0") + "c");

        n = FrequencyMap.DescribeNote(261.626, out cents);
        Check("261.6 Hz reads as C4", n == "C4" && Math.Abs(cents) < 0.5, n + " " + cents.ToString("0.0") + "c");

        n = FrequencyMap.DescribeNote(41.203, out cents);
        Check("41.2 Hz reads as E1", n == "E1" && Math.Abs(cents) < 0.5, n + " " + cents.ToString("0.0") + "c");

        // Slightly sharp A4 should report positive cents.
        n = FrequencyMap.DescribeNote(444.0, out cents);
        Check("444 Hz reads as A4 about +16 cents", n == "A4" && cents > 10 && cents < 20,
              n + " " + cents.ToString("0.0") + "c");
        Console.WriteLine();
    }

    static void TestDynamicRange()
    {
        Console.WriteLine("[adaptive dynamic range]");
        var dr = new DynamicRange();
        var db = new double[1000];
        // Material spanning -80 to -20 dB.
        for (int i = 0; i < db.Length; i++) db[i] = -80 + (i / (double)db.Length) * 60;
        for (int f = 0; f < 200; f++) { dr.Observe(db, db.Length, 0.94); dr.Update(1.0 / 60.0); }

        Check("floor tracks the quiet end", dr.Floor > -85 && dr.Floor < -50,
              "floor " + dr.Floor.ToString("0.0") + " dB");
        Check("ceiling tracks the loud end", dr.Ceiling > -30 && dr.Ceiling < 5,
              "ceiling " + dr.Ceiling.ToString("0.0") + " dB");
        Check("span stays usable", (dr.Ceiling - dr.Floor) > 20, "span " +
              (dr.Ceiling - dr.Floor).ToString("0.0") + " dB");
        Console.WriteLine();
    }


    static void TestLoudness()
    {
        Console.WriteLine("[loudness and stereo metering]");

        // BS.1770 calibration: a 1 kHz sine at -23 dBFS in both channels reads -23 LUFS.
        var m = new LoudnessMeter();
        m.Configure(Sr);
        int n = (int)(Sr * 4);
        var L = new double[n];
        var R = new double[n];
        double amp = Math.Pow(10.0, -23.0 / 20.0);
        for (int i = 0; i < n; i++) { L[i] = amp * Math.Sin(2 * Math.PI * 1000 * i / Sr); R[i] = L[i]; }
        m.Process(L, R, n);

        Check("short-term LUFS of -23 dBFS 1 kHz tone", Math.Abs(m.ShortTermLufs + 23.0) < 0.35,
              m.ShortTermLufs.ToString("0.00") + " LUFS (expected -23.00)");
        Check("momentary LUFS agrees", Math.Abs(m.MomentaryLufs + 23.0) < 0.35,
              m.MomentaryLufs.ToString("0.00") + " LUFS");
        Check("identical channels correlate at +1", Math.Abs(m.Correlation - 1.0) < 0.01,
              m.Correlation.ToString("0.000"));
        Check("identical channels are centred", Math.Abs(m.Balance) < 0.01,
              m.Balance.ToString("0.000"));

        // Full-scale sine should read about 0 dBTP.
        var m2 = new LoudnessMeter();
        m2.Configure(Sr);
        for (int i = 0; i < n; i++) { L[i] = Math.Sin(2 * Math.PI * 997 * i / Sr); R[i] = L[i]; }
        m2.Process(L, R, n);
        Check("true peak of full-scale sine near 0 dBTP", Math.Abs(m2.TruePeakDb) < 1.0,
              m2.TruePeakDb.ToString("0.00") + " dBTP");

        // Polarity-inverted right channel must read as fully out of phase.
        var m3 = new LoudnessMeter();
        m3.Configure(Sr);
        for (int i = 0; i < n; i++) { L[i] = 0.3 * Math.Sin(2 * Math.PI * 300 * i / Sr); R[i] = -L[i]; }
        m3.Process(L, R, n);
        Check("inverted channel correlates at -1", Math.Abs(m3.Correlation + 1.0) < 0.01,
              m3.Correlation.ToString("0.000"));

        // Signal only in the right channel must show hard-right balance.
        var m4 = new LoudnessMeter();
        m4.Configure(Sr);
        for (int i = 0; i < n; i++) { L[i] = 0.0; R[i] = 0.3 * Math.Sin(2 * Math.PI * 400 * i / Sr); }
        m4.Process(L, R, n);
        Check("right-only signal reads hard right", m4.Balance > 0.98,
              m4.Balance.ToString("0.000"));
        Console.WriteLine();
    }

    /// <summary>
    /// Every public setting has to survive a save and a load.
    ///
    /// Checking a handful by hand missed the real failure mode: adding a setting and
    /// forgetting one of its two lines in Load/Save. That loses the value silently, and
    /// only on the next restart - so it reads as "the plugin forgot my settings" rather
    /// than as a bug in the line you just wrote. Reflection covers every field, so a
    /// new setting is covered the moment it is declared.
    /// </summary>
    static void TestSettingsPersistence()
    {
        Console.WriteLine("[settings persistence]");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                            "NostalgiaPlusTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            FieldInfo[] fields = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            var a = new Settings();
            int mutated = 0;
            foreach (FieldInfo f in fields)
            {
                object v = Mutate(f.FieldType, f.GetValue(a));
                if (v == null) continue;
                f.SetValue(a, v);
                mutated++;
            }
            Check("every field is testable", mutated == fields.Length,
                  mutated + " of " + fields.Length);

            a.SaveUserPreset(dir, "Coverage");
            var b = new Settings();
            b.LoadUserPreset(dir, "Coverage");

            var lost = new List<string>();
            foreach (FieldInfo f in fields)
                if (!Equals(f.GetValue(a), f.GetValue(b))) lost.Add(f.Name);

            Check("every setting survives save and load", lost.Count == 0,
                  lost.Count == 0 ? fields.Length + " fields"
                                  : "lost: " + string.Join(", ", lost.ToArray()));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>A value guaranteed to differ from the one passed in.</summary>
    static object Mutate(Type t, object current)
    {
        if (t == typeof(bool)) return !(bool)current;
        if (t == typeof(int)) return (int)current + 7;
        if (t == typeof(float)) return (float)current + 1.5f;
        if (t == typeof(double)) return (double)current + 1.5;
        if (t == typeof(Color))
        {
            // Must differ from Color.Empty in a way that survives #AARRGGBB, which
            // means opaque and non-zero: an empty colour is written as "auto".
            var c = (Color)current;
            return c.IsEmpty ? Color.FromArgb(255, 17, 34, 51)
                             : Color.FromArgb(255, c.B, c.R, c.G);
        }
        if (t.IsEnum)
        {
            Array vals = Enum.GetValues(t);
            int i = Array.IndexOf(vals, current);
            return vals.GetValue((i + 1) % vals.Length);
        }
        return null;
    }

    /// <summary>
    /// The two pieces of chrome that take space off the analysis have to give it back
    /// when they are switched off, and give up gracefully when there is not enough.
    /// </summary>
    static void TestLayoutBudgets()
    {
        Console.WriteLine("[layout budgets]");
        var s = new Settings();
        using (var font = new Font("Segoe UI", 7f))
        {
            var full = new Rectangle(0, 0, 1920, 1000);
            Rectangle bar;

            s.ShowQuickButtons = true;
            Rectangle left = QuickBar.Reserve(full, s, font, out bar);
            int h = QuickBar.HeightFor(font, false);
            Check("quick bar takes its height off the bottom",
                  bar.Height == h && left.Height == full.Height - h && bar.Bottom == full.Bottom,
                  "bar " + bar.Height + "px, panes " + left.Height + "px");

            s.ShowQuickButtons = false;
            left = QuickBar.Reserve(full, s, font, out bar);
            Check("switched off it costs nothing", bar.Height == 0 && left == full, "");

            // Below four bar heights the strip is taking more than it gives back.
            s.ShowQuickButtons = true;
            left = QuickBar.Reserve(new Rectangle(0, 0, 900, h * 3), s, font, out bar);
            Check("hidden when the view is too short", bar.Height == 0, "at " + (h * 3) + "px tall");

            s.QuickBarCompact = true;
            Check("compact costs less height",
                  QuickBar.HeightFor(font, true) < QuickBar.HeightFor(font, false),
                  QuickBar.HeightFor(font, true) + " vs " + QuickBar.HeightFor(font, false));
        }

        var deck = new CenterDeck();
        var wide = new Settings();
        deck.Layout(new Rectangle(0, 0, 700, 110), wide);
        Check("a wide gap fits everything",
              deck.ArtRect.Width > 0 && deck.GoniometerRect.Width > 0 && deck.StackRect.Width > 0, "");

        deck.Layout(new Rectangle(0, 0, 260, 110), wide);
        Check("a narrow gap drops the artwork first",
              deck.ArtRect.Width == 0 && deck.GoniometerRect.Width > 0 && deck.StackRect.Width > 0,
              "gonio " + deck.GoniometerRect.Width + "px");

        deck.Layout(new Rectangle(0, 0, 120, 110), wide);
        Check("the goniometer is the last to go",
              deck.GoniometerRect.Width > 0 && deck.StackRect.Width == 0, "");

        deck.Layout(new Rectangle(0, 0, 20, 110), wide);
        Check("no room at all leaves nothing placed",
              deck.GoniometerRect.Width == 0 && deck.StackRect.Width == 0, "");
    }

    /// <summary>
    /// The features that drive the immersive reactions: onsets, tempo and brightness.
    ///
    /// Driven with a synthetic spectrum rather than audio, because that is exactly what
    /// the detector sees - a dB array per frame - and it makes the expected answer
    /// arithmetic rather than a judgement call.
    /// </summary>
    static void TestMusicFeatures()
    {
        Console.WriteLine("[music features]");
        const int Bins = 256;
        const double Fps = 60.0;
        const double Dt = 1.0 / Fps;

        // --- onsets and tempo: a 120 BPM click, one loud frame every half second ---
        var f = new MusicFeatures();
        var db = new double[Bins];
        int hitEvery = (int)(Fps * 0.5);
        int onsets = 0, framesOnBeat = 0;
        for (int frame = 0; frame < 900; frame++)          // 15 seconds
        {
            bool hit = frame % hitEvery == 0;
            for (int i = 0; i < Bins; i++) db[i] = hit ? -30.0 : -75.0;
            f.Update(db, Bins, Dt);
            if (f.Onset)
            {
                onsets++;
                if (frame % hitEvery == 0) framesOnBeat++;
            }
        }
        Check("fires roughly one onset per click", onsets >= 25 && onsets <= 32,
              onsets + " onsets for 30 clicks");
        Check("every onset lands on a click", framesOnBeat == onsets,
              framesOnBeat + " of " + onsets + " on the beat");
        Check("finds 120 BPM", Math.Abs(f.Bpm - 120.0) < 4.0, f.Bpm.ToString("0.0") + " BPM");

        // --- the pulse decays between hits and is not simply always on ---
        double afterHit = 0, beforeNext = 0;
        for (int frame = 0; frame < hitEvery; frame++)
        {
            bool hit = frame == 0;
            for (int i = 0; i < Bins; i++) db[i] = hit ? -30.0 : -75.0;
            f.Update(db, Bins, Dt);
            if (frame == 0) afterHit = f.Pulse;
            beforeNext = f.Pulse;
        }
        Check("the pulse peaks at the hit", afterHit > 0.9, afterHit.ToString("0.00"));
        // Checked against the decay curve rather than a round number, so changing the
        // constant fails here instead of quietly changing how a beat reads.
        double elapsed = (hitEvery - 1) / Fps;
        double expected = Math.Exp(-elapsed / f.PulseDecaySeconds);
        Check("and is well down before the next", beforeNext < 0.12,
              beforeNext.ToString("0.000") + " after " + elapsed.ToString("0.00") + "s");
        Check("following the stated decay", Math.Abs(beforeNext - expected) < 0.02,
              "expected " + expected.ToString("0.000"));

        // --- a steady tone produces no onsets after the first ---
        var g = new MusicFeatures();
        int steady = 0;
        for (int frame = 0; frame < 300; frame++)
        {
            for (int i = 0; i < Bins; i++) db[i] = -40.0;
            g.Update(db, Bins, Dt);
            if (f.Onset && frame > 10) steady++;
        }
        Check("silence between hits triggers nothing", steady == 0, steady + " spurious");
        Check("and no tempo is claimed", g.Bpm == 0, g.Bpm.ToString("0.0"));

        // --- centroid follows where the energy actually is ---
        var lowF = new MusicFeatures();
        var highF = new MusicFeatures();
        var low = new double[Bins];
        var high = new double[Bins];
        for (int i = 0; i < Bins; i++)
        {
            low[i] = i < Bins / 8 ? -20.0 : SpectrumAnalyzer.FloorDb;
            high[i] = i > Bins * 7 / 8 ? -20.0 : SpectrumAnalyzer.FloorDb;
        }
        for (int frame = 0; frame < 300; frame++)
        {
            lowF.Update(low, Bins, Dt);
            highF.Update(high, Bins, Dt);
        }
        Check("bass-only reads low on the axis", lowF.Centroid < 0.2,
              lowF.Centroid.ToString("0.00"));
        Check("treble-only reads high on the axis", highF.Centroid > 0.8,
              highF.Centroid.ToString("0.00"));
        Check("the two are far apart", highF.Centroid - lowF.Centroid > 0.6,
              (highF.Centroid - lowF.Centroid).ToString("0.00"));
    }

    /// <summary>
    /// Themes carry colours and nothing else, so one can be applied over any preset.
    /// The point of the test is that second half: loading a theme must not disturb the
    /// analysis settings around it.
    /// </summary>
    static void TestThemes()
    {
        Console.WriteLine("[themes]");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                            "NostalgiaPlusTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var a = new Settings();
            a.SetSlot(ThemeSlot.Background, Color.FromArgb(255, 12, 24, 36));
            a.SetSlot(ThemeSlot.PeakTrace, Color.FromArgb(255, 250, 200, 40));
            Check("saves", a.SaveTheme(dir, "Midnight"), "");
            Check("appears in the listing", Settings.ListThemes(dir).Length == 1, "");

            var b = new Settings();
            b.ApplyPreset(Preset.Bass);
            double fmax = b.FMax;
            var quality = b.Quality;
            b.SetSlot(ThemeSlot.Background, Color.FromArgb(255, 90, 0, 0));

            Check("loads", b.LoadTheme(dir, "Midnight"), "");
            Check("colours arrive",
                  b.GetSlot(ThemeSlot.Background).ToArgb() == Color.FromArgb(255, 12, 24, 36).ToArgb()
                  && b.GetSlot(ThemeSlot.PeakTrace).ToArgb() == Color.FromArgb(255, 250, 200, 40).ToArgb(),
                  "#" + ((uint)b.GetSlot(ThemeSlot.Background).ToArgb()).ToString("X8"));
            Check("unset slots come back unset", b.GetSlot(ThemeSlot.Curve).IsEmpty, "");
            Check("the preset is left alone", b.FMax == fmax && b.Quality == quality,
                  b.Quality + " to " + b.FMax.ToString("0") + " Hz");

            b.ClearAllSlots();
            Check("clearing returns everything to the palette",
                  b.GetSlot(ThemeSlot.Background).IsEmpty && b.GetSlot(ThemeSlot.PeakTrace).IsEmpty, "");
            Check("deletes", b.DeleteTheme(dir, "Midnight") && Settings.ListThemes(dir).Length == 0, "");
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Every menu action now runs through a wrapper that posts it back to the control
    /// the menu belongs to, so the menu can finish closing before a dialog or a new
    /// window appears. With no such control - which is this harness, and any detached
    /// menu - the wrapper has to fall through and run the action directly rather than
    /// silently dropping it.
    /// </summary>
    static void TestMenuActions()
    {
        Console.WriteLine("[menu actions]");
        var s = new Settings();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        MenuFactory.Populate(menu, s, new MenuFactory.Options
        {
            IsFullscreen = false,
            ScrollPixels = 800,
            IsFrozen = delegate { return false; },
            ToggleFreeze = delegate { },
            Changed = delegate(bool rebuild) { },
        });

        Check("the menu builds", menu.Items.Count > 0, menu.Items.Count + " top-level items");

        var grid = FindItem(menu.Items, "Gridlines");
        Check("a known toggle is present", grid != null, "");
        if (grid == null) return;

        bool before = s.ShowGrid;
        grid.PerformClick();
        Check("clicking it still acts with no host to post to", s.ShowGrid != before,
              before + " -> " + s.ShowGrid);

        // Explanations moved off tooltips and onto Tag, where the help window reads
        // them. The menu must not show tooltips any more, and every item must still
        // carry its text - losing it here would silently empty the help.
        Check("the menu shows no tooltips", !menu.ShowItemToolTips, "");
        Check("items still carry their explanation", !string.IsNullOrEmpty(grid.Tag as string),
              FirstLine(grid.Tag as string));

        int described = 0, total = 0;
        CountTips(menu.Items, ref described, ref total);
        Check("nearly every item explains itself", described >= total * 9 / 10,
              described + " of " + total);
        menu.Dispose();

        // The help window is the only place those explanations now surface, so it has to
        // find all of them - including the fullscreen-only half of the View menu, which
        // is not built when the menu is opened from the docked panel.
        var help = new HelpWindow(s);
        try
        {
            Check("the help window collects the menu", help.TopicCount > 200,
                  help.TopicCount + " entries");

            var missing = help.Undocumented();
            Check("no entry reaches the help undocumented", missing.Count == 0,
                  missing.Count == 0 ? "all described"
                                     : string.Join("; ", missing.ToArray()));
        }
        finally { help.Dispose(); }
    }

    static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Tooltip text is written with bare newlines, not the platform's pair.
        int n = text.IndexOf((char)10);
        return n < 0 ? text : text.Substring(0, n);
    }

    static System.Windows.Forms.ToolStripMenuItem FindItem(
        System.Windows.Forms.ToolStripItemCollection items, string startsWith)
    {
        foreach (System.Windows.Forms.ToolStripItem it in items)
        {
            var mi = it as System.Windows.Forms.ToolStripMenuItem;
            if (mi == null) continue;
            if (mi.Text != null && mi.Text.StartsWith(startsWith)) return mi;
            if (mi.HasDropDownItems)
            {
                var found = FindItem(mi.DropDownItems, startsWith);
                if (found != null) return found;
            }
        }
        return null;
    }

    static void CountTips(System.Windows.Forms.ToolStripItemCollection items,
                          ref int described, ref int total)
    {
        foreach (System.Windows.Forms.ToolStripItem it in items)
        {
            var mi = it as System.Windows.Forms.ToolStripMenuItem;
            if (mi == null) continue;
            total++;
            if (!string.IsNullOrEmpty(mi.Tag as string)) described++;
            if (mi.HasDropDownItems) CountTips(mi.DropDownItems, ref described, ref total);
        }
    }

    static void TestUserPresets()
    {
        Console.WriteLine("[user preset round trip]");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                            "NostalgiaPlusTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var a = new Settings();
            a.ApplyPreset(Preset.Bass);
            a.Contrast = 0.71;
            a.LabelFontSize = 11f;
            a.MirrorLeftPane = true;
            a.CurveWidthPct = 33;
            Check("saves under a name", a.SaveUserPreset(dir, "Round Trip"), "");

            string[] listed = Settings.ListUserPresets(dir);
            Check("appears in the listing", listed.Length == 1 && listed[0] == "Round Trip",
                  listed.Length + " found");

            // A fresh instance with deliberately different values, to prove the load
            // actually overwrites rather than merging.
            var b = new Settings();
            b.Contrast = 0.10;
            b.LabelFontSize = 6f;
            b.MirrorLeftPane = false;
            b.CurveWidthPct = 8;
            Check("loads back", b.LoadUserPreset(dir, "Round Trip"), "");

            Check("numeric value survives", Math.Abs(b.Contrast - 0.71) < 1e-9,
                  "Contrast " + b.Contrast.ToString("0.000"));
            Check("float value survives", Math.Abs(b.LabelFontSize - 11f) < 1e-6,
                  "LabelFontSize " + b.LabelFontSize);
            Check("bool value survives", b.MirrorLeftPane, "MirrorLeftPane " + b.MirrorLeftPane);
            Check("int value survives", b.CurveWidthPct == 33, "CurveWidthPct " + b.CurveWidthPct);
            Check("enum value survives", b.Scale == a.Scale && b.Quality == a.Quality,
                  b.Scale + " / " + b.Quality);
            Check("range survives", Math.Abs(b.FMax - 800) < 1e-9, "FMax " + b.FMax);

            Check("names are sanitised", Settings.SanitiseName("bad/name*?") == "badname",
                  "'" + Settings.SanitiseName("bad/name*?") + "'");
            Check("deletes", b.DeleteUserPreset(dir, "Round Trip") &&
                             Settings.ListUserPresets(dir).Length == 0, "");
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
        Console.WriteLine();
    }

    static double Average(double[] a, int from, int to)
    {
        if (from < 0) from = 0;
        if (to >= a.Length) to = a.Length - 1;
        double s = 0; int n = 0;
        for (int i = from; i <= to; i++) { s += a[i]; n++; }
        return n == 0 ? 0 : s / n;
    }

    static int PeakNear(double[] db, FrequencyMap map, double freq)
    {
        return PeakNear(db, map, freq, 40);
    }

    static int PeakNear(double[] db, FrequencyMap map, double freq, int window)
    {
        int centre = 0;
        double best = double.MaxValue;
        for (int i = 0; i < map.Width; i++)
        {
            double d = Math.Abs(map.Centres[i] - freq);
            if (d < best) { best = d; centre = i; }
        }
        if (window == 0) return centre;
        int lo = Math.Max(0, centre - window), hi = Math.Min(db.Length - 1, centre + window);
        int peak = lo;
        for (int i = lo; i <= hi; i++) if (db[i] > db[peak]) peak = i;
        return peak;
    }
}

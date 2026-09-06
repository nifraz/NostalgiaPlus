using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NostalgiaPlus.Dsp;
using NostalgiaPlus.Render;

namespace NostalgiaPlus
{
    // Values pinned and new members appended, so adding one never changes what an
    // existing number means.
    public enum Preset
    {
        Nostalgia = 0, Studio = 1, QC = 2, Immersive = 3, Custom = 4,
        Vocal = 5, Bass = 6, Percussion = 7, Mastering = 8
    }

    /// <summary>What the frequency axis prints at each gridline.</summary>
    public enum AxisLabelMode { Notes, Frequency, Both }

    /// <summary>Which end of the panes the reserved scale strip sits at.</summary>
    public enum ScaleLanePosition { Top, Bottom }

    /// <summary>Returns {title, artist, album}; any element may be null or empty.</summary>
    public delegate string[] NowPlayingProvider();

    /// <summary>
    /// Plain key=value settings. Kept human-editable on purpose - the plugin this
    /// replaces stores an XML blob, and hand-fixing a bad value there is unpleasant.
    /// </summary>
    public sealed class Settings
    {
        public Preset Preset = Preset.Studio;
        public PaletteKind Palette = PaletteKind.Magma;
        public FreqScale Scale = FreqScale.Note;
        public double FMin = 20.0;
        public double FMax = 20000.0;
        public AnalysisQuality Quality = AnalysisQuality.Balanced;
        public WindowType Window = WindowType.Hann;
        public BandAggregate Aggregate = BandAggregate.Peak;
        public double TiltDbPerOctave = 3.0;
        public bool AdaptiveRange = true;
        public double FloorDb = -95.0;
        public double CeilingDb = -5.0;
        // Anything prefixed Fs is genuinely fullscreen-only. Everything else is shared
        // by both views, so a change in one is visible in the other.
        public bool ShowGrid = true;
        public bool ShowLabels = true;
        public bool ShowColorBar = true;
        public bool ShowHud = true;
        public bool ShowStatus = true;
        public int TargetFps = 60;
        public double AttackMs = 20.0;
        public double ReleaseMs = 320.0;
        /// <summary>How fast the held maximum trace gives up its reading.</summary>
        public double PeakDecayDbPerSec = 14.0;
        /// <summary>Seconds for the average trace to follow a step change.</summary>
        public double AverageSeconds = 1.2;
        public bool UseLoopback = true;
        public int ScrollDivider = 1;

        // --- per-channel panes (shared by both modes) ---
        public ChannelPairMode PairMode = ChannelPairMode.LeftRight;
        public CurveStyle Style = CurveStyle.Line;
        public CurveInterpolation Interp = CurveInterpolation.LinearSmooth;
        public FilteringAmount Filter = FilteringAmount.Light;
        public bool ShowMax = true;
        public bool ShowMin = false;
        public bool ShowAvg = false;
        public bool SolidFill = true;
        public bool CurveOnLeft = true;
        /// <summary>
        /// Mirrors the left pane so both channels' newest slices meet at the centre and
        /// history flows outward - the sound appears to emerge from the middle.
        /// </summary>
        public bool MirrorLeftPane = false;
        /// <summary>Graph strip width as a percentage of each pane; 0 hides the graph.</summary>
        public int CurveWidthPct = 18;
        /// <summary>Waveform lane height as a percentage of the view; 0 hides it.</summary>
        public int WaveHeightPct = 10;

        // --- axes, grid and hover ---
        public GraphBackground Background = GraphBackground.Lines;
        public bool ShowDbScale = true;
        public bool ShowTimeMarks = true;
        public bool ShowSemitones = true;
        public bool ShowOuterLabels = true;
        /// <summary>Master switch for frequency axis labels in the gutter and at the edges.</summary>
        public bool ShowAxisLabels = true;
        public AxisLabelMode LabelMode = AxisLabelMode.Notes;
        /// <summary>Point size for axis, scale and readout text.</summary>
        public float LabelFontSize = 7f;
        /// <summary>Draw the hover line across both panes and read out both channels.</summary>
        public bool SyncHover = true;
        /// <summary>Stamp the hovered frequency onto the frequency axis itself.</summary>
        public bool ShowHoverPin = true;
        /// <summary>Ghost lines at integer multiples of the hovered frequency.</summary>
        public bool ShowHarmonics = false;
        /// <summary>Master switch for all fullscreen on-screen display.</summary>
        public bool FsShowOsd = true;

        // --- quick action bar (both views) ---
        /// <summary>Row of one-click cycling buttons along the bottom of the view.</summary>
        public bool ShowQuickButtons = true;
        /// <summary>Value-only buttons on one line, for short panels.</summary>
        public bool QuickBarCompact = false;

        // --- reserved scale strip ---
        /// <summary>
        /// Give the time and dB scales a strip of their own instead of printing them on
        /// chips over the image. Costs a little height and makes both scales legible
        /// against dense material, which the chips never quite managed.
        /// </summary>
        public bool ReserveScaleSpace = true;
        public ScaleLanePosition ScaleLanePos = ScaleLanePosition.Top;

        // --- centre deck (fullscreen) ---
        /// <summary>
        /// The block filling the gap between the two waveform lanes. Everything in it
        /// describes both channels at once, which is why it belongs to neither pane.
        /// </summary>
        public bool ShowCenterDeck = true;
        public bool DeckShowGoniometer = true;
        public bool DeckShowMeters = true;
        public bool DeckShowTransport = true;
        public bool DeckShowArtwork = true;
        /// <summary>Split the quick bar around the centre gutter so the axis runs unbroken.</summary>
        public bool QuickBarSplit = true;
        public int BarSize = 6;
        public int LedSegment = 5;
        /// <summary>
        /// Preferred height of the docked panel, reported to MusicBee when it creates the
        /// panel. Kept as a setting rather than a constant so it cannot silently overwrite
        /// a height stored in MusicBee's own layout on every launch.
        /// </summary>
        public int DockPanelHeight = 320;
        /// <summary>
        /// Low percentile for auto-ranging. Raising it blackens more of the noise floor,
        /// which is the main lever against dense material rendering as haze.
        /// </summary>
        public double Contrast = 0.25;

        // --- fullscreen mirrored stereo view ---
        /// <summary>Centre label gutter. Shared: both views lay panes out identically.</summary>
        public int GutterWidth = 34;
        public bool FsShowWaveform = true;
        public bool FsShowOverlays = true;
        /// <summary>Glow and auto-hiding furniture, for watching rather than measuring.</summary>
        public bool FsImmersive = false;
        public bool FsGlow = true;
        public bool FsAutoHide = true;

        /// <summary>
        /// Height of the reserved scale strip, in pixels. Derived from the text size
        /// rather than stored, so changing the font never leaves the strip too small
        /// for its own labels: points to pixels at 96 dpi is x1.333, plus line spacing
        /// and a little padding above and below.
        /// </summary>
        public int ScaleLaneHeight
        {
            get
            {
                if (!ReserveScaleSpace) return 0;
                float f = LabelFontSize;
                if (f < 5f) f = 5f; else if (f > 20f) f = 20f;
                return (int)(f * 1.9f) + 7;
            }
        }

        public void ApplyPreset(Preset p)
        {
            Preset = p;
            switch (p)
            {
                case Preset.Nostalgia:
                    // The original look, but with the mapping bugs left out.
                    Palette = PaletteKind.NostalgiaRed;
                    Scale = FreqScale.Linear;
                    FMin = 0; FMax = 22050;
                    Quality = AnalysisQuality.Balanced;
                    TiltDbPerOctave = 0.0;
                    Aggregate = BandAggregate.Peak;
                    AdaptiveRange = true;
                    FsImmersive = false;
                    break;

                case Preset.QC:
                    // Reading masters and spotting lossy sources: flat, wide, no tilt,
                    // linear top end so a codec shelf is obvious.
                    Palette = PaletteKind.Viridis;
                    Scale = FreqScale.Linear;
                    FMin = 0; FMax = 22050;
                    Quality = AnalysisQuality.High;
                    TiltDbPerOctave = 0.0;
                    Aggregate = BandAggregate.Energy;
                    AdaptiveRange = false;
                    FloorDb = -110; CeilingDb = 0;
                    FsImmersive = false;
                    break;

                case Preset.Immersive:
                    // Tuned for watching music rather than measuring it: musical axis,
                    // tilt so the top half is not haze, and enough resolution that
                    // harmonics resolve into lines instead of smear.
                    Palette = PaletteKind.Magma;
                    Scale = FreqScale.Note;
                    FMin = 25; FMax = 18000;
                    Quality = AnalysisQuality.High;
                    TiltDbPerOctave = 1.5;
                    Aggregate = BandAggregate.Peak;
                    AdaptiveRange = true;
                    Contrast = 0.55;
                    ScrollDivider = 2;
                    FsImmersive = true;
                    FsGlow = true;
                    FsAutoHide = true;
                    FsShowWaveform = true;
                    FsShowOverlays = true;
                    break;

                case Preset.Vocal:
                    // Where voices live, with enough resolution to separate formants.
                    Palette = PaletteKind.Magma;
                    Scale = FreqScale.Note;
                    FMin = 150; FMax = 9000;
                    Quality = AnalysisQuality.Balanced;
                    TiltDbPerOctave = 1.5;
                    Aggregate = BandAggregate.Peak;
                    AdaptiveRange = true; Contrast = 0.45;
                    ScrollDivider = 2;
                    FsImmersive = false;
                    break;

                case Preset.Bass:
                    // A narrow low range needs the largest transforms to resolve at all:
                    // a semitone at E1 is 2.4 Hz wide.
                    Palette = PaletteKind.Inferno;
                    Scale = FreqScale.Note;
                    FMin = 20; FMax = 800;
                    Quality = AnalysisQuality.High;
                    TiltDbPerOctave = 0.0;
                    Aggregate = BandAggregate.Peak;
                    AdaptiveRange = true; Contrast = 0.35;
                    ScrollDivider = 2;
                    FsImmersive = false;
                    break;

                case Preset.Percussion:
                    // Transients are about timing, so latency and scroll speed matter
                    // more than frequency detail.
                    Palette = PaletteKind.Turbo;
                    Scale = FreqScale.Note;
                    FMin = 40; FMax = 18000;
                    Quality = AnalysisQuality.LowLatency;
                    TiltDbPerOctave = 3.0;
                    Aggregate = BandAggregate.Peak;
                    AdaptiveRange = true; Contrast = 0.60;
                    ScrollDivider = 1;
                    FsImmersive = false;
                    break;

                case Preset.Mastering:
                    // Measurement rather than viewing: nothing tilted, nothing adaptive,
                    // energy summed so noise floors read at their true level.
                    Palette = PaletteKind.Viridis;
                    Scale = FreqScale.Linear;
                    FMin = 0; FMax = 22050;
                    Quality = AnalysisQuality.High;
                    TiltDbPerOctave = 0.0;
                    Aggregate = BandAggregate.Energy;
                    AdaptiveRange = false;
                    FloorDb = -120; CeilingDb = 0;
                    ScrollDivider = 2;
                    FsImmersive = false;
                    break;

                default: // Studio
                    Palette = PaletteKind.Magma;
                    Scale = FreqScale.Note;
                    FMin = 20; FMax = 20000;
                    Quality = AnalysisQuality.Balanced;
                    TiltDbPerOctave = 3.0;
                    Aggregate = BandAggregate.Peak;
                    AdaptiveRange = true;
                    FsImmersive = false;
                    break;
            }
        }

        // ---- persistence ----

        private static string PathFor(string storageDir)
        {
            return Path.Combine(storageDir, "NostalgiaPlus.settings");
        }

        // ---- user presets ----

        private static string PresetDir(string storageDir)
        {
            return Path.Combine(storageDir, "Presets");
        }

        /// <summary>Strips anything that cannot appear in a file name.</summary>
        public static string SanitiseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.Trim())
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_') sb.Append(c);
            return sb.ToString().Trim();
        }

        public static string[] ListUserPresets(string storageDir)
        {
            try
            {
                string dir = PresetDir(storageDir);
                if (!Directory.Exists(dir)) return new string[0];
                string[] files = Directory.GetFiles(dir, "*.settings");
                var names = new string[files.Length];
                for (int i = 0; i < files.Length; i++)
                    names[i] = Path.GetFileNameWithoutExtension(files[i]);
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                return names;
            }
            catch { return new string[0]; }
        }

        public bool SaveUserPreset(string storageDir, string name)
        {
            name = SanitiseName(name);
            if (name.Length == 0) return false;
            try
            {
                string dir = PresetDir(storageDir);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                SaveTo(Path.Combine(dir, name + ".settings"));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Copies a stored preset over this instance. Both views hold a reference to the
        /// same Settings object, so the values are replaced in place rather than the
        /// object being swapped - otherwise one view would keep the old one.
        /// </summary>
        public bool LoadUserPreset(string storageDir, string name)
        {
            try
            {
                string file = Path.Combine(PresetDir(storageDir), SanitiseName(name) + ".settings");
                if (!File.Exists(file)) return false;
                Settings loaded = LoadFile(file);
                foreach (var f in typeof(Settings).GetFields(
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.Instance))
                    f.SetValue(this, f.GetValue(loaded));
                return true;
            }
            catch { return false; }
        }

        public bool DeleteUserPreset(string storageDir, string name)
        {
            try
            {
                string file = Path.Combine(PresetDir(storageDir), SanitiseName(name) + ".settings");
                if (!File.Exists(file)) return false;
                File.Delete(file);
                return true;
            }
            catch { return false; }
        }

        public static Settings Load(string storageDir)
        {
            return LoadFile(PathFor(storageDir));
        }

        /// <summary>Reads any settings file; user presets use the same format.</summary>
        public static Settings LoadFile(string file)
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(file)) return s;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                s.Preset = ParseEnum(map, "Preset", s.Preset);
                s.Palette = ParseEnum(map, "Palette", s.Palette);
                s.Scale = ParseEnum(map, "Scale", s.Scale);
                s.Quality = ParseEnum(map, "Quality", s.Quality);
                s.Window = ParseEnum(map, "Window", s.Window);
                s.Aggregate = ParseEnum(map, "Aggregate", s.Aggregate);
                s.FMin = ParseDouble(map, "FMin", s.FMin);
                s.FMax = ParseDouble(map, "FMax", s.FMax);
                s.TiltDbPerOctave = ParseDouble(map, "Tilt", s.TiltDbPerOctave);
                s.FloorDb = ParseDouble(map, "FloorDb", s.FloorDb);
                s.CeilingDb = ParseDouble(map, "CeilingDb", s.CeilingDb);
                s.AttackMs = ParseDouble(map, "AttackMs", s.AttackMs);
                s.ReleaseMs = ParseDouble(map, "ReleaseMs", s.ReleaseMs);
                s.PeakDecayDbPerSec = ParseDouble(map, "PeakDecay", s.PeakDecayDbPerSec);
                s.AverageSeconds = ParseDouble(map, "AverageSeconds", s.AverageSeconds);
                s.AdaptiveRange = ParseBool(map, "AdaptiveRange", s.AdaptiveRange);
                s.ShowGrid = ParseBool(map, "ShowGrid", s.ShowGrid);
                s.ShowLabels = ParseBool(map, "ShowLabels", s.ShowLabels);
                s.ShowColorBar = ParseBool(map, "ShowColorBar", s.ShowColorBar);
                s.ShowHud = ParseBool(map, "ShowHud", s.ShowHud);
                s.ShowStatus = ParseBool(map, "ShowStatus", s.ShowStatus);
                s.UseLoopback = ParseBool(map, "UseLoopback", s.UseLoopback);
                s.TargetFps = (int)ParseDouble(map, "TargetFps", s.TargetFps);
                s.ScrollDivider = (int)ParseDouble(map, "ScrollDivider", s.ScrollDivider);
                s.PairMode = ParseEnum(map, "PairMode", s.PairMode);
                s.Style = ParseEnum(map, "Style", s.Style);
                s.Interp = ParseEnum(map, "Interp", s.Interp);
                s.Filter = ParseEnum(map, "Filter", s.Filter);
                s.ShowMax = ParseBool(map, "ShowMax", s.ShowMax);
                s.ShowMin = ParseBool(map, "ShowMin", s.ShowMin);
                s.ShowAvg = ParseBool(map, "ShowAvg", s.ShowAvg);
                s.SolidFill = ParseBool(map, "SolidFill", s.SolidFill);
                s.CurveOnLeft = ParseBool(map, "CurveOnLeft", s.CurveOnLeft);
                s.MirrorLeftPane = ParseBool(map, "MirrorLeftPane", s.MirrorLeftPane);
                s.CurveWidthPct = (int)ParseDouble(map, "CurveWidthPct", s.CurveWidthPct);
                s.WaveHeightPct = (int)ParseDouble(map, "WaveHeightPct", s.WaveHeightPct);
                s.Background = ParseEnum(map, "Background", s.Background);
                s.ShowDbScale = ParseBool(map, "ShowDbScale", s.ShowDbScale);
                s.ShowTimeMarks = ParseBool(map, "ShowTimeMarks", s.ShowTimeMarks);
                s.ShowSemitones = ParseBool(map, "ShowSemitones", s.ShowSemitones);
                s.ShowOuterLabels = ParseBool(map, "ShowOuterLabels", s.ShowOuterLabels);
                s.ShowAxisLabels = ParseBool(map, "ShowAxisLabels", s.ShowAxisLabels);
                s.LabelMode = ParseEnum(map, "LabelMode", s.LabelMode);
                s.LabelFontSize = (float)ParseDouble(map, "LabelFontSize", s.LabelFontSize);
                s.SyncHover = ParseBool(map, "SyncHover", s.SyncHover);
                s.ShowHoverPin = ParseBool(map, "ShowHoverPin", s.ShowHoverPin);
                s.ShowHarmonics = ParseBool(map, "ShowHarmonics", s.ShowHarmonics);
                s.FsShowOsd = ParseBool(map, "FsShowOsd", s.FsShowOsd);
                s.BarSize = (int)ParseDouble(map, "BarSize", s.BarSize);
                s.LedSegment = (int)ParseDouble(map, "LedSegment", s.LedSegment);
                s.DockPanelHeight = (int)ParseDouble(map, "DockPanelHeight", s.DockPanelHeight);
                s.Contrast = ParseDouble(map, "Contrast", s.Contrast);
                s.GutterWidth = (int)ParseDouble(map, "GutterWidth", s.GutterWidth);
                s.ShowQuickButtons = ParseBool(map, "ShowQuickButtons", s.ShowQuickButtons);
                s.QuickBarCompact = ParseBool(map, "QuickBarCompact", s.QuickBarCompact);
                s.ReserveScaleSpace = ParseBool(map, "ReserveScaleSpace", s.ReserveScaleSpace);
                s.ScaleLanePos = ParseEnum(map, "ScaleLanePos", s.ScaleLanePos);
                s.ShowCenterDeck = ParseBool(map, "ShowCenterDeck", s.ShowCenterDeck);
                s.DeckShowGoniometer = ParseBool(map, "DeckShowGoniometer", s.DeckShowGoniometer);
                s.DeckShowMeters = ParseBool(map, "DeckShowMeters", s.DeckShowMeters);
                s.DeckShowTransport = ParseBool(map, "DeckShowTransport", s.DeckShowTransport);
                s.DeckShowArtwork = ParseBool(map, "DeckShowArtwork", s.DeckShowArtwork);
                s.QuickBarSplit = ParseBool(map, "QuickBarSplit", s.QuickBarSplit);
                s.FsShowWaveform = ParseBool(map, "FsShowWaveform", s.FsShowWaveform);
                s.FsShowOverlays = ParseBool(map, "FsShowOverlays", s.FsShowOverlays);
                s.FsImmersive = ParseBool(map, "FsImmersive", s.FsImmersive);
                s.FsGlow = ParseBool(map, "FsGlow", s.FsGlow);
                s.FsAutoHide = ParseBool(map, "FsAutoHide", s.FsAutoHide);
            }
            catch { /* a corrupt file should never stop the panel from opening */ }
            return s;
        }

        public void Save(string storageDir)
        {
            SaveTo(PathFor(storageDir));
        }

        public void SaveTo(string file)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# Nostalgia+ settings");
                sb.AppendLine("Preset=" + Preset);
                sb.AppendLine("Palette=" + Palette);
                sb.AppendLine("Scale=" + Scale);
                sb.AppendLine("Quality=" + Quality);
                sb.AppendLine("Window=" + Window);
                sb.AppendLine("Aggregate=" + Aggregate);
                sb.AppendLine("FMin=" + Inv(FMin));
                sb.AppendLine("FMax=" + Inv(FMax));
                sb.AppendLine("Tilt=" + Inv(TiltDbPerOctave));
                sb.AppendLine("FloorDb=" + Inv(FloorDb));
                sb.AppendLine("CeilingDb=" + Inv(CeilingDb));
                sb.AppendLine("AttackMs=" + Inv(AttackMs));
                sb.AppendLine("ReleaseMs=" + Inv(ReleaseMs));
                sb.AppendLine("PeakDecay=" + Inv(PeakDecayDbPerSec));
                sb.AppendLine("AverageSeconds=" + Inv(AverageSeconds));
                sb.AppendLine("AdaptiveRange=" + AdaptiveRange);
                sb.AppendLine("ShowGrid=" + ShowGrid);
                sb.AppendLine("ShowLabels=" + ShowLabels);
                sb.AppendLine("ShowColorBar=" + ShowColorBar);
                sb.AppendLine("ShowHud=" + ShowHud);
                sb.AppendLine("ShowStatus=" + ShowStatus);
                sb.AppendLine("UseLoopback=" + UseLoopback);
                sb.AppendLine("TargetFps=" + TargetFps);
                sb.AppendLine("ScrollDivider=" + ScrollDivider);
                sb.AppendLine("PairMode=" + PairMode);
                sb.AppendLine("Style=" + Style);
                sb.AppendLine("Interp=" + Interp);
                sb.AppendLine("Filter=" + Filter);
                sb.AppendLine("ShowMax=" + ShowMax);
                sb.AppendLine("ShowMin=" + ShowMin);
                sb.AppendLine("ShowAvg=" + ShowAvg);
                sb.AppendLine("SolidFill=" + SolidFill);
                sb.AppendLine("CurveOnLeft=" + CurveOnLeft);
                sb.AppendLine("MirrorLeftPane=" + MirrorLeftPane);
                sb.AppendLine("CurveWidthPct=" + CurveWidthPct);
                sb.AppendLine("WaveHeightPct=" + WaveHeightPct);
                sb.AppendLine("Background=" + Background);
                sb.AppendLine("ShowDbScale=" + ShowDbScale);
                sb.AppendLine("ShowTimeMarks=" + ShowTimeMarks);
                sb.AppendLine("ShowSemitones=" + ShowSemitones);
                sb.AppendLine("ShowOuterLabels=" + ShowOuterLabels);
                sb.AppendLine("ShowAxisLabels=" + ShowAxisLabels);
                sb.AppendLine("LabelMode=" + LabelMode);
                sb.AppendLine("LabelFontSize=" + Inv(LabelFontSize));
                sb.AppendLine("SyncHover=" + SyncHover);
                sb.AppendLine("ShowHoverPin=" + ShowHoverPin);
                sb.AppendLine("ShowHarmonics=" + ShowHarmonics);
                sb.AppendLine("FsShowOsd=" + FsShowOsd);
                sb.AppendLine("BarSize=" + BarSize);
                sb.AppendLine("LedSegment=" + LedSegment);
                sb.AppendLine("DockPanelHeight=" + DockPanelHeight);
                sb.AppendLine("Contrast=" + Inv(Contrast));
                sb.AppendLine("GutterWidth=" + GutterWidth);
                sb.AppendLine("ShowQuickButtons=" + ShowQuickButtons);
                sb.AppendLine("QuickBarCompact=" + QuickBarCompact);
                sb.AppendLine("ReserveScaleSpace=" + ReserveScaleSpace);
                sb.AppendLine("ScaleLanePos=" + ScaleLanePos);
                sb.AppendLine("ShowCenterDeck=" + ShowCenterDeck);
                sb.AppendLine("DeckShowGoniometer=" + DeckShowGoniometer);
                sb.AppendLine("DeckShowMeters=" + DeckShowMeters);
                sb.AppendLine("DeckShowTransport=" + DeckShowTransport);
                sb.AppendLine("DeckShowArtwork=" + DeckShowArtwork);
                sb.AppendLine("QuickBarSplit=" + QuickBarSplit);
                sb.AppendLine("FsShowWaveform=" + FsShowWaveform);
                sb.AppendLine("FsShowOverlays=" + FsShowOverlays);
                sb.AppendLine("FsImmersive=" + FsImmersive);
                sb.AppendLine("FsGlow=" + FsGlow);
                sb.AppendLine("FsAutoHide=" + FsAutoHide);
                File.WriteAllText(file, sb.ToString());
            }
            catch { }
        }

        private static string Inv(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        private static T ParseEnum<T>(Dictionary<string, string> map, string key, T fallback)
        {
            string v;
            if (!map.TryGetValue(key, out v)) return fallback;
            try { return (T)Enum.Parse(typeof(T), v, true); }
            catch { return fallback; }
        }

        private static double ParseDouble(Dictionary<string, string> map, string key, double fallback)
        {
            string v;
            double d;
            if (map.TryGetValue(key, out v) &&
                double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }

        private static bool ParseBool(Dictionary<string, string> map, string key, bool fallback)
        {
            string v;
            bool b;
            if (map.TryGetValue(key, out v) && bool.TryParse(v, out b)) return b;
            return fallback;
        }
    }
}

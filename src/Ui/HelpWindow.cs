using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// The help window: every menu entry, with its explanation, in one searchable place.
    ///
    /// The explanations are not written here. They live on the menu items themselves, in
    /// <see cref="MenuFactory"/>, and this walks a menu built for the purpose to collect
    /// them. That is the whole point of the arrangement: an explanation sits beside the
    /// code that implements the option, so adding an option and documenting it are the
    /// same edit, and the two cannot drift apart. A test fails if any item arrives here
    /// without a description.
    ///
    /// Only the overview sections at the front are written here, because they describe
    /// the display rather than any one menu entry.
    /// </summary>
    public sealed class HelpWindow : Form
    {
        private sealed class Topic
        {
            public string Title;
            public string Text;
            public int Depth;
            public string Section;
        }

        private readonly List<Topic> _all = new List<Topic>();
        private readonly TreeView _tree = new TreeView();
        private readonly RichTextBox _body = new RichTextBox();
        private readonly TextBox _search = new TextBox();
        private readonly Label _hint = new Label();

        private static readonly Color Ink = Color.FromArgb(226, 226, 232);
        private static readonly Color Dim = Color.FromArgb(150, 152, 160);
        private static readonly Color Accent = Color.FromArgb(150, 200, 245);
        private static readonly Color Ground = Color.FromArgb(24, 24, 28);
        private static readonly Color Panel = Color.FromArgb(18, 18, 22);

        public HelpWindow(Settings settings)
        {
            Text = "Nostalgia+ — Help";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1000, 700);
            MinimumSize = new Size(720, 460);
            BackColor = Ground;
            ForeColor = Ink;
            KeyPreview = true;
            ShowInTaskbar = false;

            CollectOverview();
            CollectFromMenu(settings);

            _search.SetBounds(12, 12, 300, 26);
            _search.BackColor = Panel;
            _search.ForeColor = Ink;
            _search.BorderStyle = BorderStyle.FixedSingle;
            _search.Font = new Font("Segoe UI", 9.5f);
            _search.TextChanged += delegate { Rebuild(_search.Text); };

            _hint.SetBounds(320, 16, 660, 20);
            _hint.ForeColor = Dim;
            _hint.Font = new Font("Segoe UI", 9f);
            _hint.Text = "Type to filter. Esc closes.";

            _tree.SetBounds(12, 48, 300, ClientSize.Height - 60);
            _tree.BackColor = Panel;
            _tree.ForeColor = Ink;
            _tree.BorderStyle = BorderStyle.FixedSingle;
            _tree.Font = new Font("Segoe UI", 9.5f);
            _tree.HideSelection = false;
            _tree.FullRowSelect = true;
            // A flat list of sections: the lines, glyphs and indentation of a tree
            // control suggest a hierarchy that is not there.
            _tree.ShowLines = false;
            _tree.ShowRootLines = false;
            _tree.ShowPlusMinus = false;
            _tree.Indent = 8;
            _tree.ItemHeight = 24;
            // Owner-drawn text so the selection is not the system's light blue on a
            // dark control, which is unreadable.
            _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _tree.DrawNode += DrawSectionNode;
            _tree.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            _tree.AfterSelect += delegate { Show(_tree.SelectedNode); };

            _body.SetBounds(324, 48, ClientSize.Width - 336, ClientSize.Height - 60);
            _body.BackColor = Panel;
            _body.ForeColor = Ink;
            _body.BorderStyle = BorderStyle.FixedSingle;
            _body.ReadOnly = true;
            _body.DetectUrls = false;
            _body.Font = new Font("Segoe UI", 10f);
            _body.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            Controls.Add(_search);
            Controls.Add(_hint);
            Controls.Add(_tree);
            Controls.Add(_body);

            Rebuild("");
        }

        private void DrawSectionNode(object sender, DrawTreeNodeEventArgs e)
        {
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            var row = new Rectangle(0, e.Bounds.Y, _tree.ClientSize.Width, e.Bounds.Height);
            using (var back = new SolidBrush(selected ? Color.FromArgb(44, 48, 58) : Panel))
                e.Graphics.FillRectangle(back, row);
            if (selected)
                using (var edge = new SolidBrush(Accent))
                    e.Graphics.FillRectangle(edge, 0, row.Y, 3, row.Height);
            using (var ink = new SolidBrush(selected ? Ink : Dim))
                e.Graphics.DrawString(e.Node.Text, _tree.Font, ink, 12, e.Bounds.Y + 4);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            // Ctrl+F is where everyone's hands go; there is only one box to go to.
            if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); _search.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- content ----------------

        private void Add(string section, string title, string text, int depth)
        {
            var t = new Topic();
            t.Section = section; t.Title = title; t.Text = text; t.Depth = depth;
            _all.Add(t);
        }

        /// <summary>
        /// The parts that describe the display itself. Nothing in the menu covers these,
        /// and they are what someone opening the plugin for the first time actually needs
        /// before any individual option means anything.
        /// </summary>
        private void CollectOverview()
        {
            const string S = "Reading the display";

            Add(S, "The two panes",
                "The view is split into one pane per channel, left and right, each showing the same "
                + "thing for its own channel: a spectrogram and, beside it, the instantaneous spectrum "
                + "as a curve or bars.\n\n"
                + "Both panes share a single frequency axis, which runs vertically - low notes at the "
                + "bottom, high at the top - so a horizontal line across the screen is one frequency in "
                + "both channels, and the two can be compared by eye without counting rows.\n\n"
                + "Channels can instead be shown as Mid and Side: Mid is what is common to both "
                + "speakers, Side is what differs. Side carrying a lot of bass is usually a "
                + "mono-compatibility problem. The single-channel modes give one channel the full width.", 1);

            Add(S, "Which way time runs",
                "New spectrogram columns enter against the graph and age away from it. That keeps the "
                + "live trace next to the slice of image that produced it, so the curve is a legend for "
                + "the newest column rather than a separate instrument.\n\n"
                + "Which side the graph sits on is a setting, and it therefore also sets which way "
                + "history flows. Mirror left pane flips the left one, so both channels' newest columns "
                + "meet at the centre and history spreads outward to both edges - the sound appears to "
                + "emerge from the middle. Under Mirror the two time scales count outward in opposite "
                + "directions, which is why each pane carries its own.", 1);

            Add(S, "The scales",
                "Three scales meet on screen, and each is named once at the end it is measured from.\n\n"
                + "Frequency runs up the axis columns - the two outer margins and the centre gutter - "
                + "captioned Hz, or note when the labels are note names. Labels sit centred on the row "
                + "they name, and a row too close to the top or bottom edge to carry a centred label "
                + "keeps its gridline but loses its number, so the spacing stays even.\n\n"
                + "Level runs across each graph strip, captioned dBFS - decibels relative to full scale, "
                + "so 0 is the loudest a sample can be and everything else is negative. It grows from "
                + "the graph's baseline toward the spectrogram.\n\n"
                + "Time runs across each spectrogram, counted back in seconds from the live edge, which "
                + "is captioned now.", 1);

            Add(S, "Colour, and what it means",
                "The spectrogram's colour is level. Which colour means which level depends on the "
                + "current range: with auto dynamic range on, the ends track what is actually playing, "
                + "so quiet passages stay visible but the same colour does not mean the same dB from one "
                + "moment to the next. Turn it off, or choose a fixed range, to compare absolute levels "
                + "between tracks.\n\n"
                + "The docked panel's colour bar shows the ramp with the current floor and ceiling "
                + "printed at its ends.\n\n"
                + "All the palettes except the last are perceptually uniform: equal steps in level look "
                + "like equal steps in colour, so the picture does not invent contrast that is not in "
                + "the data. Turbo is the exception among them and can suggest edges that are not real.", 1);

            Add(S, "Tilt, contrast and what they change",
                "Two settings change the picture without changing the measurement, and between them "
                + "they account for most of the difference between a readable display and a wash.\n\n"
                + "Spectral tilt lifts the top of the spectrum before it is drawn. Music falls off at "
                + "roughly 3 dB per octave, so with no tilt the treble sits near the bottom of the "
                + "colour range and reads as haze. A tilt of about 3 dB per octave makes the whole "
                + "range use the palette.\n\n"
                + "Contrast sets where black begins, as a percentile of the current spectrum. Raising "
                + "it blackens more of the noise floor. It is the main lever against dense material "
                + "looking like fog, and the main way to lose quiet detail if pushed too far.", 1);

            Add(S, "The centre deck",
                "The gap between the two waveform lanes belongs to neither channel, so it holds the "
                + "things that describe the pair.\n\n"
                + "The goniometer plots left against right, rotated so mono reads as a vertical line. A "
                + "circle is a wide image, a horizontal line means the channels are out of phase, and a "
                + "lean to one side is a level imbalance.\n\n"
                + "Correlation says the same thing as a number: +1 is mono, 0 is uncorrelated, and "
                + "negative means the channels partly cancel and the track will lose material when "
                + "summed to mono. Balance is simply which side is louder.\n\n"
                + "Alongside them are the album art and what is playing, the transport with elapsed "
                + "time and a seek bar you can click, and nine readouts.\n\n"
                + "Four of those describe loudness right now: LUFS momentary and short term, true "
                + "peak, and crest factor. True peak turns red above -1 dBTP, where a lossy encoder "
                + "will clip even though the samples never did.\n\n"
                + "Three describe the track as a whole, and build up as it plays. Integrated LUFS is "
                + "the gated figure a track is quoted at - around -14 is what Spotify normalises to, "
                + "-16 Apple. Loudness range is how much it moves, in LU: under 3 is flattened, 8 or "
                + "more has its dynamics. Overs counts how many times true peak passed -1 dBTP, with "
                + "the time of the last one in the caption; excursions within 200ms count once, so a "
                + "master sitting on the ceiling reads five a second rather than five thousand. All "
                + "three restart when the track changes.\n\n"
                + "The last two describe the music rather than the master: tempo in BPM, and "
                + "brightness - where the energy is sitting, as a frequency. Both show -- when there "
                + "is no confident answer. Brightness is worth watching move rather than reading: it "
                + "climbing through a build is the cymbals arriving.\n\n"
                + "The title and those readouts used to float in the top corners of the screen, on a "
                + "bar painted over the top of both spectrograms. They cost the image nothing here, "
                + "and the repeated frequency labels now run the full height of the axis instead of "
                + "starting 84 pixels down to stay clear of the bar.\n\n"
                + "Every item has its own switch under View - Centre deck contents. The deck only "
                + "gets the gap the two graph strips leave, so it holds what fits and drops the rest: "
                + "artwork first, then the loudness columns, then the title, and the transport block "
                + "last. The goniometer always stays. Widen the graph strips to make room for more, "
                + "or press O to hide the deck altogether.", 1);

            Add(S, "Mouse and keyboard",
                "Click anywhere on the image to freeze it, and click again to release. Analysis keeps "
                + "running while frozen; only the picture is held.\n\n"
                + "Hover to read the frequency, note name and cents under the pointer, with the level in "
                + "each channel. Over the spectrogram it reads the moment you are pointing at, not the "
                + "live spectrum - so pointing ten seconds back reports what happened then.\n\n"
                + "Drag to measure: the readout reports the interval in semitones and the time between "
                + "the two points.\n\n"
                + "Double-click a spectrogram column to jump the player to the moment that produced "
                + "it. The image is a timeline with far more detail than a seek bar has - you can aim "
                + "at one hit. Freeze first if you want to take your time: the position the jump is "
                + "measured back from is stamped when the image stops, not when you click, so a frozen "
                + "picture still lands where you point. Only the spectrograms respond; the curve "
                + "strips and the label columns are not a timeline.\n\n"
                + "Press A to hold the current average spectrum as an amber line, and leave it there "
                + "while the music moves under it. That is how to answer \"is this master brighter "
                + "than that one\" without trusting your memory of a curve from thirty seconds ago: "
                + "take it on one track, start the other, and compare. A again drops it.\n\n"
                + "Keys, in the fullscreen view: F11 or Esc leaves, Space freezes, A holds or drops "
                + "the comparison curve, I toggles immersive mode, H hides the on-screen display, O "
                + "the centre deck, W the waveform lanes, G the gridlines, M mirror, B the graph "
                + "style, C the channel mode, P the palette. F1 opens this window. In the docked "
                + "panel, F11 goes fullscreen, Space freezes and A compares.", 1);

            Add(S, "Where your settings are kept",
                "Settings are stored as plain key=value text under MusicBee's persistent storage path, "
                + "so a bad value can be fixed in a text editor rather than by resetting everything.\n\n"
                + "Saved presets live in a Presets folder beside that file and carry every setting. "
                + "Saved themes live in a Themes folder and carry colours only, which is why a theme can "
                + "be applied over any preset without dragging that preset's analysis settings with it.\n\n"
                + "Changing anything a preset defines switches the preset to Custom. That is not a "
                + "warning, just a label - but it is why saving a configuration you like is worth doing "
                + "before you carry on adjusting.", 1);
        }

        /// <summary>
        /// Walks a menu built for the purpose, taking each item's title and the
        /// explanation left on its Tag.
        /// </summary>
        private void CollectFromMenu(Settings settings)
        {
            var menu = new ContextMenuStrip();
            try
            {
                MenuFactory.Populate(menu, settings ?? new Settings(), new MenuFactory.Options
                {
                    IsFullscreen = false,
                    ScrollPixels = 900,
                    IsFrozen = delegate { return false; },
                    ToggleFreeze = delegate { },
                    // Entries that appear only when the view wires them up still have to
                    // be collected, or the one item nobody documented is the one item
                    // this check cannot see.
                    ToggleSnapshot = delegate { },
                    HasSnapshot = delegate { return false; },
                    Changed = delegate(bool rebuild) { },
                });
                Walk(menu.Items, null, 0);

                // The fullscreen half of the View menu is built only when the menu is
                // opened from that view, so it is collected from a second pass. Without
                // it, everything immersive would be undocumented here.
                var fs = new ContextMenuStrip();
                try
                {
                    MenuFactory.Populate(fs, settings ?? new Settings(), new MenuFactory.Options
                    {
                        IsFullscreen = true,
                        ScrollPixels = 900,
                        IsFrozen = delegate { return false; },
                        ToggleFreeze = delegate { },
                        ToggleImmersive = delegate { },
                        Changed = delegate(bool rebuild) { },
                    });
                    foreach (ToolStripItem it in fs.Items)
                    {
                        var mi = it as ToolStripMenuItem;
                        if (mi != null && mi.Text == "View")
                        {
                            Walk(mi.DropDownItems, "View (fullscreen)", 1);
                            break;
                        }
                    }
                }
                finally { fs.Dispose(); }
            }
            finally { menu.Dispose(); }
        }

        private void Walk(ToolStripItemCollection items, string section, int depth)
        {
            foreach (ToolStripItem it in items)
            {
                var mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                string title = Clean(mi.Text);
                string sec = depth == 0 ? title : section;
                string text = mi.Tag as string;
                if (!string.IsNullOrEmpty(text) || depth > 0 || mi.HasDropDownItems)
                    Add(sec, title, text ?? "", depth);
                if (mi.HasDropDownItems) Walk(mi.DropDownItems, sec, depth + 1);
            }
        }

        /// <summary>Strips the shortcut hints, which belong in the keyboard section.</summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int i = text.IndexOf("  (");
            return (i > 0 ? text.Substring(0, i) : text).Trim();
        }

        // ---------------- presentation ----------------

        private void Rebuild(string filter)
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            filter = (filter ?? "").Trim();

            TreeNode current = null;
            string section = null;
            int shown = 0;
            foreach (Topic t in _all)
            {
                bool match = filter.Length == 0 || Matches(t, filter);
                if (!match) continue;
                if (t.Section != section)
                {
                    section = t.Section;
                    current = _tree.Nodes.Add(section);
                    current.Tag = section;
                }
                shown++;
            }

            _hint.Text = filter.Length == 0
                ? "Type to filter.  Ctrl+F searches.  Esc closes."
                : shown + (shown == 1 ? " entry matches " : " entries match ") + "\"" + filter + "\"";

            _tree.EndUpdate();
            _filter = filter;
            if (_tree.Nodes.Count > 0)
            {
                _tree.SelectedNode = _tree.Nodes[0];
                // Called rather than left to AfterSelect: during construction the tree
                // has no handle yet, so assigning the selection raises no event and the
                // window opened with an empty page.
                Show(_tree.Nodes[0]);
            }
            else _body.Clear();
        }

        private string _filter = "";

        private static bool Matches(Topic t, string filter)
        {
            return (t.Title != null && t.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                || (t.Text != null && t.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                || (t.Section != null && t.Section.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void Show(TreeNode node)
        {
            if (node == null) { _body.Clear(); return; }
            string section = node.Tag as string;

            _body.Clear();
            AppendLine(section, new Font("Segoe UI Light", 17f), Ink, 8);

            foreach (Topic t in _all)
            {
                if (t.Section != section) continue;
                if (_filter.Length > 0 && !Matches(t, _filter)) continue;

                // The heading above already carries the section's own name, so a
                // top-level entry contributes its text and not its title a second time.
                string indent = new string(' ', Math.Max(0, (t.Depth - 1) * 3));
                if (t.Depth > 0)
                    AppendLine(indent + t.Title,
                               new Font("Segoe UI Semibold", t.Depth <= 1 ? 11.5f : 10.5f),
                               t.Depth <= 1 ? Accent : Ink, 10);
                if (t.Text.Length > 0)
                    AppendLine(indent + t.Text.Replace("\n", "\n" + indent),
                               new Font("Segoe UI", 10f), t.Depth <= 1 ? Ink : Dim, 4);
            }
            _body.SelectionStart = 0;
            _body.ScrollToCaret();
        }

        private void AppendLine(string text, Font font, Color colour, int spaceAfter)
        {
            _body.SelectionFont = font;
            _body.SelectionColor = colour;
            _body.AppendText(text + "\n");
            // A blank line at the smaller size, rather than real paragraph spacing, which
            // RichTextBox only exposes through RTF.
            _body.SelectionFont = new Font("Segoe UI", spaceAfter / 2f + 1f);
            _body.AppendText("\n");
        }

        /// <summary>Number of entries collected. Used by the tests.</summary>
        public int TopicCount { get { return _all.Count; } }

        /// <summary>Entries that arrived with no explanation, for the tests to fail on.</summary>
        public List<string> Undocumented()
        {
            var missing = new List<string>();
            foreach (Topic t in _all)
                if (t.Depth > 0 && string.IsNullOrEmpty(t.Text)) missing.Add(t.Section + " > " + t.Title);
            return missing;
        }
    }
}

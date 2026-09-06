using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NostalgiaPlus;
using NostalgiaPlus.Ui;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private const short PluginInfoVersionValue = 1;
        // The reference panel plugin on this host declares 43/57. Declaring 1/1 risks
        // MusicBee applying legacy panel semantics, and the host is known to satisfy
        // these since that plugin runs here.
        private const short MinInterfaceVersionValue = 43;
        private const short MinApiRevisionValue = 57;

        private MusicBeeApiInterface _mb;
        private readonly PluginInfo _about = new PluginInfo();
        private Settings _settings;
        private AnalyzerPanel _panel;
        private Control _hostPanel;
        private string _storageDir;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mb = (MusicBeeApiInterface)Marshal.PtrToStructure(apiInterfacePtr, typeof(MusicBeeApiInterface));

            _about.PluginInfoVersion = PluginInfoVersionValue;
            _about.Name = "Nostalgia+";
            _about.Description = "Multi-resolution spectrum analyser and spectrogram with a musical frequency axis, perceptual colour ramps and adaptive dynamic range.";
            _about.Author = "Nifraz Navahz";
            _about.TargetApplication = "";
            _about.Type = PluginType.PanelView;
            _about.VersionMajor = 1;
            _about.VersionMinor = 0;
            _about.Revision = 0;
            _about.MinInterfaceVersion = MinInterfaceVersionValue;
            _about.MinApiRevision = MinApiRevisionValue;
            _about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            _about.ConfigurationPanelHeight = 0;

            _storageDir = ResolveStorageDir();
            _settings = Settings.Load(_storageDir);

            // Bindable from MusicBee's own hotkey preferences, so the fullscreen view is
            // reachable even when the panel does not hold keyboard focus.
            try
            {
                if (_mb.MB_RegisterCommand != null)
                    _mb.MB_RegisterCommand("Nostalgia+: Toggle fullscreen stereo view",
                                           OnToggleFullscreenCommand);
            }
            catch { }

            return _about;
        }

        private string ResolveStorageDir()
        {
            string dir = null;
            try
            {
                if (_mb.Setting_GetPersistentStoragePath != null)
                    dir = _mb.Setting_GetPersistentStoragePath();
            }
            catch { }

            if (string.IsNullOrEmpty(dir))
                dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);

            try
            {
                dir = Path.Combine(dir, "NostalgiaPlus");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch
            {
                dir = Path.GetTempPath();
            }
            return dir;
        }

        /// <summary>
        /// MusicBee hands us the host panel once the user adds this view to a layout.
        /// The return value is the panel's preferred height in pixels; returning 0 let
        /// MusicBee fall back to a cramped 200.
        /// </summary>
        public int OnDockablePanelCreated(Control panel)
        {
            // MusicBee does not guarantee this runs on the thread that owns the host
            // panel. A WinForms control belongs to whichever thread created its handle,
            // so building the view here directly throws "Controls created on one thread
            // cannot be parented to a control on a different thread". Marshal onto the
            // panel's own thread, and if its handle does not exist yet, wait for it.
            try
            {
                if (panel.IsHandleCreated)
                {
                    if (panel.InvokeRequired)
                        panel.Invoke((MethodInvoker)delegate { BuildPanel(panel); });
                    else
                        BuildPanel(panel);
                }
                else
                {
                    EventHandler onCreated = null;
                    onCreated = delegate
                    {
                        panel.HandleCreated -= onCreated;
                        try { BuildPanel(panel); }
                        catch (Exception ex) { Trace("deferred panel creation failed: " + ex); }
                    };
                    panel.HandleCreated += onCreated;
                }
            }
            catch (Exception ex)
            {
                Trace("panel creation failed: " + ex);
            }
            return _settings != null ? _settings.DockPanelHeight : 320;
        }

        private void BuildPanel(Control panel)
        {
            panel.SuspendLayout();
            _hostPanel = panel;
            var view = new AnalyzerPanel(_settings, _storageDir);
            view.Player = BuildBridge();
            view.DockHeightRequested = ApplyDockHeight;
            view.Dock = DockStyle.Fill;
            panel.Controls.Add(view);
            panel.ResumeLayout(true);
            _panel = view;
            view.StartCapture();
            Trace("panel created on thread " + System.Threading.Thread.CurrentThread.ManagedThreadId);
        }

        /// <summary>
        /// Controls whether MusicBee lets the user drag the docked panel's height.
        ///
        /// Returning -1 here produced a stored PanelHeight of -200 - MusicBee's marker
        /// for a fixed-height panel - while the working reference plugin stores a
        /// positive 512. The exact units are undocumented, so return 1: that reads
        /// correctly whether the value means "resizable", a minimum height, or a resize
        /// increment in pixels.
        /// </summary>
        public int GetDockResizeSetting()
        {
            return 1;
        }

        public bool Configure(IntPtr panelHandle)
        {
            MessageBox.Show(
                "Nostalgia+ is configured from the panel itself.\r\n\r\n" +
                "Right-click the analyser for presets, palette, frequency scale, " +
                "resolution, spectral tilt and scroll speed.\r\n\r\n" +
                "Space or double-click freezes the display.\r\n" +
                "Hover to read frequency, note name, cents and level.\r\n\r\n" +
                "Settings file:\r\n" + Path.Combine(_storageDir, "NostalgiaPlus.settings"),
                "Nostalgia+", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        public void SaveSettings()
        {
            if (_settings != null && _storageDir != null) _settings.Save(_storageDir);
        }

        public void Close(PluginCloseReason reason)
        {
            try
            {
                if (_panel != null)
                {
                    _panel.StopCapture();
                    _panel.Dispose();
                    _panel = null;
                }
                SaveSettings();
            }
            catch { }
        }

        public void Uninstall()
        {
            try
            {
                string file = Path.Combine(_storageDir, "NostalgiaPlus.settings");
                if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(_storageDir) && Directory.GetFiles(_storageDir).Length == 0)
                    Directory.Delete(_storageDir);
            }
            catch { }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (_panel == null) return;
            switch (type)
            {
                case NotificationType.TrackChanged:
                    // Give the new track its own auto-range rather than inheriting the
                    // previous one's floor and ceiling.
                    _panel.NotifyTrackChanged();
                    break;
            }
        }

        private void OnToggleFullscreenCommand(object sender, EventArgs e)
        {
            AnalyzerPanel p = _panel;
            if (p == null || p.IsDisposed) return;
            try
            {
                if (p.InvokeRequired) p.Invoke((MethodInvoker)p.ToggleFullscreen);
                else p.ToggleFullscreen();
            }
            catch (Exception ex) { Trace("fullscreen command failed: " + ex.Message); }
        }

        /// <summary>
        /// Resizes the docked panel immediately.
        ///
        /// MusicBee lays the panel out from its own stored PanelHeight and treats the
        /// value returned by OnDockablePanelCreated only as a hint recorded for the next
        /// launch - so changing the setting alone took two restarts to show up. Resizing
        /// the live host control instead applies at once, and MusicBee then persists the
        /// size it actually ended up with.
        /// </summary>
        private void ApplyDockHeight(int px)
        {
            Control host = _hostPanel;
            if (host == null || host.IsDisposed) return;
            if (px < 60) px = 60;
            try
            {
                if (host.InvokeRequired)
                    host.Invoke((MethodInvoker)delegate { ResizeHost(host, px); });
                else
                    ResizeHost(host, px);
            }
            catch (Exception ex) { Trace("dock resize failed: " + ex.Message); }
        }

        private void ResizeHost(Control host, int px)
        {
            // A control docked top or bottom honours its own Height.
            host.Height = px;

            // If MusicBee nests it in a splitter, the splitter position is what actually
            // governs the height, so move that too.
            Control c = host;
            for (int depth = 0; depth < 4 && c != null; depth++)
            {
                SplitContainer sc = c.Parent as SplitContainer;
                if (sc != null && sc.Orientation == Orientation.Horizontal)
                {
                    bool inPanel1 = ReferenceEquals(c, sc.Panel1);
                    int target = inPanel1 ? px : sc.Height - px - sc.SplitterWidth;
                    int min = sc.Panel1MinSize;
                    int max = sc.Height - sc.Panel2MinSize - sc.SplitterWidth;
                    if (max > min)
                        sc.SplitterDistance = Math.Max(min, Math.Min(target, max));
                    break;
                }
                c = c.Parent;
            }

            if (host.Parent != null) host.Parent.PerformLayout();
            host.Refresh();
            Trace("dock height set to " + px + " (actual " + host.Height + ")");
        }

        /// <summary>
        /// Wraps the host API in the views' own vocabulary. Every delegate is checked
        /// for null here rather than at the call site, because which slots MusicBee
        /// fills depends on its version.
        /// </summary>
        private PlayerBridge BuildBridge()
        {
            var b = new PlayerBridge();
            b.Info = GetNowPlaying;
            if (_mb.Player_GetPosition != null)
                b.Position = delegate { return _mb.Player_GetPosition(); };
            if (_mb.NowPlaying_GetDuration != null)
                b.Duration = delegate { return _mb.NowPlaying_GetDuration(); };
            if (_mb.Player_GetPlayState != null)
                b.IsPlaying = delegate { return _mb.Player_GetPlayState() == PlayState.Playing; };
            if (_mb.NowPlaying_GetArtwork != null)
                b.Artwork = delegate { return _mb.NowPlaying_GetArtwork(); };
            if (_mb.Setting_GetSkinElementColour != null)
                b.SkinColour = delegate(int element, int state, int component)
                {
                    return _mb.Setting_GetSkinElementColour(
                        (SkinElement)element, (ElementState)state, (ElementComponent)component);
                };
            if (_mb.Player_PlayPause != null)
                b.PlayPause = delegate { _mb.Player_PlayPause(); };
            if (_mb.Player_PlayNextTrack != null)
                b.Next = delegate { _mb.Player_PlayNextTrack(); };
            if (_mb.Player_PlayPreviousTrack != null)
                b.Previous = delegate { _mb.Player_PlayPreviousTrack(); };
            if (_mb.Player_SetPosition != null)
                b.Seek = delegate(int ms) { _mb.Player_SetPosition(ms); };
            return b;
        }

        /// <summary>Track metadata for the fullscreen overlay.</summary>
        private string[] GetNowPlaying()
        {
            try
            {
                if (_mb.NowPlaying_GetFileTag == null) return null;
                return new string[] {
                    _mb.NowPlaying_GetFileTag(MetaDataType.TrackTitle),
                    _mb.NowPlaying_GetFileTag(MetaDataType.Artist),
                    _mb.NowPlaying_GetFileTag(MetaDataType.Album)
                };
            }
            catch { return null; }
        }

        private void Trace(string message)
        {
            try { if (_mb.MB_Trace != null) _mb.MB_Trace("Nostalgia+: " + message); }
            catch { }
        }
    }
}

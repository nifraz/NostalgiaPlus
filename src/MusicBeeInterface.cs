using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    // Field order below was extracted by reflection from the MusicBee plugin assembly
    // installed on this machine, so it matches this MusicBee build exactly.
    // Slots this plugin never invokes are declared as IntPtr: a delegate field and an
    // IntPtr marshal to the same function-pointer width, so struct layout is preserved
    // while keeping the surface we have to get right small.

    public enum PluginType
    {
        Unknown = 0, General = 1, LyricsRetrieval = 2, ArtworkRetrieval = 3,
        PanelView = 4, DataStream = 5, InstantMessenger = 6, Storage = 7,
        VideoPlayer = 8, DSP = 9, TagRetrieval = 10, TagOrArtworkRetrieval = 11,
        Upnp = 12, WebBrowser = 13
    }

    public enum PluginPanelDock
    {
        ApplicationWindow = 0, TrackAndArtistPanel = 1, TextBox = 3,
        ComboBox = 4, MainPanel = 5, TabControl = 6, Button = 7
    }

    public enum NotificationType
    {
        PluginStartup = 0, TrackChanged = 1, PlayStateChanged = 2,
        NowPlayingListChanged = 7, ShutdownStarted = 17, EmbedInPanel = 19,
        ApplicationWindowChanged = 27, MusicBeeStarted = 34, PlayingTracksChanged = 35
    }

    [Flags]
    public enum ReceiveNotificationFlags
    {
        StartupOnly = 0, PlayerEvents = 1, DataStreamEvents = 2,
        TagEvents = 4, DownloadEvents = 8
    }

    public enum PlayState { Undefined = 0, Loading = 1, Playing = 3, Paused = 6, Stopped = 7 }

    public enum SkinElement
    {
        SkinSubPanel = 0, SkinButton = 2, SkinInputControl = 7,
        SkinInputPanel = 10, SkinInputPanelLabel = 14, SkinTrackAndArtistPanel = -1
    }

    public enum ElementState { ElementStateDefault = 0, ElementStateModified = 6 }
    public enum ElementComponent { ComponentBorder = 0, ComponentBackground = 1, ComponentForeground = 3 }
    public enum PluginCloseReason { MusicBeeClosing = 1, UserDisabled = 2, StopNoUnload = 3 }

    /// <summary>Subset of MusicBee's tag ids, recovered from the installed host.</summary>
    public enum MetaDataType
    {
        Album = 30, AlbumArtist = 31, Artist = 32, YearOnly = 35,
        TrackTitle = 65, TrackNo = 86, Year = 88
    }

    public partial class Plugin
    {
        public delegate void MB_ReleaseString_D(string p1);
        public delegate void MB_Trace_D(string p1);
        public delegate string Setting_GetPersistentStoragePath_D();
        public delegate string Setting_GetSkin_D();
        public delegate int Setting_GetSkinElementColour_D(SkinElement element, ElementState state, ElementComponent component);
        public delegate bool Setting_IsWindowBordersSkinned_D();
        public delegate int Player_GetPosition_D();
        // Signatures below were read off the reference plugin by reflection, the same
        // way the struct layout was, rather than guessed - a wrong delegate here is a
        // crash inside the host, not a quiet failure.
        public delegate bool Player_SetPosition_D(int position);
        public delegate bool Player_PlayPause_D();
        public delegate bool Player_PlayPreviousTrack_D();
        public delegate bool Player_PlayNextTrack_D();
        public delegate string NowPlaying_GetArtwork_D();
        public delegate PlayState Player_GetPlayState_D();
        public delegate string NowPlaying_GetFileUrl_D();
        public delegate int NowPlaying_GetDuration_D();
        public delegate string NowPlaying_GetFileTag_D(MetaDataType field);
        public delegate IntPtr MB_GetWindowHandle_D();
        public delegate void MB_RefreshPanels_D();
        public delegate void MB_RegisterCommand_D(string command, EventHandler handler);
        public delegate Font Setting_GetDefaultFont_D();
        public delegate int NowPlaying_GetSpectrumData_D(float[] fftData);
        public delegate bool NowPlaying_GetSoundGraph_D(float[] graphData);
        public delegate Rectangle MB_GetPanelBounds_D(PluginPanelDock dock);
        public delegate Control MB_AddPanel_D(Control panel, PluginPanelDock dock);
        public delegate void MB_RemovePanel_D(Control panel);
        public delegate string MB_GetLocalisation_D(string id, string defaultText);

        [StructLayout(LayoutKind.Sequential)]
        public struct MusicBeeApiInterface
        {
            public short InterfaceVersion;
            public short ApiRevision;
            public MB_ReleaseString_D MB_ReleaseString;
            public MB_Trace_D MB_Trace;
            public Setting_GetPersistentStoragePath_D Setting_GetPersistentStoragePath;
            public Setting_GetSkin_D Setting_GetSkin;
            public Setting_GetSkinElementColour_D Setting_GetSkinElementColour;
            public Setting_IsWindowBordersSkinned_D Setting_IsWindowBordersSkinned;
            public IntPtr Library_GetFileProperty;
            public IntPtr Library_GetFileTag;
            public IntPtr Library_SetFileTag;
            public IntPtr Library_CommitTagsToFile;
            public IntPtr Library_GetLyrics;
            public IntPtr Library_GetArtwork;
            public IntPtr Library_QueryFiles;
            public IntPtr Library_QueryGetNextFile;
            public Player_GetPosition_D Player_GetPosition;
            public Player_SetPosition_D Player_SetPosition;
            public Player_GetPlayState_D Player_GetPlayState;
            public Player_PlayPause_D Player_PlayPause;
            public IntPtr Player_Stop;
            public IntPtr Player_StopAfterCurrent;
            public Player_PlayPreviousTrack_D Player_PlayPreviousTrack;
            public Player_PlayNextTrack_D Player_PlayNextTrack;
            public IntPtr Player_StartAutoDj;
            public IntPtr Player_EndAutoDj;
            public IntPtr Player_GetVolume;
            public IntPtr Player_SetVolume;
            public IntPtr Player_GetMute;
            public IntPtr Player_SetMute;
            public IntPtr Player_GetShuffle;
            public IntPtr Player_SetShuffle;
            public IntPtr Player_GetRepeat;
            public IntPtr Player_SetRepeat;
            public IntPtr Player_GetEqualiserEnabled;
            public IntPtr Player_SetEqualiserEnabled;
            public IntPtr Player_GetDspEnabled;
            public IntPtr Player_SetDspEnabled;
            public IntPtr Player_GetScrobbleEnabled;
            public IntPtr Player_SetScrobbleEnabled;
            public NowPlaying_GetFileUrl_D NowPlaying_GetFileUrl;
            public NowPlaying_GetDuration_D NowPlaying_GetDuration;
            public IntPtr NowPlaying_GetFileProperty;
            public NowPlaying_GetFileTag_D NowPlaying_GetFileTag;
            public IntPtr NowPlaying_GetLyrics;
            public NowPlaying_GetArtwork_D NowPlaying_GetArtwork;
            public IntPtr NowPlayingList_Clear;
            public IntPtr NowPlayingList_QueryFiles;
            public IntPtr NowPlayingList_QueryGetNextFile;
            public IntPtr NowPlayingList_PlayNow;
            public IntPtr NowPlayingList_QueueNext;
            public IntPtr NowPlayingList_QueueLast;
            public IntPtr NowPlayingList_PlayLibraryShuffled;
            public IntPtr Playlist_QueryPlaylists;
            public IntPtr Playlist_QueryGetNextPlaylist;
            public IntPtr Playlist_GetType;
            public IntPtr Playlist_QueryFiles;
            public IntPtr Playlist_QueryGetNextFile;
            public MB_GetWindowHandle_D MB_GetWindowHandle;
            public MB_RefreshPanels_D MB_RefreshPanels;
            public IntPtr MB_SendNotification;
            public IntPtr MB_AddMenuItem;
            public IntPtr Setting_GetFieldName;
            public IntPtr Library_QueryGetAllFiles;
            public IntPtr NowPlayingList_QueryGetAllFiles;
            public IntPtr Playlist_QueryGetAllFiles;
            public IntPtr MB_CreateBackgroundTask;
            public IntPtr MB_SetBackgroundTaskMessage;
            public MB_RegisterCommand_D MB_RegisterCommand;
            public Setting_GetDefaultFont_D Setting_GetDefaultFont;
            public IntPtr Player_GetShowTimeRemaining;
            public IntPtr NowPlayingList_GetCurrentIndex;
            public IntPtr NowPlayingList_GetListFileUrl;
            public IntPtr NowPlayingList_GetFileProperty;
            public IntPtr NowPlayingList_GetFileTag;
            public NowPlaying_GetSpectrumData_D NowPlaying_GetSpectrumData;
            public NowPlaying_GetSoundGraph_D NowPlaying_GetSoundGraph;
            public MB_GetPanelBounds_D MB_GetPanelBounds;
            public MB_AddPanel_D MB_AddPanel;
            public MB_RemovePanel_D MB_RemovePanel;
            public MB_GetLocalisation_D MB_GetLocalisation;
        }

        public class PluginInfo
        {
            public short PluginInfoVersion;
            public PluginType Type;
            public string Name;
            public string Description;
            public string Author;
            public string TargetApplication;
            public short VersionMajor;
            public short VersionMinor;
            public short Revision;
            public short MinInterfaceVersion;
            public short MinApiRevision;
            public ReceiveNotificationFlags ReceiveNotifications;
            public int ConfigurationPanelHeight;
        }
    }
}

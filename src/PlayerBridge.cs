using System;

namespace NostalgiaPlus
{
    /// <summary>
    /// Everything the views need from the host player, as plain delegates over
    /// primitives.
    ///
    /// Deliberately not typed against MusicBee's own enums or interfaces: the render
    /// and UI code stays testable from the offscreen harnesses, which supply their own
    /// implementations, and a change to the host's API surface stops at
    /// <c>Plugin.cs</c> instead of reaching the views. Every field may be null - the
    /// deck simply omits whatever is not wired.
    /// </summary>
    public sealed class PlayerBridge
    {
        /// <summary>
        /// {title, artist, album, composer, year}; any element may be null or empty,
        /// and a short array is fine - the deck reads what is there.
        /// </summary>
        public NowPlayingProvider Info;

        /// <summary>Playback position in milliseconds.</summary>
        public Func<int> Position;
        /// <summary>Track length in milliseconds; 0 or less when unknown.</summary>
        public Func<int> Duration;
        /// <summary>True while audio is actually playing, as opposed to paused or stopped.</summary>
        public Func<bool> IsPlaying;
        /// <summary>Base64 image data for the current track's artwork, or null.</summary>
        public Func<string> Artwork;

        /// <summary>
        /// A colour from the host's current skin: element, state, component. Not player
        /// state, but it comes from the same place and through the same door - the views
        /// stay free of MusicBee's own types either way.
        /// </summary>
        public Func<int, int, int, int> SkinColour;

        public Action PlayPause;
        public Action Next;
        public Action Previous;
        /// <summary>Seek to a position in milliseconds.</summary>
        public Action<int> Seek;

        public string[] SafeInfo()
        {
            try { return Info == null ? null : Info(); }
            catch { return null; }
        }

        public int SafePosition() { try { return Position == null ? 0 : Position(); } catch { return 0; } }
        public int SafeDuration() { try { return Duration == null ? 0 : Duration(); } catch { return 0; } }
        public bool SafeIsPlaying() { try { return IsPlaying != null && IsPlaying(); } catch { return false; } }
        public string SafeArtwork() { try { return Artwork == null ? null : Artwork(); } catch { return null; } }

        public void Do(Action a) { try { if (a != null) a(); } catch { } }
    }
}

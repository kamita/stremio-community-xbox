using System;
using System.Collections.Generic;
using Windows.Storage;

namespace StremioCommunity.Core
{
    /// <summary>
    /// Xbox port of the desktop configuration in src/utils/config.cpp + src/core/globals.cpp.
    /// The Win32 build stored these in a stremio-settings.ini (GetPrivateProfileString*);
    /// on Xbox they live in ApplicationData.Current.LocalSettings plus in-memory defaults.
    /// Only settings that are meaningful on a UWP/console target are ported here.
    /// </summary>
    public static class CommunitySettings
    {
        // ---- src/core/globals.cpp: g_webuiUrls (official site first, community mirrors as fallback) ----
        public static readonly IReadOnlyList<string> WebUiUrls = new[]
        {
            "https://web.stremio.com/",
            "https://stremio.zarg.me/",
            "https://zaarrg.github.io/stremio-web-shell-fixes/",
        };

        /// <summary>Primary Stremio Web URL (first mirror).</summary>
        public static string PrimaryUrl => WebUiUrls[0];

        // ---- src/core/globals.cpp: g_subtitleExtensions (local subtitle file detection) ----
        public static readonly IReadOnlyList<string> SubtitleExtensions = new[]
        {
            ".srt", ".ass", ".ssa", ".sub", ".vtt", ".ttml",
            ".dfxp", ".smi", ".sami", ".sup", ".scc",
            ".xml", ".lrc", ".pjs", ".mpl", ".usf", ".qtvr"
        };

        /// <summary>True when the path's extension is one of the known subtitle types (desktop drag&amp;drop).</summary>
        public static bool IsSubtitleFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string ext = System.IO.Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                return false;
            }

            ext = ext.ToLowerInvariant();
            for (int i = 0; i < SubtitleExtensions.Count; i++)
            {
                if (ext == SubtitleExtensions[i])
                {
                    return true;
                }
            }

            return false;
        }

        // ---- src/utils/config.cpp [MPV] InitialVolume (default 50) ----
        private const string VolumeKey = "MPV_InitialVolume";
        private const int DefaultVolume = 50;

        /// <summary>Initial playback volume in 0-100, matching the desktop default of 50.</summary>
        public static int InitialVolume
        {
            get => ReadInt(VolumeKey, DefaultVolume);
            set => WriteInt(VolumeKey, value);
        }

        // ---- src/utils/config.cpp [General] PauseOnMinimize (default 1) / PauseOnLostFocus (default 0) ----
        private const string PauseMinKey = "General_PauseOnMinimize";
        private const string PauseFocusKey = "General_PauseOnLostFocus";

        /// <summary>Pause on minimize (desktop default: on). On Xbox this maps to the app going to background.</summary>
        public static bool PauseOnMinimize
        {
            get => ReadBool(PauseMinKey, true);
            set => WriteBool(PauseMinKey, value);
        }

        /// <summary>Pause on lost focus (desktop default: off).</summary>
        public static bool PauseOnLostFocus
        {
            get => ReadBool(PauseFocusKey, false);
            set => WriteBool(PauseFocusKey, value);
        }

        /// <summary>Combined flag: pause native playback when the app leaves the foreground.</summary>
        public static bool PauseWhenInBackground => PauseOnMinimize || PauseOnLostFocus;

        // ---- src/utils/config.cpp [Security] MpvSetPropAllowlist: portable playback props ----
        // These MPV set-props have a real Xbox MediaPlayer / TimedTextStyle equivalent, so they are
        // ported. (speed, mute, aid, sid, sub-scale, sub-color, sub-back-color, sub-border-color,
        // sub-pos). Non-portable ones (vo, hwdec, osc, input-*, sub-delay, subs-fallback-forced,
        // subs-with-matching-audio) are dropped because the Xbox pipeline owns them.

        private const string PlaybackRateKey = "MPV_Speed";
        private const double DefaultPlaybackRate = 1.0;

        /// <summary>MPV "speed" -> MediaPlayer.PlaybackRate. Clamped to the Xbox-supported 0.25-4 range.</summary>
        public static double PlaybackRate
        {
            get => ReadDouble(PlaybackRateKey, DefaultPlaybackRate);
            set => WriteDouble(PlaybackRateKey, value);
        }

        /// <summary>MPV "mute" -> MediaPlayer.IsMuted (bool).</summary>
        public static bool Muted
        {
            get => ReadBool("MPV_Mute", false);
            set => WriteBool("MPV_Mute", value);
        }

        // ---- MPV subtitle style props -> Windows.Media.Playback TimedTextStyle ----

        /// <summary>MPV "sub-scale" (0.5-4). Scales subtitle font size on the native player.</summary>
        public static double SubtitleScale
        {
            get => ReadDouble("MPV_SubScale", 1.0);
            set => WriteDouble("MPV_SubScale", value);
        }

        /// <summary>MPV "sub-color" (#RRGGBB). Foreground color of subtitles.</summary>
        public static int SubtitleColor
        {
            get => ReadInt("MPV_SubColor", 0xFFFFFF);
            set => WriteInt("MPV_SubColor", value);
        }

        /// <summary>MPV "sub-back-color" (#RRGGBB). Background/box color behind subtitles.</summary>
        public static int SubtitleBackColor
        {
            get => ReadInt("MPV_SubBackColor", 0x00000000); // 0 = transparent
            set => WriteInt("MPV_SubBackColor", value);
        }

        /// <summary>MPV "sub-border-color" (#RRGGBB). Outline/edge color around subtitles.</summary>
        public static int SubtitleBorderColor
        {
            get => ReadInt("MPV_SubBorderColor", 0x000000);
            set => WriteInt("MPV_SubBorderColor", value);
        }

        /// <summary>MPV "no-sub-ass" (1 = off). When true, force the plain (non-ASS) subtitle style.</summary>
        public static bool SubtitleAssDisabled
        {
            get => ReadBool("MPV_NoSubAss", false);
            set => WriteBool("MPV_NoSubAss", value);
        }

        // ---- MPV "sub-delay" (subtitles timing) -> TimedMetadataTrack.SubDelay ----

        /// <summary>MPV "sub-delay" in seconds. Positive shifts subtitles later (fixes early subs), negative earlier. -30..+30.</summary>
        public static double SubtitleDelay
        {
            get => ReadDouble("MPV_SubDelay", 0.0);
            set => WriteDouble("MPV_SubDelay", Math.Max(-30, Math.Min(30, value)));
        }

        // ---- MPV subtitle speed (no direct desktop key; built for the Xbox port) ----

        /// <summary>Subtitle speed multiplier applied via TimedMetadataTrack.SubRate. 0.5-2.0, default 1.0.</summary>
        public static double SubtitleRate
        {
            get => ReadDouble("MPV_SubRate", 1.0);
            set => WriteDouble("MPV_SubRate", Math.Max(0.5, Math.Min(2.0, value)));
        }

        // ---- Resume playback (desktop "resume" behavior, built for the Xbox port) ----

        /// <summary>Resume videos from the last watched position (default: on).</summary>
        public static bool ResumePlayback
        {
            get => ReadBool("Playback_Resume", true);
            set => WriteBool("Playback_Resume", value);
        }

        /// <summary>Consider a video finished (don't resume) when within this many seconds of the end. 0 = never finished.</summary>
        public static int ResumeEndThresholdSeconds
        {
            get => ReadInt("Playback_ResumeEndThreshold", 300);
            set => WriteInt("Playback_ResumeEndThreshold", value);
        }

        // ---- Auto-continue (desktop auto-play-next behavior) ----

        /// <summary>Keep watching: when a title ends, play the next episode/movie if the page offers one.</summary>
        public static bool AutoContinue
        {
            get => ReadBool("Playback_AutoContinue", false);
            set => WriteBool("Playback_AutoContinue", value);
        }

        // ---- Debrid read-ahead buffer size (MB). 0 = automatic (memory-derived). ----
        private static readonly int[] BufferMbSteps = { 0, 128, 256, 512, 768, 1024, 1536 };

        /// <summary>Index into <see cref="BufferMbSteps"/> for the debrid read-ahead cap (0 = automatic).</summary>
        public static int BufferMbIndex
        {
            get => ReadInt("Debrid_BufferMbIndex", 0);
            set => WriteInt("Debrid_BufferMbIndex", Math.Max(0, Math.Min(value, BufferMbSteps.Length - 1)));
        }

        /// <summary>Cap in MB, or 0 for automatic (the memory-derived value from the app).</summary>
        public static int BufferMb => BufferMbSteps[BufferMbIndex];

        // ---- HDR / display behavior ----

        /// <summary>Let the app switch the HDMI resolution to match the video (default: on).</summary>
        public static bool MatchResolution
        {
            get => ReadBool("Display_MatchResolution", true);
            set => WriteBool("Display_MatchResolution", value);
        }

        /// <summary>Let the app switch the HDMI refresh rate to match the frame rate (default: on).</summary>
        public static bool MatchRefreshRate
        {
            get => ReadBool("Display_MatchRefreshRate", true);
            set => WriteBool("Display_MatchRefreshRate", value);
        }

        /// <summary>HDR quality mode: 0 = auto (follow the video), 1 = force HDR, 2 = force SDR.</summary>
        public static int HdrQualityMode
        {
            get => ReadInt("Display_HdrQualityMode", 0);
            set => WriteInt("Display_HdrQualityMode", Math.Max(0, Math.Min(value, 2)));
        }

        private static double ReadDouble(string key, double fallback)
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.TryGetValue(key, out var v) && v is double d)
                {
                    return d;
                }
                if (values.TryGetValue(key, out var v2) && v2 is int i2)
                {
                    return i2;
                }
                return fallback;
            }
            catch (System.Exception)
            {
                return fallback;
            }
        }

        private static void WriteDouble(string key, double value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (System.Exception)
            {
            }
        }

        private static int ReadInt(string key, int fallback)
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                return values.TryGetValue(key, out var v) && v is int i ? i : fallback;
            }
            catch (System.Exception)
            {
                return fallback; // Non-packaged debug runs have no ApplicationData.
            }
        }

        private static void WriteInt(string key, int value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (System.Exception)
            {
            }
        }

        private static bool ReadBool(string key, bool fallback)
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                return values.TryGetValue(key, out var v) && v is bool b ? b : fallback;
            }
            catch (System.Exception)
            {
                return fallback;
            }
        }

        private static void WriteBool(string key, bool value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (System.Exception)
            {
            }
        }
    }
}

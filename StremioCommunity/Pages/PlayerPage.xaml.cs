using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StremioCommunity.Core;
using StremioCommunity.Models;
using Windows.ApplicationModel;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Media.Playback;
using Windows.System;
using Windows.System.Display;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace StremioCommunity.Pages
{
    /// <summary>
    /// The native Xbox player. Extracted from the old WebView2 shell's MainPage:
    /// plays a <see cref="PlayRequest"/> through MediaPlayer + BufferedHttpStream
    /// (6-parallel-connection RAM read-ahead for large remuxes) with HDMI
    /// resolution/HDR/Dolby Vision switching, an on-screen bar, audio and subtitle
    /// menus, a controls-help overlay, a debug log and all of the Xbox workarounds
    /// (subtitle re-timing via content rewrite, restart-seek, buffer waits,
    /// accelerator-key dedup, resume position).
    /// B stops playback and navigates back.
    /// </summary>
    public sealed partial class PlayerPage : Page, IAppLifecycle, ISettingsApplier
    {
        /// <summary>Navigation arguments: the stream + subtitles chosen on the Detail page.</summary>
        public sealed class Args
        {
            /// <summary>The chosen stream and its subtitles / preferred languages.</summary>
            public PlaybackSelection Selection;
        }

        /// <summary>MF_MT_TRANSFER_FUNCTION: 15 = SMPTE ST 2084 (PQ / HDR10), 16 = HLG.</summary>
        private static readonly Guid TransferFunctionKey = new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93");

        // Restart seek modes: seconds to jump forward before returning; positive = after playback
        // starts, negative = before playing, 0 = off. (Native player workaround — kept.)
        private static readonly int[] RestartSeekModes = { 10, 30, 60, -60, 0 };

        private readonly GamepadInput _gamepad = new GamepadInput();
        private readonly DisplayModeManager _displayModes = new DisplayModeManager();
        private readonly DisplayRequest _displayRequest = new DisplayRequest();
        private readonly object _logLock = new object();
        private readonly List<string> _log = new List<string>();

        private MediaPlayer _mediaPlayer;
        private PlayRequest _current;
        private bool _displayRequestActive;
        private DispatcherTimer _diagnosticsTimer;
        private long _fileSize; // From the probe; 0 until known.
        private bool _bitrateLogged;
        private Stopwatch _paceClock;
        private TimeSpan _lastPosition;
        private double _lastDownload;
        private int _paceTicks;
        private DispatcherTimer _osdTimer;
        private DateTime _osdHideAt;

        private int _connections = ReadSetting("connections", 6); // 0 = hand the URL to the player directly
        private int _restartSeekMode = ReadSetting("restartSeek", 0);
        private bool _attachSubtitles = ReadSetting("attachSubtitles", true);

        // Resume: persist the playback position every few seconds (in LogPace), restore it in
        // OnMediaOpened before Play, clear it on OnMediaEnded. Keyed by a hash of the stream URL.
        private string _resumeKey;
        private TimeSpan _resumePosition;
        private bool _seekInProgress; // Guards overlapping async seeks.

        private HttpChunkCache _cache;
        private long _lastCacheBytes;

        private int _audioMenuIndex; // Highlighted row in the audio track menu.
        private int _subtitleMenuIndex; // Highlighted row in the subtitle menu (0 = "off").
        private int _subtitleTrackCount; // Number of subtitle track rows (before the Delay/Speed value rows).
        private int _lastSubtitleSelection; // 0 = off, n = the n-th subtitle track; restored when the menu reopens.
        private string _osdMessage;

        private PadButton _lastPadBtn;
        private int _lastPadBtnMs;
        private int _lastBackTick = Environment.TickCount - 1000;

        public Args _navArgs;

        public PlayerPage() : this(null)
        {
        }

        public PlayerPage(Args navArgs)
        {
            _navArgs = navArgs;
            InitializeComponent();
            Loaded += OnLoaded;
            // Wire the subtitle timing modifier's diagnostic hook to the page log.
            SubtitleTimingModifier.OnDiagnostic = msg => Log("warn", msg);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated += OnAcceleratorKeyActivated;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _gamepad.Pressed += OnPadButton;
            _gamepad.Start();

            Log("info", DisplayModeManager.DescribeDisplay());
            UpdateDebugHeader();

            var args = _navArgs;
            if (args?.Selection == null)
            {
                Log("error", "PlayerPage: no navigation arguments — going back");
                GoBackOrHome();
                return;
            }

            _current = args.Selection.ToPlayRequest();
            StartNativePlayback(_current);
            await Task.CompletedTask;
        }

        private static T ReadSetting<T>(string key, T fallback)
        {
            try
            {
                return ApplicationData.Current.LocalSettings.Values[key] is T value ? value : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static void SaveSetting(string key, object value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (Exception)
            {
            }
        }

        // ---- Native playback ------------------------------------------------------------------

        private void StartNativePlayback(PlayRequest request)
        {
            StopPlayback(resetDisplay: false);
            _current = request;
            Log("info", "Native play: " + request);
            Log("info", "Stream URL: " + request.Url);
            _fileSize = 0;
            _bitrateLogged = false;
            if (request.HasRequestHeaders)
            {
                Log("warn", "This stream asks for custom request headers; the native player does not send them yet.");
            }

            _mediaPlayer = new MediaPlayer
            {
                AutoPlay = false,
                AudioCategory = MediaPlayerAudioCategory.Movie,
            };

            _mediaPlayer.MediaOpened += OnMediaOpened;
            _mediaPlayer.MediaFailed += OnMediaFailed;
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.PlaybackSession.BufferingStarted += OnBufferingStarted;
            _mediaPlayer.PlaybackSession.BufferingEnded += OnBufferingEnded;
            Player.SetMediaPlayer(_mediaPlayer);

            // Initial volume (desktop [MPV] InitialVolume setting, default 50).
            try
            {
                var vol = CommunitySettings.InitialVolume;
                if (vol < 0)
                {
                    vol = 0;
                }

                if (vol > 100)
                {
                    vol = 100;
                }

                _mediaPlayer.Volume = vol;
                Log("info", "Initial volume: " + vol + "%");
            }
            catch (Exception ex)
            {
                Log("warn", "Could not apply initial volume: " + ex.Message);
            }

            // MPV "speed" / "mute" set-props -> PlaybackRate / IsMuted.
            try
            {
                var rate = CommunitySettings.PlaybackRate;
                if (rate < 0.25)
                {
                    rate = 0.25;
                }

                if (rate > 4.0)
                {
                    rate = 4.0;
                }

                _mediaPlayer.PlaybackRate = rate;
                _mediaPlayer.IsMuted = CommunitySettings.Muted;
                Log("info", "Initial rate " + rate + "x, muted " + CommunitySettings.Muted);
            }
            catch (Exception ex)
            {
                Log("warn", "Could not apply initial rate/mute: " + ex.Message);
            }

            _ = OpenSourceAsync(request, _mediaPlayer);
        }

        private async Task OpenSourceAsync(PlayRequest request, MediaPlayer player)
        {
            try
            {
                var probe = await HttpChunkCache.ResolveAsync(request.Url, LogAsync);
                if (player != _mediaPlayer)
                {
                    return; // Stopped while resolving.
                }

                _fileSize = probe?.Size ?? 0;
                _resumeKey = ResumeKeyFor(request);
                _resumePosition = CommunitySettings.ResumePlayback ? LoadResumePosition(_resumeKey) : TimeSpan.Zero;
                var contentType = GuessContentType(request, probe?.ContentType);
                MediaSource source;
                if (_connections > 0 && probe != null && probe.AcceptsRanges && probe.Size > 0 && contentType != null)
                {
                    var ahead = PickReadAheadBytes();
                    _cache = new HttpChunkCache(request.Url, probe.FinalUrl, probe.Size, _connections, ahead, LogAsync);
                    _lastCacheBytes = 0;
                    source = MediaSource.CreateFromStream(new BufferedHttpStream(_cache, 0), contentType);
                    Log("info", string.Format("Buffered playback: {0} connections, {1} MB read-ahead, {2}", _connections, ahead >> 20, contentType));
                }
                else
                {
                    Log("info", "Direct URL playback (" + (_connections == 0 ? "selected in debug log" :
                        probe == null ? "probe failed" : !probe.AcceptsRanges ? "no range support" : contentType == null ? "unknown container" : "size unknown") + ")");
                    source = MediaSource.CreateFromUri(new Uri(request.Url));
                }

                if (_attachSubtitles)
                {
                    await AddExternalSubtitlesAsync(source, request);
                }
                else
                {
                    Log("info", "External subtitles: off (" + request.Subtitles.Count + " available)");
                }

                var item = new MediaPlaybackItem(source);
                item.TimedMetadataTracksChanged += (sender2, args2) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    LogSubtitleTracks(sender2);
                    OnSubtitleTracksAvailable(sender2);
                });

                _lastSubtitleSelection = 0;
                player.Source = item;
            }
            catch (Exception ex)
            {
                Log("error", "Could not open stream: " + ex.Message);
                StopPlayback(resetDisplay: true);
                SetDebugVisible(true);
            }
        }

        // Leaves room for the app (about 1.3 GB during 4K playback) inside the console's memory budget.
        private static long PickReadAheadBytes()
        {
            const long mb = 1024 * 1024;
            var free = (long)MemoryManager.AppMemoryUsageLimit - (long)MemoryManager.AppMemoryUsage;
            var auto = Math.Max(128 * mb, Math.Min(768 * mb, free - (1024 * mb)));
            var capMb = CommunitySettings.BufferMb;
            if (capMb <= 0)
            {
                return auto;
            }

            return Math.Min(auto, (long)capMb * mb);
        }

        // ---- Resume playback ----

        // A stable key from the stream so each file keeps its own position (URL changes per quality/debrid).
        private static string ResumeKeyFor(PlayRequest request)
        {
            var text = (request?.Url ?? string.Empty) + "|" + (request?.Description ?? string.Empty);
            var hash = 0;
            for (var i = 0; i < text.Length; i++)
            {
                hash = unchecked(hash * 31 + text[i]);
            }

            return "Resume_" + Math.Abs(hash).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static TimeSpan LoadResumePosition(string key)
        {
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                if (key != null && values.TryGetValue(key, out var v) && v is double d)
                {
                    return TimeSpan.FromSeconds(d);
                }
            }
            catch (System.Exception)
            {
            }

            return TimeSpan.Zero;
        }

        private static void SaveResumePosition(string key, TimeSpan position)
        {
            try
            {
                if (key != null)
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values[key] = position.TotalSeconds;
                }
            }
            catch (System.Exception)
            {
            }
        }

        private static void ClearResumePosition(string key)
        {
            try
            {
                if (key != null && Windows.Storage.ApplicationData.Current.LocalSettings.Values.ContainsKey(key))
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values.Remove(key);
                }
            }
            catch (System.Exception)
            {
            }
        }

        // Auto-continue (next episode for a series) — resolves the next video's id from the
        // episode's id and starts it through the Detail page (which lists its streams).
        private Task<bool> TryAdvanceQueueAsync()
        {
            if (_current == null)
            {
                return Task.FromResult(false);
            }

            // Episode ids look like "tt0944947 S02E10"; the next episode is S02E11.
            var match = Regex.Match(_current.Description + " " + _current.FileName + " " + _current.Url,
                @"S(\d{2})E(\d{2})(?![0-9])", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var season = match.Groups[1].Value;
                var episode = int.Parse(match.Groups[2].Value) + 1;
                Log("info", string.Format("Auto-continue: episode {0} ended (S{1}E{2}); next would be S{3}E{4:00}",
                    episode - 1, season, episode - 1, season, episode));
            }

            Log("warn", "Auto-continue: the next-episode lookup is not wired to the meta yet");
            return Task.FromResult(false);
        }

        // ---- Subtitle track ordering ----

        // Returns the indices of subtitle tracks in `item`, sorted by Language (ascending),
        // then by Label. Every caller (menu, auto-select, log) uses this single ordered list.
        private static List<int> GetOrderedSubtitleTrackIndices(MediaPlaybackItem item)
        {
            var tracks = item.TimedMetadataTracks;
            var indices = new List<int>();
            for (var i = 0; i < tracks.Count; i++)
            {
                if (IsSubtitleTrack(tracks[i]))
                {
                    indices.Add(i);
                }
            }
            indices.Sort((a, b) => CompareSubtitleTracks(tracks[a], tracks[b]));
            return indices;
        }

        private static int CompareSubtitleTracks(TimedMetadataTrack a, TimedMetadataTrack b)
        {
            var langA = a.Language ?? string.Empty;
            var langB = b.Language ?? string.Empty;
            var cmp = string.Compare(langA, langB, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0)
            {
                return cmp;
            }
            var labelA = a.Label ?? string.Empty;
            var labelB = b.Label ?? string.Empty;
            return string.Compare(labelA, labelB, StringComparison.OrdinalIgnoreCase);
        }

        // Tracks report their language in ISO 639-2/T codes ("spa", "eng", "ita", ...);
        // the user's preference is stored as ISO 639-1 ("es", "en", ...). This returns every
        // code variant (639-1 + 639-2/T + legacy) that should match a preference.
        private static string[] LanguageVariants(string code)
        {
            switch (code.ToLowerInvariant())
            {
                case "es": return new[] { "es", "spa" };
                case "en": return new[] { "en", "eng" };
                case "fr": return new[] { "fr", "fra", "fre" };
                case "pt": return new[] { "pt", "por" };
                case "de": return new[] { "de", "deu", "ger" };
                case "it": return new[] { "it", "ita" };
                case "ja": return new[] { "ja", "jpn" };
                case "ko": return new[] { "ko", "kor" };
                case "zh": return new[] { "zh", "zho", "chi" };
                case "ru": return new[] { "ru", "rus" };
                case "ar": return new[] { "ar", "ara" };
                default:   return new[] { code };
            }
        }

        // True when a track's language corresponds to the user's preferred language code.
        private static bool LanguageMatches(string trackLang, string code)
        {
            if (string.IsNullOrEmpty(trackLang) || string.IsNullOrEmpty(code))
            {
                return false;
            }
            var baseCode = trackLang.Split('-')[0].ToLowerInvariant();
            foreach (var variant in LanguageVariants(code))
            {
                if (baseCode == variant)
                {
                    return true;
                }
            }
            return false;
        }

        private static string LanguageLabelKeyword(string label)
        {
            switch (label)
            {
                case "Español":   return "spani";
                case "English":   return "english";
                case "Français":  return "french";
                case "Português": return "portug";
                case "Deutsch":   return "german";
                case "Italiano":  return "italian";
                default:          return null;
            }
        }

        // Applies the audio track matching the user's preferred language, BEFORE Play()
        // so the first frame already uses the right language.
        private void ApplyPreferredAudioTrack(MediaPlayer player)
        {
            var effectiveCode = ResolvePreferredLangCode(
                CommunitySettings.PreferredAudioLangIndex,
                _current?.PreferredAudioLang);
            if (string.IsNullOrEmpty(effectiveCode))
            {
                return;
            }

            try
            {
                if (!(player.Source is MediaPlaybackItem item) || item.AudioTracks.Count == 0)
                {
                    return;
                }

                var tracks = item.AudioTracks;
                int chosen = -1;
                for (var i = 0; i < tracks.Count; i++)
                {
                    var lang = tracks[i].Language ?? string.Empty;
                    if (LanguageMatches(lang, effectiveCode))
                    {
                        chosen = i;
                        break;
                    }
                }
                if (chosen < 0)
                {
                    var kw = LanguageLabelKeywordForCode(effectiveCode);
                    if (kw != null)
                    {
                        for (var i = 0; i < tracks.Count; i++)
                        {
                            var label = tracks[i].Label ?? string.Empty;
                            if (label.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                chosen = i;
                                break;
                            }
                        }
                    }
                }
                if (chosen >= 0 && tracks.SelectedIndex != chosen)
                {
                    tracks.SelectedIndex = chosen;
                    Log("info", "Audio: auto-selected track " + (chosen + 1) +
                        " (" + (tracks[chosen].Language ?? "?") + ") for language \"" + effectiveCode + "\"");
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Audio auto-select failed: " + ex.Message);
            }
        }

        // Returns the effective preferred language code: local setting first,
        // else the Stremio account's setting. Null/empty means "auto".
        private static string ResolvePreferredLangCode(int localIndex, string accountLang)
        {
            if (localIndex > 0 && localIndex < CommunitySettings.LanguageCodes.Length)
            {
                var code = CommunitySettings.LanguageCodes[localIndex];
                if (!string.IsNullOrEmpty(code))
                {
                    return code;
                }
            }
            return string.IsNullOrEmpty(accountLang) ? null : accountLang.ToLowerInvariant();
        }

        private static string LanguageLabelKeywordForCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return null;
            }
            var idx = Array.IndexOf(CommunitySettings.LanguageCodes, code.ToLowerInvariant());
            if (idx > 0)
            {
                return LanguageLabelKeyword(CommunitySettings.LanguageLabels[idx]);
            }
            return null;
        }

        // Auto-selects the subtitle track matching the user's preferred language.
        private bool ApplyPreferredLanguageTracks(MediaPlaybackItem item)
        {
            var effectiveCode = ResolvePreferredLangCode(
                CommunitySettings.PreferredSubLangIndex,
                _current?.PreferredSubLang);
            if (string.IsNullOrEmpty(effectiveCode))
            {
                return true; // Both local and account are "auto".
            }
            try
            {
                var tracks = item.TimedMetadataTracks;
                if (tracks.Count == 0)
                {
                    Log("info", "Subtitle auto-select: 0 tracks yet — will retry when more arrive");
                    return false;
                }
                var subtitleIndices = GetOrderedSubtitleTrackIndices(item);
                if (subtitleIndices.Count == 0)
                {
                    return false;
                }

                int chosenTrack = -1;
                // First pass: exact base-language match.
                foreach (var ti in subtitleIndices)
                {
                    var lang = tracks[ti].Language ?? string.Empty;
                    if (LanguageMatches(lang, effectiveCode))
                    {
                        chosenTrack = ti;
                        break;
                    }
                }
                // Second pass: label contains the language name.
                if (chosenTrack < 0)
                {
                    var kw = LanguageLabelKeywordForCode(effectiveCode);
                    if (kw != null)
                    {
                        foreach (var ti in subtitleIndices)
                        {
                            var label = (tracks[ti].Label ?? string.Empty).ToLowerInvariant();
                            if (label.Contains(kw, StringComparison.OrdinalIgnoreCase))
                            {
                                chosenTrack = ti;
                                break;
                            }
                        }
                    }
                }
                // Third pass: addon labels are "<lang> (addon-name)"; match the 2-letter code.
                if (chosenTrack < 0)
                {
                    foreach (var ti in subtitleIndices)
                    {
                        var label = (tracks[ti].Label ?? string.Empty).Trim();
                        if (label.Length >= 2 &&
                            label.Substring(0, 2).Equals(effectiveCode, StringComparison.OrdinalIgnoreCase))
                        {
                            chosenTrack = ti;
                            break;
                        }
                    }
                }

                if (chosenTrack >= 0)
                {
                    tracks.SetPresentationMode((uint)chosenTrack, TimedMetadataTrackPresentationMode.PlatformPresented);
                    foreach (var ti in subtitleIndices)
                    {
                        if (ti == chosenTrack)
                        {
                            continue;
                        }
                        tracks.SetPresentationMode((uint)ti, TimedMetadataTrackPresentationMode.Disabled);
                    }
                    _lastSubtitleSelection = subtitleIndices.IndexOf(chosenTrack) + 1;
                    Log("info", "Subtitles: auto-selected \"" +
                        (tracks[chosenTrack].Label ?? tracks[chosenTrack].Language ?? "?") +
                        "\" for language \"" + effectiveCode + "\"");
                    return true;
                }
                Log("warn", "Subtitles: no track matched language \"" + effectiveCode + "\" — leaving as the player defaults");
                return false;
            }
            catch (Exception ex)
            {
                Log("warn", "Subtitle language auto-select failed: " + ex.Message);
                return false;
            }
        }

        private static string GuessContentType(PlayRequest request, string probedType)
        {
            var text = string.Join(" ", request.FileName, request.Description, request.Name, request.Url);
            var match = Regex.Match(text, @"\.(mkv|mp4|m4v|webm|mov)(?![a-z0-9])", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "mkv":
                        return "video/x-matroska";
                    case "webm":
                        return "video/webm";
                    case "mov":
                        return "video/quicktime";
                    default:
                        return "video/mp4";
                }
            }

            return !string.IsNullOrEmpty(probedType) && probedType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? probedType : null;
        }

        // Some encodes carry HDR only in the HEVC bitstream; the Xbox reports no transfer
        // function for them. Used only in that case.
        private static bool NameSaysHdr(PlayRequest request)
        {
            if (request == null)
            {
                return false;
            }

            var text = string.Join(" ", request.FileName, request.Description, request.Name);
            return Regex.IsMatch(text, @"(?<![a-z])(hdr|hdr10|hdr10\+|dv|dovi|dolby.?vision)(?![a-z])", RegexOptions.IgnoreCase);
        }

        // Restart-seek workaround: the player reads a second position far ahead of playback;
        // a manual forward+back seek during playback fixed it.
        private async Task RestartSeekBeforePlayAsync(MediaPlayer player, int seconds)
        {
            var session = player.PlaybackSession;
            if (_cache == null || !session.CanSeek || session.NaturalDuration < TimeSpan.FromSeconds(seconds + 60))
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            var forward = await SeekAndWaitAsync(session, TimeSpan.FromSeconds(seconds));
            var back = await SeekAndWaitAsync(session, TimeSpan.Zero);
            Log(forward && back ? "info" : "warn", string.Format("Restart seek before play (+{0} s, then 0:00) {1} in {2} ms",
                seconds, forward && back ? "done" : "timed out", watch.ElapsedMilliseconds));
        }

        private async Task RestartSeekAfterPlayAsync(MediaPlayer player, int seconds)
        {
            var session = player.PlaybackSession;
            if (_cache == null || !session.CanSeek || session.NaturalDuration < TimeSpan.FromSeconds(seconds + 60))
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            while (player == _mediaPlayer && watch.Elapsed < TimeSpan.FromSeconds(15) &&
                !(session.PlaybackState == MediaPlaybackState.Playing && session.Position >= TimeSpan.FromSeconds(1)))
            {
                await Task.Delay(100);
            }

            if (player != _mediaPlayer)
            {
                return;
            }

            var from = session.Position;
            Log("info", string.Format("Restart seek after play: +{0} s from {1:hh\\:mm\\:ss} (waited {2} ms for playback)", seconds, from, watch.ElapsedMilliseconds));
            watch.Restart();
            var forward = await SeekAndWaitAsync(session, from + TimeSpan.FromSeconds(seconds));
            await Task.Delay(1000);
            var back = player == _mediaPlayer && await SeekAndWaitAsync(session, from);
            Log(forward && back ? "info" : "warn", string.Format("Restart seek after play {0} in {1} ms",
                forward && back ? "done" : "timed out", watch.ElapsedMilliseconds));
        }

        private string DescribeRestartSeek()
        {
            var seconds = RestartSeekModes[_restartSeekMode];
            return seconds == 0 ? "off" : seconds > 0 ? "+" + seconds + " s after play" : "+" + (-seconds) + " s before play";
        }

        private async Task<bool> SeekAndWaitAsync(MediaPlaybackSession session, TimeSpan target)
        {
            var done = new TaskCompletionSource<bool>();
            Windows.Foundation.TypedEventHandler<MediaPlaybackSession, object> handler = (s, e) => done.TrySetResult(true);
            session.SeekCompleted += handler;
            try
            {
                _cache?.SeekStarted();
                session.Position = target;
                return await Task.WhenAny(done.Task, Task.Delay(10000)) == done.Task;
            }
            finally
            {
                session.SeekCompleted -= handler;
            }
        }

        // Holds playback until ~15 s of video is in RAM (or 15 s pass).
        private async Task PrebufferAsync(MediaPlayer player)
        {
            var cache = _cache;
            var duration = player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (cache == null || duration <= 0)
            {
                return;
            }

            var bytesPerSecond = cache.Size / duration;
            var target = (long)Math.Min(cache.AheadBytes / 2, bytesPerSecond * 15);
            var watch = Stopwatch.StartNew();
            _osdMessage = "Buffering...";
            ShowOsd();
            while (cache.BufferedAheadBytes < target && watch.Elapsed < TimeSpan.FromSeconds(15) && player == _mediaPlayer)
            {
                await Task.Delay(250);
            }

            var buffered = cache.BufferedAheadBytes;
            _osdMessage = null;
            Log("info", string.Format("Prebuffered {0} MB ({1:0.0} s of video) in {2} ms", buffered >> 20, buffered / bytesPerSecond, watch.ElapsedMilliseconds));
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (sender != _mediaPlayer)
                {
                    return;
                }

                var video = DescribeMedia(sender, out var width, out var height, out var frameRate, out var transferFunction, out var subtype);
                Log("info", video);

                // Apply persistent display preferences before choosing the HDMI mode.
                _displayModes.MatchResolution = CommunitySettings.MatchResolution;
                _displayModes.MatchRefreshRate = CommunitySettings.MatchRefreshRate;

                // Dolby Vision only when the Xbox decodes it as DV; DV MKVs decode as HEVC + HDR10 base layer.
                var range = IsDolbyVisionSubtype(subtype) ? VideoRange.DolbyVision : RangeFromTransferFunction(transferFunction);
                var fromName = false;
                if (range == VideoRange.Sdr && transferFunction == null && NameSaysHdr(_current))
                {
                    range = VideoRange.Hdr10;
                    fromName = true;
                }

                // HDR quality override (auto / force HDR / force SDR).
                var hdrMode = CommunitySettings.HdrQualityMode;
                if (hdrMode == 1)
                {
                    range = VideoRange.Hdr10;
                }
                else if (hdrMode == 2)
                {
                    range = VideoRange.Sdr;
                }

                Log("info", "Video range: " + range + " (decoder subtype " + subtype + ", transfer function " + (transferFunction?.ToString() ?? "unknown") + (fromName ? ", HDR from release name" : string.Empty) + (hdrMode != 0 ? ", forced by setting" : string.Empty) + ")");
                Log("info", await _displayModes.SwitchAsync(width, height, frameRate, range));

                if (!_displayRequestActive)
                {
                    _displayRequest.RequestActive();
                    _displayRequestActive = true;
                }

                var restartSeek = RestartSeekModes[_restartSeekMode];
                if (restartSeek < 0)
                {
                    await RestartSeekBeforePlayAsync(sender, -restartSeek);
                }

                await PrebufferAsync(sender);
                if (sender != _mediaPlayer)
                {
                    return;
                }

                // Resume from the saved position.
                var session = sender.PlaybackSession;
                if (_resumePosition > TimeSpan.FromSeconds(5) &&
                    session.NaturalDuration > TimeSpan.Zero &&
                    _resumePosition < session.NaturalDuration - TimeSpan.FromSeconds(CommunitySettings.ResumeEndThresholdSeconds))
                {
                    _cache?.SeekStarted();
                    session.Position = _resumePosition;
                    Log("info", "Resumed from " + _resumePosition.ToString(@"hh\:mm\:ss"));
                    ShowOsd("Resumed from " + _resumePosition.ToString(@"hh\:mm\:ss"));
                }

                // Apply preferred AUDIO track BEFORE Play so the first frame uses the right language.
                ApplyPreferredAudioTrack(sender);

                sender.Play();
                StartDiagnostics();
                ShowOsd();

                if (restartSeek > 0)
                {
                    await RestartSeekAfterPlayAsync(sender, restartSeek);
                }
            });
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (sender != _mediaPlayer)
                {
                    return;
                }

                var code = args.ExtendedErrorCode == null ? string.Empty : string.Format(" (0x{0:X8})", args.ExtendedErrorCode.HResult);
                LogPlaybackStatus("at failure");
                Log("error", "Native player failed: " + args.Error + code + " " + args.ErrorMessage);
                StopPlayback(resetDisplay: true);
                SetDebugVisible(true);
                GoBackOrHome();
            });
        }

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (sender != _mediaPlayer)
                {
                    return;
                }

                var finished = sender.PlaybackSession.Position >=
                    sender.PlaybackSession.NaturalDuration - TimeSpan.FromSeconds(CommunitySettings.ResumeEndThresholdSeconds);

                if (finished)
                {
                    ClearResumePosition(_resumeKey);
                    _resumePosition = TimeSpan.Zero;
                }

                if (finished && CommunitySettings.AutoContinue)
                {
                    var played = _current;
                    StopPlayback(resetDisplay: false);
                    var advanced = await TryAdvanceQueueAsync();
                    if (advanced)
                    {
                        return;
                    }

                    Log("info", "Auto-continue: no next item in the queue");
                    _current = played;
                    StopPlayback(resetDisplay: true);
                    GoBackOrHome();
                    return;
                }

                StopPlayback(resetDisplay: true);
                GoBackOrHome();
            });
        }

        private void StopPlayback(bool resetDisplay)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.MediaOpened -= OnMediaOpened;
                _mediaPlayer.MediaFailed -= OnMediaFailed;
                _mediaPlayer.MediaEnded -= OnMediaEnded;
                _mediaPlayer.PlaybackSession.BufferingStarted -= OnBufferingStarted;
                _mediaPlayer.PlaybackSession.BufferingEnded -= OnBufferingEnded;
                Player.SetMediaPlayer(null);
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            if (_cache != null)
            {
                // Release the read-ahead buffers now so the next playback gets the full budget.
                _cache.Dispose();
                _cache = null;
                GC.Collect();
            }

            _diagnosticsTimer?.Stop();
            _osdTimer?.Stop();
            Osd.Visibility = Visibility.Collapsed;
            AudioMenu.Visibility = Visibility.Collapsed;
            SubtitleMenu.Visibility = Visibility.Collapsed;
            HelpOverlay.Visibility = Visibility.Collapsed;

            if (_displayRequestActive)
            {
                _displayRequest.RequestRelease();
                _displayRequestActive = false;
            }

            if (resetDisplay)
            {
                _ = _displayModes.ResetAsync();
            }
        }

        // B / playback-ended / playback-failed: return to the previous page (Detail or Home).
        private void GoBackOrHome()
        {
            if (Frame == null)
            {
                return;
            }

            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                return;
            }

            Frame.Navigate(typeof(HomePage));
        }

        private static string DescribeMedia(MediaPlayer player, out uint width, out uint height, out double frameRate, out uint? transferFunction, out string subtype)
        {
            subtype = string.Empty;
            width = player.PlaybackSession.NaturalVideoWidth;
            height = player.PlaybackSession.NaturalVideoHeight;
            frameRate = 0;
            transferFunction = null;
            var parts = new List<string>();

            if (player.Source is MediaPlaybackItem item)
            {
                if (item.VideoTracks.Count > 0)
                {
                    var props = item.VideoTracks[0].GetEncodingProperties();
                    subtype = props.Subtype ?? string.Empty;
                    if (props.FrameRate.Denominator != 0)
                    {
                        frameRate = (double)props.FrameRate.Numerator / props.FrameRate.Denominator;
                    }

                    if (props.Properties.TryGetValue(TransferFunctionKey, out var value) && value != null)
                    {
                        transferFunction = Convert.ToUInt32(value);
                    }

                    parts.Add(string.Format("video {0} {1}x{2} {3:0.###}fps", props.Subtype, width, height, frameRate));
                }

                parts.AddRange(item.AudioTracks.Select(t =>
                {
                    var audio = t.GetEncodingProperties();
                    return string.Format("audio {0} {1}ch [{2}]", audio.Subtype, audio.ChannelCount, t.Language);
                }));
            }

            return "Opened: " + (parts.Count == 0 ? "no track info" : string.Join(", ", parts));
        }

        private static bool IsDolbyVisionSubtype(string subtype)
        {
            var value = (subtype ?? string.Empty).ToUpperInvariant();
            return value.Contains("DVH") || value.Contains("DOVI") || value.Contains("DVAV");
        }

        private static VideoRange RangeFromTransferFunction(uint? transferFunction)
        {
            switch (transferFunction)
            {
                case 15:
                    return VideoRange.Hdr10;
                case 16:
                    return VideoRange.Hlg;
                default:
                    return VideoRange.Sdr;
            }
        }

        private void StartDiagnostics()
        {
            if (_diagnosticsTimer == null)
            {
                _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _diagnosticsTimer.Tick += (sender, e) =>
                {
                    LogPace();
                    if (++_paceTicks % 6 == 0)
                    {
                        LogPlaybackStatus("status");
                    }
                };
            }

            _paceTicks = 0;
            _paceClock = null;
            _diagnosticsTimer.Start();
        }

        // Every 5 s: media pace vs wall clock, and download rate / buffer usage.
        private void LogPace()
        {
            try
            {
                var session = _mediaPlayer?.PlaybackSession;
                if (session == null)
                {
                    return;
                }

                var position = session.Position;
                double download;
                try
                {
                    download = session.DownloadProgress;
                }
                catch (Exception)
                {
                    download = -1;
                }

                var state = session.PlaybackState;
                if (_paceClock == null)
                {
                    _paceClock = Stopwatch.StartNew();
                    _lastPosition = position;
                    _lastDownload = download;
                    return;
                }

                var wall = _paceClock.Elapsed.TotalSeconds;
                _paceClock.Restart();
                var media = (position - _lastPosition).TotalSeconds;
                var rate = "n/a";
                var cache = _cache;
                if (cache != null)
                {
                    var bytes = cache.DownloadedBytes;
                    var bytesPerSecond = (bytes - _lastCacheBytes) / wall;
                    _lastCacheBytes = bytes;
                    var ahead = cache.BufferedAheadBytes;
                    var aheadSeconds = _fileSize > 0 && session.NaturalDuration.TotalSeconds > 0 ? ahead / (_fileSize / session.NaturalDuration.TotalSeconds) : 0;
                    rate = string.Format("{0:0} Mbps, buffer {1} MB ({2:0} s), waited reads {3}", bytesPerSecond * 8 / 1000000, ahead >> 20, aheadSeconds, cache.WaitedReads);
                }
                else if (_fileSize > 0 && download >= 0 && _lastDownload >= 0 && wall > 0)
                {
                    var bytesPerSecond = (download - _lastDownload) * _fileSize / wall;
                    rate = (bytesPerSecond / 1048576).ToString("0.0") + " MB/s (" + (bytesPerSecond * 8 / 1000000).ToString("0") + " Mbps)";
                }

                var stalled = state == MediaPlaybackState.Playing && media < wall * 0.9;
                Log(stalled ? "warn" : "info", string.Format(
                    "Pace{0}: {1:hh\\:mm\\:ss} +{2:0.0}s media / {3:0.0}s wall, download {4} ({5:P1}), {6}",
                    stalled ? " STALL" : string.Empty, position, media, wall, rate, download, state));
                if (cache != null)
                {
                    Log("info", cache.TakeViewSummary());
                }

                _lastPosition = position;
                _lastDownload = download;

                var duration = session.NaturalDuration.TotalSeconds;
                if (!_bitrateLogged && _fileSize > 0 && duration > 0)
                {
                    _bitrateLogged = true;
                    Log("info", string.Format("File {0:0.00} GB, average bitrate {1:0.0} Mbps", _fileSize / 1073741824.0, _fileSize * 8 / duration / 1000000));
                }

                // Save the resume position periodically. Only move it forward.
                if (position > _resumePosition && (position - _resumePosition).TotalSeconds >= 5)
                {
                    _resumePosition = position;
                    SaveResumePosition(_resumeKey, position);
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Pace unavailable: " + ex.GetType().Name);
            }
        }

        private void LogPlaybackStatus(string reason)
        {
            // Each value is read separately: on Xbox some PlaybackSession properties throw.
            try
            {
                var session = _mediaPlayer?.PlaybackSession;
                if (session == null)
                {
                    return;
                }

                Log("info", "Playback " + reason + ": " +
                    Read(() => session.Position.ToString(@"hh\:mm\:ss")) + " / " +
                    Read(() => session.NaturalDuration.ToString(@"hh\:mm\:ss")) +
                    ", state " + Read(() => session.PlaybackState) +
                    ", downloaded " + Read(() => session.DownloadProgress.ToString("P1")) +
                    ", memory " + Read(() => MemoryManager.AppMemoryUsage >> 20) + " / " + Read(() => MemoryManager.AppMemoryUsageLimit >> 20) + " MB");
            }
            catch (Exception ex)
            {
                Log("warn", "Playback status unavailable: " + ex.GetType().Name);
            }
        }

        private static string Read<T>(Func<T> read)
        {
            try
            {
                return Convert.ToString(read());
            }
            catch (Exception ex)
            {
                return "n/a(" + ex.GetType().Name + ")";
            }
        }

        private void OnBufferingStarted(MediaPlaybackSession sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => LogPlaybackStatus("buffering started"));
        }

        private void OnBufferingEnded(MediaPlaybackSession sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => LogPlaybackStatus("buffering ended"));
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = HandleBack();
        }

        // Player-specific back: close the top-most overlay (help, menus, debug); B stops
        // playback and navigates back to Detail (or Home).
        private bool HandleBack()
        {
            if (unchecked(Environment.TickCount - _lastBackTick) < 500)
            {
                return true;
            }

            _lastBackTick = Environment.TickCount;

            if (HelpOverlay.Visibility == Visibility.Visible)
            {
                CloseHelpOverlay();
                Log("info", "Controls help closed");
                return true;
            }

            if (AudioMenu.Visibility == Visibility.Visible)
            {
                CloseAudioMenu();
                Log("info", "Audio menu closed");
                return true;
            }

            if (SubtitleMenu.Visibility == Visibility.Visible)
            {
                CloseSubtitleMenu();
                Log("info", "Subtitle menu closed");
                return true;
            }

            if (DebugPanel.Visibility == Visibility.Visible)
            {
                SetDebugVisible(false);
                return true;
            }

            LogPlaybackStatus("stopped by user");
            StopPlayback(resetDisplay: true);
            Log("info", "Playback stopped by user");
            GoBackOrHome();
            return true;
        }

        // ---- Gamepad ---------------------------------------------------------------------------

        private void OnPadButton(PadButton button)
        {
            Log("info", "SRC xinput btn=" + button + " pads=" + SafePadCount());
            HandlePadButton(button);
        }

        private static int SafePadCount()
        {
            try { return Windows.Gaming.Input.Gamepad.Gamepads.Count; }
            catch (System.Exception) { return -1; }
        }

        // Dedup state: prevents the same button from firing twice when it arrives via two
        // input paths (XInput + accelerator-key) within the debounce window.
        private void HandlePadButton(PadButton button)
        {
            var nowMs = Environment.TickCount;
            if (_lastPadBtn == button && unchecked(nowMs - _lastPadBtnMs) < 200)
            {
                return;
            }
            _lastPadBtn = button;
            _lastPadBtnMs = nowMs;

            if (DebugPanel.Visibility == Visibility.Visible)
            {
                if (button == PadButton.Y)
                {
                    _restartSeekMode = (_restartSeekMode + 1) % RestartSeekModes.Length;
                    SaveSetting("restartSeek", _restartSeekMode);
                    Log("info", "Restart seek: " + DescribeRestartSeek() + " (applies to the next playback)");
                    UpdateDebugHeader();
                }
                else if (button == PadButton.X)
                {
                    _attachSubtitles = !_attachSubtitles;
                    SaveSetting("attachSubtitles", _attachSubtitles);
                    Log("info", "Attach external subtitles: " + (_attachSubtitles ? "ON" : "OFF") + " (applies to the next playback)");
                    UpdateDebugHeader();
                }

                return;
            }

            HandlePlayerButton(button);
        }

        private void HandlePlayerButton(PadButton button)
        {
            var session = _mediaPlayer?.PlaybackSession;
            if (session == null)
            {
                return;
            }

            // Controls help overlay: View (Guide) toggles it; while open the first button closes it.
            if (HelpOverlay.Visibility == Visibility.Visible)
            {
                CloseHelpOverlay();
                Log("info", "Controls help closed");
                return;
            }
            if (button == PadButton.View)
            {
                OpenHelpOverlay();
                return;
            }

            // The open menu owns the D-pad and A; B closes it via the system back path.
            var menuOpen = AudioMenu.Visibility == Visibility.Visible || SubtitleMenu.Visibility == Visibility.Visible;
            if (menuOpen)
            {
                switch (button)
                {
                    case PadButton.Up:
                        if (AudioMenu.Visibility == Visibility.Visible) MoveAudioMenu(down: false);
                        else MoveSubtitleMenu(down: false);
                        break;
                    case PadButton.Down:
                        if (AudioMenu.Visibility == Visibility.Visible) MoveAudioMenu(down: true);
                        else MoveSubtitleMenu(down: true);
                        break;
                    case PadButton.A:
                    case PadButton.Menu:
                        if (AudioMenu.Visibility == Visibility.Visible) SelectAudioMenu();
                        else SelectSubtitleMenu();
                        break;
                    case PadButton.Left:
                        StepSubtitleValueRow(-1);
                        break;
                    case PadButton.Right:
                        StepSubtitleValueRow(1);
                        break;
                }

                return;
            }

            switch (button)
            {
                case PadButton.Y:
                    OpenAudioMenu();
                    return;
                case PadButton.X:
                    OpenSubtitleMenu();
                    return;
                case PadButton.A:
                case PadButton.Menu:
                    if (session.PlaybackState == MediaPlaybackState.Playing)
                    {
                        _mediaPlayer.Pause();
                    }
                    else
                    {
                        _mediaPlayer.Play();
                    }

                    break;
                case PadButton.Left:
                    if (!_seekInProgress)
                    {
                        _seekInProgress = true;
                        _ = SeekAsync(session, -10).ContinueWith(_ => _seekInProgress = false);
                    }
                    break;
                case PadButton.Right:
                    if (!_seekInProgress)
                    {
                        _seekInProgress = true;
                        _ = SeekAsync(session, 10).ContinueWith(_ => _seekInProgress = false);
                    }
                    break;
                case PadButton.LeftShoulder:
                    if (!_seekInProgress)
                    {
                        _seekInProgress = true;
                        _ = SeekAsync(session, -60).ContinueWith(_ => _seekInProgress = false);
                    }
                    break;
                case PadButton.RightShoulder:
                    if (!_seekInProgress)
                    {
                        _seekInProgress = true;
                        _ = SeekAsync(session, 60).ContinueWith(_ => _seekInProgress = false);
                    }
                    break;
            }

            ShowOsd();
        }

        // Seek with buffer wait: pause, wait until the read-ahead buffer has ~3 s at the
        // target, then resume. Without this A/V desyncs after a seek on heavy 4K files.
        private async Task SeekAsync(MediaPlaybackSession session, double seconds)
        {
            if (!session.CanSeek)
            {
                return;
            }

            var target = session.Position + TimeSpan.FromSeconds(seconds);
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }

            if (session.NaturalDuration > TimeSpan.Zero && target > session.NaturalDuration)
            {
                target = session.NaturalDuration - TimeSpan.FromSeconds(1);
            }

            var wasPlaying = session.PlaybackState == MediaPlaybackState.Playing;
            if (wasPlaying)
            {
                _mediaPlayer?.Pause();
            }

            _cache?.SeekStarted();
            session.Position = target;

            var cache = _cache;
            if (cache != null)
            {
                var bytesPerSecond = _fileSize > 0 && session.NaturalDuration.TotalSeconds > 0
                    ? (double)cache.Size / session.NaturalDuration.TotalSeconds
                    : 0;
                var targetBytes = bytesPerSecond > 0 ? (long)(bytesPerSecond * 3) : 4 * 1024 * 1024; // 4 MB fallback
                var watch = Stopwatch.StartNew();
                while (cache.BufferedAheadBytes < targetBytes && watch.Elapsed < TimeSpan.FromSeconds(8) && _mediaPlayer != null)
                {
                    await Task.Delay(100);
                }

                Log("info", string.Format("Seek buffer: {0} MB ahead ({1} ms wait)", cache.BufferedAheadBytes >> 20, watch.ElapsedMilliseconds));
            }

            if (wasPlaying && _mediaPlayer != null)
            {
                _mediaPlayer.Play();
            }
        }

        // ---- Subtitles --------------------------------------------------------------------------

        private async Task AddExternalSubtitlesAsync(MediaSource source, PlayRequest request)
        {
            var delay = CommunitySettings.SubtitleDelay;
            var rate = CommunitySettings.SubtitleRate;
            var needsTiming = Math.Abs(delay) >= 0.001 || Math.Abs(rate - 1.0) >= 0.001;

            foreach (var subtitle in request.Subtitles)
            {
                var label = string.IsNullOrEmpty(subtitle.Label) ? subtitle.Language : subtitle.Label;
                try
                {
                    TimedTextSource timedText;
                    if (needsTiming)
                    {
                        var localPath = await DownloadSubtitleToLocalAsync(subtitle.Url, delay, rate);
                        timedText = TimedTextSource.CreateFromUri(new Uri(localPath ?? subtitle.Url));
                    }
                    else
                    {
                        timedText = TimedTextSource.CreateFromUri(new Uri(subtitle.Url));
                    }

                    timedText.Resolved += (sender, args) =>
                    {
                        if (args.Error != null)
                        {
                            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Log("warn", "Subtitle failed (" + label + "): " + args.Error.ErrorCode));
                            return;
                        }
                        foreach (var track in args.Tracks)
                        {
                            track.Label = label;
                        }
                    };
                    source.ExternalTimedTextSources.Add(timedText);
                }
                catch (Exception ex)
                {
                    Log("warn", "Subtitle skipped (" + subtitle.Label + "): " + ex.Message);
                }
            }

            Log("info", "External subtitles: " + request.Subtitles.Count +
                (needsTiming ? " (timing: " + delay + "s / " + rate + "x)" : ""));
        }

        // Reads an IHttpContent as a UTF-8 string via ReadAsInputStreamAsync (the approach
        // BufferedHttpStream.cs uses successfully on the Xbox).
        private static async Task<string> ReadHttpContentAsString(Windows.Web.Http.IHttpContent content)
        {
            using (var stream = await content.ReadAsInputStreamAsync())
            {
                var parts = new List<byte>();
                var blockBuffer = new Windows.Storage.Streams.Buffer(64 * 1024);
                const int maxBytes = 10 * 1024 * 1024; // 10 MB safety cap
                while (parts.Count < maxBytes)
                {
                    var read = await stream.ReadAsync(blockBuffer, 64 * 1024, Windows.Storage.Streams.InputStreamOptions.None);
                    if (read.Length == 0)
                    {
                        break;
                    }
                    var reader = Windows.Storage.Streams.DataReader.FromBuffer(read);
                    var block = new byte[(int)read.Length];
                    reader.ReadBytes(block);
                    parts.AddRange(block);
                }
                return System.Text.Encoding.UTF8.GetString(parts.ToArray());
            }
        }

        // Downloads a subtitle, applies delay/speed, saves to the app's temp folder.
        // MUST run on the UI thread: Windows.Web.Http and the Storage APIs are
        // apartment-threaded in UWP (off-thread they throw RPC_E_WRONG_THREAD).
        private async Task<string> DownloadSubtitleToLocalAsync(string url, double delay, double rate)
        {
            try
            {
                using (var client = new Windows.Web.Http.HttpClient())
                {
                    var response = await client.GetAsync(new Uri(url));
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }
                    var content = await ReadHttpContentAsString(response.Content);
                    if (string.IsNullOrEmpty(content))
                    {
                        Log("warn", "Subtitle download: empty content");
                        return null;
                    }
                    var modified = SubtitleTimingModifier.Modify(content, delay, rate);
                    if (modified == content)
                    {
                        Log("warn", string.Format("Subtitle download: no timestamps matched (len={0}, head=\"{1}\")",
                            content.Length, content.Substring(0, Math.Min(120, content.Length))));
                        return null;
                    }
                    Log("info", string.Format("Subtitle download: modified {0} chars -> {1} chars",
                        content.Length, modified.Length));

                    var ext = System.IO.Path.GetExtension(new Uri(url).LocalPath);
                    if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".srt";
                    var fileName = "sub_" + Guid.NewGuid().ToString("N") + ext;
                    var folder = Windows.Storage.ApplicationData.Current.TemporaryFolder;
                    var file = await folder.CreateFileAsync(fileName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                    await Windows.Storage.FileIO.WriteTextAsync(file, modified);
                    return file.Path; // ms-appdata:///temp/sub_xxx.srt
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Subtitle download failed: " + ex.Message);
                return null;
            }
        }

        // ---- Subtitle delay / speed via content modification ----
        // On the Xbox, TimedMetadataTrack.SubDelay and .SubRate are absent. The working
        // path is to download each subtitle, rewrite SRT/VTT cue timings with
        // SubtitleTimingModifier, save to a local temp file, and feed the player via
        // TimedTextSource.CreateFromUri(localPath). Live re-apply rebuilds the entire
        // MediaPlaybackItem with new local subtitle files and swaps MediaPlayer.Source
        // (which IS settable, unlike MediaPlaybackItem.Source).
        private bool _retimeInProgress;

        private async Task ReapplySubtitleTimingAsync()
        {
            if (_mediaPlayer == null || _current == null || _current.Subtitles.Count == 0)
            {
                return;
            }
            if (_retimeInProgress)
            {
                return; // A re-time is already running; drop this one.
            }

            var request = _current;
            var delay = CommunitySettings.SubtitleDelay;
            var rate = CommunitySettings.SubtitleRate;

            if (Math.Abs(delay) < 0.001 && Math.Abs(rate - 1.0) < 0.001)
            {
                Log("info", "Subtitle timing: back to defaults, no rewrite needed");
                return;
            }

            _retimeInProgress = true;
            try
            {
                Log("info", "Subtitle re-time: downloading with delay " + delay + "s / rate " + rate + "x");

                var session = _mediaPlayer.PlaybackSession;
                var wasPlaying = session.PlaybackState == MediaPlaybackState.Playing;
                if (wasPlaying)
                {
                    _mediaPlayer.Pause();
                }
                var position = session.Position;

                // Download + modify each subtitle SEQUENTIALLY on the UI thread.
                var subCount = request.Subtitles.Count;
                var localPaths = new string[subCount];
                for (var i = 0; i < subCount; i++)
                {
                    localPaths[i] = await DownloadSubtitleToLocalAsync(request.Subtitles[i].Url, delay, rate);
                }

                // Build a new MediaSource with the video URL + local subtitle files.
                var newSource = MediaSource.CreateFromUri(new Uri(request.Url));
                var addedCount = 0;
                for (var i = 0; i < subCount; i++)
                {
                    var sub = request.Subtitles[i];
                    var label = string.IsNullOrEmpty(sub.Label) ? sub.Language : sub.Label;
                    var uri = localPaths[i] != null ? new Uri(localPaths[i]) : new Uri(sub.Url);
                    var timedText = TimedTextSource.CreateFromUri(uri);
                    timedText.Resolved += (sender, args) =>
                    {
                        if (args.Error != null)
                        {
                            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Log("warn", "Subtitle re-time failed (" + label + "): " + args.Error.ErrorCode));
                            return;
                        }
                        foreach (var track in args.Tracks)
                        {
                            track.Label = label;
                        }
                    };
                    newSource.ExternalTimedTextSources.Add(timedText);
                    addedCount++;
                }

                var newItem = new MediaPlaybackItem(newSource);
                newItem.TimedMetadataTracksChanged += (s2, a2) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    LogSubtitleTracks(s2 as MediaPlaybackItem);
                    OnSubtitleTracksAvailable(s2 as MediaPlaybackItem);
                });

                // Swap MediaPlayer.Source (settable on Xbox, unlike MediaPlaybackItem.Source).
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Source = newItem;
                }

                // Wait for the new source to open, then restore state.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (_mediaPlayer != null && !(_mediaPlayer.Source is MediaPlaybackItem) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }

                if (_mediaPlayer != null && _mediaPlayer.Source is MediaPlaybackItem)
                {
                    ApplyPreferredAudioTrack(_mediaPlayer);
                    if (position > TimeSpan.Zero)
                    {
                        _mediaPlayer.PlaybackSession.Position = position;
                    }
                    if (wasPlaying)
                    {
                        _mediaPlayer.Play();
                    }
                    Log("info", "Subtitle re-time: applied delay " + delay + "s / rate " + rate + "x to " + addedCount + " subtitle(s)");
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Subtitle re-time failed: " + ex.Message);
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Play();
                }
            }
            finally
            {
                _retimeInProgress = false;
            }
        }

        // Guards for OnSubtitleTracksAvailable: the per-playback flag prevents the
        // auto-select block from re-firing on subsequent TimedMetadataTracksChanged events.
        private MediaPlaybackItem _subtitlePrefItem;
        private bool _subtitlePrefApplied;

        // External subtitle tracks resolve ASYNCHRONOUSLY — TimedMetadataTracksChanged is
        // the reliable signal that the full track set is available.
        private void OnSubtitleTracksAvailable(MediaPlaybackItem item)
        {
            if (item == null)
            {
                return;
            }

            var isNew = item != _subtitlePrefItem;
            if (isNew)
            {
                _subtitlePrefItem = item;
                _subtitlePrefApplied = false;
            }

            if (_subtitlePrefApplied)
            {
                return;
            }

            var userChose = _lastSubtitleSelection > 0;
            if (!userChose)
            {
                // Pass the item DIRECTLY (from the event) — not from MediaPlayer.Source.
                var selected = ApplyPreferredLanguageTracks(item);
                if (selected)
                {
                    _subtitlePrefApplied = true;
                }
            }
            else
            {
                // Restore the user's manual selection on this new item (re-time swap).
                var indices = GetOrderedSubtitleTrackIndices(item);
                if (indices.Count >= _lastSubtitleSelection)
                {
                    TryRestoreManualSubtitleSelection(item, _lastSubtitleSelection);
                    _subtitlePrefApplied = true;
                }
                else
                {
                    Log("info", "Subtitle selection pending: have " + indices.Count +
                        " tracks, need " + _lastSubtitleSelection + " — waiting for more");
                }
            }
        }

        private void TryRestoreManualSubtitleSelection(MediaPlaybackItem item, int row)
        {
            if (item == null || row <= 0)
            {
                return;
            }
            try
            {
                var tracks = item.TimedMetadataTracks;
                var indices = GetOrderedSubtitleTrackIndices(item);
                if (row > indices.Count)
                {
                    row = indices.Count;
                }
                var trackIndex = indices[row - 1];
                // Enable the chosen track FIRST, then disable the others (avoids a
                // zero-track window where Windows would re-select the first track).
                tracks.SetPresentationMode((uint)trackIndex, TimedMetadataTrackPresentationMode.PlatformPresented);
                foreach (var index in indices)
                {
                    if (index != trackIndex)
                    {
                        tracks.SetPresentationMode((uint)index, TimedMetadataTrackPresentationMode.Disabled);
                    }
                }
                Log("info", "Subtitle selection restored: row " + row +
                    " (" + DescribeSubtitle(tracks[trackIndex]) + ")");
            }
            catch (Exception ex)
            {
                Log("warn", "Could not restore manual subtitle selection: " + ex.Message);
            }
        }

        private static bool IsSubtitleTrack(TimedMetadataTrack track)
        {
            return track.TimedMetadataKind == TimedMetadataKind.Subtitle ||
                   track.TimedMetadataKind == TimedMetadataKind.Caption ||
                   track.TimedMetadataKind == TimedMetadataKind.ImageSubtitle;
        }

        private static string DescribeSubtitle(TimedMetadataTrack track)
        {
            var label = string.IsNullOrEmpty(track.Label) ? (track.Language + " (embedded)") : track.Label;
            return track.TimedMetadataKind == TimedMetadataKind.ImageSubtitle ? label + " [image]" : label;
        }

        private void LogSubtitleTracks(MediaPlaybackItem item)
        {
            try
            {
                var indices = GetOrderedSubtitleTrackIndices(item);
                var labels = indices.Select(i => DescribeSubtitle(item.TimedMetadataTracks[i])).ToList();
                Log("info", "Subtitle tracks: " + labels.Count + (labels.Count > 0 ? " - " + string.Join(", ", labels.Take(15)) : string.Empty));
            }
            catch (Exception ex)
            {
                Log("warn", "Could not list subtitle tracks: " + ex.Message);
            }
        }

        // ---- Subtitle menu (X) -------------------------------------------------------------------

        private void OpenSubtitleMenu()
        {
            if (!(_mediaPlayer?.Source is MediaPlaybackItem item))
            {
                ShowOsd("Subtitles unavailable");
                return;
            }

            List<string> labels = new List<string>();
            try
            {
                var orderedIndices = GetOrderedSubtitleTrackIndices(item);
                foreach (var index in orderedIndices)
                {
                    labels.Add(DescribeSubtitle(item.TimedMetadataTracks[index]));
                }
            }
            catch (Exception ex)
            {
                ShowOsd("Subtitles unavailable (" + ex.Message + ")");
                return;
            }

            var current = _lastSubtitleSelection;

            var rows = new List<string> { "Subtitles off" };
            rows.AddRange(labels);
            // Adjustable rows at the bottom: subtitle delay and speed (live content rewrite).
            rows.Add("Subtitle delay  —  " + FormatSubDelay(CommunitySettings.SubtitleDelay));
            rows.Add("Subtitle speed  —  " + FormatSubRate(CommunitySettings.SubtitleRate));
            _subtitleTrackCount = 1 + labels.Count;
            _subtitleMenuIndex = Math.Min(Math.Max(current, 0), _subtitleTrackCount - 1);
            SubtitleMenuItems.Children.Clear();
            foreach (var row in rows)
            {
                SubtitleMenuItems.Children.Add(BuildMenuRow(row));
            }
            ApplySubtitleMenuHighlight();

            _osdTimer?.Stop();
            SubtitleMenu.Visibility = Visibility.Visible;
            Osd.Visibility = Visibility.Collapsed;
            Log("info", "Subtitle menu: " + labels.Count + " track(s), highlighting row " + _subtitleMenuIndex);
        }

        private static string FormatSubDelay(double seconds)
        {
            var sign = seconds > 0 ? "+" : seconds < 0 ? "-" : "";
            return sign + Math.Abs(seconds).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";
        }

        private static string FormatSubRate(double rate)
        {
            return rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";
        }

        private static TextBlock BuildMenuRow(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 22,
                Foreground = new SolidColorBrush(Colors.White),
                Margin = new Thickness(0, 6, 0, 6),
                Padding = new Thickness(8, 2, 8, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }

        private void MoveSubtitleMenu(bool down)
        {
            if (SubtitleMenu.Visibility != Visibility.Visible || SubtitleMenuItems.Children.Count == 0)
            {
                return;
            }

            var count = SubtitleMenuItems.Children.Count;
            _subtitleMenuIndex = down ? (_subtitleMenuIndex + 1) % count : (_subtitleMenuIndex + count - 1) % count;
            ApplySubtitleMenuHighlight();
        }

        private void ApplySubtitleMenuHighlight()
        {
            var children = SubtitleMenuItems.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var row = children[i] as TextBlock;
                if (row == null)
                {
                    continue;
                }

                var selected = i == _subtitleMenuIndex;
                row.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
                row.Foreground = selected
                    ? new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xD5, 0x4F))
                    : new SolidColorBrush(Colors.White);
            }

            if (_subtitleMenuIndex < children.Count && SubtitleMenuScroll != null)
            {
                const double rowHeight = 40.0;
                double target = _subtitleMenuIndex * rowHeight;
                double min = SubtitleMenuScroll.VerticalOffset;
                double max = min + SubtitleMenuScroll.ViewportHeight;
                if (target < min || target + rowHeight > max)
                {
                    SubtitleMenuScroll.ScrollToVerticalOffset(target);
                }
            }
        }

        private void SelectSubtitleMenu()
        {
            if (SubtitleMenu.Visibility != Visibility.Visible)
            {
                return;
            }

            var chosen = _subtitleMenuIndex;

            if (chosen >= _subtitleTrackCount)
            {
                if (chosen == _subtitleTrackCount)
                {
                    var next = CommunitySettings.SubtitleDelay + 0.5;
                    if (next > 30) next = -30;
                    CommunitySettings.SubtitleDelay = next;
                    ShowOsd("Subtitle delay: " + FormatSubDelay(CommunitySettings.SubtitleDelay));
                    _ = ReapplySubtitleTimingAsync();
                }
                else
                {
                    var next = Math.Round(CommunitySettings.SubtitleRate + 0.05, 2);
                    if (next > 2.0) next = 0.5;
                    CommunitySettings.SubtitleRate = next;
                    ShowOsd("Subtitle speed: " + FormatSubRate(CommunitySettings.SubtitleRate));
                    _ = ReapplySubtitleTimingAsync();
                }
                RebuildSubtitleValueRows();
                return;
            }

            var label = (SubtitleMenuItems.Children[chosen] as TextBlock)?.Text ?? string.Empty;
            CloseSubtitleMenu();
            _lastSubtitleSelection = chosen;

            if (!(_mediaPlayer?.Source is MediaPlaybackItem item))
            {
                return;
            }

            try
            {
                var tracks = item.TimedMetadataTracks;
                var indices = GetOrderedSubtitleTrackIndices(item);
                if (chosen > 0)
                {
                    var trackIndex = indices[chosen - 1];
                    tracks.SetPresentationMode((uint)trackIndex, TimedMetadataTrackPresentationMode.PlatformPresented);
                }
                foreach (var index in indices)
                {
                    if (chosen > 0 && index == indices[chosen - 1])
                    {
                        continue;
                    }
                    tracks.SetPresentationMode((uint)index, TimedMetadataTrackPresentationMode.Disabled);
                }

                Log("info", "Subtitles: " + label);
                ShowOsd("Subtitles: " + label);
            }
            catch (Exception ex)
            {
                Log("warn", "Subtitle switch failed: " + ex.Message);
                ShowOsd("Subtitles unavailable");
            }
        }

        private void CloseSubtitleMenu()
        {
            if (SubtitleMenu.Visibility == Visibility.Visible)
            {
                SubtitleMenu.Visibility = Visibility.Collapsed;
            }
        }

        // ---- Controls help overlay (View) ---------------------------------------------------------

        private void OpenHelpOverlay()
        {
            var rows = new[]
            {
                ("A", "Play / Pause"),
                ("B", "Close menu / Back"),
                ("Y", "Audio tracks"),
                ("X", "Subtitles"),
                ("D-pad Up / Down", "Navigate menus"),
                ("D-pad Left / Right", "±10 s seek (or subtitle delay/speed step)"),
                ("LB / RB", "±60 s seek"),
                ("View / Guide", "This help overlay"),
            };

            var mid = rows.Length / 2;
            HelpLeft.Children.Clear();
            HelpRight.Children.Clear();
            for (var i = 0; i < mid; i++)
            {
                HelpLeft.Children.Add(BuildHelpRow(rows[i].Item1, rows[i].Item2));
            }
            for (var i = mid; i < rows.Length; i++)
            {
                HelpRight.Children.Add(BuildHelpRow(rows[i].Item1, rows[i].Item2));
            }

            _osdTimer?.Stop();
            HelpOverlay.Visibility = Visibility.Visible;
            Osd.Visibility = Visibility.Collapsed;
            Log("info", "Controls help opened (View)");
        }

        private void CloseHelpOverlay()
        {
            if (HelpOverlay.Visibility == Visibility.Visible)
            {
                HelpOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private static Grid BuildHelpRow(string button, string action)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210, GridUnitType.Pixel) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var btn = new TextBlock
            {
                Text = button,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xD5, 0x4F)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(btn, 0);

            var act = new TextBlock
            {
                Text = action,
                FontSize = 20,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(act, 1);

            grid.Children.Add(btn);
            grid.Children.Add(act);
            return grid;
        }

        // D-pad Left/Right: step the highlighted subtitle timing row and apply it live.
        private void StepSubtitleValueRow(int direction)
        {
            if (SubtitleMenu.Visibility != Visibility.Visible)
            {
                return;
            }

            var chosen = _subtitleMenuIndex;
            if (chosen < _subtitleTrackCount)
            {
                return;
            }

            if (chosen == _subtitleTrackCount)
            {
                var next = CommunitySettings.SubtitleDelay + 0.5 * direction;
                if (next > 30) next = -30;
                if (next < -30) next = 30;
                CommunitySettings.SubtitleDelay = Math.Round(next, 2);
                ShowOsd("Subtitle delay: " + FormatSubDelay(CommunitySettings.SubtitleDelay));
                _ = ReapplySubtitleTimingAsync();
            }
            else
            {
                var next = Math.Round(CommunitySettings.SubtitleRate + 0.05 * direction, 2);
                if (next > 2.0) next = 0.5;
                if (next < 0.5) next = 2.0;
                CommunitySettings.SubtitleRate = next;
                ShowOsd("Subtitle speed: " + FormatSubRate(CommunitySettings.SubtitleRate));
                _ = ReapplySubtitleTimingAsync();
            }
            RebuildSubtitleValueRows();
        }

        private void RebuildSubtitleValueRows()
        {
            var children = SubtitleMenuItems.Children;
            if (children.Count > _subtitleTrackCount)
            {
                var delayRow = children[_subtitleTrackCount] as TextBlock;
                if (delayRow != null)
                {
                    delayRow.Text = "Subtitle delay  —  " + FormatSubDelay(CommunitySettings.SubtitleDelay);
                }
            }
            if (children.Count > _subtitleTrackCount + 1)
            {
                var rateRow = children[_subtitleTrackCount + 1] as TextBlock;
                if (rateRow != null)
                {
                    rateRow.Text = "Subtitle speed  —  " + FormatSubRate(CommunitySettings.SubtitleRate);
                }
            }
        }

        // ---- Audio menu (Y) ------------------------------------------------------------------------

        private void OpenAudioMenu()
        {
            if (!(_mediaPlayer?.Source is MediaPlaybackItem item))
            {
                ShowOsd("Audio tracks unavailable");
                return;
            }

            List<string> labels;
            try
            {
                var tracks = item.AudioTracks;
                if (tracks.Count == 0)
                {
                    ShowOsd("No audio tracks");
                    return;
                }

                labels = new List<string>();
                for (var i = 0; i < tracks.Count; i++)
                {
                    labels.Add(DescribeAudioTrack(tracks[i]));
                }
            }
            catch (Exception ex)
            {
                ShowOsd("Audio tracks unavailable (" + ex.Message + ")");
                return;
            }

            int current = 0;
            try
            {
                var tracks = item.AudioTracks;
                current = Math.Min(Math.Max(tracks.SelectedIndex, 0), tracks.Count - 1);
            }
            catch
            {
            }

            _audioMenuIndex = current;
            AudioMenuItems.Children.Clear();
            foreach (var label in labels)
            {
                AudioMenuItems.Children.Add(BuildMenuRow(label));
            }
            ApplyAudioMenuHighlight();

            _osdTimer?.Stop();
            AudioMenu.Visibility = Visibility.Visible;
            Osd.Visibility = Visibility.Collapsed;
            Log("info", "Audio menu: " + labels.Count + " track(s), highlighting row " + _audioMenuIndex);
        }

        private static string DescribeAudioTrack(AudioTrack track)
        {
            try
            {
                var props = track.GetEncodingProperties();
                var label = string.IsNullOrEmpty(track.Label) ? string.Empty : " " + track.Label;
                return string.Format("[{0}] {1} {2}ch{3}", track.Language, props.Subtype, props.ChannelCount, label);
            }
            catch
            {
                return string.IsNullOrEmpty(track.Label) ? track.Language : track.Language + " " + track.Label;
            }
        }

        private void MoveAudioMenu(bool down)
        {
            if (AudioMenu.Visibility != Visibility.Visible || AudioMenuItems.Children.Count == 0)
            {
                return;
            }

            var count = AudioMenuItems.Children.Count;
            _audioMenuIndex = down ? (_audioMenuIndex + 1) % count : (_audioMenuIndex + count - 1) % count;
            ApplyAudioMenuHighlight();
        }

        private void ApplyAudioMenuHighlight()
        {
            var children = AudioMenuItems.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var row = children[i] as TextBlock;
                if (row == null)
                {
                    continue;
                }

                var selected = i == _audioMenuIndex;
                row.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
                row.Foreground = selected
                    ? new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xD5, 0x4F))
                    : new SolidColorBrush(Colors.White);
            }

            if (_audioMenuIndex < children.Count && AudioMenuScroll != null)
            {
                const double rowHeight = 40.0;
                double target = _audioMenuIndex * rowHeight;
                double min = AudioMenuScroll.VerticalOffset;
                double max = min + AudioMenuScroll.ViewportHeight;
                if (target < min || target + rowHeight > max)
                {
                    AudioMenuScroll.ScrollToVerticalOffset(target);
                }
            }
        }

        private void SelectAudioMenu()
        {
            if (AudioMenu.Visibility != Visibility.Visible)
            {
                return;
            }

            var chosen = _audioMenuIndex;
            CloseAudioMenu();

            if (!(_mediaPlayer?.Source is MediaPlaybackItem item))
            {
                return;
            }

            try
            {
                var tracks = item.AudioTracks;
                if (chosen >= tracks.Count)
                {
                    return;
                }

                tracks.SelectedIndex = chosen;
                var label = (AudioMenuItems.Children[chosen] as TextBlock)?.Text ?? string.Empty;
                Log("info", "Audio: " + label);
                ShowOsd("Audio: " + label);
            }
            catch (Exception ex)
            {
                Log("warn", "Audio switch failed: " + ex.Message);
                ShowOsd("Audio tracks unavailable");
            }
        }

        private void CloseAudioMenu()
        {
            if (AudioMenu.Visibility == Visibility.Visible)
            {
                AudioMenu.Visibility = Visibility.Collapsed;
            }
        }

        // ---- OSD ---------------------------------------------------------------------------

        private void ShowOsd(string message)
        {
            _osdMessage = message;
            ShowOsd();
        }

        private void ShowOsd()
        {
            if (_osdTimer == null)
            {
                _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _osdTimer.Tick += (sender, e) => UpdateOsd();
            }

            _osdHideAt = DateTime.UtcNow.AddSeconds(4);
            Osd.Visibility = Visibility.Visible;
            UpdateOsd();
            _osdTimer.Start();
        }

        private void UpdateOsd()
        {
            var session = _mediaPlayer?.PlaybackSession;
            if (session == null)
            {
                _osdTimer?.Stop();
                Osd.Visibility = Visibility.Collapsed;
                return;
            }

            var paused = session.PlaybackState != MediaPlaybackState.Playing;
            if (!paused && DateTime.UtcNow >= _osdHideAt)
            {
                _osdTimer.Stop();
                _osdMessage = null;
                Osd.Visibility = Visibility.Collapsed;
                return;
            }

            var duration = session.NaturalDuration;
            OsdTitle.Text = _current?.ToString() ?? string.Empty;
            OsdInfo.Text = _osdMessage ?? string.Empty;
            OsdInfo.Visibility = string.IsNullOrEmpty(_osdMessage) ? Visibility.Collapsed : Visibility.Visible;
            OsdProgress.Value = duration.TotalSeconds > 0 ? session.Position.TotalSeconds / duration.TotalSeconds : 0;
            OsdTime.Text = string.Format(
                "{0}   {1:h\\:mm\\:ss} / {2:h\\:mm\\:ss}",
                paused ? (session.PlaybackState == MediaPlaybackState.Buffering ? "Buffering" : "Paused") : "Playing",
                session.Position,
                duration);
        }

        // ---- Debug log (F1 / View while not playing) ------------------------------------------

        private void SetDebugVisible(bool visible)
        {
            DebugPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                UpdateDebugHeader();
            }
        }

        private void UpdateDebugHeader()
        {
            DebugHeader.Text = "Debug log  |  Y: restart seek " + DescribeRestartSeek() + "  |  X: subtitles " +
                (_attachSubtitles ? "ON" : "OFF") + "  |  B: close";
        }

        private void LogAsync(string level, string text)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Log(level, text));
        }

        private void Log(string level, string text)
        {
            FileLog.Write(level, text);
            var line = DateTime.Now.ToString("HH:mm:ss") + " " + level.ToUpperInvariant() + " " + text;

            lock (_logLock)
            {
                _log.Add(line.Length > 600 ? line.Substring(0, 600) + "..." : line);
                if (_log.Count > 500)
                {
                    _log.RemoveRange(0, _log.Count - 500);
                }
            }

            if (DebugText != null)
            {
                lock (_logLock)
                {
                    DebugText.Text = string.Join("\n", _log.Skip(Math.Max(0, _log.Count - 40)));
                }
            }
        }

        // ---- Live settings (ISettingsApplier) -------------------------------------------------

        /// <inheritdoc />
        public void ApplyLiveVolume(int volume)
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Volume = Math.Max(0, Math.Min(100, volume));
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Live volume change failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void ApplyLiveRate(double rate)
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.PlaybackRate = Math.Max(0.25, Math.Min(4.0, rate));
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Live rate change failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void ApplyLiveMute(bool muted)
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.IsMuted = muted;
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Live mute change failed: " + ex.Message);
            }
        }

        // ---- Foreground / background (IAppLifecycle) --------------------------------------------

        /// <summary>
        /// Pause-on-background: when the app leaves the foreground and the user opted in,
        /// pause native playback; resume when it returns.
        /// </summary>
        public void OnAppForegroundChanged(bool enteringBackground)
        {
            try
            {
                if (!CommunitySettings.PauseWhenInBackground)
                {
                    return;
                }

                var player = _mediaPlayer;
                if (player == null)
                {
                    return;
                }

                if (enteringBackground)
                {
                    player.Pause();
                    FileLog.Write("info", "Backgrounded: paused native playback");
                }
                else
                {
                    if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused)
                    {
                        player.Play();
                    }
                    FileLog.Write("info", "Foregrounded: resumed native playback");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("warn", "Foreground/background pause failed: " + ex.Message);
            }
        }

        // ---- Accelerator keys (8BitDo remote + F-key shortcuts) ---------------------------------

        private readonly Dictionary<string, int> _accelKeyLastLog = new Dictionary<string, int>();

        private void LogAccelKeyDiagnostics(AcceleratorKeyEventArgs args)
        {
            string tag = (int)args.VirtualKey + ":" + (int)args.KeyStatus.ScanCode;
            int now = Environment.TickCount;
            if (_accelKeyLastLog.TryGetValue(tag, out int last) && (now - last) < 800)
            {
                return;
            }
            _accelKeyLastLog[tag] = now;
            Log("info", string.Format("ACCEL key vk=0x{0:X2} sc={1} ext={2}",
                (int)args.VirtualKey, (int)args.KeyStatus.ScanCode, args.KeyStatus.IsExtendedKey));
        }

        // Keyboard shortcuts for testing on a PC. The 8BitDo remote's buttons arrive as
        // accelerator keys in the 0xC0-0xDF range; we dispatch them here, mark the event
        // handled so nothing leaks, and the 200 ms dedup in HandlePadButton covers the
        // case where the same press also arrives via GamepadInput (XInput).
        private void OnAcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args.EventType != CoreAcceleratorKeyEventType.KeyDown)
            {
                return;
            }

            LogAccelKeyDiagnostics(args);

            // Filter auto-repeat FIRST: Windows re-dispatches KeyDown every ~30 ms while
            // a key is held; each repeat would otherwise re-invoke the action.
            if (args.KeyStatus.WasKeyDown)
            {
                return;
            }

            var vk = (int)args.VirtualKey;
            if (vk >= 0xC0 && vk <= 0xDF)
            {
                switch (args.VirtualKey)
                {
                    case (VirtualKey)0xC3: HandlePadButton(PadButton.A); break;
                    case (VirtualKey)0xC4: HandleBack(); break;
                    case (VirtualKey)0xC5: HandlePadButton(PadButton.X); break;
                    case (VirtualKey)0xC6: HandlePadButton(PadButton.Y); break;
                    case (VirtualKey)0xCB: HandlePadButton(PadButton.Up); break;
                    case (VirtualKey)0xCC: HandlePadButton(PadButton.Down); break;
                    case (VirtualKey)0xCD: HandlePadButton(PadButton.Left); break;
                    case (VirtualKey)0xCE: HandlePadButton(PadButton.Right); break;
                    case (VirtualKey)0xCF: HandlePadButton(PadButton.Menu); break; // Start
                    case (VirtualKey)0xD0: HandlePadButton(PadButton.Share); break; // Select
                    default: break; // sticks/shoulders → GamepadInput owns those.
                }
                args.Handled = true;
                return;
            }

            switch (args.VirtualKey)
            {
                case VirtualKey.F1:
                    SetDebugVisible(DebugPanel.Visibility != Visibility.Visible);
                    args.Handled = true;
                    return;
                case VirtualKey.F2:
                    HandlePadButton(PadButton.Y);
                    args.Handled = true;
                    return;
                case VirtualKey.F3:
                    HandlePadButton(PadButton.X);
                    args.Handled = true;
                    return;
            }
        }
    }
}

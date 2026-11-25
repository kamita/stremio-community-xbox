using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StremioCommunity.Core;
using StremioCommunity.Models;
using Windows.Gaming.Input;
using Windows.UI;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace StremioCommunity.Pages
{
    /// <summary>
    /// Detail page: meta info on the left (poster, name, description, genres, cast),
    /// and on the right either the episode list (series) or the available streams
    /// (movie / episode). A on a row navigates forward (episode → episode detail,
    /// stream → PlayerPage).
    /// </summary>
    public sealed partial class DetailPage : Page
    {
        /// <summary>Navigation arguments passed from the Home page (or a parent episode list).</summary>
        public sealed class Args
        {
            /// <summary>Meta type: "movie" or "series".</summary>
            public string Type;

            /// <summary>Stremio id (base id for series, full id for episodes/movies).</summary>
            public string Id;

            /// <summary>User profile (may be null when not signed in).</summary>
            public StremioProfile Profile;
        }

        private readonly GamepadInput _gamepad = new GamepadInput();
        private readonly StremioApiClient _api = new StremioApiClient();
        private SettingsPanelController _settings; // created in OnNavigatedTo (needs the XAML panel)

        private StremioMeta _meta;
        private StremioProfile _profile;

        private readonly List<ListRow> _rows = new List<ListRow>();
        private int _rowIndex;

        private PadButton _lastPadBtn;
        private int _lastPadBtnMs;
        private int _lastBackTick = Environment.TickCount - 1000;

        private sealed class ListRow
        {
            public TextBlock Block;
            public Action Activate;
        }

        public Args _navArgs;

        public DetailPage() : this(null)
        {
        }

        public DetailPage(Args navArgs)
        {
            _navArgs = navArgs;
            InitializeComponent();
        }

        private bool _loaded; // one-time guard: load runs on first entry, not on every back-stack re-entry

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated += OnAcceleratorKeyActivated;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _gamepad.Pressed += OnPadButton;
            _gamepad.Start();
            base.OnNavigatedTo(e);

            if (!_loaded)
            {
                _loaded = true;
                _settings = new SettingsPanelController(SettingsItems, msg => Log("info", msg));

                var args = _navArgs ?? new Args { Type = "movie", Id = string.Empty };
                var type = args.Type ?? "movie";
                var id = args.Id ?? string.Empty;
                _profile = args.Profile;

                Log("info", "Detail " + type + " " + id);
                _ = LoadAsync(type, id);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated -= OnAcceleratorKeyActivated;
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            _gamepad.Stop();
            base.OnNavigatedFrom(e);
        }

        // ---- Data ------------------------------------------------------------------------------

        private async Task LoadAsync(string type, string id)
        {
            ListHeader.Text = type == "series" ? "Episodes" : "Streams";

            if (_profile == null)
            {
                try
                {
                    _profile = await _api.GetProfileAsync();
                }
                catch (Exception ex)
                {
                    Log("warn", "Profile unavailable: " + ex.Message);
                }
            }

            StremioMeta meta;
            try
            {
                meta = await _api.GetMetaAsync(type, id);
            }
            catch (Exception ex)
            {
                Log("warn", "Meta failed for " + type + " " + id + ": " + ex.Message);
                meta = null;
            }

            if (meta == null)
            {
                meta = new StremioMeta { Type = type, Id = id, Name = id };
                Log("warn", "Meta unavailable for " + type + " " + id + " — showing streams only");
            }

            _meta = meta;
            RenderMeta();

            if (type == "series")
            {
                BuildEpisodes(meta);
            }
            else
            {
                await LoadStreamsAsync(type, id);
            }
        }

        private async Task LoadStreamsAsync(string type, string id)
        {
            var loadingBlock = new TextBlock { Text = "Loading streams…", FontSize = 20, Foreground = new SolidColorBrush(Colors.White), Margin = new Thickness(0, 8, 0, 8) };
            ListItems.Children.Add(loadingBlock);
            _rows.Clear();
            _rows.Add(new ListRow { Block = loadingBlock, Activate = null });
            _rowIndex = 0;

            IReadOnlyList<StremioApiClient.StreamRow> streams;
            try
            {
                streams = await _api.GetStreamsAsync(_profile, type, id);
            }
            catch (Exception ex)
            {
                Log("error", "Streams failed: " + ex.Message);
                streams = new List<StremioApiClient.StreamRow>();
            }

            ListItems.Children.Clear();
            _rows.Clear();

            foreach (var row in streams)
            {
                var stream = row.Stream;
                if (string.IsNullOrEmpty(stream.Url))
                {
                    continue; // torrent-only streams are not playable by the native player
                }

                var text = FormatStream(stream) + "  —  " + row.Name;
                var localId = id;
                _rows.Add(BuildRow(text, () =>
                {
                    Log("info", "Play " + stream.Name + " " + (stream.Description ?? string.Empty));
                    _ = PlayAsync(type, localId, stream);
                }));
            }

            if (_rows.Count == 0)
            {
                _rows.Add(BuildRow("No playable streams found"));
            }

            _rowIndex = 0;
            RenderRows();
            Log("info", "Streams: " + _rows.Count);
        }

        private static string FormatStream(StremioStream stream)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(stream.Name))
            {
                parts.Add(stream.Name);
            }

            if (!string.IsNullOrEmpty(stream.Description))
            {
                parts.Add(stream.Description.Replace('\n', ' ').Trim());
            }

            if (!string.IsNullOrEmpty(stream.FileName))
            {
                parts.Add(stream.FileName);
            }

            return string.Join("  |  ", parts);
        }

        private void RenderMeta()
        {
            var meta = _meta;
            Title.Text = meta.Name ?? meta.Id;
            ReleaseInfo.Text = meta.ReleaseInfo ?? string.Empty;
            ReleaseInfo.Visibility = string.IsNullOrEmpty(meta.ReleaseInfo) ? Visibility.Collapsed : Visibility.Visible;

            var rating = new List<string>();
            if (meta.ImdbRating.HasValue)
            {
                rating.Add("★ " + meta.ImdbRating.Value.ToString("0.#"));
            }

            if (meta.Runtime.HasValue)
            {
                rating.Add(FormatRuntime(meta.Runtime.Value));
            }

            Rating.Text = string.Join("   ", rating);
            Rating.Visibility = rating.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            Description.Text = meta.Description ?? string.Empty;
            Description.Visibility = string.IsNullOrEmpty(meta.Description) ? Visibility.Collapsed : Visibility.Visible;

            Genres.Text = string.Join(", ", meta.Genres);
            Genres.Visibility = meta.Genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            Cast.Text = string.Join(", ", meta.Cast);
            Cast.Visibility = meta.Cast.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrEmpty(meta.Poster))
            {
                try
                {
                    PosterImage.Source = new BitmapImage(new Uri(meta.Poster));
                    return;
                }
                catch (Exception)
                {
                    PosterImage.Source = null;
                }
            }

            PosterImage.Source = null;
        }

        private static string FormatRuntime(long seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return (ts.TotalHours >= 1 ? ((int)ts.TotalHours) + "h " : string.Empty) + ts.Minutes + "m";
        }

        private void BuildEpisodes(StremioMeta meta)
        {
            _rows.Clear();
            foreach (var video in meta.Videos)
            {
                var episodeId = video.Id;
                if (string.IsNullOrEmpty(episodeId))
                {
                    continue;
                }

                var text = video.Name + (string.IsNullOrEmpty(video.Overview)
                    ? string.Empty
                    : "  —  " + video.Overview.Replace('\n', ' ').Trim());
                _rows.Add(BuildRow(text, () =>
                {
                    Log("info", "Open episode " + episodeId);
                    Frame.Navigate(typeof(DetailPage), new Args { Type = "series", Id = episodeId, Profile = _profile });
                }));
            }

            if (_rows.Count == 0)
            {
                _rows.Add(BuildRow("No episodes listed"));
            }

            _rowIndex = 0;
            RenderRows();
        }

        private ListRow BuildRow(string text, Action activate = null)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 20,
                Foreground = new SolidColorBrush(Colors.White),
                Margin = new Thickness(0, 8, 0, 8),
                Padding = new Thickness(8, 4, 8, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ListItems.Children.Add(block);
            return new ListRow { Block = block, Activate = activate };
        }

        private void RenderRows()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var selected = i == _rowIndex;
                _rows[i].Block.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
                _rows[i].Block.Foreground = selected
                    ? new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xD5, 0x4F))
                    : new SolidColorBrush(Colors.White);
            }

            if (_rows.Count > 0)
            {
                var target = _rowIndex * 44.0;
                var min = ListScroll.VerticalOffset;
                var max = min + ListScroll.ViewportHeight;
                if (target < min || target > max - 44)
                {
                    ListScroll.ScrollToVerticalOffset(Math.Max(0, target));
                }
            }
        }

        private async Task PlayAsync(string type, string id, StremioStream stream)
        {
            StremioApiClient.SubtitleResult result;
            try
            {
                result = await _api.CollectSubtitlesAsync(_profile, stream, type, id);
            }
            catch (Exception ex)
            {
                Log("warn", "Subtitle collection failed: " + ex.Message);
                result = new StremioApiClient.SubtitleResult { Subtitles = stream.Subtitles };
            }

            var selection = new PlaybackSelection
            {
                Stream = stream,
                Subtitles = result.Subtitles,
                PreferredSubLang = _profile?.SubtitlesLanguage ?? string.Empty,
                PreferredAudioLang = _profile?.DefaultAudioLanguage ?? string.Empty,
            };

            Log("info", string.Format("Subtitles: {0} (preferred sub \"{1}\", audio \"{2}\")",
                result.Subtitles.Count, selection.PreferredSubLang, selection.PreferredAudioLang));
            Frame.Navigate(typeof(PlayerPage), new PlayerPage.Args { Selection = selection });
        }

        // ---- Settings overlay (Y) ------------------------------------------------------------------

        private void ToggleSettings()
        {
            if (SettingsPanelGrid.Visibility == Visibility.Visible)
            {
                SettingsPanelGrid.Visibility = Visibility.Collapsed;
                Log("info", "Settings closed");
            }
            else
            {
                _settings.Open();
                SettingsPanelGrid.Visibility = Visibility.Visible;
                Log("info", "Settings opened");
            }
        }

        private void HandleSettingsButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up:
                    _settings.Move(false);
                    break;
                case PadButton.Down:
                    _settings.Move(true);
                    break;
                case PadButton.LeftShoulder:
                    _settings.Adjust(-1);
                    break;
                case PadButton.RightShoulder:
                    _settings.Adjust(1);
                    break;
                case PadButton.A:
                    _settings.ToggleOrStep();
                    break;
                case PadButton.Y:
                    SettingsPanelGrid.Visibility = Visibility.Collapsed;
                    Log("info", "Settings closed");
                    break;
            }
        }

        // ---- Gamepad ---------------------------------------------------------------------------

        private void OnPadButton(PadButton button)
        {
            var nowMs = Environment.TickCount;
            if (_lastPadBtn == button && unchecked(nowMs - _lastPadBtnMs) < 200)
            {
                return;
            }

            _lastPadBtn = button;
            _lastPadBtnMs = nowMs;

            if (SettingsPanelGrid.Visibility == Visibility.Visible)
            {
                HandleSettingsButton(button);
                return;
            }

            if (button == PadButton.Y)
            {
                ToggleSettings();
                return;
            }

            if (button == PadButton.Up)
            {
                if (_rows.Count > 0)
                {
                    _rowIndex = (_rowIndex + _rows.Count - 1) % _rows.Count;
                    RenderRows();
                }

                return;
            }

            if (button == PadButton.Down)
            {
                if (_rows.Count > 0)
                {
                    _rowIndex = (_rowIndex + 1) % _rows.Count;
                    RenderRows();
                }

                return;
            }

            if (button == PadButton.A || button == PadButton.Menu)
            {
                if (_rows.Count > 0)
                {
                    _rows[_rowIndex].Activate?.Invoke();
                }
            }
        }

        // ---- Back / accelerator keys ---------------------------------------------------------------

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = HandleBack();
        }

        private bool HandleBack()
        {
            if (unchecked(Environment.TickCount - _lastBackTick) < 500)
            {
                return true;
            }

            _lastBackTick = Environment.TickCount;

            if (SettingsPanelGrid.Visibility == Visibility.Visible)
            {
                SettingsPanelGrid.Visibility = Visibility.Collapsed;
                Log("info", "Settings closed");
                return true;
            }

            // B goes back (episode list → series, series → Home).
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
                return true;
            }

            return false;
        }

        private void Log(string level, string text)
        {
            FileLog.Write(level, text);
        }

        // Keyboard shortcuts for testing on a PC. Controller input comes from GamepadInput.
        private void OnAcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args.EventType != CoreAcceleratorKeyEventType.KeyDown)
            {
                return;
            }

            if (args.KeyStatus.WasKeyDown)
            {
                return; // auto-repeat noise
            }

            var vk = (int)args.VirtualKey;
            if (vk >= 0xC0 && vk <= 0xDF)
            {
                switch (args.VirtualKey)
                {
                    case (VirtualKey)0xC3: OnPadButton(PadButton.A); break;
                    case (VirtualKey)0xC4: HandleBack(); break;
                    case (VirtualKey)0xC5: OnPadButton(PadButton.X); break;
                    case (VirtualKey)0xC6: OnPadButton(PadButton.Y); break;
                    case (VirtualKey)0xCB: OnPadButton(PadButton.Up); break;
                    case (VirtualKey)0xCC: OnPadButton(PadButton.Down); break;
                    case (VirtualKey)0xCD: OnPadButton(PadButton.Left); break;
                    case (VirtualKey)0xCE: OnPadButton(PadButton.Right); break;
                    case (VirtualKey)0xCF: OnPadButton(PadButton.Menu); break;
                    case (VirtualKey)0xD0: OnPadButton(PadButton.Share); break;
                    default: break;
                }

                args.Handled = true; // never let a gamepad VK reach the input system
                return;
            }

            switch (args.VirtualKey)
            {
                case VirtualKey.F1:
                    OnPadButton(PadButton.View);
                    args.Handled = true;
                    return;
                case VirtualKey.F2:
                    OnPadButton(PadButton.Y);
                    args.Handled = true;
                    return;
                case VirtualKey.F3:
                    OnPadButton(PadButton.X);
                    args.Handled = true;
                    return;
            }
        }
    }
}

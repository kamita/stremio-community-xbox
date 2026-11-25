using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StremioCommunity.Core;
using StremioCommunity.Models;
using Windows.Foundation;
using Windows.Gaming.Input;
using Windows.UI;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Interop;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace StremioCommunity.Pages
{
    /// <summary>
    /// Home page: category tabs (catalog rows), a poster grid with controller navigation,
    /// a search box, the settings overlay (Y) and the debug log (View).
    /// Navigation flow: A on a tile → DetailPage; Y → Settings overlay; View → debug log.
    /// </summary>
    public sealed partial class HomePage : Page
    {
        private const int PageSize = 8; // tiles per loaded page (2 rows of 4)
        private const int PerRow = 4; // tiles across at 270px on a 1080p screen
        private const string PlaceholderText = "Search movies and series (A to type, B to search)";

        private readonly GamepadInput _gamepad = new GamepadInput();
        private readonly StremioApiClient _api = new StremioApiClient();
        private SettingsPanelController _settings; // created in OnNavigatedTo (needs the XAML panel)
        private readonly object _logLock = new object();
        private readonly List<string> _log = new List<string>();

        private sealed class Tab
        {
            public string Title;
            public string Type;
            public string CatalogId;
            public bool IsSearch;
        }

        private sealed class Tile
        {
            public StremioMeta Meta;
            public Border Border;
            public bool IsLoadMore;
        }

        private StremioProfile _profile;
        private readonly List<Tab> _tabs = new List<Tab>();
        private int _tabIndex;

        private readonly List<Tile> _tiles = new List<Tile>();
        private int _tileIndex;
        private int _tilesLoaded;
        private bool _loading;

        private PadButton _lastPadBtn;
        private int _lastPadBtnMs;
        private int _lastBackTick = Environment.TickCount - 1000;
        private bool _searching; // a search result is currently displayed
        private bool _searchBoxFocused; // UWP TextBox has no IsFocused property; tracked via events

        public HomePage()
        {
            InitializeComponent();
            SubtitleTimingModifier.OnDiagnostic = msg => Log("warn", msg);
            SearchBox.GotFocus += (s, a) => _searchBoxFocused = true;
            SearchBox.LostFocus += (s, a) =>
            {
                _searchBoxFocused = false;
                if (string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    SearchBox.Text = PlaceholderText;
                }
            };
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
                Log("info", DisplayModeManager.DescribeDisplay());
                _ = LoadTabsAsync();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated -= OnAcceleratorKeyActivated;
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            _gamepad.Stop();
            base.OnNavigatedFrom(e);
        }

        // ---- Data loading ---------------------------------------------------------------------

        private async Task LoadTabsAsync()
        {
            _ = LoadProfileAsync();

            TabPanel.Children.Clear();
            _tabs.Clear();

            var preferredOrder = new[] { "top", "trending", "new", "popular" };
            Func<string, int> rank = id =>
            {
                var idx = System.Array.IndexOf(preferredOrder, id);
                return idx < 0 ? 100 : idx;
            };

            foreach (var type in new[] { "movie", "series" })
            {
                IReadOnlyList<StremioApiClient.CatalogEntry> entries;
                try
                {
                    entries = await _api.GetCatalogEntriesAsync(type);
                }
                catch (Exception ex)
                {
                    Log("warn", "Catalog entries (" + type + ") failed: " + ex.Message);
                    entries = new List<StremioApiClient.CatalogEntry>();
                }

                foreach (var entry in entries.OrderBy(e => rank(e.Id)).Take(5))
                {
                    _tabs.Add(new Tab
                    {
                        Title = type == "series" ? "Series: " + entry.Name : entry.Name,
                        Type = type,
                        CatalogId = entry.Id,
                    });
                }
            }

            if (_tabs.Count == 0)
            {
                _tabs.Add(new Tab { Title = "Top", Type = "movie", CatalogId = "top" });
                _tabs.Add(new Tab { Title = "Top", Type = "series", CatalogId = "top" });
            }

            // The search tab is first: the v1 core API has no search endpoint, so search
            // opens the on-screen keyboard and filters the top catalogs client-side.
            _tabs.Insert(0, new Tab { Title = "Search", Type = "movie", CatalogId = "search", IsSearch = true });

            _tabIndex = 0;
            RenderTabs();
            await LoadTabPageAsync();
        }

        private void RenderTabs()
        {
            TabPanel.Children.Clear();
            for (var i = 0; i < _tabs.Count; i++)
            {
                var tab = _tabs[i];
                var selected = i == _tabIndex;
                TabPanel.Children.Add(new Border
                {
                    Margin = new Thickness(0, 0, 14, 0),
                    Padding = new Thickness(16, 8, 16, 8),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(selected ? Color.FromArgb(255, 0x7B, 0x5B, 0xF5) : Color.FromArgb(25, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(selected ? Color.FromArgb(255, 0x7B, 0x5B, 0xF5) : Color.FromArgb(40, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = tab.Title,
                        FontSize = 19,
                        Foreground = new SolidColorBrush(Colors.White),
                    },
                });
            }
        }

        private async Task LoadTabPageAsync()
        {
            if (_tabs[_tabIndex].IsSearch)
            {
                return; // the search tab opens the keyboard on activation, not a catalog load
            }

            if (_loading)
            {
                return;
            }

            _loading = true;
            try
            {
                TileWrap.Children.Clear();
                _tiles.Clear();
                _tilesLoaded = 0;
                _tileIndex = 0;
                _searching = false;
                await LoadNextPageAsync();
            }
            finally
            {
                _loading = false;
            }
        }

        private async Task LoadNextPageAsync()
        {
            var tab = _tabs[_tabIndex];
            Log("info", string.Format("Loading {0} /{1} skip={2}", tab.Title, tab.CatalogId, _tilesLoaded));
            var page = await _api.GetCatalogPageAsync(tab.Type, tab.CatalogId, _tilesLoaded, PageSize);
            _tilesLoaded += page.Metas.Count;
            var hasMore = page.NextSkip.HasValue;

            var start = _tiles.Count;
            foreach (var meta in page.Metas)
            {
                _tiles.Add(BuildTile(meta));
            }

            for (var i = start; i < _tiles.Count; i++)
            {
                TileWrap.Children.Add(_tiles[i].Border);
            }

            if (hasMore)
            {
                _tiles.Add(BuildLoadMoreTile());
                TileWrap.Children.Add(_tiles[_tiles.Count - 1].Border);
            }

            RenderTiles();
            Log("info", "Loaded " + page.Metas.Count + " tiles (" + _tiles.Count + " total)");
        }

        private Tile BuildTile(StremioMeta meta)
        {
            var stack = new StackPanel { Width = 250, Margin = new Thickness(10) };

            var posterBorder = new Border
            {
                Width = 250,
                Height = 350,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x28)),
            };

            if (!string.IsNullOrEmpty(meta.Poster))
            {
                try
                {
                    posterBorder.Child = new Image
                    {
                        Source = new BitmapImage(new Uri(meta.Poster)),
                        Stretch = Stretch.UniformToFill,
                    };
                }
                catch (Exception)
                {
                    posterBorder.Child = null;
                }
            }

            if (posterBorder.Child == null)
            {
                posterBorder.Child = new TextBlock
                {
                    Text = meta.Name,
                    FontSize = 18,
                    Foreground = new SolidColorBrush(Colors.White),
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12),
                };
            }

            stack.Children.Add(posterBorder);
            stack.Children.Add(new TextBlock
            {
                Text = meta.Name,
                FontSize = 18,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 10, 4, 0),
            });
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("  ", new[] { meta.ReleaseInfo, FormatImdb(meta.ImdbRating) }.Where(s => !string.IsNullOrEmpty(s))),
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xD5, 0x4F)),
                Margin = new Thickness(4, 4, 4, 4),
            });

            return new Tile { Meta = meta, Border = new Border { Width = 270, Child = stack } };
        }

        private static string FormatImdb(double? rating)
        {
            return rating.HasValue ? "★ " + rating.Value.ToString("0.#") : string.Empty;
        }

        private Tile BuildLoadMoreTile()
        {
            return new Tile
            {
                IsLoadMore = true,
                Border = new Border
                {
                    Width = 270,
                    Height = 390,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "Load more…",
                        FontSize = 20,
                        Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
        }

        private void RenderTiles()
        {
            for (var i = 0; i < _tiles.Count; i++)
            {
                var tile = _tiles[i];
                var selected = i == _tileIndex;
                tile.Border.BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(255, 0x7B, 0x5B, 0xF5)) : null;
                tile.Border.BorderThickness = selected ? new Thickness(3) : new Thickness(0);
            }

            if (_tiles.Count > 0)
            {
                var tile = _tiles[_tileIndex];
                try
                {
                    var row = _tileIndex / PerRow;
                    var rowHeight = tile.Border.DesiredSize.Height + 20; // height + vertical margins
                    var offset = row * rowHeight;
                    if (offset < GridScroll.VerticalOffset || offset > GridScroll.VerticalOffset + GridScroll.ViewportHeight - 40)
                    {
                        GridScroll.ScrollToVerticalOffset(Math.Max(0, offset - 40));
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private async Task LoadProfileAsync()
        {
            if (_profile != null)
            {
                return;
            }

            try
            {
                _profile = await _api.GetProfileAsync();
                Log("info", "Profile: " + (_profile == null ? "not signed in" : _profile.Account + ", " + _profile.Addons.Count + " addon(s)"));
            }
            catch (Exception ex)
            {
                Log("warn", "Profile unavailable: " + ex.Message);
            }
        }

        // ---- Search ------------------------------------------------------------------------------

        // UWP has no programmatic "show the on-screen keyboard" API; giving the focused
        // TextBox keyboard focus triggers the system keyboard on the Xbox. We track focus
        // ourselves because the UWP TextBox exposes no IsFocused property.
        private void OpenSearch()
        {
            SearchBox.Text = string.Empty;
            SearchBox.Focus(Windows.UI.Xaml.FocusState.Programmatic);
            _searchBoxFocused = true;
            Log("info", "Search focused — type, then A to search");
        }

        private void RunSearch()
        {
            var text = (SearchBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) || text == PlaceholderText)
            {
                SearchBox.Text = PlaceholderText;
                return;
            }

            // Keep the query visible in the box (so the user can see what was searched);
            // LostFocus will not overwrite a non-empty value.
            _searching = true;
            _ = SearchAsync(text);
        }

        private async Task SearchAsync(string text)
        {
            try
            {
                Log("info", "Search: " + text);
                var wanted = text.ToLowerInvariant();
                // The v1 core API has no search endpoint; filter the top catalogs of both types.
                var moviePage = await _api.GetCatalogPageAsync("movie", "top", 0, 60);
                var seriesPage = await _api.GetCatalogPageAsync("series", "top", 0, 60);

                TileWrap.Children.Clear();
                _tiles.Clear();
                _tilesLoaded = 0;
                _tileIndex = 0;

                    foreach (var meta in moviePage.Metas.Concat(seriesPage.Metas))
                    {
                        if (meta.Name != null && meta.Name.ToLowerInvariant().Contains(wanted))
                        {
                            _tiles.Add(BuildTile(meta));
                            TileWrap.Children.Add(_tiles[_tiles.Count - 1].Border);
                        }
                    }

                    if (_tiles.Count == 0)
                    {
                        var empty = new Tile
                        {
                            Border = new Border
                            {
                                Margin = new Thickness(20),
                                Child = new TextBlock
                                {
                                    Text = "No results for \"" + text + "\" (search covers the top 60 of each catalog)",
                                    FontSize = 22,
                                    Foreground = new SolidColorBrush(Colors.White),
                                },
                            },
                        };
                        _tiles.Add(empty);
                        TileWrap.Children.Add(empty.Border);
                    }

                    _searching = true;
                    RenderTiles();
                    Log("info", "Search results: " + _tiles.Count);
            }
            catch (Exception ex)
            {
                _searching = false;
                Log("error", "Search failed: " + ex.Message);
            }
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

        // ---- Debug log (View) ----------------------------------------------------------------------

        private void SetDebugVisible(bool visible)
        {
            DebugPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                DebugHeader.Text = "Debug log  |  View: hide  |  B: close";
            }
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

            if (DebugPanel.Visibility == Visibility.Visible)
            {
                if (button == PadButton.View || button == PadButton.A)
                {
                    SetDebugVisible(false);
                }

                return;
            }

            if (button == PadButton.View)
            {
                SetDebugVisible(DebugPanel.Visibility != Visibility.Visible);
                return;
            }

            if (button == PadButton.Y)
            {
                ToggleSettings();
                return;
            }

            if (_searchBoxFocused)
            {
                if (button == PadButton.A)
                {
                    RunSearch();
                }

                return; // the Xbox keyboard owns the other buttons while typing
            }

            switch (button)
            {
                case PadButton.Up:
                    MoveTile(up: true);
                    break;
                case PadButton.Down:
                    MoveTile(up: false);
                    break;
                case PadButton.Left:
                    CycleTab(-1);
                    break;
                case PadButton.Right:
                    CycleTab(1);
                    break;
                case PadButton.A:
                    ActivateSelected();
                    break;
            }
        }

        private void CycleTab(int direction)
        {
            _tabIndex = (_tabIndex + direction + _tabs.Count) % _tabs.Count;
            RenderTabs();
            if (_tiles.Count > 0)
            {
                _tiles.Clear();
                TileWrap.Children.Clear();
                _tilesLoaded = 0;
                _tileIndex = 0;
            }
            _ = LoadTabPageAsync();
        }

        private void MoveTile(bool up)
        {
            if (_tiles.Count == 0)
            {
                return;
            }

            _tileIndex = up
                ? (_tileIndex - PerRow + _tiles.Count) % _tiles.Count
                : (_tileIndex + PerRow) % _tiles.Count;
            RenderTiles();
        }

        private void ActivateSelected()
        {
            var tab = _tabs[_tabIndex];
            if (tab.IsSearch)
            {
                OpenSearch();
                return;
            }

            if (_tiles.Count == 0)
            {
                return;
            }

            var tile = _tiles[_tileIndex];
            if (tile.IsLoadMore)
            {
                _ = LoadNextPageAsync();
                return;
            }

            if (Frame == null || tile.Meta == null)
            {
                return;
            }

            Log("info", "Open " + tile.Meta.Type + " " + tile.Meta.Id + " (" + tile.Meta.Name + ")");
            Frame.Navigate(typeof(DetailPage), new DetailPage.Args
            {
                Type = tile.Meta.Type,
                Id = tile.Meta.Id,
                Profile = _profile,
            });
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

            if (_searchBoxFocused)
            {
                if (string.IsNullOrWhiteSpace(SearchBox.Text) || SearchBox.Text == PlaceholderText)
                {
                    SearchBox.Text = PlaceholderText;
                    return true;
                }

                // A previous search is showing: B clears it and goes back to the catalog.
                SearchBox.Text = PlaceholderText;
                if (Frame != null && Frame.CanGoBack)
                {
                    Frame.GoBack();
                    return true;
                }

                if (_searching)
                {
                    _ = LoadTabPageAsync();
                    return true;
                }
            }

            if (SettingsPanelGrid.Visibility == Visibility.Visible)
            {
                SettingsPanelGrid.Visibility = Visibility.Collapsed;
                Log("info", "Settings closed");
                return true;
            }

            if (DebugPanel.Visibility == Visibility.Visible)
            {
                SetDebugVisible(false);
                return true;
            }

            // B goes back in navigation (e.g. Detail → Home). On the root page there is nothing to go back to.
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
                return true;
            }

            return false;
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

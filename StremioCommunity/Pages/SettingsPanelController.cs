using System;
using System.Collections.Generic;
using StremioCommunity.Core;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace StremioCommunity.Pages
{
    /// <summary>
    /// Shared settings-panel logic (the original app's OpenSettings/RenderSettings/MoveSettings/
    /// ToggleSetting/AdjustSetting extracted from MainPage.xaml.cs). Renders rows into a
    /// <see cref="StackPanel"/> owned by the hosting page (the Home/Detail overlays or the
    /// standalone SettingsPage). The WebView2-specific rows (zoom / connections / restartSeek)
    /// are intentionally absent — they don't apply to the native XAML port. "bufferMb" (the
    /// native player's read-ahead cap) is kept.
    /// </summary>
    public sealed class SettingsPanelController
    {
        private sealed class SettingRow
        {
            public string Kind; // "number" | "toggle" | "cycle"
            public string Key;
            public string Title;
            public double Value;
            public double Min;
            public double Max;
            public double Step;
            public string Unit;
            public bool IsToggle;
            public bool On;
            public double[] CycleValues;
            public int CycleIndex;
        }

        private static readonly string[] RowKeys =
        {
            "volume", "speed", "mute",
            "audioLang", "subLang",
            "pauseMin", "pauseFocus",
            "resume", "autoContinue", "bufferMb", "hdrQuality",
        };

        private static readonly double[] BufferMbSteps = { 0, 128, 256, 512, 768, 1024, 1536 }; // 0 = automatic
        private static readonly double[] HdrQualitySteps = { 0, 1, 2 }; // auto / force HDR / force SDR

        private readonly StackPanel _panel;
        private readonly Action<string> _log;
        private readonly Action<int> _liveVolume;
        private readonly Action<double> _liveRate;
        private readonly Action<bool> _liveMute;
        private int _index;

        /// <summary>
        /// Creates a controller that renders its rows into <paramref name="panel"/>.
        /// The live-apply callbacks let the current page push volume/rate/mute into a
        /// running player; pass null when the page has no player (e.g. the Home page).
        /// </summary>
        public SettingsPanelController(
            StackPanel panel,
            Action<string> log,
            Action<int> liveVolume = null,
            Action<double> liveRate = null,
            Action<bool> liveMute = null)
        {
            _panel = panel;
            _log = log;
            _liveVolume = liveVolume;
            _liveRate = liveRate;
            _liveMute = liveMute;
        }

        /// <summary>Number of settings rows (the D-pad wraps over this many).</summary>
        public int Count => RowKeys.Length;

        /// <summary>Resets the highlight to the first row and re-renders.</summary>
        public void Open()
        {
            _index = 0;
            Render();
        }

        /// <summary>Steps the highlight up or down (wrapping) and re-renders.</summary>
        public void Move(bool down)
        {
            _index = down ? (_index + 1) % Count : (_index + Count - 1) % Count;
            Render();
        }

        /// <summary>A on a toggle row flips it; on a number/cycle row it steps forward.</summary>
        public void ToggleOrStep()
        {
            var row = CurrentRow(_index);
            if (row == null)
            {
                return;
            }

            switch (row.Key)
            {
                case "mute":
                    CommunitySettings.Muted = !CommunitySettings.Muted;
                    _liveMute?.Invoke(CommunitySettings.Muted);
                    _log?.Invoke("Start muted: " + (CommunitySettings.Muted ? "ON" : "OFF"));
                    break;
                case "pauseMin":
                    CommunitySettings.PauseOnMinimize = !CommunitySettings.PauseOnMinimize;
                    _log?.Invoke("Pause on background: " + (CommunitySettings.PauseOnMinimize ? "ON" : "OFF"));
                    break;
                case "pauseFocus":
                    CommunitySettings.PauseOnLostFocus = !CommunitySettings.PauseOnLostFocus;
                    _log?.Invoke("Pause on lost focus: " + (CommunitySettings.PauseOnLostFocus ? "ON" : "OFF"));
                    break;
                case "resume":
                    CommunitySettings.ResumePlayback = !CommunitySettings.ResumePlayback;
                    _log?.Invoke("Resume playback: " + (CommunitySettings.ResumePlayback ? "ON" : "OFF"));
                    break;
                case "autoContinue":
                    CommunitySettings.AutoContinue = !CommunitySettings.AutoContinue;
                    _log?.Invoke("Auto-continue: " + (CommunitySettings.AutoContinue ? "ON" : "OFF"));
                    break;
                default:
                    Adjust(1);
                    return;
            }

            Render();
        }

        /// <summary>LB/RB (and A as +) steps the highlighted number or cycle row.</summary>
        public void Adjust(int direction)
        {
            var row = CurrentRow(_index);
            if (row == null)
            {
                return;
            }

            if (row.Kind == "cycle" && row.CycleValues != null)
            {
                var count = row.CycleValues.Length;
                var newIndex = ((row.CycleIndex + direction) % count + count) % count;
                switch (row.Key)
                {
                    case "bufferMb":
                        CommunitySettings.BufferMbIndex = newIndex;
                        _log?.Invoke("Debrid buffer: " + DescribeCycle("bufferMb", newIndex) + " (applies to the next playback)");
                        break;
                    case "hdrQuality":
                        CommunitySettings.HdrQualityMode = newIndex;
                        _log?.Invoke("HDR quality: " + DescribeCycle("hdrQuality", newIndex) + " (applies to the next playback)");
                        break;
                    case "audioLang":
                        CommunitySettings.PreferredAudioLangIndex = newIndex;
                        _log?.Invoke("Audio language: " + DescribeCycle("audioLang", newIndex) + " (applies to the next playback)");
                        break;
                    case "subLang":
                        CommunitySettings.PreferredSubLangIndex = newIndex;
                        _log?.Invoke("Subtitle language: " + DescribeCycle("subLang", newIndex) + " (applies to the next playback)");
                        break;
                }

                Render();
                return;
            }

            var next = row.Value + (row.Step * direction);
            if (next < row.Min)
            {
                next = row.Min;
            }

            if (next > row.Max)
            {
                next = row.Max;
            }

            switch (row.Key)
            {
                case "volume":
                    CommunitySettings.InitialVolume = (int)next;
                    _liveVolume?.Invoke((int)next);
                    _log?.Invoke("Initial volume: " + (int)next + "%");
                    break;
                case "speed":
                    CommunitySettings.PlaybackRate = next;
                    _liveRate?.Invoke(next);
                    _log?.Invoke("Playback speed: " + next + "x");
                    break;
                default:
                    return;
            }

            Render();
        }

        /// <summary>Re-renders all rows into the host panel with the current highlight.</summary>
        public void Render()
        {
            _panel.Children.Clear();
            for (var i = 0; i < RowKeys.Length; i++)
            {
                var row = CurrentRow(i);
                if (row == null)
                {
                    continue;
                }

                var selected = i == _index;
                _panel.Children.Add(new TextBlock
                {
                    Text = row.Title + "  —  " + DescribeSetting(row),
                    FontSize = 22,
                    Foreground = selected ? new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xD5, 0x4F)) : new SolidColorBrush(Colors.White),
                    FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
                    Margin = new Thickness(0, 6, 0, 6),
                    Padding = new Thickness(8, 2, 8, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
        }

        private SettingRow CurrentRow(int index)
        {
            switch (RowKeys[index])
            {
                case "volume":
                    return new SettingRow { Kind = "number", Key = "volume", Title = "Initial volume", Value = CommunitySettings.InitialVolume, Min = 0, Max = 100, Step = 5, Unit = "%" };
                case "speed":
                    return new SettingRow { Kind = "number", Key = "speed", Title = "Playback speed", Value = CommunitySettings.PlaybackRate, Min = 0.25, Max = 4.0, Step = 0.25, Unit = "x" };
                case "mute":
                    return new SettingRow { Kind = "toggle", Key = "mute", Title = "Start muted", IsToggle = true, On = CommunitySettings.Muted };
                case "audioLang":
                    return new SettingRow { Kind = "cycle", Key = "audioLang", Title = "Audio language", CycleValues = LangCycleValues(), CycleIndex = CommunitySettings.PreferredAudioLangIndex };
                case "subLang":
                    return new SettingRow { Kind = "cycle", Key = "subLang", Title = "Subtitle language", CycleValues = LangCycleValues(), CycleIndex = CommunitySettings.PreferredSubLangIndex };
                case "pauseMin":
                    return new SettingRow { Kind = "toggle", Key = "pauseMin", Title = "Pause on background", IsToggle = true, On = CommunitySettings.PauseOnMinimize };
                case "pauseFocus":
                    return new SettingRow { Kind = "toggle", Key = "pauseFocus", Title = "Pause on lost focus", IsToggle = true, On = CommunitySettings.PauseOnLostFocus };
                case "resume":
                    return new SettingRow { Kind = "toggle", Key = "resume", Title = "Resume playback", IsToggle = true, On = CommunitySettings.ResumePlayback };
                case "autoContinue":
                    return new SettingRow { Kind = "toggle", Key = "autoContinue", Title = "Auto-continue next", IsToggle = true, On = CommunitySettings.AutoContinue };
                case "bufferMb":
                    return new SettingRow { Kind = "cycle", Key = "bufferMb", Title = "Debrid buffer", CycleValues = BufferMbSteps, CycleIndex = CommunitySettings.BufferMbIndex };
                case "hdrQuality":
                    return new SettingRow { Kind = "cycle", Key = "hdrQuality", Title = "HDR quality", CycleValues = HdrQualitySteps, CycleIndex = CommunitySettings.HdrQualityMode };
                default:
                    return null;
            }
        }

        private static double[] LangCycleValues()
        {
            var values = new double[CommunitySettings.LanguageCodes.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = i;
            }

            return values;
        }

        private string DescribeSetting(SettingRow row)
        {
            if (row.IsToggle)
            {
                return row.On ? "ON" : "OFF";
            }

            if (row.Kind == "cycle")
            {
                return DescribeCycle(row.Key, row.CycleIndex);
            }

            return FormatSettingValue(row.Value, row.Unit, row.Step);
        }

        private static string DescribeCycle(string key, int index)
        {
            switch (key)
            {
                case "bufferMb":
                    var mb = (int)BufferMbSteps[index];
                    return mb == 0 ? "automatic" : mb + " MB";
                case "hdrQuality":
                    switch (index)
                    {
                        case 1: return "force HDR";
                        case 2: return "force SDR";
                        default: return "auto (follow video)";
                    }
                case "audioLang":
                case "subLang":
                    return index < CommunitySettings.LanguageLabels.Length
                        ? CommunitySettings.LanguageLabels[index]
                        : string.Empty;
                default:
                    return string.Empty;
            }
        }

        private static string FormatSettingValue(double value, string unit, double step)
        {
            if (step >= 1)
            {
                return ((int)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture) + unit;
            }

            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + unit;
        }
    }
}

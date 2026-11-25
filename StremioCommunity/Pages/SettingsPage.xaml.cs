using System;
using StremioCommunity.Core;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Controls;

namespace StremioCommunity.Pages
{
    /// <summary>
    /// Standalone settings page: the same rows as the Home/Detail overlays (see
    /// <see cref="SettingsPanelController"/>). D-pad moves, LB/RB adjust the highlighted
    /// row, A toggles/steps, B closes and goes back. The 8BitDo remote's accelerator
    /// keys (VK 0xC0-0xDF) are mapped the same way as on the other pages, with the
    /// same 200 ms debounce to avoid double-firing.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        private readonly GamepadInput _gamepad = new GamepadInput();
        private SettingsPanelController _settings;
        private PadButton _lastPadBtn;
        private int _lastPadBtnMs;
        private int _lastBackTick = Environment.TickCount - 1000;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated += OnAcceleratorKeyActivated;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _gamepad.Pressed += OnPadButton;
            _gamepad.Start();
            _settings = new SettingsPanelController(SettingsItems, msg => Log(msg));
            _settings.Open();
        }

        private static void Log(string message)
        {
            FileLog.Write("info", "[Settings] " + message);
        }

        // ---- Gamepad ---------------------------------------------------------------------------

        private void OnPadButton(PadButton button)
        {
            var nowMs = Environment.TickCount;
            if (_lastPadBtn == button && unchecked(nowMs - _lastPadBtnMs) < 200)
            {
                return; // debounce: XInput + accelerator keys can deliver the same press twice
            }

            _lastPadBtn = button;
            _lastPadBtnMs = nowMs;

            switch (button)
            {
                case PadButton.Up:
                    _settings.Move(false);
                    break;
                case PadButton.Down:
                    _settings.Move(true);
                    break;
                case PadButton.Left:
                    _settings.Adjust(-1);
                    break;
                case PadButton.Right:
                    _settings.Adjust(1);
                    break;
                case PadButton.A:
                    _settings.ToggleOrStep();
                    break;
                case PadButton.Menu:
                    HandleBack();
                    break;
                default:
                    break;
            }
        }

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

            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
                return true;
            }

            // Root fallback: land on Home.
            if (Frame != null)
            {
                Frame.Navigate(typeof(HomePage));
            }

            return true;
        }

        // ---- Accelerator keys (8BitDo remote sends its buttons as VK 0xC0-0xDF) ------------------

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
                    default: break;
                }

                args.Handled = true; // never let a gamepad VK reach the input system
                return;
            }

            switch (args.VirtualKey)
            {
                case VirtualKey.F2:
                    HandleBack();
                    args.Handled = true;
                    return;
            }
        }
    }
}
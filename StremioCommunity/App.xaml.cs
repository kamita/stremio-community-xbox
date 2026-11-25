using System;
using StremioCommunity.Core;
using Windows.ApplicationModel.Activation;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace StremioCommunity
{
    /// <summary>Application entry point.</summary>
    public sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            FileLog.Start();
            FileLog.Write("info", "App starting");

            // Render at 1:1 so the XAML layout lands at 1920x1080 rather than the Xbox's default 200% scale.
            FileLog.Write("info", "Layout scaling disabled: " + ApplicationViewScaling.TrySetDisableLayoutScaling(true));

            // No Xbox system cursor by default: the native XAML pages handle controller navigation
            // themselves (D-pad + face buttons), so the system pointer would only add a ghost dot.
            var systemCursor = false;
            try
            {
                systemCursor = Windows.Storage.ApplicationData.Current.LocalSettings.Values["systemCursor"] is bool saved && saved;
            }
            catch (Exception)
            {
            }

            if (!systemCursor)
            {
                RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            }

            FileLog.Write("info", "System cursor: " + (systemCursor ? "ON (initial-build pointer mode)" : "off"));

            UnhandledException += (sender, e) =>
            {
                FileLog.Write("error", "Unhandled exception: " + e.Exception);
                e.Handled = true;
            };
            Suspending += (sender, e) => FileLog.Write("info", "Lifecycle: suspending");
            Resuming += (sender, e) => FileLog.Write("info", "Lifecycle: resuming");
            EnteredBackground += (sender, e) =>
            {
                FileLog.Write("info", "Lifecycle: entered background");
                NotifyPageForegroundChanged(true);
            };
            LeavingBackground += (sender, e) =>
            {
                FileLog.Write("info", "Lifecycle: leaving background");
                NotifyPageForegroundChanged(false);
            };
            MemoryManager.AppMemoryUsageLimitChanging += (sender, e) =>
                FileLog.Write("warn", string.Format("Memory limit changing {0} -> {1} MB (usage {2} MB)", e.OldLimit >> 20, e.NewLimit >> 20, MemoryManager.AppMemoryUsage >> 20));
            MemoryManager.AppMemoryUsageIncreased += (sender, e) =>
                FileLog.Write("warn", "Memory usage level now " + MemoryManager.AppMemoryUsageLevel + " (" + (MemoryManager.AppMemoryUsage >> 20) + " MB of " + (MemoryManager.AppMemoryUsageLimit >> 20) + " MB)");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (!(Window.Current.Content is Frame rootFrame))
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += (sender, args) => throw new InvalidOperationException("Failed to load page " + args.SourcePageType.FullName);

                // Use the whole screen; the TV-safe area is left to the XAML pages.
                ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
                Window.Current.Content = rootFrame;
            }

            if (!e.PrelaunchActivated)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(StremioCommunity.Pages.HomePage), e.Arguments);
                }

                Window.Current.Activate();
            }
        }

        /// <summary>Forwards foreground/background transitions to the active page (pause-on-background).</summary>
        private void NotifyPageForegroundChanged(bool enteringBackground)
        {
            try
            {
                if (Window.Current.Content is Frame rootFrame && rootFrame.Content is IAppLifecycle page)
                {
                    page.OnAppForegroundChanged(enteringBackground);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("warn", "Foreground notification failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Pushes a live playback setting to the active page when it implements
        /// <see cref="ISettingsApplier"/> (the PlayerPage). No-op otherwise.
        /// Only the non-zero / non-null value(s) are applied.
        /// </summary>
        public void NotifyLiveSettings(int volume, double rate, bool applyMute, bool muted)
        {
            try
            {
                if (Window.Current.Content is Frame rootFrame && rootFrame.Content is ISettingsApplier page)
                {
                    if (volume > 0)
                    {
                        page.ApplyLiveVolume(volume);
                    }

                    if (rate > 0)
                    {
                        page.ApplyLiveRate(rate);
                    }

                    if (applyMute)
                    {
                        page.ApplyLiveMute(muted);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("warn", "Live settings push failed: " + ex.Message);
            }
        }
    }
}

namespace StremioCommunity.Core
{
    /// <summary>Implemented by pages that want foreground/background notifications (pause-on-background).</summary>
    public interface IAppLifecycle
    {
        /// <summary>Called when the app enters or leaves the background.</summary>
        void OnAppForegroundChanged(bool enteringBackground);
    }

    /// <summary>Implemented by pages that can apply live playback settings (volume/speed/mute).</summary>
    public interface ISettingsApplier
    {
        /// <summary>Applies the initial volume (0-100) to the live player, when one is active.</summary>
        void ApplyLiveVolume(int volume);

        /// <summary>Applies the playback rate (0.25-4) to the live player, when one is active.</summary>
        void ApplyLiveRate(double rate);

        /// <summary>Applies the mute state to the live player, when one is active.</summary>
        void ApplyLiveMute(bool muted);
    }
}

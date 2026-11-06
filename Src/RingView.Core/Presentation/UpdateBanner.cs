using RingView.Core.Updates;

namespace RingView.Core.Presentation;

public enum UpdateBannerTone
{
    /// <summary>Blue: found or downloading.</summary>
    Info,

    /// <summary>Green, with "Restart &amp; install".</summary>
    Ready,

    /// <summary>Amber: the check failed.</summary>
    Warn,
}

/// <summary>The thin banner above the grid (UpdateBanner.tsx); null hides it.</summary>
public sealed record UpdateBanner(string Text, UpdateBannerTone Tone)
{
    public bool ShowInstall => Tone == UpdateBannerTone.Ready;

    public static UpdateBanner? From(UpdateState state)
    {
        string version = state.Version is { } v ? " v" + v : "";
        return state.Status switch
        {
            UpdateStatus.Available => new UpdateBanner($"Update{version} found - downloading...", UpdateBannerTone.Info),
            UpdateStatus.Downloading => new UpdateBanner($"Downloading update... {state.Percent ?? 0}%", UpdateBannerTone.Info),
            UpdateStatus.Downloaded => new UpdateBanner($"Update{version} ready to install.", UpdateBannerTone.Ready),
            UpdateStatus.Error => new UpdateBanner("Update check failed - will retry later.", UpdateBannerTone.Warn),
            _ => null,
        };
    }
}

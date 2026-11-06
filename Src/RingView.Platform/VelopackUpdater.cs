using RingView.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace RingView.Platform;

/// <summary>
/// <see cref="IUpdater"/> on Velopack, reading this repo's GitHub releases (public, so no token).
/// A copy run from the build output is not installed, so the updater reports itself disabled.
/// </summary>
public sealed class VelopackUpdater : IUpdater
{
    public const string ReleasesUrl = "https://github.com/matthewjenner/RingView";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _found;

    public VelopackUpdater()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(ReleasesUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            _manager = null; // the app runs without updates
        }
    }

    public bool IsInstalled => _manager?.IsInstalled ?? false;

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        if (_manager is null)
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        _found = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return _found?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken) =>
        _manager is null || _found is null
            ? Task.CompletedTask
            : _manager.DownloadUpdatesAsync(_found, progress, cancellationToken);

    public void ApplyAndRestart()
    {
        if (_manager is not null && _found is not null)
            _manager.ApplyUpdatesAndRestart(_found.TargetFullRelease);
    }

    public void ApplyOnExit()
    {
        if (_manager is not null && _found is not null)
            _manager.WaitExitThenApplyUpdates(_found.TargetFullRelease, silent: true, restart: false);
    }
}

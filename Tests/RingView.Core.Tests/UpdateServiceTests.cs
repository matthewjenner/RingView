using Microsoft.Extensions.Time.Testing;
using RingView.Core.Diagnostics;
using RingView.Core.Presentation;
using RingView.Core.Updates;

namespace RingView.Core.Tests;

/// <summary>The Electron UpdateService's states on Velopack, and the banner each one shows.</summary>
public sealed class UpdateServiceTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeUpdater _updater = new();
    private readonly List<string> _lines = [];
    private readonly List<UpdateStatus> _seen = [];

    private UpdateService Create()
    {
        var service = new UpdateService(_updater, _time, new DiagLog(new Sink(_lines), new SecretRedactor()));
        service.StateChanged += s => _seen.Add(s.Status);
        return service;
    }

    [Fact]
    public async Task ADevBuildIsDisabledAndNeverChecks()
    {
        _updater.Installed = false;
        using UpdateService service = Create();
        service.Start();
        _time.Advance(TimeSpan.FromHours(2));
        await service.CheckAsync();

        service.State.Status.Should().Be(UpdateStatus.Disabled);
        _updater.Checks.Should().Be(0);
        UpdateBanner.From(service.State).Should().BeNull();
    }

    [Fact]
    public void ChecksFiveSecondsAfterLaunchThenHourly()
    {
        using UpdateService service = Create();
        service.Start();
        _time.Advance(TimeSpan.FromSeconds(4));
        _updater.Checks.Should().Be(0);
        _time.Advance(TimeSpan.FromSeconds(1));
        _updater.Checks.Should().Be(1);
        _updater.Found.SetResult(null);
        service.State.Status.Should().Be(UpdateStatus.NotAvailable);

        _updater.Found = new();
        _time.Advance(TimeSpan.FromHours(1));
        _updater.Checks.Should().Be(2);
    }

    [Fact]
    public async Task AnUpdateDownloadsWithProgressAndInstallsOnQuit()
    {
        using UpdateService service = Create();
        Task check = service.CheckAsync();
        _updater.Found.SetResult("0.2.1");
        UpdateBanner.From(service.State).Should().Be(new UpdateBanner("Update v0.2.1 found - downloading...", UpdateBannerTone.Info));

        _updater.Progress!(42);
        UpdateBanner.From(service.State)!.Text.Should().Be("Downloading update... 42%");
        _updater.Downloaded.SetResult();
        await check;

        UpdateBanner banner = UpdateBanner.From(service.State)!;
        banner.Should().Be(new UpdateBanner("Update v0.2.1 ready to install.", UpdateBannerTone.Ready));
        banner.ShowInstall.Should().BeTrue();
        _seen.Should().Equal(UpdateStatus.Checking, UpdateStatus.Available, UpdateStatus.Downloading, UpdateStatus.Downloaded);

        service.OnQuit();
        _updater.Applied.Should().Equal("on exit");
        _lines.Should().Contain("update v0.2.1 downloaded; installs on quit");
    }

    [Fact]
    public async Task OnceDownloadedLaterChecksDoNothingAndInstallRestarts()
    {
        using UpdateService service = Create();
        Task check = service.CheckAsync();
        _updater.Found.SetResult("0.2.1");
        _updater.Downloaded.SetResult();
        await check;

        await service.CheckAsync();
        _updater.Checks.Should().Be(1);

        service.Install();
        _updater.Applied.Should().Equal("restart");
    }

    [Fact]
    public async Task InstallAndQuitDoNothingBeforeADownload()
    {
        using UpdateService service = Create();
        Task check = service.CheckAsync();
        _updater.Found.SetResult(null);
        await check;

        service.Install();
        service.OnQuit();
        _updater.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedCheckShowsTheAmberBannerAndTheNextCheckRetries()
    {
        using UpdateService service = Create();
        Task check = service.CheckAsync();
        _updater.Found.SetException(new HttpRequestException("network down"));
        await check;

        service.State.Should().Be(new UpdateState(UpdateStatus.Error, Error: "network down"));
        UpdateBanner.From(service.State).Should().Be(new UpdateBanner("Update check failed - will retry later.", UpdateBannerTone.Warn));
        _lines.Should().Contain("update check failed: network down");

        _updater.Found = new();
        Task retry = service.CheckAsync();
        _updater.Checks.Should().Be(2);
        _updater.Found.SetResult(null);
        await retry;
        UpdateBanner.From(service.State).Should().BeNull();
    }

    [Fact]
    public async Task ACheckWhileOneIsRunningIsIgnored()
    {
        using UpdateService service = Create();
        Task first = service.CheckAsync();
        await service.CheckAsync();
        _updater.Checks.Should().Be(1);
        _updater.Found.SetResult(null);
        await first;
    }

    [Fact]
    public async Task ALateProgressTickDoesNotUndoDownloaded()
    {
        using UpdateService service = Create();
        Task check = service.CheckAsync();
        _updater.Found.SetResult("0.2.1");
        Action<int> progress = _updater.Progress!;
        _updater.Downloaded.SetResult();
        await check;

        progress(99);
        service.State.Status.Should().Be(UpdateStatus.Downloaded);
    }

    private sealed class FakeUpdater : IUpdater
    {
        public bool Installed { get; set; } = true;
        public int Checks { get; private set; }
        public TaskCompletionSource<string?> Found { get; set; } = new();
        public TaskCompletionSource Downloaded { get; } = new();
        public Action<int>? Progress { get; private set; }
        public List<string> Applied { get; } = [];

        public bool IsInstalled => Installed;

        public Task<string?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Found.Task;
        }

        public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
        {
            Progress = progress;
            return Downloaded.Task;
        }

        public void ApplyAndRestart() => Applied.Add("restart");

        public void ApplyOnExit() => Applied.Add("on exit");
    }

    private sealed class Sink(List<string> lines) : IDiagSink
    {
        public void Write(DiagLevel level, string scope, string message) => lines.Add(message);
    }
}

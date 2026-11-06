using Microsoft.Win32;

namespace RingView.Platform.Tests;

public sealed class InstanceAndStartupTests
{
    [Fact]
    public async Task ASecondLaunchIsTurnedAwayAndAsksTheFirstToShow()
    {
        string id = Guid.NewGuid().ToString("N");
        using var first = new SingleInstance("RingView.Test.Mutex." + id, "RingView.Test.Pipe." + id);
        using var second = new SingleInstance("RingView.Test.Mutex." + id, "RingView.Test.Pipe." + id);
        using var cts = new CancellationTokenSource();
        var shown = new TaskCompletionSource();

        first.TryAcquire().Should().BeTrue();
        Task listening = first.ListenAsync(() => shown.TrySetResult(), cts.Token);
        // A mutex is re-entrant on its owning thread; another launch is another process (thread here).
        (await Task.Run(second.TryAcquire, TestContext.Current.CancellationToken)).Should().BeFalse("one RingView per session");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        second.SignalActivate();

        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task ReleasingLetsTheNextLaunchIn()
    {
        string id = Guid.NewGuid().ToString("N");
        var first = new SingleInstance("RingView.Test.Mutex." + id, "RingView.Test.Pipe." + id);
        first.TryAcquire().Should().BeTrue();
        first.Dispose();

        using var next = new SingleInstance("RingView.Test.Mutex." + id, "RingView.Test.Pipe." + id);
        (await Task.Run(next.TryAcquire, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public void StartWithWindowsWritesAndRemovesItsRunValue()
    {
        // A test-only key, never the real Run key.
        string root = @"Software\RingView.Tests." + Guid.NewGuid().ToString("N");
        string keyPath = root + @"\Run";
        var autoStart = new AutoStart(keyPath, "RingView");
        try
        {
            autoStart.Apply(true, @"C:\Users\someone\AppData\Local\RingView\current\RingView.exe");
            autoStart.IsEnabled.Should().BeTrue();
            autoStart.Command.Should().Be("\"C:\\Users\\someone\\AppData\\Local\\RingView\\current\\RingView.exe\"");

            autoStart.Apply(false, "unused");
            autoStart.IsEnabled.Should().BeFalse();
            autoStart.Apply(false, "unused"); // already off: no error
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }
}

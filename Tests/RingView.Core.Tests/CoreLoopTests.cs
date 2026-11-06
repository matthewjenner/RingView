using RingView.Core.Streaming;

namespace RingView.Core.Tests;

public sealed class CoreLoopTests
{
    [Fact]
    public async Task WorkRunsOnTheLoopThread()
    {
        using var loop = new CoreLoop();
        int thread = await loop.InvokeAsync(() => Task.FromResult(Environment.CurrentManagedThreadId));
        thread.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void WorkPostedAfterShutdownIsDroppedNotThrown()
    {
        var loop = new CoreLoop();
        loop.Dispose();

        Action late = () =>
        {
            loop.Post(_ => { }, null);
            loop.Send(_ => { }, null);
            loop.Dispose();
        };

        late.Should().NotThrow("timers and network callbacks still fire while the app shuts down");
    }
}

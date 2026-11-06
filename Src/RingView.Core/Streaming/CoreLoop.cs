using System.Collections.Concurrent;

namespace RingView.Core.Streaming;

/// <summary>
/// A single-threaded event loop for the controller and its state machines, the way the Electron
/// app's main process ran them: one thread, continuations posted back to it, so there are no
/// locks in the state machines and their timelines are deterministic.
/// </summary>
public sealed class CoreLoop : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public CoreLoop(string name = "RingView core")
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Raised (on the loop) when a posted callback throws; the loop keeps running.</summary>
    public event Action<Exception>? Faulted;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Thread.CurrentThread == _thread)
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        }, null);
        done.Wait();
        if (error is not null)
            throw new InvalidOperationException("Callback failed on the core loop.", error);
    }

    /// <summary>Runs <paramref name="work"/> on the loop and completes with its result.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                tcs.SetResult(await work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public Task InvokeAsync(Func<Task> work) => InvokeAsync(async () =>
    {
        await work();
        return true;
    });

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}

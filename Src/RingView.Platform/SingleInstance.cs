using System.IO.Pipes;

namespace RingView.Platform;

/// <summary>
/// One RingView per Windows session (the Klakr pattern): a named mutex stops a second launch,
/// and a named pipe lets that launch ask the running one to show its window before it exits.
/// </summary>
/// <remarks>
/// Session-local names (no <c>Global\</c>), so each Windows user gets their own. A mutex left
/// by a crash is abandoned, and <see cref="AbandonedMutexException"/> hands it to the next
/// launch. Debug builds use different names (see <see cref="ForThisBuild"/>) so a dev build can
/// run next to the installed app.
/// </remarks>
public sealed class SingleInstance(string mutexName, string pipeName) : IDisposable
{
    private const string ShowCommand = "show";

    private Mutex? _mutex;

    public static SingleInstance ForThisBuild(bool debug) => debug
        ? new("RingView.App.SingleInstance.Debug", "RingView.App.Activation.Debug")
        : new("RingView.App.SingleInstance", "RingView.App.Activation");

    /// <summary>Tries to become the running instance.</summary>
    public bool TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                _mutex = mutex;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            _mutex = mutex; // the previous holder crashed; we own it now
            return true;
        }
        mutex.Dispose();
        return false;
    }

    /// <summary>Asks the running instance to show its window. Silent if it cannot be reached.</summary>
    public void SignalActivate()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(timeout: 500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(ShowCommand);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // The running instance is starting or stopping; nothing more to do.
        }
    }

    /// <summary>Listens for <see cref="SignalActivate"/> until <paramref name="cancellationToken"/> is canceled.</summary>
    public Task ListenAsync(Action onActivate, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) == ShowCommand)
                    onActivate();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false); // rebuild the pipe
            }
        }
    }, CancellationToken.None);

    public void Dispose()
    {
        if (_mutex is null)
            return;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread any more (released on another thread); disposing is enough.
        }
        _mutex.Dispose();
        _mutex = null;
    }
}

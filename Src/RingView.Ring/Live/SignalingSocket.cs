using System.Net.WebSockets;
using System.Text;

namespace RingView.Ring.Live;

/// <summary>Ring's signaling WebSocket over <see cref="ClientWebSocket"/>.</summary>
internal sealed class SignalingSocket : ISignalingChannel
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _receive;
    private int _closed;

    public SignalingSocket()
    {
        // Ring closes the socket at once without a user agent; the value does not seem to matter.
        _socket.Options.SetRequestHeader("User-Agent", RingEndpoints.UserAgent);
    }

    public event Action<string>? MessageReceived;

    public event Action? Closed;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _receive = Task.Run(ReceiveLoopAsync, CancellationToken.None);
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                MessageReceived?.Invoke(text);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        RaiseClosed();
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            Closed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_receive is not null)
            await _receive.ConfigureAwait(false);
        _socket.Dispose();
        _sendGate.Dispose();
        _stop.Dispose();
    }
}

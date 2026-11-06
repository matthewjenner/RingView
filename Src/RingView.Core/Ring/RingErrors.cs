using System.Net.Http;
using System.Net.Sockets;

namespace RingView.Core.Ring;

/// <summary>Classifies exceptions from the Ring layer with <see cref="RingErrorClassifier"/>.</summary>
public static class RingErrors
{
    public static RingErrorKind Classify(Exception error) => error switch
    {
        RingException ring => RingErrorClassifier.Classify(ring.Message, ring.Code),
        SocketException => RingErrorKind.Network,
        HttpRequestException { StatusCode: null } => RingErrorKind.Network,
        TimeoutException => RingErrorKind.Network,
        TaskCanceledException => RingErrorKind.Network,
        _ => RingErrorClassifier.Classify(error.Message),
    };
}

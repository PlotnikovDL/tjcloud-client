namespace TjClient.Core;

public sealed class TransportException(int statusCode, bool transient, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public bool Transient { get; } = transient;
}

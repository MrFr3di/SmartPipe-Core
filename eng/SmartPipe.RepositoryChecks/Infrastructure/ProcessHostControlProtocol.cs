using System.Buffers.Binary;
using System.Text;

namespace SmartPipe.RepositoryChecks.Infrastructure;

internal enum ProcessHostControlMessageKind
{
    Ready,
    Start,
    Cancel,
    Started,
    StartFailed,
    Exit,
}

internal readonly record struct ProcessHostControlMessage(
    ProcessHostControlMessageKind Kind,
    string? Detail = null);

internal sealed class ProcessHostProtocolException : Exception
{
    public ProcessHostProtocolException(string message)
        : base(message)
    {
    }

    public ProcessHostProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class ProcessHostControlProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 512;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task WriteAsync(
        Stream stream,
        string nonce,
        ProcessHostControlMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateNonce(nonce);
        ValidateMessage(message);

        var payload = string.Join(
            '|',
            Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nonce,
            message.Kind.ToString(),
            message.Detail ?? string.Empty);
        var payloadBytes = StrictUtf8.GetBytes(payload);
        if (payloadBytes.Length > MaximumFrameBytes)
        {
            throw new ProcessHostProtocolException("The process-host control frame is too large.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payloadBytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProcessHostControlMessage> ReadAsync(
        Stream stream,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateNonce(expectedNonce);

        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength <= 0 || payloadLength > MaximumFrameBytes)
        {
            throw new ProcessHostProtocolException("The process-host control frame length is invalid.");
        }

        var payloadBytes = new byte[payloadLength];
        await ReadExactlyAsync(stream, payloadBytes, cancellationToken).ConfigureAwait(false);
        string payload;
        try
        {
            payload = StrictUtf8.GetString(payloadBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProcessHostProtocolException("The process-host control frame is not valid UTF-8.", exception);
        }

        var parts = payload.Split('|');
        if (parts.Length != 4
            || !int.TryParse(
                parts[0],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var version)
            || version != Version
            || !string.Equals(parts[1], expectedNonce, StringComparison.Ordinal)
            || !Enum.TryParse<ProcessHostControlMessageKind>(parts[2], ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind)
            || !string.Equals(Enum.GetName(kind), parts[2], StringComparison.Ordinal))
        {
            throw new ProcessHostProtocolException("The process-host control frame is invalid.");
        }

        var message = new ProcessHostControlMessage(
            kind,
            string.IsNullOrEmpty(parts[3]) ? null : parts[3]);
        ValidateMessage(message);
        return message;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ProcessHostProtocolException(
                    "The process-host control channel closed before a complete frame was received.");
            }

            totalRead += read;
        }
    }

    private static void ValidateNonce(string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out _))
        {
            throw new ProcessHostProtocolException("The process-host control nonce is invalid.");
        }
    }

    private static void ValidateMessage(ProcessHostControlMessage message)
    {
        if (message.Detail is { Length: > 64 }
            || message.Detail?.Contains('|', StringComparison.Ordinal) == true
            || (message.Kind == ProcessHostControlMessageKind.Exit
                && !int.TryParse(
                    message.Detail,
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            || (message.Kind != ProcessHostControlMessageKind.Exit
                && message.Kind != ProcessHostControlMessageKind.StartFailed
                && message.Detail is not null))
        {
            throw new ProcessHostProtocolException("The process-host control message payload is invalid.");
        }
    }
}

#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SmartPipe.Core;

/// <summary>Serializes and reads dead-letter envelopes.</summary>
/// <typeparam name="T">Original payload type.</typeparam>
public interface IDeadLetterSerializer<T>
{
    /// <summary>Writes a dead-letter envelope to a stream.</summary>
    /// <param name="envelope">Envelope to write.</param>
    /// <param name="stream">Destination stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A value task representing the write operation.</returns>
    ValueTask WriteAsync(
        DeadLetterEnvelope<T> envelope,
        Stream stream,
        CancellationToken ct = default
    );

    /// <summary>Reads dead-letter envelopes from a stream.</summary>
    /// <param name="stream">Source stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async sequence of envelopes.</returns>
    IAsyncEnumerable<DeadLetterEnvelope<T>> ReadAsync(
        Stream stream,
        CancellationToken ct = default
    );
}

/// <summary>JSON Lines dead-letter serializer.</summary>
/// <typeparam name="T">Original payload type.</typeparam>
/// <remarks>
/// The default format is one JSON envelope per line. Corrupt or partial lines throw
/// <see cref="JsonException"/> so callers can choose whether to stop replay or skip the record.
/// A <see cref="JsonTypeInfo{T}"/> can be supplied for source-generated JSON and AOT scenarios.
/// </remarks>
public sealed class JsonLinesDeadLetterSerializer<T> : IDeadLetterSerializer<T>
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Func<DeadLetterEnvelope<T>, Stream, CancellationToken, ValueTask> _write;
    private readonly Func<Stream, CancellationToken, IAsyncEnumerable<DeadLetterEnvelope<T>>> _read;

    /// <summary>Creates a serializer using reflection-based System.Text.Json metadata.</summary>
    /// <param name="options">Optional JSON options.</param>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use the JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public JsonLinesDeadLetterSerializer(JsonSerializerOptions? options = null)
    {
        var frozenOptions = options == null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        frozenOptions.MakeReadOnly(populateMissingResolver: true);
        _write = (envelope, stream, ct) => WriteReflectionAsync(envelope, stream, frozenOptions, ct);
        _read = (stream, ct) => ReadReflectionAsync(stream, frozenOptions, ct);
    }

    /// <summary>Creates a serializer using source-generated JSON metadata.</summary>
    /// <param name="typeInfo">Source-generated type information.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="typeInfo"/> is null.</exception>
    public JsonLinesDeadLetterSerializer(JsonTypeInfo<DeadLetterEnvelope<T>> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        _write = (envelope, stream, ct) => WriteWithTypeInfoAsync(envelope, stream, typeInfo, ct);
        _read = (stream, ct) => ReadWithTypeInfoAsync(stream, typeInfo, ct);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(
        DeadLetterEnvelope<T> envelope,
        Stream stream,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(stream);
        return _write(envelope, stream, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<DeadLetterEnvelope<T>> ReadAsync(
        Stream stream,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        return _read(stream, ct);
    }

    private static async ValueTask WriteWithTypeInfoAsync(
        DeadLetterEnvelope<T> envelope,
        Stream stream,
        JsonTypeInfo<DeadLetterEnvelope<T>> typeInfo,
        CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(stream, envelope, typeInfo, ct).ConfigureAwait(false);
        await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
    }

    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use the JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    private static async ValueTask WriteReflectionAsync(
        DeadLetterEnvelope<T> envelope,
        Stream stream,
        JsonSerializerOptions? options,
        CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(stream, envelope, options, ct).ConfigureAwait(false);
        await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadWithTypeInfoAsync(
        Stream stream,
        JsonTypeInfo<DeadLetterEnvelope<T>> typeInfo,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prepared = await PrepareJsonStreamAsync(stream, ct).ConfigureAwait(false);
        if (prepared.FirstByte == null)
            yield break;
        var topLevelValues = prepared.FirstByte != (byte)'[';
        await foreach (var envelope in JsonSerializer.DeserializeAsyncEnumerable(
            prepared.Stream,
            typeInfo,
            topLevelValues,
            ct).ConfigureAwait(false))
        {
            if (envelope != null)
                yield return envelope;
        }
    }

    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use the JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    private static async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadReflectionAsync(
        Stream stream,
        JsonSerializerOptions? options,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var prepared = await PrepareJsonStreamAsync(stream, ct).ConfigureAwait(false);
        if (prepared.FirstByte == null)
            yield break;
        var topLevelValues = prepared.FirstByte != (byte)'[';
        await foreach (var envelope in JsonSerializer.DeserializeAsyncEnumerable<DeadLetterEnvelope<T>>(
            prepared.Stream,
            topLevelValues,
            options,
            ct).ConfigureAwait(false))
        {
            if (envelope != null)
                yield return envelope;
        }
    }

    private static async ValueTask<(byte? FirstByte, Stream Stream)> PrepareJsonStreamAsync(
        Stream stream,
        CancellationToken ct)
    {
        var prefix = new byte[3];
        var read = 0;
        while (read < prefix.Length)
        {
            var count = await stream.ReadAsync(prefix.AsMemory(read), ct).ConfigureAwait(false);
            if (count == 0)
                break;
            read += count;
        }
        var bomLength = read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF ? 3 : 0;

        if (stream.CanSeek)
        {
            var start = stream.Position - read + bomLength;
            stream.Position = start;
            var firstByte = await ReadFirstNonWhitespaceByteAsync(stream, ct).ConfigureAwait(false);
            stream.Position = start;
            return (firstByte, stream);
        }

        using var replay = new MemoryStream();
        replay.Write(prefix, bomLength, read - bomLength);
        var first = FindFirstNonWhitespace(prefix.AsSpan(bomLength, read - bomLength));
        var buffer = new byte[1];
        while (first == null && await stream.ReadAsync(buffer, ct).ConfigureAwait(false) == 1)
        {
            replay.WriteByte(buffer[0]);
            first = FindFirstNonWhitespace(buffer);
        }
        return (first, new PrefixReadStream(replay.ToArray(), stream));
    }

    private static async ValueTask<byte?> ReadFirstNonWhitespaceByteAsync(
        Stream stream,
        CancellationToken ct)
    {
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) == 1)
        {
            var value = FindFirstNonWhitespace(buffer);
            if (value != null)
                return value;
        }
        return null;
    }

    private static byte? FindFirstNonWhitespace(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return value;
        return null;
    }

    private sealed class PrefixReadStream(byte[] prefix, Stream inner) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < prefix.Length)
            {
                var copied = Math.Min(count, prefix.Length - _offset);
                prefix.AsSpan(_offset, copied).CopyTo(buffer.AsSpan(offset, copied));
                _offset += copied;
                return copied;
            }
            return inner.Read(buffer, offset, count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

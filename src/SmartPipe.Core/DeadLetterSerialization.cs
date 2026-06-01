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
    private readonly Func<DeadLetterEnvelope<T>, Stream, CancellationToken, ValueTask> _write;
    private readonly Func<Stream, CancellationToken, IAsyncEnumerable<DeadLetterEnvelope<T>>> _read;

    /// <summary>Creates a serializer using reflection-based System.Text.Json metadata.</summary>
    /// <param name="options">Optional JSON options.</param>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use the JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public JsonLinesDeadLetterSerializer(JsonSerializerOptions? options = null)
    {
        _write = (envelope, stream, ct) => WriteReflectionAsync(envelope, stream, options, ct);
        _read = (stream, ct) => ReadReflectionAsync(stream, options, ct);
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
        await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
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
        await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadWithTypeInfoAsync(
        Stream stream,
        JsonTypeInfo<DeadLetterEnvelope<T>> typeInfo,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var envelope = JsonSerializer.Deserialize(line, typeInfo);
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
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var envelope = JsonSerializer.Deserialize<DeadLetterEnvelope<T>>(line, options);

            if (envelope != null)
                yield return envelope;
        }
    }
}

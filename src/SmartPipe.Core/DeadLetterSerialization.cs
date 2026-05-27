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
    private readonly JsonSerializerOptions? _options;
    private readonly JsonTypeInfo<DeadLetterEnvelope<T>>? _typeInfo;

    /// <summary>Creates a serializer using reflection-based System.Text.Json metadata.</summary>
    /// <param name="options">Optional JSON options.</param>
    public JsonLinesDeadLetterSerializer(JsonSerializerOptions? options = null)
    {
        _options = options;
    }

    /// <summary>Creates a serializer using source-generated JSON metadata.</summary>
    /// <param name="typeInfo">Source-generated type information.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="typeInfo"/> is null.</exception>
    public JsonLinesDeadLetterSerializer(JsonTypeInfo<DeadLetterEnvelope<T>> typeInfo)
    {
        _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050:RequiresDynamicCode",
        Justification = "The reflection-based path is documented as non-AOT; callers can use the JsonTypeInfo constructor."
    )]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The reflection-based path is documented as non-trim-safe; callers can use the JsonTypeInfo constructor."
    )]
    public async ValueTask WriteAsync(
        DeadLetterEnvelope<T> envelope,
        Stream stream,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(stream);
        if (_typeInfo != null)
            await JsonSerializer
                .SerializeAsync(stream, envelope, _typeInfo, ct)
                .ConfigureAwait(false);
        else
            await JsonSerializer
                .SerializeAsync(stream, envelope, _options, ct)
                .ConfigureAwait(false);

        await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050:RequiresDynamicCode",
        Justification = "The reflection-based path is documented as non-AOT; callers can use the JsonTypeInfo constructor."
    )]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The reflection-based path is documented as non-trim-safe; callers can use the JsonTypeInfo constructor."
    )]
    public async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            DeadLetterEnvelope<T>? envelope =
                _typeInfo != null
                    ? JsonSerializer.Deserialize(line, _typeInfo)
                    : JsonSerializer.Deserialize<DeadLetterEnvelope<T>>(line, _options);

            if (envelope != null)
                yield return envelope;
        }
    }
}

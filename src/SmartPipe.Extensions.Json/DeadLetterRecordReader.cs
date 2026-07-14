using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

internal sealed class DeadLetterRecordReader<T>
{
    public async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadFramedAsync(
        Stream stream,
        IDeadLetterSerializer<T> serializer,
        DeadLetterSourceOptions options,
        ILogger? logger,
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var recordIndex = 0L;
        await foreach (var record in Utf8LineRecordReader.ReadAsync(stream, options.MaxRecordSizeBytes, ct).ConfigureAwait(false))
        {
            recordIndex++;
            JsonException? invalid = null;
            DeadLetterEnvelope<T>? envelope = null;

            if (record.TooLarge)
            {
                invalid = new JsonException($"JSON record {recordIndex} in '{path}' exceeds the {options.MaxRecordSizeBytes}-byte limit.");
            }
            else
            {
                try
                {
                    envelope = await ReadAndValidateRecordAsync(serializer, record.Bytes, options.MaxDepth, path, recordIndex, ct).ConfigureAwait(false);
                }
                catch (JsonException exception)
                {
                    invalid = exception;
                }
            }

            if (invalid is not null)
            {
                if (options.InvalidRecordBehavior == InvalidJsonRecordBehavior.Throw)
                    throw invalid;
                logger!.LogWarning(invalid, "Skipping invalid dead-letter record {RecordIndex} in {Path}.", recordIndex, path);
                continue;
            }

            yield return envelope!;
        }
    }

    private static async ValueTask<DeadLetterEnvelope<T>> ReadAndValidateRecordAsync(
        IDeadLetterSerializer<T> serializer,
        byte[] bytes,
        int maxDepth,
        string path,
        long recordIndex,
        CancellationToken ct)
    {
        JsonRecordValidator.ValidateObject(bytes, maxDepth, path, recordIndex);
        await using var recordStream = new MemoryStream(bytes, writable: false);
        await using var enumerator = serializer.ReadAsync(recordStream, ct).GetAsyncEnumerator(ct);
        return await EnsureSingleEnvelopeAsync(enumerator, recordIndex, path, ct).ConfigureAwait(false);
    }

    private static async ValueTask<DeadLetterEnvelope<T>> EnsureSingleEnvelopeAsync(
        IAsyncEnumerator<DeadLetterEnvelope<T>> enumerator,
        long recordIndex,
        string path,
        CancellationToken ct)
    {
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            throw new JsonException($"Dead-letter record {recordIndex} in '{path}' produced no envelopes.");
        var envelope = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
            throw new JsonException($"Dead-letter record {recordIndex} in '{path}' produced more than one envelope.");
        if (envelope.OriginalPayload is null)
            throw new JsonException($"Dead-letter record {recordIndex} in '{path}' has a null OriginalPayload.");
        return envelope;
    }
}

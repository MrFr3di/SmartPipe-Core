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
                    JsonRecordValidator.Validate(record.Bytes, options.MaxDepth, path, recordIndex);
                    await using var recordStream = new MemoryStream(record.Bytes, writable: false);
                    await using var enumerator = serializer.ReadAsync(recordStream, ct).GetAsyncEnumerator(ct);
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        throw new JsonException($"Dead-letter record {recordIndex} in '{path}' produced no envelopes.");
                    envelope = enumerator.Current;
                    if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                        throw new JsonException($"Dead-letter record {recordIndex} in '{path}' produced more than one envelope.");
                    if (envelope.OriginalPayload is null)
                        throw new JsonException($"Dead-letter record {recordIndex} in '{path}' has a null OriginalPayload.");
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
}

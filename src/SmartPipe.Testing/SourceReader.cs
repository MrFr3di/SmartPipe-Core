#nullable enable

using System.Runtime.ExceptionServices;
using SmartPipe.Core;

namespace SmartPipe.Testing;

/// <summary>Collects a finite source stream while preserving its envelopes.</summary>
public static class SourceReader
{
    /// <summary>Reads up to <paramref name="maxItems"/> envelopes in source order.</summary>
    /// <remarks>The caller owns source initialization and disposal. This method disposes only its enumerator.</remarks>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The maximum is less than one.</exception>
    /// <exception cref="InvalidOperationException">The source produces more than the maximum.</exception>
    public static async Task<IReadOnlyList<ProcessingEnvelope<T>>> ReadEnvelopesAsync<T>(
        IPipelineSource<T> source,
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var envelopes = new List<ProcessingEnvelope<T>>();
        var enumerator = source.ReadEnvelopesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Exception? primary = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await enumerator.MoveNextAsync())
                    break;
                if (envelopes.Count == maxItems)
                    throw new InvalidOperationException("The source produced more than maxItems envelopes.");
                envelopes.Add(enumerator.Current);
            }
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        try
        {
            await enumerator.DisposeAsync();
        }
        catch (Exception cleanup)
        {
            if (primary is not null)
                throw new AggregateException(primary, cleanup);
            throw;
        }

        if (primary is not null)
            ExceptionDispatchInfo.Capture(primary).Throw();

        return envelopes;
    }
}

using System.Threading.Channels;

namespace SmartPipe.Extensions;

/// <summary>
/// Provides methods for merging multiple <see cref="ChannelReader{T}"/> streams into a single reader.
/// Items from all readers are interleaved as they arrive.
/// </summary>
public static class ChannelMerge
{
    /// <summary>
    /// Merges two <see cref="ChannelReader{T}"/> instances into a single channel reader.
    /// Both readers are pumped concurrently, and items are written to the output as they arrive.
    /// Uses unbounded channel by default; pass <paramref name="options"/> for bounded capacity.
    /// </summary>
    /// <typeparam name="T">The type of items in the channels.</typeparam>
    /// <param name="first">The first channel reader.</param>
    /// <param name="second">The second channel reader.</param>
    /// <param name="options">Optional bounded channel options. If null, an unbounded channel is created.</param>
    /// <returns>A <see cref="ChannelReader{T}"/> that receives items from both input readers.</returns>
#pragma warning disable RS0027 // Existing optional overload preserved for source compatibility.
    public static ChannelReader<T> Merge<T>(
        ChannelReader<T> first,
        ChannelReader<T> second,
        BoundedChannelOptions? options = null
    )
    {
        return Merge(first, second, options, CancellationToken.None);
    }
#pragma warning restore RS0027

    /// <summary>
    /// Merges two <see cref="ChannelReader{T}"/> instances into a single channel reader.
    /// Both readers are pumped concurrently until they complete or cancellation is requested.
    /// Uses unbounded channel by default; pass <paramref name="options"/> for bounded capacity.
    /// </summary>
    /// <typeparam name="T">The type of items in the channels.</typeparam>
    /// <param name="first">The first channel reader.</param>
    /// <param name="second">The second channel reader.</param>
    /// <param name="options">Optional bounded channel options. If null, an unbounded channel is created.</param>
    /// <param name="cancellationToken">A token that cancels both input pumps.</param>
    /// <returns>A <see cref="ChannelReader{T}"/> that receives items from both input readers.</returns>
    public static ChannelReader<T> Merge<T>(
        ChannelReader<T> first,
        ChannelReader<T> second,
        BoundedChannelOptions? options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var output =
            options != null ? Channel.CreateBounded<T>(options) : Channel.CreateUnbounded<T>();

        _ = CompleteMergeAsync(first, second, output.Writer, cancellationToken);

        return output.Reader;
    }

    private static async Task CompleteMergeAsync<T>(
        ChannelReader<T> first,
        ChannelReader<T> second,
        ChannelWriter<T> writer,
        CancellationToken cancellationToken
    )
    {
        var coordinator = new MergeFailureCoordinator(cancellationToken);

        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var firstPump = PumpAndCancelOnFailureAsync(
            first,
            writer,
            pumpCancellation,
            coordinator);
        var secondPump = PumpAndCancelOnFailureAsync(
            second,
            writer,
            pumpCancellation,
            coordinator);

        try
        {
            await Task.WhenAll(firstPump, secondPump).ConfigureAwait(false);
        }
        catch
        {
            // Completion is coordinated explicitly so sibling cancellation or
            // cancellation callback failures cannot replace the primary input failure.
        }
        finally
        {
            writer.TryComplete(coordinator.GetCompletionError());
        }
    }

    private static async Task PumpAndCancelOnFailureAsync<T>(
        ChannelReader<T> reader,
        ChannelWriter<T> writer,
        CancellationTokenSource cancellationSource,
        MergeFailureCoordinator coordinator
    )
    {
        try
        {
            await PumpAsync(reader, writer, cancellationSource.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !cancellationSource.IsCancellationRequested)
            {
                coordinator.TryRecordFailure(ex);
                try
                {
                    await cancellationSource.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception cancellationFailure)
                {
                    coordinator.TryRecordFailure(cancellationFailure);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Pumps items from a source <see cref="ChannelReader{T}"/> to a target <see cref="ChannelWriter{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type of items.</typeparam>
    /// <param name="reader">The source channel reader.</param>
    /// <param name="writer">The target channel writer.</param>
    /// <param name="cancellationToken">A token that cancels pending reads and writes.</param>
    private static async Task PumpAsync<T>(
        ChannelReader<T> reader,
        ChannelWriter<T> writer,
        CancellationToken cancellationToken
    )
    {
        await foreach (
            var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)
        )
        {
            var written = false;

            while (
                await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false)
            )
            {
                if (writer.TryWrite(item))
                {
                    written = true;
                    break;
                }
            }

            if (!written)
                return;
        }
    }

    private sealed class MergeFailureCoordinator
    {
        private readonly object _gate = new();
        private readonly CancellationToken _externalCancellationToken;
        private Exception? _primaryFailure;

        public MergeFailureCoordinator(CancellationToken externalCancellationToken)
        {
            _externalCancellationToken = externalCancellationToken;
        }

        public void TryRecordFailure(Exception exception)
        {
            lock (_gate)
                _primaryFailure ??= exception;
        }

        public Exception? GetCompletionError()
        {
            lock (_gate)
            {
                if (_primaryFailure is not null)
                    return _primaryFailure;
            }

            return _externalCancellationToken.IsCancellationRequested
                ? new OperationCanceledException(_externalCancellationToken)
                : null;
        }
    }
}

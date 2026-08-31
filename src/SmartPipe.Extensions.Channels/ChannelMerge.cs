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
        BoundedChannelOptions? options = null)
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return MergeMany(
            new[] { first, second },
            options,
            cancellationToken);
    }

    /// <summary>
    /// Merges all readers into a single channel reader.
    /// Each reader is pumped concurrently and retains its own item order.
    /// </summary>
    /// <typeparam name="T">The type of items in the channels.</typeparam>
    /// <param name="readers">The readers to merge.</param>
    /// <returns>A reader receiving items from every input reader.</returns>
    public static ChannelReader<T> Merge<T>(
        IReadOnlyList<ChannelReader<T>> readers)
    {
        return MergeMany(readers, null, CancellationToken.None);
    }

    /// <summary>
    /// Merges all readers into a single channel reader with output configuration and cancellation support.
    /// </summary>
    /// <typeparam name="T">The type of items in the channels.</typeparam>
    /// <param name="readers">The readers to merge.</param>
    /// <param name="options">Optional bounded output channel options.</param>
    /// <param name="cancellationToken">A token that cancels all input pumps.</param>
    /// <returns>A reader receiving items from every input reader.</returns>
    public static ChannelReader<T> MergeMany<T>(
        IReadOnlyList<ChannelReader<T>> readers,
        BoundedChannelOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readers);

        if (readers.Count == 0)
        {
            var emptyOutput = CreateOutput<T>(options);
            emptyOutput.Writer.TryComplete();
            return emptyOutput.Reader;
        }

        var readerSnapshot = new ChannelReader<T>[readers.Count];
        for (var index = 0; index < readers.Count; index++)
        {
            readerSnapshot[index] = readers[index]
                ?? throw new ArgumentNullException(nameof(readers));
        }

        var output = CreateOutput<T>(options);
        _ = CompleteMergeAsync(
            readerSnapshot,
            output.Writer,
            cancellationToken);

        return output.Reader;
    }

    private static Channel<T> CreateOutput<T>(BoundedChannelOptions? options)
    {
        if (options is null)
            return Channel.CreateUnbounded<T>();

        var snapshot = new BoundedChannelOptions(options.Capacity)
        {
            FullMode = options.FullMode,
            SingleReader = options.SingleReader,
            SingleWriter = false,
            AllowSynchronousContinuations = options.AllowSynchronousContinuations,
        };

        return Channel.CreateBounded<T>(snapshot);
    }

    private static async Task CompleteMergeAsync<T>(
        IReadOnlyList<ChannelReader<T>> readers,
        ChannelWriter<T> writer,
        CancellationToken externalCancellationToken)
    {
        var coordinator = new MergeFailureCoordinator(externalCancellationToken);
        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            externalCancellationToken);
        var pumps = new Task[readers.Count];

        for (var index = 0; index < readers.Count; index++)
        {
            pumps[index] = PumpAndCancelOnFailureAsync(
                readers[index],
                writer,
                pumpCancellation,
                coordinator,
                index);
        }

        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        catch
        {
            // Completion is coordinated explicitly so sibling cancellation cannot
            // replace an observed input failure.
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
        MergeFailureCoordinator coordinator,
        int readerIndex)
    {
        try
        {
            await PumpAsync(reader, writer, cancellationSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException || !cancellationSource.IsCancellationRequested)
            {
                coordinator.RecordInputFailure(readerIndex, exception);
                try
                {
                    await cancellationSource.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception cancellationFailure)
                {
                    coordinator.RecordCancellationFailure(cancellationFailure);
                }
            }

            throw;
        }
    }

    private static async Task PumpAsync<T>(
        ChannelReader<T> reader,
        ChannelWriter<T> writer,
        CancellationToken cancellationToken)
    {
        await foreach (
            var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                if (writer.TryWrite(item))
                    break;
            }
        }
    }

    private sealed class MergeFailureCoordinator(CancellationToken externalCancellationToken)
    {
        private readonly object _gate = new();
        private readonly CancellationToken _externalCancellationToken = externalCancellationToken;
        private readonly List<InputFailure> _inputFailures = [];
        private Exception? _cancellationFailure;

        public Exception? GetCompletionError()
        {
            lock (_gate)
            {
                if (_inputFailures.Count > 0)
                {
                    var primary = _inputFailures[0];
                    for (var index = 1; index < _inputFailures.Count; index++)
                    {
                        if (_inputFailures[index].ReaderIndex < primary.ReaderIndex)
                            primary = _inputFailures[index];
                    }

                    return _cancellationFailure is null
                        ? primary.Exception
                        : new AggregateException(primary.Exception, _cancellationFailure);
                }

                if (_cancellationFailure is not null)
                    return _cancellationFailure;
            }

            return _externalCancellationToken.IsCancellationRequested
                ? new OperationCanceledException(_externalCancellationToken)
                : null;
        }

        public void RecordCancellationFailure(Exception exception)
        {
            lock (_gate)
                _cancellationFailure ??= exception;
        }

        public void RecordInputFailure(int readerIndex, Exception exception)
        {
            lock (_gate)
                _inputFailures.Add(new InputFailure(readerIndex, exception));
        }

        private sealed record InputFailure(int ReaderIndex, Exception Exception);
    }
}

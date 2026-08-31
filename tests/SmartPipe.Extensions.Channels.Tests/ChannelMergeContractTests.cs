using System.Threading.Channels;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Channels.Tests;

public sealed class ChannelMergeContractTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LivenessTimeout = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void Merge_NullReaderCollection_ThrowsArgumentNullException()
    {
        IReadOnlyList<ChannelReader<int>>? readers = null;
        var act = () =>
        {
            _ = ChannelMerge.Merge<int>(readers!);
        };

        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public void Merge_NullReaderElement_ThrowsArgumentException()
    {
        ChannelReader<int>[] readers = [Channel.CreateUnbounded<int>().Reader, null!];

        var act = () =>
        {
            _ = ChannelMerge.Merge(readers);
        };

        var exception = Assert.Throws<ArgumentNullException>(act);

        Assert.Equal("readers", exception.ParamName);
    }

    [Fact]
    public void MergeMany_NullReaderElementIsValidatedBeforeInvalidOptions()
    {
        ChannelReader<int>[] readers = [null!];
        var invalidOptions = new BoundedChannelOptions(1);
        // The public setter rejects invalid modes, so seed the invalid state only to test validation order.
        var modeField = typeof(BoundedChannelOptions).GetField(
            "_mode",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(modeField);
        modeField.SetValue(invalidOptions, (BoundedChannelFullMode)int.MaxValue);

        var exception = Assert.Throws<ArgumentNullException>(
            () => _ = ChannelMerge.MergeMany(readers, invalidOptions, CancellationToken.None));

        Assert.Equal("readers", exception.ParamName);
    }

    [Fact]
    public async Task Merge_ZeroReaders_CompletesAsEmpty()
    {
        var merged = ChannelMerge.Merge<int>(Array.Empty<ChannelReader<int>>());

        var results = await ReadAllWithTimeoutAsync(merged);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Merge_OneReader_PreservesReaderOrder()
    {
        var source = CreateCompletedReader([1, 2, 3]);

        var merged = ChannelMerge.Merge(new[] { source });
        var results = await ReadAllWithTimeoutAsync(merged);

        Assert.Equal([1, 2, 3], results);
    }

    [Fact]
    public async Task Merge_NReaders_PreservesPerReaderOrderAndBackpressure()
    {
        var readers = new[]
        {
            CreateCompletedReader([0, 1, 2, 3]),
            CreateCompletedReader([100, 101, 102, 103]),
            CreateCompletedReader([200, 201, 202, 203]),
        };
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true,
        };

        var merged = ChannelMerge.MergeMany(readers, options, CancellationToken.None);
        var results = await ReadAllWithTimeoutAsync(merged);

        Assert.Equal(12, results.Count);
        Assert.Equal([0, 1, 2, 3], results.Where(value => value < 100));
        Assert.Equal([100, 101, 102, 103], results.Where(value => value is >= 100 and < 200));
        Assert.Equal([200, 201, 202, 203], results.Where(value => value >= 200));
    }

    [Fact]
    public async Task Merge_BoundedCapacityBlocksSecondWriteUntilFirstItemIsConsumed()
    {
        var source = new ReadTrackingReader<int>([1, 2, 3]);
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        };
        var merged = ChannelMerge.MergeMany(new[] { source }, options, CancellationToken.None);

        await WaitForOutputReadyAsync(merged);
        await WaitWithTimeout(source.SecondRead);

        await Assert.ThrowsAsync<TimeoutException>(
            () => source.ThirdRead.WaitAsync(LivenessTimeout, TestContext.Current.CancellationToken));

        Assert.True(merged.TryRead(out var first));
        Assert.Equal(1, first);
        await WaitWithTimeout(source.ThirdRead);

        var remaining = await ReadAllWithTimeoutAsync(merged);

        Assert.Equal([2, 3], remaining);
    }

    [Fact]
    public async Task Merge_LegacyPairOverload_RemainsUsable()
    {
        var first = CreateCompletedReader([1, 2, 3]);
        var second = CreateCompletedReader([10, 11, 12]);

        var merged = ChannelMerge.Merge(first, second);
        var results = await ReadAllWithTimeoutAsync(merged);

        Assert.Equal([1, 2, 3], results.Where(value => value < 10));
        Assert.Equal([10, 11, 12], results.Where(value => value >= 10));
    }

    [Fact]
    public void Merge_LegacyPairOverload_NullFirst_ThrowsArgumentNullException()
    {
        var second = Channel.CreateUnbounded<int>();
        var act = () =>
        {
            _ = ChannelMerge.Merge<int>(null!, second.Reader);
        };

        var exception = Assert.Throws<ArgumentNullException>(act);

        Assert.Equal("first", exception.ParamName);
    }

    [Fact]
    public void Merge_LegacyPairOverload_NullSecond_ThrowsArgumentNullException()
    {
        var first = Channel.CreateUnbounded<int>();
        var act = () =>
        {
            _ = ChannelMerge.Merge<int>(first.Reader, null!);
        };

        var exception = Assert.Throws<ArgumentNullException>(act);

        Assert.Equal("second", exception.ParamName);
    }

    [Fact]
#pragma warning disable xUnit1051 // Default tokens are intentional source-compatibility probes.
    public void Merge_LegacyPairOverload_AllNullAndDefaultCallsRemainSourceCompatible()
    {
        var twoNull = Assert.Throws<ArgumentNullException>(
            () => _ = ChannelMerge.Merge<int>(null!, null!));
        var twoDefault = Assert.Throws<ArgumentNullException>(
            () => _ = ChannelMerge.Merge<int>(default!, default!));
        var threeNull = Assert.Throws<ArgumentNullException>(
            () => _ = ChannelMerge.Merge<int>(null!, null!, null));
        var threeDefault = Assert.Throws<ArgumentNullException>(
            () => _ = ChannelMerge.Merge<int>(default!, default!, default));
        var fourNull = Assert.Throws<ArgumentNullException>(
            () => _ = ChannelMerge.Merge<int>(null!, null!, null, default));
        var fourDefault = Assert.Throws<ArgumentNullException>(
            () => _ = ChannelMerge.Merge<int>(default!, default!, default!, default));

        Assert.All(
            new[] { twoNull, twoDefault, threeNull, threeDefault, fourNull, fourDefault },
            exception => Assert.Equal("first", exception.ParamName));
    }
#pragma warning restore xUnit1051

    [Fact]
    public async Task Merge_InputFailureWithCancellationCallbackFailure_PreservesPrimaryThenCallbackAggregate()
    {
        var first = Channel.CreateUnbounded<int>();
        var callbackFailure = new InvalidOperationException("cancellation callback failed");
        var second = new CancellationCallbackFailureReader<int>(callbackFailure);
        var primary = new InvalidOperationException("primary input failed");
        var merged = ChannelMerge.Merge(first.Reader, second);

        await WaitWithTimeout(second.Started);
        first.Writer.TryComplete(primary);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => ReadAllWithTimeoutAsync(merged));

        Assert.Same(primary, exception.InnerExceptions[0]);
        var callbackAggregate = Assert.IsType<AggregateException>(exception.InnerExceptions[1]);
        Assert.Same(callbackFailure, callbackAggregate.InnerExceptions[0]);
    }

    [Fact]
    public async Task Merge_OptionsAreSnapshottedBeforeCallerMutation()
    {
        var readers = new[]
        {
            CreateCompletedReader([1, 2, 3]),
            CreateCompletedReader([4, 5, 6]),
        };
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        };

        var merged = ChannelMerge.MergeMany(readers, options, CancellationToken.None);
        Assert.Equal(1, options.Capacity);
        Assert.Equal(BoundedChannelFullMode.Wait, options.FullMode);
        Assert.True(options.SingleReader);
        Assert.True(options.SingleWriter);
        Assert.False(options.AllowSynchronousContinuations);

        options.FullMode = BoundedChannelFullMode.DropWrite;
        options.Capacity = 2;
        options.SingleReader = false;
        options.SingleWriter = false;
        options.AllowSynchronousContinuations = true;

        var results = await ReadAllWithTimeoutAsync(merged);

        Assert.Equal(6, results.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], results.OrderBy(value => value));
    }

    [Fact]
    public async Task Merge_PreCancelledToken_ShutsDownOutputAsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var readers = new[]
        {
            Channel.CreateUnbounded<int>().Reader,
            Channel.CreateUnbounded<int>().Reader,
        };

        var merged = ChannelMerge.MergeMany(readers, null, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAllWithTimeoutAsync(merged));
    }

    [Fact]
    public async Task Merge_CancellationWithReadyData_PreservesQueuedDataAndFaultsOutput()
    {
        var ready = CreateCompletedReader([42]);
        var pending = new CancellationGateReader<int>();
        using var cancellation = new CancellationTokenSource();
        var merged = ChannelMerge.MergeMany(
            new[] { ready, pending },
            options: null,
            cancellation.Token);

        await WaitWithTimeout(pending.Started);
        await WaitForOutputReadyAsync(merged);
        cancellation.Cancel();
        await WaitWithTimeout(pending.Cancelled);

        var observation = await ReadAllCapturingCancellationWithTimeoutAsync(merged);

        Assert.True(observation.Canceled);
        Assert.Equal([42], observation.Items);
    }

    [Fact]
    public async Task Merge_CancellationWhileWriteIsPending_UnblocksPumpAndFaultsOutput()
    {
        var source = new PendingAfterItemsReader<int>([1, 2]);
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        };
        using var cancellation = new CancellationTokenSource();
        var merged = ChannelMerge.MergeMany(new[] { source }, options, cancellation.Token);

        await WaitForOutputReadyAsync(merged);
        await WaitWithTimeout(source.SecondRead);
        cancellation.Cancel();

        var observation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAllWithTimeoutAsync(merged));

        Assert.NotNull(observation);
        Assert.False(source.AfterItemsWaitEntered.IsCompleted);
    }

    [Fact]
    public async Task Merge_MultipleInputFailures_UsesLowestReaderIndexAsPrimary()
    {
        var first = new GateFaultReader<int>();
        var second = new GateFaultReader<int>();
        var expectedPrimary = new InvalidOperationException("reader zero failed");
        var secondary = new InvalidOperationException("reader one failed");
        var merged = ChannelMerge.Merge(new ChannelReader<int>[] { first, second });

        await WaitWithTimeout(Task.WhenAll(first.Started, second.Started));
        second.Fail(secondary);
        await WaitWithTimeout(second.FailureThrown);
        first.Fail(expectedPrimary);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAllWithTimeoutAsync(merged));

        Assert.Same(expectedPrimary, exception);
    }

    private static ChannelReader<T> CreateCompletedReader<T>(IEnumerable<T> items)
    {
        var channel = Channel.CreateUnbounded<T>();
        foreach (var item in items)
            channel.Writer.TryWrite(item);
        channel.Writer.TryComplete();
        return channel.Reader;
    }

    private static Task<List<T>> ReadAllWithTimeoutAsync<T>(ChannelReader<T> reader)
    {
        return ReadAllAsync(reader, TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private static Task<(List<T> Items, bool Canceled)> ReadAllCapturingCancellationWithTimeoutAsync<T>(
        ChannelReader<T> reader)
    {
        return ReadAllCapturingCancellationAsync(reader, TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private static async Task WaitForOutputReadyAsync<T>(ChannelReader<T> reader)
    {
        var ready = await reader.WaitToReadAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(ready);
    }

    private static Task WaitWithTimeout(Task task)
    {
        return task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private static async Task<List<T>> ReadAllAsync<T>(
        ChannelReader<T> reader,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();
        await foreach (var item in reader.ReadAllAsync(cancellationToken))
            results.Add(item);
        return results;
    }

    private static async Task<(List<T> Items, bool Canceled)> ReadAllCapturingCancellationAsync<T>(
        ChannelReader<T> reader,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();
        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken))
                results.Add(item);
        }
        catch (OperationCanceledException)
        {
            return (results, true);
        }

        return (results, false);
    }

    private sealed class ReadTrackingReader<T> : ChannelReader<T>
    {
        private readonly Channel<T> _source = Channel.CreateUnbounded<T>();
        private readonly TaskCompletionSource _secondRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _thirdRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public ReadTrackingReader(IEnumerable<T> items)
        {
            foreach (var item in items)
                _source.Writer.TryWrite(item);
            _source.Writer.TryComplete();
        }

        public Task SecondRead => _secondRead.Task;

        public Task ThirdRead => _thirdRead.Task;

        public override bool TryRead(out T item)
        {
            if (!_source.Reader.TryRead(out item!))
                return false;

            switch (Interlocked.Increment(ref _readCount))
            {
                case 2:
                    _secondRead.TrySetResult();
                    break;
                case 3:
                    _thirdRead.TrySetResult();
                    break;
            }

            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(
            CancellationToken cancellationToken = default)
        {
            return _source.Reader.WaitToReadAsync(cancellationToken);
        }
    }

    private sealed class PendingAfterItemsReader<T> : ChannelReader<T>
    {
        private readonly Queue<T> _items;
        private readonly TaskCompletionSource<bool> _afterItemsWait =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _afterItemsWaitEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public PendingAfterItemsReader(IEnumerable<T> items)
        {
            _items = new Queue<T>(items);
        }

        public Task AfterItemsWaitEntered => _afterItemsWaitEntered.Task;

        public Task SecondRead => _secondRead.Task;

        public override bool TryRead(out T item)
        {
            if (_items.Count == 0)
            {
                item = default!;
                return false;
            }

            item = _items.Dequeue();
            if (Interlocked.Increment(ref _readCount) == 2)
                _secondRead.TrySetResult();
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(
            CancellationToken cancellationToken = default)
        {
            if (_items.Count > 0)
                return ValueTask.FromResult(true);

            _afterItemsWaitEntered.TrySetResult();
            return new ValueTask<bool>(_afterItemsWait.Task);
        }
    }

    private sealed class CancellationGateReader<T> : ChannelReader<T>
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Cancelled => _cancelled.Task;

        public Task Started => _started.Task;

        public override bool TryRead(out T item)
        {
            item = default!;
            return false;
        }

        public override async ValueTask<bool> WaitToReadAsync(
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            try
            {
                return await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class CancellationCallbackFailureReader<T> : ChannelReader<T>
    {
        private readonly Exception _callbackFailure;
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationCallbackFailureReader(Exception callbackFailure)
        {
            _callbackFailure = callbackFailure;
        }

        public Task Started => _started.Task;

        public override bool TryRead(out T item)
        {
            item = default!;
            return false;
        }

        public override async ValueTask<bool> WaitToReadAsync(
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await using var completeRegistration = cancellationToken.UnsafeRegister(
                static state =>
                {
                    var reader = (CancellationCallbackFailureReader<T>)state!;
                    reader._completion.TrySetCanceled();
                },
                this);
            await using var throwingRegistration = cancellationToken.UnsafeRegister(
                static state =>
                {
                    var reader = (CancellationCallbackFailureReader<T>)state!;
                    throw reader._callbackFailure;
                },
                this);

            return await _completion.Task.ConfigureAwait(false);
        }
    }

    private sealed class GateFaultReader<T> : ChannelReader<T>
    {
        private readonly TaskCompletionSource<Exception> _failure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _failureThrown =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FailureThrown => _failureThrown.Task;

        public Task Started => _started.Task;

        public override bool TryRead(out T item)
        {
            item = default!;
            return false;
        }

        public override async ValueTask<bool> WaitToReadAsync(
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            var failure = await _failure.Task.ConfigureAwait(false);
            _failureThrown.TrySetResult();
            throw failure;
        }

        public void Fail(Exception exception)
        {
            _failure.TrySetResult(exception);
        }
    }
}

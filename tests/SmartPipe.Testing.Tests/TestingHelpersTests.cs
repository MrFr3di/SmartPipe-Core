#nullable enable

using SmartPipe.Core;
using SmartPipe.Testing;

namespace SmartPipe.Testing.Tests;

public sealed class TestingHelpersTests
{
    [Fact]
    public void Create_PreservesKeyAndUsesFreshRunIds()
    {
        var first = TestActivation.Create(" Orders ");
        var second = TestActivation.Create(" Orders ");

        Assert.Equal(" Orders ", first.PipelineKey.Value);
        Assert.Equal(first.PipelineKey, second.PipelineKey);
        Assert.NotEqual(Guid.Empty, first.RunId);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Null(first.Services);
        Assert.Same(TimeProvider.System, first.TimeProvider);
    }

    [Fact]
    public void Create_UsesCoreKeyValidation()
    {
        Assert.Throws<ArgumentNullException>(() => TestActivation.Create(null!));
        Assert.Throws<ArgumentException>(() => TestActivation.Create(" "));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public async Task ReadEnvelopesAsync_ReturnsEmptyBelowAndAtLimit(int count, int maxItems)
    {
        var items = Enumerable.Range(1, count).Select(ProcessingEnvelope<int>.Create).ToArray();
        var source = new ProbeSource<int>(items);

        var result = await SourceReader.ReadEnvelopesAsync(source, maxItems);

        Assert.Equal(count, result.Count);
        for (var index = 0; index < count; index++)
            Assert.Same(items[index], result[index]);
        Assert.Equal(1, source.ReadCalls);
        Assert.Equal(count + 1, source.MoveNextCalls);
        Assert.Equal(1, source.EnumeratorDisposeCalls);
        Assert.Equal(0, source.InitializeCalls);
        Assert.Equal(0, source.SourceDisposeCalls);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ReturnsANewListForEachCall()
    {
        var envelope = ProcessingEnvelope<int>.Create(1);
        var source = new ProbeSource<int>([envelope]);

        var first = await SourceReader.ReadEnvelopesAsync(source, 1);
        var second = await SourceReader.ReadEnvelopesAsync(source, 1);

        Assert.NotSame(first, second);
        Assert.Same(envelope, first[0]);
        Assert.Same(envelope, second[0]);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ThrowsOnFirstExcessItemAndDisposesEnumerator()
    {
        var source = new ProbeSource<int>(
            [ProcessingEnvelope<int>.Create(1), ProcessingEnvelope<int>.Create(2)]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SourceReader.ReadEnvelopesAsync(source, 1));

        Assert.Equal(2, source.MoveNextCalls);
        Assert.Equal(1, source.EnumeratorDisposeCalls);
        Assert.Equal(0, source.SourceDisposeCalls);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_RejectsInvalidArgumentsBeforeSourceRead()
    {
        var source = new ProbeSource<int>([]);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SourceReader.ReadEnvelopesAsync<int>(null!, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SourceReader.ReadEnvelopesAsync(source, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SourceReader.ReadEnvelopesAsync(source, -1));
        Assert.Equal(0, source.ReadCalls);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_PreCanceledTokenFailsBeforeSourceRead()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var source = new ProbeSource<int>([]);

        var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SourceReader.ReadEnvelopesAsync(source, 1, cancellation.Token));

        Assert.Equal(cancellation.Token, observed.CancellationToken);
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(0, source.EnumeratorDisposeCalls);
        Assert.Equal(0, source.SourceDisposeCalls);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_PropagatesSourceFaultAndDisposesEnumerator()
    {
        var failure = new InvalidOperationException("source failed");
        var source = new ProbeSource<int>([]) { MoveFailure = failure };

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SourceReader.ReadEnvelopesAsync(source, 1));

        Assert.Same(failure, observed);
        Assert.Equal(1, source.EnumeratorDisposeCalls);
        Assert.Equal(0, source.SourceDisposeCalls);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_CancelsGatedNextPullAndDisposesEnumerator()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new ProbeSource<int>([ProcessingEnvelope<int>.Create(1)])
        {
            GateAtMove = 2,
        };
        var read = SourceReader.ReadEnvelopesAsync(source, 1, cancellation.Token);
        await source.MoveStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        Assert.Equal(cancellation.Token, source.ReadToken);
        Assert.Equal(cancellation.Token, observed.CancellationToken);
        Assert.Equal(2, source.MoveNextCalls);
        Assert.Equal(1, source.EnumeratorDisposeCalls);
        Assert.Equal(0, source.SourceDisposeCalls);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_PropagatesCleanupOnlyFailure()
    {
        var cleanup = new InvalidOperationException("cleanup failed");
        var source = new ProbeSource<int>([]) { DisposeFailure = cleanup };

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SourceReader.ReadEnvelopesAsync(source, 1));

        Assert.Same(cleanup, observed);
        Assert.Equal(1, source.EnumeratorDisposeCalls);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_AggregatesSourceFailureBeforeCleanup()
    {
        var sourceFailure = new InvalidOperationException("source failed");
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var source = new ProbeSource<int>([])
        {
            MoveFailure = sourceFailure,
            DisposeFailure = cleanupFailure,
        };

        var observed = await Assert.ThrowsAsync<AggregateException>(
            () => SourceReader.ReadEnvelopesAsync(source, 1));

        Assert.Same(sourceFailure, observed.InnerExceptions[0]);
        Assert.Same(cleanupFailure, observed.InnerExceptions[1]);
        Assert.Equal(0, source.SourceDisposeCalls);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_AggregatesOverflowBeforeCleanup()
    {
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var source = new ProbeSource<int>(
            [ProcessingEnvelope<int>.Create(1), ProcessingEnvelope<int>.Create(2)])
        {
            DisposeFailure = cleanupFailure,
        };

        var observed = await Assert.ThrowsAsync<AggregateException>(
            () => SourceReader.ReadEnvelopesAsync(source, 1));

        Assert.IsType<InvalidOperationException>(observed.InnerExceptions[0]);
        Assert.Same(cleanupFailure, observed.InnerExceptions[1]);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_AggregatesCancellationBeforeCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var source = new ProbeSource<int>([])
        {
            GateAtMove = 1,
            DisposeFailure = cleanupFailure,
        };
        var read = SourceReader.ReadEnvelopesAsync(source, 1, cancellation.Token);
        await source.MoveStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var observed = await Assert.ThrowsAsync<AggregateException>(() => read);

        Assert.IsAssignableFrom<OperationCanceledException>(observed.InnerExceptions[0]);
        Assert.Same(cleanupFailure, observed.InnerExceptions[1]);
        Assert.Equal(1, source.EnumeratorDisposeCalls);
        Assert.Equal(0, source.SourceDisposeCalls);
    }

    private sealed class ProbeSource<T>(IReadOnlyList<ProcessingEnvelope<T>> items)
        : IPipelineSource<T>, IAsyncEnumerable<ProcessingEnvelope<T>>
    {
        public int InitializeCalls { get; private set; }
        public int SourceDisposeCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int MoveNextCalls { get; private set; }
        public int EnumeratorDisposeCalls { get; private set; }
        public int GateAtMove { get; init; }
        public Exception? MoveFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public CancellationToken ReadToken { get; private set; }
        public TaskCompletionSource MoveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            InitializeCalls++;
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(CancellationToken ct = default)
        {
            ReadCalls++;
            ReadToken = ct;
            return this;
        }

        public IAsyncEnumerator<ProcessingEnvelope<T>> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new ProbeEnumerator(this, items);

        public ValueTask DisposeAsync()
        {
            SourceDisposeCalls++;
            return ValueTask.CompletedTask;
        }

        private sealed class ProbeEnumerator(ProbeSource<T> owner, IReadOnlyList<ProcessingEnvelope<T>> values)
            : IAsyncEnumerator<ProcessingEnvelope<T>>
        {
            private int _index;

            public ProcessingEnvelope<T> Current { get; private set; } = null!;

            public async ValueTask<bool> MoveNextAsync()
            {
                owner.MoveNextCalls++;
                if (owner.GateAtMove == owner.MoveNextCalls)
                {
                    owner.MoveStarted.TrySetResult();
                    await owner.Gate.Task.WaitAsync(owner.ReadToken);
                }

                if (owner.MoveFailure is not null)
                    throw owner.MoveFailure;
                if (_index == values.Count)
                    return false;
                Current = values[_index++];
                return true;
            }

            public ValueTask DisposeAsync()
            {
                owner.EnumeratorDisposeCalls++;
                if (owner.DisposeFailure is not null)
                    throw owner.DisposeFailure;
                return ValueTask.CompletedTask;
            }
        }
    }
}

using FluentAssertions;
using SmartPipe.Core;
using System.Threading.Channels;

namespace SmartPipe.Core.Tests.Engine;

public class RunInBackgroundTests
{
    [Fact]
    public async Task RunInBackground_ShouldReturnReader()
    {
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PassthroughTransformer<int>();

        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(source);
        pipe.AddTransformer(transformer);

        var reader = pipe.RunInBackground();

        var results = await ReadResultsAsync(reader).WaitAsync(TimeSpan.FromSeconds(5));

        results.Where(static result => result.IsSuccess)
            .Select(static result => result.Value)
            .Should()
            .Equal(1, 2, 3);
        reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WithoutUserSink_ShouldStillThrow()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1));
        pipe.AddTransformer(new PassthroughTransformer<int>());

        await pipe.Invoking(p => p.RunAsync())
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*sink*");
    }

    [Fact]
    public async Task RunInBackground_WithoutUserSink_ReturnedReaderReceivesAllAndCompletes()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1, 2, 3));
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground();

        var results = await ReadResultsAsync(reader).WaitAsync(TimeSpan.FromSeconds(5));

        results.Where(static r => r.IsSuccess).Select(static r => r.Value).Should().Equal(1, 2, 3);
        reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task RunInBackground_WithUserSink_ReturnedReaderAndSinkReceiveSameSuccessOutputs()
    {
        var sink = new ResultCollectingSink<int>();
        var pipe = new SmartPipeChannel<int, int>(
            new SmartPipeChannelOptions { MaxDegreeOfParallelism = 1 });
        pipe.AddSource(new SimpleSource<int>(1, 2, 3));
        pipe.AddTransformer(new PassthroughTransformer<int>());
        pipe.AddSink(sink);

        var reader = pipe.RunInBackground();

        var results = await ReadResultsAsync(reader).WaitAsync(TimeSpan.FromSeconds(5));

        results.Select(static r => r.Value).Should().Equal(1, 2, 3);
        sink.Results.Select(static r => r.Value).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task RunInBackground_WithUserSink_ReturnedReaderAndSinkReceiveSameFailureOutputs()
    {
        var sink = new ResultCollectingSink<int>();
        var pipe = new SmartPipeChannel<int, int>(new SmartPipeChannelOptions { ContinueOnError = true });
        pipe.AddSource(new SimpleSource<int>(1));
        pipe.AddTransformer(new FailingTransformer<int>("expected failure"));
        pipe.AddSink(sink);

        var reader = pipe.RunInBackground();

        var results = await ReadResultsAsync(reader).WaitAsync(TimeSpan.FromSeconds(5));

        results.Should().ContainSingle();
        results[0].IsSuccess.Should().BeFalse();
        results[0].Error?.Message.Should().Be("expected failure");
        sink.Results.Should().ContainSingle();
        sink.Results[0].IsSuccess.Should().BeFalse();
        sink.Results[0].Error?.Message.Should().Be("expected failure");
    }

    [Fact]
    public async Task RunInBackground_UserSinkThrows_ReturnedReaderReceivesOutputThenCompletionFaults()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(42));
        pipe.AddTransformer(new PassthroughTransformer<int>());
        pipe.AddSink(new ThrowingSink<int>());

        var reader = pipe.RunInBackground();

        var first = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        first.Value.Should().Be(42);
        await reader.Completion.Invoking(static t => t.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*sink failed*");
    }

    [Fact]
    public void RunInBackground_CalledTwice_ShouldThrowWithoutAddingExtraExternalChannels()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1));
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var first = pipe.RunInBackground();
        var act = () => pipe.RunInBackground();

        act.Should().Throw<InvalidOperationException>().WithMessage("*already started*");
        pipe.AsChannelReader().Should().BeSameAs(first);
        pipe.Cancel();
    }

    [Fact]
    public async Task RunInBackground_Cancel_ShouldCompleteReturnedReaderAsCanceled()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new NeverYieldingSource());
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground();

        pipe.Cancel();

        await reader.Completion.Invoking(static t => t.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunInBackground_Cancel_WhenReturnedReaderNotConsumed_ShouldUnblockAndCompleteReader()
    {
        var pipe = new SmartPipeChannel<int, int>(
            new SmartPipeChannelOptions { BoundedCapacity = 1, MaxDegreeOfParallelism = 1 });
        var source = new SignaledInfiniteSource();
        pipe.AddSource(source);
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground();
        await source.FirstItemProduced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (await reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        pipe.Cancel();

        var (_, completion) = await ReadResultsCapturingCompletionAsync(reader)
            .WaitAsync(TimeSpan.FromSeconds(5));

        completion.Should().BeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task DisposeAsync_RunInBackgroundReturnedReaderNotConsumed_ShouldCompleteBounded()
    {
        var pipe = new SmartPipeChannel<int, int>(
            new SmartPipeChannelOptions { BoundedCapacity = 1, MaxDegreeOfParallelism = 1 });
        var source = new SignaledInfiniteSource();
        pipe.AddSource(source);
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground();
        await source.FirstItemProduced.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await pipe.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(7));

        var (_, completion) = await ReadResultsCapturingCompletionAsync(reader)
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (completion is not null)
            completion.Should().BeOfType<OperationCanceledException>(
                "Dispose may cancel a still-running background run or complete after graceful drain");
    }

    [Fact]
    public async Task RunInBackground_Cancel_ShouldPreserveAlreadyBufferedExternalResultsBeforeCompletion()
    {
        var pipe = new SmartPipeChannel<int, int>(
            new SmartPipeChannelOptions { BoundedCapacity = 4, MaxDegreeOfParallelism = 1 });
        var source = new SignaledInfiniteSource();
        pipe.AddSource(source);
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground();
        await source.FirstItemProduced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (await reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        pipe.Cancel();

        var (results, completion) = await ReadResultsCapturingCompletionAsync(reader)
            .WaitAsync(TimeSpan.FromSeconds(5));

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(static result => result.IsSuccess);
        completion.Should().BeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task RunInBackground_TotalRequestTimeout_ShouldFaultReturnedReaderCompletion()
    {
        var pipe = new SmartPipeChannel<int, int>(
            new SmartPipeChannelOptions
            {
                TotalRequestTimeout = TimeSpan.FromMilliseconds(100),
                MaxDegreeOfParallelism = 1,
            });
        pipe.AddSource(new NeverYieldingSource());
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground();

        await reader.Completion.Invoking(static t => t.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .ThrowAsync<TimeoutException>()
            .WithMessage("*total request timeout*");
    }

    [Fact]
    public async Task RunInBackground_AfterDispose_ShouldThrowObjectDisposedException()
    {
        var pipe = new SmartPipeChannel<int, int>();
        await pipe.DisposeAsync();

        var act = () => pipe.RunInBackground();

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task RunAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        var pipe = new SmartPipeChannel<int, int>();
        await pipe.DisposeAsync();

        await pipe.Invoking(static p => p.RunAsync())
            .Should()
            .ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void RunInBackground_WithoutSource_ShouldThrowSynchronously()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var act = () => pipe.RunInBackground();

        act.Should().Throw<InvalidOperationException>().WithMessage("*source*");
    }

    [Fact]
    public void RunInBackground_WithoutTransformer_ShouldThrowSynchronously()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1));

        var act = () => pipe.RunInBackground();

        act.Should().Throw<InvalidOperationException>().WithMessage("*transformer*");
    }

    [Fact]
    public async Task RunInBackground_AlreadyCanceledToken_ShouldReturnCanceledReaderCompletion()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1));
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground(cts.Token);

        await reader.Completion.Invoking(static t => t.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AsChannelReader_BeforeRunInBackground_ShouldReturnNull()
    {
        var pipe = new SmartPipeChannel<int, int>();

        pipe.AsChannelReader().Should().BeNull();

        await pipe.DisposeAsync();
    }

    [Fact]
    public async Task AsChannelReader_AfterRunInBackground_ShouldReturnReturnedReader()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1));
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground();

        pipe.AsChannelReader().Should().BeSameAs(reader);
        _ = await ReadResultsAsync(reader).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AsChannelReader_AfterRunInBackground_ShouldReturnSameReaderInstance()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1));
        pipe.AddTransformer(new PassthroughTransformer<int>());

        var reader = pipe.RunInBackground();

        pipe.AsChannelReader().Should().BeSameAs(reader);
        pipe.AsChannelReader().Should().BeSameAs(reader);
        _ = await ReadResultsAsync(reader).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AsChannelReader_AfterRunAsync_ShouldReturnNull()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1));
        pipe.AddTransformer(new PassthroughTransformer<int>());
        pipe.AddSink(new NoOpSink<int>());

        await pipe.RunAsync();

        pipe.AsChannelReader().Should().BeNull();
    }

    private static async Task<List<ProcessingResult<T>>> ReadResultsAsync<T>(
        ChannelReader<ProcessingResult<T>> reader)
    {
        var results = new List<ProcessingResult<T>>();
        await foreach (var result in reader.ReadAllAsync())
            results.Add(result);
        return results;
    }

    private static async Task<(
        List<ProcessingResult<T>> Results,
        Exception? Completion
    )> ReadResultsCapturingCompletionAsync<T>(ChannelReader<ProcessingResult<T>> reader)
    {
        var results = new List<ProcessingResult<T>>();
        try
        {
            await foreach (var result in reader.ReadAllAsync())
                results.Add(result);
            return (results, null);
        }
        catch (Exception ex)
        {
            return (results, ex);
        }
    }

    private sealed class ResultCollectingSink<T> : ISink<T>
    {
        private readonly List<ProcessingResult<T>> _results = [];

        public IReadOnlyList<ProcessingResult<T>> Results => _results;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
        {
            lock (_results)
                _results.Add(result);
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class FailingTransformer<T>(string message) : ITransformer<T, T>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<ProcessingResult<T>> TransformAsync(
            ProcessingContext<T> ctx,
            CancellationToken ct = default) =>
            ValueTask.FromResult(
                ProcessingResult<T>.Failure(
                    new SmartPipeError(message, ErrorType.Permanent, "Test"),
                    ctx.TraceId));

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class ThrowingSink<T> : ISink<T>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default) =>
            throw new InvalidOperationException("sink failed");

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class SignaledInfiniteSource : ISource<int>
    {
        private int _count;

        public TaskCompletionSource FirstItemProduced { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ProcessingContext<int>> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(10, ct).ConfigureAwait(false);
                var value = Interlocked.Increment(ref _count);
                FirstItemProduced.TrySetResult();
                yield return new ProcessingContext<int>(value);
            }
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class NeverYieldingSource : ISource<int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ProcessingContext<int>> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            yield break;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }
}

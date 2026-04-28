using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class DeadLetterRoutingTests
{
    [Fact]
    public async Task DeadLetterSink_ShouldReceivePermanentErrors()
    {
        var deadLetter = new TestDeadLetterSink();
        var options = new SmartPipeChannelOptions { ContinueOnError = true, DeadLetterSink = deadLetter };

        // Permanent error → goes directly to DeadLetterSink via HandleFailureAsync
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PermanentFailTransformer<int>();
        var sink = new CollectionSink<int>();
        var pipe = new SmartPipeChannel<int, int>(options);
        pipe.AddSource(source);
        pipe.AddTransformer(transformer);
        pipe.AddSink(sink);
        await pipe.RunAsync();

        deadLetter.Received.Should().Be(3); // All 3 items failed with Permanent
    }
}

internal class PermanentFailTransformer<T> : ITransformer<T, T>
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
        => ValueTask.FromResult(ProcessingResult<T>.Failure(
            new SmartPipeError("Permanent fail", ErrorType.Permanent), ctx.TraceId));
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class TestDeadLetterSink : ISink<object>
{
    public int Received;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteAsync(ProcessingResult<object> result, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Received);
        return Task.CompletedTask;
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

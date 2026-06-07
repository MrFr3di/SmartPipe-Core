using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class DeadLetterRoutingTests
{
    [Fact]
    public async Task DeadLetterSink_ShouldReceivePermanentErrors()
    {
        var deadLetter = new TestDeadLetterSink();
        var options = new SmartPipeChannelOptions
        {
            ContinueOnError = true,
            DeadLetterSink = deadLetter,
        };

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

    [Fact]
    public async Task TransientFailure_WithRetryDisabled_ShouldEmitFailureAndDeadLetterEnvelope()
    {
        var deadLetter = new CapturingDeadLetterSink();
        var sink = new ResultCollectingSink<int>();
        var options = new SmartPipeChannelOptions
        {
            ContinueOnError = true,
            DeadLetterSink = deadLetter,
        };
        var pipe = new SmartPipeChannel<int, int>(options);
        pipe.AddSource(new SimpleSource<int>(42));
        pipe.AddTransformer(new AlwaysTransientFailingTransformer<int, int>());
        pipe.AddSink(sink);

        await pipe.RunAsync();

        var output = sink.Results.Should().ContainSingle().Subject;
        output.IsSuccess.Should().BeFalse();
        output.Error!.Value.Type.Should().Be(ErrorType.Transient);

        var deadLetterResult = deadLetter.Results.Should().ContainSingle().Subject;
        deadLetterResult.IsSuccess.Should().BeTrue();
        var envelope = deadLetterResult.Value.Should().BeOfType<DeadLetterEnvelope<int>>().Subject;
        envelope.OriginalPayload.Should().Be(42);
        envelope.TraceId.Should().Be(output.TraceId);
        envelope.Error.Type.Should().Be(ErrorType.Transient);
        envelope.Attempt.Should().Be(0);
        envelope.FailedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_TransientFailureRetryDisabled_ShouldCompleteQuickly()
    {
        var deadLetter = new CapturingDeadLetterSink();
        var sink = new ResultCollectingSink<int>();
        var options = new SmartPipeChannelOptions
        {
            ContinueOnError = true,
            DeadLetterSink = deadLetter,
        };
        var pipe = new SmartPipeChannel<int, int>(options);
        pipe.AddSource(new SimpleSource<int>(42));
        pipe.AddTransformer(new AlwaysTransientFailingTransformer<int, int>());
        pipe.AddSink(sink);

        await pipe.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        sink.Results.Should().ContainSingle();
        deadLetter.Results.Should().ContainSingle();
        pipe.State.Should().Be(PipelineState.Completed);
    }

    [Fact]
    public async Task TransientFailure_WhenRetryBudgetExhausted_ShouldDeadLetterOnce()
    {
        var deadLetter = new CapturingDeadLetterSink();
        var sink = new ResultCollectingSink<int>();
        var options = new SmartPipeChannelOptions
        {
            ContinueOnError = true,
            DeadLetterSink = deadLetter,
            DefaultRetryPolicy = new RetryPolicy(1, TimeSpan.Zero),
        };
        options.EnableFeature("RetryQueue");
        var pipe = new SmartPipeChannel<int, int>(options);
        pipe.AddSource(new SimpleSource<int>(42));
        pipe.AddTransformer(new AlwaysTransientFailingTransformer<int, int>());
        pipe.AddSink(sink);

        await pipe.RunAsync();

        sink.Results.Should().ContainSingle();
        var deadLetterResult = deadLetter.Results.Should().ContainSingle().Subject;
        var envelope = deadLetterResult.Value.Should().BeOfType<DeadLetterEnvelope<int>>().Subject;
        envelope.OriginalPayload.Should().Be(42);
        envelope.Attempt.Should().Be(1);
        envelope.Error.Type.Should().Be(ErrorType.Transient);
    }
}

internal class PermanentFailTransformer<T> : ITransformer<T, T>
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<ProcessingResult<T>> TransformAsync(
        ProcessingContext<T> ctx,
        CancellationToken ct = default
    ) =>
        ValueTask.FromResult(
            ProcessingResult<T>.Failure(
                new SmartPipeError("Permanent fail", ErrorType.Permanent),
                ctx.TraceId
            )
        );

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

internal class CapturingDeadLetterSink : ISink<object>
{
    private readonly List<ProcessingResult<object>> _results = [];

    public IReadOnlyList<ProcessingResult<object>> Results => _results;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task WriteAsync(ProcessingResult<object> result, CancellationToken ct = default)
    {
        lock (_results)
            _results.Add(result);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

internal class ResultCollectingSink<T> : ISink<T>
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

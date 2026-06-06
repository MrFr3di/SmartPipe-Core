#nullable enable

using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

/// <summary>
/// P2A: Tests for <see cref="PipelineOutputMode"/> applied at the output channel write boundary.
/// Sink writes and observer events remain independent of output mode filtering.
/// </summary>
public sealed class TypedPipelineOutputModeTests
{
    [Fact]
    public async Task EmitAll_ShouldEmitBothSuccessfulAndFailedOutputs()
    {
        var source = new EnvelopeSource<int>(1, 2, 3);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => x switch
                {
                    1 => StageResult<string>.Success("a"),
                    2 => StageResult<string>.Success("b"),
                    3 => StageResult<string>.Failure(new SmartPipeError("fail", ErrorType.Permanent, "Test")),
                    _ => throw new InvalidOperationException(),
                }
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.EmitAll,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(3);
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be("a");
        outputs[1].Result.IsSuccess.Should().BeTrue();
        outputs[1].Result.Value.Should().Be("b");
        outputs[2].Result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task EmitAll_WithSink_ShouldEmitOutputsAndWriteSink()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => StageResult<string>.Success(x == 1 ? "ok" : "also-ok")
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.EmitAll,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(2);
        sink.Payloads.Should().Equal("ok", "also-ok");
    }

    [Fact]
    public async Task SuppressWhenSinkAttached_WithoutSink_ShouldEmitSuccessOutputs()
    {
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.SuppressWhenSinkAttached,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(2);
        outputs[0].Result.Value.Should().Be("1");
        outputs[1].Result.Value.Should().Be("2");
    }

    [Fact]
    public async Task SuppressWhenSinkAttached_WithSink_ShouldSuppressSuccessOutputsButWriteSink()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => StageResult<string>.Success(x == 1 ? "a" : "b")
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.SuppressWhenSinkAttached,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // Success outputs are suppressed when sink is attached.
        outputs.Should().BeEmpty();
        // Sink writes are independent of output mode.
        sink.Payloads.Should().Equal("a", "b");
    }

    [Fact]
    public async Task SuppressWhenSinkAttached_WithSink_ShouldSuppressFailureOutputs()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => x == 1
                    ? StageResult<string>.Success("a")
                    : StageResult<string>.Failure(new SmartPipeError("fail", ErrorType.Permanent, "Test"))
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.SuppressWhenSinkAttached,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().BeEmpty();
        sink.Payloads.Should().Equal("a");
    }

    [Fact]
    public async Task FailuresOnlyWhenSinkAttached_WithoutSink_ShouldEmitBoth()
    {
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => x switch
                {
                    1 => StageResult<string>.Success("ok"),
                    2 => StageResult<string>.Failure(new SmartPipeError("fail", ErrorType.Permanent, "Test")),
                    _ => throw new InvalidOperationException(),
                }
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.FailuresOnlyWhenSinkAttached,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // Without sink, FailuresOnlyWhenSinkAttached emits all outputs (sink is null, so no suppression).
        outputs.Should().HaveCount(2);
    }

    [Fact]
    public async Task FailuresOnlyWhenSinkAttached_WithSink_ShouldEmitOnlyFailures()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2, 3);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => x switch
                {
                    1 => StageResult<string>.Success("ok"),
                    2 => StageResult<string>.Failure(new SmartPipeError("e1", ErrorType.Permanent, "T1")),
                    3 => StageResult<string>.Success("skip-me"),
                    _ => throw new InvalidOperationException(),
                }
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.FailuresOnlyWhenSinkAttached,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // Only failure output is emitted.
        outputs.Should().HaveCount(1);
        outputs[0].Result.IsSuccess.Should().BeFalse();
        outputs[0].Result.Error!.Value.Category.Should().Be("T1");
        // Sink writes both successful items.
        sink.Payloads.Should().Equal("ok", "skip-me");
    }

    [Fact]
    public async Task SuppressAll_ShouldEmitNoOutputsButStillWriteSink()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => StageResult<string>.Success(x.ToString())
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.SuppressAll,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().BeEmpty();
        sink.Payloads.Should().Equal("1", "2");
    }

    [Fact]
    public void UndefinedOutputMode_ShouldBeRejectedByValidation()
    {
        var options = new PipelineRuntimeOptions
        {
#pragma warning disable SMA0010 // Intentional test of undefined enum
            OutputMode = (PipelineOutputMode)99,
#pragma warning restore SMA0010
        };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("OutputMode");
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);
        return outputs;
    }
}

internal sealed class ConditionalTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly Func<TInput, StageResult<TOutput>> _transform;

    public ConditionalTransformer(Func<TInput, StageResult<TOutput>> transform)
    {
        _transform = transform;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult(_transform(envelope.Payload));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class EnvelopeSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T>[] _items;

    public EnvelopeSource(params T[] payloads)
    {
        _items = payloads
            .Select(payload =>
                ProcessingEnvelope<T>.Create(
                    payload,
                    "source-pipeline",
                    "source-run",
                    (ulong)Random.Shared.Next(1, int.MaxValue)
                )
            )
            .ToArray();
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class EnvelopeTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly Func<TInput, TOutput> _transform;

    public EnvelopeTransformer(Func<TInput, TOutput> transform)
    {
        _transform = transform;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult(StageResult<TOutput>.Success(_transform(envelope.Payload)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class EnvelopeCollectingSink<T> : IPipelineSink<T>
{
    private readonly List<T> _payloads = [];

    public IReadOnlyList<T> Payloads => _payloads;

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        _payloads.Add(envelope.Payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

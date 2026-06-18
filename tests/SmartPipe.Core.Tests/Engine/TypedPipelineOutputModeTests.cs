#nullable enable
#pragma warning disable CS0618 // These tests cover compatibility aliases.

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
    public async Task TypedPipeline_OutputPolicyEmitAll_EmitsSuccessOutputs()
    {
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().Equal("1", "2");
    }

    [Fact]
    public async Task TypedPipeline_OutputPolicyEmitFailuresOnly_SuppressesSuccess()
    {
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => x == 1
                    ? StageResult<string>.Success("ok")
                    : StageResult<string>.Failure(new SmartPipeError("fail", ErrorType.Permanent, "Test"))
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitFailuresOnly,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        outputs[0].Result.Error!.Value.Category.Should().Be("Test");
    }

    [Fact]
    public async Task TypedPipeline_OutputPolicySuppressSuccessWhenSinkAttached_DoesNotBlockSink()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2, 3, 4);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputCapacity = 1,
                OutputFullMode = BoundedChannelFullMode.Wait,
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .To(sink);

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var outputs = await ReadOutputsAsync(run.Outputs);

        outputs.Should().BeEmpty();
        sink.Payloads.Should().Equal("1", "2", "3", "4");
    }

    [Fact]
    public async Task ToSink_DoesNotEmitSuccessBeforeSinkWriteSucceeds()
    {
        var sink = new BlockingSink<string>();
        var source = new EnvelopeSource<int>(1);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .To(sink);

        await sink.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        run.Outputs.TryRead(out _).Should().BeFalse(
            "sink-backed success output must not be visible until the sink write succeeds");

        sink.Release();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be("1");
    }

    [Fact]
    public async Task ToSink_SinkFailure_DoesNotEmitSuccess()
    {
        var source = new EnvelopeSource<int>(1);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .To(new ThrowingSink<string>());

        var completion = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await completion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("sink boom");
        run.Outputs.TryRead(out var output).Should().BeFalse(
            "a failed sink write must not leave a pre-published success output behind");
    }

    [Fact]
    public async Task ToSink_SinkSuccess_EmitsSuccessAfterSinkWrite()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        sink.Payloads.Should().Equal("1");
        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be("1");
    }

    [Fact]
    public async Task WithoutSink_TransformSuccess_EmitsSuccess()
    {
        var source = new EnvelopeSource<int>(1);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be("1");
    }

    [Fact]
    public async Task Filtered_DoesNotWriteSink()
    {
        var sink = new EnvelopeCollectingSink<string>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new ConditionalTransformer<int, string>(_ => StageResult<string>.Filtered()))
            .To(sink);

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        sink.Payloads.Should().BeEmpty();
    }

    [Fact]
    public async Task Filtered_DoesNotIncrementFailedMetric()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new ConditionalTransformer<int, string>(_ => StageResult<string>.Filtered()))
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        run.Metrics.ItemsFailed.Should().Be(0);
        run.Metrics.ItemsFiltered.Should().Be(1);
    }

    [Fact]
    public async Task Filtered_DoesNotWriteDeadLetter()
    {
        await using var stream = new MemoryStream();
        var serializer = new JsonLinesDeadLetterSerializer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new ConditionalTransformer<int, string>(_ => StageResult<string>.Filtered()),
                new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
                new StageDeadLetterOptions<int>(stream, serializer))
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        stream.Length.Should().Be(0);
        run.Metrics.ItemsDeadLettered.Should().Be(0);
    }

    [Fact]
    public async Task Filtered_EmitsItemFilteredEvent()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new ConditionalTransformer<int, string>(_ => StageResult<string>.Filtered()))
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<ItemFilteredEvent>().Should().ContainSingle();
        observer.Events.OfType<StageFailedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Filtered_OutputPolicyCanEmitFilteredTerminalOutput()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new ConditionalTransformer<int, string>(_ => StageResult<string>.Filtered()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.Kind.Should().Be(PipelineResultKind.Filtered);
        outputs[0].Result.IsFailure.Should().BeFalse();
    }

    [Fact]
    public async Task Filtered_OutputPolicyCanSuppressFilteredTerminalOutput()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new ConditionalTransformer<int, string>(_ => StageResult<string>.Filtered()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitFailuresOnly,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task OutputPolicySuppressSuccessWhenSinkAttached_WithSink_ShouldStillEmitFailures()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2, 3);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => x switch
                {
                    1 => StageResult<string>.Success("ok"),
                    2 => StageResult<string>.Failure(new SmartPipeError("fail", ErrorType.Permanent, "Test")),
                    3 => StageResult<string>.Success("also-ok"),
                    _ => throw new InvalidOperationException(),
                }
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        sink.Payloads.Should().Equal("ok", "also-ok");
    }

    [Fact]
    public async Task TypedPipeline_OutputPolicySuppressAllWhenSinkAttached_DoesNotEmitOutputs()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2, 3);

        var run = PipelineBuilder
            .From(source)
            .Transform(new ConditionalTransformer<int, string>(
                x => x == 2
                    ? StageResult<string>.Failure(new SmartPipeError("fail", ErrorType.Permanent, "Test"))
                    : StageResult<string>.Success(x.ToString())
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().BeEmpty();
        sink.Payloads.Should().Equal("1", "3");
    }

    [Fact]
    public async Task TypedPipeline_OutputPolicyEmitAll_BoundedOutputBlocksWhenReaderSlow()
    {
        var source = new EnvelopeSource<int>(1, 2, 3);
        var sink = new SignalingSink<string>(expectedWrites: 1);
        var transformer = new SignalingTransformer<int, string>(
            x => x.ToString(),
            signalOnPayload: 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputCapacity = 1,
                OutputFullMode = BoundedChannelFullMode.Wait,
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .To(sink);

        await sink.WaitForCountAsync(2, TimeSpan.FromSeconds(5));
        await transformer.SignalReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Func<Task> thirdSinkWrite = async () =>
            await sink.WaitForCountAsync(3, TimeSpan.FromMilliseconds(150));
        await thirdSinkWrite.Should().ThrowAsync<TimeoutException>(
            "the third item should wait because the second success output is blocked");

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().Equal("1", "2", "3");
        sink.Payloads.Should().Equal("1", "2", "3");
    }

    [Fact]
    public async Task TypedPipeline_OutputPolicyEmitAll_DefaultOutputBlocksWhenReaderSlow()
    {
        const int defaultCapacity = 1024;
        var source = new EnvelopeSource<int>(Enumerable.Range(1, defaultCapacity + 1).ToArray());
        var transformer = new SignalingTransformer<int, string>(
            x => x.ToString(),
            signalOnPayload: defaultCapacity + 1);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .Run();

        await transformer.SignalReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Func<Task> completion = async () =>
            await run.Completion.WaitAsync(TimeSpan.FromMilliseconds(150));
        await completion.Should().ThrowAsync<TimeoutException>(
            "the default output-only run should use bounded output backpressure");

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value)
            .Should()
            .Equal(Enumerable.Range(1, defaultCapacity + 1).Select(x => x.ToString()));
    }

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
                OutputPolicy = PipelineOutputPolicy.EmitAll,
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
                OutputPolicy = PipelineOutputPolicy.EmitAll,
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
                OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
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
                OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
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
                OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
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
    public async Task OutputModeEmitAll_WithSink_ShouldEmitSuccessWhenPolicyIsNotSet()
    {
        var sink = new EnvelopeCollectingSink<string>();
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.EmitAll,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(output => output.Result.Value).Should().Equal("1", "2");
        sink.Payloads.Should().Equal("1", "2");
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
    public async Task OutputModeSuppressAll_WithoutSink_ShouldSuppressSuccessWhenPolicyIsNotSet()
    {
        var source = new EnvelopeSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.SuppressAll,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().BeEmpty();
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

    [Fact]
    public void OutputModeAndOutputPolicyConflict_ShouldBeRejectedByValidation()
    {
        var options = new PipelineRuntimeOptions
        {
            OutputMode = PipelineOutputMode.SuppressAll,
            OutputPolicy = PipelineOutputPolicy.EmitAll,
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OutputMode*OutputPolicy*");
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

internal sealed class SignalingTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly Func<TInput, TOutput> _transform;
    private readonly TInput _signalOnPayload;

    public SignalingTransformer(Func<TInput, TOutput> transform, TInput signalOnPayload)
    {
        _transform = transform;
        _signalOnPayload = signalOnPayload;
    }

    public TaskCompletionSource SignalReached { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        if (EqualityComparer<TInput>.Default.Equals(envelope.Payload, _signalOnPayload))
            SignalReached.TrySetResult();

        return ValueTask.FromResult(StageResult<TOutput>.Success(_transform(envelope.Payload)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SignalingSink<T> : IPipelineSink<T>
{
    private readonly int _expectedWrites;
    private readonly List<T> _payloads = [];
    private readonly object _gate = new();
    private TaskCompletionSource _countChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writeCount;

    public SignalingSink(int expectedWrites)
    {
        _expectedWrites = expectedWrites;
    }

    public IReadOnlyList<T> Payloads => _payloads;

    public TaskCompletionSource ExpectedWritesReached { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _payloads.Add(envelope.Payload);
            if (Interlocked.Increment(ref _writeCount) >= _expectedWrites)
                ExpectedWritesReached.TrySetResult();

            _countChanged.TrySetResult();
            _countChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return ValueTask.CompletedTask;
    }

    public async Task WaitForCountAsync(int expectedCount, TimeSpan timeout)
    {
        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                if (_payloads.Count >= expectedCount)
                    return;

                waitTask = _countChanged.Task;
            }

            await waitTask.WaitAsync(timeout).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class BlockingSink<T> : IPipelineSink<T>
{
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource WriteStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        WriteStarted.TrySetResult();
        await _release.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Release() => _release.TrySetResult();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ThrowingSink<T> : IPipelineSink<T>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default) =>
        ValueTask.FromException(new InvalidOperationException("sink boom"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingObserver : IPipelineObserver
{
    private readonly List<PipelineEvent> _events = [];
    private readonly object _gate = new();

    public IReadOnlyList<PipelineEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToArray();
        }
    }

    public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
    {
        lock (_gate)
            _events.Add(pipelineEvent);

        return ValueTask.CompletedTask;
    }
}

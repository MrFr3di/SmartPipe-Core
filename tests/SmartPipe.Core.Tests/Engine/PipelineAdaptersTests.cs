#nullable enable

using System.Collections;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class PipelineAdaptersTests
{
    [Fact]
    public async Task Adapters_FromAsyncEnumerable_Works()
    {
        var run = PipelineBuilder
            .From(PipelineSource.FromAsyncEnumerable(ToAsyncEnumerable([1, 2, 3])))
            .Transform(PipelineTransformer.FromFunc<int, string>(
                static (value, ct) => ValueTask.FromResult(value.ToString())))
            .Run();

        var results = new List<string>();
        await foreach (var output in run.Outputs.ReadAllAsync())
            results.Add(output.Result.Value!);
        await run.Completion;

        results.Should().Equal("1", "2", "3");
    }

    [Fact]
    public async Task Adapters_TransformerFromFunc_Works()
    {
        var transformer = PipelineTransformer.FromFunc<int, string>(
            static (value, ct) => ValueTask.FromResult($"value:{value}"));

        var result = await transformer.TransformAsync(
            ProcessingEnvelope<int>.Create(42, "adapters", "run", 1));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("value:42");
    }

    [Fact]
    public async Task Adapters_SinkFromFunc_Works()
    {
        var observed = new List<string>();
        var run = PipelineBuilder
            .From(PipelineSource.FromAsyncEnumerable(ToAsyncEnumerable([1, 2])))
            .Transform(PipelineTransformer.FromFunc<int, string>(
                static (value, ct) => ValueTask.FromResult($"value:{value}")))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .To(PipelineSink.FromFunc<string>((value, ct) =>
            {
                observed.Add(value);
                return ValueTask.CompletedTask;
            }));

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        observed.Should().Equal("value:1", "value:2");
    }

    [Fact]
    public async Task Adapters_CancellationTokenIsPassed()
    {
        using var cts = new CancellationTokenSource();
        var sourceEnumerable = new CapturingAsyncEnumerable<int>([7]);
        var source = PipelineSource.FromAsyncEnumerable(sourceEnumerable);
        var transformerToken = default(CancellationToken);
        var sinkToken = default(CancellationToken);
        var transformer = PipelineTransformer.FromFunc<int, int>((value, ct) =>
        {
            transformerToken = ct;
            return ValueTask.FromResult(value);
        });
        var sink = PipelineSink.FromFunc<int>((value, ct) =>
        {
            sinkToken = ct;
            return ValueTask.CompletedTask;
        });

        await foreach (var _ in source.ReadEnvelopesAsync(cts.Token))
            break;
        await transformer.TransformAsync(
            ProcessingEnvelope<int>.Create(7, "adapters", "run", 1),
            cts.Token);
        await sink.WriteAsync(
            ProcessingEnvelope<int>.Create(7, "adapters", "run", 1),
            cts.Token);

        sourceEnumerable.ObservedToken.Should().Be(cts.Token);
        transformerToken.Should().Be(cts.Token);
        sinkToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task Migration_SimpleLegacyScenario_CanBeExpressedWithTypedAdapters()
    {
        var sinkValues = new List<string>();

        var run = PipelineBuilder
            .From(PipelineSource.FromAsyncEnumerable(ToAsyncEnumerable([1, 2, 3])))
            .Transform(PipelineTransformer.FromFunc<int, string>(
                static (value, ct) => ValueTask.FromResult((value * 2).ToString())))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .To(PipelineSink.FromFunc<string>((value, ct) =>
            {
                sinkValues.Add(value);
                return ValueTask.CompletedTask;
            }));

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        sinkValues.Should().Equal("2", "4", "6");
    }

    [Fact]
    public async Task Migration_SourceTransformSink_CanBeExpressedWithTypedBuilder()
    {
        var source = PipelineSource.FromAsyncEnumerable(ToAsyncEnumerable(["a", "bb"]));
        var transformer = PipelineTransformer.FromFunc<string, int>(
            static (value, ct) => ValueTask.FromResult(value.Length));
        var sinkValues = new List<int>();
        var sink = PipelineSink.FromFunc<int>((value, ct) =>
        {
            sinkValues.Add(value);
            return ValueTask.CompletedTask;
        });

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .To(sink);

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        sinkValues.Should().Equal(1, 2);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        IEnumerable<T> values,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    private sealed class CapturingAsyncEnumerable<T> : IAsyncEnumerable<T>
    {
        private readonly IReadOnlyList<T> _values;

        public CapturingAsyncEnumerable(IReadOnlyList<T> values)
        {
            _values = values;
        }

        public CancellationToken ObservedToken { get; private set; }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            return new Enumerator(_values);
        }

        private sealed class Enumerator : IAsyncEnumerator<T>
        {
            private readonly IEnumerator<T> _inner;

            public Enumerator(IEnumerable<T> values)
            {
                _inner = values.GetEnumerator();
            }

            public T Current => _inner.Current;

            public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(_inner.MoveNext());

            public ValueTask DisposeAsync()
            {
                _inner.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

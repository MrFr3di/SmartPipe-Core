#nullable enable

using System.Runtime.CompilerServices;
using SmartPipe.Core;

namespace SmartPipe.Extensions.OpenTelemetry.Tests;

internal static class SdkPipelineFixtures
{
    internal static PipelineRun<int> StartPipeline(int itemCount, CancellationToken ct = default) =>
        PipelineBuilder
            .FromFactory<int>(_ => new ItemSource(itemCount))
            .TransformFactory<int>(_ => new IdentityTransformer())
            .ToFactory(_ => new CountingSink(), ct);

    internal static PipelineRun<int> StartFailingPipeline(CancellationToken ct = default) =>
        PipelineBuilder
            .FromFactory<int>(_ => new ItemSource(1))
            .TransformFactory<int>(_ => new ThrowingTransformer())
            .ToFactory(_ => new CountingSink(), ct);

    internal static PipelineRun<int> StartInfinitePipeline(CancellationToken ct = default) =>
        PipelineBuilder
            .FromFactory<int>(_ => new InfiniteSource())
            .TransformFactory<int>(_ => new IdentityTransformer())
            .ToFactory(_ => new CountingSink(), ct);
}

internal sealed class ItemSource(int itemCount) : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var index = 0; index < itemCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            yield return ProcessingEnvelope<int>.Create(index, "otel-sdk", "run", 1);
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InfiniteSource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var index = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            yield return ProcessingEnvelope<int>.Create(index++, "otel-sdk", "run", 1);
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class IdentityTransformer : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ThrowingTransformer : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) =>
        throw new InvalidOperationException("sdk test stage failure");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class CountingSink : IPipelineSink<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

#nullable enable

using Mapster;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Mapster.Tests;

internal static class MapsterHarness
{
    internal static readonly PipelineStageKey DefaultStageKey = new("map");

    internal static PipelineDefinition<TInput, TOutput> Build<TInput, TOutput>(
        PipelineComponent<IPipelineTransformer<TInput, TOutput>> transform,
        IReadOnlyList<TInput> inputs,
        PipelineStageKey stageKey,
        Action? beforeFirstItem = null)
    {
        var source = PipelineComponent.RuntimeOwned<IPipelineSource<TInput>>(
            (context, cancellationToken) => ValueTask.FromResult(
                PipelineSource.FromAsyncEnumerable(Emit(inputs, beforeFirstItem), "mapster-tests", context.RunId.ToString())));

        return PipelineDefinitionBuilder
            .From(new PipelineKey("mapster-tests"), source)
            .Transform(stageKey, transform)
            .Build();
    }

    internal static async Task<List<TOutput>> RunAsync<TInput, TOutput>(
        PipelineComponent<IPipelineTransformer<TInput, TOutput>> transform,
        IReadOnlyList<TInput> inputs,
        PipelineStageKey? stageKey = null)
    {
        var definition = Build(transform, inputs, stageKey ?? DefaultStageKey);
        var results = new List<TOutput>();
        await using var run = await definition.StartAsync();
        await foreach (var output in run.Outputs.ReadAllAsync())
        {
            results.Add(output.Result.Value!);
        }

        await run.Completion;
        return results;
    }

    internal static async Task<List<PipelineOutput<TOutput>>> RunCollectingAsync<TInput, TOutput>(
        PipelineComponent<IPipelineTransformer<TInput, TOutput>> transform,
        IReadOnlyList<TInput> inputs)
    {
        var definition = Build(transform, inputs, DefaultStageKey);
        var outputs = new List<PipelineOutput<TOutput>>();
        await using var run = await definition.StartAsync();
        await foreach (var output in run.Outputs.ReadAllAsync())
        {
            outputs.Add(output);
        }

        await run.Completion;
        return outputs;
    }

    internal static async Task<Exception> RunExpectingFailureAsync<TInput, TOutput>(
        PipelineComponent<IPipelineTransformer<TInput, TOutput>> transform,
        IReadOnlyList<TInput> inputs,
        CancellationToken runToken = default,
        Action? beforeFirstItem = null)
    {
        var definition = Build(transform, inputs, DefaultStageKey, beforeFirstItem);
        try
        {
            await using var run = await definition.StartAsync(runToken);
            await foreach (var _ in run.Outputs.ReadAllAsync())
            {
            }

            await run.Completion;
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The pipeline run was expected to fail but completed successfully.");
    }

    private static async IAsyncEnumerable<T> Emit<T>(IReadOnlyList<T> items, Action? beforeFirstItem)
    {
        var isFirst = true;
        foreach (var item in items)
        {
            if (isFirst)
            {
                isFirst = false;
                beforeFirstItem?.Invoke();
            }

            await Task.Yield();
            yield return item;
        }
    }
}

internal sealed class Source
{
    public int N { get; set; }
}

internal sealed class Destination
{
    public int Value { get; set; }
}

internal sealed class GlobalSource
{
    public int N { get; set; }
}

internal sealed class GlobalDestination
{
    public int Value { get; set; }
}

internal sealed class OrderSource
{
    public string Id { get; set; } = string.Empty;

    public CustomerSource? Customer { get; set; }
}

internal sealed class OrderDestination
{
    public string Id { get; set; } = string.Empty;

    public CustomerDestination? Customer { get; set; }
}

internal sealed class CustomerSource
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class CustomerDestination
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class TaggedSource
{
    public Source Person { get; set; } = new();

    public string Tag { get; set; } = string.Empty;
}

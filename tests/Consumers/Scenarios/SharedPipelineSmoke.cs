using SmartPipe.Core;

internal static class ConsumerPipelineSmoke
{
    internal static async Task<bool> RunAsync(PipelineKey key)
    {
        var definition = PipelineDefinitionBuilder
            .From(key, PipelineComponent.RuntimeOwned<IPipelineSource<int>>(CreateSource))
            .Transform(
                new PipelineStageKey("double"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>(CreateTransformer))
            .Build();

        await using var run = await definition.StartAsync();
        var values = new List<int>();
        await foreach (var output in run.Outputs.ReadAllAsync())
        {
            if (output.Result.IsSuccess)
                values.Add(output.Result.Value);
        }

        await run.Completion;
        return run.PipelineKey == key && values.SequenceEqual([2, 4, 6]);
    }

    private static ValueTask<IPipelineSource<int>> CreateSource(
        PipelineActivationContext _context,
        CancellationToken _cancellationToken) =>
        ValueTask.FromResult<IPipelineSource<int>>(PipelineSource.FromAsyncEnumerable(Values()));

    private static ValueTask<IPipelineTransformer<int, int>> CreateTransformer(
        PipelineActivationContext _context,
        CancellationToken _cancellationToken) =>
        ValueTask.FromResult<IPipelineTransformer<int, int>>(
            PipelineTransformer.FromFunc<int, int>(
                static (value, _) => ValueTask.FromResult(value * 2)));

    private static async IAsyncEnumerable<int> Values()
    {
        yield return 1;
        yield return 2;
        yield return 3;
        await Task.CompletedTask;
    }
}

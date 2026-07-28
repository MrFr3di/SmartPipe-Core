using SmartPipe.Core;

var sink = new List<int>();
var source = PipelineSource.FromAsyncEnumerable(Values());
var run = PipelineBuilder.From(source)
    .Transform(PipelineTransformer.FromFunc<int, int>((value, _) => ValueTask.FromResult(value * 2)))
    .To(PipelineSink.FromFunc<int>((value, _) => { sink.Add(value); return ValueTask.CompletedTask; }));
await run.Completion;
var drain = await run.TryDrainAsync(TimeSpan.FromSeconds(5));
if (!sink.SequenceEqual([2, 4, 6]) || run.Metrics.ItemsProcessed < 3 || drain.Status is PipelineDrainStatus.Faulted) return 1;
await run.DisposeAsync();
Console.WriteLine("CONSUMER_OK core-direct");
return 0;

static async IAsyncEnumerable<int> Values() { yield return 1; yield return 2; yield return 3; await Task.CompletedTask; }

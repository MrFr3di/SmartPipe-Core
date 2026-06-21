using System.Globalization;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests.Fixtures;

public class CsvPipelineFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartpipe-csv-pipeline-" + Guid.NewGuid().ToString("N"));

    public CsvPipelineFixtureTests()
    {
        GeneratedFixtureData.WriteAllTo(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp files created by the test.
        }
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvPipeline_ValidRows_WriteToSink()
    {
        var sink = new CollectingSink<CsvAmountRecord>();

        var run = PipelineBuilder
            .From(CreateCsvSource<CsvAmountRecord>("csv/basic.csv"))
            .Transform(PipelineTransformer.FromFunc<CsvAmountRecord, CsvAmountRecord>(
                static (row, ct) => ValueTask.FromResult(row)))
            .To(sink);

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        sink.Values.Select(row => row.Name).Should().Equal("alpha", "beta");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvPipeline_InvalidRows_UseFailurePolicy()
    {
        var sink = new CollectingSink<CsvAmountRecord>();

        var run = PipelineBuilder
            .From(CreateCsvSource<CsvAmountRecord>("csv/malformed.csv"))
            .Transform(PipelineTransformer.FromFunc<CsvAmountRecord, CsvAmountRecord>(
                static (row, ct) => ValueTask.FromResult(row)))
            .To(sink);

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<Exception>()
            .Where(ex => ex.GetType().FullName != null
                && ex.GetType().FullName!.Contains("CsvHelper", StringComparison.Ordinal));
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvPipeline_FilteredRows_AreNotFailures()
    {
        var run = PipelineBuilder
            .From(CreateCsvSource<CsvAmountRecord>("csv/basic.csv"))
            .Transform(new FilterTransform<CsvAmountRecord>(row => row.Amount > 1))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .Run();

        var results = await ReadResultsAsync(run);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        results.Select(result => result.Kind).Should().BeEquivalentTo(
            [PipelineResultKind.Filtered, PipelineResultKind.Success]);
        run.Metrics.ItemsFiltered.Should().Be(1);
        run.Metrics.ItemsFailed.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvPipeline_OutputBackpressure_DoesNotLoseRows()
    {
        await WriteAmountCsvAsync("csv/many.csv", Enumerable.Range(1, 64));

        var run = PipelineBuilder
            .From(CreateCsvSource<CsvAmountRecord>("csv/many.csv"))
            .Transform(PipelineTransformer.FromFunc<CsvAmountRecord, CsvAmountRecord>(
                static (row, ct) => ValueTask.FromResult(row)))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputCapacity = 1,
                OutputFullMode = BoundedChannelFullMode.Wait,
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .Run();

        var results = new List<PipelineResult<CsvAmountRecord>>();
        await foreach (var result in run.ReadResultsAsync(TestContext.Current.CancellationToken))
        {
            results.Add(result);
            await Task.Yield();
        }

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        results.Should().HaveCount(64);
        results.Select(result => result.Value!.Amount).Should().Equal(Enumerable.Range(1, 64).Select(i => (decimal)i));
        run.Metrics.OutputItemsDropped.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvPipeline_DropMode_RecordsDroppedRows()
    {
        await WriteAmountCsvAsync("csv/drop.csv", Enumerable.Range(1, 20));

        var run = PipelineBuilder
            .From(CreateCsvSource<CsvAmountRecord>("csv/drop.csv"))
            .Transform(PipelineTransformer.FromFunc<CsvAmountRecord, CsvAmountRecord>(
                static (row, ct) => ValueTask.FromResult(row)))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputCapacity = 1,
                OutputFullMode = BoundedChannelFullMode.DropOldest,
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var results = await ReadResultsAsync(run);

        results.Should().ContainSingle();
        run.Metrics.OutputItemsDropped.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvPipeline_DrainMidFile_CompletesAcceptedRows()
    {
        await WriteAmountCsvAsync("csv/drain.csv", Enumerable.Range(1, 20));
        var transformer = new BlockingTransformer<CsvAmountRecord>();
        var sink = new CollectingSink<CsvAmountRecord>();

        var run = PipelineBuilder
            .From(CreateCsvSource<CsvAmountRecord>("csv/drain.csv"))
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                InputCapacity = 1,
                MaxConcurrency = 1,
            })
            .To(sink);

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drainTask = run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        drainTask.IsCompleted.Should().BeFalse();

        transformer.Release();

        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        sink.Values.Should().NotBeEmpty();
        sink.Values.Count.Should().BeLessThan(20);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvPipeline_CancelMidFile_CancelsSourceAndWorkers()
    {
        await WriteAmountCsvAsync("csv/cancel.csv", Enumerable.Range(1, 20));
        var transformer = new BlockingTransformer<CsvAmountRecord>();

        var run = PipelineBuilder
            .From(CreateCsvSource<CsvAmountRecord>("csv/cancel.csv"))
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                InputCapacity = 1,
                MaxConcurrency = 1,
            })
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.CancelAsync();
        transformer.Release();

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Cancelled);
    }

    private CsvFileSource<CsvAmountRecord> CreateCsvSource<TIgnored>(string relativePath)
        where TIgnored : CsvAmountRecord
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return new CsvFileSource<CsvAmountRecord>(path);
    }

    private async Task WriteAmountCsvAsync(string relativePath, IEnumerable<int> amounts)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string> { "Name,Amount" };
        lines.AddRange(amounts.Select(amount => FormattableString.Invariant($"item-{amount},{amount}")));
        await File.WriteAllLinesAsync(path, lines, TestContext.Current.CancellationToken);
    }

    private static async Task<List<PipelineResult<T>>> ReadResultsAsync<T>(PipelineRun<T> run)
    {
        var results = new List<PipelineResult<T>>();
        await foreach (var result in run.ReadResultsAsync(TestContext.Current.CancellationToken))
            results.Add(result);
        return results;
    }

    public class CsvAmountRecord
    {
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
    }

    private sealed class CollectingSink<T> : IPipelineSink<T>
    {
        public List<T> Values { get; } = [];

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
        {
            Values.Add(envelope.Payload);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _shouldBlock = 1;

        public TaskCompletionSource Entered => _entered;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _shouldBlock, 0) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return StageResult<T>.Success(envelope.Payload);
        }

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

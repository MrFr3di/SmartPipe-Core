using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Testing.Fixtures;

namespace SmartPipe.Extensions.Tests.Fixtures;

public class SocPokecFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartpipe-soc-pokec-" + Guid.NewGuid().ToString("N"));
    private readonly string _edgePath;

    public SocPokecFixtureTests()
    {
        Directory.CreateDirectory(_root);
        _edgePath = Path.Combine(_root, "soc-pokec-small.txt");
        File.WriteAllText(
            _edgePath,
            """
            1 2
            2	3
            invalid line
            4 5
            6 7

            """);
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
    [Trait("Category", "HugeFixture")]
    public void SocPokec_HugeFixture_IsSkippedUnlessEnabled()
    {
        var originalEnabled = Environment.GetEnvironmentVariable(FixtureEnvironment.EnableHugeFixtures);
        var originalPath = Environment.GetEnvironmentVariable(FixtureEnvironment.SocPokecPath);

        try
        {
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableHugeFixtures, null);
            Environment.SetEnvironmentVariable(FixtureEnvironment.SocPokecPath, null);

            SocPokecFixture.TryGetHugeFixturePath(out _, out var reason).Should().BeFalse();
            reason.Should().Contain(FixtureEnvironment.EnableHugeFixtures);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableHugeFixtures, originalEnabled);
            Environment.SetEnvironmentVariable(FixtureEnvironment.SocPokecPath, originalPath);
        }
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_StreamEdges_DoesNotMaterializeFile()
    {
        var firstTwo = new List<SocPokecEdge>();

        await foreach (var edge in SocPokecFixture.ReadEdgesAsync(_edgePath, ct: TestContext.Current.CancellationToken))
        {
            firstTwo.Add(edge);
            if (firstTwo.Count == 2)
                break;
        }

        firstTwo.Should().Equal(new SocPokecEdge(1, 2), new SocPokecEdge(2, 3));
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_StreamEdges_CountsRows()
    {
        var summary = await SocPokecFixture.AnalyzeAsync(_edgePath, TestContext.Current.CancellationToken);

        summary.TotalLines.Should().Be(5);
        summary.ValidCount.Should().Be(4);
        summary.InvalidCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_StreamEdges_ParsesTwoIdsPerLine()
    {
        var edges = await ReadEdgesAsync();

        edges.Should().Contain(new SocPokecEdge(1, 2));
        edges.Should().Contain(new SocPokecEdge(2, 3));
        edges.Should().Contain(new SocPokecEdge(4, 5));
        edges.Should().Contain(new SocPokecEdge(6, 7));
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_StreamEdges_ComputesStableRollingDigest()
    {
        var first = await SocPokecFixture.AnalyzeAsync(_edgePath, TestContext.Current.CancellationToken);
        var second = await SocPokecFixture.AnalyzeAsync(_edgePath, TestContext.Current.CancellationToken);

        first.RollingDigest.Should().Be(second.RollingDigest);
        first.RollingDigest.Should().HaveLength(64);
        first.MinId.Should().Be(1);
        first.MaxId.Should().Be(7);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_BoundedPipeline_ProcessesAllEdgesWithoutDrops()
    {
        var options = new PipelineRuntimeOptions
        {
            InputCapacity = 2,
            OutputCapacity = 2,
            OutputPolicy = PipelineOutputPolicy.EmitAll,
        };

        var run = PipelineBuilder
            .From(new SocPokecEdgeSource(_edgePath))
            .Transform(PipelineTransformer.FromFunc<SocPokecEdge, SocPokecEdge>(
                static (edge, ct) => ValueTask.FromResult(edge)))
            .WithRuntimeOptions(options)
            .Run();

        var results = await ReadResultsAsync(run);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        results.Should().HaveCount(4);
        run.Metrics.ItemsDropped.Should().Be(0);
        run.Metrics.OutputItemsDropped.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_BoundedPipeline_MaxConcurrency4_Completes()
    {
        var run = PipelineBuilder
            .From(new SocPokecEdgeSource(_edgePath))
            .Transform(PipelineTransformer.FromFunc<SocPokecEdge, SocPokecEdge>(
                static (edge, ct) => ValueTask.FromResult(edge)))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 4,
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .Run();

        var results = await ReadResultsAsync(run);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        results.Should().HaveCount(4);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_BoundedPipeline_DrainMidFile_CompletesAcceptedEdges()
    {
        var transformer = new BlockingTransformer<SocPokecEdge>();
        var sink = new CollectingSink<SocPokecEdge>();

        var run = PipelineBuilder
            .From(new SocPokecEdgeSource(_edgePath))
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
        sink.Values.Count.Should().BeLessThan(4);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_BoundedPipeline_CancelMidFile_CancelsPredictably()
    {
        var transformer = new BlockingTransformer<SocPokecEdge>();

        var run = PipelineBuilder
            .From(new SocPokecEdgeSource(_edgePath))
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

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_InvalidLines_GoToFailurePolicy()
    {
        var invalidLines = new List<string>();
        _ = await SocPokecFixture.ReadEdgesAsync(
                _edgePath,
                invalidLines.Add,
                TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        invalidLines.Should().ContainSingle().Which.Should().Be("invalid line");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task SocPokec_ThroughputSmoke_ReportsItemsPerSecond()
    {
        var options = new PipelineRuntimeOptions
        {
            OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
        };
        var stopwatch = Stopwatch.StartNew();
        var sink = new CollectingSink<SocPokecEdge>();

        var run = PipelineBuilder
            .From(new SocPokecEdgeSource(_edgePath))
            .Transform(PipelineTransformer.FromFunc<SocPokecEdge, SocPokecEdge>(
                static (edge, ct) => ValueTask.FromResult(edge)))
            .WithRuntimeOptions(options)
            .To(sink);

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        var summary = SocPokecFixture.CreateSummary(
            "soc-pokec-small",
            _edgePath,
            run.Metrics.ItemsProcessed,
            sink.Values.Count,
            invalidCount: 1,
            stopwatch,
            run.State,
            options);

        summary.ProcessedCount.Should().Be(4);
        summary.ItemsPerSecond.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task StressSummary_ContainsRequiredFields()
    {
        var options = new PipelineRuntimeOptions
        {
            InputCapacity = 8,
            OutputCapacity = 4,
            MaxConcurrency = 2,
            OutputPolicy = PipelineOutputPolicy.EmitAll,
        };
        var summary = SocPokecFixture.CreateSummary(
            "soc-pokec-small",
            _edgePath,
            processedCount: 4,
            validCount: 4,
            invalidCount: 1,
            Stopwatch.StartNew(),
            PipelineRunState.Completed,
            options,
            filteredCount: 0,
            droppedCount: 0,
            deadLetterCount: 0);
        var path = Path.Combine(_root, "artifacts", "stress", "soc-pokec-summary.json");

        await SocPokecFixture.WriteSummaryAsync(path, summary, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        var root = document.RootElement;

        foreach (var property in new[]
        {
            "FixtureId",
            "FixturePath",
            "SizeBytes",
            "ProcessedCount",
            "ValidCount",
            "InvalidCount",
            "FilteredCount",
            "DroppedCount",
            "DeadLetterCount",
            "ElapsedMs",
            "ItemsPerSecond",
            "MaxWorkingSet",
            "MaxGcHeap",
            "Gen0Collections",
            "Gen1Collections",
            "Gen2Collections",
            "FinalPipelineState",
            "OutputPolicy",
            "InputCapacity",
            "OutputCapacity",
            "MaxConcurrency",
        })
        {
            root.TryGetProperty(property, out _).Should().BeTrue(property);
        }
    }

    private async Task<List<SocPokecEdge>> ReadEdgesAsync()
    {
        var edges = new List<SocPokecEdge>();
        await foreach (var edge in SocPokecFixture.ReadEdgesAsync(_edgePath, ct: TestContext.Current.CancellationToken))
            edges.Add(edge);
        return edges;
    }

    private static async Task<List<PipelineResult<SocPokecEdge>>> ReadResultsAsync(PipelineRun<SocPokecEdge> run)
    {
        var results = new List<PipelineResult<SocPokecEdge>>();
        await foreach (var result in run.ReadResultsAsync(TestContext.Current.CancellationToken))
            results.Add(result);
        return results;
    }

    private sealed class SocPokecEdgeSource(string path) : IPipelineSource<SocPokecEdge>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<SocPokecEdge>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var edge in SocPokecFixture.ReadEdgesAsync(path, ct: ct).ConfigureAwait(false))
                yield return ProcessingEnvelope<SocPokecEdge>.Create(edge);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

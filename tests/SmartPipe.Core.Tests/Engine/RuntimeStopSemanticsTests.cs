using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class RuntimeStopSemanticsTests
{
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Parallel_FaultPipeline_WhileSourceWaitsForNextItem_FaultsPromptly()
    {
        var source = new IdleAfterFirstSource();
        var run = PipelineBuilder
            .From(source)
            .Transform(
                PipelineTransformer.FromFunc<int, int>(static (_, _) =>
                    throw new InvalidOperationException("boom")),
                new StageFailureOptions { OnPermanentFailure = FailureAction.FaultPipeline })
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = 2 })
            .Run();

        var act = async () => await run.Completion.WaitAsync(CompletionBudget);

        await act.Should().ThrowAsync<PipelineFailureActionException>();
        run.State.Should().Be(PipelineRunState.Faulted);
        source.ObservedCancellation.Should().BeTrue();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task Parallel_StopPipeline_WhileSourceWaitsForNextItem_CompletesPromptly()
    {
        var source = new IdleAfterFirstSource();
        var run = PipelineBuilder
            .From(source)
            .Transform(
                PipelineTransformer.FromFunc<int, int>(static (_, _) =>
                    throw new InvalidOperationException("boom")),
                new StageFailureOptions { OnPermanentFailure = FailureAction.StopPipeline })
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = 2 })
            .Run();

        var outputs = new List<PipelineOutput<int>>();
        await foreach (var output in run.Outputs.ReadAllAsync().WithCancellation(
            new CancellationTokenSource(CompletionBudget).Token))
        {
            outputs.Add(output);
        }

        await run.Completion.WaitAsync(CompletionBudget);

        run.State.Should().Be(PipelineRunState.Completed);
        outputs.Should().ContainSingle().Which.Result.IsFailure.Should().BeTrue();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task Parallel_DeadLetterWrites_DoNotInterleaveOnSharedStream()
    {
        const int itemCount = 200;
        var stream = new OverlapDetectingStream();
        var run = PipelineBuilder
            .From(new RangeSource(itemCount))
            .Transform(
                PipelineTransformer.FromFunc<int, int>(static async (_, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("boom");
                }),
                new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
                new StageDeadLetterOptions<int>(stream))
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = 8 })
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await run.DisposeAsync();

        stream.OverlappingWrites.Should().Be(0);
        var lines = Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(itemCount);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }
    }

    [Fact]
    public async Task TryDrainAsync_WhileActivationIsPending_HonorsTimeoutAndDrainsAfterActivation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SlowInitializingSource(gate.Task);
        var run = PipelineBuilder
            .From(source)
            .Transform(PipelineTransformer.FromFunc<int, int>(static (x, _) => ValueTask.FromResult(x)))
            .Run();

        var drain = run.TryDrainAsync(TimeSpan.FromMilliseconds(50)).AsTask();
        var finished = await Task.WhenAny(drain, Task.Delay(CompletionBudget));
        gate.TrySetResult();

        finished.Should().BeSameAs(drain);
        (await drain).Status.Should().Be(PipelineDrainStatus.TimedOutStillRunning);

        await run.Completion.WaitAsync(CompletionBudget);
        run.State.Should().Be(PipelineRunState.Completed);
        source.ItemsYielded.Should().Be(0, "the pending drain applies once the executor attaches");
        await run.DisposeAsync();
    }

    private sealed class IdleAfterFirstSource : IPipelineSource<int>
    {
        public bool ObservedCancellation { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return ProcessingEnvelope<int>.Create(1, "source-pipeline", "source-run", 1);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RangeSource(int count) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                await Task.Yield();
                yield return ProcessingEnvelope<int>.Create(i, "source-pipeline", "source-run", (ulong)i + 1);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SlowInitializingSource(Task gate) : IPipelineSource<int>
    {
        private int _itemsYielded;

        public int ItemsYielded => Volatile.Read(ref _itemsYielded);

        public async ValueTask InitializeAsync(CancellationToken ct = default) => await gate.WaitAsync(ct);

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _itemsYielded);
            yield return ProcessingEnvelope<int>.Create(1, "source-pipeline", "source-run", 1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OverlapDetectingStream : MemoryStream
    {
        private readonly object _writeSync = new();
        private int _activeWrites;
        private int _overlappingWrites;

        public int OverlappingWrites => Volatile.Read(ref _overlappingWrites);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _activeWrites) > 1)
                Interlocked.Increment(ref _overlappingWrites);
            try
            {
                await Task.Yield();
                lock (_writeSync)
                    Write(buffer.Span);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}

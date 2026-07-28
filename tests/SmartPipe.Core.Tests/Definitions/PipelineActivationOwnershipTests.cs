using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineActivationOwnershipTests
{
    [Fact]
    public async Task Activator_PreservesFactoryInitializeOrderAndStageIdentity()
    {
        var events = new ConcurrentQueue<string>();
        var source = new ActivationRecordingSource(events);
        var transformer = new ActivationRecordingTransformer(events);
        var sink = new ActivationRecordingSink(events);
        var stageKey = new PipelineStageKey("normalize");
        using var activationCancellation = new CancellationTokenSource();
        var expectedToken = activationCancellation.Token;
        CancellationToken sourceToken = default;
        CancellationToken stageToken = default;
        CancellationToken sinkToken = default;
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((context, token) =>
            {
                sourceToken = token;
                events.Enqueue($"source.factory:{context.PipelineKey.Value}:{token.CanBeCanceled}");
                return ValueTask.FromResult<IPipelineSource<int>>(source);
            }),
            PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, token) =>
            {
                stageToken = token;
                events.Enqueue($"stage.factory:{token.CanBeCanceled}");
                return ValueTask.FromResult<IPipelineTransformer<int, int>>(transformer);
            }),
            stageKey,
            "canonical-name",
            PipelineComponent.RuntimeOwned<IPipelineSink<int>>((_, token) =>
            {
                sinkToken = token;
                events.Enqueue($"sink.factory:{token.CanBeCanceled}");
                return ValueTask.FromResult<IPipelineSink<int>>(sink);
            }));

        var context = ActivationTestSupport.CreateContext(definition.Key);
        var graph = await definition.ActivateAsync(context, expectedToken);

        events.Should().Equal(
            "source.factory:orders:True", "source.init",
            "stage.factory:True", "stage.init",
            "sink.factory:True", "sink.init");
        graph.Source.Should().BeSameAs(source);
        graph.Stages.Should().ContainSingle();
        graph.Stages[0].Key.Should().Be(stageKey);
        graph.Stages[0].StageName.Should().Be("canonical-name");
        graph.Sink.Should().BeSameAs(sink);
        sourceToken.Should().Be(expectedToken);
        stageToken.Should().Be(expectedToken);
        sinkToken.Should().Be(expectedToken);

        await graph.Lifetime.DisposeAsync();
        events.TakeLast(3).Should().Equal("sink.dispose", "stage.dispose", "source.dispose");
    }

    [Fact]
    public async Task Activator_DoesNotDisposeScopeOwnedOrExternallyOwnedComponents()
    {
        var events = new ConcurrentQueue<string>();
        var source = new ActivationRecordingSource(events);
        var transformer = new ActivationRecordingTransformer(events);
        var sink = new ActivationRecordingSink(events);
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.ScopeOwned<IPipelineSource<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSource<int>>(source)),
            PipelineComponent.Borrowed<IPipelineTransformer<int, int>>(transformer, initialize: true),
            new PipelineStageKey("normalize"),
            "normalize",
            PipelineComponent.Borrowed<IPipelineSink<int>>(sink, initialize: true));

        var graph = await definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key, new ActivationEmptyServices()),
            CancellationToken.None);
        await graph.Lifetime.DisposeAsync();

        events.Should().NotContain("source.dispose");
        events.Should().NotContain("stage.dispose");
        events.Should().NotContain("sink.dispose");
    }

    [Fact]
    public async Task Ledger_RollsBackInReverseOrderAndSkipsNonRuntimeOwnership()
    {
        var calls = new ConcurrentQueue<string>();
        var ledger = new PipelineActivationLedger();
        var stageKey = new PipelineStageKey("normalize");

        ledger.Append(new ActivatedComponentLease
        {
            Role = "source",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = () => Record(calls, "source"),
        });
        ledger.Append(new ActivatedComponentLease
        {
            Role = "stage",
            Ownership = PipelineComponentOwnership.ScopeOwned,
            StageKey = stageKey,
            RuntimeOwnedCleanup = null,
        });
        ledger.Append(new ActivatedComponentLease
        {
            Role = "sink",
            Ownership = PipelineComponentOwnership.ExternallyOwned,
            RuntimeOwnedCleanup = null,
        });
        ledger.Append(new ActivatedComponentLease
        {
            Role = "stage-2",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = () => Record(calls, "stage-2"),
        });

        var errors = await ledger.RollbackAsync();

        errors.Should().BeEmpty();
        calls.Should().Equal("stage-2", "source");
    }

    [Fact]
    public async Task Ledger_AttemptsEveryCleanupAndReturnsErrorsInAttemptOrder()
    {
        var calls = new ConcurrentQueue<string>();
        var firstError = new InvalidOperationException("first");
        var secondError = new IOException("second");
        var ledger = new PipelineActivationLedger();

        ledger.Append(new ActivatedComponentLease
        {
            Role = "source",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = () => Throw(calls, "source", firstError),
        });
        ledger.Append(new ActivatedComponentLease
        {
            Role = "stage",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = () => ThrowAsync(calls, "stage", secondError),
        });
        ledger.Append(new ActivatedComponentLease
        {
            Role = "sink",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = () => Record(calls, "sink"),
        });

        var errors = await ledger.RollbackAsync();

        calls.Should().Equal("sink", "stage", "source");
        errors.Should().Equal(secondError, firstError);
    }

    [Fact]
    public async Task Ledger_ConcurrentCleanupSharesTaskAndInvokesCallbacksOnce()
    {
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var ledger = new PipelineActivationLedger();
        ledger.Append(new ActivatedComponentLease
        {
            Role = "source",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = async () =>
            {
                Interlocked.Increment(ref callbackCount);
                await release.Task.ConfigureAwait(false);
            },
        });

        var rollback = ledger.RollbackAsync();
        var dispose = ledger.DisposeAsync();

        rollback.AsTask().Should().BeSameAs(dispose.AsTask());
        release.SetResult(null);
        (await rollback).Should().BeEmpty();
        (await dispose).Should().BeEmpty();
        callbackCount.Should().Be(1);
    }

    private static ValueTask Record(ConcurrentQueue<string> calls, string role)
    {
        calls.Enqueue(role);
        return ValueTask.CompletedTask;
    }

    private static ValueTask Throw(
        ConcurrentQueue<string> calls,
        string role,
        Exception error)
    {
        calls.Enqueue(role);
        throw error;
    }

    private static async ValueTask ThrowAsync(
        ConcurrentQueue<string> calls,
        string role,
        Exception error)
    {
        calls.Enqueue(role);
        await ValueTask.CompletedTask;
        throw error;
    }
}

internal static class ActivationTestSupport
{
    public static PipelineDefinition<int, int> CreateDefinition(
        PipelineComponent<IPipelineSource<int>> source,
        PipelineComponent<IPipelineTransformer<int, int>>? transformer = null,
        PipelineStageKey? stageKey = null,
        string stageName = "normalize",
        PipelineComponent<IPipelineSink<int>>? sink = null)
    {
        var root = PipelineDefinitionBuilder.From(new PipelineKey("orders"), source);
        if (transformer is null)
            return sink is null ? root.Build() : root.To(sink);

        var transformed = root.Transform(
            stageKey ?? new PipelineStageKey("normalize"),
            transformer,
            stageName: stageName);
        return sink is null ? transformed.Build() : transformed.To(sink);
    }

    public static PipelineActivationContext CreateContext(
        PipelineKey key,
        IServiceProvider? services = null) =>
        new(key, Guid.NewGuid(), services ?? new ActivationEmptyServices());
}

internal sealed class ActivationEmptyServices : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

internal sealed class ActivationRecordingSource : IPipelineSource<int>
{
    private readonly ConcurrentQueue<string> _events;

    public ActivationRecordingSource(ConcurrentQueue<string> events) => _events = events;

    public Exception? InitializeError { get; set; }

    public Exception? DisposeError { get; set; }

    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        _events.Enqueue("source.init");
        return InitializeError is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(InitializeError);
    }

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        _events.Enqueue("source.dispose");
        if (DisposeError is not null)
            throw DisposeError;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ActivationRecordingTransformer : IPipelineTransformer<int, int>
{
    private readonly ConcurrentQueue<string> _events;
    private readonly string _role;

    public ActivationRecordingTransformer(
        ConcurrentQueue<string> events,
        string role = "stage")
    {
        _events = events;
        _role = role;
    }

    public Exception? InitializeError { get; set; }

    public Exception? DisposeError { get; set; }

    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        _events.Enqueue($"{_role}.init");
        return InitializeError is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(InitializeError);
    }

    public ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

    public ValueTask DisposeAsync()
    {
        _events.Enqueue($"{_role}.dispose");
        if (DisposeError is not null)
            throw DisposeError;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ActivationRecordingSink : IPipelineSink<int>
{
    private readonly ConcurrentQueue<string> _events;

    public ActivationRecordingSink(ConcurrentQueue<string> events) => _events = events;

    public Exception? InitializeError { get; set; }

    public Exception? DisposeError { get; set; }

    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        _events.Enqueue("sink.init");
        return InitializeError is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(InitializeError);
    }

    public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _events.Enqueue("sink.dispose");
        if (DisposeError is not null)
            throw DisposeError;
        return ValueTask.CompletedTask;
    }
}

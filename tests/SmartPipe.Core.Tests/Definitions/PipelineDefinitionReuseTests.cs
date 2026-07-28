using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineDefinitionReuseTests
{
    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ReusableDefinition_ActivationFailureInOneRun_DoesNotPoisonOtherRuns()
    {
        var key = new PipelineKey("reuse-failure-isolated");
        var failedRunId = Guid.NewGuid();
        var successfulRunId = Guid.NewGuid();
        var expected = new InvalidOperationException("one run failed");
        var factoryCalls = 0;
        var createdSources = new ConcurrentBag<ReuseSource>();

        var definition = PipelineDefinitionBuilder
            .From(
                key,
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>(async (context, _) =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    if (context.RunId == failedRunId)
                        throw expected;

                    var source = new ReuseSource();
                    createdSources.Add(source);
                    await Task.CompletedTask;
                    return source;
                }))
            .Build();

        var failed = definition.StartDeferred(
            new PipelineActivationContext(key, failedRunId),
            CancellationToken.None);
        var successful = definition.StartDeferred(
            new PipelineActivationContext(key, successfulRunId),
            CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => failed.Completion);
        failure.Should().BeSameAs(expected);
        await successful.Completion;

        factoryCalls.Should().Be(2);
        createdSources.Should().ContainSingle();
        successful.Run.RunId.Should().Be(successfulRunId);
        successful.Run.PipelineKey.Should().Be(key);
        createdSources.Single().DisposeCount.Should().Be(1);
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ReusableDefinition_FactoryExceptionIsPerRunAndNotCachedByCompiledPlan()
    {
        var key = new PipelineKey("reuse-factory-failure-per-run");
        var factoryCalls = 0;
        var errors = new ConcurrentBag<Exception>();

        var definition = PipelineDefinitionBuilder
            .From(
                key,
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>(async (_, _) =>
                {
                    var call = Interlocked.Increment(ref factoryCalls);
                    var error = new InvalidOperationException($"factory attempt {call}");
                    errors.Add(error);
                    await Task.CompletedTask;
                    throw error;
                }))
            .Build();

        var first = definition.StartDeferred(
            new PipelineActivationContext(key, Guid.NewGuid()),
            CancellationToken.None);
        var second = definition.StartDeferred(
            new PipelineActivationContext(key, Guid.NewGuid()),
            CancellationToken.None);

        var firstError = await Record.ExceptionAsync(() => first.Completion);
        var secondError = await Record.ExceptionAsync(() => second.Completion);

        firstError.Should().BeOfType<InvalidOperationException>();
        secondError.Should().BeOfType<InvalidOperationException>();
        firstError.Should().NotBeSameAs(secondError);
        errors.Should().HaveCount(2);
        errors.Should().OnlyHaveUniqueItems();
        factoryCalls.Should().Be(2);
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ScopeOwnedResolver_ReceivesExactContextAndProviderPerRun()
    {
        var key = new PipelineKey("scope-context-per-run");
        var firstProvider = new ReuseServices();
        var secondProvider = new ReuseServices();
        var contexts = new ConcurrentBag<(Guid RunId, IServiceProvider? Services)>();

        var definition = PipelineDefinitionBuilder
            .From(
                key,
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(async (context, _) =>
                {
                    contexts.Add((context.RunId, context.Services));
                    await Task.CompletedTask;
                    return new ReuseSource();
                }))
            .Build();

        var firstContext = new PipelineActivationContext(key, Guid.NewGuid(), firstProvider);
        var secondContext = new PipelineActivationContext(key, Guid.NewGuid(), secondProvider);
        var runs = await Task.WhenAll(
            definition.StartAsync(firstContext, CancellationToken.None),
            definition.StartAsync(secondContext, CancellationToken.None));

        await Task.WhenAll(runs.Select(run => run.Completion));

        contexts.Should().HaveCount(2);
        contexts.Should().Contain((firstContext.RunId, firstProvider));
        contexts.Should().Contain((secondContext.RunId, secondProvider));
        contexts.Select(entry => entry.Services).Should().OnlyHaveUniqueItems();
        runs.Select(run => run.RunId).Should().OnlyHaveUniqueItems();
        runs.Should().OnlyContain(run => run.PipelineKey == key);
    }

    private sealed class ReuseSource : IPipelineSource<int>
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReuseServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

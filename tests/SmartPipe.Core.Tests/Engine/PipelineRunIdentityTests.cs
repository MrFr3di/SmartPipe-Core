using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class PipelineRunIdentityTests
{
    [Fact]
    public void PublicConstructor_UsesCompatibilityIdentityDefaults()
    {
        var run = new PipelineRun<int>(
            Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            Task.CompletedTask,
            () => PipelineRunState.Completed);

        run.PipelineKey.IsEmpty.Should().BeTrue();
        run.RunId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task RuntimeCreatedRun_ExposesPipelineIdentity()
    {
        var key = new PipelineKey("identity");
        var source = new IdentityEmptySource();
        var definition = PipelineDefinitionBuilder.From(
                key,
                PipelineComponent.Borrowed<IPipelineSource<int>>(source, initialize: true))
            .Build();
        var context = new PipelineActivationContext(
            key,
            Guid.NewGuid(),
            new IdentityEmptyServices());

        var run = await definition.StartAsync(context, CancellationToken.None);

        run.PipelineKey.Should().Be(key);
        run.RunId.Should().Be(context.RunId);
        run.RunId.Should().NotBe(Guid.Empty);
        await run.Completion;
    }

    [Fact]
    public async Task WithLifetime_PropagatesIdentityExactly()
    {
        var key = new PipelineKey("identity-wrapper");
        var source = new IdentityEmptySource();
        var definition = PipelineDefinitionBuilder.From(
                key,
                PipelineComponent.Borrowed<IPipelineSource<int>>(source, initialize: true))
            .Build();
        var context = new PipelineActivationContext(
            key,
            Guid.NewGuid(),
            new IdentityEmptyServices());
        var run = await definition.StartAsync(context, CancellationToken.None);
        await run.Completion;

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);

        derived.PipelineKey.Should().Be(run.PipelineKey);
        derived.RunId.Should().Be(run.RunId);
        derived.Outputs.Should().BeSameAs(run.Outputs);
    }
}

internal sealed class IdentityEmptySource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class IdentityEmptyServices : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

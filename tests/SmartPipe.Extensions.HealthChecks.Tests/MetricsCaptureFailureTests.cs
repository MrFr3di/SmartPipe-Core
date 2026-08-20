using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks.Tests;

public sealed class MetricsCaptureFailureTests
{
    [Fact]
    public async Task MetricsCaptureFailureIsSanitizedWithoutChangingPrepopulatedTerminal()
    {
        var key = new PipelineKey("metrics-failure");
        var services = new ServiceCollection();
        services.AddSmartPipe().AddPipeline(Definition(key.Value));
        services.AddOptions<SmartPipeLivenessOptions>("metrics");
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(key.Value);
        var run = await factory.StartAsync(TestContext.Current.CancellationToken);
        await run.Completion;
        await run.DisposeAsync();

        var source = provider.GetRequiredService<ISmartPipeRunObservationSource>();
        var before = source.Capture(key).LatestTerminal;
        Assert.NotNull(before);
        var throwingSource = new ThrowingMetricsSource(
            source,
            new InvalidOperationException("sensitive metrics provider failure"));
        var check = new SmartPipePipelineLivenessHealthCheck(
            key,
            throwingSource,
            provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>(),
            TimeProvider.System);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("metrics", check, HealthStatus.Degraded, null),
        };

        var result = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);
        var after = source.Capture(key).LatestTerminal;

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("sensitive", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metrics provider", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Data.Values, value =>
            value.ToString()!.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
        Assert.Same(before, after);
        Assert.Equal(before!.Sequence, after!.Sequence);
        Assert.Equal(before.Identity.RunId, after.Identity.RunId);
    }

    private static PipelineDefinition<int, int> Definition(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
            .Build();

    private sealed class ThrowingMetricsSource(
        ISmartPipeRunObservationSource source,
        Exception error) : ISmartPipeRunObservationSource
    {
        public SmartPipePipelineObservation Capture(PipelineKey pipelineKey)
        {
            _ = source.Capture(pipelineKey);
            throw error;
        }

        public IReadOnlyList<SmartPipePipelineObservation> CaptureAll()
        {
            _ = source.CaptureAll();
            throw error;
        }
    }

    private sealed class EmptySource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

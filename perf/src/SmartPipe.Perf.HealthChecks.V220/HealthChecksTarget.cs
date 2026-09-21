using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.HealthChecks;

namespace SmartPipe.Perf.HealthChecks;

internal sealed class HealthChecksTarget : IDisposable
{
    private const string PipelineKey = "perf-health";
    private const string CheckName = "perf-health-readiness";

    private readonly ServiceProvider _provider;
    private readonly HealthCheckService _healthCheckService;

    internal HealthChecksTarget()
    {
        _provider = BuildProvider();
        _healthCheckService = _provider.GetRequiredService<HealthCheckService>();
    }

    internal static int RegisterAndBuildProvider()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(CreateProviderOptions());
        _ = provider.GetRequiredService<HealthCheckService>();
        return services.Count;
    }

    internal object ResolveHealthCheckService() =>
        _provider.GetRequiredService<HealthCheckService>();

    internal Task<HealthReport> CheckRegisteredPipelineAsync() =>
        _healthCheckService.CheckHealthAsync(
            registration => string.Equals(registration.Name, CheckName, StringComparison.Ordinal),
            CancellationToken.None);

    public void Dispose() => _provider.Dispose();

    private static ServiceProvider BuildProvider() =>
        CreateServices().BuildServiceProvider(CreateProviderOptions());

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<EmptySource>();
        services.AddScoped<IdentityTransformer>();
        services.AddScoped<NullSink>();

        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey(PipelineKey),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (context, _) =>
                        ValueTask.FromResult<IPipelineSource<int>>(
                            context.Services!.GetRequiredService<EmptySource>())))
            .Transform(
                new PipelineStageKey("identity"),
                PipelineComponent.ScopeOwned<IPipelineTransformer<int, int>>(
                    static (context, _) =>
                        ValueTask.FromResult<IPipelineTransformer<int, int>>(
                            context.Services!.GetRequiredService<IdentityTransformer>())))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .To(PipelineComponent.ScopeOwned<IPipelineSink<int>>(
                static (context, _) =>
                    ValueTask.FromResult<IPipelineSink<int>>(
                        context.Services!.GetRequiredService<NullSink>())));

        services.AddSmartPipe()
            .AddPipeline(definition)
            .AddReadiness(
                options =>
                {
                    options.RunRequirement = SmartPipeReadinessRunRequirement.ActiveRunRequired;
                    options.FailOnLatestFailure = true;
                },
                name: CheckName,
                failureStatus: HealthStatus.Degraded);

        return services;
    }

    private static ServiceProviderOptions CreateProviderOptions() =>
        new()
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        };

    private sealed class EmptySource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdentityTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullSink : IPipelineSink<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

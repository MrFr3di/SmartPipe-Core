using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;
using SmartPipe.Extensions;

namespace SmartPipe.Perf.HealthChecks;

internal sealed class HealthChecksTarget : IDisposable
{
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

        services.AddSmartPipe<int, int>(
            "perf-health",
            builder => builder
                .UseSource<EmptySource>()
                .UseStage<IdentityTransformer>()
                .UseSink<NullSink>()
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
                }));

        services
            .AddHealthChecks()
            .AddSmartPipeHealthCheck<int, int>(CheckName);

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

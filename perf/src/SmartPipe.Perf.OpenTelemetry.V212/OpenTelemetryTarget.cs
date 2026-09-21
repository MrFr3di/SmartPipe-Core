using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace SmartPipe.Perf.OpenTelemetry;

internal sealed class OpenTelemetryTarget : IDisposable
{
    private const string SourceName = "SmartPipe.Core";

    private readonly ServiceProvider _provider;

    internal OpenTelemetryTarget() =>
        _provider = BuildProvider(CreateServices());

    internal static int RegisterInstrumentation()
    {
        var services = CreateServices();
        return services.Count;
    }

    internal static int BuildResolveDisposeProviders()
    {
        using var provider = BuildProvider(CreateServices());
        _ = provider.GetRequiredService<MeterProvider>();
        _ = provider.GetRequiredService<TracerProvider>();
        return 2;
    }

    internal int ResolveProviders()
    {
        _ = _provider.GetRequiredService<MeterProvider>();
        _ = _provider.GetRequiredService<TracerProvider>();
        return 2;
    }

    internal static void ValidateRegistration()
    {
        var metrics = new List<Metric>();
        var activities = new List<Activity>();
        var services = CreateServices();

        services.AddOpenTelemetry()
            .WithMetrics(metricsBuilder => metricsBuilder.AddInMemoryExporter(metrics))
            .WithTracing(tracingBuilder => tracingBuilder.AddInMemoryExporter(activities));

        using var provider = BuildProvider(services);
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        using var meter = new Meter(SourceName);
        var counter = meter.CreateCounter<long>("smartpipe.perf.registration.probe");
        if (!counter.Enabled)
            throw new InvalidOperationException("Manual baseline meter registration is not active.");

        counter.Add(1);

        using var activitySource = new ActivitySource(SourceName);
        if (!activitySource.HasListeners())
            throw new InvalidOperationException("Manual baseline activity-source registration is not active.");

        using (var activity = activitySource.StartActivity("smartpipe.perf.registration.probe"))
        {
            if (activity is null)
                throw new InvalidOperationException("Manual baseline tracing registration did not create an Activity.");
        }

        if (!meterProvider.ForceFlush())
            throw new InvalidOperationException("Manual baseline MeterProvider failed to flush.");
        if (!tracerProvider.ForceFlush())
            throw new InvalidOperationException("Manual baseline TracerProvider failed to flush.");

        if (metrics.Count == 0)
            throw new InvalidOperationException("Manual baseline registration exported no metric data.");
        if (activities.Count == 0)
            throw new InvalidOperationException("Manual baseline registration exported no trace data.");
    }

    public void Dispose() => _provider.Dispose();

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();

        services.AddOpenTelemetry()
            .WithMetrics(static metrics => metrics.AddMeter(SourceName))
            .WithTracing(static tracing => tracing.AddSource(SourceName));

        return services;
    }

    private static ServiceProvider BuildProvider(ServiceCollection services) =>
        services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
}

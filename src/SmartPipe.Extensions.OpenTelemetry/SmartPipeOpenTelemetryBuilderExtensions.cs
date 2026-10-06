#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmartPipe.Core;

namespace SmartPipe.Extensions.OpenTelemetry;

/// <summary>
/// Registers SmartPipe pipeline diagnostics sources into the standard OpenTelemetry builder.
/// </summary>
public static class SmartPipeOpenTelemetryBuilderExtensions
{
    /// <summary>
    /// Registers the SmartPipe meter and activity source with the OpenTelemetry provider builders.
    /// </summary>
    /// <param name="builder">The OpenTelemetry builder provided by the host application.</param>
    /// <returns>The exact same <paramref name="builder" /> instance.</returns>
    /// <remarks>
    /// <para>
    /// This method only subscribes to the diagnostics sources that <c>SmartPipe.Core</c> already
    /// emits. It does not create a telemetry provider, does not start background work, does not
    /// select an exporter, and does not configure resources, samplers, processors, or readers.
    /// Repeated successful calls on the same <see cref="IServiceCollection" /> produce one logical
    /// SmartPipe registration for metrics and one for tracing; different service collections remain
    /// independent. Retrying on the same collection after an OpenTelemetry configuration callback
    /// threw is not supported.
    /// </para>
    /// </remarks>
    public static IOpenTelemetryBuilder AddSmartPipeInstrumentation(this IOpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(SmartPipeInstrumentationMarker)))
            return builder;

        services.ConfigureOpenTelemetryMeterProvider(static metrics =>
            metrics.AddMeter(SmartPipeDiagnostics.MeterName));
        services.ConfigureOpenTelemetryTracerProvider(static tracing =>
            tracing.AddSource(SmartPipeDiagnostics.ActivitySourceName));
        services.AddSingleton(SmartPipeInstrumentationMarker.Instance);

        return builder;
    }
}

/// <summary>Collection-local marker proving that SmartPipe instrumentation was registered.</summary>
internal sealed class SmartPipeInstrumentationMarker
{
    internal static readonly SmartPipeInstrumentationMarker Instance = new();

    private SmartPipeInstrumentationMarker()
    {
    }
}

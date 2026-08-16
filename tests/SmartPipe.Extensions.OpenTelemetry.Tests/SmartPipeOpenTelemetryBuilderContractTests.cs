#nullable enable

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmartPipe.Extensions.OpenTelemetry;

namespace SmartPipe.Extensions.OpenTelemetry.Tests;

public class SmartPipeOpenTelemetryBuilderContractTests
{
    [Fact]
    public void AddSmartPipeInstrumentation_NullBuilder_Throws()
    {
        var act = () => SmartPipeOpenTelemetryBuilderExtensions.AddSmartPipeInstrumentation(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("builder");
    }

    [Fact]
    public void AddSmartPipeInstrumentation_ReturnsSameBuilderInstance()
    {
        var services = new ServiceCollection();
        var builder = services.AddOpenTelemetry();

        var returned = builder.AddSmartPipeInstrumentation();

        returned.Should().BeSameAs(builder);
    }

    [Fact]
    public void AddSmartPipeInstrumentation_RegistersMetricsAndTracing()
    {
        var services = new ServiceCollection();
        var builder = services.AddOpenTelemetry();

        builder.AddSmartPipeInstrumentation();

        services.Should().Contain(d => d.ServiceType == typeof(SmartPipeInstrumentationMarker));
        using var provider = services.BuildServiceProvider();
        provider.GetService<MeterProvider>().Should().BeNull(
            "AddSmartPipeInstrumentation must not build a provider by itself");
        provider.GetService<TracerProvider>().Should().BeNull(
            "AddSmartPipeInstrumentation must not build a provider by itself");
    }

    [Fact]
    public void AddSmartPipeInstrumentation_RepeatedSuccessfulCall_IsLogicallyIdempotent()
    {
        var services = new ServiceCollection();
        var builder = services.AddOpenTelemetry();

        builder.AddSmartPipeInstrumentation();
        var descriptorsAfterFirst = services.ToList();
        builder.AddSmartPipeInstrumentation();
        var descriptorsAfterSecond = services.ToList();

        descriptorsAfterSecond.Should().HaveCount(descriptorsAfterFirst.Count,
            "repeated successful calls must not append additional SmartPipe registrations");
        descriptorsAfterSecond.Count(d => d.ServiceType == typeof(SmartPipeInstrumentationMarker))
            .Should().Be(1);
    }

    [Fact]
    public void AddSmartPipeInstrumentation_DifferentServiceCollections_AreIndependent()
    {
        var firstServices = new ServiceCollection();
        var secondServices = new ServiceCollection();

        firstServices.AddOpenTelemetry().AddSmartPipeInstrumentation();
        secondServices.AddOpenTelemetry().AddSmartPipeInstrumentation();

        firstServices.Count(d => d.ServiceType == typeof(SmartPipeInstrumentationMarker)).Should().Be(1);
        secondServices.Count(d => d.ServiceType == typeof(SmartPipeInstrumentationMarker)).Should().Be(1);
    }

    [Fact]
    public void AddSmartPipeInstrumentation_ComposesWithConsumerBuilderConfiguration()
    {
        var servicesBefore = new ServiceCollection();
        servicesBefore.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddMeter("Application.Metrics"))
            .AddSmartPipeInstrumentation();
        var servicesAfter = new ServiceCollection();
        servicesAfter.AddOpenTelemetry()
            .AddSmartPipeInstrumentation()
            .WithMetrics(metrics => metrics.AddMeter("Application.Metrics"));

        using var providerBefore = servicesBefore.BuildServiceProvider();
        using var providerAfter = servicesAfter.BuildServiceProvider();

        providerBefore.GetRequiredService<MeterProvider>().Should().NotBeNull();
        providerAfter.GetRequiredService<MeterProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddSmartPipeInstrumentation_DoesNotCreateListenersOrProviders()
    {
        var services = new ServiceCollection();

        services.AddOpenTelemetry().AddSmartPipeInstrumentation();

        services.Should().NotContain(d =>
            d.ServiceType == typeof(MeterProvider) || d.ServiceType == typeof(TracerProvider));
        using var provider = services.BuildServiceProvider();
        provider.GetService<MeterProvider>().Should().BeNull();
        provider.GetService<TracerProvider>().Should().BeNull();
    }
}

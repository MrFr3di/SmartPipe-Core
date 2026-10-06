#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using SmartPipe.Core;
using SmartPipe.Extensions.OpenTelemetry;

namespace SmartPipe.Extensions.OpenTelemetry.Tests;

[Collection("otel-sdk-integration")]
public class SmartPipeMetricsSdkIntegrationTests
{
    private static readonly string[] FrozenInstrumentNames =
    [
        "smartpipe.items.processed",
        "smartpipe.items.failed",
        "smartpipe.items.filtered",
        "smartpipe.items.dropped",
        "smartpipe.output.items.dropped",
        "smartpipe.observer.events.dropped",
        "smartpipe.items.retried",
        "smartpipe.items.deadlettered",
        "smartpipe.items.duplicates_filtered",
        "smartpipe.stage.duration",
        "smartpipe.sink.duration",
    ];

    [Fact]
    public async Task RegisteredMeter_ExportsAllFrozenInstrumentsWithExactKindsAndUnits()
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithMetrics(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var meterProvider = provider.GetRequiredService<MeterProvider>();
        var recorder = new SmartPipeMetricsRecorder();
        recorder.RecordProcessed(1.5);
        recorder.RecordFailed();
        recorder.RecordFiltered();
        recorder.RecordItemDropped();
        recorder.RecordOutputDropped();
        recorder.RecordObserverEventDropped();
        recorder.RecordDuplicate();
        recorder.RecordRetry();
        recorder.RecordDeadLetter();

        await using (var run = SdkPipelineFixtures.StartPipeline(1, TestContext.Current.CancellationToken))
            await run.Completion;

        meterProvider.ForceFlush();

        var byName = exported.ToDictionary(snapshot => snapshot.Name, snapshot => snapshot);
        byName.Keys.Should().BeEquivalentTo(FrozenInstrumentNames);

        byName["smartpipe.items.processed"].MetricType.Should().Be(MetricType.LongSum);
        byName["smartpipe.items.processed"].Unit.Should().Be("items");
        byName["smartpipe.items.failed"].MetricType.Should().Be(MetricType.LongSum);
        byName["smartpipe.observer.events.dropped"].Unit.Should().Be("events");
        byName["smartpipe.stage.duration"].MetricType.Should().Be(MetricType.Histogram);
        byName["smartpipe.stage.duration"].Unit.Should().Be("ms");
        byName["smartpipe.sink.duration"].MetricType.Should().Be(MetricType.Histogram);
        byName["smartpipe.sink.duration"].Unit.Should().Be("ms");

        byName["smartpipe.items.processed"].MetricPoints.Sum(point => point.GetSumLong()).Should().Be(2);
        byName["smartpipe.items.failed"].MetricPoints.Sum(point => point.GetSumLong()).Should().Be(1);
        byName["smartpipe.stage.duration"].MetricPoints.Sum(point => point.GetHistogramCount()).Should().Be(2);
        byName["smartpipe.sink.duration"].MetricPoints.Sum(point => point.GetHistogramCount()).Should().Be(1);
    }

    [Fact]
    public void RepeatedSuccessfulRegistration_DoesNotDuplicateMeasurements()
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        var builder = services.AddOpenTelemetry()
            .WithMetrics(builder => builder.AddInMemoryExporter(exported));
        builder.AddSmartPipeInstrumentation();
        builder.AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var meterProvider = provider.GetRequiredService<MeterProvider>();
        new SmartPipeMetricsRecorder().RecordProcessed(1.5);

        meterProvider.ForceFlush();

        exported.Count(snapshot => snapshot.Name == "smartpipe.items.processed").Should().Be(1);
        exported.Single(snapshot => snapshot.Name == "smartpipe.items.processed")
            .MetricPoints.Sum(point => point.GetSumLong()).Should().Be(1);
    }

    [Fact]
    public void BclMeterListener_RemainsUsable_AlongsideSdkProvider()
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithMetrics(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var meterProvider = provider.GetRequiredService<MeterProvider>();

        var observed = new ConcurrentQueue<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == SmartPipeDiagnostics.MeterName
                    && instrument.Name == "smartpipe.items.processed")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed.Enqueue(value));
        listener.Start();

        new SmartPipeMetricsRecorder().RecordProcessed(1.5);
        meterProvider.ForceFlush();

        observed.Should().ContainSingle().Which.Should().Be(1);
        exported.Single(snapshot => snapshot.Name == "smartpipe.items.processed")
            .MetricPoints.Sum(point => point.GetSumLong()).Should().Be(1);
    }

    [Fact]
    public async Task DefinitionPipelines_ExportSeparateSeriesPerPipelineId()
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithMetrics(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var meterProvider = provider.GetRequiredService<MeterProvider>();

        await using (var orders = await SdkPipelineFixtures.StartDefinitionAsync(
            "orders", 2, TestContext.Current.CancellationToken))
            await orders.Completion;
        await using (var invoices = await SdkPipelineFixtures.StartDefinitionAsync(
            "invoices", 3, TestContext.Current.CancellationToken))
            await invoices.Completion;

        meterProvider.ForceFlush();

        var processed = exported.Single(snapshot => snapshot.Name == "smartpipe.items.processed");
        var byPipeline = processed.MetricPoints
            .GroupBy(point => PipelineIdOf(point))
            .ToDictionary(group => group.Key ?? "<untagged>", group => group.Sum(point => point.GetSumLong()));
        byPipeline.Should().Contain("orders", 2).And.Contain("invoices", 3);
    }

    [Fact]
    public async Task RepeatedRunsOfOnePipeline_ShareOneSeriesAndCarryOnlyThePipelineTag()
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithMetrics(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var meterProvider = provider.GetRequiredService<MeterProvider>();

        for (var run = 0; run < 3; run++)
        {
            await using var pipelineRun = await SdkPipelineFixtures.StartDefinitionAsync(
                "orders-repeated", 2, TestContext.Current.CancellationToken);
            await pipelineRun.Completion;
        }

        meterProvider.ForceFlush();

        var processed = exported.Single(snapshot => snapshot.Name == "smartpipe.items.processed");
        processed.MetricPoints.Where(point => PipelineIdOf(point) == "orders-repeated")
            .Should().ContainSingle().Which.GetSumLong().Should().Be(6);
        foreach (var snapshot in exported)
        {
            foreach (var point in snapshot.MetricPoints)
            {
                foreach (var tag in point.Tags)
                    tag.Key.Should().Be("smartpipe.pipeline_id", "{0} must not carry run-level tags", snapshot.Name);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task StablePipelineIds_ProduceExactlyOneSeriesPerId(int pipelineCount)
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithMetrics(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var meterProvider = provider.GetRequiredService<MeterProvider>();
        var prefix = $"cardinality-{pipelineCount}-";

        for (var round = 0; round < 2; round++)
        {
            for (var index = 0; index < pipelineCount; index++)
            {
                await using var run = await SdkPipelineFixtures.StartDefinitionAsync(
                    $"{prefix}{index:D3}", 1, TestContext.Current.CancellationToken);
                await run.Completion;
            }
        }

        meterProvider.ForceFlush();

        var processed = exported.Single(snapshot => snapshot.Name == "smartpipe.items.processed");
        var series = processed.MetricPoints
            .Where(point => PipelineIdOf(point)?.StartsWith(prefix, StringComparison.Ordinal) == true)
            .ToArray();
        series.Should().HaveCount(pipelineCount);
        series.Should().OnlyContain(point => point.GetSumLong() == 2);
    }

    private static string? PipelineIdOf(MetricPoint point)
    {
        foreach (var tag in point.Tags)
        {
            if (tag.Key == "smartpipe.pipeline_id")
                return tag.Value as string;
        }

        return null;
    }

    [Fact]
    public async Task ExportedMetricPoints_WithoutExplicitPipelineId_CarryNoTags()
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithMetrics(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var meterProvider = provider.GetRequiredService<MeterProvider>();
        var recorder = new SmartPipeMetricsRecorder();
        recorder.RecordProcessed(1.5);
        recorder.RecordFailed();
        recorder.RecordOutputDropped();

        await using (var run = SdkPipelineFixtures.StartPipeline(1, TestContext.Current.CancellationToken))
            await run.Completion;

        meterProvider.ForceFlush();

        foreach (var snapshot in exported)
        {
            foreach (var point in snapshot.MetricPoints)
            {
                point.Tags.Count.Should().Be(0, "{0} must remain zero-tag", snapshot.Name);
            }
        }
    }

    [Fact]
    public async Task MultiplePipelineRuns_DoNotCreateAdditionalMeterStreams()
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithMetrics(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var meterProvider = provider.GetRequiredService<MeterProvider>();

        await using (var first = SdkPipelineFixtures.StartPipeline(2, TestContext.Current.CancellationToken))
            await first.Completion;
        await using (var second = SdkPipelineFixtures.StartPipeline(2, TestContext.Current.CancellationToken))
            await second.Completion;

        meterProvider.ForceFlush();

        exported.Count(snapshot => snapshot.Name == "smartpipe.items.processed").Should().Be(1);
        var processed = exported.Single(snapshot => snapshot.Name == "smartpipe.items.processed");
        processed.MeterName.Should().Be(SmartPipeDiagnostics.MeterName);
        processed.MetricPoints.Sum(point => point.GetSumLong()).Should().Be(4);
    }
}

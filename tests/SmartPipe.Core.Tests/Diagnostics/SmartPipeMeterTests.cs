#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Diagnostics;

public class SmartPipeMeterTests
{
    private static readonly HashSet<string> ForbiddenMeterTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "trace_id",
        "traceid",
        "run_id",
        "runid",
        "item_id",
        "itemid",
        "payload",
        "exception.message",
        "error.message",
        "message",
        "user",
        "user_data",
    };

    [Fact]
    public void SmartPipeMetrics_ShouldPublishStableMeterInstrumentsThroughMeterListener()
    {
        var measurements = new ConcurrentQueue<MeterMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SmartPipe.Core")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            measurements.Enqueue(MeterMeasurement.From(instrument, measurement, tags)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            measurements.Enqueue(MeterMeasurement.From(instrument, measurement, tags)));
        listener.Start();

        var metrics = new SmartPipeMetrics();
        metrics.RecordProcessed(12.5);
        metrics.RecordFailed();
        metrics.RecordDuplicate();
        metrics.RecordRetry();

        var snapshot = measurements.ToArray();
        snapshot.Should().Contain(m => m.MeterName == "SmartPipe.Core");
        snapshot.Should().Contain(m => m.InstrumentName == "smartpipe.items.processed" && m.Unit == "items");
        snapshot.Should().Contain(m => m.InstrumentName == "smartpipe.items.failed" && m.Unit == "items");
        snapshot.Should().Contain(m => m.InstrumentName == "smartpipe.duplicates.filtered" && m.Unit == "items");
        snapshot.Should().Contain(m => m.InstrumentName == "smartpipe.retries" && m.Unit == "retries");
        snapshot.Should().Contain(m => m.InstrumentName == "smartpipe.latency" && m.Unit == "ms");

        snapshot.SelectMany(m => m.Tags).Where(tag => !IsAllowedMeterTag(tag)).Should().BeEmpty();
    }

    private static bool IsAllowedMeterTag(KeyValuePair<string, object?> tag)
    {
        if (ForbiddenMeterTagNames.Contains(tag.Key))
            return false;

        return tag.Value is not string value || !LooksLikeHighCardinalityValue(value);
    }

    private static bool LooksLikeHighCardinalityValue(string value) =>
        value.StartsWith("trace-", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("run-", StringComparison.OrdinalIgnoreCase)
        || value.Length > 64;

    private readonly record struct MeterMeasurement(
        string MeterName,
        string InstrumentName,
        string? Unit,
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public static MeterMeasurement From<T>(
            Instrument instrument,
            T value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct, IConvertible
        {
            var tagSnapshot = tags.ToArray();
            return new MeterMeasurement(
                instrument.Meter.Name,
                instrument.Name,
                instrument.Unit,
                value.ToDouble(null),
                tagSnapshot);
        }
    }
}

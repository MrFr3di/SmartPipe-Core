#nullable enable

using System.Reflection;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class SmartPipeMetricsRecorderTests
{
    [Fact]
    public async Task Metrics_ConcurrentRecordProcessed_ProducesCorrectCounters()
    {
        var recorder = new SmartPipeMetricsRecorder();
        var workers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 1_000; i++)
                    recorder.RecordProcessed(2.5);
            }))
            .ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));

        recorder.CaptureSnapshot().ItemsProcessed.Should().Be(8_000);
    }

    [Fact]
    public void Metrics_SnapshotIsImmutable()
    {
        typeof(SmartPipeMetricsSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Should()
            .OnlyContain(property => property.SetMethod == null);
    }

    [Fact]
    public void Metrics_QueueDepthReflectsInputOutputQueues()
    {
        var recorder = new SmartPipeMetricsRecorder();

        recorder.UpdateQueueDepths(inputQueueDepth: 3, outputQueueDepth: 5);

        var snapshot = recorder.CaptureSnapshot();
        snapshot.InputQueueDepth.Should().Be(3);
        snapshot.OutputQueueDepth.Should().Be(5);
    }

    [Fact]
    public void Metrics_LastProcessedUtc_UpdatesAfterSuccess()
    {
        var recorder = new SmartPipeMetricsRecorder();

        recorder.RecordProcessed(10.0);

        recorder.CaptureSnapshot().LastProcessedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Metrics_NoPublicMutableFields()
    {
        typeof(SmartPipeMetrics)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Should()
            .BeEmpty();
    }
}

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeMetricsTests
{
    [Fact]
    public void Default_ShouldHaveZeroValues()
    {
        var metrics = new SmartPipeMetrics();
        metrics.ItemsProcessed.Should().Be(0);
        metrics.ItemsFailed.Should().Be(0);
        metrics.DuplicatesFiltered.Should().Be(0);
        metrics.Retries.Should().Be(0);
        metrics.AvgLatencyMs.Should().Be(0);
    }

    [Fact]
    public void RecordProcessed_ShouldIncrementCounters()
    {
        var metrics = new SmartPipeMetrics();
        metrics.RecordProcessed(50.0);

        metrics.ItemsProcessed.Should().Be(1);
        metrics.AvgLatencyMs.Should().Be(50.0);
    }

    [Fact]
    public void RecordFailed_ShouldIncrementFailedCounter()
    {
        var metrics = new SmartPipeMetrics();
        metrics.RecordFailed();
        metrics.ItemsFailed.Should().Be(1);
    }

    [Fact]
    public void RecordDuplicate_ShouldIncrementDuplicates()
    {
        var metrics = new SmartPipeMetrics();
        metrics.RecordDuplicate();
        metrics.DuplicatesFiltered.Should().Be(1);
    }

    [Fact]
    public void RecordRetry_ShouldIncrementRetries()
    {
        var metrics = new SmartPipeMetrics();
        metrics.RecordRetry();
        metrics.Retries.Should().Be(1);
    }

    [Fact]
    public void CaptureSnapshot_ShouldReturnStableSampledView()
    {
        var metrics = new SmartPipeMetrics();
        metrics.UpdateSmoothing(smoothLatencyMs: 12.5, smoothThroughput: 20.25);
        metrics.UpdateQueueSize(3);
        metrics.RecordPoolHitRate(0.75);

        metrics.RecordProcessed(10.0);
        metrics.RecordFailed();
        metrics.RecordDuplicate();
        metrics.RecordRetry();

        var snapshot = metrics.CaptureSnapshot();

        snapshot.ItemsProcessed.Should().Be(1);
        snapshot.ItemsFailed.Should().Be(1);
        snapshot.DuplicatesFiltered.Should().Be(1);
        snapshot.Retries.Should().Be(1);
        snapshot.AvgLatencyMs.Should().Be(10.0);
        snapshot.SmoothLatencyMs.Should().Be(12.5);
        snapshot.SmoothThroughput.Should().Be(20.25);
        snapshot.QueueSize.Should().Be(3);
        snapshot.PoolHitRate.Should().Be(0.75);
    }
}

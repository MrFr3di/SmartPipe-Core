using System;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>
/// Adaptive backpressure with dual thresholds to prevent oscillation.
/// PauseThreshold: stop producer. ResumeThreshold: allow producer again.
/// Based on System.IO.Pipelines pattern.
/// </summary>
public class BackpressureStrategy
{
    private readonly int _capacity;

    /// <summary>Stop producer at this fill ratio. Default: 0.80.</summary>
    public double PauseThreshold { get; set; } = 0.80;

    /// <summary>Resume producer when queue drops below this. Default: 0.50.</summary>
    public double ResumeThreshold { get; set; } = 0.50;

    /// <summary>Critical: drop oldest at this fill ratio. Default: 0.95.</summary>
    public double CriticalThreshold { get; set; } = 0.95;

    private volatile bool _paused;

    public bool IsPaused => _paused;

    public BackpressureStrategy(int capacity) => _capacity = capacity;

    /// <summary>Update thresholds based on throughput.</summary>
    public void UpdateThroughput(double throughputPerSec)
    {
        if (throughputPerSec > 1000) { PauseThreshold = 0.90; ResumeThreshold = 0.60; CriticalThreshold = 0.98; }
        else if (throughputPerSec < 100) { PauseThreshold = 0.50; ResumeThreshold = 0.30; CriticalThreshold = 0.80; }
        else { PauseThreshold = 0.80; ResumeThreshold = 0.50; CriticalThreshold = 0.95; }
    }

    public double GetFillRatio(int currentSize) => (double)currentSize / _capacity;

    public bool ShouldPause(int currentSize)
    {
        double fill = GetFillRatio(currentSize);
        if (fill >= PauseThreshold) _paused = true;
        else if (fill <= ResumeThreshold) _paused = false;
        return _paused;
    }

    public bool IsCritical(int currentSize) => GetFillRatio(currentSize) >= CriticalThreshold;

    public async ValueTask ThrottleAsync(int currentSize, CancellationToken ct)
    {
        if (ShouldPause(currentSize))
            await Task.Delay((int)(GetFillRatio(currentSize) * 100), ct);
    }
}

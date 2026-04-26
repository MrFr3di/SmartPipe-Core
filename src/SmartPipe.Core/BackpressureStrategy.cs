using System;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>Adaptive backpressure based on queue fill ratio using watermark algorithm.</summary>
public class BackpressureStrategy
{
    private readonly int _capacity;

    /// <summary>High watermark: start throttling at this fill ratio (default: 0.80).</summary>
    public double HighWatermark { get; init; } = 0.80;

    /// <summary>Critical watermark: drop oldest at this fill ratio (default: 0.95).</summary>
    public double CriticalWatermark { get; init; } = 0.95;

    /// <summary>Low watermark: return to normal at this fill ratio (default: 0.50).</summary>
    public double LowWatermark { get; init; } = 0.50;

    /// <summary>Create strategy for given capacity.</summary>
    /// <param name="capacity">Total channel capacity.</param>
    public BackpressureStrategy(int capacity) => _capacity = capacity;

    /// <summary>Get current fill ratio.</summary>
    /// <param name="currentSize">Current queue size.</param>
    /// <returns>Fill ratio in [0.0, 1.0].</returns>
    public double GetFillRatio(int currentSize) => (double)currentSize / _capacity;

    /// <summary>Check if throttling should be active.</summary>
    /// <param name="currentSize">Current queue size.</param>
    /// <returns>True if queue is above high watermark.</returns>
    public bool ShouldThrottle(int currentSize) => GetFillRatio(currentSize) >= HighWatermark;

    /// <summary>Check if critical (drop) mode should be active.</summary>
    /// <param name="currentSize">Current queue size.</param>
    /// <returns>True if queue is above critical watermark.</returns>
    public bool IsCritical(int currentSize) => GetFillRatio(currentSize) >= CriticalWatermark;

    /// <summary>Apply throttling delay proportional to fill ratio.</summary>
    /// <param name="currentSize">Current queue size.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask ThrottleAsync(int currentSize, CancellationToken ct)
    {
        double fill = GetFillRatio(currentSize);
        if (fill >= HighWatermark)
            await Task.Delay((int)(fill * 100), ct);
    }
}

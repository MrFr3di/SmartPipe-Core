using System;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>Adaptive backpressure with dynamic watermarks based on throughput.</summary>
public class BackpressureStrategy
{
    private readonly int _capacity;

    public double HighWatermark { get; private set; } = 0.80;
    public double CriticalWatermark { get; private set; } = 0.95;

    public BackpressureStrategy(int capacity) => _capacity = capacity;

    /// <summary>Update watermarks based on throughput (items/sec).</summary>
    public void UpdateThroughput(double throughputPerSec)
    {
        if (throughputPerSec > 1000) { HighWatermark = 0.90; CriticalWatermark = 0.98; }
        else if (throughputPerSec < 100) { HighWatermark = 0.50; CriticalWatermark = 0.80; }
        else { HighWatermark = 0.80; CriticalWatermark = 0.95; }
    }

    public double GetFillRatio(int currentSize) => (double)currentSize / _capacity;
    public bool ShouldThrottle(int currentSize) => GetFillRatio(currentSize) >= HighWatermark;
    public bool IsCritical(int currentSize) => GetFillRatio(currentSize) >= CriticalWatermark;

    public async ValueTask ThrottleAsync(int currentSize, CancellationToken ct)
    {
        double fill = GetFillRatio(currentSize);
        if (fill >= HighWatermark)
            await Task.Delay((int)(fill * 100), ct);
    }
}

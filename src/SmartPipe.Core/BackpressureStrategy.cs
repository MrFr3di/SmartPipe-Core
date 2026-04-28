using System;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>
/// P-controller based backpressure. Smoothly adjusts delay proportional to queue fill error.
/// </summary>
public class BackpressureStrategy
{
    private readonly int _capacity;
    private double _targetFillRatio = 0.70;
    private const double Kp = 1.0;

    public BackpressureStrategy(int capacity) => _capacity = capacity;

    public void UpdateThroughput(double throughputPerSec, double predictedLatencyMs = 0)
    {
        if (throughputPerSec > 1000) _targetFillRatio = 0.50;
        else if (throughputPerSec < 100) _targetFillRatio = 0.85;
        else _targetFillRatio = 0.70;

        if (predictedLatencyMs > 50) _targetFillRatio = Math.Max(0.30, _targetFillRatio - 0.10);
    }

    public double GetFillRatio(int currentSize) => (double)currentSize / _capacity;

    public async ValueTask ThrottleAsync(int currentSize, CancellationToken ct)
    {
        double fillRatio = GetFillRatio(currentSize);
        double error = fillRatio - _targetFillRatio;
        
        if (error <= 0) return; // Below target — no throttling

        double delayMs = Kp * error * 100;
        delayMs = Math.Max(0, Math.Min(delayMs, 200));
        if (delayMs > 1)
            await Task.Delay((int)delayMs, ct);
    }

    [Obsolete("Use ThrottleAsync instead.")]
    public bool ShouldPause(int _) => false;

    [Obsolete("Use continuous throttling via ThrottleAsync.")]
    public bool IsCritical(int currentSize) => GetFillRatio(currentSize) >= 0.95;
}

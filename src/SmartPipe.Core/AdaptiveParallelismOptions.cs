#nullable enable

namespace SmartPipe.Core;

/// <summary>Configures opt-in adaptive parallelism for the typed pipeline runtime.</summary>
public sealed class AdaptiveParallelismOptions
{
    /// <summary>Gets a value indicating whether adaptive parallelism is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the minimum adaptive concurrency limit.</summary>
    public int MinConcurrency { get; init; } = 1;

    /// <summary>Gets the maximum adaptive concurrency limit.</summary>
    public int MaxConcurrency { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>Gets the initial adaptive concurrency limit.</summary>
    public int InitialConcurrency { get; init; } = 1;

    /// <summary>Gets the desired per-envelope processing latency.</summary>
    public TimeSpan TargetLatency { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gets the latency range around the target where no adjustment is made.</summary>
    public TimeSpan DeadZone { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Gets the minimum elapsed time between adaptive limit changes.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the maximum concurrency limit change allowed per controller decision.</summary>
    public int MaxAdjustmentStep { get; init; } = 1;

    /// <summary>Gets the failure or retry ratio that prevents growth and reduces concurrency.</summary>
    public double FailurePressureThreshold { get; init; } = 0.10;

    /// <summary>Gets the minimum smoothing factor used for latency samples.</summary>
    public double MinSmoothingFactor { get; init; } = 0.2;

    internal void Validate()
    {
        if (MinConcurrency < 1)
            throw new ArgumentOutOfRangeException(
                nameof(MinConcurrency),
                MinConcurrency,
                "Minimum adaptive concurrency must be greater than or equal to one.");

        if (MaxConcurrency < MinConcurrency)
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrency),
                MaxConcurrency,
                "Maximum adaptive concurrency must be greater than or equal to minimum adaptive concurrency.");

        if (InitialConcurrency < MinConcurrency || InitialConcurrency > MaxConcurrency)
            throw new ArgumentOutOfRangeException(
                nameof(InitialConcurrency),
                InitialConcurrency,
                "Initial adaptive concurrency must be within the configured min/max range.");

        if (TargetLatency <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(TargetLatency),
                TargetLatency,
                "Adaptive target latency must be greater than zero.");

        if (DeadZone <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(DeadZone),
                DeadZone,
                "Adaptive dead zone must be greater than zero.");

        if (Cooldown <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(Cooldown),
                Cooldown,
                "Adaptive cooldown must be greater than zero.");

        if (MaxAdjustmentStep < 1)
            throw new ArgumentOutOfRangeException(
                nameof(MaxAdjustmentStep),
                MaxAdjustmentStep,
                "Adaptive maximum adjustment step must be greater than or equal to one.");

        if (!(FailurePressureThreshold > 0 && FailurePressureThreshold <= 1))
            throw new ArgumentOutOfRangeException(
                nameof(FailurePressureThreshold),
                FailurePressureThreshold,
                "Adaptive failure pressure threshold must be greater than zero and less than or equal to one.");

        if (!(MinSmoothingFactor > 0 && MinSmoothingFactor <= 1))
            throw new ArgumentOutOfRangeException(
                nameof(MinSmoothingFactor),
                MinSmoothingFactor,
                "Adaptive minimum smoothing factor must be greater than zero and less than or equal to one.");
    }
}

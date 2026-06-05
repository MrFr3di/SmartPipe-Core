#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>Configures opt-in adaptive parallelism for the legacy channel runtime.</summary>
public sealed class AdaptiveParallelismOptions
{
    /// <summary>Gets or sets whether adaptive parallelism is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the minimum active lane count.</summary>
    public int MinDegreeOfParallelism { get; set; } = 1;

    /// <summary>Gets or sets the maximum active lane count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>Gets or sets the initial active lane count.</summary>
    public int InitialDegreeOfParallelism { get; set; } = Math.Min(4, Environment.ProcessorCount);

    /// <summary>Gets or sets the initial in-flight item budget.</summary>
    public int InitialInFlightItems { get; set; } = Math.Min(4, Environment.ProcessorCount);

    /// <summary>Gets or sets the maximum in-flight item budget.</summary>
    public int MaxInFlightItems { get; set; } = Environment.ProcessorCount * 4;

    /// <summary>Gets or sets how often the adaptive controller samples runtime state.</summary>
    public TimeSpan SamplingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the minimum time between controller changes.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the active queue pressure threshold for scaling up.</summary>
    public double ScaleUpQueuePressure { get; set; } = 0.75;

    /// <summary>Gets or sets the active queue pressure threshold for scaling down.</summary>
    public double ScaleDownQueuePressure { get; set; } = 0.25;

    /// <summary>Gets or sets the failure rate threshold for scaling down.</summary>
    public double FailureRateScaleDownThreshold { get; set; } = 0.10;

    /// <summary>Validates adaptive parallelism settings against channel runtime constraints.</summary>
    /// <param name="fullMode">The bounded channel full mode configured for the pipeline.</param>
    /// <param name="jumpHashEnabled">Whether JumpHash routing is enabled.</param>
    public void Validate(BoundedChannelFullMode fullMode, bool jumpHashEnabled)
    {
        if (MinDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(
                nameof(MinDegreeOfParallelism),
                MinDegreeOfParallelism,
                "MinDegreeOfParallelism must be greater than or equal to one."
            );

        if (MaxDegreeOfParallelism < MinDegreeOfParallelism)
            throw new ArgumentOutOfRangeException(
                nameof(MaxDegreeOfParallelism),
                MaxDegreeOfParallelism,
                "MaxDegreeOfParallelism must be greater than or equal to MinDegreeOfParallelism."
            );

        if (InitialDegreeOfParallelism < MinDegreeOfParallelism
            || InitialDegreeOfParallelism > MaxDegreeOfParallelism)
            throw new ArgumentOutOfRangeException(
                nameof(InitialDegreeOfParallelism),
                InitialDegreeOfParallelism,
                "InitialDegreeOfParallelism must be between MinDegreeOfParallelism and MaxDegreeOfParallelism."
            );

        if (InitialInFlightItems < InitialDegreeOfParallelism)
            throw new ArgumentOutOfRangeException(
                nameof(InitialInFlightItems),
                InitialInFlightItems,
                "InitialInFlightItems must be greater than or equal to InitialDegreeOfParallelism."
            );

        if (MaxInFlightItems < InitialInFlightItems)
            throw new ArgumentOutOfRangeException(
                nameof(MaxInFlightItems),
                MaxInFlightItems,
                "MaxInFlightItems must be greater than or equal to InitialInFlightItems."
            );

        if (SamplingInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(SamplingInterval),
                SamplingInterval,
                "SamplingInterval must be greater than zero."
            );

        if (Cooldown <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(Cooldown),
                Cooldown,
                "Cooldown must be greater than zero."
            );

        if (Enabled && fullMode != BoundedChannelFullMode.Wait)
            throw new InvalidOperationException(
                "Adaptive parallelism requires FullMode to be BoundedChannelFullMode.Wait."
            );

        if (Enabled && jumpHashEnabled)
            throw new InvalidOperationException(
                "Adaptive parallelism cannot be combined with JumpHash until routing occurs before lane writes."
            );
    }
}

namespace SmartPipe.Extensions.Hosting;

/// <summary>Configures one pipeline registration hosted by the SmartPipe orchestrator.</summary>
public sealed class SmartPipeHostedPipelineOptions
{
    /// <summary>Gets or sets the pipeline startup order.</summary>
    public int Order { get; set; }

    /// <summary>Gets or sets the maximum graceful drain duration.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how a pipeline fault affects the application.</summary>
    public SmartPipeHostedPipelineFailureBehavior FailureBehavior { get; set; } =
        SmartPipeHostedPipelineFailureBehavior.StopApplication;

    /// <summary>Gets or sets how normal pipeline completion affects the application.</summary>
    public SmartPipeHostedCompletionBehavior CompletionBehavior { get; set; } =
        SmartPipeHostedCompletionBehavior.KeepHostAlive;
}

internal sealed record SmartPipeHostedPipelineOptionsSnapshot(
    int Order,
    TimeSpan DrainTimeout,
    SmartPipeHostedPipelineFailureBehavior FailureBehavior,
    SmartPipeHostedCompletionBehavior CompletionBehavior)
{
    internal static SmartPipeHostedPipelineOptionsSnapshot Create(
        SmartPipeHostedPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(options.FailureBehavior))
            throw new ArgumentOutOfRangeException(
                nameof(options.FailureBehavior),
                options.FailureBehavior,
                "Hosted pipeline failure behavior is invalid.");

        if (!Enum.IsDefined(options.CompletionBehavior))
            throw new ArgumentOutOfRangeException(
                nameof(options.CompletionBehavior),
                options.CompletionBehavior,
                "Hosted pipeline completion behavior is invalid.");

        if (options.DrainTimeout != Timeout.InfiniteTimeSpan
            && options.DrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options.DrainTimeout),
                options.DrainTimeout,
                "Hosted pipeline drain timeout must be positive or infinite.");

        return new(
            options.Order,
            options.DrainTimeout,
            options.FailureBehavior,
            options.CompletionBehavior);
    }
}

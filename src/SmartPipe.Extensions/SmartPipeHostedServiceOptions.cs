namespace SmartPipe.Extensions;

/// <summary>Controls how <see cref="SmartPipeHostedService{TInput,TOutput}"/> handles pipeline faults.</summary>
public enum SmartPipeHostedFailureBehavior
{
    /// <summary>Request host shutdown through <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime"/>.</summary>
    StopApplication,

    /// <summary>Rethrow the pipeline exception from the hosted service execution task.</summary>
    Rethrow,

    /// <summary>Keep the host alive; health monitoring reports the tracked run state.</summary>
    MarkUnhealthyAndKeepHostAlive,

    /// <summary>Log and ignore the pipeline fault.</summary>
    Ignore,
}

/// <summary>Options for typed SmartPipe hosted service lifecycle behavior.</summary>
public sealed class SmartPipeHostedServiceOptions
{
    /// <summary>Gets the behavior used when the hosted pipeline faults.</summary>
    public SmartPipeHostedFailureBehavior FailureBehavior { get; init; } =
        SmartPipeHostedFailureBehavior.StopApplication;

    /// <summary>Gets the maximum duration used by <see cref="SmartPipeHostedService{TInput,TOutput}.StopAsync"/> drain.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates option values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an option value is invalid.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(FailureBehavior))
            throw new ArgumentOutOfRangeException(
                nameof(FailureBehavior),
                FailureBehavior,
                "Hosted service failure behavior is invalid.");

        if (DrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(DrainTimeout),
                DrainTimeout,
                "Hosted service drain timeout must be greater than zero.");
    }
}

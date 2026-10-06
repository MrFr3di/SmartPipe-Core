using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Provides one immutable, bounded terminal observation without retaining a runtime graph.</summary>
public sealed record SmartPipeTerminalRunObservation
{
    private SmartPipeRunIdentity _identity = null!;
    private Type _inputType = null!;
    private Type _outputType = null!;
    private SmartPipeRunObservationOutcome _outcome;
    private SmartPipeMetricsSnapshot _metrics = null!;
    private int _inputCapacity;
    private int _outputCapacity;
    private long _sequence;

    /// <summary>Gets the run identity.</summary>
    public required SmartPipeRunIdentity Identity
    {
        get => _identity;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.PipelineKey.IsEmpty || value.RunId == Guid.Empty)
            {
                throw new ArgumentException("Run identity must contain an initialized key and non-empty RunId.", nameof(value));
            }

            _identity = value;
        }
    }

    /// <summary>Gets the pipeline input type.</summary>
    public required Type InputType
    {
        get => _inputType;
        init => _inputType = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the pipeline output type.</summary>
    public required Type OutputType
    {
        get => _outputType;
        init => _outputType = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the terminal outcome.</summary>
    public required SmartPipeRunObservationOutcome Outcome
    {
        get => _outcome;
        init
        {
            if (!Enum.IsDefined(value) || value == SmartPipeRunObservationOutcome.None)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _outcome = value;
        }
    }

    /// <summary>Gets the UTC run start timestamp.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Gets the UTC terminal timestamp.</summary>
    public required DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>Gets the final immutable metrics value.</summary>
    public required SmartPipeMetricsSnapshot Metrics
    {
        get => _metrics;
        init => _metrics = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the effective input capacity.</summary>
    public required int InputCapacity
    {
        get => _inputCapacity;
        init => _inputCapacity = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Gets the effective output capacity.</summary>
    public required int OutputCapacity
    {
        get => _outputCapacity;
        init => _outputCapacity = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Gets the positive per-key terminal commit sequence.</summary>
    public required long Sequence
    {
        get => _sequence;
        init => _sequence = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
}

#nullable enable

namespace SmartPipe.Core;

/// <summary>Behavior when all timed execution slots are occupied.</summary>
public enum TimedAttemptAdmissionMode
{
    /// <summary>Wait for observation of an outstanding execution, subject to cancellation and stage deadline.</summary>
    Wait = 0,
    /// <summary>Reject the attempt as a permanent admission failure.</summary>
    Reject = 1,
}

/// <summary>Bounds active and detached timed executions together for each run.</summary>
public sealed class TimedAttemptAdmissionOptions
{
    /// <summary>Creates timed execution admission options.</summary>
    /// <param name="maxOutstandingAttempts">Positive limit of active and detached timed executions.</param>
    /// <param name="mode">Behavior at capacity.</param>
    public TimedAttemptAdmissionOptions(int maxOutstandingAttempts, TimedAttemptAdmissionMode mode)
    {
        if (maxOutstandingAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutstandingAttempts));
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        MaxOutstandingAttempts = maxOutstandingAttempts;
        Mode = mode;
    }

    /// <summary>Gets the limit of active and detached timed executions.</summary>
    public int MaxOutstandingAttempts { get; }

    /// <summary>Gets behavior at capacity.</summary>
    public TimedAttemptAdmissionMode Mode { get; }
}

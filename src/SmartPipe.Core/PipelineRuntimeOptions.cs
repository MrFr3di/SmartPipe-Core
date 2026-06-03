#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>Provides time used by envelope-aware pipeline runtime components.</summary>
public interface IPipelineClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTimeOffset GetUtcNow();

    /// <summary>Gets a high-resolution timestamp.</summary>
    long GetTimestamp();

    /// <summary>Gets elapsed time between two timestamps.</summary>
    /// <param name="startingTimestamp">Start timestamp.</param>
    /// <param name="endingTimestamp">End timestamp.</param>
    /// <returns>Elapsed time.</returns>
    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);
}

/// <summary>System clock implementation for pipeline runtime behavior.</summary>
public sealed class SystemPipelineClock : IPipelineClock
{
    /// <summary>Gets the singleton system clock instance.</summary>
    public static SystemPipelineClock Instance { get; } = new();

    private SystemPipelineClock() { }

    /// <inheritdoc />
    public DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    /// <inheritdoc />
    public long GetTimestamp() => TimeProvider.System.GetTimestamp();

    /// <inheritdoc />
    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeProvider.System.GetElapsedTime(startingTimestamp, endingTimestamp);
}

/// <summary>Adapts a <see cref="TimeProvider"/> to <see cref="IPipelineClock"/>.</summary>
public sealed class TimeProviderPipelineClock : IPipelineClock
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a clock backed by a time provider.</summary>
    /// <param name="timeProvider">Time provider to use.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="timeProvider"/> is null.</exception>
    public TimeProviderPipelineClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    /// <inheritdoc />
    public long GetTimestamp() => _timeProvider.GetTimestamp();

    /// <inheritdoc />
    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        _timeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);
}

/// <summary>Observer dispatch mode for envelope-aware pipeline runtime events.</summary>
public enum ObserverDispatchMode
{
    /// <summary>Dispatch events inline on the pipeline execution path.</summary>
    Inline,

    /// <summary>Dispatch events through a bounded best-effort background queue.</summary>
    BufferedBestEffort,

    /// <summary>Dispatch events through a bounded reliable background queue.</summary>
    BufferedReliable,
}

/// <summary>Failure behavior for buffered observer dispatch.</summary>
public enum ObserverFailureMode
{
    /// <summary>Use each observer registration's failure policy.</summary>
    UseRegistrationPolicy,

    /// <summary>Ignore observer failures.</summary>
    Ignore,

    /// <summary>Fault the pipeline when an observer fails.</summary>
    FaultPipeline,
}

/// <summary>Configures observer event dispatch for the envelope-aware runtime.</summary>
public sealed class ObserverDispatchOptions
{
    /// <summary>Gets inline observer dispatch options.</summary>
    public static ObserverDispatchOptions Inline { get; } = new();

    /// <summary>Gets the observer dispatch mode.</summary>
    public ObserverDispatchMode Mode { get; init; } = ObserverDispatchMode.Inline;

    /// <summary>Gets the bounded queue capacity for buffered modes.</summary>
    public int Capacity { get; init; } = 1024;

    /// <summary>Gets the full-mode behavior for buffered observer queues.</summary>
    public BoundedChannelFullMode FullMode { get; init; } = BoundedChannelFullMode.Wait;

    /// <summary>Gets the observer failure handling mode for buffered dispatch.</summary>
    public ObserverFailureMode FailureMode { get; init; } =
        ObserverFailureMode.UseRegistrationPolicy;

    /// <summary>Gets a value indicating whether buffered dispatch should flush during completion.</summary>
    public bool FlushOnCompletion { get; init; } = true;

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode))
            throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Observer dispatch mode is invalid.");

        if (Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Capacity), Capacity, "Observer dispatch capacity must be greater than zero.");

        if (!Enum.IsDefined(FullMode))
            throw new ArgumentOutOfRangeException(nameof(FullMode), FullMode, "Observer dispatch full mode is invalid.");

        if (!Enum.IsDefined(FailureMode))
            throw new ArgumentOutOfRangeException(nameof(FailureMode), FailureMode, "Observer failure mode is invalid.");

        if (Mode == ObserverDispatchMode.BufferedReliable && !FlushOnCompletion)
            throw new ArgumentException("BufferedReliable requires FlushOnCompletion = true.");
    }
}

/// <summary>Options for the envelope-aware pipeline runtime.</summary>
public sealed class PipelineRuntimeOptions
{
    /// <summary>Gets the optional output channel capacity. Null preserves the default unbounded output behavior.</summary>
    public int? OutputCapacity { get; init; } = null;

    /// <summary>Gets the bounded output channel full mode when <see cref="OutputCapacity"/> is configured.</summary>
    public BoundedChannelFullMode OutputFullMode { get; init; } = BoundedChannelFullMode.Wait;

    /// <summary>Gets observer dispatch options. Inline dispatch is the default.</summary>
    public ObserverDispatchOptions ObserverDispatch { get; init; } = ObserverDispatchOptions.Inline;

    /// <summary>Gets the runtime clock. The system clock is the default.</summary>
    public IPipelineClock Clock { get; init; } = SystemPipelineClock.Instance;

    internal void Validate()
    {
        if (OutputCapacity is <= 0)
            throw new ArgumentOutOfRangeException(nameof(OutputCapacity), OutputCapacity, "Output capacity must be greater than zero.");

        if (!Enum.IsDefined(OutputFullMode))
            throw new ArgumentOutOfRangeException(nameof(OutputFullMode), OutputFullMode, "Output full mode is invalid.");

        ArgumentNullException.ThrowIfNull(ObserverDispatch);
        ObserverDispatch.Validate();
        ArgumentNullException.ThrowIfNull(Clock);
    }
}

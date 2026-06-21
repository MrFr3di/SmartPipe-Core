#nullable enable
#pragma warning disable CS0618 // Compatibility aliases are intentionally defined and validated here.

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

    /// <summary>Ignore observer failures, including critical and registration-level fault policies.</summary>
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

    /// <summary>Gets the best-effort wait duration before an observer event is counted as dropped.</summary>
    public TimeSpan BestEffortWriteTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gets a value indicating whether buffered dispatch should try to emit observer-drop events.</summary>
    public bool EmitDroppedObserverEvents { get; init; } = true;

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

        if (BestEffortWriteTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(BestEffortWriteTimeout),
                BestEffortWriteTimeout,
                "Best-effort observer write timeout must not be negative.");
    }
}

/// <summary>Controls which processing results are emitted to the typed pipeline output channel.</summary>
/// <remarks>
/// Output mode filtering applies only at the output channel write boundary.
/// Sink writes, observer events, retry, and failure routing are independent of output mode.
/// </remarks>
[Obsolete("Use PipelineOutputPolicy. PipelineOutputMode is a compatibility surface and will be removed in a future major version.")]
public enum PipelineOutputMode
{
    /// <summary>Emit all processing results — successful and failed — to the output channel.</summary>
    EmitAll = 0,

    /// <summary>When a sink is attached, emit only failure results to the output channel.
    /// When no sink is attached, emit all results.</summary>
    FailuresOnlyWhenSinkAttached = 1,

    /// <summary>When a sink is attached, suppress all results from the output channel.
    /// When no sink is attached, emit all results.</summary>
    SuppressWhenSinkAttached = 2,

    /// <summary>Suppress all results from the output channel. Sink writes are unaffected.</summary>
    SuppressAll = 3,
}

/// <summary>Controls which processing results are emitted to the typed pipeline output channel.</summary>
public enum PipelineOutputPolicy
{
    /// <summary>Emit all processing results to the output channel.</summary>
    EmitAll = 0,

    /// <summary>Emit only failure results to the output channel.</summary>
    EmitFailuresOnly = 1,

    /// <summary>When a sink is attached, suppress successful results from the output channel.</summary>
    SuppressSuccessWhenSinkAttached = 2,

    /// <summary>When a sink is attached, suppress all output channel results.</summary>
    SuppressAllWhenSinkAttached = 3,
}

/// <summary>Controls cross-envelope output ordering for the typed runtime.</summary>
public enum PipelineOrderingMode
{
    /// <summary>Allow outputs to be emitted as envelopes complete.</summary>
    Unordered = 0,

    /// <summary>Preserve input order. Parallel preserving is not implemented yet.</summary>
    [Obsolete("Not supported. Use sequential processing.")]
    PreserveInputOrder = 1,
}

/// <summary>Options for the envelope-aware pipeline runtime.</summary>
public sealed class PipelineRuntimeOptions
{
    private PipelineOutputMode _outputMode = PipelineOutputMode.EmitAll;
    private PipelineOutputPolicy _outputPolicy =
        PipelineOutputPolicy.SuppressSuccessWhenSinkAttached;

    /// <summary>Gets the maximum number of typed envelopes processed concurrently.</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>Gets the bounded typed input channel capacity.</summary>
    public int InputCapacity { get; init; } = 1024;

    /// <summary>Gets the typed input channel full mode.</summary>
    public BoundedChannelFullMode InputFullMode { get; init; } = BoundedChannelFullMode.Wait;

    /// <summary>Gets the optional output channel capacity. Null uses the bounded runtime default.</summary>
    public int? OutputCapacity { get; init; } = null;

    /// <summary>Gets the bounded output channel full mode.</summary>
    public BoundedChannelFullMode OutputFullMode { get; init; } = BoundedChannelFullMode.Wait;

    /// <summary>Gets the compatibility output filtering mode. Prefer <see cref="OutputPolicy"/>.</summary>
    [Obsolete("Use OutputPolicy. OutputMode is a compatibility alias and will be removed in a future major version.")]
    public PipelineOutputMode OutputMode
    {
        get => _outputMode;
        init
        {
            _outputMode = value;
            IsOutputModeConfigured = true;
        }
    }

    /// <summary>Gets the maximum number of typed envelopes processed concurrently.</summary>
    /// <remarks>
    /// Compatibility name for 1.1 callers. New typed-only code should prefer
    /// <see cref="MaxConcurrency"/>.
    /// </remarks>
    [Obsolete("Use MaxConcurrency. This compatibility alias will be removed in a future major version.")]
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>Gets the typed output filtering policy.</summary>
    public PipelineOutputPolicy OutputPolicy
    {
        get => _outputPolicy;
        init
        {
            _outputPolicy = value;
            IsOutputPolicyConfigured = true;
        }
    }

    /// <summary>Gets the typed output ordering mode.</summary>
    public PipelineOrderingMode OrderingMode { get; init; } = PipelineOrderingMode.Unordered;

    /// <summary>Gets observer dispatch options. Inline dispatch is the default.</summary>
    public ObserverDispatchOptions ObserverDispatch { get; init; } = ObserverDispatchOptions.Inline;

    /// <summary>Gets opt-in adaptive parallelism options. Disabled by default.</summary>
    public AdaptiveParallelismOptions AdaptiveParallelism { get; init; } = new();

    /// <summary>Gets the runtime clock. The system clock is the default.</summary>
    public IPipelineClock Clock { get; init; } = SystemPipelineClock.Instance;

    internal bool IsOutputModeConfigured { get; private init; }

    internal bool IsOutputPolicyConfigured { get; private init; }

    internal bool UseCompatibilityOutputMode =>
        IsOutputModeConfigured && !IsOutputPolicyConfigured;

    internal int EffectiveMaxConcurrency =>
        MaxConcurrency != 1 ? MaxConcurrency : MaxDegreeOfParallelism;

    internal void Validate()
    {
        if (MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrency),
                MaxConcurrency,
                "Max concurrency must be greater than zero.");

        if (InputCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(InputCapacity), InputCapacity, "Input capacity must be greater than zero.");

        if (!Enum.IsDefined(InputFullMode))
            throw new ArgumentOutOfRangeException(nameof(InputFullMode), InputFullMode, "Input full mode is invalid.");

        if (OutputCapacity is <= 0)
            throw new ArgumentOutOfRangeException(nameof(OutputCapacity), OutputCapacity, "Output capacity must be greater than zero.");

        if (!Enum.IsDefined(OutputFullMode))
            throw new ArgumentOutOfRangeException(nameof(OutputFullMode), OutputFullMode, "Output full mode is invalid.");

        if (!Enum.IsDefined(OutputMode))
            throw new ArgumentOutOfRangeException(nameof(OutputMode), OutputMode, "Output mode is invalid.");

        if (MaxDegreeOfParallelism <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxDegreeOfParallelism),
                MaxDegreeOfParallelism,
                "Max degree of parallelism must be greater than zero.");

        if (MaxConcurrency != 1 && MaxDegreeOfParallelism != 1 && MaxConcurrency != MaxDegreeOfParallelism)
            throw new InvalidOperationException(
                "MaxConcurrency and MaxDegreeOfParallelism cannot specify different non-default values.");

        if (!Enum.IsDefined(OutputPolicy))
            throw new ArgumentOutOfRangeException(nameof(OutputPolicy), OutputPolicy, "Output policy is invalid.");

        if (IsOutputModeConfigured
            && IsOutputPolicyConfigured
            && !AreEquivalent(OutputMode, OutputPolicy))
        {
            throw new InvalidOperationException(
                "OutputMode and OutputPolicy cannot specify different output behavior.");
        }

        if (!Enum.IsDefined(OrderingMode))
            throw new ArgumentOutOfRangeException(nameof(OrderingMode), OrderingMode, "Ordering mode is invalid.");

        if (OrderingMode == PipelineOrderingMode.PreserveInputOrder && EffectiveMaxConcurrency > 1)
            throw new NotSupportedException(
                "PreserveInputOrder with MaxConcurrency > 1 is not supported in this runtime version.");

        ArgumentNullException.ThrowIfNull(ObserverDispatch);
        ObserverDispatch.Validate();
        ArgumentNullException.ThrowIfNull(AdaptiveParallelism);
        AdaptiveParallelism.Validate();
        if (AdaptiveParallelism.Enabled && InputFullMode != BoundedChannelFullMode.Wait)
            throw new InvalidOperationException("Adaptive parallelism requires InputFullMode = Wait.");

        if (AdaptiveParallelism.Enabled)
        {
            var effectiveAdaptiveMax = Math.Min(
                EffectiveMaxConcurrency,
                AdaptiveParallelism.MaxConcurrency);
            if (AdaptiveParallelism.MinConcurrency > effectiveAdaptiveMax)
                throw new InvalidOperationException(
                    "Minimum adaptive concurrency cannot exceed the effective adaptive maximum.");
        }

        ArgumentNullException.ThrowIfNull(Clock);
    }

    private static bool AreEquivalent(PipelineOutputMode mode, PipelineOutputPolicy policy) =>
        mode == PipelineOutputMode.EmitAll && policy == PipelineOutputPolicy.EmitAll;
}

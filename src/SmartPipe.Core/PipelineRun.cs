#nullable enable

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>Represents one running pipeline instance.</summary>
/// <typeparam name="TOutput">Output payload type.</typeparam>
/// <remarks>
/// A pipeline run is single-use. It owns the runtime completion signal and exposes one primary
/// output stream. Result-only consumption is provided as a projection through
/// <see cref="ReadResultsAsync"/>.
/// </remarks>
public sealed class PipelineRun<TOutput> : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask>? _cancel;
    private readonly Func<TimeSpan, CancellationToken, ValueTask>? _drain;
    private readonly Func<TimeSpan, CancellationToken, ValueTask<PipelineDrainResult>>? _tryDrain;
    private readonly Func<CancellationToken, ValueTask>? _abort;
    private readonly Func<ValueTask>? _dispose;
    private readonly Func<SmartPipeMetricsSnapshot>? _metricsProvider;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    /// <summary>Creates a pipeline run handle.</summary>
    /// <param name="outputs">Primary output reader.</param>
    /// <param name="completion">Task that completes when the run finishes.</param>
    /// <param name="stateProvider">Function returning the current state.</param>
    /// <param name="cancel">Cancellation delegate.</param>
    /// <param name="drain">Drain delegate.</param>
    /// <param name="abort">Abort delegate.</param>
    /// <param name="dispose">Dispose delegate.</param>
    /// <exception cref="ArgumentNullException">Thrown when required arguments are null.</exception>
    public PipelineRun(
        ChannelReader<PipelineOutput<TOutput>> outputs,
        Task completion,
        Func<PipelineRunState> stateProvider,
        Func<CancellationToken, ValueTask>? cancel = null,
        Func<TimeSpan, CancellationToken, ValueTask>? drain = null,
        Func<CancellationToken, ValueTask>? abort = null,
        Func<ValueTask>? dispose = null
    )
        : this(
            outputs,
            completion,
            stateProvider,
            cancel,
            drain,
            tryDrain: null,
            abort,
            dispose,
            metricsProvider: null)
    {
    }

    internal PipelineRun(
        ChannelReader<PipelineOutput<TOutput>> outputs,
        Task completion,
        Func<PipelineRunState> stateProvider,
        Func<CancellationToken, ValueTask>? cancel,
        Func<TimeSpan, CancellationToken, ValueTask>? drain,
        Func<TimeSpan, CancellationToken, ValueTask<PipelineDrainResult>>? tryDrain,
        Func<CancellationToken, ValueTask>? abort,
        Func<ValueTask>? dispose,
        Func<SmartPipeMetricsSnapshot>? metricsProvider
    ) : this(
        outputs,
        completion,
        stateProvider,
        cancel,
        drain,
        tryDrain,
        abort,
        dispose,
        metricsProvider,
        default,
        Guid.Empty)
    {
    }

    internal PipelineRun(
        ChannelReader<PipelineOutput<TOutput>> outputs,
        Task completion,
        Func<PipelineRunState> stateProvider,
        Func<CancellationToken, ValueTask>? cancel,
        Func<TimeSpan, CancellationToken, ValueTask>? drain,
        Func<TimeSpan, CancellationToken, ValueTask<PipelineDrainResult>>? tryDrain,
        Func<CancellationToken, ValueTask>? abort,
        Func<ValueTask>? dispose,
        Func<SmartPipeMetricsSnapshot>? metricsProvider,
        PipelineKey pipelineKey,
        Guid runId
    )
    {
        Outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));
        Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _cancel = cancel;
        _drain = drain;
        _tryDrain = tryDrain;
        _abort = abort;
        _dispose = dispose;
        _metricsProvider = metricsProvider;
        if (pipelineKey.IsEmpty)
        {
            if (runId != Guid.Empty)
                throw new ArgumentException("A run identifier requires a pipeline key.", nameof(runId));
        }
        else
        {
            PipelineKeyGuard.ThrowIfInvalid(pipelineKey, nameof(pipelineKey));
            if (runId == Guid.Empty)
                throw new ArgumentException("RunId must not be empty.", nameof(runId));
        }

        PipelineKey = pipelineKey;
        RunId = runId;
    }

    /// <summary>
    /// Creates a run handle over the same output stream and runtime controls with a replacement completion and disposal lifetime.
    /// </summary>
    /// <param name="completion">Replacement completion task for the returned run handle.</param>
    /// <param name="dispose">Replacement disposal delegate for the returned run handle.</param>
    /// <returns>
    /// A run handle that preserves this run's outputs, state, cancellation, drain, abort,
    /// structured drain, and metrics delegates while using the supplied completion and disposal lifetime.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="completion"/> or <paramref name="dispose"/> is null.
    /// </exception>
    public PipelineRun<TOutput> WithLifetime(Task completion, Func<ValueTask> dispose)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(dispose);

        return new PipelineRun<TOutput>(
            Outputs,
            completion,
            _stateProvider,
            _cancel,
            _drain,
            _tryDrain,
            _abort,
            dispose,
            _metricsProvider,
            PipelineKey,
            RunId);
    }

    private readonly Func<PipelineRunState> _stateProvider;

    /// <summary>Gets the primary envelope-aware output stream.</summary>
    public ChannelReader<PipelineOutput<TOutput>> Outputs { get; }

    /// <summary>Gets the task that observes run success, cancellation, or failure.</summary>
    public Task Completion { get; }

    /// <summary>Gets the canonical definition identity for a runtime-created run.</summary>
    /// <remarks>Manually constructed compatibility handles return the default key.</remarks>
    public PipelineKey PipelineKey { get; }

    /// <summary>Gets the canonical run identifier for a runtime-created run.</summary>
    /// <remarks>Manually constructed compatibility handles return <see cref="Guid.Empty"/>.</remarks>
    public Guid RunId { get; }

    /// <summary>Gets the current run state.</summary>
    public PipelineRunState State => _stateProvider();

    /// <summary>Gets an immutable point-in-time metrics snapshot for this run.</summary>
    public SmartPipeMetricsSnapshot Metrics => _metricsProvider?.Invoke() ?? SmartPipeMetricsSnapshot.Empty;

    /// <summary>Reads typed results projected from the primary output stream.</summary>
    /// <param name="ct">Cancellation token for enumeration.</param>
    /// <returns>Async sequence of pipeline results.</returns>
    public async IAsyncEnumerable<PipelineResult<TOutput>> ReadResultsAsync(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await foreach (var output in Outputs.ReadAllAsync(ct).ConfigureAwait(false))
            yield return output.Result;
    }

    /// <summary>Requests cooperative cancellation for the run.</summary>
    /// <param name="ct">Cancellation token for the cancellation request.</param>
    /// <returns>A value task representing cancellation dispatch.</returns>
    public ValueTask CancelAsync(CancellationToken ct = default) =>
        _cancel?.Invoke(ct) ?? ValueTask.CompletedTask;

    /// <summary>Waits for accepted work to complete until the supplied timeout elapses.</summary>
    /// <param name="timeout">Maximum drain duration.</param>
    /// <param name="ct">Cancellation token for the drain request.</param>
    /// <returns>A value task representing the drain request.</returns>
    public ValueTask DrainAsync(TimeSpan timeout, CancellationToken ct = default) =>
        _drain?.Invoke(timeout, ct) ?? ValueTask.CompletedTask;

    /// <summary>Attempts to drain accepted work and returns structured completion status.</summary>
    /// <param name="timeout">Maximum drain duration.</param>
    /// <param name="ct">Cancellation token for the drain request.</param>
    /// <returns>Structured drain result.</returns>
    public ValueTask<PipelineDrainResult> TryDrainAsync(
        TimeSpan timeout,
        CancellationToken ct = default) =>
        _tryDrain?.Invoke(timeout, ct)
        ?? ValueTask.FromResult(new PipelineDrainResult(
            PipelineDrainStatus.AlreadyCompleted,
            State,
            TimeSpan.Zero));

    /// <summary>Requests immediate abort of pending work.</summary>
    /// <param name="ct">Cancellation token for the abort request.</param>
    /// <returns>A value task representing abort dispatch.</returns>
    public ValueTask AbortAsync(CancellationToken ct = default) =>
        _abort?.Invoke(ct) ?? ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_dispose is null)
            return ValueTask.CompletedTask;

        TaskCompletionSource? starter = null;
        Task task;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = starter.Task;
            }

            task = _disposeTask;
        }

        if (starter is not null)
            _ = CompleteDisposeAsync(starter);

        return new(task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await _dispose!().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception error)
        {
            completion.SetException(error);
        }
    }
}

/// <summary>Current lifecycle state for a <see cref="PipelineRun{TOutput}"/>.</summary>
public enum PipelineRunState
{
    /// <summary>The run has been created but has not started.</summary>
    NotStarted,

    /// <summary>The run is processing work.</summary>
    Running,

    /// <summary>The run is draining accepted work.</summary>
    Draining,

    /// <summary>The run completed successfully.</summary>
    Completed,

    /// <summary>The run was cancelled cooperatively.</summary>
    Cancelled,

    /// <summary>The run was aborted immediately.</summary>
    Aborted,

    /// <summary>The run faulted.</summary>
    Faulted,
}

/// <summary>Structured status returned by <see cref="PipelineRun{TOutput}.TryDrainAsync"/>.</summary>
public enum PipelineDrainStatus
{
    /// <summary>The run drained and completed during this call.</summary>
    Completed,

    /// <summary>The drain timed out and the run is still active.</summary>
    TimedOutStillRunning,

    /// <summary>The drain request was cancelled by the caller.</summary>
    CancelledByCaller,

    /// <summary>The run faulted while the drain was waiting.</summary>
    Faulted,

    /// <summary>The run had already completed before this drain call.</summary>
    AlreadyCompleted,
}

/// <summary>Structured result returned by a non-throwing drain attempt.</summary>
/// <param name="Status">Drain outcome.</param>
/// <param name="State">Pipeline run state observed when the drain attempt completed.</param>
/// <param name="Elapsed">Elapsed drain wait time.</param>
/// <param name="Exception">Fault or cancellation exception, when available.</param>
public sealed record PipelineDrainResult(
    PipelineDrainStatus Status,
    PipelineRunState State,
    TimeSpan Elapsed,
    Exception? Exception = null);

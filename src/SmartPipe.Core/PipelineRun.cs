#nullable enable

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>Represents one running pipeline instance.</summary>
/// <typeparam name="TOutput">Output payload type.</typeparam>
/// <remarks>
/// A pipeline run is single-use. It owns the runtime completion signal and exposes one primary
/// output stream. Result-only consumption is provided as a compatibility projection through
/// <see cref="ReadResultsAsync"/>.
/// </remarks>
public sealed class PipelineRun<TOutput> : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask>? _cancel;
    private readonly Func<TimeSpan, CancellationToken, ValueTask>? _drain;
    private readonly Func<CancellationToken, ValueTask>? _abort;
    private readonly Func<ValueTask>? _dispose;

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
    {
        Outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));
        Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _cancel = cancel;
        _drain = drain;
        _abort = abort;
        _dispose = dispose;
    }

    private readonly Func<PipelineRunState> _stateProvider;

    /// <summary>Gets the primary envelope-aware output stream.</summary>
    public ChannelReader<PipelineOutput<TOutput>> Outputs { get; }

    /// <summary>Gets the task that observes run success, cancellation, or failure.</summary>
    public Task Completion { get; }

    /// <summary>Gets the current run state.</summary>
    public PipelineRunState State => _stateProvider();

    /// <summary>Reads legacy processing results projected from the primary output stream.</summary>
    /// <param name="ct">Cancellation token for enumeration.</param>
    /// <returns>Async sequence of processing results.</returns>
    public async IAsyncEnumerable<ProcessingResult<TOutput>> ReadResultsAsync(
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

    /// <summary>Requests immediate abort of pending work.</summary>
    /// <param name="ct">Cancellation token for the abort request.</param>
    /// <returns>A value task representing abort dispatch.</returns>
    public ValueTask AbortAsync(CancellationToken ct = default) =>
        _abort?.Invoke(ct) ?? ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _dispose?.Invoke() ?? ValueTask.CompletedTask;
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

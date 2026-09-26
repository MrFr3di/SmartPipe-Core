#nullable enable

using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Polly;

/// <summary>Runs an inner transform through an application-owned typed Polly pipeline.</summary>
/// <typeparam name="TInput">Input payload type.</typeparam>
/// <typeparam name="TOutput">Output payload type.</typeparam>
/// <remarks>
/// <para>
/// Every Polly attempt awaits the inner <see cref="IPipelineTransformer{TInput, TOutput}.TransformAsync"/>
/// once with the Polly context's cancellation token. The decorator adds no retry, timeout, or
/// fallback of its own; the supplied pipeline decides everything. The final Polly outcome is returned
/// unchanged: a final result stays a result, and a final exception is rethrown with its original
/// identity and stack trace unless <see cref="PollyTransformDecoratorOptions.ExceptionMapper"/> maps it.
/// </para>
/// <para>
/// The caller initializes and disposes the decorator. Transforms are rejected before initialization
/// succeeds and after disposal starts. Disposal waits for initialization and for every active
/// execution, including Polly retry delays, then disposes an <see cref="PollyInnerTransformOwnership.Owned"/>
/// inner transform once. A <see cref="PollyInnerTransformOwnership.Borrowed"/> inner transform and the
/// pipeline are never disposed.
/// </para>
/// </remarks>
public sealed class PollyTransformDecorator<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
{
    private readonly Lock _gate = new();
    private readonly IPipelineTransformer<TInput, TOutput> _inner;
    private readonly ResiliencePipeline<StageResult<TOutput>> _pipeline;
    private readonly PollyInnerTransformOwnership _innerOwnership;
    private readonly PollyTransformDecoratorSettings _settings;
    private LifecycleState _state;
    private Task? _initialization;
    private Task? _disposal;
    private int _activeOperations;
    private TaskCompletionSource? _drained;

    /// <summary>Creates a decorator over an inner transform.</summary>
    /// <param name="inner">The transform executed once per Polly attempt.</param>
    /// <param name="pipeline">The application-owned typed Polly pipeline. It is never disposed.</param>
    /// <param name="innerOwnership">Whether the decorator disposes <paramref name="inner"/>.</param>
    /// <param name="options">Optional settings, snapshotted by the constructor.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> or <paramref name="pipeline"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="innerOwnership"/> is not defined.</exception>
    /// <exception cref="ArgumentException">The operation key is invalid.</exception>
    public PollyTransformDecorator(
        IPipelineTransformer<TInput, TOutput> inner,
        ResiliencePipeline<StageResult<TOutput>> pipeline,
        PollyInnerTransformOwnership innerOwnership,
        PollyTransformDecoratorOptions? options = null)
        : this(inner, pipeline, innerOwnership, PollyTransformDecoratorSettings.Create(options))
    {
    }

    internal PollyTransformDecorator(
        IPipelineTransformer<TInput, TOutput> inner,
        ResiliencePipeline<StageResult<TOutput>> pipeline,
        PollyInnerTransformOwnership innerOwnership,
        PollyTransformDecoratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(pipeline);
        PollyTransformDecoratorSettings.ThrowIfUndefined(innerOwnership, nameof(innerOwnership));

        _inner = inner;
        _pipeline = pipeline;
        _innerOwnership = innerOwnership;
        _settings = settings;
    }

    private enum LifecycleState
    {
        Created,
        Initializing,
        Initialized,
        InitializationFailed,
        Disposing,
        Disposed,
    }

    /// <summary>Initializes the inner transform once.</summary>
    /// <param name="ct">Cancellation token for the first caller's initialization, or for waiting on it.</param>
    /// <returns>A task that completes when the shared initialization completes.</returns>
    /// <remarks>
    /// Concurrent and repeated calls share one initialization, including its failure. Initialization is
    /// rejected once disposal has started.
    /// </remarks>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        TaskCompletionSource? starter = null;
        Task initialization;
        lock (_gate)
        {
            if (_state >= LifecycleState.Disposing)
                throw CreateDisposedException();

            if (_initialization is null)
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _initialization = starter.Task;
                _state = LifecycleState.Initializing;
            }

            initialization = _initialization;
        }

        if (starter is null)
        {
            await initialization.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _inner.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompleteInitialization(LifecycleState.InitializationFailed);
            starter.TrySetException(exception);
            throw;
        }

        CompleteInitialization(LifecycleState.Initialized);
        starter.TrySetResult();
    }

    /// <summary>Runs the inner transform through the Polly pipeline.</summary>
    /// <param name="envelope">Input envelope passed unchanged to every attempt.</param>
    /// <param name="ct">Caller cancellation token placed in the pooled Polly context.</param>
    /// <returns>The final result produced by the inner transform or by a caller-owned Polly strategy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The decorator has not been initialized successfully.</exception>
    /// <exception cref="ObjectDisposedException">Disposal has started.</exception>
    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return TransformCoreAsync(envelope, ct);
    }

    /// <summary>Waits for initialization and active executions, then disposes an owned inner transform once.</summary>
    /// <returns>A task shared by every disposer, including any cleanup failure.</returns>
    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource? starter = null;
        Task disposal;
        Task? initialization = null;
        Task? drained = null;
        lock (_gate)
        {
            if (_disposal is null)
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = starter.Task;
                _state = LifecycleState.Disposing;
                initialization = _initialization;
                if (_activeOperations > 0)
                {
                    _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    drained = _drained.Task;
                }
            }

            disposal = _disposal;
        }

        if (starter is not null)
            await CompleteDisposalAsync(starter, initialization, drained).ConfigureAwait(false);

        await disposal.ConfigureAwait(false);
    }

    private async ValueTask<StageResult<TOutput>> TransformCoreAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct)
    {
        EnterOperation();
        try
        {
            return await ExecuteAsync(envelope, ct).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private async ValueTask<StageResult<TOutput>> ExecuteAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct)
    {
        Outcome<StageResult<TOutput>> outcome;
        var context = ResilienceContextPool.Shared.Get(
            _settings.OperationKey,
            continueOnCapturedContext: false,
            cancellationToken: ct);
        try
        {
            outcome = await _pipeline.ExecuteOutcomeAsync(
                static (attemptContext, state) => InvokeInnerAsync(
                    state.Inner,
                    state.Envelope,
                    attemptContext.CancellationToken),
                context,
                (Inner: _inner, Envelope: envelope)).ConfigureAwait(false);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }

        if (outcome.Exception is { } exception && TryMapException(exception, out var error))
            return StageResult<TOutput>.Failure(error);

        outcome.ThrowIfException();
        return outcome.Result;
    }

    private static async ValueTask<Outcome<StageResult<TOutput>>> InvokeInnerAsync(
        IPipelineTransformer<TInput, TOutput> inner,
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct)
    {
        // Polly requires outcome callbacks to report failures as outcomes instead of throwing.
        try
        {
            return Outcome.FromResult(await inner.TransformAsync(envelope, ct).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return Outcome.FromException<StageResult<TOutput>>(exception);
        }
    }

    private bool TryMapException(Exception exception, out SmartPipeError error)
    {
        error = default;
        if (_settings.ExceptionMapper is not { } mapper || exception is OperationCanceledException)
            return false;

        SmartPipeError? mapped;
        try
        {
            mapped = mapper(exception);
        }
        catch (Exception mapperFailure)
        {
            throw new AggregateException(
                "The Polly exception mapper failed. The original exception is first.",
                exception,
                mapperFailure);
        }

        if (mapped is not { } value)
            return false;

        error = value;
        return true;
    }

    private void EnterOperation()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case LifecycleState.Initialized:
                    _activeOperations++;
                    return;
                case >= LifecycleState.Disposing:
                    throw CreateDisposedException();
                default:
                    throw new InvalidOperationException(
                        "The Polly transform decorator must be initialized successfully before it transforms items.");
            }
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _activeOperations--;
            if (_activeOperations == 0)
                drained = _drained;
        }

        drained?.TrySetResult();
    }

    private void CompleteInitialization(LifecycleState state)
    {
        lock (_gate)
        {
            if (_state == LifecycleState.Initializing)
                _state = state;
        }
    }

    private async Task CompleteDisposalAsync(
        TaskCompletionSource completion,
        Task? initialization,
        Task? drained)
    {
        try
        {
            // Initialization failures belong to the initializing callers; disposal still cleans up.
            if (initialization is not null)
                await initialization.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            if (drained is not null)
                await drained.ConfigureAwait(false);

            if (_innerOwnership == PollyInnerTransformOwnership.Owned)
                await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            MarkDisposed();
            completion.TrySetException(exception);
            return;
        }

        MarkDisposed();
        completion.TrySetResult();
    }

    private void MarkDisposed()
    {
        lock (_gate)
            _state = LifecycleState.Disposed;
    }

    private ObjectDisposedException CreateDisposedException() =>
        new(GetType().FullName, "The Polly transform decorator has been disposed.");
}

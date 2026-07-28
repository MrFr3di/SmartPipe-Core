#nullable enable

using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace SmartPipe.Core;

internal sealed class PipelineStartOperation<TOutput>
{
    public required PipelineRun<TOutput> Run { get; init; }

    public required Task Ready { get; init; }

    public required Task Completion { get; init; }

    public static PipelineStartOperation<TOutput> Start<TInput>(
        PipelineExecutionPlan<TInput, TOutput> plan,
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var controller = new DeferredPipelineRunController<TInput, TOutput>(
            plan,
            context,
            cancellationToken);
        return controller.Start();
    }
}

internal sealed class DeferredPipelineRunController<TInput, TOutput>
{
    private readonly PipelineExecutionPlan<TInput, TOutput> _plan;
    private readonly PipelineActivationContext _context;
    private readonly PipelineRuntimeOptions _options;
    private readonly Channel<PipelineOutput<TOutput>> _outputs;
    private readonly CancellationTokenSource _activationCancellation;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TypedPipelineExecutor<TInput, TOutput>?> _executorAttached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _executorStartup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Task> _completionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TypedPipelineExecutor<TInput, TOutput>? _executor;
    private PipelineRun<TOutput>? _executorRun;
    private Task _completion = Task.CompletedTask;
    private ExceptionDispatchInfo? _startupFailure;
    private int _state = (int)PipelineRunState.NotStarted;
    private int _abortRequested;

    public DeferredPipelineRunController(
        PipelineExecutionPlan<TInput, TOutput> plan,
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _options = plan.RuntimeOptions.Materialize(context);
        _activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _outputs = TypedPipelineExecutor<TInput, TOutput>.CreateOutputChannel(
            _options,
            OnOutputDropped);
    }

    public PipelineStartOperation<TOutput> Start()
    {
        _completion = _completionSource.Task.Unwrap();
        var lifecycle = RunLifecycleAsync();
        _ = PublishLifecycleCompletionAsync(lifecycle);
        var run = new PipelineRun<TOutput>(
            _outputs.Reader,
            _completion,
            GetState,
            CancelAsync,
            DrainAsync,
            TryDrainAsync,
            AbortAsync,
            DisposeAsync,
            GetMetrics,
            _plan.Key,
            _context.RunId);

        return new()
        {
            Run = run,
            Ready = _ready.Task,
            Completion = _completion,
        };
    }

    private async Task PublishLifecycleCompletionAsync(Task lifecycle)
    {
        try
        {
            await lifecycle.ConfigureAwait(false);
            _completionSource.TrySetResult(Task.CompletedTask);
        }
        catch (Exception error)
        {
            var state = GetState();
            _completionSource.TrySetResult(
                error is OperationCanceledException
                    && state is PipelineRunState.Cancelled or PipelineRunState.Aborted
                    ? lifecycle
                    : Task.FromException(error));
        }
    }

    private async Task RunLifecycleAsync()
    {
        try
        {
            var graph = await PipelineActivator.ActivateAsync(
                    _plan,
                    _context,
                    _activationCancellation.Token)
                .ConfigureAwait(false);
            var executor = new TypedPipelineExecutor<TInput, TOutput>(
                _plan.Key,
                _context.RunId,
                graph,
                _options,
                _plan.LineageMode,
                _plan.ForcePipelineId,
                _outputs,
                _activationCancellation.Token,
                _executorStartup);
            Volatile.Write(ref _executor, executor);

            var executorRun = executor.Start();
            Volatile.Write(ref _executorRun, executorRun);
            _executorAttached.TrySetResult(executor);

            if (Volatile.Read(ref _abortRequested) != 0)
                await executor.AbortAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                await _executorStartup.Task.ConfigureAwait(false);
                _ready.TrySetResult();
            }
            catch (Exception startupError)
            {
                _startupFailure = ExceptionDispatchInfo.Capture(startupError);
                _ready.TrySetException(startupError);
                _ = _ready.Task.Exception;
            }

            await executorRun.Completion.ConfigureAwait(false);
            _startupFailure?.Throw();
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _executorRun) is null)
            {
                SetPreExecutorTerminalState(error);
                var outputError =
                    error is OperationCanceledException
                    && !_activationCancellation.IsCancellationRequested
                        ? new AggregateException(error)
                        : error;
                _outputs.Writer.TryComplete(outputError);
                _startupFailure = ExceptionDispatchInfo.Capture(error);
                _executorAttached.TrySetResult(null);
                _ready.TrySetException(error);
                _ = _ready.Task.Exception;
            }

            throw;
        }
    }

    private PipelineRunState GetState() =>
        Volatile.Read(ref _executorRun)?.State
        ?? (PipelineRunState)Volatile.Read(ref _state);

    private SmartPipeMetricsSnapshot GetMetrics() =>
        Volatile.Read(ref _executorRun)?.Metrics
        ?? SmartPipeMetricsSnapshot.Empty;

    private async ValueTask CancelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executor = Volatile.Read(ref _executor);
        if (executor is not null)
        {
            await executor.CancelAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Interlocked.CompareExchange(
            ref _state,
            (int)PipelineRunState.Cancelled,
            (int)PipelineRunState.NotStarted);
        await _activationCancellation.CancelAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _abortRequested, 1);
        Interlocked.Exchange(ref _state, (int)PipelineRunState.Aborted);
        var executor = Volatile.Read(ref _executor);
        if (executor is not null)
        {
            await executor.AbortAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _activationCancellation.CancelAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executor = await GetAttachedExecutorAsync(cancellationToken).ConfigureAwait(false);
        await executor.DrainAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PipelineDrainResult> TryDrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var executor = await GetAttachedExecutorAsync(cancellationToken).ConfigureAwait(false);
        return await executor.TryDrainAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TypedPipelineExecutor<TInput, TOutput>> GetAttachedExecutorAsync(
        CancellationToken cancellationToken)
    {
        var executor = await _executorAttached.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (executor is not null)
            return executor;

        _startupFailure?.Throw();
        throw new InvalidOperationException("Pipeline startup failed before executor attachment.");
    }

    private async ValueTask DisposeAsync()
    {
        try
        {
            await CancelAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_completion.IsCompleted)
        {
        }

        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch
        {
            // Completion remains the authoritative startup/runtime outcome.
        }

        var executor = Volatile.Read(ref _executor);
        if (executor is not null)
            await executor.DisposeAsync().ConfigureAwait(false);

        _activationCancellation.Dispose();
    }

    private void OnOutputDropped(PipelineOutput<TOutput> output) =>
        Volatile.Read(ref _executor)?.RecordOutputDropped(output);

    private void SetPreExecutorTerminalState(Exception error)
    {
        if (error is not OperationCanceledException || !_activationCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref _state, (int)PipelineRunState.Faulted);
            return;
        }

        if (Volatile.Read(ref _abortRequested) != 0)
        {
            Volatile.Write(ref _state, (int)PipelineRunState.Aborted);
            return;
        }

        Volatile.Write(ref _state, (int)PipelineRunState.Cancelled);
    }
}

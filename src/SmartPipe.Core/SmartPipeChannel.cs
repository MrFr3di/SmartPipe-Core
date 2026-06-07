#nullable enable

using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SmartPipe.Core;

/// <summary>Pipeline engine that orchestrates sources, transformers, and sinks using System.Threading.Channels.</summary>
/// <typeparam name="TInput">Input type from sources.</typeparam>
/// <typeparam name="TOutput">Output type to sinks.</typeparam>
/// <remarks>
/// All internal channels use <see cref="SmartPipeChannelOptions.BoundedCapacity"/> for backpressure.
/// Implements <see cref="IAsyncDisposable"/> for proper resource cleanup.
/// </remarks>
public class SmartPipeChannel<TInput, TOutput> : IAsyncDisposable
{
    private const string RetryCountMetadataKey = "__smartpipe_retry_count";

    private static readonly ActivitySource _activitySource = new(
        "SmartPipe.Core",
        typeof(SmartPipeChannel<,>).Assembly.GetName().Version?.ToString() ?? "1.0.0"
    );
    private readonly ILogger? _logger;
    private readonly IClock _clock;

    private readonly List<ISource<TInput>> _sources = [];
    private readonly List<ITransformer<TInput, TOutput>> _transformers = [];
    private readonly List<ISink<TOutput>> _sinks = [];
    private readonly SmartPipeChannelOptions _options;
    private readonly CancellationTokenSource _internalCts = new();
    private Channel<ProcessingContext<TInput>>? _inputChannel;
    private IInputBuffer<ProcessingContext<TInput>>? _inputBuffer;
    private AdaptiveChannelSet<ProcessingContext<TInput>>? _adaptiveChannelSet;
    private AdaptiveInFlightLimiter? _adaptiveInFlightLimiter;
    private AdaptiveParallelismController? _adaptiveController;
    private Channel<ProcessingResult<TOutput>>? _outputChannel;
    private Channel<ProcessingResult<TOutput>>? _backgroundOutputChannel;
    private volatile bool _producerCompleted,
        _isPaused;
    private volatile bool _backgroundRunMode;
    private volatile bool _inputBufferCompleted;
    private volatile PipelineState _state = PipelineState.NotStarted;
    private int _isDraining = 0; // 0 = not draining, 1 = draining
    private int _disposed; // 0 = not disposed, 1 = disposed
    private int _disposeStarted; // 0 = not started, 1 = started
    private int _componentsDisposed;
    private int _cancelCalled;
    private int _backgroundOutputCompleted;
    private volatile bool _drainRequested;
    private int _activeConsumerCount;
    private TaskCompletionSource _runCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private readonly AdaptiveParallelism? _adaptiveParallelism;
    private readonly AdaptiveMetrics _adaptiveMetrics = new();
    private readonly ExponentialHistogram _latencyHistogram = new();
    private readonly RetryQueue<TInput>? _retryQueue;
    private readonly CircuitBreaker? _circuitBreaker;
    private readonly BackpressureStrategy _backpressure;
    private readonly ReservoirSampler<TInput>? _debugSampler;
    private readonly CuckooFilter? _cuckooFilter;
    private readonly int[]? _shardBuckets;
    private int _totalCount;
    private DateTime _startTime;
    private DateTimeOffset _lastAdaptiveDecisionUtc;
    private long _lastAdaptiveProcessed;
    private long _lastAdaptiveFailed;
    private long _lastAdaptiveRetried;

    /// <summary>Gets the pipeline configuration options.</summary>
    public SmartPipeChannelOptions Options => _options;

    /// <summary>Gets the current pipeline metrics.</summary>
    public SmartPipeMetrics Metrics { get; private set; } = new();

    /// <summary>Gets the latency histogram for performance tracking.</summary>
    public ExponentialHistogram LatencyHistogram => _latencyHistogram;

    /// <summary>Gets a value indicating whether the pipeline is paused.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Gets the current pipeline state.</summary>
    public PipelineState State => _state;

    /// <summary>Event raised when the pipeline state changes.</summary>
    /// <remarks>Parameters: (oldState, newState).</remarks>
    public event Action<PipelineState, PipelineState>? OnStateChanged;

    /// <summary>Initializes a new pipeline with default options.</summary>
    public SmartPipeChannel()
        : this(new SmartPipeChannelOptions(), null) { }

    /// <summary>Initializes a new pipeline with the specified options.</summary>
    /// <param name="options">Pipeline configuration options.</param>
    /// <param name="clock">Optional clock for testability (defaults to TimeProviderClock()).</param>
    /// <param name="logger">Optional logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when options is null.</exception>
    public SmartPipeChannel(
        SmartPipeChannelOptions options,
        IClock? clock = null,
        ILogger<SmartPipeChannel<TInput, TOutput>>? logger = null
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _clock = clock ?? new TimeProviderClock();
        _logger = logger;
        _runCompletion.TrySetResult(); // No pipeline run active yet
        _backpressure = new BackpressureStrategy(options.BoundedCapacity);
        if (options.IsEnabled("RetryQueue"))
            _retryQueue = new RetryQueue<TInput>(
                options.BoundedCapacity,
                null,
                options.DeadLetterSink,
                _clock,
                options.RetryQueueOverflowPolicy
            );
        if (options.IsEnabled("CircuitBreaker"))
            _circuitBreaker = new CircuitBreaker(clock: _clock);
        if (options.IsEnabled("DebugSampling"))
            _debugSampler = new ReservoirSampler<TInput>(1000);
        if (options.IsEnabled("CuckooFilter"))
            _cuckooFilter = new CuckooFilter();
        if (options.IsEnabled("JumpHash"))
            _shardBuckets = new int[options.MaxDegreeOfParallelism];
        if (!options.AdaptiveParallelism.Enabled)
            _adaptiveParallelism = new AdaptiveParallelism(2, options.MaxDegreeOfParallelism);
    }

    /// <summary>Adds a data source to the pipeline.</summary>
    /// <param name="source">The source to add.</param>
    public void AddSource(ISource<TInput> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfStartedOrDisposed();
        _sources.Add(source);
    }

    /// <summary>Adds a transformer to the pipeline.</summary>
    /// <param name="t">The transformer to add.</param>
    public void AddTransformer(ITransformer<TInput, TOutput> t)
    {
        ArgumentNullException.ThrowIfNull(t);
        ThrowIfStartedOrDisposed();
        _transformers.Add(t);
    }

    /// <summary>Adds a sink to the pipeline.</summary>
    /// <param name="sink">The sink to add.</param>
    public void AddSink(ISink<TOutput> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ThrowIfStartedOrDisposed();
        _sinks.Add(sink);
    }

    private void ThrowIfStartedOrDisposed()
    {
        if (_options.ThrowOnMutationAfterStart && (_state != PipelineState.NotStarted || _disposed == 1))
            throw new InvalidOperationException("Pipeline cannot be mutated after start.");
    }

    /// <summary>Pauses the pipeline processing.</summary>
    public void Pause()
    {
        _isPaused = true;
        TransitionState(PipelineState.Paused);
    }

    /// <summary>Resumes the paused pipeline.</summary>
    public void Resume()
    {
        _isPaused = false;
        TransitionState(PipelineState.Running);
    }

    /// <summary>Cancels the pipeline execution.</summary>
    public void Cancel()
    {
        var cancellation = new OperationCanceledException("Pipeline execution was cancelled.");
        if (Interlocked.CompareExchange(ref _cancelCalled, 1, 0) != 0)
        {
            CompleteExternalOutput(cancellation);
            return;
        }
        _inputChannel?.Writer.TryComplete();
        _outputChannel?.Writer.TryComplete();
        CompleteExternalOutput(cancellation);
        _internalCts.Cancel();
        TransitionState(PipelineState.Cancelled);
    }

    private void TransitionState(PipelineState newState)
    {
        var old = _state;
        _state = newState;
        if (old != newState)
            OnStateChanged?.Invoke(old, newState);
    }

    /// <summary>Drains the pipeline by signaling the producer to stop and waiting for all consumer tasks to finish processing accepted work.</summary>
    /// <param name="timeout">Maximum time to wait for draining.</param>
    /// <param name="ct">Cancellation token to abort the drain operation.</param>
    /// <remarks>Idempotent: subsequent calls return immediately. Does not discard items. Output writer is completed only after all consumers finish.</remarks>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _isDraining, 1, 0) != 0)
            return;

        _drainRequested = true;
        var completed = false;

        // Wait for the entire pipeline run to complete.
        // RunPipelineAsync handles graceful shutdown: producer stops,
        // consumers process remaining items including requeued retries,
        // then output writer is completed.
        try
        {
            await WaitWithTimeoutAsync(_runCompletion.Task, timeout, ct, "Pipeline drain timed out before the run completed.")
                .ConfigureAwait(false);

            // Ensure output reader is fully consumed (belt-and-suspenders after run completion)
            if (_outputChannel != null)
            {
                await WaitWithTimeoutAsync(
                        _outputChannel.Reader.Completion,
                        timeout,
                        ct,
                        "Pipeline drain timed out before output completion."
                    )
                    .ConfigureAwait(false);
            }

            completed = true;
        }
        finally
        {
            if (!completed)
                Interlocked.Exchange(ref _isDraining, 0);
        }
    }

    private static async Task WaitWithTimeoutAsync(
        Task task,
        TimeSpan timeout,
        CancellationToken ct,
        string timeoutMessage
    )
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    private async Task WaitForRunCompletionToSettleAsync()
    {
        try
        {
            await WaitWithTimeoutAsync(
                    _runCompletion.Task,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    "Pipeline run did not settle during disposal."
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Run faults are observed by DrainAsync when they are part of the disposal decision.
            // The bounded settle wait only prevents lifecycle cleanup from hanging.
        }
    }

    private void CompleteExternalOutput(Exception? error = null)
    {
        var channel = _backgroundOutputChannel;
        if (channel == null)
            return;

        if (Interlocked.Exchange(ref _backgroundOutputCompleted, 1) != 0)
            return;

        var drainBufferedOutput = ShouldDrainExternalOutputOnCompletion(error);
        if (drainBufferedOutput)
            DrainExternalOutputBuffer(channel);

        channel.Writer.TryComplete(error);

        if (drainBufferedOutput)
            DrainExternalOutputBuffer(channel);
    }

    private bool ShouldDrainExternalOutputOnCompletion(Exception? error)
    {
        return error is OperationCanceledException
            || error is TimeoutException
            || Volatile.Read(ref _cancelCalled) != 0
            || Volatile.Read(ref _disposeStarted) != 0
            || _internalCts.IsCancellationRequested;
    }

    private static void DrainExternalOutputBuffer(Channel<ProcessingResult<TOutput>> channel)
    {
        while (channel.Reader.TryRead(out _))
        {
        }
    }

    private void CompleteExternalOutputFromRunCompletion()
    {
        var completion = _runCompletion.Task;
        if (!completion.IsCompleted)
            return;

        if (completion.IsCompletedSuccessfully)
        {
            CompleteExternalOutput();
            return;
        }

        if (completion.IsCanceled)
        {
            CompleteExternalOutput(new OperationCanceledException("Pipeline execution was cancelled."));
            return;
        }

        CompleteExternalOutput(completion.Exception?.InnerException ?? completion.Exception);
    }

    private bool IsLifecycleStopping(CancellationToken ct) =>
        ct.IsCancellationRequested
        || Volatile.Read(ref _cancelCalled) != 0
        || Volatile.Read(ref _disposeStarted) != 0
        || _internalCts.IsCancellationRequested
        || _runCompletion.Task.IsCompleted;

    private async Task DisposeComponentsAsync()
    {
        if (Interlocked.CompareExchange(ref _componentsDisposed, 1, 0) != 0)
            return;
        foreach (var s in _sources)
            await s.DisposeAsync().ConfigureAwait(false);
        foreach (var t in _transformers)
            await t.DisposeAsync().ConfigureAwait(false);
        foreach (var s in _sinks)
            await s.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the pipeline by draining pending items and releasing all resources.
    /// Thread-safe: concurrent calls wait for the same disposal operation.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous disposal operation.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeStarted, 1);
        Interlocked.Exchange(ref _disposed, 1);

        List<Exception>? failures = null;

        if (_state != PipelineState.NotStarted)
        {
            try
            {
                using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await DrainAsync(TimeSpan.FromSeconds(5), drainCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                try
                {
                    Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            catch (Exception ex)
            {
                AddFailure(ex);
            }

            CompleteExternalOutputFromRunCompletion();
            await WaitForRunCompletionToSettleAsync().ConfigureAwait(false);
        }

        await CaptureFailureAsync(DisposeComponentsAsync).ConfigureAwait(false);
        if (_inputBuffer != null)
            await CaptureFailureAsync(() => _inputBuffer.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_adaptiveInFlightLimiter != null)
            await CaptureFailureAsync(() => _adaptiveInFlightLimiter.DisposeAsync().AsTask()).ConfigureAwait(false);

        try
        {
            _internalCts?.Dispose();
        }
        catch (Exception ex)
        {
            AddFailure(ex);
        }

        GC.SuppressFinalize(this);

        if (failures is { Count: 1 })
            throw failures[0];
        if (failures is { Count: > 1 })
            throw new AggregateException(failures);

        return;

        async Task CaptureFailureAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddFailure(ex);
            }
        }

        void AddFailure(Exception exception)
        {
            failures ??= [];
            failures.Add(exception);
        }
    }

    /// <summary>Gets the dedicated background output reader created by <see cref="RunInBackground"/>.</summary>
    /// <returns>The background output reader, or null when the pipeline was not started with <see cref="RunInBackground"/>.</returns>
    /// <remarks>This method never exposes the internal pipeline output reader owned by the runtime.</remarks>
    public ChannelReader<ProcessingResult<TOutput>>? AsChannelReader() =>
        _backgroundOutputChannel?.Reader;

    /// <summary>Runs the pipeline in background and returns a dedicated output channel reader.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dedicated channel reader for pipeline output.</returns>
    /// <remarks>
    /// Creates a bounded external output channel with <see cref="SmartPipeChannelOptions.BoundedCapacity"/>.
    /// This is the only legacy sinkless run mode. When user sinks are also registered,
    /// the returned reader receives each output before user sinks are invoked.
    /// An unread returned reader can backpressure the run until cancellation or disposal completes it.
    /// </remarks>
    public ChannelReader<ProcessingResult<TOutput>> RunInBackground(CancellationToken ct = default)
    {
        if (_state != PipelineState.NotStarted)
            throw new InvalidOperationException("Pipeline already started.");

        _backgroundOutputChannel = Channel.CreateBounded<ProcessingResult<TOutput>>(
            new BoundedChannelOptions(
                _options.BoundedCapacity > 0 ? _options.BoundedCapacity : 1000
            )
            {
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
        _backgroundRunMode = true;
        TransitionState(PipelineState.Running);

        // Create a fresh completion source synchronously so DrainAsync can observe it
        // without racing against the background task startup.
        var runTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runCompletion = runTcs;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(ct).ConfigureAwait(false);
                runTcs.TrySetResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                runTcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                runTcs.TrySetException(ex);
            }
            finally
            {
                CompleteExternalOutputFromRunCompletion();
            }
        }, CancellationToken.None);
        return _backgroundOutputChannel.Reader;
    }

    /// <summary>Creates a real-time dashboard snapshot of pipeline state.</summary>
    /// <returns>Dashboard object with current metrics.</returns>
    public PipelineDashboard CreateDashboard() =>
        new(
            _state,
            _totalCount,
            null,
            _startTime != default ? _clock.UtcNow - _startTime : TimeSpan.Zero,
            _latencyHistogram.P99,
            _circuitBreaker?.State.ToString() ?? "N/A",
            Metrics.Export()
        );

    private void Validate()
    {
        if (_sources.Count == 0)
            throw new InvalidOperationException("At least one source required.");
        if (_transformers.Count == 0)
            throw new InvalidOperationException("At least one transformer required.");
        if (_sinks.Count == 0 && !_backgroundRunMode)
            throw new InvalidOperationException("At least one sink required.");
    }

    /// <summary>Runs the pipeline: initializes components, processes items, handles retries and errors.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Pipeline flow: Sources → Transformers → Sinks with optional retry queue and circuit breaker.
    /// Uses <see cref="SmartPipeChannelOptions.BoundedCapacity"/> for backpressure.
    /// </remarks>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Validate();
        TransitionState(PipelineState.Running);
        _startTime = _clock.UtcNow;
        using var totalTimeoutCts = new CancellationTokenSource(_options.TotalRequestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _internalCts.Token,
            totalTimeoutCts.Token
        );
        var token = linkedCts.Token;
        _producerCompleted = _isPaused = false;
        _inputBufferCompleted = false;
        using var activity = _activitySource.StartActivity("Pipeline.Run");
        activity?.SetTag("smartpipe.parallelism", _options.MaxDegreeOfParallelism);
        try
        {
            await RunPipelineAsync(token, totalTimeoutCts.Token, activity).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (totalTimeoutCts.IsCancellationRequested)
        {
            CompleteRunAsTimedOut(activity);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TransitionState(PipelineState.Cancelled);
            _runCompletion.TrySetCanceled(token);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Pipeline faulted due to unhandled exception");
            TransitionState(PipelineState.Faulted);
            _runCompletion.TrySetException(ex);
            throw;
        }
    }

    private void HandleRunAsyncErrors(Activity? activity, string errorTag)
    {
        activity?.SetStatus(ActivityStatusCode.Error, errorTag);
        TransitionState(PipelineState.Faulted);
    }

    private void CompleteRunAsTimedOut(Activity? activity)
    {
        HandleRunAsyncErrors(activity, "TotalRequestTimeout");
        _runCompletion.TrySetException(new TimeoutException("Pipeline total request timeout expired."));
    }

    private async Task RunPipelineAsync(
        CancellationToken token,
        CancellationToken totalTimeoutToken,
        Activity? activity
    )
    {
        await InitializePipelineAsync(token).ConfigureAwait(false);
        var retryTask = _retryQueue != null ? ProcessRetriesAsync(token) : null;
        var producerTask = ProduceAsync(token);
        int p = _inputBuffer?.TotalLaneCount ?? _adaptiveParallelism?.Current ?? _options.MaxDegreeOfParallelism;
        var consumers = new Task[p];
        for (int i = 0; i < p; i++)
            consumers[i] = ConsumeAsync(token, i);
        var monitor = MonitorParallelismAsync(token);
        var sink = ConsumeOutputAsync(token);
        await producerTask.ConfigureAwait(false);
        _producerCompleted = true;

        // Allow retry task to complete before closing the input channel.
        // Retries may requeue items back into the input channel via HandleRetryRequeued,
        // so the input writer must remain open until all retry processing is done.
        if (retryTask != null)
            await retryTask.ConfigureAwait(false);

        CompleteInput();
        await Task.WhenAll(consumers).ConfigureAwait(false);
        _outputChannel!.Writer.TryComplete();
        await sink.ConfigureAwait(false);
        await monitor.ConfigureAwait(false);
        await DisposePipelineAsync(token).ConfigureAwait(false);
        if (_inputChannel != null)
            ChannelPool.CloseChannel(_inputChannel);
        ChannelPool.CloseChannel(_outputChannel);
        if (Volatile.Read(ref _cancelCalled) != 0 || token.IsCancellationRequested)
        {
            if (totalTimeoutToken.IsCancellationRequested)
            {
                CompleteRunAsTimedOut(activity);
                return;
            }

            TransitionState(PipelineState.Cancelled);
            _runCompletion.TrySetCanceled(token);
            return;
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        TransitionState(PipelineState.Completed);
        _runCompletion.TrySetResult();
    }

    private async Task InitializePipelineAsync(CancellationToken token)
    {
        foreach (var s in _sources)
            await s.InitializeAsync(token).ConfigureAwait(false);
        foreach (var t in _transformers)
            await t.InitializeAsync(token).ConfigureAwait(false);
        foreach (var s in _sinks)
            await s.InitializeAsync(token).ConfigureAwait(false);
        if (_options.AdaptiveParallelism.Enabled)
            InitializeAdaptiveInputBuffer();
        else
            _inputChannel = ChannelPool.CreateBoundedMultiReaderMultiWriter<ProcessingContext<TInput>>(
                _options.BoundedCapacity,
                _options.FullMode
            );
        _outputChannel = ChannelPool.CreateBoundedSingleReaderMultiWriter<ProcessingResult<TOutput>>(
            _options.BoundedCapacity,
            _options.FullMode
        );
        Metrics = new SmartPipeMetrics();
    }

    private void InitializeAdaptiveInputBuffer()
    {
        var adaptive = _options.AdaptiveParallelism;
        var totalLaneCount = Math.Min(adaptive.MaxDegreeOfParallelism, _options.BoundedCapacity);
        var initialActiveLaneCount = Math.Min(adaptive.InitialDegreeOfParallelism, totalLaneCount);

        _adaptiveChannelSet = new AdaptiveChannelSet<ProcessingContext<TInput>>(
            _options.BoundedCapacity,
            totalLaneCount,
            initialActiveLaneCount,
            _options.FullMode
        );
        _inputBuffer = _adaptiveChannelSet;
        _adaptiveInFlightLimiter = new AdaptiveInFlightLimiter(adaptive.InitialInFlightItems);
        _adaptiveController = new AdaptiveParallelismController(adaptive);
        _lastAdaptiveDecisionUtc = DateTimeOffset.UtcNow;
        _lastAdaptiveProcessed = _lastAdaptiveFailed = _lastAdaptiveRetried = 0;
    }

    private async Task DisposePipelineAsync(CancellationToken token)
    {
        await DisposeComponentsAsync().ConfigureAwait(false);
    }

    /// <summary>Processes a single item through the transformer chain.</summary>
    /// <param name="ctx">Processing context with input payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Processing result with output or error.</returns>
    /// <remarks>Checks circuit breaker before processing and executes compatible transformers in insertion order.</remarks>
    public async ValueTask<ProcessingResult<TOutput>> ProcessSingleAsync(
        ProcessingContext<TInput> ctx,
        CancellationToken ct = default
    )
    {
        using var activity = _activitySource.StartActivity("ProcessSingle");
        activity?.SetTag("smartpipe.trace_id", ctx.TraceId);
        if (_circuitBreaker != null && !_circuitBreaker.AllowRequest())
            return ProcessingResult<TOutput>.Failure(
                new SmartPipeError(
                    "Circuit breaker is open",
                    ErrorType.Transient,
                    "CircuitBreaker"
                ),
                ctx.TraceId
            );
        var (result, elapsed) = await TransformWithTimeoutAsync(ctx, ct).ConfigureAwait(false);
        _adaptiveMetrics.Update(elapsed);
        _latencyHistogram.Record(elapsed);
        Metrics.RecordProcessed(elapsed);
        if (result)
        {
            _circuitBreaker?.RecordSuccess();
            activity?.SetTag("smartpipe.latency_ms", elapsed);
        }
        else
        {
            _circuitBreaker?.RecordFailure();
            Metrics.RecordFailed();
            activity?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);
        }
        return result;
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        try
        {
            foreach (var source in _sources)
                await ProcessSourceAsync(source, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Operation cancellation is expected behavior in pipeline processing
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
    }

    private async Task ProcessSourceAsync(ISource<TInput> source, CancellationToken ct)
    {
        try
        {
            await foreach (var ctx in source.ReadAsync(ct).ConfigureAwait(false))
            {
                if (_drainRequested) break;
                await ProcessSourceItemAsync(ctx, ct).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex)
        {
            using var a = _activitySource.StartActivity("Source.Error");
            a?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger?.LogError(ex, "Source error in ProduceAsync");
            Metrics.RecordFailed();
        }
        catch (NotSupportedException ex)
        {
            using var a = _activitySource.StartActivity("Source.Error");
            a?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger?.LogError(ex, "Source error in ProduceAsync");
            Metrics.RecordFailed();
        }
        catch (IOException ex)
        {
            using var a = _activitySource.StartActivity("Source.Error");
            a?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger?.LogError(ex, "Source error in ProduceAsync");
            Metrics.RecordFailed();
        }
        catch (Exception ex) when (ct.IsCancellationRequested == false && _options.ContinueOnError)
        {
            using var a = _activitySource.StartActivity("Source.Error");
            a?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger?.LogError(ex, "Source error in ProduceAsync (ContinueOnError)");
            Metrics.RecordFailed();
        }
    }

    private async ValueTask ProcessSourceItemAsync(
        ProcessingContext<TInput> ctx,
        CancellationToken ct
    )
    {
        while (_isPaused && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        Metrics.QueueSize = GetInputQueueSize();

        if (_inputChannel != null || _inputBuffer != null)
        {
            _backpressure.UpdateThroughput(
                _adaptiveMetrics.SmoothThroughputPerSec,
                _adaptiveMetrics.PredictNextLatency()
            );
            await _backpressure.ThrottleAsync(GetInputQueueSize(), ct).ConfigureAwait(false);
        }

        if (_cuckooFilter?.Contains(ctx.TraceId) == true)
        {
            Metrics.RecordDuplicate();
            _options.OnMetrics?.Invoke(Metrics);
            return;
        }

        _cuckooFilter?.Add(ctx.TraceId);

        if (_options.DeduplicationFilter?.ContainsAndAdd(ctx.TraceId) == true)
        {
            Metrics.RecordDuplicate();
            _options.OnMetrics?.Invoke(Metrics);
            return;
        }

        _debugSampler?.Add(ctx.Payload);
        await WriteInputAsync(ctx, ct).ConfigureAwait(false);
        int current = Interlocked.Increment(ref _totalCount);
        _options.OnMetrics?.Invoke(Metrics);
        _options.OnProgress?.Invoke(current, null, _clock.UtcNow - _startTime, null);
    }

    private async Task ConsumeAsync(CancellationToken ct, int consumerIndex)
    {
        if (_inputBuffer != null)
        {
            await ConsumeAdaptiveAsync(ct, consumerIndex).ConfigureAwait(false);
            return;
        }

        try
        {
            await foreach (var ctx in _inputChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!ShouldProcessItem(ctx, consumerIndex))
                    continue;
                if (!await HandleCircuitBreakerAsync(ctx, ct).ConfigureAwait(false))
                    continue;
                Interlocked.Increment(ref _activeConsumerCount);
                try
                {
                    var (result, elapsed) = await ProcessTransformAsync(ctx, ct).ConfigureAwait(false);
                    await HandleTransformResultAsync(ctx, result, elapsed, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeConsumerCount);
                }
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Operation cancellation is expected behavior in pipeline processing
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
    }

    private async ValueTask<(
        ProcessingResult<TOutput> Result,
        long ElapsedMs
    )> ProcessTransformAsync(ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        using var activity = _activitySource.StartActivity("Transform");
        activity?.SetTag("smartpipe.trace_id", ctx.TraceId);
        var (result, elapsed) = await TransformWithTimeoutAsync(ctx, ct).ConfigureAwait(false);
        RecordMetrics(elapsed);
        return (result, elapsed);
    }

    private async Task HandleTransformResultAsync(
        ProcessingContext<TInput> ctx,
        ProcessingResult<TOutput> result,
        long elapsed,
        CancellationToken ct
    )
    {
        if (!result)
        {
            if (result.Error?.Category == "Filtered")
            {
                await WriteOutputAsync(result, ct).ConfigureAwait(false);
                return;
            }
            await HandleFailureAsync(ctx, result, null, ct).ConfigureAwait(false);
            // Do NOT return ctx to pool here — it may be enqueued in RetryQueue
        }
        else
        {
            HandleSuccess(result, null, elapsed);
            await WriteOutputAsync(result, ct).ConfigureAwait(false);
        }
    }

    private bool ShouldProcessItem(ProcessingContext<TInput> ctx, int consumerIndex) =>
        _shardBuckets == null || JumpHash.Hash(ctx.TraceId, _shardBuckets.Length) == consumerIndex;

    private async ValueTask<bool> HandleCircuitBreakerAsync(
        ProcessingContext<TInput> ctx,
        CancellationToken ct
    )
    {
        if (_circuitBreaker != null && !_circuitBreaker.AllowRequest())
        {
            if (_retryQueue != null)
            {
                var policy =
                    _options.DefaultRetryPolicy ?? new RetryPolicy(3, TimeSpan.FromSeconds(1));
                await _retryQueue
                    .EnqueueAsync(
                        ctx,
                        policy,
                        0,
                        new SmartPipeError("CB open", ErrorType.Transient, "CircuitBreaker"),
                        ct
                    )
                    .ConfigureAwait(false);
            }
            return false;
        }
        return true;
    }

    private async ValueTask<(
        ProcessingResult<TOutput> Result,
        long ElapsedMs
    )> TransformWithTimeoutAsync(ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        var sw = Environment.TickCount64;
        try
        {
            var r = await ExecuteTransformerChainAsync(ctx, ct).ConfigureAwait(false);
            return (r, Environment.TickCount64 - sw);
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogError(ex, "Transform error for TraceId: {TraceId}", ctx.TraceId);
            return (
                ProcessingResult<TOutput>.Failure(
                    new SmartPipeError(ex.Message, ErrorType.Permanent, "TransformError", ex),
                    ctx.TraceId
                ),
                Environment.TickCount64 - sw
            );
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogError(ex, "Transform error for TraceId: {TraceId}", ctx.TraceId);
            return (
                ProcessingResult<TOutput>.Failure(
                    new SmartPipeError(ex.Message, ErrorType.Permanent, "TransformError", ex),
                    ctx.TraceId
                ),
                Environment.TickCount64 - sw
            );
        }
        catch (TimeoutException ex)
        {
            _logger?.LogWarning(ex, "Transform timeout for TraceId: {TraceId}", ctx.TraceId);
            return (
                ProcessingResult<TOutput>.Failure(
                    new SmartPipeError(ex.Message, ErrorType.Transient, "Timeout", ex),
                    ctx.TraceId
                ),
                Environment.TickCount64 - sw
            );
        }
        catch (Exception ex) // catch-all for unexpected exceptions (ArgumentException, JsonException, NullReferenceException, etc.)
        {
            _logger?.LogError(ex, "Unexpected transform error for TraceId: {TraceId}", ctx.TraceId);
            return (
                ProcessingResult<TOutput>.Failure(
                    new SmartPipeError(ex.Message, ErrorType.Permanent, "UnexpectedError", ex),
                    ctx.TraceId
                ),
                Environment.TickCount64 - sw
            );
        }
    }

    private async ValueTask<ProcessingResult<TOutput>> ExecuteTransformerChainAsync(
        ProcessingContext<TInput> ctx,
        CancellationToken ct
    )
    {
        if (_transformers.Count == 0)
            return ProcessingResult<TOutput>.Failure(
                new SmartPipeError("No transformers", ErrorType.Permanent),
                ctx.TraceId
            );

        ProcessingResult<TOutput> result = default;
        ProcessingContext<TInput> current = ctx;

        for (int i = 0; i < _transformers.Count; i++)
        {
            result = await PipelineCancellation
                .WithTimeoutAsync(
                    _transformers[i].TransformAsync(current, ct),
                    _options.AttemptTimeout,
                    ctx.TraceId
                )
                .ConfigureAwait(false);

            if (!result || i == _transformers.Count - 1)
                return result;

            if (result.Value is not TInput nextPayload)
            {
                return ProcessingResult<TOutput>.Failure(
                    new SmartPipeError(
                        "Multiple direct-channel transformers require compatible same-type payloads. "
                            + "Use PipelineBuilder typed stages for TInput -> TMid -> TOutput pipelines.",
                        ErrorType.Permanent,
                        "TransformerChainTypeMismatch"
                    ),
                    ctx.TraceId
                );
            }

            current = new ProcessingContext<TInput>(nextPayload, ctx.Metadata)
            {
                TraceId = ctx.TraceId,
                EnterPipelineTicks = ctx.EnterPipelineTicks,
            };
        }

        return result;
    }

    private void RecordMetrics(long elapsedMs)
    {
        _adaptiveMetrics.Update(elapsedMs);
        _latencyHistogram.Record(elapsedMs);
        Metrics.RecordProcessed(elapsedMs);
    }

    private async ValueTask HandleFailureAsync(
        ProcessingContext<TInput> ctx,
        ProcessingResult<TOutput> result,
        Activity? activity,
        CancellationToken ct
    )
    {
        Metrics.RecordFailed();
        _circuitBreaker?.RecordFailure();
        activity?.SetTag("smartpipe.error.type", result.Error?.Type.ToString());
        activity?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);
        LogFailure(result.Error);

        var retryResult = ShouldRetry(result.Error)
            ? await TryScheduleRetryAsync(ctx, result, ct).ConfigureAwait(false)
            : RetryScheduleResult.NotScheduled;

        if (!retryResult.Scheduled)
            await WriteTerminalFailureAsync(
                    ctx,
                    result,
                    retryResult.DeadLetterWritten,
                    ct
                )
                .ConfigureAwait(false);

        if (!_options.ContinueOnError)
            _internalCts.Cancel();
    }

    private static void LogFailure(SmartPipeError? error)
    {
        // Logging is handled via Activity tags above; this method exists for extensibility
    }

    private static bool ShouldRetry(SmartPipeError? error)
    {
        return error?.Type == ErrorType.Transient;
    }

    private async ValueTask<RetryScheduleResult> TryScheduleRetryAsync(
        ProcessingContext<TInput> ctx,
        ProcessingResult<TOutput> result,
        CancellationToken ct
    )
    {
        if (_retryQueue == null)
            return RetryScheduleResult.NotScheduled;

        Metrics.RecordRetry();
        var policy = _options.DefaultRetryPolicy ?? new RetryPolicy(3, TimeSpan.FromSeconds(1));
        var retryCount = GetRetryCount(ctx);
        var enqueued = await _retryQueue
            .EnqueueAsync(ctx, policy, retryCount, result.Error!.Value, ct)
            .ConfigureAwait(false);

        if (enqueued)
            return RetryScheduleResult.ScheduledResult;

        var deadLetterWritten =
            _options.DeadLetterSink is not null
            && (
                retryCount >= policy.MaxRetries
                || _options.RetryQueueOverflowPolicy == RetryQueueOverflowPolicy.DeadLetter
            );
        return new RetryScheduleResult(Scheduled: false, DeadLetterWritten: deadLetterWritten);
    }

    private async Task ConsumeAdaptiveAsync(CancellationToken ct, int consumerIndex)
    {
        var reader = _inputBuffer!.CreateReader(consumerIndex);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ctx = await reader.ReadAsync(ct).ConfigureAwait(false);

                if (!ShouldProcessItem(ctx, consumerIndex))
                    continue;
                if (!await HandleCircuitBreakerAsync(ctx, ct).ConfigureAwait(false))
                    continue;

                await using var lease = await _adaptiveInFlightLimiter!
                    .AcquireAsync(ct)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _activeConsumerCount);
                try
                {
                    var (result, elapsed) = await ProcessTransformAsync(ctx, ct).ConfigureAwait(false);
                    await HandleTransformResultAsync(ctx, result, elapsed, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeConsumerCount);
                }
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
        catch (ChannelClosedException)
        {
            // Normal completion path after the producer and retry queue complete the input buffer.
        }
    }

    private async ValueTask HandleDeadLetterAsync(
        ProcessingContext<TInput> ctx,
        ProcessingResult<TOutput> result,
        CancellationToken ct
    )
    {
        if (_options.DeadLetterSink == null)
            return;

        var deadLetterError = result.Error ?? new SmartPipeError("Unknown", ErrorType.Permanent);
        var failedAtUtc = DateTime.SpecifyKind(_clock.UtcNow, DateTimeKind.Utc);
        var deadLetter = new DeadLetterEnvelope<TInput>
        {
            SchemaVersion = 1,
            PipelineId = GetMetadataValue(ctx, ProcessingContext<TInput>.LineagePipeline, "legacy"),
            RunId = GetMetadataValue(ctx, "run_id", "legacy"),
            TraceId = ctx.TraceId,
            StageId = deadLetterError.Category ?? "legacy",
            StageName = deadLetterError.Category ?? "LegacySmartPipeChannel",
            OriginalPayload = ctx.Payload,
            Metadata = MetadataBag.From(ctx.Metadata),
            Error = deadLetterError,
            Attempt = GetRetryCount(ctx),
            FailedAtUtc = new DateTimeOffset(failedAtUtc),
        };

        await _options
            .DeadLetterSink.WriteAsync(
                ProcessingResult<object>.Success(deadLetter, ctx.TraceId),
                ct
            )
            .ConfigureAwait(false);
    }

    private void HandleSuccess(ProcessingResult<TOutput> result, Activity? activity, long elapsedMs)
    {
        _circuitBreaker?.RecordSuccess();
        activity?.SetTag("smartpipe.latency_ms", elapsedMs);
        if (
            _options.IsEnabled("SecretScanner")
            && result.Value is string str
            && SecretScanner.HasSecrets(str)
        )
            activity?.SetTag("smartpipe.secret_found", true);
    }

    private async ValueTask WriteOutputAsync(ProcessingResult<TOutput> result, CancellationToken ct)
    {
        Metrics.SmoothLatencyMs = _adaptiveMetrics.SmoothLatencyMs;
        Metrics.SmoothThroughput = _adaptiveMetrics.SmoothThroughputPerSec;
        Metrics.QueueSize = GetInputQueueSize();
        _options.OnMetrics?.Invoke(Metrics);
        await _outputChannel!.Writer.WriteAsync(result, ct).ConfigureAwait(false);
    }

    private async Task ConsumeOutputAsync(CancellationToken ct)
    {
        try
        {
            await foreach (
                var result in _outputChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false)
            )
            {
                await WriteExternalOutputAsync(result, ct).ConfigureAwait(false);
                foreach (var sink in _sinks)
                    await sink.WriteAsync(result, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Operation cancellation is expected behavior in pipeline processing
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
    }

    private async ValueTask WriteExternalOutputAsync(
        ProcessingResult<TOutput> result,
        CancellationToken ct
    )
    {
        var channel = _backgroundOutputChannel;
        if (channel == null)
            return;

        try
        {
            await channel.Writer.WriteAsync(result, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (IsLifecycleStopping(ct))
        {
        }
        catch (OperationCanceledException) when (IsLifecycleStopping(ct))
        {
        }
    }

    private async Task ProcessRetriesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await HandleRetryLoopItemAsync(ct).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Operation cancellation is expected behavior in pipeline processing
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
        catch (ChannelClosedException ex)
        {
            _logger?.LogDebug(ex, "Retry channel closed, exiting retry loop");
        }
    }

    private async Task<bool> HandleRetryLoopItemAsync(CancellationToken ct)
    {
        var item = await _retryQueue!.TryGetNextAsync(ct).ConfigureAwait(false);
        if (item == null)
        {
            if (ShouldBreakRetryLoop())
                return false;
            await Task.Delay(50, ct).ConfigureAwait(false);
            return true;
        }

        await HandleRetryItemAsync(item.Value, ct).ConfigureAwait(false);
        return true;
    }

    private bool ShouldBreakRetryLoop()
    {
        // Producer is done, no consumers are actively processing,
        // and the input channel is drained — no more retries can be generated.
        return _producerCompleted
            && Volatile.Read(ref _activeConsumerCount) == 0
            && GetInputQueueSize() == 0
            && (_retryQueue?.HasPendingItems != true);
    }

    private async Task HandleRetryItemAsync(RetryItem<TInput> ri, CancellationToken ct)
    {
        await WriteRetryToInputAsync(ri, ct).ConfigureAwait(false);
    }

    private async Task HandleRetryBudgetExhaustedAsync(RetryItem<TInput> ri, CancellationToken ct)
    {
        await WriteTerminalFailureAsync(
                ri.Context,
                ProcessingResult<TOutput>.Failure(ri.Error, ri.Context.TraceId),
                deadLetterAlreadyWritten: false,
                ct
            )
            .ConfigureAwait(false);
    }

    private async ValueTask WriteTerminalFailureAsync(
        ProcessingContext<TInput> ctx,
        ProcessingResult<TOutput> result,
        bool deadLetterAlreadyWritten,
        CancellationToken ct
    )
    {
        if (!deadLetterAlreadyWritten)
            await HandleDeadLetterAsync(ctx, result, ct).ConfigureAwait(false);

        if (_outputChannel is null || _outputChannel.Reader.Completion.IsCompleted)
            return;

        try
        {
            await WriteOutputAsync(result, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            _logger?.LogDebug(ex, "Output channel closed while writing terminal failure");
        }
    }

    private async Task WriteRetryToInputAsync(RetryItem<TInput> ri, CancellationToken ct)
    {
        ri.Context.Metadata[RetryCountMetadataKey] = ri.RetryCount.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );

        try
        {
            await WriteInputAsync(ri.Context, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            _logger?.LogDebug(ex, "Input channel closed while requeueing retry item");
            await HandleRetryBudgetExhaustedAsync(ri, ct).ConfigureAwait(false);
        }
    }

    private static int GetRetryCount(ProcessingContext<TInput> ctx)
    {
        return ctx.Metadata.TryGetValue(RetryCountMetadataKey, out var value)
            && int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var retryCount
            )
            ? retryCount
            : 0;
    }

    private static string GetMetadataValue(
        ProcessingContext<TInput> ctx,
        string key,
        string fallback
    ) => ctx.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : fallback;

    private readonly record struct RetryScheduleResult(bool Scheduled, bool DeadLetterWritten)
    {
        public static RetryScheduleResult ScheduledResult { get; } =
            new(Scheduled: true, DeadLetterWritten: false);

        public static RetryScheduleResult NotScheduled { get; } =
            new(Scheduled: false, DeadLetterWritten: false);
    }

    private async Task MonitorParallelismAsync(CancellationToken ct)
    {
        if (_inputBuffer != null)
        {
            await MonitorAdaptiveParallelismAsync(ct).ConfigureAwait(false);
            return;
        }

        try
        {
            while (
                !ct.IsCancellationRequested
                && _inputChannel != null
                && !_inputChannel.Reader.Completion.IsCompleted
            )
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                _adaptiveParallelism?.Update(
                    _adaptiveMetrics.PredictNextLatency(),
                    _inputChannel.Reader.Count
                );
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Operation cancellation is expected behavior in pipeline processing
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
    }

    private async Task MonitorAdaptiveParallelismAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_inputBufferCompleted)
            {
                await Task.Delay(_options.AdaptiveParallelism.SamplingInterval, ct).ConfigureAwait(false);
                ApplyAdaptiveParallelismDecision();
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
    }

    private void ApplyAdaptiveParallelismDecision()
    {
        if (_inputBuffer == null || _adaptiveInFlightLimiter == null || _adaptiveController == null)
            return;

        var now = DateTimeOffset.UtcNow;
        var bufferSnapshot = _inputBuffer.CaptureSnapshot();
        var metricsSnapshot = Metrics.CaptureSnapshot();
        var processedDelta = metricsSnapshot.ItemsProcessed - _lastAdaptiveProcessed;
        var failedDelta = metricsSnapshot.ItemsFailed - _lastAdaptiveFailed;
        var retriedDelta = metricsSnapshot.Retries - _lastAdaptiveRetried;

        _lastAdaptiveProcessed = metricsSnapshot.ItemsProcessed;
        _lastAdaptiveFailed = metricsSnapshot.ItemsFailed;
        _lastAdaptiveRetried = metricsSnapshot.Retries;

        var snapshot = new AdaptiveParallelismSnapshot(
            now,
            bufferSnapshot.ActiveLaneCount,
            bufferSnapshot.TotalLaneCount,
            bufferSnapshot.ActiveBufferedItems,
            bufferSnapshot.InactiveBufferedItems,
            bufferSnapshot.TotalBufferedItems,
            bufferSnapshot.ActiveQueuePressure,
            bufferSnapshot.TotalQueuePressure,
            _adaptiveInFlightLimiter.InUse,
            _adaptiveInFlightLimiter.CurrentLimit,
            processedDelta,
            failedDelta,
            retriedDelta,
            TimeSpan.FromMilliseconds(_latencyHistogram.GetPercentile(0.95)),
            now - _lastAdaptiveDecisionUtc
        );

        var decision = _adaptiveController.Decide(snapshot);
        if (decision.TargetActiveLanes == bufferSnapshot.ActiveLaneCount
            && decision.TargetInFlightLimit == _adaptiveInFlightLimiter.CurrentLimit)
            return;

        _inputBuffer.RequestActiveLaneCount(decision.TargetActiveLanes);
        _adaptiveInFlightLimiter.UpdateLimit(decision.TargetInFlightLimit);
        _lastAdaptiveDecisionUtc = now;
        _logger?.LogDebug(
            "Adaptive parallelism decision {Reason}: active lanes {ActiveLanes}, in-flight limit {InFlightLimit}",
            decision.Reason,
            decision.TargetActiveLanes,
            decision.TargetInFlightLimit
        );
    }

    private async ValueTask WriteInputAsync(ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        if (_inputBuffer != null)
        {
            await _inputBuffer.WriteAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        await _inputChannel!.Writer.WriteAsync(ctx, ct).ConfigureAwait(false);
    }

    private void CompleteInput()
    {
        if (_inputBuffer != null)
        {
            _inputBufferCompleted = true;
            _inputBuffer.Complete();
            return;
        }

        _inputChannel!.Writer.TryComplete();
    }

    private int GetInputQueueSize()
    {
        if (_inputBuffer != null)
            return SaturatingToInt32(_inputBuffer.CaptureSnapshot().TotalBufferedItems);

        return _inputChannel?.Reader.Count ?? 0;
    }

    private static int SaturatingToInt32(long value)
    {
        if (value <= 0)
            return 0;
        if (value >= int.MaxValue)
            return int.MaxValue;

        return (int)value;
    }

}

/// <summary>Represents the state of a pipeline during its lifecycle.</summary>
public enum PipelineState
{
    /// <summary>Pipeline has not started yet.</summary>
    NotStarted,

    /// <summary>Pipeline is currently running.</summary>
    Running,

    /// <summary>Pipeline is paused (producer suspended).</summary>
    Paused,

    /// <summary>Pipeline completed successfully.</summary>
    Completed,

    /// <summary>Pipeline terminated due to an error.</summary>
    Faulted,

    /// <summary>Pipeline was cancelled by user or timeout.</summary>
    Cancelled,
}

/// <summary>Real-time dashboard data for pipeline monitoring. Immutable snapshot.</summary>
public readonly record struct PipelineDashboard(
    PipelineState State,
    int Current,
    int? Total,
    TimeSpan Elapsed,
    double P99LatencyMs,
    string CbState,
    Dictionary<string, object> Metrics
)
{
    /// <summary>Empty dashboard with default values.</summary>
    public static PipelineDashboard Empty =>
        new(PipelineState.NotStarted, 0, null, TimeSpan.Zero, 0.0, "N/A", []);
}

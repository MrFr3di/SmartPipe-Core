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
    private static readonly ActivitySource _activitySource = 
        new("SmartPipe.Core", 
            typeof(SmartPipeChannel<,>).Assembly.GetName().Version?.ToString() ?? "1.0.0");
    private readonly ILogger? _logger;
    private readonly IClock _clock;
    
    private readonly List<ISource<TInput>> _sources = new();
    private readonly List<ITransformer<TInput, TOutput>> _transformers = new();
    private readonly List<ISink<TOutput>> _sinks = new();
    private readonly SmartPipeChannelOptions _options;
    private readonly CancellationTokenSource _internalCts = new();
    private Channel<ProcessingContext<TInput>>? _inputChannel;
    private Channel<ProcessingResult<TOutput>>? _outputChannel;
    private volatile bool _producerCompleted, _isPaused;
    private volatile PipelineState _state = PipelineState.NotStarted;
    private int _isDraining = 0; // 0 = not draining, 1 = draining
    private readonly AdaptiveParallelism? _adaptiveParallelism;
    private readonly AdaptiveMetrics _adaptiveMetrics = new();
    private readonly ExponentialHistogram _latencyHistogram = new();
    private readonly RetryQueue<TInput>? _retryQueue;
    private readonly CircuitBreaker? _circuitBreaker;
    private readonly BackpressureStrategy _backpressure;
    private readonly ReservoirSampler<TInput>? _debugSampler;
    private readonly CuckooFilter? _cuckooFilter;
    private readonly ObjectPool<ProcessingContext<TInput>>? _contextPool;
    private readonly int[]? _shardBuckets;
    private int _totalCount;
    private DateTime _startTime;

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
    public SmartPipeChannel() : this(new SmartPipeChannelOptions(), null) { }

    /// <summary>Initializes a new pipeline with the specified options.</summary>
    /// <param name="options">Pipeline configuration options.</param>
    /// <param name="clock">Optional clock for testability (defaults to TimeProviderClock()).</param>
    /// <param name="logger">Optional logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when options is null.</exception>
    public SmartPipeChannel(SmartPipeChannelOptions options, IClock? clock = null, ILogger<SmartPipeChannel<TInput, TOutput>>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.UseRendezvous)
        {
            throw new InvalidOperationException(
                "UseRendezvous=true sets capacity to 0, which violates the global constraint " +
                "\"BoundedCapacity set for all channels\". Disable UseRendezvous or remove this constraint.");
        }
        _clock = clock ?? new TimeProviderClock();
        _logger = logger;
        _backpressure = new BackpressureStrategy(options.BoundedCapacity);
        if (options.IsEnabled("RetryQueue"))
            _retryQueue = new RetryQueue<TInput>(options.BoundedCapacity, null, options.DeadLetterSink, _clock);
        if (options.IsEnabled("CircuitBreaker")) _circuitBreaker = new CircuitBreaker(clock: _clock);
        if (options.IsEnabled("DebugSampling")) _debugSampler = new ReservoirSampler<TInput>(1000);
        if (options.IsEnabled("CuckooFilter")) _cuckooFilter = new CuckooFilter();
        if (options.IsEnabled("ObjectPool")) _contextPool = new ObjectPool<ProcessingContext<TInput>>(() => new ProcessingContext<TInput>(), 256);
        if (options.IsEnabled("JumpHash")) _shardBuckets = new int[options.MaxDegreeOfParallelism];
        _adaptiveParallelism = new AdaptiveParallelism(2, options.MaxDegreeOfParallelism);
    }

    /// <summary>Adds a data source to the pipeline.</summary>
    /// <param name="source">The source to add.</param>
    public void AddSource(ISource<TInput> source) => _sources.Add(source);

    /// <summary>Adds a transformer to the pipeline.</summary>
    /// <param name="t">The transformer to add.</param>
    public void AddTransformer(ITransformer<TInput, TOutput> t) => _transformers.Add(t);

    /// <summary>Adds a sink to the pipeline.</summary>
    /// <param name="sink">The sink to add.</param>
    public void AddSink(ISink<TOutput> sink) => _sinks.Add(sink);

    /// <summary>Pauses the pipeline processing.</summary>
    public void Pause() { _isPaused = true; TransitionState(PipelineState.Paused); }

    /// <summary>Resumes the paused pipeline.</summary>
    public void Resume() { _isPaused = false; TransitionState(PipelineState.Running); }

    /// <summary>Cancels the pipeline execution.</summary>
    public void Cancel() { _internalCts.Cancel(); TransitionState(PipelineState.Cancelled); }

    private void TransitionState(PipelineState newState)
    {
        var old = _state; _state = newState;
        if (old != newState) OnStateChanged?.Invoke(old, newState);
    }

    /// <summary>Drains the pipeline by completing the input channel and flushing remaining items.</summary>
    /// <param name="timeout">Maximum time to wait for draining.</param>
    /// <remarks>Only one drain operation runs at a time (lock-free).</remarks>
    public async Task DrainAsync(TimeSpan timeout)
    {
        Pause(); 
        
        // Lock-free: only one drain at a time
        if (Interlocked.CompareExchange(ref _isDraining, 1, 0) != 0)
            return; // Already draining
        
        try 
        { 
            _inputChannel?.Writer.Complete(); 
            var sw = Stopwatch.StartNew(); 
            while (_inputChannel?.Reader.TryRead(out _) == true && sw.Elapsed < timeout) { } 
            _outputChannel?.Writer.Complete(); 
        }
        finally 
        { 
            Interlocked.Exchange(ref _isDraining, 0); 
        }
    }

    /// <summary>
    /// Disposes the pipeline by draining pending items and releasing resources.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous disposal operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _internalCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Gets a channel reader for consuming output results.</summary>
    /// <returns>Channel reader or null if not running.</returns>
    public ChannelReader<ProcessingResult<TOutput>>? AsChannelReader() => _outputChannel?.Reader;

    /// <summary>Runs the pipeline in background and returns output channel reader.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Channel reader for pipeline output.</returns>
    /// <remarks>Creates bounded channel with <see cref="SmartPipeChannelOptions.BoundedCapacity"/>.</remarks>
    public ChannelReader<ProcessingResult<TOutput>> RunInBackground(CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<ProcessingResult<TOutput>>(new BoundedChannelOptions(_options.BoundedCapacity > 0 ? _options.BoundedCapacity : 1000) { FullMode = BoundedChannelFullMode.Wait });
        _ = Task.Run(async () => { try { await RunAsync(ct); } finally { channel.Writer.TryComplete(); } }, ct);
        return channel.Reader;
    }

    /// <summary>Creates a real-time dashboard snapshot of pipeline state.</summary>
    /// <returns>Dashboard object with current metrics.</returns>
    public PipelineDashboard CreateDashboard() => new() { State = _state, Current = _totalCount, Total = null, Elapsed = _startTime != default ? _clock.UtcNow - _startTime : TimeSpan.Zero, P99LatencyMs = _latencyHistogram.P99, CBState = _circuitBreaker?.State.ToString() ?? "N/A", Metrics = Metrics.Export() };

    private void Validate() { if (_sources.Count == 0) throw new InvalidOperationException("At least one source required."); if (_transformers.Count == 0) throw new InvalidOperationException("At least one transformer required."); if (_sinks.Count == 0) throw new InvalidOperationException("At least one sink required."); }

    /// <summary>Runs the pipeline: initializes components, processes items, handles retries and errors.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Pipeline flow: Sources → Transformers → Sinks with optional retry queue and circuit breaker.
    /// Uses <see cref="SmartPipeChannelOptions.BoundedCapacity"/> for backpressure.
    /// </remarks>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Validate(); TransitionState(PipelineState.Running); _startTime = _clock.UtcNow;
        using var totalTimeoutCts = new CancellationTokenSource(_options.TotalRequestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _internalCts.Token, totalTimeoutCts.Token);
        var token = linkedCts.Token; _producerCompleted = _isPaused = false;
        using var activity = _activitySource.StartActivity("Pipeline.Run");
        activity?.SetTag("smartpipe.parallelism", _options.MaxDegreeOfParallelism);
        try
        {
            await RunPipelineAsync(token, activity).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (totalTimeoutCts.IsCancellationRequested) { HandleRunAsyncErrors(activity, "TotalRequestTimeout"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { TransitionState(PipelineState.Cancelled); }
        catch (Exception ex) { _logger?.LogError(ex, "Pipeline faulted due to unhandled exception"); TransitionState(PipelineState.Faulted); throw; }
    }

    private void HandleRunAsyncErrors(Activity? activity, string errorTag)
    {
        activity?.SetStatus(ActivityStatusCode.Error, errorTag);
        TransitionState(PipelineState.Faulted);
    }

    private async Task RunPipelineAsync(CancellationToken token, Activity? activity)
    {
        await InitializePipelineAsync(token).ConfigureAwait(false);
        var retryTask = _retryQueue != null ? ProcessRetriesAsync(token) : null;
        var producerTask = ProduceAsync(token);
        int p = _adaptiveParallelism?.Current ?? _options.MaxDegreeOfParallelism;
        var consumers = new Task[p]; for (int i = 0; i < p; i++) consumers[i] = ConsumeAsync(token, i);
        var monitor = MonitorParallelismAsync(token); var sink = ConsumeOutputAsync(token);
        await producerTask.ConfigureAwait(false); _producerCompleted = true; _inputChannel!.Writer.Complete();
        await Task.WhenAll(consumers).ConfigureAwait(false); _outputChannel!.Writer.Complete();
        if (retryTask != null) await retryTask.ConfigureAwait(false);
        await sink.ConfigureAwait(false); await monitor.ConfigureAwait(false);
        await DisposePipelineAsync(token).ConfigureAwait(false);
        ChannelPool.CloseChannel(_inputChannel); ChannelPool.CloseChannel(_outputChannel);
        activity?.SetStatus(ActivityStatusCode.Ok); TransitionState(PipelineState.Completed);
    }

    private async Task InitializePipelineAsync(CancellationToken token)
    {
        foreach (var s in _sources) await s.InitializeAsync(token).ConfigureAwait(false);
        foreach (var t in _transformers) await t.InitializeAsync(token).ConfigureAwait(false);
        foreach (var s in _sinks) await s.InitializeAsync(token).ConfigureAwait(false);
        _inputChannel = ChannelPool.RentBounded<ProcessingContext<TInput>>(_options.UseRendezvous ? 0 : _options.BoundedCapacity, _options.FullMode);
        _outputChannel = ChannelPool.RentBounded<ProcessingResult<TOutput>>(_options.UseRendezvous ? 0 : _options.BoundedCapacity, _options.FullMode);
        Metrics = new SmartPipeMetrics();
    }

    private async Task DisposePipelineAsync(CancellationToken token)
    {
        foreach (var s in _sources) await s.DisposeAsync().ConfigureAwait(false);
        foreach (var t in _transformers) await t.DisposeAsync().ConfigureAwait(false);
        foreach (var s in _sinks) await s.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Processes a single item through the transformer chain.</summary>
    /// <param name="ctx">Processing context with input payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Processing result with output or error.</returns>
    /// <remarks>Checks circuit breaker before processing. Uses first transformer only.</remarks>
    public async ValueTask<ProcessingResult<TOutput>> ProcessSingleAsync(ProcessingContext<TInput> ctx, CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("ProcessSingle"); activity?.SetTag("smartpipe.trace_id", ctx.TraceId);
        if (_circuitBreaker != null && !_circuitBreaker.AllowRequest()) return ProcessingResult<TOutput>.Failure(new SmartPipeError("Circuit breaker is open", ErrorType.Transient, "CircuitBreaker"), ctx.TraceId);
        foreach (var t in _transformers)
        {
            var sw = Environment.TickCount64;
            var result = await PipelineCancellation.WithTimeoutAsync(t.TransformAsync(ctx, ct), _options.AttemptTimeout, ctx.TraceId).ConfigureAwait(false);
            var elapsed = Environment.TickCount64 - sw; _adaptiveMetrics.Update(elapsed); _latencyHistogram.Record(elapsed); Metrics.RecordProcessed(elapsed);
            if (result) { _circuitBreaker?.RecordSuccess(); activity?.SetTag("smartpipe.latency_ms", elapsed); }
            else { _circuitBreaker?.RecordFailure(); Metrics.RecordFailed(); activity?.SetStatus(ActivityStatusCode.Error, result.Error?.Message); }
            return result;
        }
        return ProcessingResult<TOutput>.Failure(new SmartPipeError("No transformers", ErrorType.Permanent), ctx.TraceId);
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
                await ProcessSourceItemAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ct.IsCancellationRequested == false)
        { using var a = _activitySource.StartActivity("Source.Error"); a?.SetStatus(ActivityStatusCode.Error, ex.Message); _logger?.LogError(ex, "Source error in ProduceAsync"); Metrics.RecordFailed(); }
        catch (NotSupportedException ex) when (ct.IsCancellationRequested == false)
        { using var a = _activitySource.StartActivity("Source.Error"); a?.SetStatus(ActivityStatusCode.Error, ex.Message); _logger?.LogError(ex, "Source error in ProduceAsync"); Metrics.RecordFailed(); }
        catch (IOException ex) when (ct.IsCancellationRequested == false)
        { using var a = _activitySource.StartActivity("Source.Error"); a?.SetStatus(ActivityStatusCode.Error, ex.Message); _logger?.LogError(ex, "Source error in ProduceAsync"); Metrics.RecordFailed(); }
        catch (Exception ex) when (ct.IsCancellationRequested == false && _options.ContinueOnError)
        { using var a = _activitySource.StartActivity("Source.Error"); a?.SetStatus(ActivityStatusCode.Error, ex.Message); _logger?.LogError(ex, "Source error in ProduceAsync (ContinueOnError)"); Metrics.RecordFailed(); }
    }

    private async ValueTask ProcessSourceItemAsync(ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        while (_isPaused && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(10, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
        
        Metrics.QueueSize = _inputChannel?.Reader.Count ?? 0;
        
        if (_inputChannel != null) 
        { 
            _backpressure.UpdateThroughput(_adaptiveMetrics.SmoothThroughputPerSec, _adaptiveMetrics.PredictNextLatency()); 
            await _backpressure.ThrottleAsync(_inputChannel.Reader.Count, ct).ConfigureAwait(false); 
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
        await _inputChannel!.Writer.WriteAsync(ctx, ct).ConfigureAwait(false);
        int current = Interlocked.Increment(ref _totalCount);
        _options.OnMetrics?.Invoke(Metrics);
        _options.OnProgress?.Invoke(current, null, _clock.UtcNow - _startTime, null);
    }

    private async Task ConsumeAsync(CancellationToken ct, int consumerIndex)
    {
        try
        {
            await foreach (var ctx in _inputChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!ShouldProcessItem(ctx, consumerIndex)) continue;
                if (!await HandleCircuitBreakerAsync(ctx, ct).ConfigureAwait(false)) continue;
                var (result, elapsed) = await ProcessTransformAsync(ctx, ct).ConfigureAwait(false);
                await HandleTransformResultAsync(ctx, result, elapsed, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Operation cancellation is expected behavior in pipeline processing
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
    }

    private async ValueTask<(ProcessingResult<TOutput> Result, long ElapsedMs)> ProcessTransformAsync(ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        using var activity = _activitySource.StartActivity("Transform"); activity?.SetTag("smartpipe.trace_id", ctx.TraceId);
        var (result, elapsed) = await TransformWithTimeoutAsync(ctx, ct).ConfigureAwait(false);
        RecordMetrics(elapsed);
        return (result, elapsed);
    }

    private async Task HandleTransformResultAsync(ProcessingContext<TInput> ctx, ProcessingResult<TOutput> result, long elapsed, CancellationToken ct)
    {
        if (!result)
        {
            if (result.Error?.Category == "Filtered") { await WriteOutputAsync(result, ct).ConfigureAwait(false); _contextPool?.Return(ctx); return; }
            await HandleFailureAsync(ctx, result, null, ct).ConfigureAwait(false);
        }
        else HandleSuccess(result, null, elapsed);
        await WriteOutputAsync(result, ct).ConfigureAwait(false); _contextPool?.Return(ctx);
    }

    private bool ShouldProcessItem(ProcessingContext<TInput> ctx, int consumerIndex) => _shardBuckets == null || JumpHash.Hash(ctx.TraceId, _shardBuckets.Length) == consumerIndex;

    private async ValueTask<bool> HandleCircuitBreakerAsync(ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        if (_circuitBreaker != null && !_circuitBreaker.AllowRequest())
        {
            if (_retryQueue != null)
            {
                var policy = _options.DefaultRetryPolicy ?? new RetryPolicy(3, TimeSpan.FromSeconds(1));
                await _retryQueue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("CB open", ErrorType.Transient, "CircuitBreaker"), ct).ConfigureAwait(false);
            }
            return false;
        }
        return true;
    }

    private async ValueTask<(ProcessingResult<TOutput> Result, long ElapsedMs)> TransformWithTimeoutAsync(ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        var sw = Environment.TickCount64;
        try { var r = await PipelineCancellation.WithTimeoutAsync(_transformers[0].TransformAsync(ctx, ct), _options.AttemptTimeout, ctx.TraceId).ConfigureAwait(false); return (r, Environment.TickCount64 - sw); }
        catch (InvalidOperationException ex) { _logger?.LogError(ex, "Transform error for TraceId: {TraceId}", ctx.TraceId); return (ProcessingResult<TOutput>.Failure(new SmartPipeError(ex.Message, ErrorType.Permanent, "TransformError", ex), ctx.TraceId), Environment.TickCount64 - sw); }
        catch (NotSupportedException ex) { _logger?.LogError(ex, "Transform error for TraceId: {TraceId}", ctx.TraceId); return (ProcessingResult<TOutput>.Failure(new SmartPipeError(ex.Message, ErrorType.Permanent, "TransformError", ex), ctx.TraceId), Environment.TickCount64 - sw); }
        catch (TimeoutException ex) { _logger?.LogWarning(ex, "Transform timeout for TraceId: {TraceId}", ctx.TraceId); return (ProcessingResult<TOutput>.Failure(new SmartPipeError(ex.Message, ErrorType.Transient, "Timeout", ex), ctx.TraceId), Environment.TickCount64 - sw); }
    }

    private void RecordMetrics(long elapsedMs) { _adaptiveMetrics.Update(elapsedMs); _latencyHistogram.Record(elapsedMs); Metrics.RecordProcessed(elapsedMs); }

    private async ValueTask HandleFailureAsync(ProcessingContext<TInput> ctx, ProcessingResult<TOutput> result, Activity? activity, CancellationToken ct)
    {
        Metrics.RecordFailed();
        _circuitBreaker?.RecordFailure();
        activity?.SetTag("smartpipe.error.type", result.Error?.Type.ToString());
        activity?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);
        LogFailure(result.Error);

        if (ShouldRetry(result.Error))
        {
            await HandleRetryAsync(ctx, result, ct).ConfigureAwait(false);
        }
        else
        {
            await HandleDeadLetterAsync(ctx, result, ct).ConfigureAwait(false);
        }

        if (!_options.ContinueOnError) _internalCts.Cancel();
    }

    private static void LogFailure(SmartPipeError? error)
    {
        // Logging is handled via Activity tags above; this method exists for extensibility
    }

    private static bool ShouldRetry(SmartPipeError? error)
    {
        return error?.Type == ErrorType.Transient;
    }

    private async ValueTask HandleRetryAsync(ProcessingContext<TInput> ctx, ProcessingResult<TOutput> result, CancellationToken ct)
    {
        if (_retryQueue == null) return;

        Metrics.RecordRetry();
        var policy = _options.DefaultRetryPolicy ?? new RetryPolicy(3, TimeSpan.FromSeconds(1));
        bool enqueued = await _retryQueue.EnqueueAsync(ctx, policy, 0, result.Error!.Value, ct).ConfigureAwait(false);

        if (!enqueued && _options.DeadLetterSink != null)
        {
            var deadLetterError = new SmartPipeError(result.Error?.Message ?? "Retry exhausted", ErrorType.Permanent);
            await _options.DeadLetterSink.WriteAsync(ProcessingResult<object>.Failure(deadLetterError, ctx.TraceId), ct).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleDeadLetterAsync(ProcessingContext<TInput> ctx, ProcessingResult<TOutput> result, CancellationToken ct)
    {
        if (_options.DeadLetterSink == null) return;

        var deadLetterError = new SmartPipeError(result.Error?.Message ?? "Unknown", ErrorType.Permanent);
        await _options.DeadLetterSink.WriteAsync(ProcessingResult<object>.Failure(deadLetterError, ctx.TraceId), ct).ConfigureAwait(false);
    }

    private void HandleSuccess(ProcessingResult<TOutput> result, Activity? activity, long elapsedMs) { _circuitBreaker?.RecordSuccess(); activity?.SetTag("smartpipe.latency_ms", elapsedMs); if (result.Value is string str && SecretScanner.HasSecrets(str)) activity?.SetTag("smartpipe.secret_found", true); }

    private async ValueTask WriteOutputAsync(ProcessingResult<TOutput> result, CancellationToken ct) { Metrics.SmoothLatencyMs = _adaptiveMetrics.SmoothLatencyMs; Metrics.SmoothThroughput = _adaptiveMetrics.SmoothThroughputPerSec; Metrics.QueueSize = _inputChannel!.Reader.Count; _options.OnMetrics?.Invoke(Metrics); await _outputChannel!.Writer.WriteAsync(result, ct).ConfigureAwait(false); }

    private async Task ConsumeOutputAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var result in _outputChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                foreach (var sink in _sinks)
                    await sink.WriteAsync(result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Operation cancellation is expected behavior in pipeline processing
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
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
        catch (ChannelClosedException ex) { _logger?.LogDebug(ex, "Retry channel closed, exiting retry loop"); }
    }

    private async Task<bool> HandleRetryLoopItemAsync(CancellationToken ct)
    {
        var item = await _retryQueue!.TryGetNextAsync(ct).ConfigureAwait(false);
        if (item == null)
        {
            if (ShouldBreakRetryLoop()) return false;
            await Task.Delay(50, ct).ConfigureAwait(false);
            return true;
        }

        await HandleRetryItemAsync(item.Value, ct).ConfigureAwait(false);
        return true;
    }

    private bool ShouldBreakRetryLoop()
    {
        return _producerCompleted && _inputChannel!.Reader.Completion.IsCompleted;
    }

    private async Task HandleRetryItemAsync(RetryItem<TInput> ri, CancellationToken ct)
    {
        var retryBudget = ri.RetryBudget == -1 ? (int?)null : ri.RetryBudget;
        bool enqueued = await _retryQueue!.EnqueueAsync(ri.Context, ri.Policy, ri.RetryCount, ri.Error, ct, retryBudget).ConfigureAwait(false);

        if (!enqueued)
            await HandleRetryBudgetExhaustedAsync(ri, ct).ConfigureAwait(false);
        else
            HandleRetryRequeued(ri);
    }

    private async Task HandleRetryBudgetExhaustedAsync(RetryItem<TInput> ri, CancellationToken ct)
    {
        if (!_outputChannel!.Reader.Completion.IsCompleted)
        {
            try { await _outputChannel.Writer.WriteAsync(ProcessingResult<TOutput>.Failure(ri.Error, ri.Context.TraceId), ct).ConfigureAwait(false); }
            catch (ChannelClosedException ex) { _logger?.LogDebug(ex, "Output channel closed while writing retry failure"); }
        }
    }

    private void HandleRetryRequeued(RetryItem<TInput> ri)
    {
        if (!_inputChannel!.Reader.Completion.IsCompleted && !_inputChannel.Writer.TryWrite(ri.Context))
        {
            // Channel closed or full - exit
        }
    }

    private async Task MonitorParallelismAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _inputChannel != null && !_inputChannel.Reader.Completion.IsCompleted)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                _adaptiveParallelism?.Update(_adaptiveMetrics.PredictNextLatency(), _inputChannel.Reader.Count);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Operation cancellation is expected behavior in pipeline processing
            _logger?.LogDebug(ex, "Pipeline operation was cancelled");
        }
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
    Cancelled
}

/// <summary>Real-time dashboard data for pipeline monitoring.</summary>
public class PipelineDashboard
{
    /// <summary>Current pipeline state.</summary>
    public PipelineState State { get; set; }

    /// <summary>Number of items processed so far.</summary>
    public int Current { get; set; }

    /// <summary>Total items to process (null if unknown).</summary>
    public int? Total { get; set; }

    /// <summary>Elapsed time since pipeline started.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>99th percentile latency in milliseconds.</summary>
    public double P99LatencyMs { get; set; }

    /// <summary>Circuit breaker state string (or "N/A").</summary>
    public string CBState { get; set; } = "N/A";

    /// <summary>Exported metrics dictionary.</summary>
    public Dictionary<string, object> Metrics { get; set; } = new();
}

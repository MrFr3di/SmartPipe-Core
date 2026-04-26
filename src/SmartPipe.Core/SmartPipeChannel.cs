using System.Diagnostics;
using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>
/// Core pipeline engine based on System.Threading.Channels.
/// Integrates all 21 improvements: adaptive parallelism, EMA metrics, backpressure,
/// retry queue with jitter, circuit breaker with sliding window, deduplication,
/// reservoir sampling, object pooling, OpenTelemetry tracing, gracefule shutdown,
/// and pipeline timeouts.
/// </summary>
/// <typeparam name="TInput">Input element type.</typeparam>
/// <typeparam name="TOutput">Output element type.</typeparam>
public class SmartPipeChannel<TInput, TOutput> : IAsyncDisposable
{
    private static readonly ActivitySource _activitySource = new("SmartPipe.Core", "1.0.0");

    private readonly List<ISource<TInput>> _sources = new();
    private readonly List<ITransformer<TInput, TOutput>> _transformers = new();
    private readonly List<ISink<TOutput>> _sinks = new();
    private readonly SmartPipeChannelOptions _options;
    private readonly CancellationTokenSource _internalCts = new();
    private Channel<ProcessingContext<TInput>>? _inputChannel;
    private Channel<ProcessingResult<TOutput>>? _outputChannel;
    private volatile bool _producerCompleted, _isPaused;
    private readonly SemaphoreSlim _drainLock = new(1, 1);

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

    /// <summary>Pipeline configuration.</summary>
    public SmartPipeChannelOptions Options => _options;

    /// <summary>Pipeline metrics (counters, latency histogram, throughput).</summary>
    public SmartPipeMetrics Metrics { get; private set; } = new();

    /// <summary>Exponential histogram for latency percentiles (p50/p95/p99).</summary>
    public ExponentialHistogram LatencyHistogram => _latencyHistogram;

    /// <summary>Whether the pipeline is currently paused.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Create a pipeline with default options.</summary>
    public SmartPipeChannel() : this(new SmartPipeChannelOptions()) { }

    /// <summary>Create a pipeline with custom options.</summary>
    /// <param name="options">Pipeline configuration.</param>
    public SmartPipeChannel(SmartPipeChannelOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _backpressure = new BackpressureStrategy(options.BoundedCapacity);

        if (options.IsEnabled("RetryQueue"))
            _retryQueue = new RetryQueue<TInput>(options.BoundedCapacity);
        if (options.IsEnabled("CircuitBreaker"))
            _circuitBreaker = new CircuitBreaker();
        if (options.IsEnabled("DebugSampling"))
            _debugSampler = new ReservoirSampler<TInput>(1000);
        if (options.IsEnabled("CuckooFilter"))
            _cuckooFilter = new CuckooFilter();
        if (options.IsEnabled("ObjectPool"))
            _contextPool = new ObjectPool<ProcessingContext<TInput>>(
                () => new ProcessingContext<TInput>(default!), 256);
        if (options.IsEnabled("JumpHash"))
            _shardBuckets = new int[options.MaxDegreeOfParallelism];

        _adaptiveParallelism = new AdaptiveParallelism(2, options.MaxDegreeOfParallelism);
    }

    /// <summary>Add a data source to the pipeline.</summary>
    public void AddSource(ISource<TInput> source) => _sources.Add(source);

    /// <summary>Add a transformer to the pipeline.</summary>
    public void AddTransformer(ITransformer<TInput, TOutput> t) => _transformers.Add(t);

    /// <summary>Add a data sink to the pipeline.</summary>
    public void AddSink(ISink<TOutput> sink) => _sinks.Add(sink);

    /// <summary>Pause reading from sources. Items in flight will complete.</summary>
    public void Pause() => _isPaused = true;

    /// <summary>Resume reading from sources.</summary>
    public void Resume() => _isPaused = false;

    /// <summary>Gracefully drain the pipeline, completing all in-flight items.</summary>
    /// <param name="timeout">Maximum time to wait for drain.</param>
    public async Task DrainAsync(TimeSpan timeout)
    {
        Pause();
        await _drainLock.WaitAsync(timeout).ConfigureAwait(false);
        try
        {
            _inputChannel?.Writer.Complete();
            using var cts = new CancellationTokenSource(timeout);
            while (_inputChannel?.Reader.Count > 0 && !cts.Token.IsCancellationRequested)
                await Task.Delay(10, cts.Token).ConfigureAwait(false);
            _outputChannel?.Writer.Complete();
        }
        finally { _drainLock.Release(); }
    }

    /// <summary>Dispose the pipeline, draining with 5-second timeout.</summary>
    public async ValueTask DisposeAsync()
    {
        await DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Run the pipeline until sources are exhausted.
    /// Resilience order: TotalTimeout → CircuitBreaker → Retry → AttemptTimeout.</summary>
    /// <param name="ct">External cancellation token.</param>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Validate();
        using var totalTimeoutCts = new CancellationTokenSource(_options.TotalRequestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _internalCts.Token, totalTimeoutCts.Token);
        var token = linkedCts.Token;
        _producerCompleted = _isPaused = false;

        using var activity = _activitySource.StartActivity("Pipeline.Run");
        activity?.SetTag("smartpipe.parallelism", _options.MaxDegreeOfParallelism);

        try
        {
            foreach (var s in _sources) await s.InitializeAsync(token).ConfigureAwait(false);
            foreach (var t in _transformers) await t.InitializeAsync(token).ConfigureAwait(false);
            foreach (var s in _sinks) await s.InitializeAsync(token).ConfigureAwait(false);

            _inputChannel = ChannelPool.RentBounded<ProcessingContext<TInput>>(_options.BoundedCapacity, BoundedChannelFullMode.Wait);
            _outputChannel = ChannelPool.RentBounded<ProcessingResult<TOutput>>(_options.BoundedCapacity, BoundedChannelFullMode.Wait);
            Metrics = new SmartPipeMetrics();

            var retryTask = _retryQueue != null ? ProcessRetriesAsync(token) : null;
            var producerTask = ProduceAsync(token);
            int p = _adaptiveParallelism?.Current ?? _options.MaxDegreeOfParallelism;
            var consumers = new Task[p];
            for (int i = 0; i < p; i++) consumers[i] = ConsumeAsync(token, i);
            var monitor = MonitorParallelismAsync(token);
            var sink = ConsumeOutputAsync(token);

            await producerTask.ConfigureAwait(false);
            _producerCompleted = true;
            _inputChannel.Writer.Complete();
            await Task.WhenAll(consumers).ConfigureAwait(false);
            _outputChannel.Writer.Complete();
            if (retryTask != null) await retryTask.ConfigureAwait(false);
            await sink.ConfigureAwait(false);
            await monitor.ConfigureAwait(false);

            foreach (var s in _sources) await s.DisposeAsync().ConfigureAwait(false);
            foreach (var t in _transformers) await t.DisposeAsync().ConfigureAwait(false);
            foreach (var s in _sinks) await s.DisposeAsync().ConfigureAwait(false);

            ChannelPool.Return(_inputChannel);
            ChannelPool.Return(_outputChannel);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (totalTimeoutCts.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "TotalRequestTimeout");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Process a single item (for Agent Framework integration).</summary>
    /// <param name="ctx">Processing context to transform.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Processing result.</returns>
    public async ValueTask<ProcessingResult<TOutput>> ProcessSingleAsync(
        ProcessingContext<TInput> ctx, CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("ProcessSingle");
        activity?.SetTag("smartpipe.trace_id", ctx.TraceId);

        if (_circuitBreaker != null && !_circuitBreaker.AllowRequest())
            return ProcessingResult<TOutput>.Failure(
                new SmartPipeError("Circuit breaker is open", ErrorType.Transient, "CircuitBreaker"), ctx.TraceId);

        foreach (var t in _transformers)
        {
            var sw = Environment.TickCount64;
            var result = await PipelineCancellation.WithTimeoutAsync(
                t.TransformAsync(ctx, ct), _options.AttemptTimeout, ctx.TraceId).ConfigureAwait(false);
            var elapsed = Environment.TickCount64 - sw;
            _adaptiveMetrics.Update(elapsed);
            _latencyHistogram.Record(elapsed);
            Metrics.RecordProcessed(elapsed);
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
            {
                try
                {
                    await foreach (var ctx in source.ReadAsync(ct).ConfigureAwait(false))
                    {
                        while (_isPaused && !ct.IsCancellationRequested) await Task.Delay(10, ct).ConfigureAwait(false);
                        Metrics.QueueSize = _inputChannel?.Reader.Count ?? 0;
                        if (_inputChannel != null) await _backpressure.ThrottleAsync(_inputChannel.Reader.Count, ct).ConfigureAwait(false);
                        if (_cuckooFilter?.Contains(ctx.TraceId) == true) { Metrics.RecordDuplicate(); _options.OnMetrics?.Invoke(Metrics); continue; }
                        _cuckooFilter?.Add(ctx.TraceId);
                        if (_options.DeduplicationFilter?.ContainsAndAdd(ctx.TraceId) == true) { Metrics.RecordDuplicate(); _options.OnMetrics?.Invoke(Metrics); continue; }
                        _debugSampler?.Add(ctx.Payload);
                        await _inputChannel!.Writer.WriteAsync(ctx, ct).ConfigureAwait(false);
                        _options.OnMetrics?.Invoke(Metrics);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    using var activity = _activitySource.StartActivity("Source.Error");
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    Metrics.RecordFailed();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ConsumeAsync(CancellationToken ct, int consumerIndex)
    {
        try
        {
            await foreach (var ctx in _inputChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_shardBuckets != null && JumpHash.Hash(ctx.TraceId, _shardBuckets.Length) != consumerIndex) continue;

                if (_circuitBreaker != null && !_circuitBreaker.AllowRequest())
                {
                    if (_retryQueue != null)
                        await _retryQueue.EnqueueAsync(ctx, new RetryPolicy(3, TimeSpan.FromSeconds(1)), 0,
                            new SmartPipeError("CB open", ErrorType.Transient, "CircuitBreaker"), ct).ConfigureAwait(false);
                    continue;
                }

                using var activity = _activitySource.StartActivity("Transform");
                activity?.SetTag("smartpipe.trace_id", ctx.TraceId);

                var sw = Environment.TickCount64;
                ProcessingResult<TOutput> result;
                try { result = await PipelineCancellation.WithTimeoutAsync(_transformers[0].TransformAsync(ctx, ct), _options.AttemptTimeout, ctx.TraceId).ConfigureAwait(false); }
                catch (Exception ex) { result = ProcessingResult<TOutput>.Failure(new SmartPipeError(ex.Message, ErrorType.Permanent, "Exception", ex), ctx.TraceId); }
                var elapsed = Environment.TickCount64 - sw;
                _adaptiveMetrics.Update(elapsed);
                _latencyHistogram.Record(elapsed);
                Metrics.RecordProcessed(elapsed);

                if (!result)
                {
                    Metrics.RecordFailed();
                    _circuitBreaker?.RecordFailure();
                    activity?.SetTag("smartpipe.error.type", result.Error?.Type.ToString());
                    activity?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);
                    if (result.Error?.Type == ErrorType.Transient && _retryQueue != null)
                    {
                        Metrics.RecordRetry();
                        await _retryQueue.EnqueueAsync(ctx, new RetryPolicy(3, TimeSpan.FromSeconds(1)), 0, result.Error!.Value, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!_options.ContinueOnError) _internalCts.Cancel();
                }
                else { _circuitBreaker?.RecordSuccess(); activity?.SetTag("smartpipe.latency_ms", elapsed); }

                if (result.Value is string str && SecretScanner.HasSecrets(str)) activity?.SetTag("smartpipe.secret_found", true);
                Metrics.SmoothLatencyMs = _adaptiveMetrics.SmoothLatencyMs;
                Metrics.SmoothThroughput = _adaptiveMetrics.SmoothThroughputPerSec;
                Metrics.QueueSize = _inputChannel.Reader.Count;
                _options.OnMetrics?.Invoke(Metrics);
                await _outputChannel!.Writer.WriteAsync(result, ct).ConfigureAwait(false);
                _contextPool?.Return(ctx);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ConsumeOutputAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var result in _outputChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                foreach (var sink in _sinks) await sink.WriteAsync(result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ProcessRetriesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var item = await _retryQueue!.TryGetNextAsync(ct).ConfigureAwait(false);
                if (item == null) { if (_producerCompleted && _inputChannel!.Reader.Completion.IsCompleted) break; await Task.Delay(50, ct).ConfigureAwait(false); continue; }
                var ri = item.Value;
                if (ri.RetryCount < ri.Policy.MaxRetries && !_inputChannel!.Reader.Completion.IsCompleted)
                    await _inputChannel.Writer.WriteAsync(ri.Context, ct).ConfigureAwait(false);
                else if (!_outputChannel!.Reader.Completion.IsCompleted)
                    await _outputChannel.Writer.WriteAsync(ProcessingResult<TOutput>.Failure(ri.Error, ri.Context.TraceId), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task MonitorParallelismAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _inputChannel != null && !_inputChannel.Reader.Completion.IsCompleted)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                _adaptiveParallelism?.Update(_adaptiveMetrics.SmoothLatencyMs, _inputChannel.Reader.Count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void Validate()
    {
        if (_sources.Count == 0) throw new InvalidOperationException("At least one source required.");
        if (_transformers.Count == 0) throw new InvalidOperationException("At least one transformer required.");
        if (_sinks.Count == 0) throw new InvalidOperationException("At least one sink required.");
    }
}

using System.Diagnostics;
using System.Threading.Channels;

namespace SmartPipe.Core;

public class SmartPipeChannel<TInput, TOutput> : IAsyncDisposable
{
    private static readonly ActivitySource _activitySource = new("SmartPipe.Core", "1.0.2");
    
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

    public SmartPipeChannelOptions Options => _options;
    public SmartPipeMetrics Metrics { get; private set; } = new();
    public ExponentialHistogram LatencyHistogram => _latencyHistogram;
    public bool IsPaused => _isPaused;

    public SmartPipeChannel() : this(new SmartPipeChannelOptions()) { }

    public SmartPipeChannel(SmartPipeChannelOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _backpressure = new BackpressureStrategy(options.BoundedCapacity);
        if (options.IsEnabled("RetryQueue")) _retryQueue = new RetryQueue<TInput>(options.BoundedCapacity);
        if (options.IsEnabled("CircuitBreaker")) _circuitBreaker = new CircuitBreaker();
        if (options.IsEnabled("DebugSampling")) _debugSampler = new ReservoirSampler<TInput>(1000);
        if (options.IsEnabled("CuckooFilter")) _cuckooFilter = new CuckooFilter();
        if (options.IsEnabled("ObjectPool")) _contextPool = new ObjectPool<ProcessingContext<TInput>>(() => new ProcessingContext<TInput>(), 256);
        if (options.IsEnabled("JumpHash")) _shardBuckets = new int[options.MaxDegreeOfParallelism];
        _adaptiveParallelism = new AdaptiveParallelism(2, options.MaxDegreeOfParallelism);
    }

    public void AddSource(ISource<TInput> source) => _sources.Add(source);
    public void AddTransformer(ITransformer<TInput, TOutput> t) => _transformers.Add(t);
    public void AddSink(ISink<TOutput> sink) => _sinks.Add(sink);
    public void Pause() => _isPaused = true;
    public void Resume() => _isPaused = false;

    public async Task DrainAsync(TimeSpan timeout)
    {
        Pause();
        await _drainLock.WaitAsync(timeout).ConfigureAwait(false);
        try
        {
            _inputChannel?.Writer.Complete();
            var sw = Stopwatch.StartNew();
            while (_inputChannel?.Reader.TryRead(out _) == true && sw.Elapsed < timeout) { }
            _outputChannel?.Writer.Complete();
        }
        finally { _drainLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Get the output channel reader for SignalR/gRPC integration.</summary>
    public ChannelReader<ProcessingResult<TOutput>>? AsChannelReader() => _outputChannel?.Reader;

    /// <summary>Run pipeline in background, returning a ChannelReader for streaming consumption.</summary>
    public ChannelReader<ProcessingResult<TOutput>> RunInBackground(CancellationToken ct = default)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<ProcessingResult<TOutput>>(
            new System.Threading.Channels.BoundedChannelOptions(_options.BoundedCapacity > 0 ? _options.BoundedCapacity : 1000)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
        _ = Task.Run(async () =>
        {
            try { await RunAsync(ct); }
            finally { channel.Writer.TryComplete(); }
        }, ct);
        return channel.Reader;
    }

    private void Validate()
    {
        if (_sources.Count == 0) throw new InvalidOperationException("At least one source required.");
        if (_transformers.Count == 0) throw new InvalidOperationException("At least one transformer required.");
        if (_sinks.Count == 0) throw new InvalidOperationException("At least one sink required.");
    }

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

            _inputChannel = ChannelPool.RentBounded<ProcessingContext<TInput>>(_options.UseRendezvous ? 0 : _options.BoundedCapacity, _options.FullMode);
            _outputChannel = ChannelPool.RentBounded<ProcessingResult<TOutput>>(_options.UseRendezvous ? 0 : _options.BoundedCapacity, _options.FullMode);
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
                        if (_inputChannel != null)
                        {
                            _backpressure.UpdateThroughput(_adaptiveMetrics.SmoothThroughputPerSec);
                            await _backpressure.ThrottleAsync(_inputChannel.Reader.Count, ct).ConfigureAwait(false);
                        }
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
                if (!ShouldProcessItem(ctx, consumerIndex)) continue;
                if (!await HandleCircuitBreakerAsync(ctx, ct).ConfigureAwait(false)) continue;

                using var activity = _activitySource.StartActivity("Transform");
                activity?.SetTag("smartpipe.trace_id", ctx.TraceId);

                var (result, elapsed) = await TransformWithTimeoutAsync(ctx, ct).ConfigureAwait(false);
                RecordMetrics(elapsed);

                if (!result)
                    await HandleFailureAsync(ctx, result, activity, ct).ConfigureAwait(false);
                else
                    HandleSuccess(result, activity, elapsed);

                await WriteOutputAsync(result, ct).ConfigureAwait(false);
                _contextPool?.Return(ctx);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private bool ShouldProcessItem(ProcessingContext<TInput> ctx, int consumerIndex) =>
        _shardBuckets == null || JumpHash.Hash(ctx.TraceId, _shardBuckets.Length) == consumerIndex;

    private async ValueTask<bool> HandleCircuitBreakerAsync(ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        if (_circuitBreaker != null && !_circuitBreaker.AllowRequest())
        {
            if (_retryQueue != null)
                await _retryQueue.EnqueueAsync(ctx, new RetryPolicy(3, TimeSpan.FromSeconds(1)), 0,
                    new SmartPipeError("CB open", ErrorType.Transient, "CircuitBreaker"), ct).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private async ValueTask<(ProcessingResult<TOutput> Result, long ElapsedMs)> TransformWithTimeoutAsync(
        ProcessingContext<TInput> ctx, CancellationToken ct)
    {
        var sw = Environment.TickCount64;
        ProcessingResult<TOutput> result;
        try
        {
            result = await PipelineCancellation.WithTimeoutAsync(
                _transformers[0].TransformAsync(ctx, ct), _options.AttemptTimeout, ctx.TraceId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = ProcessingResult<TOutput>.Failure(
                new SmartPipeError(ex.Message, ErrorType.Permanent, "Exception", ex), ctx.TraceId);
        }
        return (result, Environment.TickCount64 - sw);
    }

    private void RecordMetrics(long elapsedMs)
    {
        _adaptiveMetrics.Update(elapsedMs);
        _latencyHistogram.Record(elapsedMs);
        Metrics.RecordProcessed(elapsedMs);
    }

    private async ValueTask HandleFailureAsync(ProcessingContext<TInput> ctx, ProcessingResult<TOutput> result,
        Activity? activity, CancellationToken ct)
    {
        Metrics.RecordFailed();
        _circuitBreaker?.RecordFailure();
        activity?.SetTag("smartpipe.error.type", result.Error?.Type.ToString());
        activity?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);

        if (result.Error?.Type == ErrorType.Transient && _retryQueue != null)
        {
            Metrics.RecordRetry();
            await _retryQueue.EnqueueAsync(ctx, new RetryPolicy(3, TimeSpan.FromSeconds(1)), 0,
                result.Error!.Value, ct).ConfigureAwait(false);
        }
        if (!_options.ContinueOnError) _internalCts.Cancel();
    }

    private void HandleSuccess(ProcessingResult<TOutput> result, Activity? activity, long elapsedMs)
    {
        _circuitBreaker?.RecordSuccess();
        activity?.SetTag("smartpipe.latency_ms", elapsedMs);
        if (result.Value is string str && SecretScanner.HasSecrets(str))
            activity?.SetTag("smartpipe.secret_found", true);
    }

    private async ValueTask WriteOutputAsync(ProcessingResult<TOutput> result, CancellationToken ct)
    {
        Metrics.SmoothLatencyMs = _adaptiveMetrics.SmoothLatencyMs;
        Metrics.SmoothThroughput = _adaptiveMetrics.SmoothThroughputPerSec;
        Metrics.QueueSize = _inputChannel!.Reader.Count;
        _options.OnMetrics?.Invoke(Metrics);
        await _outputChannel!.Writer.WriteAsync(result, ct).ConfigureAwait(false);
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
}

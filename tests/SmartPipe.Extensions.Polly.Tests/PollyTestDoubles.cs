using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Polly.Tests;

/// <summary>Records calls, tokens, and lifecycle events for an inner transform.</summary>
internal sealed class RecordingTransformer<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
{
    private readonly Func<ProcessingEnvelope<TInput>, int, CancellationToken, ValueTask<StageResult<TOutput>>> _transform;
    private readonly List<ProcessingEnvelope<TInput>> _envelopes = [];
    private readonly List<CancellationToken> _tokens = [];
    private int _transformCalls;
    private int _initializeCalls;
    private int _disposeCalls;

    public RecordingTransformer(
        Func<ProcessingEnvelope<TInput>, int, CancellationToken, ValueTask<StageResult<TOutput>>> transform,
        EventLog? log = null)
    {
        _transform = transform;
        Log = log ?? new EventLog();
    }

    public EventLog Log { get; }

    public Func<CancellationToken, ValueTask>? OnInitialize { get; init; }

    public Func<ValueTask>? OnDispose { get; init; }

    public int TransformCalls => Volatile.Read(ref _transformCalls);

    public int InitializeCalls => Volatile.Read(ref _initializeCalls);

    public int DisposeCalls => Volatile.Read(ref _disposeCalls);

    public IReadOnlyList<ProcessingEnvelope<TInput>> Envelopes
    {
        get { lock (_envelopes) return _envelopes.ToArray(); }
    }

    public IReadOnlyList<CancellationToken> Tokens
    {
        get { lock (_tokens) return _tokens.ToArray(); }
    }

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _initializeCalls);
        Log.Add("inner-initialize-start");
        if (OnInitialize is not null)
            await OnInitialize(ct);
        Log.Add("inner-initialize-end");
    }

    public async ValueTask<StageResult<TOutput>> TransformAsync(ProcessingEnvelope<TInput> envelope, CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref _transformCalls);
        lock (_envelopes) _envelopes.Add(envelope);
        lock (_tokens) _tokens.Add(ct);
        Log.Add($"inner-transform-{attempt}");
        return await _transform(envelope, attempt, ct);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);
        Log.Add("inner-dispose");
        if (OnDispose is not null)
            await OnDispose();
    }
}

/// <summary>Thread-safe ordered event log.</summary>
internal sealed class EventLog
{
    private readonly List<string> _events = [];

    public void Add(string value)
    {
        lock (_events) _events.Add(value);
    }

    public IReadOnlyList<string> Events
    {
        get { lock (_events) return _events.ToArray(); }
    }
}

/// <summary>A gate that tests release explicitly; continuations never run inline.</summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public async ValueTask WaitAsync(CancellationToken ct = default)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(ct);
    }

    public void Release() => _released.TrySetResult();
}

/// <summary>A fake clock that reports when Polly schedules a timer, so tests advance time only after it exists.</summary>
internal sealed class SignalingTimeProvider : FakeTimeProvider
{
    private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task TimerCreated => _timerCreated.Task;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // Token-source timers are created with an infinite due time and rescheduled, so any creation counts.
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _timerCreated.TrySetResult();
        return timer;
    }
}

/// <summary>Captures the pooled Polly context seen during execution, optionally faulting the strategy itself.</summary>
internal sealed class ContextCaptureStrategy<T>(Exception? strategyFault) : ResilienceStrategy<T>
{
    public ResilienceContext? Context { get; private set; }

    public string? OperationKeyDuringExecution { get; private set; }

    public CancellationToken TokenDuringExecution { get; private set; }

    public bool ContinueOnCapturedContextDuringExecution { get; private set; }

    public int PropertyCountDuringExecution { get; private set; } = -1;

    protected override ValueTask<Outcome<T>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<T>>> callback,
        ResilienceContext context,
        TState state)
    {
        Context = context;
        OperationKeyDuringExecution = context.OperationKey;
        TokenDuringExecution = context.CancellationToken;
        ContinueOnCapturedContextDuringExecution = context.ContinueOnCapturedContext;
        PropertyCountDuringExecution = PollyTestSupport.PropertyCount(context.Properties);
        if (strategyFault is not null)
            throw strategyFault;

        return callback(context, state);
    }
}

internal sealed class ContextCaptureOptions : ResilienceStrategyOptions;

internal static class PollyTestSupport
{
    public static ProcessingEnvelope<T> Envelope<T>(T payload) => ProcessingEnvelope<T>.Create(payload);

    public static ResiliencePipeline<StageResult<T>> Empty<T>() => ResiliencePipeline<StageResult<T>>.Empty;

    public static (ResiliencePipeline<StageResult<T>> Pipeline, ContextCaptureStrategy<StageResult<T>> Capture) Capturing<T>(
        Exception? strategyFault = null)
    {
        var capture = new ContextCaptureStrategy<StageResult<T>>(strategyFault);
        var pipeline = new ResiliencePipelineBuilder<StageResult<T>>()
            .AddStrategy(_ => capture, new ContextCaptureOptions())
            .Build();
        return (pipeline, capture);
    }

    public static async Task<PollyTransformDecorator<TInput, TOutput>> InitializedAsync<TInput, TOutput>(
        IPipelineTransformer<TInput, TOutput> inner,
        ResiliencePipeline<StageResult<TOutput>> pipeline,
        PollyTransformDecoratorOptions? options = null,
        PollyInnerTransformOwnership ownership = PollyInnerTransformOwnership.Owned)
    {
        var decorator = new PollyTransformDecorator<TInput, TOutput>(inner, pipeline, ownership, options);
        await decorator.InitializeAsync(TestContext.Current.CancellationToken);
        return decorator;
    }

    /// <summary>Polly exposes no public enumeration of context properties; tests read the backing dictionary.</summary>
    public static int PropertyCount(ResilienceProperties properties)
    {
        var options = typeof(ResilienceProperties).GetProperty("Options", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("Polly ResilienceProperties no longer exposes its backing options.");
        return ((ICollection)options.GetValue(properties)!).Count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<StageResult<TOutput>> ThrowFromInner<TOutput>(Exception exception) => throw exception;
}

/// <summary>Emits a fixed payload list from a runtime-owned source.</summary>
internal sealed class ListSource<T>(IReadOnlyList<T> items) : IPipelineSource<T>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ProcessingEnvelope<T>.Create(item);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Collects every written payload.</summary>
internal sealed class CollectingSink<T> : IPipelineSink<T>
{
    private readonly List<T> _items = [];

    public IReadOnlyList<T> Items
    {
        get { lock (_items) return _items.ToArray(); }
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        lock (_items) _items.Add(envelope.Payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

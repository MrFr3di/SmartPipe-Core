#nullable enable

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>Immutable typed SmartPipe health-check input snapshot.</summary>
/// <param name="PipelineId">Stable pipeline identifier.</param>
/// <param name="State">Current pipeline run state.</param>
/// <param name="Metrics">Immutable metrics snapshot.</param>
/// <param name="InputCapacity">Configured bounded input capacity.</param>
/// <param name="OutputCapacity">Configured bounded output capacity.</param>
/// <param name="CapturedAtUtc">Snapshot capture time.</param>
public sealed record SmartPipeHealthSnapshot(
    string PipelineId,
    PipelineRunState State,
    SmartPipeMetricsSnapshot Metrics,
    int InputCapacity,
    int OutputCapacity,
    DateTimeOffset CapturedAtUtc);

/// <summary>Exposes immutable typed pipeline health snapshots.</summary>
/// <typeparam name="TInput">Pipeline input payload type.</typeparam>
/// <typeparam name="TOutput">Pipeline output payload type.</typeparam>
public interface ISmartPipeRunHealthMonitor<TInput, TOutput>
{
    /// <summary>Captures the current typed pipeline health snapshot.</summary>
    SmartPipeHealthSnapshot CaptureSnapshot();
}

/// <summary>Tracks the current typed run for health checks without registering the runtime as a singleton.</summary>
/// <typeparam name="TInput">Pipeline input payload type.</typeparam>
/// <typeparam name="TOutput">Pipeline output payload type.</typeparam>
public sealed class SmartPipeRunHealthMonitor<TInput, TOutput>
    : ISmartPipeRunHealthMonitor<TInput, TOutput>
{
    private const int DefaultOutputCapacity = 1024;

    private readonly object _gate = new();
    private readonly string _pipelineId;
    private readonly int _inputCapacity;
    private readonly int _outputCapacity;
    private Func<PipelineRunState>? _stateProvider;
    private Func<SmartPipeMetricsSnapshot>? _metricsProvider;

    /// <summary>Creates a health monitor for a typed pipeline definition.</summary>
    /// <param name="pipelineId">Stable pipeline identifier.</param>
    /// <param name="runtimeOptions">Runtime options used by the pipeline definition.</param>
    public SmartPipeRunHealthMonitor(string pipelineId, PipelineRuntimeOptions runtimeOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        if (runtimeOptions.InputCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(runtimeOptions),
                runtimeOptions.InputCapacity,
                "Input capacity must be greater than zero.");

        if (runtimeOptions.OutputCapacity is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(runtimeOptions),
                runtimeOptions.OutputCapacity,
                "Output capacity must be greater than zero when configured.");

        _pipelineId = pipelineId;
        _inputCapacity = runtimeOptions.InputCapacity;
        _outputCapacity = runtimeOptions.OutputCapacity ?? DefaultOutputCapacity;
    }

    /// <summary>Tracks a started run through snapshot delegates.</summary>
    /// <param name="run">Started run handle.</param>
    public void Track(PipelineRun<TOutput> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Track(() => run.State, () => run.Metrics);
    }

    /// <summary>Tracks a run through state and metrics snapshot providers.</summary>
    /// <param name="stateProvider">Function returning the current run state.</param>
    /// <param name="metricsProvider">Function returning an immutable metrics snapshot.</param>
    public void Track(
        Func<PipelineRunState> stateProvider,
        Func<SmartPipeMetricsSnapshot> metricsProvider)
    {
        ArgumentNullException.ThrowIfNull(stateProvider);
        ArgumentNullException.ThrowIfNull(metricsProvider);

        lock (_gate)
        {
            _stateProvider = stateProvider;
            _metricsProvider = metricsProvider;
        }
    }

    /// <inheritdoc />
    public SmartPipeHealthSnapshot CaptureSnapshot()
    {
        Func<PipelineRunState>? stateProvider;
        Func<SmartPipeMetricsSnapshot>? metricsProvider;

        lock (_gate)
        {
            stateProvider = _stateProvider;
            metricsProvider = _metricsProvider;
        }

        return new SmartPipeHealthSnapshot(
            _pipelineId,
            stateProvider?.Invoke() ?? PipelineRunState.NotStarted,
            metricsProvider?.Invoke() ?? SmartPipeMetricsSnapshot.Empty,
            _inputCapacity,
            _outputCapacity,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>Typed SmartPipe health check based on run state and immutable metrics snapshots.</summary>
/// <typeparam name="TInput">Pipeline input payload type.</typeparam>
/// <typeparam name="TOutput">Pipeline output payload type.</typeparam>
internal sealed class SmartPipeHealthCheck<TInput, TOutput> : IHealthCheck
{
    private readonly ISmartPipeRunHealthMonitor<TInput, TOutput> _monitor;
    private readonly SmartPipeHealthCheckOptions _options;

    /// <summary>Creates a typed SmartPipe health check.</summary>
    /// <param name="monitor">Typed pipeline health monitor.</param>
    /// <param name="options">Health-check options.</param>
    public SmartPipeHealthCheck(
        ISmartPipeRunHealthMonitor<TInput, TOutput> monitor,
        IOptions<SmartPipeHealthCheckOptions> options)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("Options value is null.", nameof(options));
        _options.Validate();
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _monitor.CaptureSnapshot();
        var data = CreateData(snapshot);

        if (snapshot.State == PipelineRunState.Faulted)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"SmartPipe pipeline '{snapshot.PipelineId}' is faulted.",
                data: data));
        }

        if (snapshot.State == PipelineRunState.NotStarted && _options.TreatNotStartedAsDegraded)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"SmartPipe pipeline '{snapshot.PipelineId}' has not started.",
                data: data));
        }

        if (QueueIsDegraded(snapshot))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"SmartPipe pipeline '{snapshot.PipelineId}' queue utilization is high.",
                data: data));
        }

        if (IsStale(snapshot))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"SmartPipe pipeline '{snapshot.PipelineId}' has not processed an item recently.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"SmartPipe pipeline '{snapshot.PipelineId}' is healthy.",
            data));
    }

    private bool QueueIsDegraded(SmartPipeHealthSnapshot snapshot)
    {
        var inputUtilization = snapshot.InputCapacity == 0
            ? 0
            : (double)snapshot.Metrics.InputQueueDepth / snapshot.InputCapacity;
        var outputUtilization = snapshot.OutputCapacity == 0
            ? 0
            : (double)snapshot.Metrics.OutputQueueDepth / snapshot.OutputCapacity;

        return inputUtilization >= _options.QueueUtilizationDegradedThreshold
            || outputUtilization >= _options.QueueUtilizationDegradedThreshold;
    }

    private bool IsStale(SmartPipeHealthSnapshot snapshot)
    {
        if (snapshot.State is not (PipelineRunState.Running or PipelineRunState.Draining))
            return false;

        var lastProcessed = snapshot.Metrics.LastProcessedAtUtc;
        return lastProcessed is not null
            && snapshot.CapturedAtUtc - lastProcessed.Value > _options.StaleAfter;
    }

    private static Dictionary<string, object> CreateData(SmartPipeHealthSnapshot snapshot)
    {
        return new Dictionary<string, object>
        {
            ["pipeline_id"] = snapshot.PipelineId,
            ["state"] = snapshot.State.ToString(),
            ["input_queue_depth"] = snapshot.Metrics.InputQueueDepth,
            ["output_queue_depth"] = snapshot.Metrics.OutputQueueDepth,
            ["input_capacity"] = snapshot.InputCapacity,
            ["output_capacity"] = snapshot.OutputCapacity,
            ["items_failed"] = snapshot.Metrics.ItemsFailed,
            ["items_dead_lettered"] = snapshot.Metrics.ItemsDeadLettered,
            ["last_processed_at_utc"] = snapshot.Metrics.LastProcessedAtUtc?.ToString("O") ?? string.Empty,
        };
    }
}

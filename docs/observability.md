# Observability

SmartPipe.Core exposes in-process diagnostics through immutable metric
snapshots and .NET `Meter` instruments. These signals are observational. They
describe the current run state, but they are not synchronization primitives and
do not provide durable delivery, exactly-once processing, or external exporter
configuration by themselves.

## Metrics recorder

`SmartPipeMetricsRecorder` owns mutable metric state. It updates counters and
current-state values through `Interlocked` and `Volatile` operations, so callers
do not mutate public counter fields directly.

The compatibility `SmartPipeMetrics` facade now exposes read-only properties and
mutation methods such as `RecordProcessed`, `RecordFailed`, `RecordRetry`,
`RecordDeadLetter`, `UpdateQueueDepths`, `UpdateQueueSize`, and
`UpdateSmoothing`.

## Snapshots

`SmartPipeMetrics.CaptureSnapshot()` and
`SmartPipeMetricsRecorder.CaptureSnapshot()` return an immutable
`SmartPipeMetricsSnapshot`.

Snapshot fields include:

- `ItemsProcessed`
- `ItemsFailed`
- `ItemsRetried`
- `ItemsDeadLettered`
- `InputQueueDepth`
- `OutputQueueDepth`
- `LastStageLatencyMs`
- `LastProcessedAtUtc`

Compatibility export fields such as `duplicates_filtered`, `retries`,
`avg_latency_ms`, `smooth_latency_ms`, `smooth_throughput`, `queue_size`, and
`pool_hit_rate` remain available through `Export()`, `ExportJson()`, and
`ExportPrometheus()`.

Snapshots are point-in-time samples. Under concurrent updates, individual values
may come from adjacent moments, so snapshots are suitable for reporting and
health checks, not for coordinating pipeline lifecycle.

## Meter instruments

Meter name:

```text
SmartPipe.Core
```

ActivitySource name:

```text
SmartPipe.Core
```

Current instruments:

- `smartpipe.items.processed` counter, unit `items`
- `smartpipe.items.failed` counter, unit `items`
- `smartpipe.items.retried` counter, unit `items`
- `smartpipe.items.deadlettered` counter, unit `items`
- `smartpipe.items.duplicates_filtered` counter, unit `items`
- `smartpipe.stage.duration` histogram, unit `ms`
- `smartpipe.sink.duration` histogram, unit `ms`

Allowed low-cardinality metric dimensions are `pipeline_id`, `stage_id`,
`outcome`, and `error_type`.

Meter measurements must not include high-cardinality dimensions such as
`run_id`, `trace_id`, `exception_message`, `payload_value`, or `raw_payload`.

`run_id` and `trace_id` may appear on Activity tags for debugging and tracing,
but they must not be used as metric dimensions by default.

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
`RecordFiltered`, `RecordItemDropped`, `RecordOutputDropped`,
`RecordObserverEventDropped`, `RecordDeadLetter`, `UpdateQueueDepths`,
`UpdateQueueSize`, and `UpdateSmoothing`.

## Snapshots

`SmartPipeMetrics.CaptureSnapshot()` and
`SmartPipeMetricsRecorder.CaptureSnapshot()` return an immutable
`SmartPipeMetricsSnapshot`.

Snapshot fields include:

- `ItemsProcessed`
- `ItemsFailed`
- `ItemsFiltered`
- `ItemsDropped`
- `OutputItemsDropped`
- `ObserverEventsDropped`
- `ItemsRetried`
- `ItemsDeadLettered`
- `InputQueueDepth`
- `OutputQueueDepth`
- `LastStageLatencyMs`
- `LastActivityAtUtc`
- `LastProcessedAtUtc`

Compatibility export fields such as `duplicates_filtered`, `retries`,
`items_dropped`, `output_items_dropped`, `observer_events_dropped`,
`avg_latency_ms`, `smooth_latency_ms`, `smooth_throughput`, `queue_size`, and
`pool_hit_rate` remain available through `Export()`, `ExportJson()`, and
`ToDiagnosticText()`.

`ToDiagnosticText()` is a diagnostic snapshot for logs and support dumps. It is
not a Prometheus exporter. Prometheus scraping should be wired through .NET
OpenTelemetry metrics exporters at the host boundary.

Snapshots are point-in-time samples. Under concurrent updates, individual values
may come from adjacent moments, so snapshots are suitable for reporting and
health checks, not for coordinating pipeline lifecycle.

Queue depths are sampled from the runtime channel readers when those readers can
report counts. They are observational, point-in-time values; a depth of zero can
mean that the channel is empty or that no countable channel is active for the
current execution path.

## Meter instruments

Stable diagnostic source names are published as constants on
`SmartPipeDiagnostics`:

- `SmartPipeDiagnostics.MeterName` — meter name for all SmartPipe runtime
  metrics.
- `SmartPipeDiagnostics.ActivitySourceName` — activity source name for all
  SmartPipe runtime activities.

Meter name:

```text
SmartPipe.Core
```

ActivitySource name:

```text
SmartPipe.Core
```

`SmartPipeMeter.Name` remains as an alias of `SmartPipeDiagnostics.MeterName`.
External tooling that needs the exact source names (for example an
OpenTelemetry `AddMeter`/`AddSource` registration) must use these constants
instead of hard-coded strings.

Current instruments:

- `smartpipe.items.processed` counter, unit `items`
- `smartpipe.items.failed` counter, unit `items`
- `smartpipe.items.filtered` counter, unit `items`
- `smartpipe.items.dropped` counter, unit `items`
- `smartpipe.output.items.dropped` counter, unit `items`
- `smartpipe.observer.events.dropped` counter, unit `events`
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

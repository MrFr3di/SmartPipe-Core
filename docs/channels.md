# Channels

Install `SmartPipe.Extensions.Channels` for `ChannelMerge`; the broad
`SmartPipe.Extensions` package forwards the same public type for 2.x compatibility.

`Merge` accepts either the shipped pair of readers or an unconfigured
`IReadOnlyList` of readers. Use `MergeMany` when configuring bounded output options
or cancellation. Each input preserves its own order; arrival order between inputs
is intentionally not defined. Bounded options apply backpressure to every pump.
Zero readers return an already-completing reader; for nonempty input, reader/null
validation precedes bounded output-option validation and snapshotting.
Caller cancellation stops pending reads and writes, completes the output with
cancellation, and the lowest-index observed input failure wins when inputs fail
concurrently. If cancellation callbacks also fail, the primary input failure is
retained first and callback failures follow it in an `AggregateException`. Abandoning
a bounded output reader without cancelling remains caller responsibility.

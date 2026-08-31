# SmartPipe.Extensions.Transforms

Composable transforms for SmartPipe.Core without the broad extensions facade.

- `CompositeTransform<T>` owns and sequences child transforms with deterministic rollback and disposal.
- `ConditionalTransform<T>` applies an owned child only when its predicate matches.
- `CompressionTransform` compresses byte arrays with Brotli or GZip.
- `FilterTransform<T>` supports synchronous, legacy task-based, and token-aware predicates.
- `RuleValidationTransform<T>` freezes reflection-free application rules before execution.

Legacy task predicates cannot cancel predicate work because their delegate has no cancellation token. Use the token-aware `Func<T, CancellationToken, ValueTask<bool>>` constructor when cancellation must reach the predicate.

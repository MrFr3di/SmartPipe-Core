# Transforms

`SmartPipe.Extensions.Transforms` contains `CompositeTransform<T>`,
`ConditionalTransform<T>`, `CompressionTransform`, `FilterTransform<T>`, and the
reflection-free `RuleValidationTransform<T>`.

`CompositeTransform<T>` must initialize successfully before transformation. It
initializes once, rolls partial initialization back in reverse order, short-circuits
terminal results, and disposes acquired children once in reverse order. Filter
predicates receive the caller token; `&`, `|`, and `!` short-circuit. Rules freeze
on initialization or first execution. Compression supports Brotli and GZip and
observes cancellation before work.

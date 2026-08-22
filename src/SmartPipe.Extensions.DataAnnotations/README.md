# SmartPipe.Extensions.DataAnnotations

DataAnnotations validation transforms for SmartPipe.Core.

`ValidationTransform<T>` keeps the existing public namespace
`SmartPipe.Extensions.Transforms` and combines object/property DataAnnotations
validation with fluent `Require` rules. Validation follows
`Validator.TryValidateObject` and is deliberately non-recursive: nested object
properties are not walked.

Rules are mutable during configuration and freeze on initialization or the
first execution. Adding a rule after that point throws
`InvalidOperationException`. `ToFilter()` forwards the pipeline cancellation
token to validation and filters invalid items.

The reflection-based `TransformAsync` and `ToFilter` APIs carry
`RequiresUnreferencedCode`; use them only when the required DataAnnotations
metadata is preserved by the application.

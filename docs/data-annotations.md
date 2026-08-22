# DataAnnotations validation

Install `SmartPipe.Extensions.DataAnnotations` for the compatibility
`ValidationTransform<T>` and `ToFilter` APIs. Validation follows BCL
`Validator.TryValidateObject` behavior and does not recursively walk nested object
graphs. Custom rules freeze on initialization or first execution.

The reflection path is marked `RequiresUnreferencedCode`; direct invocation emits
the trimming warning and is not a NativeAOT-safe contract. A package-reference-only
trimmed application remains clean when it does not invoke that path. Use
`RuleValidationTransform<T>` from `SmartPipe.Extensions.Transforms` for a
reflection-free trimmed or NativeAOT validation path.

Release validation keeps the two boundaries separate: an untrimmed runtime
consumer invokes `ValidationTransform<T>` and observes an invalid-model failure,
while the trimmed consumer remains clean and separately proves the exact IL2026
diagnostic when reflection validation is enabled.

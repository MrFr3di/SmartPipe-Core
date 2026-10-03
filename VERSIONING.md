# Versioning and compatibility

SmartPipe uses synchronized package versions for the 2.2 release train. The
repository-level `Version` property is the source for package versioning, while
`eng/package-graph.json` is the source of truth for package lifecycle,
dependency policy, publish order, and package-scoped AOT contracts.

## Compatibility is multi-dimensional

Do not infer compatibility from the package number alone. SmartPipe reviews and
validates compatibility across separate dimensions:

| Dimension | Meaning | Primary evidence |
| --- | --- | --- |
| Source | Existing source still compiles against the new package set | source consumers and build tests |
| Binary | Previously compiled assemblies still bind and run | unchanged old-binary consumers, forwarder/assembly identity checks |
| API | Public signatures remain compatible or a break is explicitly accepted | PublicAPI baselines and package validation |
| Behavioral | Existing API keeps its documented semantics | correctness, lifecycle, integration, and regression tests |
| Package graph | Package IDs, dependency ownership, and lifecycle remain valid | `eng/package-graph.json` and repository checks |
| AOT / trimming | A package keeps only the claim it actually validates | package-specific trim/NativeAOT consumers |
| Release provenance | Published artifacts originate from the accepted source and immutable producer artifact | release validation and artifact provenance checks |

A change can be source-compatible and still be binary-incompatible. Namespace
preservation is not proof of binary compatibility.

## 2.1.2 → 2.2.0

The 2.2 release intentionally contains documented breaking changes from the
2.1.2 compatibility baseline. Therefore, SmartPipe does not treat a
minor-version number by itself as a promise of binary/source compatibility.

For the 2.2 transition:

- 23 relevant facade identities are preserved through type forwarding;
- 13 remain physically in the compatibility facade;
- 6 are intentionally removed and require migration/recompilation.

See the
[compatibility matrix](docs/reference/compatibility/2.1.2-to-2.2.0.md)
and [migration guide](docs/migration/2.2.0-integration-packages.md).

## Package version policy

All publishable SmartPipe packages in one release candidate use the same
release version. A package may have a different lifecycle or dependency surface,
but it does not independently choose a release version inside the 2.2 train.

Do not introduce:

- floating SmartPipe package versions;
- per-project version overrides that bypass repository versioning;
- package dependency versions that disagree with the release artifact manifest;
- a release tag whose source commit is not the accepted release history.

## Compatibility facade

`SmartPipe.Extensions` is a compatibility facade/bundle, not the preferred
dependency for new applications.

A moved public type may be kept through a real metadata type forwarder when the
implementation assembly owns the type and binary binding can be proven. A
wrapper is used only when the compatibility contract explicitly requires one.
An intentionally removed type is not restored simply to silence API
compatibility tooling.

Narrow integration leaves must not depend back on the facade.

## Baselines

Compatibility baselines are immutable evidence. A failing compatibility check is
resolved by fixing the implementation or by accepting/documenting a real
breaking change. Do not regenerate a baseline to make the candidate pass.

The 2.1.2 baseline under `eng/baselines/2.1.2/` is comparison material for the
2.2 release and does not by itself define support status.

## Release documentation

For every release-facing compatibility change, keep these layers consistent:

1. machine-readable package/ownership/API contracts;
2. executable source/binary/trim/AOT consumers;
3. migration guidance and compatibility matrix;
4. release notes;
5. exact-head hosted evidence.

Support lifecycle is documented separately in [SUPPORT.md](SUPPORT.md).

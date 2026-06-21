# Contributing To SmartPipe

## Getting Started

1. Fork and clone the repository.
2. Create a focused branch.
3. Restore, build, and test with the repository tooling.
4. Update docs when public behavior or public API changes.
5. Open a pull request with the verification commands you ran.

## Code Style

- Target the project framework and language version already configured in the
  repository.
- Keep nullable annotations accurate.
- Add XML documentation for public and protected package APIs.
- Prefer async all the way through library code.
- Use `ConfigureAwait(false)` in library internals where appropriate.
- Use `ILogger<T>` for logging; do not add console output to library code.
- Keep Core persistence-agnostic. Database, HTTP, file, hosting, and other
  integration concerns belong in Extensions or consuming applications.

## Documentation Rules

- Keep README concise and link to topic docs.
- Put option tables in `docs/configuration.md`.
- Put retry, timeout, circuit breaker, drain/cancel, dead-letter, and observer
  behavior in `docs/resilience.md`.
- Put public API summaries in `docs/api-reference.md` and verify them against
  `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`.
- Keep user-facing docs focused on supported behavior and practical usage.
- Avoid stale generated catalogs and per-file summaries.

## Claims Policy

Do not add unsupported claims such as:

- production-ready;
- zero dependencies;
- zero allocations or zero-byte hot paths;
- lock-free behavior;
- exact current coverage or exact current test count;
- broad AOT-ready compatibility;
- exactly-once delivery;
- replay-safe legacy dead-letter records.

Claims need reproducible evidence from CI logs, package validation, API
compatibility reports, benchmark artifacts, consumer smoke tests, trim/AOT
publish logs, or targeted tests.

## Release Checklist Summary

Before release-facing changes are accepted, verify the relevant subset:

- restore;
- Release build for Core and Extensions;
- tests;
- public API baseline review;
- package validation and pack;
- consumer smoke;
- trim and NativeAOT smoke for scoped compatibility claims;
- dependency/security scan;
- `git diff --check`.

## Bugs And Feature Requests

Open an issue with a clear description, reproduction steps, expected behavior,
actual behavior, environment, and package version. For feature requests, include
the use case and proposed API shape when possible.

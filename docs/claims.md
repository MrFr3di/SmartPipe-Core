# Claims Policy

SmartPipe.Core release claims must be backed by reproducible evidence.

## Verdicts

Each claim should be classified as:

- `Proven`
- `Partially Proven`
- `Not Proven`
- `False`
- `Not Applicable`

## Claims Requiring Evidence

The following claims must not appear in release-facing docs or package metadata
without evidence:

- production-ready;
- 0 allocations;
- 0 dependencies;
- lock-free;
- exact coverage;
- exact test count;
- AOT-ready;
- dead-letter replay;
- adaptive parallelism;
- O(1) behavior for complex operations.

## Evidence Types

Acceptable evidence includes:

- CI logs;
- package validation results;
- API compatibility reports;
- benchmark artifacts;
- consumer smoke-test logs;
- trim/AOT publish logs;
- targeted tests that assert the behavior.

## Current 1.1.0 Claim Posture

`SmartPipe.Core` is described as a streaming pipeline library built on
`System.Threading.Channels`. It currently depends on
`Microsoft.Extensions.Logging.Abstractions`, so it must not be documented as
having zero dependencies.

# Support

SmartPipe support is release-line based. This file defines how support status is
communicated; it does not infer a support commitment merely because an older
package or compatibility baseline exists.

## Current release state

- `2.2.0` is the active release train in this repository until it is published.
  A release candidate is not treated as a supported stable release merely
  because its version is present in source.
- `2.1.2` is the immutable compatibility baseline used to validate the 2.2
  migration. Baseline status is evidence for compatibility testing, not by
  itself a maintenance or support promise.
- Earlier 1.x support labels previously present in `SECURITY.md` are not
  repeated here because the current repository does not contain an accepted
  support-window policy that substantiates them.

When a release line receives an explicit support commitment, update this file
and the release notes in the same reviewed change. Do not derive support status
from NuGet availability, a Git tag, or an API compatibility baseline alone.

## What support covers

For a supported line, SmartPipe can address defects in the contracts owned by
the affected package, including runtime correctness, package metadata,
compatibility, documented AOT/trimming paths, and integration behavior that is
implemented by SmartPipe.

SmartPipe does not provide support commitments for:

- application-specific idempotency or exactly-once delivery;
- durable recovery or distributed coordination that Core does not implement;
- external database, HTTP, exporter, DI-container, or cloud-provider behavior
  outside SmartPipe's documented ownership boundary;
- unsupported runtime/framework versions;
- private forks or modified binaries whose behavior cannot be reproduced from
  the published source/package.

## Getting help

For ordinary defects, open a focused GitHub issue with:

- SmartPipe package ID and version;
- target framework and operating system;
- minimal reproduction;
- expected and actual behavior;
- relevant logs or exception details with secrets removed.

For security-sensitive reports, follow [SECURITY.md](SECURITY.md) instead of
publishing exploit details in a normal issue.

For version compatibility rules, see [VERSIONING.md](VERSIONING.md).

# ADR-0002: SmartPipe 2.2 legacy compatibility quarantine

- Status: Accepted for implementation
- Date: 2026-07-30
- Decision owners: SmartPipe maintainers
- Target release: 2.2.0
- Supersedes: ADR-0001 compatibility ownership only

## Context

The published `SmartPipe.Extensions` DI factory exposes synchronous immediate-return
`Start`, while its public constructor graph also includes legacy Hosting and Health
types. Moving only part of this graph would either break 2.1.2 binary identity,
make a leaf depend on the facade, or require sync-over-async. All three conflict
with the 2.2.0 release contract.

## Decision

The coupled legacy DI, Hosting, and Health identity cluster remains physically
implemented in `SmartPipe.Extensions` for 2.2.0 under `obsolete-wrapper` ownership.
The synchronous factory `Start` member is obsolete but preserves its shipped
immediate-return behavior. No new capability, overload, retry, fallback, or async
bridge may be added to this compatibility surface.

This quarantine is not a second Core runtime. Legacy execution continues through
the existing Core legacy adapter and the same compiler, activator, start operation,
and executor. The separate DI, Hosting, and HealthChecks packages expose only new
canonical contracts and must not reference or resolve facade-owned legacy types.

Source consumers and binaries compiled against 2.1.2 must exercise the retained
signatures and constructor graph against the 2.2.0 package set. The cluster is a
removal candidate for the next major release.

Because the retained Hosting and Health public signatures expose `IOptions<>`,
`SmartPipe.Extensions` carries a direct `Microsoft.Extensions.Options` dependency.
Its legacy Hosting identities likewise require
`Microsoft.Extensions.Hosting.Abstractions`, and its legacy health identities
require `Microsoft.Extensions.Diagnostics.HealthChecks`. These three facade
dependencies are evidence-backed, non-expiring 2.2 release allowances while the
public identities remain facade-owned. They do not authorize the canonical
Hosting leaf to add a direct Options dependency.

## Consequences

The facade retains frozen legacy scope, hosted-service, and health-monitor ingress
for one minor release. New development occurs only in leaf packages. Package
ownership, assembly-reference checks, source consumers, binary consumers, and
PublicAPI/package validation enforce the boundary.

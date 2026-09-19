# Project Progress

## Goal

Deliver the 2.2.0 Dapper package activation: a narrow independently installable
`SmartPipe.Extensions.Dapper` owning explicit-SQL query/command/batch definitions,
with `DapperSelector<T>` and `DbSink<T>` moved physically into the leaf and kept
reachable from `SmartPipe.Extensions` through type forwarding, on the accepted
Checkpoint E base.

## Overall Progress

The leaf is implemented and green: explicit-SQL components with one fresh per-run
connection released exactly once, an explicit `PerBatch`/`None` transaction mode,
bounded preformed batches, payload-free logging, and 53 focused tests. Both legacy
identities moved into the leaf under their preserved namespaces and are forwarded
from the facade. The package graph node is active, both ownership rows point at the
leaf, five `dapper-*` consumer scenarios exist, and the canonical documentation is
updated. The servicing-selected `Dapper 2.1.86` was adopted after a clean-directory
probe proved the whole chain green.

## Current Position

- Base: accepted Checkpoint E merge `c3c655f` (tree `1bb294f8`), worktree
  `.work/wt-sp220-10-dapper`, branch `upd-sp220-10-dapper`, frozen candidate
  `2dedc94` (tree `19bb6465`).
- Every repository gate is green except the checks owned by the still-planned
  EntityFrameworkCore, Http, Http.Json, Mapster, Polly and Testing epics: release-mode
  graph reports 23 violations with **zero Dapper-owned** entries.
- Five `dapper-*` consumer scenarios pass with the predicted closures 2/3/11/3/2.
- No remote mutation has been performed. Push, pull request, merge and publication
  remain separate authorizations; Checkpoint E stays open for SP220-11/12.

## Verification

- Focused: leaf `53/53`, `DapperSelectorTests` `39/39`, `DbSinkTests` `10/10`,
  `PackageOwnershipTests` `5/5`.
- Repository gates: pack `packages=13`, graph current `19/13/6`, metadata `13`,
  ownership `157`, release version `2.2.0`, `dotnet format` clean, CI-equivalent
  solution build clean, lock files `SP220_LOCK_FILES_OK`, central packages
  `packages=47`.
- Consumers: all five Dapper scenarios plus the whole current matrix.

## Next Milestone

Independent review of the frozen candidate, then the authorization-gated
contribution to `sp220/checkpoint-e`. Release promotion, publication and the
remaining leaf epics are out of this deployment's scope.

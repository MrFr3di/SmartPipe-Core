# Architecture decision records

ADRs capture accepted architecture decisions and their rationale. Current
runtime/package behavior is still described by the product/reference
documentation and machine-readable repository contracts.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-smartpipe-2.2-package-boundaries.md) | Accepted | Split the 2.2 integration surface into narrow packages around one Core runtime |
| [0002](0002-smartpipe-2.2-legacy-compatibility-quarantine.md) | Accepted | Quarantine legacy compatibility in the broad facade rather than duplicating implementations |
| [0003](0003-single-hosted-orchestrator.md) | Accepted | Use one hosted orchestrator for ordered pipeline lifecycle management |
| [0004](0004-smartpipe-2.2-breaking-migration.md) | Accepted | Allow documented HTTP/Polly breaking migration where legacy behavior should not be preserved |

When an ADR is superseded, update this index and the ADR itself; do not silently
rewrite the historical decision.

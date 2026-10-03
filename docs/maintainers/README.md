# Maintainer documentation

This area contains repository-maintenance, release-train, governance, planning,
and evidence material. It is intentionally separate from consumer-facing
SmartPipe documentation.

For product usage, start at [../index.md](../index.md).

## Stable maintainer entry points

- [2.2 branch and review policy](governance/2.2.0-branch-and-review-policy.md)
- [2.2 release-train index](2.2.0/README.md)
- [Architecture decisions](../adr/README.md)
- [Package authoring](../contributing/package-authoring.md)
- [Contribution policy](../../CONTRIBUTING.md)
- [Versioning and compatibility](../../VERSIONING.md)

## Evidence policy

Maintainer evidence records a specific candidate, release train, experiment, or
decision. It does not override current runtime/reference documentation.

When product behavior changes, update the normative/current documentation and
machine-readable contracts in the same change. Evidence may link to those
contracts, but should not become a second source of truth.

Exact-head CI, immutable package artifacts, compatibility baselines, package
graph/ownership manifests, and executable consumers remain the authoritative
acceptance evidence for claims they cover.

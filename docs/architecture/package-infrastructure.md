# Package infrastructure

SmartPipe package infrastructure is a set of machine-readable contracts and
one repository-check executable. It keeps package versions, project metadata,
dependency direction, type ownership, packed artifacts, and consumer behavior
in agreement without changing runtime code.

## Contracts and ownership

`Directory.Packages.props` owns exact external versions. `eng/package-graph.json`
owns the 19-package topology, lifecycle, package-specific policies, and
temporary allowances. `eng/package-ownership.json` maps public types to their
owning package and records compatibility forwarding. `eng/consumer-scenarios.json`
describes executable consumer claims; its JSON Schema files are checked for
parity with the manifests.

The graph is validated in both current and release modes. Topological sorting
is performed for both dependency policies, so a cycle that exists only in the
current policy cannot be hidden by release metadata. Release mode also requires
every package ID listed in `requiredAtRelease` to have a consumer scenario.

## Lifecycle

```text
planned -> active -> published
```

The transition is monotonic. A planned package is scaffolded but has no
published baseline. Activating it requires a real project, graph entry, package
metadata, ownership decision, lock file, and direct consumer coverage.
Publishing additionally requires release-version, artifact, API, ownership,
and trim/AOT evidence appropriate to the package. Removing a published ID is a
separate compatibility and release decision; package infrastructure does not
reverse the lifecycle automatically.

## Build and consumer flow

Common build properties are imported from `Directory.Build.props` and package
projects opt into `eng/SmartPipe.Package.props` and
`eng/SmartPipe.Package.targets`. The targets fail on a package-version mismatch
even when `CI=true`, so a green CI build cannot mask an incorrectly versioned
artifact. Pack output is written to `artifacts/packages` and is then consumed by
isolated workspaces.

The consumer harness writes a temporary NuGet configuration. The local source
maps only `SmartPipe.*`; external package IDs are enumerated from CPM and mapped
explicitly to nuget.org. Harness package replacement uses the same bounded ZIP
preflight as package validation, including compressed-size and expansion-ratio
limits, before an assembly reaches a scenario output directory.

## Gates

The executable in `eng/SmartPipe.RepositoryChecks` provides the following
gates: central versions, lock-file reconciliation, package projects, graph,
metadata, ownership, release version, package packing, and consumer scenarios.
`NuGet.Config` uses HTTPS V3 sources, an explicit audit source, and locked
restore. Tracked lock files are UTF-8 without BOM, use LF, and contain the
required lock content hashes for external packages.

Current mode is the development gate and must pass for every change. Release
mode is stricter: it validates the complete target topology and required future
consumer coverage, while any remaining planned work must be represented by an
explicit graph allowance or `requiredAtRelease` entry. The final release
checkpoint reruns the same commands against the exact merged commit.

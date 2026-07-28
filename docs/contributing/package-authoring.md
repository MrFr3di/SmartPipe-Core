# Package authoring

SmartPipe package projects are driven by the repository's central package
management (CPM), package graph, ownership manifest, and consumer scenarios.
Keep those four contracts synchronized in the same change.

## Add a dependency

Add the exact version once to `Directory.Packages.props` using a
`PackageVersion` item. Project files use only `PackageReference
Include="..."`; do not add a local `Version`, `VersionOverride`, range, or
floating version. Transitive pinning is disabled, so a direct reference must be
declared when the package is part of a package's supported contract.

Run `verify-central-packages --mode current` and restore with locked mode after
changing the manifest. Release mode treats unused versions and inventory drift
as errors.

## Add or activate a package

Use the scaffolder to produce a dry-run before writing files:

```powershell
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-restore -- scaffold-package --repository-root . --package-id SmartPipe.Extensions.Example --dry-run
```

An entry in `eng/package-graph.json` is required before a package is published.
The lifecycle is monotonic: `planned -> active -> published`. Planned entries
have a scaffold kind and no baseline; active entries have a baseline and a
project; published entries additionally require the release evidence defined by
the release workflow. There is no automatic reverse transition.

Every package project must be represented exactly once in the graph and marked
with `SmartPipePackage=true`. Keep package-specific description, tags, README,
icon, repository metadata, XML documentation, symbols, Source Link, and API
baselines in the package project or its template. Common properties come from
`Directory.Build.props` and `eng/SmartPipe.Package.props`.
README source paths use MSBuild path semantics on every supported operating
system and must resolve to a file inside the repository.

## Dependency and ownership rules

Declare SmartPipe edges in both current and release dependency policies. The
graph validator rejects unknown IDs, self edges, forbidden layer direction,
cycles, and expired temporary allowances. A temporary allowance must include an
owner epic, reason, evidence, and an expiry before the release version; it is
not a compatibility shortcut.

The ownership manifest is the source of truth for moved public types. Add an
explicit forwarding record when a type remains reachable from a compatibility
facade. Do not add runtime forwarding or aliases to make a graph violation
pass.

## Consumer scenarios

Add a scenario to `eng/consumer-scenarios.json` and keep its shape synchronized
with `eng/consumer-scenarios.schema.json`. Use the scenario kind that matches
the claim: direct compile/run, metadata, binary compatibility, trim, or AOT.
The package IDs in a direct scenario must be explicit. Register future release
coverage in the top-level `requiredAtRelease` list; release validation fails if
one of those IDs has no scenario. Scenario workspaces restore from an isolated
NuGet configuration that maps `SmartPipe.*` to the local package directory and
maps each external package ID explicitly to nuget.org.

## Verification

From a clean Release build, run the repository checks in this order:

```powershell
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test tests\SmartPipe.RepositoryChecks.Tests\SmartPipe.RepositoryChecks.Tests.csproj -c Release --no-build -- --filter-trait Category=PackageInfrastructure
dotnet test tests\SmartPipe.RepositoryChecks.Tests\SmartPipe.RepositoryChecks.Tests.csproj -c Release --no-build -- --filter-trait Category=Mutation
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-lock-files --repository-root .
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-central-packages --repository-root . --mode current
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-package-projects --repository-root .
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-package-graph --repository-root . --mode current --packages artifacts\packages
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-package-metadata --repository-root . --mode current --packages artifacts\packages
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-package-ownership --mode current --baseline eng\baselines\2.1.2 --packages artifacts\packages
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-release-version --mode current --tag v2.2.0 --packages artifacts\packages
dotnet run --project eng\SmartPipe.RepositoryChecks\SmartPipe.RepositoryChecks.csproj -c Release --no-build -- run-consumers --repository-root . --set current --package-directory artifacts\packages --package-version 2.2.0
```

Run the graph and all validators again with `--mode release` before a release.
Release mode must have no unexpected violations: planned package work,
registered allowances, and explicitly listed future scenarios are the only
accepted unfinished items. Do not use `--no-restore` until the locked restore
has passed for the current sources.

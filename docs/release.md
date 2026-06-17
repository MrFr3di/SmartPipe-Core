# Release Validation

Release validation packages the current code and runs consumer checks against
the package artifacts rather than project references.

## Versioning Decision

The typed-only runtime release is `2.0.0`. The removed legacy public runtime
surface was available to `1.0.x` consumers, so the removal is treated as a
SemVer major breaking change rather than a `1.1.0` compatible update.

## Package Smoke

Required package smoke shape:

```powershell
dotnet pack src\SmartPipe.Core\SmartPipe.Core.csproj -c Release --no-build -o artifacts\packages
dotnet pack src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release --no-build -o artifacts\packages
dotnet new console -n SmartPipe.ConsumerSmoke -o artifacts\consumer-smoke --force
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="smartpipe-local" value="../packages" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="smartpipe-local">
      <package pattern="SmartPipe.*" />
    </packageSource>
    <packageSource key="nuget">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
'@ | Set-Content artifacts\consumer-smoke\NuGet.Config
Push-Location artifacts\consumer-smoke
dotnet add SmartPipe.ConsumerSmoke.csproj package SmartPipe.Core --version 2.0.0
dotnet add SmartPipe.ConsumerSmoke.csproj package SmartPipe.Extensions --version 2.0.0
Pop-Location
dotnet run --project artifacts\consumer-smoke\SmartPipe.ConsumerSmoke.csproj -c Release
```

The consumer smoke must validate:

- typed source -> transform -> sink;
- output consumer reads `PipelineResult<T>`;
- `DrainAsync`;
- typed DI factory creation;
- typed Extensions components.

CI runs this on `main`, `upd`, pull requests to `main`, and manual
`workflow_dispatch`.

README examples are intentionally minimal. CI consumer smoke is the executable
check for the public quick-start scenarios.

CI also runs a lychee docs link check against `README.md` and `docs/**/*.md`.

## Performance Gate

Use the existing BenchmarkDotNet project. The release benchmark set includes
10k sequential and parallel typed pipelines, output policies, StageExecutor
success and retry paths, metrics recording, and inline/buffered observers.

No throughput regression above 15% versus the previous local baseline should be
accepted without explanation. No benchmark-only code should leak into
production runtime.

## AOT And Trimming

SmartPipe.Core is AOT-conscious and analyzer-gated. Some SmartPipe.Extensions
integrations may require source-generated serializers or may not be
AOT-friendly.

Trimmed and NativeAOT consumer smoke should pass in CI when the required SDK and
workload support are available. Do not suppress trim/AOT warnings blindly.

## Extensions Package Surface

SmartPipe.Extensions is currently a broad integration package. This release
keeps it monolithic to avoid expanding the typed-only hardening scope. Future
releases may split integrations into focused packages such as Hosting,
HealthChecks, Json, Csv, EFCore, Dapper, Mapster, and Resilience.

HTTP integrations should use `HttpClientFactorySelector<T>` /
`HttpClientFactorySink<T>` for DI-owned clients. Avoid stacking SmartPipe stage
retry and HTTP/Polly retry for the same operation unless the total retry budget
is explicit.

# Contributing

Before opening a change, run the validation tier that matches the affected
surface.

For runtime or public API changes:

```powershell
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build
dotnet pack src\SmartPipe.Core\SmartPipe.Core.csproj -c Release --no-build -o artifacts\packages
dotnet pack src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release --no-build -o artifacts\packages
```

## Test Runner And Coverage

Test projects use xUnit v3 on Microsoft Testing Platform. The repository
`global.json` selects the MTP runner, so pass projects with `--project`:

```powershell
dotnet test --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build
```

Executable runner mode is supported for both test projects:

```powershell
dotnet run --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet run --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build
```

Coverage uses the MTP code coverage extension, not VSTest data collectors:

```powershell
dotnet run --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
```

Do not use `--collect:"XPlat Code Coverage"` with the MTP runner.

Consumer smoke must install `SmartPipe.Core` and `SmartPipe.Extensions` from the
local `artifacts/packages` directory, with nuget.org present only as the
external dependency source. Smoke code must use the typed-only API:
`IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`,
`IPipelineSink<T>`, `ProcessingEnvelope<T>`, `StageResult<T>`,
`PipelineRun<T>`, and typed DI factories.

Do not reintroduce removed pre-typed APIs in tests, docs, or smoke programs.

## Concurrency And Lifecycle Tests

Concurrency tests must be deterministic. Avoid sleep-based synchronization.
Use explicit coordination primitives and treat timeouts as guards only.

Backpressure tests should use intentionally small bounded capacities and assert
final runtime state, output behavior, and disposal behavior where relevant.

## Fault Scenario Tests

Fault tests should assert the final `PipelineRunState`, output behavior,
observer or failure events where applicable, metrics where applicable, and
disposal behavior where applicable.

Do not create a separate fault-injection matrix document. Keep compact coverage
notes in the existing runtime, resilience, and contributing docs.

## Performance Gate

Use the existing BenchmarkDotNet project for release performance checks:

```powershell
dotnet run -c Release --project benchmarks\SmartPipe.Benchmarks\SmartPipe.Benchmarks.csproj -- --filter "*RuntimePipelineBenchmarks*" --noOverwrite
```

The release gate compares local results against the previous local baseline.
Document any throughput regression above 15%, obvious allocation spike, or
unbounded-memory symptom in progress notes.

README examples are intentionally minimal. CI consumer smoke is the executable
check for the public quick-start scenarios.

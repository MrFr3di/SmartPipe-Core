# Contributing

Before opening a change, run the validation tier that matches the affected
surface.

For runtime or public API changes:

```powershell
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test SmartPipe.Core.slnx -c Release --no-build
dotnet pack src\SmartPipe.Core\SmartPipe.Core.csproj -c Release --no-build -o artifacts\packages
dotnet pack src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release --no-build -o artifacts\packages
```

Consumer smoke must install `SmartPipe.Core` and `SmartPipe.Extensions` from the
local `artifacts/packages` directory, with nuget.org present only as the
external dependency source. Smoke code must use the typed-only API:
`IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`,
`IPipelineSink<T>`, `ProcessingEnvelope<T>`, `StageResult<T>`,
`PipelineRun<T>`, and typed DI factories.

Do not reintroduce removed pre-typed APIs in tests, docs, or smoke programs.

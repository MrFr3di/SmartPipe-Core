# Release Validation

Release validation packages the current code and runs consumer checks against
the package artifacts rather than project references.

Required package smoke shape:

```powershell
dotnet pack src\SmartPipe.Core\SmartPipe.Core.csproj -c Release -o artifacts\packages
dotnet pack src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release -o artifacts\packages
dotnet new console -n SmartPipe.ConsumerSmoke -o artifacts\consumer-smoke --force
dotnet add artifacts\consumer-smoke\SmartPipe.ConsumerSmoke.csproj package SmartPipe.Core --version 1.1.0 --source "$PWD/artifacts/packages" --source "https://api.nuget.org/v3/index.json"
dotnet add artifacts\consumer-smoke\SmartPipe.ConsumerSmoke.csproj package SmartPipe.Extensions --version 1.1.0 --source "$PWD/artifacts/packages" --source "https://api.nuget.org/v3/index.json"
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

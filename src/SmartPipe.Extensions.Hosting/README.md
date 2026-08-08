# SmartPipe.Extensions.Hosting

Deterministic .NET Generic Host integration for canonical SmartPipe pipeline
registrations.

```bash
dotnet add package SmartPipe.Extensions.Hosting --version 2.2.0
```

```csharp
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.Hosting;

var smartPipe = services.AddSmartPipe();
smartPipe.AddPipeline(definition)
    .RunAsHostedService(options => options.Order = 0);
```

All hosted pipelines share one orchestrator. They start sequentially, stop in
reverse order, roll back partial startup, and abort before disposal when graceful
drain cannot finish. `PipelineKey` is the unique hosted identity.

The package is trim- and NativeAOT-compatible when the registered pipeline
definitions and their components are compatible. Hosting uses explicit keyed DI
metadata and does not perform reflection scanning or build an intermediate
service provider. Reference this leaf directly for new applications; the broad
`SmartPipe.Extensions` package is the legacy compatibility facade.

The repository Hosting guide documents lifecycle, failure-policy, and migration
details.

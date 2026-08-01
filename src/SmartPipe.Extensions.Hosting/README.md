# SmartPipe.Extensions.Hosting

Deterministic .NET Generic Host integration for canonical SmartPipe pipeline
registrations.

```csharp
var smartPipe = services.AddSmartPipe();
smartPipe.AddPipeline(definition)
    .RunAsHostedService(options => options.Order = 0);
```

All hosted pipelines share one orchestrator. They start sequentially, stop in
reverse order, roll back partial startup, and abort before disposal when graceful
drain cannot finish. `PipelineKey` is the unique hosted identity.

The repository Hosting guide documents lifecycle, failure-policy, and migration
details.

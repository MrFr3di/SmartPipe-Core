# SmartPipe.Extensions.DependencyInjection

Canonical keyed dependency-injection integration for SmartPipe typed pipeline
definitions.

```csharp
ISmartPipeRegistrationBuilder<Order, OrderDto> registration = services
    .AddSmartPipe()
    .AddPipeline(definition);

ISmartPipeRunFactory<Order, OrderDto> factory = provider
    .GetRequiredKeyedService<ISmartPipeRunFactory<Order, OrderDto>>("orders");

PipelineRun<OrderDto> run = await factory.StartAsync(cancellationToken);
```

The package provides atomic global-key registration, immutable registration
metadata, exact typed factory lookup, one async DI scope per run, and active-run
snapshots. It depends only on `SmartPipe.Core` and
`Microsoft.Extensions.DependencyInjection.Abstractions`; it does not depend on
Hosting, HealthChecks, Options, or the broad `SmartPipe.Extensions` facade.

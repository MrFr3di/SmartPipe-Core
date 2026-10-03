# SmartPipe.Testing

Framework-neutral helpers for tests that use `SmartPipe.Core`. The package depends only on Core and is intended for test projects.

## Installation

```bash
dotnet package add SmartPipe.Testing
```

`TestActivation.Create("orders")` creates a fresh context with the exact key and a new run ID. `SourceReader.ReadEnvelopesAsync(source, maxItems, token)` collects envelopes in order, preserving their identity and metadata. It throws when the source yields an item beyond the explicit maximum.

Initialize and dispose the source in the test. The reader disposes only the enumerator; pass a cancellation token if the source may wait for another item at the limit.

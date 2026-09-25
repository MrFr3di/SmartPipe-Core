# SmartPipe.Extensions.Http

Streaming HTTP sources and sinks for `SmartPipe.Core`. This package owns the
transport only: request acquisition, response status handling, response and
stream lifetime, body timeouts, and cancellation. JSON codecs live in
`SmartPipe.Extensions.Http.Json`.

## Package graph

`SmartPipe.Extensions.Http` depends on `SmartPipe.Core`,
`Microsoft.Extensions.Http`, and `Microsoft.Extensions.Logging.Abstractions`. It
never references `SmartPipe.Extensions.Json`, Polly, Hosting, HealthChecks, or
the `SmartPipe.Extensions` bundle.

## What this package owns

- `HttpPipelineComponents.Source<T>` creates a runtime-owned
  `IPipelineSource<T>` from a request factory and an `HttpResponseReader<T>`.
- `HttpPipelineComponents.Sink<T>` creates a runtime-owned `IPipelineSink<T>`
  from an `HttpRequestFactory<T>` and an optional idempotency-key selector.
- `HttpPipelineDefinitionBuilderExtensions.FromHttp` starts a definition with a
  `PipelineKey`. `ToHttp` attaches a sink through Core's `To` to an initial or
  typed builder. It takes no stage key, and the pipeline input type is kept.
- `HttpSourceOptions`, `HttpSinkOptions`, and `HttpResponsePolicy` configure
  operation names, the optional body timeout, the idempotency header name, and
  non-success response handling.
- `HttpResponseStatusException` reports a non-success status.

Every overload takes either a borrowed `HttpClient` or an application-owned
`IHttpClientFactory` and client name. Building a definition does no I/O: no
client is created and no request factory is invoked until a run starts reading.

## Ownership and lifetime

| Resource | Contract |
|---|---|
| Direct `HttpClient` | Borrowed. The adapter never disposes it. |
| Factory client (source) | Created when enumeration starts, not when the iterator is created. It is disposed after the response has been enumerated. The factory and its handlers stay application-owned. |
| Factory client (sink) | Created and disposed on every `WriteAsync`. |
| Request | Created by your factory once per adapter call. It becomes transport-owned when the factory returns and is disposed exactly once, including its content. |
| Response | Sent with `HttpCompletionOption.ResponseHeadersRead`. The status is checked before any success body is read. The reader borrows the response until its enumeration ends and must yield materialized values. |
| Cleanup order | Reader enumerator, body-timeout scope, response, request, then the factory client. Every step runs on success, early break, cancellation, and failure. A lone primary failure is rethrown unchanged. See "Failures during cleanup" below for what you get when cleanup also fails. |

Before sending, the transport requires the request URI to be absolute or to
resolve against `HttpClient.BaseAddress`. Header names must be valid HTTP
tokens, and header values must not contain control characters.

## Timeouts and cancellation

- `HttpClient.Timeout` with `ResponseHeadersRead` covers only sending and
  receiving headers.
- `BodyTimeout` (default `null` = no extra limit) starts when headers arrive. It
  is a wall-clock limit on the whole body phase: acquiring the body stream,
  reading an opted-in error preview, and enumerating the reader, including the
  time the pipeline spends processing items between reads. Size it for the whole
  response, not for a single read.
- The timer uses the run's `PipelineActivationContext.TimeProvider`, so tests and
  hosts that supply a `TimeProvider` control it.
- If the body timeout fires and the caller has not cancelled, any resulting
  cancellation becomes a `TimeoutException`, even when your reader observed it
  through its own linked token. Caller cancellation stays an
  `OperationCanceledException` carrying the adapter's token.
- A late cancellation observed while disposing a fully read response never
  replaces the completed result.

### Failures during cleanup

- If cleanup fails after a cancellation, you still get an
  `OperationCanceledException` with the same token.
- If cleanup fails after a body timeout, you still get a `TimeoutException`.
- In both cases `InnerException` is an `AggregateException` holding the original
  exception first, then the cleanup failures in cleanup order.
- For any other failure, the `AggregateException` itself is thrown, in the same
  order.
- If the operation succeeded and exactly one cleanup step failed, that exception
  is rethrown unchanged.
- If the pipeline stops enumerating a source early, cleanup runs during disposal.
  A cleanup failure there surfaces from disposal, because the source cannot see
  an exception thrown by the code that consumes it.

## Non-success responses

By default a non-success status raises `HttpResponseStatusException` with only
the operation name and status code. The body is never read or logged.

Setting `HttpResponsePolicy.IncludeErrorBodyPreview = true` reads at most
`MaxErrorBodyBytes` (16 KiB by default) plus one unretained sentinel byte. That
exposes a UTF-8 `ErrorBodyPreview` and an `ErrorBodyPreviewTruncated` flag. Arbitrary
response text cannot be redacted generically, so a preview may contain sensitive
data. Enable it only when your application can handle that data. The package
never logs previews, authorization, cookies, query strings, request bodies, or
arbitrary headers.

A successful sink response is disposed without buffering its body. A sink
envelope with a `null` payload is skipped: no client, request, or send.

## Idempotency and retries

The optional `idempotencyKeySelector` must be pure and deterministic for an
envelope. It runs once per non-null sink write, before the request is sent. If it
returns `null`, no header is added. Otherwise the key must be 1–255 printable
ASCII characters with no whitespace. The header name defaults to
`Idempotency-Key` and must be a valid token that can be sent as a request header
(content headers such as `Content-Type` are rejected when the pipeline is composed). A conflicting header already on the request is an error; exactly
one identical value is accepted. A stable key does not prove that the server
deduplicates or that delivery is exactly-once.

The adapter never retries and never clones request content. Retries are your
application's choice. Three layers can each retry:

- `HttpClient` handlers,
- the Core stage policy,
- the Polly decorator from `SmartPipe.Extensions.Polly`.

Such a handler sees the same request instance and idempotency key on every
attempt. When the layers are independent, the maximum number of network sends
for one item is:

```text
core attempts × Polly attempts × handler attempts
```

## Trimming and NativeAOT

The `transport-full` contract covers the transport in trimmed and NativeAOT
applications. The `http-trim` and `http-nativeaot` consumers publish and run a
direct-client source and a factory-client sink. Your own request factories,
response readers, and handlers are outside that claim.

## Usage

```csharp
var definition = HttpPipelineDefinitionBuilderExtensions
    .FromHttp(
        new PipelineKey("orders"),
        httpClient,
        static (_, _) => ValueTask.FromResult(
            new HttpRequestMessage(HttpMethod.Get, "https://example.test/orders")),
        responseReader)
    .ToHttp(
        clientFactory,
        "orders-sink",
        static (envelope, _) => ValueTask.FromResult(
            new HttpRequestMessage(HttpMethod.Post, "https://example.test/archive")
            {
                Content = new StringContent(envelope.Payload.ToString()),
            }),
        idempotencyKeySelector: static envelope => $"order-{envelope.Payload.Id}");

await using var run = await definition.StartAsync();
await run.Completion;
```

For JSON bodies use the readers and request factories from
`SmartPipe.Extensions.Http.Json`.

## Migrating from 2.1.2

`HttpSelector<T>`, `HttpClientFactorySelector<T>`, `HttpSink<T>`,
`HttpClientFactorySink<T>`, and `HttpSelectorStreamingMode` were removed from
`SmartPipe.Extensions` by
[ADR-0004](../../docs/adr/0004-smartpipe-2.2-breaking-migration.md). There are no
wrappers or forwarders, so update the code and recompile:

| 2.1.2 | 2.2.0 |
|---|---|
| `HttpSelector<T>` / `HttpClientFactorySelector<T>` | `HttpPipelineComponents.Source` or `FromHttp`, with `HttpJsonResponseReaders.JsonArray`/`Ndjson` |
| `HttpSink<T>` / `HttpClientFactorySink<T>` | `HttpPipelineComponents.Sink` or `ToHttp`, with `HttpJsonRequestContent.Post` (or `ToHttpJson`) |
| `ResiliencePipeline` constructor argument | A handler on the `HttpClient`, the Core stage policy, or `SmartPipe.Extensions.Polly` |
| `useTraceIdIdempotencyKey: true` | `idempotencyKeySelector` that returns a stable key |
| `HttpSelectorStreamingMode.JsonArray` / `.Ndjson` | `HttpJsonResponseReaders.JsonArray` / `.Ndjson` |

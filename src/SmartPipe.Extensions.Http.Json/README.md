# SmartPipe.Extensions.Http.Json

Bounded, source-generated JSON codecs for `SmartPipe.Extensions.Http`: streaming
root-array and NDJSON response readers, and JSON request content for sinks.

## Package graph

`SmartPipe.Extensions.Http.Json` depends on `SmartPipe.Extensions.Http` and
`SmartPipe.Extensions.Json`. It adds no transport of its own, has no Polly or
bundle dependency, and has no reflection serialization fallback.

## What this package owns

- `HttpJsonResponseReaders.JsonArray<T>(JsonTypeInfo<T>, HttpJsonArrayOptions?)`
  streams the items of a root JSON array.
- `HttpJsonResponseReaders.Ndjson<T>(JsonTypeInfo<T>, HttpNdjsonOptions?)`
  streams newline-delimited JSON records.
- `HttpJsonRequestContent.Post<T>(Uri, JsonTypeInfo<T>)` returns an
  `HttpRequestFactory<T>` that creates a fresh POST request with a JSON body
  for every write.
- `HttpJsonPipelineDefinitionBuilderExtensions` provides `FromHttpJsonArray`,
  `FromHttpNdjson`, and `ToHttpJson` for direct and factory clients and for
  initial and typed builders. They only delegate to `FromHttp`/`ToHttp`, with
  the same return types and key rules.

## Metadata snapshot

At composition, each factory copies the caller's `JsonSerializerOptions` and
keeps its source-generated resolver and converters. It then applies the
configured `MaxDepth`, freezes the copy, and captures a private `JsonTypeInfo<T>`.
Runs use that snapshot directly. Your metadata is never changed, and later
changes to your options do not affect a composed reader.

A composed reader checks its `HttpResponseMessage` argument and body content
when it is invoked, before enumeration starts. A null response or a response
without content fails at that call rather than on the first `MoveNextAsync`.

## Limits and policies

| Option | Default | Contract |
|---|---|---|
| `MaxDepth` | 64 | JSON nesting limit applied to the frozen metadata. |
| `MaxUnframedBytes` | 256 MiB | Total raw body bytes. Counted while reading. Each read asks for at most the remaining budget plus one sentinel byte. |
| `MaxRecordSizeBytes` (NDJSON) | 16 MiB | Maximum encoded size of one line, and so of the memory buffered for it. Must fit a managed byte array. |
| `NullItemPolicy` | `Throw` | `Skip` drops a JSON `null` item at a complete item boundary. |
| `OversizeRecordPolicy` (NDJSON) | `Throw` | `Skip` drains the oversized line in bounded memory and continues at the next line. The total-body limit still applies. |

Exceeding `MaxUnframedBytes` raises a `JsonException`. So does invalid array or
record JSON, which ends the stream. A root array has only the total-body bound,
not a per-element limit, so a single element can use up to that bound. The array
reader never builds an unbounded list.

NDJSON framing uses the internal bounded UTF-8 line reader shared with
`SmartPipe.Extensions.Json` (`src/Shared/JsonFraming`). It handles LF, CRLF,
blank lines, a final record with no newline, UTF-8 characters split across
reads, and one-byte streams. It never uses `StreamReader.ReadLineAsync`. Raw
records and payloads never appear in exceptions or logs. The readers take no
logger, so `Skip` has no logging side effect.

## Trimming and NativeAOT

The `full-json-type-info` contract covers the source-generated
`JsonTypeInfo<T>` path. The `http-json-trim` and `http-json-nativeaot` consumers
publish and run an NDJSON source, a JSON POST sink, and a bounded array reader
with `JsonSerializerIsReflectionEnabledByDefault=false`. Reflection
serialization is not an advertised path.

## Usage

```csharp
[JsonSerializable(typeof(Order))]
internal sealed partial class OrderJsonContext : JsonSerializerContext;

var definition = HttpJsonPipelineDefinitionBuilderExtensions
    .FromHttpNdjson(
        new PipelineKey("orders"),
        httpClient,
        static (_, _) => ValueTask.FromResult(
            new HttpRequestMessage(HttpMethod.Get, "https://example.test/orders.ndjson")),
        OrderJsonContext.Default.Order,
        new HttpNdjsonOptions { OversizeRecordPolicy = HttpJsonOversizeRecordPolicy.Skip })
    .ToHttpJson(
        clientFactory,
        "orders-sink",
        new Uri("https://example.test/archive"),
        OrderJsonContext.Default.Order,
        idempotencyKeySelector: static envelope => $"order-{envelope.Payload.Id}");
```

For how the transport handles ownership, timeouts, error previews, and retries,
see the `SmartPipe.Extensions.Http` README.

using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Threading.Channels;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

_ = new CircuitBreaker();
_ = typeof(JsonTransform<string, string>);

var first = Channel.CreateUnbounded<int>();
var second = Channel.CreateUnbounded<int>();
await first.Writer.WriteAsync(20);
await second.Writer.WriteAsync(22);
first.Writer.Complete();
second.Writer.Complete();
var merged = new List<int>();
await foreach (var value in ChannelMerge.Merge(first.Reader, second.Reader).ReadAllAsync())
    merged.Add(value);

var composite = new CompositeTransform<int>(new FilterTransform<int>(static value => value > 0));
await composite.InitializeAsync();
var transformed = await composite.TransformAsync(ProcessingEnvelope<int>.Create(42));
var validator = new ValidationTransform<int>().Require(static value => value == 42, "expected 42");
await validator.InitializeAsync();
var validation = await validator.TransformAsync(ProcessingEnvelope<int>.Create(42));
var filtered = await validator.ToFilter().TransformAsync(ProcessingEnvelope<int>.Create(42));
var logger = new LoggerSink<int>(NullLogger<LoggerSink<int>>.Instance);
await logger.WriteAsync(ProcessingEnvelope<int>.Create(42));

await using var conditional = new ConditionalTransform<int>(
    condition: static value => value == 42,
    transform: new FilterTransform<int>(static value => value > 0));
await conditional.InitializeAsync();
var conditionalResult = await conditional.TransformAsync(ProcessingEnvelope<int>.Create(42));
await using var compression = new CompressionTransform(algorithm: CompressionAlgorithm.GZip);
await compression.InitializeAsync();
var compressed = await compression.TransformAsync(ProcessingEnvelope<byte[]>.Create(new byte[] { 20, 22 }));
if (!compressed.IsSuccess) return 1;
using var compressedStream = new MemoryStream(compressed.Value!);
using var decompressor = new GZipStream(compressedStream, CompressionMode.Decompress);
using var restoredStream = new MemoryStream();
await decompressor.CopyToAsync(restoredStream);
if (!conditionalResult.IsSuccess || !restoredStream.ToArray().SequenceEqual(new byte[] { 20, 22 })) return 1;

// Verify the original JSON-forwarded identities still resolve to their Json implementation.
Type[] jsonIdentities =
[
    typeof(SmartPipe.Extensions.Selectors.JsonFileSource<>),
    typeof(SmartPipe.Extensions.Selectors.DeadLetterSource<>),
    typeof(JsonFileSink<>), typeof(DeadLetterSink<>),
    typeof(DeadLetterWriteFailureMode), typeof(DeadLetterWriteException), typeof(JsonTransform<,>),
];
if (jsonIdentities.Any(type => type.Assembly.GetName().Name != "SmartPipe.Extensions.Json")) return 1;

if (merged.Sum() != 42 || !transformed.IsSuccess || !validation.IsSuccess || !filtered.IsSuccess)
    return 1;

Console.WriteLine("CONSUMER_OK legacy-binary-2.1.2");
return 0;

using Microsoft.Extensions.Logging.Abstractions;
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

if (merged.Sum() != 42 || !transformed.IsSuccess || !validation.IsSuccess || !filtered.IsSuccess)
    return 1;

Console.WriteLine("CONSUMER_OK legacy-binary-2.1.2");
return 0;

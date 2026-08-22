using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;

var sink = new LoggerSink<int>(
    NullLogger<LoggerSink<int>>.Instance,
    new LoggerSinkOptions<int> { PayloadMode = LoggerSinkPayloadMode.None });
await sink.WriteAsync(ProcessingEnvelope<int>.Create(42));

Console.WriteLine("CONSUMER_OK logging-direct");

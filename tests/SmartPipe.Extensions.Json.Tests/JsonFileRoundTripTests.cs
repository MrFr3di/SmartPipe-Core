#nullable enable

using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Tests;

public sealed class JsonFileRoundTripTests
{
    [Fact]
    public async Task BatchJsonLines_WithMultipleFlushes_RoundTripsThroughSource()
    {
        var path = Path.GetTempFileName();
        try
        {
            var sink = new JsonFileSink<RoundTripItem>(path, flushInterval: 2);
            await sink.WriteAsync(ProcessingEnvelope<RoundTripItem>.Create(new(1)));
            await sink.WriteAsync(ProcessingEnvelope<RoundTripItem>.Create(new(2)));
            await sink.WriteAsync(ProcessingEnvelope<RoundTripItem>.Create(new(3)));
            await sink.DisposeAsync();

            var source = new JsonFileSource<RoundTripItem>(path);
            var values = new List<int>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload.Value);

            Assert.Equal([1, 2, 3], values);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(JsonFileFormat.Ndjson)]
    [InlineData(JsonFileFormat.Array)]
    public async Task ExplicitFormat_RoundTripsWithoutChangingLegacyDefault(JsonFileFormat format)
    {
        var path = Path.GetTempFileName();
        try
        {
            var sinkOptions = new JsonFileSinkOptions
            {
                Format = format,
                OpenMode = JsonFileOpenMode.Create,
                FlushInterval = 2,
            };
            var sink = new JsonFileSink<RoundTripItem>(path, sinkOptions, serializerOptions: null);
            await sink.WriteAsync(ProcessingEnvelope<RoundTripItem>.Create(new(1)));
            await sink.WriteAsync(ProcessingEnvelope<RoundTripItem>.Create(new(2)));
            await sink.WriteAsync(ProcessingEnvelope<RoundTripItem>.Create(new(3)));
            await sink.DisposeAsync();

            var source = new JsonFileSource<RoundTripItem>(
                path,
                new JsonFileSourceOptions { Format = format });
            var values = new List<int>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload.Value);

            Assert.Equal([1, 2, 3], values);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ArrayAppend_IsRejectedAtConstruction()
    {
        var options = new JsonFileSinkOptions
        {
            Format = JsonFileFormat.Array,
            OpenMode = JsonFileOpenMode.Append,
        };

        Assert.Throws<ArgumentException>(() => new JsonFileSink<RoundTripItem>("items.json", options, serializerOptions: null));
    }

    public sealed record RoundTripItem(int Value);
}

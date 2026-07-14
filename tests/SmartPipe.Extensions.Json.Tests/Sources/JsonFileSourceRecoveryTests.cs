#nullable enable
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sources;

public sealed class JsonFileSourceRecoveryTests
{
    [Fact]
    public async Task OversizedMiddleRow_SkipAndLog_ContinuesAndLogsExactlyOnce()
    {
        var path = await WriteTempAsync("1\n12345\n2\n");
        try
        {
            var logger = new Mock<ILogger<JsonFileSource<int>>>();
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Ndjson,
                MaxRecordSizeBytes = 2,
                InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
            }, logger.Object);
            var values = new List<int>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload);

            Assert.Equal([1, 2], values);
            Assert.Single(logger.Invocations);
            Assert.Contains("record 2", logger.Invocations[0].Arguments[2]!.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancellation_DoesNotLogInvalidRecord()
    {
        var path = await WriteTempAsync("not-json\n");
        try
        {
            var logger = new Mock<ILogger<JsonFileSource<int>>>();
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Ndjson,
                InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
            }, logger.Object);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in source.ReadEnvelopesAsync(cts.Token)) { }
            });
            Assert.Empty(logger.Invocations);
        }
        finally { File.Delete(path); }
    }

    private static async Task<string> WriteTempAsync(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-json-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, contents);
        return path;
    }
}

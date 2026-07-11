using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Tests;

public sealed class JsonOptionsTests
{
    [Fact]
    public void Defaults_PreserveLegacyFileBehaviorAndUseStrictReads()
    {
        var source = new JsonFileSourceOptions();
        var sink = new JsonFileSinkOptions();
        var deadLetterSource = new DeadLetterSourceOptions();
        var deadLetterSink = new DeadLetterSinkOptions();

        Assert.Equal(JsonFileFormat.Auto, source.Format);
        Assert.Equal(InvalidJsonRecordBehavior.Throw, source.InvalidRecordBehavior);
        Assert.Equal(64, source.MaxDepth);
        Assert.Equal(16 * 1024 * 1024, source.MaxRecordSizeBytes);
        Assert.Equal(256L * 1024 * 1024, source.MaxDocumentSizeBytes);
        Assert.Equal(JsonFileFormat.BatchJsonLines, sink.Format);
        Assert.Equal(JsonFileOpenMode.Append, sink.OpenMode);
        Assert.Equal(1000, sink.FlushInterval);
        Assert.Equal(InvalidJsonRecordBehavior.Throw, deadLetterSource.InvalidRecordBehavior);
        Assert.Equal(JsonFileFormat.Auto, deadLetterSource.Format);
        Assert.Equal(256L * 1024 * 1024, deadLetterSource.MaxDocumentSizeBytes);
        Assert.Equal(3, deadLetterSink.RetryDelays.Count);
        Assert.Same(TimeProvider.System, deadLetterSink.TimeProvider);
    }
}

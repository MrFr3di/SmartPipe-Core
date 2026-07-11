using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public sealed class PackageOwnershipTests
{
    [Fact]
    public void JsonIntegrationTypes_AreOwnedByDedicatedAssembly()
    {
        var expectedAssembly = "SmartPipe.Extensions.Json";

        Assert.Equal(expectedAssembly, typeof(JsonFileSource<>).Assembly.GetName().Name);
        Assert.Equal(expectedAssembly, typeof(DeadLetterSource<>).Assembly.GetName().Name);
        Assert.Equal(expectedAssembly, typeof(JsonFileSink<>).Assembly.GetName().Name);
        Assert.Equal(expectedAssembly, typeof(DeadLetterSink<>).Assembly.GetName().Name);
        Assert.Equal(expectedAssembly, typeof(DeadLetterWriteFailureMode).Assembly.GetName().Name);
        Assert.Equal(expectedAssembly, typeof(DeadLetterWriteException).Assembly.GetName().Name);
        Assert.Equal(expectedAssembly, typeof(JsonTransform<,>).Assembly.GetName().Name);
    }

    [Fact]
    public void JsonLinesDeadLetterSerializer_RemainsOwnedByCore()
    {
        Assert.Equal("SmartPipe.Core", typeof(JsonLinesDeadLetterSerializer<>).Assembly.GetName().Name);
    }
}

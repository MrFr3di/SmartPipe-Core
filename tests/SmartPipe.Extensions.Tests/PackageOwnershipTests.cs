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
    public void Extensions_ForwardsEveryJsonTypeThatExistedIn211_AndNoNewOptions()
    {
        var expectedForwardedTypes = new HashSet<Type>
        {
            typeof(JsonFileSource<>),
            typeof(DeadLetterSource<>),
            typeof(JsonFileSink<>),
            typeof(DeadLetterSink<>),
            typeof(DeadLetterWriteFailureMode),
            typeof(DeadLetterWriteException),
            typeof(JsonTransform<,>),
        };

        var extensionsAssembly = typeof(DapperSelector<>).Assembly;
        var forwardedTypes = extensionsAssembly.GetForwardedTypes().ToHashSet();

        Assert.True(expectedForwardedTypes.SetEquals(forwardedTypes));
        Assert.DoesNotContain(typeof(JsonFileSourceOptions), forwardedTypes);
        Assert.DoesNotContain(typeof(JsonFileSinkOptions), forwardedTypes);
        Assert.DoesNotContain(typeof(DeadLetterSourceOptions), forwardedTypes);
        Assert.DoesNotContain(typeof(DeadLetterSinkOptions), forwardedTypes);
    }

    [Fact]
    public void JsonLinesDeadLetterSerializer_RemainsOwnedByCore()
    {
        Assert.Equal("SmartPipe.Core", typeof(JsonLinesDeadLetterSerializer<>).Assembly.GetName().Name);
    }

    [Fact]
    public void JsonFileSink_LegacyNullMetadataCallSites_RemainUnambiguous()
    {
        var nullException = Assert.Throws<ArgumentNullException>(
            () => new JsonFileSink<string>("output.json", null!));
        var defaultException = Assert.Throws<ArgumentNullException>(
            () => new JsonFileSink<string>(
                "output.json",
                default(System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<string>>)!));

        Assert.Equal("batchTypeInfo", nullException.ParamName);
        Assert.Equal("batchTypeInfo", defaultException.ParamName);
    }
}

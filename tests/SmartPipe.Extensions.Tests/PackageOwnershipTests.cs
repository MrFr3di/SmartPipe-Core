using System.Reflection;
using SmartPipe.Core;
using SmartPipe.Extensions.Hosting;
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
    public void Extensions_ForwardsEveryExtractedCompatibilityType_AndNoNewJsonOptions()
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
            typeof(ChannelMerge),
            typeof(CompositeTransform<>),
            typeof(CompressionAlgorithm),
            typeof(CompressionTransform),
            typeof(ConditionalTransform<>),
            typeof(FilterTransform<>),
            typeof(FilterValidationExtensions),
            typeof(ValidationTransform<>),
            typeof(LoggerSink<>),
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

    [Fact]
    public void HostingCompatibilityCluster_RemainsFacadeOwnedAndIsNotForwarded()
    {
        var facade = typeof(SmartPipeHostedService<,>).Assembly;
        var legacyTypes = new[]
        {
            typeof(SmartPipeHostedFailureBehavior),
            typeof(SmartPipeHostedServiceOptions),
            typeof(SmartPipeHostedService<,>),
        };

        Assert.All(legacyTypes, type => Assert.Same(facade, type.Assembly));
        Assert.DoesNotContain(facade.GetForwardedTypes(), legacyTypes.Contains);
        Assert.All(legacyTypes, type => Assert.Null(type.GetCustomAttribute<ObsoleteAttribute>()));

        var leaf = typeof(SmartPipeHostedPipelineOptions).Assembly;
        Assert.DoesNotContain(
            leaf.GetExportedTypes(),
            type => legacyTypes.Any(legacy => type.FullName == legacy.FullName));
        Assert.DoesNotContain(
            leaf.GetReferencedAssemblies(),
            reference => reference.Name == facade.GetName().Name);
    }
}

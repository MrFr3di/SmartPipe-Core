using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Json;

namespace SmartPipe.Extensions.Tests;

public sealed class JsonPipelineMetadataSnapshotLifecycleTests
{
    [Fact]
    public async Task CanonicalFileSource_UsesPrivateResolverSnapshotAfterCallerMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-json-snapshot-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{\"name\":\"private snapshot\"}\n");

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        var resolver = new DefaultJsonTypeInfoResolver();
        serializerOptions.TypeInfoResolver = resolver;
        var itemTypeInfo = (JsonTypeInfo<SnapshotItem>)resolver.GetTypeInfo(typeof(SnapshotItem), serializerOptions)!;
        var batchTypeInfo = (JsonTypeInfo<List<SnapshotItem>>)resolver.GetTypeInfo(typeof(List<SnapshotItem>), serializerOptions)!;
        var sourceOptions = new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson };

        var descriptor = JsonPipelineComponents.FileSource(
            path,
            itemTypeInfo,
            batchTypeInfo,
            sourceOptions);

        itemTypeInfo.Properties.Single(property => property.Name == "Name").Name = "MutatedName";
        serializerOptions.PropertyNameCaseInsensitive = false;

        var source = await ActivateAsync<IPipelineSource<SnapshotItem>>(descriptor);
        try
        {
            var values = new List<SnapshotItem>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload);

            Assert.Equal(new SnapshotItem("private snapshot"), Assert.Single(values));
        }
        finally
        {
            await source.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CanonicalTransform_UsesPrivateResolverSnapshotsAfterCallerMutation()
    {
        var inputOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var outputOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var inputResolver = new DefaultJsonTypeInfoResolver();
        var outputResolver = new DefaultJsonTypeInfoResolver();
        inputOptions.TypeInfoResolver = inputResolver;
        outputOptions.TypeInfoResolver = outputResolver;
        var inputTypeInfo = (JsonTypeInfo<SnapshotInput>)inputResolver.GetTypeInfo(typeof(SnapshotInput), inputOptions)!;
        var outputTypeInfo = (JsonTypeInfo<SnapshotOutput>)outputResolver.GetTypeInfo(typeof(SnapshotOutput), outputOptions)!;

        var descriptor = JsonPipelineComponents.Transform(
            inputTypeInfo,
            outputTypeInfo);

        outputTypeInfo.Properties.Single(property => property.Name == "Name").Name = "MutatedOutputName";
        outputOptions.PropertyNameCaseInsensitive = false;

        var transformer = await ActivateAsync<IPipelineTransformer<SnapshotInput, SnapshotOutput>>(descriptor);
        try
        {
            var result = await transformer.TransformAsync(
                ProcessingEnvelope<SnapshotInput>.Create(new SnapshotInput("private transform")));

            Assert.True(result.IsSuccess);
            Assert.Equal(new SnapshotOutput("private transform"), result.Value);
        }
        finally
        {
            await transformer.DisposeAsync();
        }
    }

    private static async Task<T> ActivateAsync<T>(PipelineComponent<T> descriptor)
        where T : class
    {
        var activatorProperty = descriptor.GetType().GetProperty(
            "Activator",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activatorProperty);

        var activator = Assert.IsAssignableFrom<Delegate>(activatorProperty!.GetValue(descriptor));
        var valueTask = activator.DynamicInvoke(
            new PipelineActivationContext(new PipelineKey("json-snapshot"), Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        Assert.NotNull(valueTask);

        var asTask = valueTask!.GetType().GetMethod("AsTask", Type.EmptyTypes);
        Assert.NotNull(asTask);
        var task = Assert.IsAssignableFrom<Task>(asTask!.Invoke(valueTask, null));
        await task;
        return Assert.IsAssignableFrom<T>(task.GetType().GetProperty("Result")!.GetValue(task));
    }
}

public sealed record SnapshotItem(string Name);

public sealed record SnapshotInput(string Name);

public sealed record SnapshotOutput(string Name);

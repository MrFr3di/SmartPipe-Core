using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Tests;

public sealed class JsonPipelineDefinitionContractTests
{
    [Fact]
    public void CanonicalComponentFactories_ExposeThePlannedPublicSurface()
    {
        var type = typeof(JsonFileSourceOptions).Assembly.GetType(
            "SmartPipe.Extensions.Json.JsonPipelineComponents");

        Assert.NotNull(type);
        Assert.True(type!.IsAbstract && type.IsSealed);

        AssertFactory(type, "FileSource", typeof(IPipelineSource<>), 5);
        AssertFactory(type, "FileSink", typeof(IPipelineSink<>), 4);
        AssertFactory(type, "Transform", typeof(IPipelineTransformer<,>), 2);
        AssertFactory(type, "DeadLetterSource", typeof(IPipelineSource<>), 4);
        AssertFactory(type, "DeadLetterSink", typeof(IPipelineSink<>), 4);
    }

    [Fact]
    public void CanonicalBuilders_ExposeThePlannedPublicSurface()
    {
        var assembly = typeof(JsonFileSourceOptions).Assembly;
        var builder = assembly.GetType("SmartPipe.Extensions.Json.JsonPipelineDefinitionBuilder");
        var extensions = assembly.GetType("SmartPipe.Extensions.Json.JsonPipelineDefinitionBuilderExtensions");

        Assert.NotNull(builder);
        Assert.NotNull(extensions);
        Assert.True(builder!.IsAbstract && builder.IsSealed);
        Assert.True(extensions!.IsAbstract && extensions.IsSealed);

        AssertMethod(builder, "FromJsonFile", parameterCount: 6);
        AssertMethod(builder, "FromJsonDeadLetterFile", parameterCount: 5);
        Assert.Equal(
            2,
            extensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Count(method => method.Name == "TransformJson" && method.IsGenericMethodDefinition));
        Assert.Equal(
            2,
            extensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Count(method => method.Name == "ToJsonFile" && method.IsGenericMethodDefinition));
    }

    [Fact]
    public async Task CanonicalFileSource_IsLazyRuntimeOwnedAndFreshPerActivation()
    {
        var components = RequireComponentsType();
        var loggerFactory = new TrackingLoggerFactory();
        var options = new JsonFileSourceOptions
        {
            Format = JsonFileFormat.Ndjson,
            MaxDepth = 8,
        };
        var descriptor = InvokeFactory(
            components,
            "FileSource",
            typeof(DefinitionItem),
            "this-file-does-not-exist.json",
            DefinitionJsonContext.Default.DefinitionItem,
            DefinitionJsonContext.Default.ListDefinitionItem,
            options,
            loggerFactory);

        Assert.Equal(PipelineComponentOwnership.RuntimeOwned, GetProperty(descriptor, "Ownership"));
        Assert.True((bool)GetProperty(descriptor, "Initialize")!);
        Assert.True((bool)GetProperty(descriptor, "IsPerRun")!);
        Assert.Equal(0, loggerFactory.CreateLoggerCalls);
        Assert.Equal(JsonFileFormat.Ndjson, options.Format);
        Assert.Equal(8, options.MaxDepth);

        var firstContext = new PipelineActivationContext(new PipelineKey("json"), Guid.NewGuid());
        var secondContext = new PipelineActivationContext(new PipelineKey("json"), Guid.NewGuid());
        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var secondCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = await InvokeActivatorAsync(descriptor, firstContext, firstCts.Token);
        var second = await InvokeActivatorAsync(descriptor, secondContext, secondCts.Token);

        Assert.NotSame(first, second);
        Assert.Equal(2, loggerFactory.CreateLoggerCalls);

        await ((IAsyncDisposable)first).DisposeAsync();
        await ((IAsyncDisposable)second).DisposeAsync();
        Assert.Equal(0, loggerFactory.DisposeCalls);
    }

    [Fact]
    public void CanonicalSkipPolicies_RequireAnExplicitLoggerFactory()
    {
        var components = RequireComponentsType();
        var sourceException = Assert.ThrowsAny<Exception>(() => InvokeFactory(
            components,
            "FileSource",
            typeof(DefinitionItem),
            "input.json",
            DefinitionJsonContext.Default.DefinitionItem,
            DefinitionJsonContext.Default.ListDefinitionItem,
            new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Ndjson,
                InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
            },
            null));
        AssertPolicyException(sourceException);

        var deadLetterSinkException = Assert.ThrowsAny<Exception>(() => InvokeFactory(
            components,
            "DeadLetterSink",
            typeof(DefinitionItem),
            "dead-letter.json",
            DefinitionJsonContext.Default.DeadLetterEnvelopeDefinitionItem,
            new DeadLetterSinkOptions { FailureMode = DeadLetterWriteFailureMode.LogAndDrop },
            null));
        AssertPolicyException(deadLetterSinkException);
    }

    [Fact]
    public void CanonicalDeadLetterSourceSkipPolicy_RequiresAnExplicitLoggerFactory()
    {
        var exception = Assert.ThrowsAny<Exception>(() => InvokeFactory(
            RequireComponentsType(),
            "DeadLetterSource",
            typeof(DefinitionItem),
            "dead-letter.json",
            DefinitionJsonContext.Default.DeadLetterEnvelopeDefinitionItem,
            new DeadLetterSourceOptions
            {
                Format = JsonFileFormat.Ndjson,
                InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
            },
            null));

        AssertPolicyException(exception);
    }

    [Fact]
    public void CanonicalMetadataFactories_RejectMissingOrUnresolvableResolvers()
    {
        var noResolverOptions = new JsonSerializerOptions();
        var noResolverItem = JsonTypeInfo.CreateJsonTypeInfo<DefinitionItem>(noResolverOptions);
        var noResolverBatch = JsonTypeInfo.CreateJsonTypeInfo<List<DefinitionItem>>(noResolverOptions);
        var noResolverException = Assert.ThrowsAny<Exception>(() => InvokeFactory(
            RequireComponentsType(),
            "FileSource",
            typeof(DefinitionItem),
            "input.json",
            noResolverItem,
            noResolverBatch,
            new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson },
            null));
        AssertArgumentException(noResolverException);

        var unresolvableOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new NullTypeInfoResolver(),
        };
        var unresolvableItem = JsonTypeInfo.CreateJsonTypeInfo<DefinitionItem>(unresolvableOptions);
        var unresolvableBatch = JsonTypeInfo.CreateJsonTypeInfo<List<DefinitionItem>>(unresolvableOptions);
        var unresolvableException = Assert.ThrowsAny<Exception>(() => InvokeFactory(
            RequireComponentsType(),
            "FileSource",
            typeof(DefinitionItem),
            "input.json",
            unresolvableItem,
            unresolvableBatch,
            new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson },
            null));
        AssertArgumentException(unresolvableException);
    }

    [Fact]
    public void CanonicalMetadataFactories_RejectMismatchedContexts()
    {
        var first = new DefinitionJsonContext(new JsonSerializerOptions());
        var second = new DefinitionJsonContext(new JsonSerializerOptions());
        var exception = Assert.ThrowsAny<Exception>(() => InvokeFactory(
            RequireComponentsType(),
            "FileSource",
            typeof(DefinitionItem),
            "input.json",
            first.DefinitionItem,
            second.ListDefinitionItem,
            new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson },
            null));

        AssertArgumentException(exception);
    }

    [Fact]
    public void CanonicalFileSource_DoesNotMutateCallerSerializerOptions()
    {
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        var context = new DefinitionJsonContext(serializerOptions);
        var originalMaxDepth = serializerOptions.MaxDepth;
        var originalReadOnly = serializerOptions.IsReadOnly;
        var originalResolver = serializerOptions.TypeInfoResolver;
        var originalItemTypeInfoReadOnly = context.DefinitionItem.IsReadOnly;
        var originalBatchTypeInfoReadOnly = context.ListDefinitionItem.IsReadOnly;

        _ = InvokeFactory(
            RequireComponentsType(),
            "FileSource",
            typeof(DefinitionItem),
            "input.json",
            context.DefinitionItem,
            context.ListDefinitionItem,
            new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson, MaxDepth = 13 },
            null);

        Assert.Equal(originalMaxDepth, serializerOptions.MaxDepth);
        Assert.Equal(originalReadOnly, serializerOptions.IsReadOnly);
        Assert.Same(originalResolver, serializerOptions.TypeInfoResolver);
        Assert.Same(serializerOptions, context.DefinitionItem.Options);
        Assert.Equal(originalItemTypeInfoReadOnly, context.DefinitionItem.IsReadOnly);
        Assert.Equal(originalBatchTypeInfoReadOnly, context.ListDefinitionItem.IsReadOnly);
    }

    [Fact]
    public async Task CanonicalDeadLetterSink_CopiesRetryDelaysAndCreatesLoggerAtActivation()
    {
        var loggerFactory = new TrackingLoggerFactory();
        var retryDelays = new List<TimeSpan>
        {
            TimeSpan.FromMilliseconds(11),
            TimeSpan.FromMilliseconds(22),
        };
        var descriptor = InvokeFactory(
            RequireComponentsType(),
            "DeadLetterSink",
            typeof(DefinitionItem),
            Path.Combine(Path.GetTempPath(), $"smartpipe-dl-{Guid.NewGuid():N}.json"),
            DefinitionJsonContext.Default.DeadLetterEnvelopeDefinitionItem,
            new DeadLetterSinkOptions
            {
                FailureMode = DeadLetterWriteFailureMode.LogAndDrop,
                RetryDelays = retryDelays,
            },
            loggerFactory);

        Assert.Equal(0, loggerFactory.CreateLoggerCalls);
        retryDelays[0] = TimeSpan.FromHours(1);
        var sink = await InvokeActivatorAsync(
            descriptor,
            new PipelineActivationContext(new PipelineKey("json-dead-letter"), Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, loggerFactory.CreateLoggerCalls);
        var delaysField = sink.GetType().GetField("_retryDelays", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(delaysField);
        var capturedDelays = Assert.IsType<TimeSpan[]>(delaysField!.GetValue(sink));
        Assert.Equal(TimeSpan.FromMilliseconds(11), capturedDelays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(22), capturedDelays[1]);

        await ((IAsyncDisposable)sink).DisposeAsync();
        Assert.Equal(0, loggerFactory.DisposeCalls);
    }

    [Fact]
    public void CanonicalBuilderExtensions_ChainTypedDefinitionWithoutActivation()
    {
        var assembly = typeof(JsonFileSourceOptions).Assembly;
        var builderType = assembly.GetType("SmartPipe.Extensions.Json.JsonPipelineDefinitionBuilder");
        var extensionsType = assembly.GetType("SmartPipe.Extensions.Json.JsonPipelineDefinitionBuilderExtensions");
        Assert.NotNull(builderType);
        Assert.NotNull(extensionsType);

        var itemTypeInfo = DefinitionJsonContext.Default.DefinitionItem;
        var listTypeInfo = DefinitionJsonContext.Default.ListDefinitionItem;
        var inputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-definition-input-{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-definition-output-{Guid.NewGuid():N}.json");
        Assert.False(File.Exists(inputPath));
        Assert.False(File.Exists(outputPath));
        var sourceFactory = builderType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "FromJsonFile" && method.IsGenericMethodDefinition);
        var builder = sourceFactory.MakeGenericMethod(typeof(DefinitionItem)).Invoke(null,
            [
                new PipelineKey("json-builder"),
                inputPath,
                itemTypeInfo,
                listTypeInfo,
                new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson },
                null,
            ]);
        Assert.NotNull(builder);

        var transform = extensionsType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "TransformJson"
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 2);
        var transformed = transform.MakeGenericMethod(typeof(DefinitionItem), typeof(DefinitionItem)).Invoke(null,
            [
                builder,
                new PipelineStageKey("json-transform"),
                itemTypeInfo,
                itemTypeInfo,
                null,
                null,
                null,
            ]);
        Assert.NotNull(transformed);

        var sinkFactory = extensionsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "ToJsonFile"
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 2);
        var definition = sinkFactory.MakeGenericMethod(typeof(DefinitionItem), typeof(DefinitionItem)).Invoke(null,
            [
                transformed,
                outputPath,
                itemTypeInfo,
                listTypeInfo,
                new JsonFileSinkOptions { Format = JsonFileFormat.BatchJsonLines },
            ]);

        var typedDefinition = Assert.IsType<PipelineDefinition<DefinitionItem, DefinitionItem>>(definition);
        Assert.True(typedDefinition.HasSink);
        Assert.Single(typedDefinition.Stages);
        Assert.Equal("json-transform", typedDefinition.Stages[0].Key.Value);
        Assert.False(File.Exists(inputPath));
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void JsonPackage_DoesNotReferenceDependencyInjectionOrFacade()
    {
        var references = typeof(JsonFileSourceOptions).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .Where(static name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("SmartPipe.Extensions", references);
        Assert.DoesNotContain("SmartPipe.Extensions.DependencyInjection", references);
    }

    private static Type RequireComponentsType()
    {
        var type = typeof(JsonFileSourceOptions).Assembly.GetType(
            "SmartPipe.Extensions.Json.JsonPipelineComponents");
        Assert.NotNull(type);
        return type!;
    }

    private static object InvokeFactory(
        Type components,
        string name,
        Type genericType,
        params object?[] arguments)
    {
        var method = components.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name == name && candidate.IsGenericMethodDefinition);
        return method.MakeGenericMethod(genericType).Invoke(null, arguments)!;
    }

    private static object? GetProperty(object value, string name)
    {
        var property = value.GetType().GetProperty(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(property);
        return property!.GetValue(value);
    }

    private static async Task<object> InvokeActivatorAsync(
        object descriptor,
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        var property = descriptor.GetType().GetProperty(
            "Activator",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(property);
        var activator = Assert.IsAssignableFrom<Delegate>(property!.GetValue(descriptor));
        var valueTask = activator.DynamicInvoke(context, cancellationToken);
        Assert.NotNull(valueTask);
        var asTask = valueTask!.GetType().GetMethod("AsTask", Type.EmptyTypes);
        Assert.NotNull(asTask);
        var task = Assert.IsAssignableFrom<Task>(asTask!.Invoke(valueTask, null));
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static void AssertPolicyException(Exception exception)
    {
        var actual = UnwrapInvocationException(exception);
        Assert.True(
            actual is ArgumentException or InvalidOperationException,
            $"Expected a policy validation exception, got {actual.GetType().FullName}: {actual.Message}");
    }

    private static void AssertArgumentException(Exception exception)
    {
        var actual = UnwrapInvocationException(exception);
        Assert.IsType<ArgumentException>(actual);
    }

    private static Exception UnwrapInvocationException(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException!
            : exception;

    private static void AssertFactory(Type type, string name, Type resultDefinition, int parameterCount)
    {
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(candidate => candidate.Name == name);

        Assert.NotNull(method);
        Assert.True(method!.IsGenericMethodDefinition);
        Assert.Equal(resultDefinition, method.ReturnType.GetGenericArguments()[0].GetGenericTypeDefinition());
        Assert.Equal(parameterCount, method.GetParameters().Length);
    }

    private static void AssertMethod(Type type, string name, int parameterCount)
    {
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(candidate => candidate.Name == name);

        Assert.NotNull(method);
        Assert.True(method!.IsGenericMethodDefinition);
        Assert.Equal(parameterCount, method.GetParameters().Length);
    }

    private sealed class NullTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }

    private sealed class TrackingLoggerFactory : ILoggerFactory
    {
        public int CreateLoggerCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            CreateLoggerCalls++;
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() => DisposeCalls++;
    }
}

public sealed record DefinitionItem(int Id);

[JsonSerializable(typeof(DefinitionItem))]
[JsonSerializable(typeof(List<DefinitionItem>))]
[JsonSerializable(typeof(DeadLetterEnvelope<DefinitionItem>))]
internal sealed partial class DefinitionJsonContext : JsonSerializerContext;

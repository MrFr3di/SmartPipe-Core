using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class FactoryProviderTests
{
    [Fact]
    public void Provider_ResolvesOnlyExactRegisteredKeyAndTypePair()
    {
        var services = new ServiceCollection();
        var definition = CreateDefinition<int>("orders");
        services.AddSmartPipe().AddPipeline(definition);

        using var root = services.BuildServiceProvider();
        var provider = root.GetRequiredService<ISmartPipeFactoryProvider>();
        var direct = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>("orders");

        Assert.Same(root.GetRequiredService<SmartPipeFactoryProvider>(), provider);
        Assert.Same(direct, provider.GetFactory<int, int>(definition.Key));
        Assert.True(provider.TryGetFactory<int, int>(definition.Key, out var resolved));
        Assert.Same(direct, resolved);
    }

    [Fact]
    public async Task Provider_ResolvesAndStartsMultipleKeysForTheSameTypePair()
    {
        var services = new ServiceCollection();
        var firstDefinition = CreateDefinition<int>("first");
        var secondDefinition = CreateDefinition<int>("second");
        var builder = services.AddSmartPipe();
        builder.AddPipeline(firstDefinition);
        builder.AddPipeline(secondDefinition);
        await using var root = services.BuildServiceProvider();
        var provider = root.GetRequiredService<ISmartPipeFactoryProvider>();

        var first = await provider.GetFactory<int, int>(firstDefinition.Key)
            .StartAsync(TestContext.Current.CancellationToken);
        var second = await provider.GetFactory<int, int>(secondDefinition.Key)
            .StartAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(first.Completion, second.Completion);

        Assert.Equal(firstDefinition.Key, first.PipelineKey);
        Assert.Equal(secondDefinition.Key, second.PipelineKey);
        Assert.NotSame(
            provider.GetFactory<int, int>(firstDefinition.Key),
            provider.GetFactory<int, int>(secondDefinition.Key));
    }

    [Fact]
    public void Provider_MissingKeyUsesTryAndGetSemantics()
    {
        var services = new ServiceCollection();
        services.AddSmartPipe();
        using var root = services.BuildServiceProvider();
        var provider = root.GetRequiredService<ISmartPipeFactoryProvider>();
        var missing = new PipelineKey("missing");

        Assert.False(provider.TryGetFactory<int, int>(missing, out var factory));
        Assert.Null(factory);
        Assert.Throws<KeyNotFoundException>(() => provider.GetFactory<int, int>(missing));
        Assert.Throws<ArgumentException>(() => provider.GetFactory<int, int>(default));
        Assert.Throws<ArgumentException>(() => provider.TryGetFactory<int, int>(default, out _));
    }

    [Fact]
    public void Provider_ExistingKeyWithDifferentTypesThrowsBeforeServiceResolution()
    {
        var services = new ServiceCollection();
        services.AddSmartPipe().AddPipeline(CreateDefinition<int>("orders"));
        using var root = services.BuildServiceProvider();
        var provider = root.GetRequiredService<ISmartPipeFactoryProvider>();
        var key = new PipelineKey("orders");

        var getError = Assert.Throws<InvalidOperationException>(
            () => provider.GetFactory<string, string>(key));
        var tryError = Assert.Throws<InvalidOperationException>(
            () => provider.TryGetFactory<string, string>(key, out _));

        Assert.Contains(typeof(int).ToString(), getError.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(string).ToString(), getError.Message, StringComparison.Ordinal);
        Assert.Equal(getError.Message, tryError.Message);
    }

    private static PipelineDefinition<T, T> CreateDefinition<T>(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<T>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSource<T>>(new EmptySource<T>())))
            .Build();

    private sealed class EmptySource<T> : IPipelineSource<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

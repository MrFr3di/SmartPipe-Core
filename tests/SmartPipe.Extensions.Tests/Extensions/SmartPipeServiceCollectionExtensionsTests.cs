#nullable enable
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using Xunit;

namespace SmartPipe.Extensions.Tests.Extensions;

public class SmartPipeServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSmartPipe_RegistersPipeline()
    {
        var services = new ServiceCollection();
        services.AddSmartPipe<string, string>();
        
        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetService<SmartPipeChannel<string, string>>();
        
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AddSmartPipe_WithConfigureAction_RegistersPipeline()
    {
        var services = new ServiceCollection();
        services.AddSmartPipe<string, string>(pipeline => 
        {
            // Configure pipeline
        });
        
        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetService<SmartPipeChannel<string, string>>();
        
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AddSmartPipe_WithOptions_RegistersPipeline()
    {
        var services = new ServiceCollection();
        services.AddSmartPipe<string, string>(
            options => options.BoundedCapacity = 100,
            pipeline => { }
        );
        
        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetService<SmartPipeChannel<string, string>>();
        
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AddSmartPipeFactory_Create_ShouldReturnFreshPipelinePerCall()
    {
        var services = new ServiceCollection();
        services.AddSmartPipeFactory<string, string>();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISmartPipeChannelFactory<string, string>>();

        var first = factory.Create();
        var second = factory.Create();

        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public void AddSmartPipeFactory_ShouldSnapshotOptionsObjectAtRegistration()
    {
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 25,
            MaxDegreeOfParallelism = 2,
        };
        options.EnableFeature("Metrics");

        var services = new ServiceCollection();
        services.AddSmartPipeFactory<string, string>(options);
        options.BoundedCapacity = 99;
        options.MaxDegreeOfParallelism = 9;
        options.DisableFeature("Metrics");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISmartPipeChannelFactory<string, string>>();

        var pipeline = factory.Create();

        pipeline.Options.BoundedCapacity.Should().Be(25);
        pipeline.Options.MaxDegreeOfParallelism.Should().Be(2);
        pipeline.Options.IsEnabled("Metrics").Should().BeTrue();
    }

    [Fact]
    public void AddSmartPipeFactory_ShouldSnapshotConfigureOptionsAtRegistration()
    {
        var capacity = 40;
        var services = new ServiceCollection();
        services.AddSmartPipeFactory<string, string>(options => options.BoundedCapacity = capacity);
        capacity = 80;

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISmartPipeChannelFactory<string, string>>();

        factory.Create().Options.BoundedCapacity.Should().Be(40);
    }

    [Fact]
    public void AddSmartPipeFactory_ShouldRunPipelineConfigurationForEachCreate()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSmartPipeFactory<string, string>(
            static _ => { },
            (_, pipeline) =>
            {
                calls++;
                pipeline.AddSource(new EmptySource<string>());
            });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISmartPipeChannelFactory<string, string>>();

        factory.Create();
        factory.Create();

        calls.Should().Be(2);
    }

    [Fact]
    public void AddSmartPipeFactory_ShouldResolveConfigurationFromCallerScope()
    {
        var observedScopeIds = new List<Guid>();
        var services = new ServiceCollection();
        services.AddScoped<ScopedMarker>();
        services.AddSmartPipeFactory<string, string>(
            static _ => { },
            (sp, pipeline) =>
            {
                var marker = sp.GetRequiredService<ScopedMarker>();
                observedScopeIds.Add(marker.Id);
                pipeline.AddSource(new EmptySource<string>());
            });

        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        firstScope.ServiceProvider
            .GetRequiredService<ISmartPipeChannelFactory<string, string>>()
            .Create();
        secondScope.ServiceProvider
            .GetRequiredService<ISmartPipeChannelFactory<string, string>>()
            .Create();

        observedScopeIds.Should().HaveCount(2);
        observedScopeIds[0].Should().NotBe(observedScopeIds[1]);
    }
}

internal sealed class EmptySource<T> : ISource<T>
{
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;
}

internal sealed class ScopedMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

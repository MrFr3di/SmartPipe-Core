#nullable enable
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
}

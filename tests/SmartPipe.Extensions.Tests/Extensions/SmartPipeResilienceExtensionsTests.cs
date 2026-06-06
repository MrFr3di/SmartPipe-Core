#nullable enable
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using SmartPipe.Core;
using Xunit;

namespace SmartPipe.Extensions.Tests.Extensions;

public class SmartPipeResilienceExtensionsTests
{
    [Fact]
    public void AddSmartPipe_RegistersPipelineWithResilience()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        
        services.AddSmartPipe<string, string>(
            pipeline => { },
            builder => builder.AddRetry(new() { MaxRetryAttempts = 3 })
        );

        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetService<SmartPipeChannel<string, string>>();
        
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AddSmartPipe_RegistersFactoryWithResilience()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSmartPipe<string, string>(
            pipeline => { },
            builder => builder.AddRetry(new() { MaxRetryAttempts = 3 })
        );

        using var provider = services.BuildServiceProvider();

        provider.GetService<ISmartPipeChannelFactory<string, string>>().Should().NotBeNull();
    }

    [Fact]
    public void AddSmartPipeHostedService_RegistersHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        
        services.AddSmartPipeHostedService<string, string>(
            pipeline => { }
        );

        var provider = services.BuildServiceProvider();
        var hostedService = provider.GetService<Microsoft.Extensions.Hosting.IHostedService>();
        
        Assert.NotNull(hostedService);
        Assert.IsType<SmartPipe.Extensions.SmartPipeHostedService<string, string>>(hostedService);
    }

    [Fact]
    public void AddSmartPipeHostedService_RegistersFactoryAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSmartPipeHostedService<string, string>(
            pipeline => { }
        );

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<ISmartPipeChannelFactory<string, string>>().Should().NotBeNull();
        provider.GetServices<IHostedService>()
            .Should()
            .ContainSingle(service => service is SmartPipeHostedService<string, string>);
    }
}

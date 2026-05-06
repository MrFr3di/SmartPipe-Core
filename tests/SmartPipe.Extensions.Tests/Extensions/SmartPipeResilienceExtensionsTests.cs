#nullable enable
using Microsoft.Extensions.DependencyInjection;
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
}

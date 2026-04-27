using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Tests;

public class HealthCheckTests
{
    [Fact]
    public async Task LivenessCheck_WhenNotPaused_ShouldBeHealthy()
    {
        var pipe = new SmartPipeChannel<string, string>();
        var check = new SmartPipeLivenessCheck<string, string>(pipe);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task LivenessCheck_WhenPaused_ShouldBeUnhealthy()
    {
        var pipe = new SmartPipeChannel<string, string>();
        pipe.Pause();
        var check = new SmartPipeLivenessCheck<string, string>(pipe);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task ReadinessCheck_Default_ShouldBeHealthy()
    {
        var pipe = new SmartPipeChannel<string, string>();
        var check = new SmartPipeReadinessCheck<string, string>(pipe);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Healthy);
    }
}

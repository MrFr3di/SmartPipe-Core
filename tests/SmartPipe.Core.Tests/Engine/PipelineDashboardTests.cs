#nullable enable
using SmartPipe.Core;
using Xunit;

namespace SmartPipe.Core.Tests.Engine;

public class PipelineDashboardTests
{
    [Fact]
    public void PipelineDashboard_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var dashboard = new PipelineDashboard();

        // Assert
        Assert.Equal(PipelineState.NotStarted, dashboard.State);
        Assert.Equal(0, dashboard.Current);
        Assert.Null(dashboard.Total);
        Assert.Equal(TimeSpan.Zero, dashboard.Elapsed);
        Assert.Equal(0.0, dashboard.P99LatencyMs);
        Assert.Equal("N/A", dashboard.CBState);
        Assert.NotNull(dashboard.Metrics);
        Assert.Empty(dashboard.Metrics);
    }

    [Fact]
    public void PipelineDashboard_Properties_CanBeSet()
    {
        // Arrange
        var dashboard = new PipelineDashboard();
        var metrics = new Dictionary<string, object> { ["key"] = "value" };

        // Act
        dashboard.State = PipelineState.Running;
        dashboard.Current = 100;
        dashboard.Total = 500;
        dashboard.Elapsed = TimeSpan.FromSeconds(30);
        dashboard.P99LatencyMs = 45.5;
        dashboard.CBState = "Closed";
        dashboard.Metrics = metrics;

        // Assert
        Assert.Equal(PipelineState.Running, dashboard.State);
        Assert.Equal(100, dashboard.Current);
        Assert.Equal(500, dashboard.Total);
        Assert.Equal(TimeSpan.FromSeconds(30), dashboard.Elapsed);
        Assert.Equal(45.5, dashboard.P99LatencyMs);
        Assert.Equal("Closed", dashboard.CBState);
        Assert.Same(metrics, dashboard.Metrics);
    }

    [Fact]
    public void CreateDashboard_ReturnsValidDashboard()
    {
        // Arrange
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<object, object>(options);

        // Act
        var dashboard = channel.CreateDashboard();

        // Assert
        Assert.NotNull(dashboard);
        Assert.Equal(PipelineState.NotStarted, dashboard.State);
        Assert.Equal(0, dashboard.Current);
        Assert.Null(dashboard.Total);
        Assert.NotNull(dashboard.Metrics);
    }

    [Fact]
    public void CreateDashboard_CBState_WhenCircuitBreakerIsNull_ReturnsNA()
    {
        // Arrange
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<object, object>(options);

        // Act
        var dashboard = channel.CreateDashboard();

        // Assert
        Assert.Equal("N/A", dashboard.CBState);
    }

    [Fact]
    public void CreateDashboard_CBState_WhenCircuitBreakerIsEnabled_ReturnsState()
    {
        // Arrange
        var options = new SmartPipeChannelOptions();
        options.EnableFeature("CircuitBreaker");
        var channel = new SmartPipeChannel<object, object>(options);

        // Act
        var dashboard = channel.CreateDashboard();

        // Assert
        Assert.NotEqual("N/A", dashboard.CBState);
        Assert.False(string.IsNullOrEmpty(dashboard.CBState));
    }
}

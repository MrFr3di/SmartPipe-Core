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
        var dashboard = PipelineDashboard.Empty;

        // Assert
        Assert.Equal(PipelineState.NotStarted, dashboard.State);
        Assert.Equal(0, dashboard.Current);
        Assert.Null(dashboard.Total);
        Assert.Equal(TimeSpan.Zero, dashboard.Elapsed);
        Assert.Equal(0.0, dashboard.P99LatencyMs);
        Assert.Equal("N/A", dashboard.CbState);
        Assert.NotNull(dashboard.Metrics);
        Assert.Empty(dashboard.Metrics);
    }

    [Fact]
    public void PipelineDashboard_Properties_CanBeSet()
    {
        // Arrange: create a fully populated dashboard via constructor
        var metrics = new Dictionary<string, object> { ["key"] = "value" };

        // Act
        var dashboard = new PipelineDashboard(
            PipelineState.Running,
            100,
            500,
            TimeSpan.FromSeconds(30),
            45.5,
            "Closed",
            metrics);

        // Assert
        Assert.Equal(PipelineState.Running, dashboard.State);
        Assert.Equal(100, dashboard.Current);
        Assert.Equal(500, dashboard.Total);
        Assert.Equal(TimeSpan.FromSeconds(30), dashboard.Elapsed);
        Assert.Equal(45.5, dashboard.P99LatencyMs);
        Assert.Equal("Closed", dashboard.CbState);
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

        // Assert — dashboard is a value type, cannot be null
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
        Assert.Equal("N/A", dashboard.CbState);
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
        Assert.NotEqual("N/A", dashboard.CbState);
        Assert.False(string.IsNullOrEmpty(dashboard.CbState));
    }
}
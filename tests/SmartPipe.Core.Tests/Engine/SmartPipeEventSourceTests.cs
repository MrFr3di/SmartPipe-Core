using System.Diagnostics.Tracing;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeEventSourceTests
{
    private sealed class TestEventListener : EventListener
    {
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "SmartPipe.EventSource")
                EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All,
                    new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "1" });
        }
    }

    [Fact]
    public void Log_ShouldBeSingleton()
    {
        var instance1 = SmartPipeEventSource.Log;
        var instance2 = SmartPipeEventSource.Log;
        ReferenceEquals(instance1, instance2).Should().BeTrue();
    }

    [Fact]
    public void EnableViaListener_ShouldInitializeCounters()
    {
        using var listener = new TestEventListener();
        Thread.Sleep(100);
        SmartPipeEventSource.Log.CountersInitialized.Should().BeTrue();
    }

    [Fact]
    public void RecordItemProcessed_AfterEnable_ShouldNotThrow()
    {
        using var listener = new TestEventListener();
        Thread.Sleep(100);
        SmartPipeEventSource.Log.Invoking(e => e.RecordItemProcessed()).Should().NotThrow();
    }

    [Fact]
    public void RecordBackpressureActivation_AfterEnable_ShouldNotThrow()
    {
        using var listener = new TestEventListener();
        Thread.Sleep(100);
        SmartPipeEventSource.Log.Invoking(e => e.RecordBackpressureActivation()).Should().NotThrow();
    }

    [Fact]
    public void Fields_ShouldBeSettable()
    {
        SmartPipeEventSource.Log._queueSize = 42;
        SmartPipeEventSource.Log._queueSize.Should().Be(42);
        SmartPipeEventSource.Log._queueSize = 0;
    }
}

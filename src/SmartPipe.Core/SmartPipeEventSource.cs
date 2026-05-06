#nullable enable

using System.Diagnostics.Tracing;

namespace SmartPipe.Core;

/// <summary>ETW EventSource for SmartPipe runtime telemetry.</summary>
[EventSource(Name = "SmartPipe.EventSource")]
public sealed class SmartPipeEventSource : EventSource
{
    /// <summary>Singleton instance for logging pipeline events.</summary>
    public static readonly SmartPipeEventSource Log = new();

    private IncrementingEventCounter? _itemsProcessedCounter;
    private PollingCounter? _queueSizeCounter;
    private PollingCounter? _poolHitRateCounter;
    private IncrementingEventCounter? _backpressureCounter;
    private PollingCounter? _circuitBreakerStateCounter;
    private bool _countersInitialized;

    private SmartPipeEventSource() : base("SmartPipe.EventSource") { }

    /// <summary>
    /// Called when an event command is received (enable/disable tracing).
    /// Initializes performance counters when tracing is enabled.
    /// </summary>
    /// <param name="command">The event command arguments.</param>
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable && !_countersInitialized)
        {
            _countersInitialized = true;
            _itemsProcessedCounter = new IncrementingEventCounter("items-processed", this)
            {
                DisplayName = "Items Processed", DisplayUnits = "items"
            };
            _queueSizeCounter = new PollingCounter("queue-size", this, () => _queueSize)
            {
                DisplayName = "Queue Size", DisplayUnits = "items"
            };
            _poolHitRateCounter = new PollingCounter("pool-hit-rate", this, () => _poolHitRate)
            {
                DisplayName = "ObjectPool Hit Rate", DisplayUnits = "%"
            };
            _backpressureCounter = new IncrementingEventCounter("backpressure/sec", this)
            {
                DisplayName = "Backpressure Activations", DisplayUnits = "activations/sec"
            };
            _circuitBreakerStateCounter = new PollingCounter("cb-state", this, () => _cbState)
            {
                DisplayName = "Circuit Breaker State", DisplayUnits = "0=Closed,1=Open,2=HalfOpen"
            };
        }
    }

    /// <summary>Current queue size for polling counter.</summary>
    public float _queueSize;
    /// <summary>ObjectPool hit rate for polling counter.</summary>
    public float _poolHitRate;
    /// <summary>Circuit breaker state for polling counter (0=Closed, 1=Open, 2=HalfOpen).</summary>
    public float _cbState;

    /// <summary>Record one processed item (increment counter).</summary>
    public void RecordItemProcessed() => _itemsProcessedCounter?.Increment();

    /// <summary>Record one backpressure activation.</summary>
    public void RecordBackpressureActivation() => _backpressureCounter?.Increment();

    /// <summary>True if EventSource counters have been initialized.</summary>
    public bool CountersInitialized => _countersInitialized;
}

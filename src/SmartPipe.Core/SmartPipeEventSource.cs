using System.Diagnostics.Tracing;

namespace SmartPipe.Core;

[EventSource(Name = "SmartPipe.EventSource")]
public sealed class SmartPipeEventSource : EventSource
{
    public static readonly SmartPipeEventSource Log = new();

    private IncrementingEventCounter? _itemsProcessedCounter;
    private PollingCounter? _queueSizeCounter;
    private PollingCounter? _poolHitRateCounter;
    private IncrementingEventCounter? _backpressureCounter;
    private PollingCounter? _circuitBreakerStateCounter;
    private bool _countersInitialized;

    private SmartPipeEventSource() : base("SmartPipe.EventSource") { }

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

    public float _queueSize;
    public float _poolHitRate;
    public float _cbState;

    public void RecordItemProcessed() => _itemsProcessedCounter?.Increment();
    public void RecordBackpressureActivation() => _backpressureCounter?.Increment();
    public bool CountersInitialized => _countersInitialized;
}

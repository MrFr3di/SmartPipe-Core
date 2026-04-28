using System;
using System.Collections.Generic;
using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>Pipeline engine configuration.</summary>
public class SmartPipeChannelOptions
{
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
    public int BoundedCapacity { get; set; } = 1000;
    public bool ContinueOnError { get; set; } = true;
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool UseRendezvous { get; set; }
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
    public Action<SmartPipeMetrics>? OnMetrics { get; set; }
    public DeduplicationFilter? DeduplicationFilter { get; set; }

    /// <summary>Progress reporting delegate. Called after each item.</summary>
    public Action<int, int?, TimeSpan, TimeSpan?>? OnProgress { get; set; }

    /// <summary>Optional DeadLetter sink for exhausted retries.</summary>
    public ISink<object>? DeadLetterSink { get; set; }

    public Dictionary<string, bool> FeatureFlags { get; } = new()
    {
        ["RetryQueue"] = false, ["Metrics"] = true, ["CircuitBreaker"] = false,
        ["ObjectPool"] = true, ["DebugSampling"] = false, ["CuckooFilter"] = false, ["JumpHash"] = false
    };

    public bool IsEnabled(string f) => FeatureFlags.TryGetValue(f, out var e) && e;
    public void EnableFeature(string f) => FeatureFlags[f] = true;
    public void DisableFeature(string f) => FeatureFlags[f] = false;
}

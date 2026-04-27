using System.Threading.Channels;
using System;
using System.Collections.Generic;

namespace SmartPipe.Core;

/// <summary>Pipeline engine configuration with resilience and observability options.</summary>
public class SmartPipeChannelOptions
{
    /// <summary>Maximum parallel transformers. Default: logical processors.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>Channel capacity for input and output. Default: 1000.</summary>
    public int BoundedCapacity { get; set; } = 1000;
    /// <summary>Use Rendezvous Channel (BoundedCapacity=0, strict Producer-Consumer sync). Default: false.</summary>
    public bool UseRendezvous { get; set; } = false;
    /// <summary>Channel full mode: Wait (default), DropOldest, DropNewest. Default: Wait.</summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>Continue processing when a transformer fails. Default: true.</summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>Total request timeout for the entire pipeline. Default: 5 minutes.</summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Attempt timeout per transformer invocation. Default: 30 seconds.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional metrics delegate called after each element.</summary>
    public Action<SmartPipeMetrics>? OnMetrics { get; set; }

    /// <summary>Optional Bloom filter for deduplication.</summary>
    public DeduplicationFilter? DeduplicationFilter { get; set; }

    /// <summary>Feature flags for optional components.</summary>
    public Dictionary<string, bool> FeatureFlags { get; } = new()
    {
        ["RetryQueue"] = false,
        ["Metrics"] = true,
        ["CircuitBreaker"] = false,
        ["ObjectPool"] = true,        // Включён по умолчанию — чистая оптимизация
        ["DebugSampling"] = false,
        ["CuckooFilter"] = false,
        ["JumpHash"] = false
    };

    /// <summary>Check if a feature is enabled.</summary>
    public bool IsEnabled(string feature) =>
        FeatureFlags.TryGetValue(feature, out var enabled) && enabled;

    /// <summary>Enable a feature flag.</summary>
    public void EnableFeature(string feature) => FeatureFlags[feature] = true;

    /// <summary>Disable a feature flag.</summary>
    public void DisableFeature(string feature) => FeatureFlags[feature] = false;
}

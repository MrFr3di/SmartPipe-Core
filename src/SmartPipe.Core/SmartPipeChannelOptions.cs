#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>Configuration for <see cref="SmartPipeChannel{TInput, TOutput}"/> pipeline engine.</summary>
/// <remarks>All channels must use <see cref="BoundedCapacity"/> per global constraints.</remarks>
public class SmartPipeChannelOptions
{
    /// <summary>Maximum number of parallel consumers. Defaults to processor count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>Maximum capacity for all channels. Mandatory per global constraints.</summary>
    public int BoundedCapacity { get; set; } = 1000;

    /// <summary>Whether to continue processing on non-fatal errors.</summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>Total timeout for the entire pipeline run.</summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Timeout for a single transform attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Use rendezvous channel (capacity 0) for minimal latency.</summary>
    public bool UseRendezvous { get; set; }

    /// <summary>Full mode for bounded channels when capacity is reached.</summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>Callback for real-time metrics updates.</summary>
    public Action<SmartPipeMetrics>? OnMetrics { get; set; }

    /// <summary>When true, AddSource/AddTransformer/AddSink throw after pipeline start. Default false for backward compatibility.</summary>
    public bool ThrowOnMutationAfterStart { get; set; } = false;

    /// <summary>Optional deduplication filter for input items.</summary>
    public DeduplicationFilter? DeduplicationFilter { get; set; }

    /// <summary>Progress reporting delegate. Called after each item.</summary>
    public Action<int, int?, TimeSpan, TimeSpan?>? OnProgress { get; set; }

    /// <summary>Optional DeadLetter sink for exhausted retries and permanent errors.</summary>
    public ISink<object>? DeadLetterSink { get; set; }

    /// <summary>Default retry policy for transient failures. If null, fallback to 3 retries with 1s delay.</summary>
    public RetryPolicy? DefaultRetryPolicy { get; set; }

    /// <summary>
    /// Controls how the legacy retry queue behaves when its bounded capacity is reached.
    /// Defaults to <see cref="RetryQueueOverflowPolicy.Wait"/> for safe backpressure.
    /// </summary>
    public RetryQueueOverflowPolicy RetryQueueOverflowPolicy { get; set; } =
        RetryQueueOverflowPolicy.Wait;

    /// <summary>Feature flags for optional pipeline components.</summary>
    public Dictionary<string, bool> FeatureFlags { get; } =
        new()
        {
            ["RetryQueue"] = false,
            ["Metrics"] = true,
            ["CircuitBreaker"] = false,
            ["ObjectPool"] = false,
            ["DebugSampling"] = false,
            ["CuckooFilter"] = false,
            ["JumpHash"] = false,
            ["SecretScanner"] = false, // off by default
        };

    /// <summary>Check if a feature flag is enabled.</summary>
    /// <param name="f">Feature flag name.</param>
    /// <returns>True if feature is enabled.</returns>
    public bool IsEnabled(string f) => FeatureFlags.TryGetValue(f, out var e) && e;

    /// <summary>Enable a feature flag.</summary>
    /// <param name="f">Feature flag name.</param>
    public void EnableFeature(string f) => FeatureFlags[f] = true;

    /// <summary>Disable a feature flag.</summary>
    /// <param name="f">Feature flag name.</param>
    public void DisableFeature(string f) => FeatureFlags[f] = false;

    internal void Validate()
    {
        if (UseRendezvous)
        {
            throw new InvalidOperationException(
                "UseRendezvous=true sets capacity to 0, which violates the global constraint "
                    + "\"BoundedCapacity set for all channels\". Disable UseRendezvous or remove this constraint."
            );
        }

        if (MaxDegreeOfParallelism <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxDegreeOfParallelism),
                MaxDegreeOfParallelism,
                "MaxDegreeOfParallelism must be greater than zero."
            );

        if (BoundedCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(BoundedCapacity),
                BoundedCapacity,
                "BoundedCapacity must be greater than zero."
            );

        if (AttemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(AttemptTimeout),
                AttemptTimeout,
                "AttemptTimeout must be greater than zero."
            );

        if (TotalRequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(TotalRequestTimeout),
                TotalRequestTimeout,
                "TotalRequestTimeout must be greater than zero."
            );

        if (!Enum.IsDefined(FullMode))
            throw new ArgumentOutOfRangeException(
                nameof(FullMode),
                FullMode,
                "FullMode is invalid."
            );

        if (!Enum.IsDefined(RetryQueueOverflowPolicy))
            throw new ArgumentOutOfRangeException(
                nameof(RetryQueueOverflowPolicy),
                RetryQueueOverflowPolicy,
                "RetryQueueOverflowPolicy is invalid."
            );
    }
}

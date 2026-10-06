#nullable enable

namespace SmartPipe.Core;

/// <summary>Provides immutable dependencies and identity for one pipeline activation.</summary>
/// <remarks>
/// Core does not own or dispose the supplied <see cref="Services"/> or
/// <see cref="TimeProvider"/> instances.
/// </remarks>
public sealed class PipelineActivationContext
{
    /// <summary>Creates an activation context for one pipeline run.</summary>
    /// <param name="pipelineKey">The pipeline definition key.</param>
    /// <param name="runId">The non-empty run identifier.</param>
    /// <param name="services">Optional external service provider. Core does not own or dispose it.</param>
    /// <param name="timeProvider">Optional external time provider. Core does not own or dispose it.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="pipelineKey"/> is uninitialized or <paramref name="runId"/> is empty.
    /// </exception>
    public PipelineActivationContext(
        PipelineKey pipelineKey,
        Guid runId,
        IServiceProvider? services = null,
        TimeProvider? timeProvider = null)
    {
        PipelineKeyGuard.ThrowIfInvalid(pipelineKey);
        if (runId == Guid.Empty)
            throw new ArgumentException("RunId must not be empty.", nameof(runId));

        PipelineKey = pipelineKey;
        RunId = runId;
        Services = services;
        HasExplicitTimeProvider = timeProvider is not null;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the exact pipeline definition key.</summary>
    public PipelineKey PipelineKey { get; }

    /// <summary>Gets the non-empty run identifier.</summary>
    public Guid RunId { get; }

    /// <summary>Gets the optional external service provider.</summary>
    /// <remarks>Core does not own or dispose this instance.</remarks>
    public IServiceProvider? Services { get; }

    /// <summary>Gets the external or system time provider for this activation.</summary>
    /// <remarks>Core does not own or dispose this instance.</remarks>
    public TimeProvider TimeProvider { get; }

    internal bool HasExplicitTimeProvider { get; }
}

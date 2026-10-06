namespace SmartPipe.Core;

/// <summary>Identifies who owns a component activated for a pipeline run.</summary>
public enum PipelineComponentOwnership
{
    /// <summary>The Core runtime owns and disposes the activated component.</summary>
    RuntimeOwned = 0,

    /// <summary>An external scope owns and disposes the activated component.</summary>
    ScopeOwned = 1,

    /// <summary>An external caller owns and disposes the component instance.</summary>
    ExternallyOwned = 2,
}

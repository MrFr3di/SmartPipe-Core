#nullable enable

namespace SmartPipe.Extensions.EntityFrameworkCore;

/// <summary>Selects the Entity Framework Core tracking behaviour applied to a queryable source.</summary>
/// <remarks>
/// <see cref="PreserveQuery"/> leaves the caller-created query unchanged; every other value applies the
/// matching Entity Framework Core tracking operator once, before the single enumeration of the run.
/// </remarks>
public enum EfCoreQueryTrackingMode
{
    /// <summary>Applies <c>AsNoTracking</c>, the default read-only behaviour.</summary>
    NoTracking = 0,

    /// <summary>Applies <c>AsNoTrackingWithIdentityResolution</c> so repeated entities share one instance.</summary>
    NoTrackingWithIdentityResolution = 1,

    /// <summary>Applies <c>AsTracking</c> so returned entities stay tracked by the run context.</summary>
    Tracking = 2,

    /// <summary>Applies no tracking operator and preserves the caller-created query exactly.</summary>
    PreserveQuery = 3,
}

/// <summary>Options for a queryable Entity Framework Core source.</summary>
public sealed record EfCoreQueryOptions
{
    /// <summary>Gets the operation name used for logs and diagnostics.</summary>
    /// <remarks>Must be non-empty, free of control characters, and at most 64 characters long.</remarks>
    public string OperationName { get; init; } = "query";

    /// <summary>Gets the tracking behaviour applied to the caller-created query.</summary>
    public EfCoreQueryTrackingMode TrackingMode { get; init; } = EfCoreQueryTrackingMode.NoTracking;
}

/// <summary>Options for a compiled or caller-provided async-sequence Entity Framework Core source.</summary>
/// <remarks>
/// There is deliberately no tracking option: the compiled delegate already fixes the query shape, so a
/// tracking setting could not affect the query.
/// </remarks>
public sealed record EfCoreCompiledQueryOptions
{
    /// <summary>Gets the operation name used for logs and diagnostics.</summary>
    /// <remarks>Must be non-empty, free of control characters, and at most 64 characters long.</remarks>
    public string OperationName { get; init; } = "compiled-query";
}

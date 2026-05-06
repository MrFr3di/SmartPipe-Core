#nullable enable

namespace SmartPipe.Core;

/// <summary>
/// Provides access to the current UTC time.
/// Enables testability by allowing time to be mocked in unit tests.
/// </summary>
public interface IClock
{
    /// <summary>Gets the current UTC date and time.</summary>
    DateTime UtcNow { get; }
}

/// <summary>
/// Clock implementation using System.TimeProvider (available in .NET 8+).
/// </summary>
public sealed class TimeProviderClock : IClock
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a new TimeProviderClock using the specified TimeProvider.</summary>
    /// <param name="timeProvider">The TimeProvider to use (defaults to TimeProvider.System).</param>
    public TimeProviderClock(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the current UTC date and time using the configured TimeProvider.
    /// </summary>
    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;
}



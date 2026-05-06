#nullable enable

namespace SmartPipe.Core;

/// <summary>Error classification for retry strategy.</summary>
public enum ErrorType
{
    /// <summary>Temporary error: can be retried safely.</summary>
    Transient,

    /// <summary>Permanent error: retry is useless, requires intervention.</summary>
    Permanent
}

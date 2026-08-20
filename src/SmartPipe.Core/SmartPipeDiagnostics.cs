#nullable enable

namespace SmartPipe.Core;

/// <summary>Stable diagnostic source names published by the SmartPipe runtime.</summary>
public static class SmartPipeDiagnostics
{
    /// <summary>Canonical meter name used by SmartPipe runtime metrics.</summary>
    public const string MeterName = "SmartPipe.Core";

    /// <summary>Canonical activity source name used by SmartPipe runtime tracing.</summary>
    public const string ActivitySourceName = "SmartPipe.Core";
}

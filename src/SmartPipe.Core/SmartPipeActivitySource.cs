#nullable enable

using System.Diagnostics;

namespace SmartPipe.Core;

internal static class SmartPipeActivitySource
{
    public const string Name = SmartPipeDiagnostics.ActivitySourceName;

    public static readonly ActivitySource Source = new(
        Name,
        typeof(SmartPipeActivitySource).Assembly.GetName().Version?.ToString() ?? "1.0.0");
}

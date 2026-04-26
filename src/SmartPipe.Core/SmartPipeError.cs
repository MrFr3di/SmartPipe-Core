namespace SmartPipe.Core;

/// <summary>Structured error for Partial Success pattern. Contains classification and optional exception.</summary>
/// <param name="Message">Human-readable error description.</param>
/// <param name="Type">Error classification: Transient or Permanent.</param>
/// <param name="Category">Optional category for metrics (e.g., "Network", "IO", "Validation").</param>
/// <param name="InnerException">Optional original exception.</param>
public readonly record struct SmartPipeError(
    string Message,
    ErrorType Type,
    string? Category = null,
    Exception? InnerException = null)
{
    /// <summary>Human-readable error representation.</summary>
    public override string ToString() =>
        $"Type: {Type}, Category: {Category ?? "N/A"}, Message: {Message}";
}

#nullable enable

namespace SmartPipe.Core;

internal sealed class StageAttemptAdmission(RetryBudgetLease? retry) : IDisposable
{
    public void MarkExecutionStarted() => retry?.Commit();
    public void Dispose() => retry?.Dispose();
}

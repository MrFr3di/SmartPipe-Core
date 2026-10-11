#nullable enable

namespace SmartPipe.Core;

internal sealed class StageAttemptAdmission(RetryBudgetLease? retry) : IDisposable
{
    private TimedAttemptLease? _timed;
    public void SetTimedLease(TimedAttemptLease? lease) => _timed = lease;
    public TimedAttemptLease? TransferTimedLease() => Interlocked.Exchange(ref _timed, null);
    public void MarkExecutionStarted() => retry?.Commit();
    public void Dispose()
    {
        retry?.Dispose();
        TransferTimedLease()?.Dispose();
    }
}

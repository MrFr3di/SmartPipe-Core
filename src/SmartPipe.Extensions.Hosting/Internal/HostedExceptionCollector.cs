using System.Runtime.ExceptionServices;

namespace SmartPipe.Extensions.Hosting;

internal sealed class HostedExceptionCollector
{
    private readonly List<ExceptionDispatchInfo> _errors = [];

    internal bool HasErrors => _errors.Count != 0;

    internal void Capture(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _errors.Add(ExceptionDispatchInfo.Capture(error));
    }

    internal void ThrowIfAny()
    {
        if (_errors.Count == 0)
            return;

        if (_errors.Count == 1)
            _errors[0].Throw();

        throw new AggregateException(_errors.Select(static error => error.SourceException));
    }
}

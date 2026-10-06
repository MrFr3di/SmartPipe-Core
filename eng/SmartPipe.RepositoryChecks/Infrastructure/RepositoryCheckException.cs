namespace SmartPipe.RepositoryChecks.Infrastructure;

internal sealed class RepositoryCheckException : Exception
{
    public RepositoryCheckException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public RepositoryCheckException(int exitCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

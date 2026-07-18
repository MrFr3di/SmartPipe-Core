namespace SmartPipe.RepositoryChecks.Infrastructure;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int UsageOrConfigurationError = 2;
    public const int ExternalSourceUnavailable = 3;
    public const int IntegrityOrSignatureFailure = 4;
    public const int RepositorySnapshotMismatch = 5;
    public const int UnexpectedInternalFailure = 10;
}

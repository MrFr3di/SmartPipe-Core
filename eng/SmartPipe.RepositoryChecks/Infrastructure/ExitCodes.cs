namespace SmartPipe.RepositoryChecks.Infrastructure;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int UsageOrConfigurationError = 2;
    public const int ExternalSourceUnavailable = 3;
    public const int IntegrityOrSignatureFailure = 4;
    public const int RepositorySnapshotMismatch = 5;
    public const int CentralPackagePolicyViolation = 22;
    public const int PackageProjectViolation = 23;
    public const int SchemaOrManifestInvalid = 21;
    public const int PackedPackageViolation = 24;
    public const int OwnershipViolation = 25;
    public const int ReleaseVersionMismatch = 26;
    public const int ScaffoldCollisionOrRefusedOverwrite = 28;
    public const int ConsumerScenarioFailure = 29;
    public const int PackagePackFailure = 30;
    public const int UnexpectedInternalFailure = 10;
}

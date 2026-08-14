namespace SmartPipe.RepositoryChecks.Commands;

internal static class BaselineCommandOrchestrator
{
    public static async Task<BaselineVerificationResult> VerifyAsync(
        VerifyBaselineOptions options,
        Func<ProvisionBaselineOptions, CancellationToken, Task> provision,
        Func<VerifyBaselineOptions, CancellationToken, Task<BaselineVerificationResult>> verify,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provision);
        ArgumentNullException.ThrowIfNull(verify);

        if (!options.Offline)
        {
            await provision(
                new ProvisionBaselineOptions(options.RepositoryRoot, options.ManifestPath, options.PackagesDirectory),
                cancellationToken).ConfigureAwait(false);
        }

        return await verify(options with { Offline = true }, cancellationToken).ConfigureAwait(false);
    }
}

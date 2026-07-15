namespace SmartPipe.RepositoryChecks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0
            && string.Equals(args[0], Infrastructure.RepositoryCheckProcessHost.DispatchArgument, StringComparison.Ordinal))
        {
            return await Infrastructure.RepositoryCheckProcessHost
                .RunAsync(args.AsMemory(1).ToArray())
                .ConfigureAwait(false);
        }

        return 0;
    }
}

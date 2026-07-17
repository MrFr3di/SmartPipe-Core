using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;

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

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var command = CommandLineParser.Parse(args);
            var runner = new ProcessRunner();
            using var httpClient = new HttpClient();
            var packageReader = new NuGetPackageReader();
            var signatureVerifier = new NuGetPackageSignatureVerifier(runner, "dotnet");
            var repositoryReader = new BaselineRepositorySnapshotReader(runner, "dotnet");
            var verification = new BaselineVerificationService(
                runner, "git", signatureVerifier, packageReader, repositoryReader);

            switch (command)
            {
                case CaptureBaselineOptions capture:
                    var fetcher = new NuGetPackageFetcher(httpClient, new NuGetServiceIndexClient(httpClient));
                    await new BaselineCaptureService(
                        runner, "git", "dotnet", fetcher, signatureVerifier, packageReader,
                        repositoryReader, verification).CaptureAsync(capture, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("BASELINE CAPTURED");
                    return ExitCodes.Success;

                case VerifyBaselineOptions verify:
                    var result = await verification.VerifyAsync(verify, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine(result.Format());
                    return result.Success ? ExitCodes.Success : ExitCodes.RepositorySnapshotMismatch;

                default:
                    throw new InvalidOperationException("Unsupported command type.");
            }
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return ExitCodes.UsageOrConfigurationError;
        }
        catch (RepositoryCheckException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation canceled.");
            return ExitCodes.UsageOrConfigurationError;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unexpected failure: {exception.Message}");
            return ExitCodes.UnexpectedInternalFailure;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}

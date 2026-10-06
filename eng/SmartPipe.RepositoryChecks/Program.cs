using SmartPipe.RepositoryChecks.Agent;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Release;
using SmartPipe.RepositoryChecks.Scaffolding;

namespace SmartPipe.RepositoryChecks;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
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
            return await RepositoryCommandExecutor
                .ExecuteAsync(command, runner, cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (CommandLineException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return ExitCodes.UsageOrConfigurationError;
        }
        catch (AgentPlanException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return ExitCodes.SchemaOrManifestInvalid;
        }
        catch (RepositoryCheckException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return exception.ExitCode;
        }
        catch (PackageGraphException exception)
        {
            await Console.Error.WriteLineAsync($"[{exception.Code}] {exception.Message}");
            return ExitCodes.SchemaOrManifestInvalid;
        }
        catch (ReleaseNotesException exception)
        {
            await Console.Error.WriteLineAsync($"[{exception.Code}] {exception.Message}");
            return ExitCodes.SchemaOrManifestInvalid;
        }
        catch (ScaffoldException exception)
        {
            await Console.Error.WriteLineAsync($"[{exception.Code}] {exception.Message}");
            return ExitCodes.ScaffoldCollisionOrRefusedOverwrite;
        }
        catch (ConsumerScenarioException exception)
        {
            await Console.Error.WriteLineAsync($"[{exception.Code}] {exception.Message}");
            return ExitCodes.ConsumerScenarioFailure;
        }
        catch (PackagePackException exception)
        {
            await Console.Error.WriteLineAsync($"[{exception.Code}] {exception.Message}");
            return ExitCodes.PackagePackFailure;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Operation canceled.");
            return ExitCodes.UsageOrConfigurationError;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Unexpected failure: {exception.Message}");
            return ExitCodes.UnexpectedInternalFailure;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

}

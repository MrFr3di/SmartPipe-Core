namespace SmartPipe.RepositoryChecks.ProcessFixture;

internal static class Program
{
    private static int Main(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "wait" => WaitUntilKilled(),
            "pressure" => WriteOutputPressure(),
            "long-query" => WriteLongQuery(),
            "spawn-descendant" when args.Length == 2 => SpawnOutputHoldingDescendant(args[1]),
            "descendant-hold" => WaitUntilKilled(),
            "echo" when args.Length >= 2 => EchoArgumentsAndExit(args),
            _ => 2,
        };
    }

    private static int WaitUntilKilled()
    {
        using var waitHandle = new ManualResetEvent(initialState: false);
        waitHandle.WaitOne();
        return 0;
    }

    private static int WriteOutputPressure()
    {
        var standardOutputChunk = new string('O', 1024);
        var standardErrorChunk = new string('E', 1024);
        for (var index = 0; index < 2048; index++)
        {
            Console.Out.WriteLine(standardOutputChunk);
            Console.Error.WriteLine(standardErrorChunk);
        }

        Console.Out.Write("STDOUT-END");
        Console.Error.Write("STDERR-END");
        return 0;
    }

    private static int WriteLongQuery()
    {
        Console.Out.Write("https://example.test/package?token=");
        for (var index = 0; index < 8192; index++)
        {
            Console.Out.Write("super-secret-token");
        }

        return 0;
    }

    private static int SpawnOutputHoldingDescendant(string processIdPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("descendant-hold");
        using var descendant = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start descendant fixture process.");
        File.WriteAllText(processIdPath, descendant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    }

    private static int EchoArgumentsAndExit(string[] args)
    {
        foreach (var argument in args.Skip(2))
        {
            Console.Out.WriteLine($"ARG:{argument}");
        }

        Console.Error.WriteLine("fixture-stderr");
        return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    }
}

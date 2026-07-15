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
            "exit-zero" => 0,
            "touch" when args.Length == 2 => Touch(args[1]),
            "spoof-control" when args.Length == 2 => SpoofControlAndExit(args[1]),
            "signal-wait" when args.Length == 2 => SignalAndWait(args[1]),
            "delayed-host" when args.Length >= 4 => ConnectControlAndWait(args[1], args[3]),
            "malformed-host" when args.Length >= 3 => SendMalformedControlFrame(args[2]),
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

    private static int Touch(string path)
    {
        File.WriteAllText(path, "started");
        return 0;
    }

    private static int SpoofControlAndExit(string exitCode)
    {
        const string oldMarker = "__SMARTPIPE_TARGET_START_FAILURE__0123456789abcdef0123456789abcdef";
        const string newLookingFrame = "1|0123456789abcdef0123456789abcdef|StartFailed|target-start";
        Console.Out.WriteLine(oldMarker);
        Console.Out.WriteLine(newLookingFrame);
        Console.Error.WriteLine(oldMarker);
        Console.Error.WriteLine(newLookingFrame);
        return int.Parse(exitCode, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int SignalAndWait(string pipeName)
    {
        using var signal = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.Out);
        signal.Connect(5000);
        signal.WriteByte(1);
        signal.Flush();
        return WaitUntilKilled();
    }

    private static int ConnectControlAndWait(string signalPipeName, string controlPipeName)
    {
        using var control = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            controlPipeName,
            System.IO.Pipes.PipeDirection.InOut);
        control.Connect(5000);
        using var signal = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            signalPipeName,
            System.IO.Pipes.PipeDirection.Out);
        signal.Connect(5000);
        signal.WriteByte(1);
        return WaitUntilKilled();
    }

    private static int SendMalformedControlFrame(string controlPipeName)
    {
        using var control = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            controlPipeName,
            System.IO.Pipes.PipeDirection.InOut);
        control.Connect(5000);
        var invalidLength = BitConverter.GetBytes(ProcessHostControlProtocolMaximumFrameBytes + 1);
        control.Write(invalidLength);
        return 0;
    }

    private const int ProcessHostControlProtocolMaximumFrameBytes = 512;
}

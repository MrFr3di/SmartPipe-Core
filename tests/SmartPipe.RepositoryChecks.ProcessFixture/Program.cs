namespace SmartPipe.RepositoryChecks.ProcessFixture;

internal static class Program
{
    private static int Main(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "wait" => WaitUntilKilled(),
            "pressure" => WriteOutputPressure(),
            "spill-pressure" => WriteSpillPressure(),
            "long-query" => WriteLongQuery(),
            "spawn-descendant" when args.Length == 2 => SpawnOutputHoldingDescendant(args[1]),
            "spawn-descendant-signal" when args.Length == 3 => SpawnDescendantSignalAndWait(args[1], args[2]),
            "descendant-hold" => WaitUntilKilled(),
            "echo" when args.Length >= 2 => EchoArgumentsAndExit(args),
            "exit-zero" => 0,
            "touch" when args.Length == 2 => Touch(args[1]),
            "spoof-control" when args.Length == 2 => SpoofControlAndExit(args[1]),
            "signal-wait" when args.Length == 2 => SignalAndWait(args[1]),
            "delayed-host" when args.Length >= 4 => ConnectControlAndWait(args[1], args[3]),
            "malformed-host" when args.Length >= 3 => SendMalformedControlFrame(args[2]),
            "spawn-detached-descendant" when args.Length == 3 => SpawnDetachedDescendant(args[1], args[2]),
            "post-start-malformed-host" when args.Length >= 5 => RunPostStartMalformedHost(args[1], args[3], args[4]),
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

    private static int WriteSpillPressure()
    {
        var chunk = new string('S', 1024);
        for (var index = 0; index < 6144; index++) Console.Out.WriteLine(chunk);
        Console.Out.Write("SPILL-END");
        return 0;
    }

    private static int SpawnDescendantSignalAndWait(string processIdPath, string signalPipeName)
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

        using var signal = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            signalPipeName,
            System.IO.Pipes.PipeDirection.Out);
        signal.Connect(5000);
        signal.WriteByte(1);
        signal.Flush();
        return WaitUntilKilled();
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

    private static int SpawnDetachedDescendant(string processIdPath, string exitCode)
    {
        MakeTargetOutputNonInheritable();
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("descendant-hold");
        using var descendant = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start detached descendant fixture process.");
        File.WriteAllText(processIdPath, descendant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return int.Parse(exitCode, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int RunPostStartMalformedHost(
        string processIdPath,
        string controlPipeName,
        string nonce)
    {
        using var control = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            controlPipeName,
            System.IO.Pipes.PipeDirection.InOut);
        control.Connect(5000);
        WriteControlFrame(control, nonce, "Ready", string.Empty);
        ReadControlFrame(control);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("descendant-hold");
        using var descendant = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start malformed-host descendant.");
        File.WriteAllText(processIdPath, descendant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteControlFrame(control, nonce, "Started", string.Empty);
        control.Write(BitConverter.GetBytes(ProcessHostControlProtocolMaximumFrameBytes + 1));
        return WaitUntilKilled();
    }

    private static void WriteControlFrame(
        Stream control,
        string nonce,
        string kind,
        string detail)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes($"1|{nonce}|{kind}|{detail}");
        control.Write(BitConverter.GetBytes(payload.Length));
        control.Write(payload);
    }

    private static void ReadControlFrame(Stream control)
    {
        var header = new byte[sizeof(int)];
        control.ReadExactly(header);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > ProcessHostControlProtocolMaximumFrameBytes)
        {
            throw new InvalidOperationException("Invalid controller frame length.");
        }

        control.ReadExactly(new byte[length]);
    }

    private const int ProcessHostControlProtocolMaximumFrameBytes = 512;

    private static void MakeTargetOutputNonInheritable()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!SetHandleInformation(GetStdHandle(-11), 1, 0)
                || !SetHandleInformation(GetStdHandle(-12), 1, 0))
            {
                throw new System.ComponentModel.Win32Exception();
            }
        }
        else
        {
            const int setDescriptorFlags = 2;
            const int closeOnExec = 1;
            if (SetFileDescriptorFlags(1, setDescriptorFlags, closeOnExec) != 0
                || SetFileDescriptorFlags(2, setDescriptorFlags, closeOnExec) != 0)
            {
                throw new System.ComponentModel.Win32Exception();
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int SetFileDescriptorFlags(int descriptor, int command, int flags);
}

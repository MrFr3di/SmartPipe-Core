namespace SmartPipe.RepositoryChecks.ProcessFixture;

internal static class Program
{
    private static int Main(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "wait" => WaitUntilKilled(),
            "pressure" => WriteOutputPressure(),
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
            Console.Out.Write(standardOutputChunk);
            Console.Error.Write(standardErrorChunk);
        }

        Console.Out.Write("STDOUT-END");
        Console.Error.Write("STDERR-END");
        return 0;
    }
}

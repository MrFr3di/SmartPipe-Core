using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;
using SmartPipe.Extensions.Dapper;

internal static class Program
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "This diagnostic consumer documents the Dapper reflection boundary.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "This diagnostic consumer documents the Dapper dynamic-code boundary.")]
    private static int Main()
    {
        _ = DapperPipelineDefinitionBuilder.FromQuery<Row>(
            new PipelineKey("dapper-trim-diagnostic"),
            (context, cancellationToken) => throw new InvalidOperationException("This diagnostic consumer only composes a descriptor and never opens a connection."),
            "SELECT Id, Name FROM \"In\"",
            new DapperQueryOptions { OperationName = "dapper-trim-diagnostic" },
            rowMapper: reader => new Row(reader.GetInt32(0), reader.GetString(1)));
        Console.WriteLine("CONSUMER_OK dapper-trim-diagnostic");
        return 0;
    }

    private sealed record Row(int Id, string Name);
}

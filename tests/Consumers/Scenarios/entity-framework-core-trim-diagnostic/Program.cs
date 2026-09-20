using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore;

internal static class Program
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "This diagnostic consumer documents the Entity Framework Core query-composition boundary.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "This diagnostic consumer documents the Entity Framework Core dynamic-code boundary.")]
    private static int Main()
    {
        _ = EfCorePipelineDefinitionBuilder.FromQuery<TrimContext, Row>(
            new PipelineKey("entity-framework-core-trim-diagnostic"),
            (context, cancellationToken) => throw new InvalidOperationException("This diagnostic consumer only composes a descriptor and never creates a context."),
            static (context, activation) => context.Rows,
            new EfCoreQueryOptions { OperationName = "entity-framework-core-trim-diagnostic" });
        Console.WriteLine("CONSUMER_OK entity-framework-core-trim-diagnostic");
        return 0;
    }

    private sealed class TrimContext : DbContext
    {
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "This diagnostic consumer documents the Entity Framework Core context-construction boundary; the factory-based sources are the supported alternative for trimmed consumers.")]
        public TrimContext()
        {
        }

        public DbSet<Row> Rows => Set<Row>();
    }

    private sealed class Row
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}

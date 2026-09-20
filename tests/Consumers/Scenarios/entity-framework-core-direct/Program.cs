using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartPipe.ConsumerScenarios;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore;

var databasePath = Path.Combine(Path.GetTempPath(), $"smartpipe-efcore-direct-{Guid.NewGuid():N}.db");
var connectionString = $"Data Source={databasePath};Pooling=False";
try
{
    await using (var seed = new SqliteConnection(connectionString))
    await using (var command = seed.CreateCommand())
    {
        await seed.OpenAsync();
        command.CommandText = """
            CREATE TABLE "In" (Id INTEGER NOT NULL, Name TEXT NOT NULL);
            INSERT INTO "In" (Id, Name) VALUES (1, 'Ada');
            INSERT INTO "In" (Id, Name) VALUES (2, 'Grace');
            """;
        await command.ExecuteNonQueryAsync();
    }

    var options = new DbContextOptionsBuilder<DirectContext>().UseSqlite(connectionString).Options;

    var queryDefinition = EfCorePipelineDefinitionBuilder
        .FromQuery<DirectContext, Row>(
            new PipelineKey("entity-framework-core-direct"),
            (context, cancellationToken) => ValueTask.FromResult(new DirectContext(options)),
            static (context, activation) => context.Rows.OrderBy(row => row.Id),
            new EfCoreQueryOptions { OperationName = "entity-framework-core-direct" })
        .Build();
    await using (var run = await queryDefinition.StartAsync())
    {
        var first = await run.Outputs.ReadAsync();
        var second = await run.Outputs.ReadAsync();
        await run.Completion;
        var firstName = first.Result.Value?.Name;
        var secondName = second.Result.Value?.Name;
        if (firstName != "Ada" || secondName != "Grace")
            throw new InvalidOperationException($"Unexpected queryable results: '{firstName}', '{secondName}'.");
    }

    var compiledDefinition = EfCorePipelineDefinitionBuilder
        .FromCompiledQuery<DirectContext, int>(
            new PipelineKey("entity-framework-core-direct-compiled"),
            (context, cancellationToken) => ValueTask.FromResult(new DirectContext(options)),
            static (context, activation, cancellationToken) => CountRowsAsync(context, cancellationToken),
            new EfCoreCompiledQueryOptions { OperationName = "entity-framework-core-direct-compiled" })
        .Build();
    await using (var run = await compiledDefinition.StartAsync())
    {
        var count = await run.Outputs.ReadAsync();
        await run.Completion;
        if (count.Result.Value != 2)
            throw new InvalidOperationException($"Unexpected compiled count: {count.Result.Value}.");
    }
}
finally
{
    File.Delete(databasePath);
}

Console.WriteLine("CONSUMER_OK entity-framework-core-direct");
return 0;

static async IAsyncEnumerable<int> CountRowsAsync(
    DirectContext context,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    yield return await context.Rows.CountAsync(cancellationToken);
}

namespace SmartPipe.ConsumerScenarios
{
    internal sealed class DirectContext(DbContextOptions<DirectContext> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Row>().ToTable("In");
    }

    internal sealed class Row
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}

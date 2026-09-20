using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.EntityFrameworkCore;

var databasePath = Path.Combine(Path.GetTempPath(), $"smartpipe-efcore-di-{Guid.NewGuid():N}.db");
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
            """;
        await command.ExecuteNonQueryAsync();
    }

    var options = new DbContextOptionsBuilder<DiContext>().UseSqlite(connectionString).Options;
    var key = new PipelineKey("entity-framework-core-di-composition");
    var definition = EfCorePipelineDefinitionBuilder
        .FromQuery<DiContext, Row>(
            key,
            (context, cancellationToken) => ValueTask.FromResult(new DiContext(options)),
            static (context, activation) => context.Rows.OrderBy(row => row.Id),
            new EfCoreQueryOptions { OperationName = "entity-framework-core-di-composition" })
        .Build();
    var services = new ServiceCollection();
    services.AddSmartPipe().AddPipeline(definition);
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    var factory = provider.GetRequiredService<ISmartPipeFactoryProvider>().GetFactory<Row, Row>(key);
    await using (var run = await factory.StartAsync())
    {
        var result = await run.Outputs.ReadAsync();
        await run.Completion;
        if (!result.Result.IsSuccess || result.Result.Value?.Name != "Ada")
            return 1;
    }
}
finally
{
    File.Delete(databasePath);
}

Console.WriteLine("CONSUMER_OK entity-framework-core-di-composition");
return 0;

internal sealed class DiContext(DbContextOptions<DiContext> options) : DbContext(options)
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

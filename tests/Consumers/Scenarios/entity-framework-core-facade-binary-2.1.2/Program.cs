using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartPipe.Extensions.Selectors;

var databasePath = Path.Combine(Path.GetTempPath(), $"smartpipe-efcore-facade-binary-{Guid.NewGuid():N}.db");
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

    var options = new DbContextOptionsBuilder<FacadeContext>().UseSqlite(connectionString).Options;
    await using var context = new FacadeContext(options);
    await using var selector = new EfCoreSelector<Row>(context).WithQuery(static set => set.OrderBy(row => row.Id));
    await selector.InitializeAsync();
    var names = new List<string>();
    await foreach (var envelope in selector.ReadEnvelopesAsync())
        names.Add(envelope.Payload.Name);
    if (names.Count != 2 || names[0] != "Ada" || names[1] != "Grace")
        return 1;
}
finally
{
    File.Delete(databasePath);
}

Console.WriteLine("CONSUMER_OK entity-framework-core-facade-binary-2.1.2");
return 0;

internal sealed class FacadeContext(DbContextOptions<FacadeContext> options) : DbContext(options)
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

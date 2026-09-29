using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartPipe.ConsumerScenarios;
using SmartPipe.Extensions.Selectors;

var databasePath = Path.Combine(Path.GetTempPath(), $"smartpipe-efcore-binary-{Guid.NewGuid():N}.db");
var connectionString = $"Data Source={databasePath};Pooling=False";
try
{
    await using (var seed = new SqliteConnection(connectionString))
    await using (var command = seed.CreateCommand())
    {
        await seed.OpenAsync();
        command.CommandText = """
            CREATE TABLE "Tickets" (TicketId INTEGER NOT NULL PRIMARY KEY, Title TEXT NOT NULL);
            INSERT INTO "Tickets" (TicketId, Title) VALUES (10, 'first');
            INSERT INTO "Tickets" (TicketId, Title) VALUES (11, 'second');
            INSERT INTO "Tickets" (TicketId, Title) VALUES (12, 'third');
            """;
        await command.ExecuteNonQueryAsync();
    }

    var options = new DbContextOptionsBuilder<TicketContext>().UseSqlite(connectionString).Options;
    await using var context = new TicketContext(options);
    await using var selector = new EfCoreSelector<Ticket>(context)
        .WithQuery(static set => set.Where(ticket => ticket.TicketId >= 10))
        .WithTracking();
    await selector.InitializeAsync();
    var titles = new List<string>();
    var trackedDuringEnumeration = 0;
    await foreach (var envelope in selector.ReadEnvelopesAsync())
    {
        titles.Add(envelope.Payload.Title);
        trackedDuringEnumeration = Math.Max(trackedDuringEnumeration, context.ChangeTracker.Entries().Count());
    }

    if (titles.Count != 3 || titles[0] != "first" || titles[2] != "third")
        throw new InvalidOperationException($"The forwarded legacy selector returned unexpected rows: {string.Join(',', titles)}.");
    if (trackedDuringEnumeration != 3)
        throw new InvalidOperationException($"The forwarded legacy selector did not track the returned entities: {trackedDuringEnumeration}.");
}
finally
{
    File.Delete(databasePath);
}

Console.WriteLine("CONSUMER_OK entity-framework-core-facade-binary-2.1.2");
return 0;

namespace SmartPipe.ConsumerScenarios
{
    internal sealed class TicketContext(DbContextOptions<TicketContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ticket>().ToTable("Tickets");
    }

    internal sealed class Ticket
    {
        public int TicketId { get; set; }

        public string Title { get; set; } = string.Empty;
    }
}

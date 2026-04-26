using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;

namespace SmartPipe.Extensions.Tests;

public class EfCoreSelectorTests
{
    private class TestEntity { public int Id { get; set; } public string Name { get; set; } = ""; }
    private class TestDbContext : DbContext
    {
        public DbSet<TestEntity> TestEntities { get; set; } = null!;
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseInMemoryDatabase(Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task ReadAsync_WithEntities_ShouldStreamAll()
    {
        await using var db = new TestDbContext();
        db.TestEntities.AddRange(
            new TestEntity { Id = 1, Name = "One" },
            new TestEntity { Id = 2, Name = "Two" });
        await db.SaveChangesAsync();

        var selector = new EfCoreSelector<TestEntity>(db);
        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadAsync_EmptyTable_ShouldReturnEmpty()
    {
        await using var db = new TestDbContext();
        await db.Database.EnsureCreatedAsync();
        var selector = new EfCoreSelector<TestEntity>(db);
        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        results.Should().BeEmpty();
    }
}

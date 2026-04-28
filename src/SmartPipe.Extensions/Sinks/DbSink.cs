using System.Reflection;
using System.Data;
using Dapper;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes items to database using Dapper. Supports auto-generated SQL from [Table]/[Column] attributes.</summary>
public class DbSink<T> : ISink<T>
{
    private readonly IDbConnection _connection;
    private readonly string _sql;

    public DbSink(IDbConnection connection, string? sql = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _sql = sql ?? GenerateInsertSql();
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        _connection.Open();
        return Task.CompletedTask;
    }

    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
            _connection.Execute(_sql, result.Value);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _connection.Close();
        return Task.CompletedTask;
    }

    private static string GenerateInsertSql()
    {
        var type = typeof(T);
        var tableAttr = type.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.TableAttribute>();
        var tableName = tableAttr?.Name ?? type.Name;
        var props = type.GetProperties().Where(p => p.CanRead).ToList();
        var columns = string.Join(", ", props.Select(p =>
        {
            var col = p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>();
            return col?.Name ?? p.Name;
        }));
        var values = string.Join(", ", props.Select(p => $"@{p.Name}"));
        return $"INSERT INTO {tableName} ({columns}) VALUES ({values})";
    }
}

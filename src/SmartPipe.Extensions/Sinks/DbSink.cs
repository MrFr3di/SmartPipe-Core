using System.Reflection;
using System.Data;
using Dapper;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes items to database using Dapper. Supports auto-generated SQL from [Table]/[Column] attributes.</summary>
/// <typeparam name="T">Entity type.</typeparam>
public class DbSink<T> : ISink<T>
{
    private readonly IDbConnection _connection;
    private readonly string _sql;

    /// <summary>Create DB sink with optional SQL override.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="sql">Optional INSERT SQL (auto-generated if null).</param>
    public DbSink(IDbConnection connection, string? sql = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _sql = sql ?? GenerateInsertSql();
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        _connection.Open();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
            _connection.Execute(_sql, result.Value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
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

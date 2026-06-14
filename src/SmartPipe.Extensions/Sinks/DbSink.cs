using System.Data;
using System.Reflection;
using Dapper;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes items to database using Dapper. Supports auto-generated SQL from [Table]/[Column] attributes.
/// Uses async I/O for non-blocking database writes.</summary>
/// <typeparam name="T">Entity type.</typeparam>
public class DbSink<T> : IPipelineSink<T>
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
    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        _connection.Open();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        if (envelope.Payload != null)
            await _connection.ExecuteAsync(_sql, envelope.Payload);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _connection.Close();
        return ValueTask.CompletedTask;
    }

    private static string GenerateInsertSql()
    {
        var type = typeof(T);
        var tableAttr =
            type.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.TableAttribute>();
        var tableName = tableAttr?.Name ?? type.Name;
        var props = type.GetProperties().Where(p => p.CanRead).ToList();
        var columns = string.Join(
            ", ",
            props.Select(p =>
            {
                var col =
                    p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>();
                return col?.Name ?? p.Name;
            })
        );
        var values = string.Join(", ", props.Select(p => $"@{p.Name}"));
        return $"INSERT INTO {tableName} ({columns}) VALUES ({values})";
    }
}

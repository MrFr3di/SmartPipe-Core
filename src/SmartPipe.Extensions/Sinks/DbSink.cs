using System.Data;
using System.Data.Common;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
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
    private readonly bool _leaveOpen;
    private bool _openedBySink;
    private int _disposed;

    /// <summary>Create DB sink with optional SQL override.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="sql">Optional INSERT SQL (auto-generated if null).</param>
    [SuppressMessage(
        "ApiDesign",
        "RS0027:Public API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "The shipped constructor is preserved for binary compatibility; explicit DbConnection ownership overloads do not use optional parameters."
    )]
    [RequiresUnreferencedCode("Reflection-based DbSink SQL generation is not trimming-safe. Provide explicit SQL for trimming and NativeAOT.")]
    public DbSink(IDbConnection connection, string? sql = null)
        : this(connection, sql, leaveOpen: false)
    {
    }

    /// <summary>Create DB sink with explicit connection ownership.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="leaveOpen">Whether to leave an already-open external connection open when disposing the sink.</param>
    [RequiresUnreferencedCode("Reflection-based DbSink SQL generation is not trimming-safe. Provide explicit SQL for trimming and NativeAOT.")]
    public DbSink(DbConnection connection, bool leaveOpen)
        : this((IDbConnection)connection, sql: null, leaveOpen)
    {
    }

    /// <summary>Create DB sink with explicit SQL and connection ownership.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="sql">INSERT SQL.</param>
    /// <param name="leaveOpen">Whether to leave an already-open external connection open when disposing the sink.</param>
    public DbSink(DbConnection connection, string sql, bool leaveOpen)
        : this((IDbConnection)connection, sql ?? throw new ArgumentNullException(nameof(sql)), leaveOpen)
    {
    }

    private DbSink(IDbConnection connection, string? sql, bool leaveOpen)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _sql = sql ?? GenerateInsertSql();
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_connection.State == ConnectionState.Open)
            return;

        if (_connection is DbConnection dbConnection)
            await dbConnection.OpenAsync(ct).ConfigureAwait(false);
        else
            _connection.Open();

        _openedBySink = true;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        if (envelope.Payload != null)
        {
            var command = new CommandDefinition(
                _sql,
                envelope.Payload,
                cancellationToken: ct);
            await _connection.ExecuteAsync(command).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        if (!_leaveOpen || _openedBySink)
            _connection.Close();

        return ValueTask.CompletedTask;
    }

    private static string GenerateInsertSql()
    {
        var type = typeof(T);
        var tableAttr = type.GetCustomAttribute<TableAttribute>();
        var tableName = tableAttr?.Name ?? type.Name;
        var props = type.GetProperties().Where(IsInsertableProperty).ToList();
        if (props.Count == 0)
        {
            throw new InvalidOperationException(
                $"No insertable properties were found on type '{type.FullName}'.");
        }

        var columns = string.Join(
            ", ",
            props.Select(p =>
            {
                var col = p.GetCustomAttribute<ColumnAttribute>();
                return col?.Name ?? p.Name;
            })
        );
        var values = string.Join(", ", props.Select(p => $"@{p.Name}"));
        return $"INSERT INTO {tableName} ({columns}) VALUES ({values})";
    }

    private static bool IsInsertableProperty(PropertyInfo property)
    {
        if (!property.CanRead || property.GetIndexParameters().Length != 0)
            return false;

        if (property.GetCustomAttribute<NotMappedAttribute>() is not null)
            return false;

        var generated = property.GetCustomAttribute<DatabaseGeneratedAttribute>();
        return generated is null || generated.DatabaseGeneratedOption == DatabaseGeneratedOption.None;
    }
}

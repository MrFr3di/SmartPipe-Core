using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>
/// High-performance Dapper SQL data source. Streams rows directly from database.
/// Supports parameterized queries and command timeout.
/// </summary>
/// <typeparam name="T">Row type.</typeparam>
public class DapperSelector<T> : ISource<T>, IDisposable
{
    private readonly IDbConnection _connection;
    private readonly string _sql;
    private readonly object? _parameters;
    private readonly int _commandTimeout;
    private readonly ILogger<DapperSelector<T>>? _logger;
    private IDataReader? _reader;

    /// <summary>Create Dapper source for given SQL query.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="sql">SQL query to execute.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="commandTimeout">Command timeout in seconds (default: 30).</param>
    /// <param name="logger">Optional logger.</param>
    public DapperSelector(
        IDbConnection connection,
        string sql,
        object? parameters = null,
        int commandTimeout = 30,
        ILogger<DapperSelector<T>>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _parameters = parameters;
        _commandTimeout = commandTimeout;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        _connection.Open();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _reader = await _connection.ExecuteReaderAsync(_sql, _parameters, commandTimeout: _commandTimeout);

        while (!_reader.IsClosed && _reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var row = MapRow(_reader);
            yield return new ProcessingContext<T>(row);
        }

        _logger?.LogInformation("Dapper source completed. SQL: {Sql}", _sql);
    }

    private static T MapRow(IDataRecord reader)
    {
        T instance = Activator.CreateInstance<T>();
        var properties = typeof(T).GetProperties();

        for (int i = 0; i < reader.FieldCount; i++)
        {
            var property = properties.FirstOrDefault(p =>
                p.Name.Equals(reader.GetName(i), StringComparison.OrdinalIgnoreCase)
                && p.CanWrite);

            if (property != null && !reader.IsDBNull(i))
                property.SetValue(instance, reader.GetValue(i));
        }

        return instance;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _reader?.Dispose();
        _connection?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Dispose connection and reader (synchronous).</summary>
    public void Dispose()
    {
        _reader?.Dispose();
        _connection?.Dispose();
    }
}

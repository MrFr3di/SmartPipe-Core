using System.Globalization;
using Npgsql;

namespace SmartPipe.Extensions.PostgreSql.Tests.Integration;

/// <summary>
/// Owns one private schema and the data sources of one integration test class on the real PostgreSQL server selected
/// by the environment contract.
/// </summary>
/// <remarks>
/// Isolation rules applied here: one schema per test class, one table name per scenario (always a fresh GUID), one
/// LISTEN channel per scenario, and connections that are really closed instead of pooled (<c>Pooling=false</c>) so
/// <c>pg_stat_activity</c> accounting and "the source opened no extra connection" assertions are exact. Nothing is
/// shared between classes, so the classes are parallel-safe.
/// <para>
/// Server selection and the skip/fail contract live in <see cref="PostgreSqlIntegrationEnvironment"/>: a missing
/// variable or an unreachable server fails the lane, and only <c>SMARTPIPE_POSTGRES_OPTIONAL=1</c> turns the
/// scenarios into explicit <see cref="Assert.Skip(string)"/> reports.
/// </para>
/// </remarks>
public sealed class PostgreSqlIntegrationDatabase : IAsyncLifetime
{
    private static readonly TimeSpan ProbeGuard = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CleanupGuard = TimeSpan.FromSeconds(30);

    private readonly List<NpgsqlDataSource> _dataSources = [];

    private string _connectionString = string.Empty;

    /// <summary>Gets a value indicating whether the server was reachable and this fixture owns a schema.</summary>
    public bool Available { get; private set; }

    /// <summary>Gets the reason the fixture is unavailable, or an empty string.</summary>
    public string UnavailableDetail { get; private set; } = string.Empty;

    /// <summary>Gets the server version reported by the server under test.</summary>
    public string ServerVersion { get; private set; } = string.Empty;

    /// <summary>Gets the application name every connection of <see cref="DataSource"/> carries.</summary>
    public string ApplicationName { get; private set; } = string.Empty;

    /// <summary>Gets the private schema owned by this fixture.</summary>
    public string Schema { get; private set; } = string.Empty;

    /// <summary>Gets the application-owned data source the components borrow.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Gets the resolved connection string of the server under test.</summary>
    public string ConnectionString => _connectionString;

    public async ValueTask InitializeAsync()
    {
        var resolved = PostgreSqlIntegrationEnvironment.ResolveConnectionString();
        if (resolved is null)
        {
            ReportUnavailable(PostgreSqlIntegrationEnvironment.MissingVariableFailure);
            return;
        }

        _connectionString = resolved;
        ApplicationName = $"sp-it-{Guid.NewGuid():N}"[..20];
        DataSource = BuildDataSource(_connectionString, ApplicationName, pooling: false);

        string version;
        using (var guard = new CancellationTokenSource(ProbeGuard))
        {
            try
            {
                await using var connection = await DataSource.OpenConnectionAsync(guard.Token).ConfigureAwait(false);
                version = await ScalarOnConnectionAsync<string>(
                    connection,
                    "SELECT current_setting('server_version')",
                    guard.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await DisposeDataSourcesAsync().ConfigureAwait(false);
                ReportUnavailable(PostgreSqlIntegrationEnvironment.UnreachableFailure(
                    _connectionString,
                    $"{exception.GetType().Name}: {exception.Message}"));
                return;
            }
        }

        ServerVersion = version;
        Schema = $"sp_it_{Guid.NewGuid():N}"[..18];
        await ExecuteAsync($"CREATE SCHEMA {QuoteIdentifier(Schema)}").ConfigureAwait(false);
        Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Available)
        {
            try
            {
                using var guard = new CancellationTokenSource(CleanupGuard);
                await using var connection = await DataSource.OpenConnectionAsync(guard.Token).ConfigureAwait(false);
                await ExecuteOnConnectionAsync(
                    connection,
                    $"DROP SCHEMA IF EXISTS {QuoteIdentifier(Schema)} CASCADE",
                    guard.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: a cleanup failure must never replace the scenario result. The schema is private to this
                // fixture and the local server is disposable, so a leaked empty schema is acceptable evidence-wise.
            }
        }

        await DisposeDataSourcesAsync().ConfigureAwait(false);
    }

    /// <summary>Fails the scenario when the server is unusable, or reports a visible skip under the local opt-out.</summary>
    public void RequireServer()
    {
        if (!Available)
            Assert.Skip(PostgreSqlIntegrationEnvironment.SkipReason(UnavailableDetail));
    }

    /// <summary>Creates a fresh, fully qualified table name inside this fixture's schema.</summary>
    public string NewTable(string prefix = "t") =>
        $"{QuoteIdentifier(Schema)}.{QuoteIdentifier($"{prefix}_{Guid.NewGuid():N}")}";

    /// <summary>Creates a fresh LISTEN channel name that cannot collide with another scenario.</summary>
    public string NewChannel() => $"sp_ch_{Guid.NewGuid():N}";

    /// <summary>
    /// Creates a dedicated listener data source with its own application name so the LISTEN backend can be identified
    /// (and in one scenario terminated) without touching any other connection.
    /// </summary>
    public NpgsqlDataSource CreateListenerDataSource(out string applicationName, bool pooling = false)
    {
        applicationName = $"sp-listen-{Guid.NewGuid():N}"[..24];
        return BuildDataSource(_connectionString, applicationName, pooling);
    }

    /// <summary>Quotes an identifier with the public Npgsql API.</summary>
    public static string QuoteIdentifier(string identifier) =>
        new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default) =>
        await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    public async Task ExecuteAsync(
        string sql,
        CancellationToken ct = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecuteOnConnectionAsync(connection, sql, ct, parameters).ConfigureAwait(false);
    }

    public async Task<T> ScalarAsync<T>(
        string sql,
        CancellationToken ct = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ScalarOnConnectionAsync<T>(connection, sql, ct, parameters).ConfigureAwait(false);
    }

    public static async Task ExecuteOnConnectionAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken ct = default,
        params (string Name, object? Value)[] parameters) =>
        await ExecuteOnConnectionAsync(connection, transaction: null, sql, ct, parameters).ConfigureAwait(false);

    /// <summary>Executes a statement inside an explicit transaction, exactly as the components do.</summary>
    public static async Task ExecuteOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken ct = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ScalarOnConnectionAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken ct = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new InvalidOperationException($"The scalar query returned NULL, which the scenario did not expect: {sql}");

        if (value is T typed)
            return typed;

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public Task<long> CountRowsAsync(string quotedTable, CancellationToken ct = default) =>
        ScalarAsync<long>($"SELECT count(*) FROM {quotedTable}", ct);

    /// <summary>Counts the live backends that carry an application name.</summary>
    public Task<long> CountBackendsAsync(string applicationName, CancellationToken ct = default) =>
        ScalarAsync<long>(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @application_name",
            ct,
            ("application_name", applicationName));

    /// <summary>Counts this fixture's backends that are currently executing a COPY statement.</summary>
    public Task<long> CountActiveCopyBackendsAsync(CancellationToken ct = default) =>
        ScalarAsync<long>(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @application_name AND state = 'active' AND query LIKE 'COPY%'",
            ct,
            ("application_name", ApplicationName));

    /// <summary>Counts backends that are inside an open transaction, which is how a leaked registration transaction shows up.</summary>
    public Task<long> CountIdleInTransactionBackendsAsync(string applicationName, CancellationToken ct = default) =>
        ScalarAsync<long>(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @application_name AND state = 'idle in transaction'",
            ct,
            ("application_name", applicationName));

    /// <summary>Sends one NOTIFY from a dedicated connection and returns that notifying backend's process id.</summary>
    public async Task<int> NotifyAsync(string channel, string payload, CancellationToken ct = default)
    {
        await using var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var pid = await ScalarOnConnectionAsync<int>(connection, "SELECT pg_backend_pid()", ct).ConfigureAwait(false);
        await ExecuteOnConnectionAsync(
            connection,
            "SELECT pg_notify(@channel, @payload)",
            ct,
            ("channel", channel),
            ("payload", payload)).ConfigureAwait(false);
        return pid;
    }

    /// <summary>Sends one or more NOTIFY statements inside a single explicit transaction and commits or rolls it back.</summary>
    public async Task<int> NotifyInTransactionAsync(
        IReadOnlyList<(string Channel, string Payload)> notifications,
        bool commit,
        CancellationToken ct = default)
    {
        await using var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var pid = await ScalarOnConnectionAsync<int>(connection, "SELECT pg_backend_pid()", ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var (channel, payload) in notifications)
        {
            await ExecuteOnConnectionAsync(
                connection,
                transaction,
                "SELECT pg_notify(@channel, @payload)",
                ct,
                ("channel", channel),
                ("payload", payload)).ConfigureAwait(false);
        }

        if (commit)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        else
            await transaction.RollbackAsync(ct).ConfigureAwait(false);

        return pid;
    }

    /// <summary>Terminates one backend and waits until the server no longer reports it.</summary>
    public async Task TerminateBackendAsync(int pid, CancellationToken ct = default)
    {
        await ScalarAsync<bool>("SELECT pg_terminate_backend(@pid)", ct, ("pid", pid)).ConfigureAwait(false);
        var gone = await WaitUntilAsync(
            async token => await ScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid",
                token,
                ("pid", pid)).ConfigureAwait(false) == 0,
            PostgreSqlTestGuard.Short,
            ct).ConfigureAwait(false);
        Assert.True(gone, $"Backend {pid} was still visible in pg_stat_activity after pg_terminate_backend.");
    }

    /// <summary>
    /// Bounded poll helper for state produced by a different server session. There is no task to await for such
    /// state, so the loop polls and always terminates at the deadline; it is an observation helper, never a
    /// synchronisation primitive for component behaviour.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<CancellationToken, Task<bool>> predicate,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await predicate(ct).ConfigureAwait(false))
                return true;

            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Observes for a bounded window whether a condition stays false. Used only to assert the absence of a future
    /// action (for example "no reconnect is attempted") and always terminates.
    /// </summary>
    public static async Task<bool> StaysFalseAsync(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan window,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition(ct).ConfigureAwait(false))
                return false;

            if (DateTime.UtcNow >= deadline)
                return true;

            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private NpgsqlDataSource BuildDataSource(string connectionString, string applicationName, bool pooling)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
            Pooling = pooling,
            Timeout = 10,
            CommandTimeout = 180,
        };

        var dataSource = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        _dataSources.Add(dataSource);
        return dataSource;
    }

    private void ReportUnavailable(string failure)
    {
        if (PostgreSqlIntegrationEnvironment.IsOptional)
        {
            UnavailableDetail = failure;
            return;
        }

        throw new InvalidOperationException(failure);
    }

    private async Task DisposeDataSourcesAsync()
    {
        foreach (var dataSource in _dataSources)
        {
            try
            {
                await dataSource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The data source is test-owned and disposable; a disposal failure must not mask the scenario result.
            }
        }

        _dataSources.Clear();
    }
}

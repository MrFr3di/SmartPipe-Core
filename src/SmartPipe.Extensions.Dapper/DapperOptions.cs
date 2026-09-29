#nullable enable

using System.Data;

namespace SmartPipe.Extensions.Dapper;

/// <summary>Controls whether Dapper caches the command metadata of one explicit-SQL operation.</summary>
public enum DapperCommandCacheMode
{
    /// <summary>Reuse Dapper's shared query cache for this operation.</summary>
    Default = 0,
    /// <summary>Bypass Dapper's shared query cache for this operation.</summary>
    NoCache = 1,
}

/// <summary>Controls transaction handling for one batch command sink write.</summary>
public enum DapperBatchTransactionMode
{
    /// <summary>Execute the batch without any transaction.</summary>
    None = 0,
    /// <summary>Execute the batch inside one explicitly begun and committed transaction.</summary>
    PerBatch = 1,
}

/// <summary>Options for the explicit-SQL Dapper query source.</summary>
public sealed record DapperQueryOptions
{
    /// <summary>Gets the logical operation name used for diagnostics.</summary>
    public string OperationName { get; init; } = "query";

    /// <summary>Gets the command timeout in seconds, or null to leave the provider default.</summary>
    public int? CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>Gets the command type used for the configured SQL.</summary>
    public CommandType CommandType { get; init; } = CommandType.Text;

    /// <summary>Gets the Dapper command cache mode.</summary>
    public DapperCommandCacheMode CacheMode { get; init; } = DapperCommandCacheMode.Default;
}

/// <summary>Options for the explicit-SQL Dapper command sink.</summary>
public sealed record DapperSinkOptions
{
    /// <summary>Gets the logical operation name used for diagnostics.</summary>
    public required string OperationName { get; init; }

    /// <summary>Gets the command timeout in seconds, or null to leave the provider default.</summary>
    public int? CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>Gets the command type used for the configured SQL.</summary>
    public CommandType CommandType { get; init; } = CommandType.Text;

    /// <summary>Gets the Dapper command cache mode.</summary>
    public DapperCommandCacheMode CacheMode { get; init; } = DapperCommandCacheMode.Default;
}

/// <summary>Options for the explicit-SQL Dapper batch command sink.</summary>
public sealed record DapperBatchSinkOptions
{
    /// <summary>Gets the logical operation name used for diagnostics.</summary>
    public required string OperationName { get; init; }

    /// <summary>Gets the command timeout in seconds, or null to leave the provider default.</summary>
    public int? CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>Gets the command type used for the configured SQL.</summary>
    public CommandType CommandType { get; init; } = CommandType.Text;

    /// <summary>Gets the Dapper command cache mode.</summary>
    public DapperCommandCacheMode CacheMode { get; init; } = DapperCommandCacheMode.Default;

    /// <summary>Gets the explicit transaction mode for every non-empty batch write.</summary>
    public required DapperBatchTransactionMode TransactionMode { get; init; }

    /// <summary>Gets the maximum accepted item count of one batch payload.</summary>
    public int MaxBatchItems { get; init; } = 1_000;

    /// <summary>Gets the isolation level passed to the provider, or null to let the provider choose.</summary>
    public IsolationLevel? IsolationLevel { get; init; }
}

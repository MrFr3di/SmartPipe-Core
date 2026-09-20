#nullable enable

using System.Data;
using Dapper;
using SmartPipe.Extensions.Dapper;

namespace SmartPipe.Extensions.Dapper.Internal;

/// <summary>Validates explicit-SQL option values and snapshots them for one composed component.</summary>
internal static class DapperOptionsValidation
{
    internal const int MaxOperationNameLength = 64;
    internal const int MaxCommandTimeoutSeconds = 86_400;
    internal const int MaxBatchItemsLimit = 1_000_000;

    internal static string ValidateOperationName(string? operationName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(operationName))
            throw new ArgumentException("OperationName must not be empty or whitespace.", parameterName);
        if (operationName.Length > MaxOperationNameLength)
            throw new ArgumentOutOfRangeException(
                parameterName,
                operationName.Length,
                $"OperationName must not exceed {MaxOperationNameLength} characters.");

        foreach (var character in operationName)
        {
            if (char.IsControl(character))
                throw new ArgumentException("OperationName must not contain control characters.", parameterName);
        }

        return operationName;
    }

    internal static int? ValidateCommandTimeout(int? commandTimeoutSeconds, string parameterName)
    {
        if (commandTimeoutSeconds is { } seconds && (seconds < 0 || seconds > MaxCommandTimeoutSeconds))
            throw new ArgumentOutOfRangeException(
                parameterName,
                seconds,
                $"CommandTimeoutSeconds must be null or between 0 and {MaxCommandTimeoutSeconds}.");

        return commandTimeoutSeconds;
    }

    internal static string ValidateSql(string? sql, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL text must not be empty or whitespace.", parameterName);

        return sql;
    }

    internal static TEnum ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "The value is not defined by the enum.");

        return value;
    }

    internal static CommandFlags ToFlags(DapperCommandCacheMode cacheMode) =>
        cacheMode == DapperCommandCacheMode.NoCache ? CommandFlags.NoCache : CommandFlags.None;
}

/// <summary>Immutable descriptor-time snapshot of the query source options.</summary>
internal sealed record DapperQueryOptionsSnapshot(
    string OperationName,
    int? CommandTimeoutSeconds,
    CommandType CommandType,
    DapperCommandCacheMode CacheMode)
{
    internal static DapperQueryOptionsSnapshot Create(DapperQueryOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(
            DapperOptionsValidation.ValidateOperationName(options.OperationName, nameof(options.OperationName)),
            DapperOptionsValidation.ValidateCommandTimeout(
                options.CommandTimeoutSeconds,
                nameof(options.CommandTimeoutSeconds)),
            DapperOptionsValidation.ValidateEnum(options.CommandType, nameof(options.CommandType)),
            DapperOptionsValidation.ValidateEnum(options.CacheMode, nameof(options.CacheMode)));
    }

    internal CommandFlags Flags => DapperOptionsValidation.ToFlags(CacheMode);
}

/// <summary>Immutable descriptor-time snapshot of the command sink options.</summary>
internal sealed record DapperSinkOptionsSnapshot(
    string OperationName,
    int? CommandTimeoutSeconds,
    CommandType CommandType,
    DapperCommandCacheMode CacheMode)
{
    internal static DapperSinkOptionsSnapshot Create(DapperSinkOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(
            DapperOptionsValidation.ValidateOperationName(options.OperationName, nameof(options.OperationName)),
            DapperOptionsValidation.ValidateCommandTimeout(
                options.CommandTimeoutSeconds,
                nameof(options.CommandTimeoutSeconds)),
            DapperOptionsValidation.ValidateEnum(options.CommandType, nameof(options.CommandType)),
            DapperOptionsValidation.ValidateEnum(options.CacheMode, nameof(options.CacheMode)));
    }

    internal CommandFlags Flags => DapperOptionsValidation.ToFlags(CacheMode);
}

/// <summary>Immutable descriptor-time snapshot of the batch command sink options.</summary>
internal sealed record DapperBatchSinkOptionsSnapshot(
    string OperationName,
    int? CommandTimeoutSeconds,
    CommandType CommandType,
    DapperCommandCacheMode CacheMode,
    DapperBatchTransactionMode TransactionMode,
    int MaxBatchItems,
    IsolationLevel? IsolationLevel)
{
    internal static DapperBatchSinkOptionsSnapshot Create(DapperBatchSinkOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var operationName = DapperOptionsValidation.ValidateOperationName(
            options.OperationName,
            nameof(options.OperationName));
        var commandTimeoutSeconds = DapperOptionsValidation.ValidateCommandTimeout(
            options.CommandTimeoutSeconds,
            nameof(options.CommandTimeoutSeconds));
        var commandType = DapperOptionsValidation.ValidateEnum(options.CommandType, nameof(options.CommandType));
        var cacheMode = DapperOptionsValidation.ValidateEnum(options.CacheMode, nameof(options.CacheMode));
        var transactionMode = DapperOptionsValidation.ValidateEnum(
            options.TransactionMode,
            nameof(options.TransactionMode));
        if (options.MaxBatchItems <= 0 || options.MaxBatchItems > DapperOptionsValidation.MaxBatchItemsLimit)
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxBatchItems),
                options.MaxBatchItems,
                $"MaxBatchItems must be between 1 and {DapperOptionsValidation.MaxBatchItemsLimit}.");

        if (options.IsolationLevel is { } isolationLevel)
        {
            DapperOptionsValidation.ValidateEnum(isolationLevel, nameof(options.IsolationLevel));
            if (transactionMode == DapperBatchTransactionMode.None)
                throw new ArgumentException(
                    "IsolationLevel requires DapperBatchTransactionMode.PerBatch.",
                    nameof(options.IsolationLevel));
        }

        return new(
            operationName,
            commandTimeoutSeconds,
            commandType,
            cacheMode,
            transactionMode,
            options.MaxBatchItems,
            options.IsolationLevel);
    }

    internal CommandFlags Flags => DapperOptionsValidation.ToFlags(CacheMode);
}

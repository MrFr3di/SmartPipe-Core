namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>
/// Central message catalogue. Messages never contain COPY SQL text, connection strings, channel payloads,
/// credentials or parameter values, because provider components may surface these messages to callers and logs.
/// </summary>
internal static class PostgreSqlErrorMessages
{
    internal const string OperationNameRequired = "Operation name is required.";
    internal const string OperationNameBlank = "Operation name cannot be empty or whitespace.";
    internal const string OperationNameTooLong = "Operation name cannot exceed 64 characters.";
    internal const string CopyCommandRequired = "COPY command is required.";
    internal const string CopyCommandBlank = "COPY command cannot be empty or whitespace.";
    internal const string DataSourceRequired = "An NpgsqlDataSource is required.";
    internal const string RowReaderRequired = "A row reader callback is required.";
    internal const string RowWriterRequired = "A row writer callback is required.";
    internal const string ExpectedColumnCountPositive = "Expected column count must be greater than zero.";
    internal const string MaxRowsPerBatchPositive = "MaxRowsPerBatch must be greater than zero.";
    internal const string BufferCapacityPositive = "BufferCapacity must be greater than zero.";
    internal const string ChannelsRequired = "A channel collection is required.";
    internal const string ChannelsEmpty = "At least one channel identifier is required.";
    internal const string ChannelEntryBlank = "Channel identifiers cannot be null, empty or whitespace.";
    internal const string ChannelDuplicate = "Duplicate channel identifiers are not allowed.";
    internal const string ChannelsMutated = "The channel collection was mutated after validation.";
    internal const string ColumnCountMismatchFormat =
        "The binary COPY export reported {0} columns for a row but the configured ExpectedColumnCount is {1}.";
    internal const string AmbientTransactionRejected =
        "PostgreSQL pipeline components do not participate in ambient System.Transactions transactions. "
        + "Open the connection outside a TransactionScope or suppress the ambient transaction before activating the pipeline.";
    internal const string SourceNotInitialized = "Source is not initialized. Call InitializeAsync before enumerating.";
    internal const string SinkNotInitialized = "Sink is not initialized. Call InitializeAsync before writing.";
    internal const string SourceEnumeratedTwice = "A binary COPY source can be enumerated only once per activation.";
    internal const string NotificationSourceEnumeratedTwice =
        "A LISTEN notification source can be enumerated only once per activation.";
    internal const string BatchTooLarge = "The batch exceeds MaxRowsPerBatch and was rejected before the COPY operation started.";
    internal const string NotificationBufferOverflow =
        "The notification bridge is full; the rejected notification was not accepted and may be lost.";
    internal const string NotificationConnectionLost =
        "The LISTEN connection was lost. Notifications issued while the connection was down cannot be recovered and no reconnect is attempted.";
    internal const string SourceCleanupFailed = "The PostgreSQL source operation failed and cleanup also failed.";
    internal const string SinkCleanupFailed = "The PostgreSQL sink operation failed and cleanup also failed.";
    internal const string SourceCleanupOnlyFailed = "PostgreSQL source cleanup failed.";
    internal const string SinkCleanupOnlyFailed = "PostgreSQL sink cleanup failed.";
    internal const string NotificationCleanupOnlyFailed = "PostgreSQL notification cleanup failed.";
    internal const string NotificationCleanupAfterFailureFailed =
        "The LISTEN source failed and cleanup also failed.";
}

namespace SmartPipe.Extensions.PostgreSql;

/// <summary>Configures a binary <c>COPY … FROM STDIN (FORMAT BINARY)</c> batch pipeline sink.</summary>
/// <remarks>
/// One SmartPipe batch envelope corresponds to one complete PostgreSQL COPY operation. A successful
/// <c>WriteAsync</c> means the COPY completed; the sink never defers completion to disposal.
/// </remarks>
public sealed record PostgreSqlBinaryCopySinkOptions
{
    /// <summary>Gets the logical operation name used in structured logs and failure messages.</summary>
    /// <remarks>Optional, at most 64 characters, non-blank. Defaults to <c>postgresql-copy-in</c>.</remarks>
    public string OperationName { get; init; } = "postgresql-copy-in";

    /// <summary>Gets the maximum number of rows a single batch may contain.</summary>
    /// <remarks>
    /// Must be greater than zero. The limit bounds one COPY operation; it does not describe memory the caller already
    /// used to build the batch. A batch above the limit is rejected before any server work starts.
    /// </remarks>
    public int MaxRowsPerBatch { get; init; } = 10_000;
}

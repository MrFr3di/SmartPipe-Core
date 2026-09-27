namespace SmartPipe.Extensions.PostgreSql;

/// <summary>Configures a binary <c>COPY … TO STDOUT (FORMAT BINARY)</c> pipeline source.</summary>
public sealed record PostgreSqlBinaryCopySourceOptions
{
    /// <summary>Gets the logical operation name used in structured logs and failure messages.</summary>
    /// <remarks>Optional, at most 64 characters, non-blank. Defaults to <c>postgresql-copy-out</c>.</remarks>
    public string OperationName { get; init; } = "postgresql-copy-out";

    /// <summary>
    /// Gets the optional number of columns the COPY statement is expected to produce. When set, a mismatch is
    /// reported before the row callback is invoked.
    /// </summary>
    /// <remarks>Optional, must be greater than zero when supplied. PostgreSQL remains the owner of column typing.</remarks>
    public int? ExpectedColumnCount { get; init; }
}

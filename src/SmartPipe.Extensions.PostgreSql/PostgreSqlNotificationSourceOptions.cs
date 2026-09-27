namespace SmartPipe.Extensions.PostgreSql;

/// <summary>Configures a <c>LISTEN</c> notification pipeline source.</summary>
/// <remarks>
/// The source is a best-effort wake-up signal, never a durable queue: it does not reconnect, does not replay and does
/// not guarantee delivery of every NOTIFY statement.
/// </remarks>
public sealed record PostgreSqlNotificationSourceOptions
{
    /// <summary>Gets the logical operation name used in structured logs and failure messages.</summary>
    /// <remarks>Optional, at most 64 characters, non-blank. Defaults to <c>postgresql-listen</c>.</remarks>
    public string OperationName { get; init; } = "postgresql-listen";

    /// <summary>Gets the capacity of the bounded bridge between the synchronous notification callback and the pipeline.</summary>
    /// <remarks>
    /// Must be greater than zero. The bridge is the only intentional extra buffer. A full bridge is an explicit fault:
    /// the rejected notification is not accepted and no silent drop, drop-oldest or drop-newest behaviour is applied.
    /// </remarks>
    public int BufferCapacity { get; init; } = 256;
}

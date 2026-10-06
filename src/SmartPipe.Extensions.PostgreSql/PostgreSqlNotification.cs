namespace SmartPipe.Extensions.PostgreSql;

/// <summary>An accepted PostgreSQL <c>NOTIFY</c> delivery.</summary>
/// <param name="Channel">The channel identifier exactly as PostgreSQL reported it.</param>
/// <param name="Payload">The opaque notification payload exactly as PostgreSQL reported it.</param>
/// <param name="BackendProcessId">The backend process id of the notifying session.</param>
/// <remarks>
/// PostgreSQL and Npgsql do not supply a database-side event timestamp, so none is invented here; local receipt time
/// belongs to pipeline telemetry. The payload is opaque: this package never parses, validates or logs it.
/// </remarks>
public sealed record PostgreSqlNotification(string Channel, string Payload, int BackendProcessId);

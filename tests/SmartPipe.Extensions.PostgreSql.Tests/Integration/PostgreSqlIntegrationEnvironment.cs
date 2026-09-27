using Npgsql;

namespace SmartPipe.Extensions.PostgreSql.Tests.Integration;

/// <summary>
/// Resolves the real PostgreSQL server used by the integration lane and decides whether a missing variable or an
/// unreachable server is a hard failure or an explicit local skip.
/// </summary>
/// <remarks>
/// The lane uses a single primary variable so one value describes one lane:
/// <list type="bullet">
/// <item><description><c>SMARTPIPE_POSTGRES_CONNECTION_STRING</c> — the server under test (primary lane: PostgreSQL 18.6 on port 55432).</description></item>
/// <item><description><c>SMARTPIPE_POSTGRES_CONNECTION_STRING_17</c> — optional fallback, used only when the primary variable is unset; the compatibility lane normally sets the primary variable to the 17.11 connection string instead, so this fallback never has to be set.</description></item>
/// <item><description><c>SMARTPIPE_POSTGRES_OPTIONAL</c> — the only permitted relaxation. When it is exactly <c>1</c>, a missing variable or an unreachable server turns every scenario into an explicit, visible skip. Any other value (including unset) fails the lane.</description></item>
/// </list>
/// </remarks>
internal static class PostgreSqlIntegrationEnvironment
{
    internal const string PrimaryConnectionVariable = "SMARTPIPE_POSTGRES_CONNECTION_STRING";

    internal const string CompatibilityConnectionVariable = "SMARTPIPE_POSTGRES_CONNECTION_STRING_17";

    internal const string OptionalVariable = "SMARTPIPE_POSTGRES_OPTIONAL";

    internal static bool IsOptional =>
        string.Equals(Environment.GetEnvironmentVariable(OptionalVariable), "1", StringComparison.Ordinal);

    internal static string? ResolveConnectionString()
    {
        var primary = Environment.GetEnvironmentVariable(PrimaryConnectionVariable);
        if (!string.IsNullOrWhiteSpace(primary))
            return primary;

        var compatibility = Environment.GetEnvironmentVariable(CompatibilityConnectionVariable);
        return string.IsNullOrWhiteSpace(compatibility) ? null : compatibility;
    }

    /// <summary>Describes host, port and database without ever echoing credentials or the full connection string.</summary>
    internal static string DescribeTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "<no connection string>";

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"Host={builder.Host};Port={builder.Port};Database={builder.Database}";
        }
        catch (Exception)
        {
            return "<unparseable connection string>";
        }
    }

    internal static string MissingVariableFailure =>
        $"Neither {PrimaryConnectionVariable} nor {CompatibilityConnectionVariable} is set, so the real-PostgreSQL "
        + "integration lane cannot run. This lane fails instead of silently passing; set the variable to the server "
        + $"under test, or set {OptionalVariable}=1 for an explicit local opt-out that reports every scenario as skipped.";

    internal static string UnreachableFailure(string? connectionString, string detail) =>
        $"The PostgreSQL server at {DescribeTarget(connectionString)} is unreachable ({detail}), so the "
        + "real-PostgreSQL integration lane cannot run. This lane fails instead of silently passing; start the server, "
        + $"or set {OptionalVariable}=1 for an explicit local opt-out that reports every scenario as skipped.";

    internal static string SkipReason(string detail) =>
        $"SKIPPED integration scenario: {detail} Only {OptionalVariable}=1 permits skipping this lane."
        + $" Target was {DescribeTarget(ResolveConnectionString())}.";
}

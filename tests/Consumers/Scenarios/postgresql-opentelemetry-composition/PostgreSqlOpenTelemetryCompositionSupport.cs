using System.Diagnostics;
using System.Reflection;
using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;

namespace SmartPipe.Consumer.PostgreSql;



/// <summary>The names this scenario owns inside the shared test database.</summary>
internal static class TelemetryScenario
{
    public const string Schema = "sp_consumer_postgresql_opentelemetry_composition";
    public const string RowsTable = Schema + ".telemetry_rows";
    public const string CopyInPipelineId = "postgresql-opentelemetry-composition-copy-in";
    public const string CopyOutPipelineId = "postgresql-opentelemetry-composition-copy-out";
}

/// <summary>Proves a deployed assembly carries no dependency on a forbidden assembly name fragment.</summary>
internal static class ConsumerAssemblyGuard
{
    public static void RequireNoReferenceContaining(Assembly assembly, string forbiddenFragment)
    {
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            if (reference.Name is not null
                && reference.Name.Contains(forbiddenFragment, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The deployed assembly '{assembly.GetName().Name}' references '{reference.Name}'.");
            }
        }
    }
}


/// <summary>A row of the scenario's table.</summary>
internal sealed record TelemetryRow(int Id, string Name);

/// <summary>Walks the exported activity graph without assuming a fixed stage depth.</summary>
internal static class ActivityGraph
{
    private const int MaximumDepth = 32;

    public static Activity? FindSmartPipeAncestor(
        Activity activity,
        IReadOnlyDictionary<ActivitySpanId, Activity> activitiesBySpanId)
    {
        var parentSpanId = activity.ParentSpanId;
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (parentSpanId == default || !activitiesBySpanId.TryGetValue(parentSpanId, out var parent))
                return null;

            if (string.Equals(parent.Source.Name, SmartPipeDiagnostics.ActivitySourceName, StringComparison.Ordinal))
                return parent;

            parentSpanId = parent.ParentSpanId;
        }

        return null;
    }

    /// <summary>True when the activity carries any database client attribute.</summary>
    public static bool CarriesDatabaseAttribute(Activity activity)
    {
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Key.StartsWith("db.", StringComparison.Ordinal)
                || tag.Key.StartsWith("server.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string? Text(Activity activity, string attributeName) =>
        activity.GetTagItem(attributeName) as string;
}

/// <summary>The per-row COPY callbacks used by this scenario.</summary>
internal static class TelemetryCallbacks
{
    public static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, TelemetryRow row, CancellationToken cancellationToken)
    {
        await importer.WriteAsync(row.Id, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Name, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<TelemetryRow> ReadRowAsync(
        NpgsqlBinaryExporter exporter,
        int columnCount,
        CancellationToken cancellationToken)
    {
        ConsumerCheck.Require(columnCount == 2, "The COPY OUT export reported an unexpected column count.");
        var id = await exporter.ReadAsync<int>(NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
        var name = await exporter.ReadAsync<string>(NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        return new TelemetryRow(id, name);
    }
}

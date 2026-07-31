using System.Data.Common;
using DuckDB.NET.Data;
using MemorySnapshotDataTools.Validation;
using Microsoft.Data.Sqlite;

namespace MemorySnapshotDataTools.Report;

/// <summary>
/// Reads the <c>summary_metrics</c> table from an exported DuckDB or SQLite database into a
/// <see cref="SummaryMetrics"/>. Shared by the <c>summary</c> command and the multi-snapshot report so
/// the group-mapping and DuckDB/SQLite normalization live in one place. The query is the constant
/// <see cref="GoldenValidationQueries.SummaryMetricsSql"/> (no parameters, no identifier interpolation),
/// and a missing table yields <c>Available = false</c> rather than throwing — see docs/sql-safety.md.
/// </summary>
internal static class SummaryMetricsDbReader
{
    /// <summary>
    /// Reads <c>summary_metrics</c> from an already-open (ideally read-only) connection.
    /// </summary>
    /// <returns>The populated metrics and whether the table was present.</returns>
    public static (SummaryMetrics Metrics, bool Available) Read(DbConnection connection)
    {
        var metrics = new SummaryMetrics();
        try
        {
            // Swapped columns exist from schema v2.0; older databases degrade to resident-only
            // (SwappedAvailable stays false). The presence check is a parameterized catalog query.
            var hasSwapped = HasSummarySwappedColumns(connection);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = hasSwapped
                ? GoldenValidationQueries.SummaryMetricsWithSwappedSql
                : GoldenValidationQueries.SummaryMetricsSql;
            using var reader = cmd.ExecuteReader();

            var any = false;
            while (reader.Read())
            {
                any = true;
                var group = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var category = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var committed = ToULong(DbScalarReader.GetInt64(reader, 2));
                var resident = ToULong(DbScalarReader.GetInt64(reader, 3));
                var residentAvailable = DbScalarReader.GetInt64(reader, 4) != 0;
                var swapped = hasSwapped ? ToULong(DbScalarReader.GetInt64(reader, 5)) : 0UL;
                var swappedAvailable = hasSwapped && DbScalarReader.GetInt64(reader, 6) != 0;

                if (group == SummaryMetricsTable.GroupTotals && category == SummaryMetricsTable.CategoryTotal)
                {
                    metrics.TotalAllocatedBytes = committed;
                    metrics.TotalResidentBytes = resident;
                    metrics.TotalSwappedBytes = swapped;
                    metrics.SwappedAvailable = swappedAvailable;
                }
                else if (group == SummaryMetricsTable.GroupAllocatedMemoryDistribution)
                {
                    metrics.AllocatedMemoryDistribution.Add(MakeCategory(category, committed, resident, residentAvailable, swapped, swappedAvailable));
                }
                else if (group == SummaryMetricsTable.GroupManagedHeapUtilization)
                {
                    metrics.ManagedHeapUtilization.Add(MakeCategory(category, committed, resident, residentAvailable, swapped, swappedAvailable));
                }
            }

            return (metrics, any);
        }
        catch (DbException)
        {
            // summary_metrics table absent (database exported by an older tool version).
            return (metrics, false);
        }
    }

    /// <summary>
    /// Whether <c>summary_metrics</c> has the schema v2.0 swapped columns. Identifier checks go through
    /// the catalog tables with bound parameters (never spliced into SQL) — see docs/sql-safety.md.
    /// </summary>
    private static bool HasSummarySwappedColumns(DbConnection connection)
    {
        if (connection is DuckDBConnection duckDb)
        {
            using var cmd = duckDb.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM information_schema.columns WHERE table_schema = 'main' AND table_name = ? AND column_name = ? LIMIT 1";
            cmd.Parameters.Add(new DuckDBParameter { Value = "summary_metrics" });
            cmd.Parameters.Add(new DuckDBParameter { Value = "swapped_bytes" });
            return cmd.ExecuteScalar() != null;
        }

        if (connection is SqliteConnection sqlite)
        {
            using var cmd = sqlite.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM pragma_table_info($t) WHERE name = $c LIMIT 1";
            cmd.Parameters.AddWithValue("$t", "summary_metrics");
            cmd.Parameters.AddWithValue("$c", "swapped_bytes");
            return cmd.ExecuteScalar() != null;
        }

        return false;
    }

    private static SummaryCategory MakeCategory(string name, ulong committed, ulong resident, bool residentAvailable, ulong swapped, bool swappedAvailable) =>
        new()
        {
            Name = name,
            CommittedBytes = committed,
            ResidentBytes = resident,
            ResidentAvailable = residentAvailable,
            SwappedBytes = swapped,
            SwappedAvailable = swappedAvailable,
        };

    private static ulong ToULong(long value) => value < 0 ? 0UL : (ulong)value;
}

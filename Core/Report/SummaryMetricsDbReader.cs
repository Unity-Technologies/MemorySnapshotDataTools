using System.Data.Common;
using MemorySnapshotDataTools.Validation;

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
            using var cmd = connection.CreateCommand();
            cmd.CommandText = GoldenValidationQueries.SummaryMetricsSql;
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

                if (group == SummaryMetricsTable.GroupTotals && category == SummaryMetricsTable.CategoryTotal)
                {
                    metrics.TotalAllocatedBytes = committed;
                    metrics.TotalResidentBytes = resident;
                }
                else if (group == SummaryMetricsTable.GroupAllocatedMemoryDistribution)
                {
                    metrics.AllocatedMemoryDistribution.Add(MakeCategory(category, committed, resident, residentAvailable));
                }
                else if (group == SummaryMetricsTable.GroupManagedHeapUtilization)
                {
                    metrics.ManagedHeapUtilization.Add(MakeCategory(category, committed, resident, residentAvailable));
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

    private static SummaryCategory MakeCategory(string name, ulong committed, ulong resident, bool residentAvailable) =>
        new()
        {
            Name = name,
            CommittedBytes = committed,
            ResidentBytes = resident,
            ResidentAvailable = residentAvailable,
        };

    private static ulong ToULong(long value) => value < 0 ? 0UL : (ulong)value;
}

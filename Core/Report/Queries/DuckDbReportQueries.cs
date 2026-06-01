using DuckDB.NET.Data;

namespace MemorySnapshotDataTools.Report.Queries;

/// <summary>DuckDB implementation of <see cref="IReportQueryBackend"/>. Opens the database at construction and executes report SQL via DuckDB.NET.</summary>
internal sealed class DuckDbReportQueries : IReportQueryBackend
{
    private readonly DuckDBConnection _connection;

    /// <summary>Opens a read-only connection to the DuckDB database at the given path.</summary>
    /// <param name="dbPath">Path to the .duckdb file.</param>
    public DuckDbReportQueries(string dbPath)
    {
        // The report path only ever runs SELECTs. Open read-only (least privilege) so that even a
        // malformed query reaching ExecuteQuery cannot modify or drop data. See docs/sql-safety.md.
        _connection = new DuckDBConnection($"Data Source={dbPath};ACCESS_MODE=READ_ONLY");
        _connection.Open();
    }

    /// <inheritdoc/>
    public ReportBackendDialect Dialect => ReportBackendDialect.DuckDb;

    /// <inheritdoc/>
    public (string[] Columns, List<object?[]> Rows) ExecuteQuery(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            columns[i] = reader.GetName(i);
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return (columns, rows);
    }

    /// <inheritdoc/>
    public bool HasColumn(string tableName, string columnName)
    {
        try
        {
            // Bind table/column names as parameters rather than interpolating them. information_schema.columns
            // is a regular table, so it accepts bind parameters (DuckDB uses positional '?').
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM information_schema.columns WHERE table_schema = 'main' AND table_name = ? AND column_name = ? LIMIT 1";
            cmd.Parameters.Add(new DuckDBParameter { Value = tableName });
            cmd.Parameters.Add(new DuckDBParameter { Value = columnName });
            return cmd.ExecuteScalar() != null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _connection.Dispose();
}

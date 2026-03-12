using DuckDB.NET.Data;

namespace MemorySnapshotDataTools.Report.Queries;

/// <summary>DuckDB implementation of <see cref="IReportQueryBackend"/>. Opens the database at construction and executes report SQL via DuckDB.NET.</summary>
internal sealed class DuckDbReportQueries : IReportQueryBackend
{
    private readonly DuckDBConnection _connection;

    /// <summary>Opens a connection to the DuckDB database at the given path.</summary>
    /// <param name="dbPath">Path to the .duckdb file.</param>
    public DuckDbReportQueries(string dbPath)
    {
        _connection = new DuckDBConnection($"Data Source={dbPath}");
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
            var (_, rows) = ExecuteQuery(
                $"SELECT 1 FROM information_schema.columns WHERE table_schema = 'main' AND table_name = '{tableName.Replace("'", "''")}' AND column_name = '{columnName.Replace("'", "''")}' LIMIT 1");
            return rows.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _connection.Dispose();
}

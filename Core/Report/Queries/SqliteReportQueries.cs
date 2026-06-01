using Microsoft.Data.Sqlite;

namespace MemorySnapshotDataTools.Report.Queries;

/// <summary>SQLite implementation of <see cref="IReportQueryBackend"/>. Opens the database at construction and executes report SQL via Microsoft.Data.Sqlite.</summary>
internal sealed class SqliteReportQueries : IReportQueryBackend
{
    private readonly SqliteConnection _connection;

    /// <summary>Opens a read-only connection to the SQLite database at the given path.</summary>
    /// <param name="dbPath">Path to the .db or .sqlite file.</param>
    public SqliteReportQueries(string dbPath)
    {
        // The report path only ever runs SELECTs. Open read-only (least privilege) so that even a
        // malformed query reaching ExecuteQuery cannot modify or drop data. See docs/sql-safety.md.
        _connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        _connection.Open();
    }

    /// <inheritdoc/>
    public ReportBackendDialect Dialect => ReportBackendDialect.Sqlite;

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
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info($t) WHERE name = $c";
            cmd.Parameters.AddWithValue("$t", tableName);
            cmd.Parameters.AddWithValue("$c", columnName);
            using var reader = cmd.ExecuteReader();
            return reader.Read();
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _connection.Dispose();
}

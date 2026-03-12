namespace MemorySnapshotDataTools.Report.Queries;

/// <summary>Database dialect used for report queries (affects SQL for e.g. LOG/rounding).</summary>
internal enum ReportBackendDialect
{
    /// <summary>DuckDB backend.</summary>
    DuckDb,

    /// <summary>SQLite backend.</summary>
    Sqlite,
}

/// <summary>
/// Abstraction for running report queries against an exported snapshot database.
/// Implementations exist for DuckDB and SQLite so the report generator is backend-agnostic.
/// </summary>
internal interface IReportQueryBackend : IDisposable
{
    /// <summary>Dialect of the connected database (used to choose dialect-specific SQL).</summary>
    ReportBackendDialect Dialect { get; }

    /// <summary>Executes the given SQL and returns column names and rows (null for missing values).</summary>
    /// <param name="sql">SQL query (single statement).</param>
    /// <returns>Column names and list of row arrays.</returns>
    (string[] Columns, List<object?[]> Rows) ExecuteQuery(string sql);

    /// <summary>Returns whether the table has a column with the given name.</summary>
    /// <param name="tableName">Table name.</param>
    /// <param name="columnName">Column name.</param>
    /// <returns>True if the column exists.</returns>
    bool HasColumn(string tableName, string columnName);
}

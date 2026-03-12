namespace MemorySnapshotDataTools;

/// <summary>
/// How much validation to run after writing the database (counts only, or full referential checks).
/// </summary>
public enum ValidationMode
{
    /// <summary>Skip validation.</summary>
    None,

    /// <summary>Verify row counts match extracted data.</summary>
    Minimal,

    /// <summary>Counts plus duplicate-key and orphan/reference checks.</summary>
    Full,
}

/// <summary>
/// Which database backend to use for export (DuckDB or SQLite).
/// </summary>
public enum DestinationKind
{
    /// <summary>Export to a DuckDB database (.duckdb).</summary>
    DuckDb,

    /// <summary>Export to a SQLite database (.db).</summary>
    Sqlite,
}

/// <summary>
/// Options for the export pipeline. Created by the CLI from parsed arguments and passed to
/// <see cref="Export.ExportPipeline.Run"/>.
/// </summary>
public sealed class ExportRunOptions
{
    /// <summary>Output database file path (.duckdb or .db).</summary>
    public string OutputDbPath { get; set; } = string.Empty;

    /// <summary>Number of rows per batch produced by the pipeline (default 2048).</summary>
    public int BatchSize { get; set; } = 2048;

    /// <summary>Maximum number of batches that can be queued between producers and the writer (default 256).</summary>
    public int QueueCapacity { get; set; } = 256;

    /// <summary>Validation to run after write (default <see cref="ValidationMode.Minimal"/>).</summary>
    public ValidationMode Validate { get; set; } = ValidationMode.Minimal;
}

/// <summary>
/// Options for report generation. Created by the CLI from parsed arguments and passed to
/// <see cref="Report.ReportRunner.Run"/>.
/// </summary>
public sealed class ReportRunOptions
{
    /// <summary>Path to the exported database (DuckDB or SQLite).</summary>
    public string ReportDbPath { get; set; } = string.Empty;

    /// <summary>Output HTML path; if null, a temp file is used and the report is opened in the browser.</summary>
    public string? ReportOutputPath { get; set; }

    /// <summary>Title shown in the generated report (default "Memory Snapshot Report").</summary>
    public string ReportTitle { get; set; } = "Memory Snapshot Report";
}

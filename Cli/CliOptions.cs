using MemorySnapshotDataTools;

namespace MemorySnapshotDataTools.Cli;

internal enum CommandKind
{
    Export,
    Report,
    Gephi,
}

/// <summary>
/// Parsed CLI options passed from System.CommandLine handlers to RunExport/RunReport.
/// </summary>
internal sealed class CliOptions
{
    public CommandKind Command { get; set; } = CommandKind.Export;
    public string SnapshotPath { get; set; } = string.Empty;
    public string OutputDbPath { get; set; } = string.Empty;
    public string ReportDbPath { get; set; } = string.Empty;
    public string? ReportOutputPath { get; set; }
    public string ReportTitle { get; set; } = "Memory Snapshot Report";
    public int BatchSize { get; set; } = 2048;
    public int QueueCapacity { get; set; } = 256;
    public ValidationMode Validate { get; set; } = ValidationMode.Minimal;
    public DestinationKind Destination { get; set; } = DestinationKind.DuckDb;
    public bool Verbose { get; set; }

    /// <summary>Path to the database for Gephi export (gephi command).</summary>
    public string GephiDbPath { get; set; } = string.Empty;

    /// <summary>Output path for the edges CSV (gephi command).</summary>
    public string GephiOutPath { get; set; } = string.Empty;

    /// <summary>Optional output path for the nodes CSV (gephi command).</summary>
    public string? GephiNodesPath { get; set; }

    /// <summary>Gephi export mode: native, managed, or mixed (only native is supported).</summary>
    public string GephiMode { get; set; } = "native";
}

internal sealed class ConsoleProgress : IProgressReporter
{
    private readonly bool _verbose;
    private readonly object _lock = new();
    private DateTime _lastWrite = DateTime.MinValue;

    public ConsoleProgress(bool verbose)
    {
        _verbose = verbose;
    }

    public void Report(string message, bool force = false)
    {
        if (!_verbose && !force)
            return;

        lock (_lock)
        {
            if (!force && DateTime.UtcNow - _lastWrite < TimeSpan.FromMilliseconds(250))
                return;
            _lastWrite = DateTime.UtcNow;
            Console.WriteLine($"[{DateTime.UtcNow:O}] {message}");
        }
    }
}

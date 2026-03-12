using MemorySnapshotDataTools;

namespace MemorySnapshotDataTools.Cli;

internal enum CommandKind
{
    Export,
    Report,
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

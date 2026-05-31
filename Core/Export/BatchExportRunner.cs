namespace MemorySnapshotDataTools.Export;

/// <summary>
/// Options for exporting every <c>.snap</c> file in a directory to a database alongside it.
/// </summary>
public sealed class BatchExportRunOptions
{
    /// <summary>Directory to scan for <c>.snap</c> files (top level only).</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Optional case-insensitive substring filter on snapshot filenames.</summary>
    public string? NameFilter { get; set; }

    /// <summary>Database backend (default DuckDB).</summary>
    public DestinationKind Destination { get; set; } = DestinationKind.DuckDb;

    /// <summary>Pipeline batch size passed to each export.</summary>
    public int BatchSize { get; set; } = 2048;

    /// <summary>Pipeline queue capacity passed to each export.</summary>
    public int QueueCapacity { get; set; } = 256;

    /// <summary>Post-export validation mode.</summary>
    public ValidationMode Validate { get; set; } = ValidationMode.Minimal;

    /// <summary>When true, skip snapshots whose output database exists and is newer than the snap file.</summary>
    public bool SkipExisting { get; set; }

    /// <summary>When true, continue exporting remaining files after a single-file failure.</summary>
    public bool ContinueOnError { get; set; } = true;
}

/// <summary>
/// Result summary for a <see cref="BatchExportRunner"/> run.
/// </summary>
public sealed class BatchExportResult
{
    /// <summary>Snapshot files that exported successfully.</summary>
    public IReadOnlyList<string> Succeeded { get; init; } = [];

    /// <summary>Snapshot files skipped because output was up to date.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>Snapshot paths paired with failure messages.</summary>
    public IReadOnlyList<(string SnapshotPath, string Error)> Failed { get; init; } = [];
}

/// <summary>
/// Exports multiple <c>.snap</c> files from a directory to <c>.duckdb</c> or <c>.db</c> files with matching basenames.
/// </summary>
public static class BatchExportRunner
{
    /// <summary>
    /// Discovers <c>.snap</c> files in <paramref name="directory"/> matching an optional name filter.
    /// </summary>
    public static IReadOnlyList<string> DiscoverSnapshotFiles(string directory, string? nameFilter)
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.snap", SearchOption.TopDirectoryOnly)
            .Where(p => string.IsNullOrWhiteSpace(nameFilter)
                || Path.GetFileName(p).Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Exports each discovered snapshot. Returns process exit code: 0 all ok, 1 partial failure, 2 cancelled, 3 fatal.
    /// </summary>
    public static int Run(BatchExportRunOptions options, IProgressReporter progress, CancellationToken token = default)
    {
        var directory = Path.GetFullPath(options.Directory);
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"Directory not found: {directory}");
            return 3;
        }

        var snapshots = DiscoverSnapshotFiles(directory, options.NameFilter);
        if (snapshots.Count == 0)
        {
            Console.Error.WriteLine($"No .snap files found in {directory}" +
                (string.IsNullOrWhiteSpace(options.NameFilter) ? "." : $" matching filter '{options.NameFilter}'."));
            return 3;
        }

        var extension = options.Destination == DestinationKind.Sqlite ? ".db" : ".duckdb";
        var pipelineOptions = new ExportRunOptions
        {
            BatchSize = options.BatchSize,
            QueueCapacity = options.QueueCapacity,
            Validate = options.Validate,
        };

        progress.Report(
            $"Batch export: {directory} ({snapshots.Count} snapshots, filter: {options.NameFilter ?? "(none)"}, destination: {options.Destination})",
            force: true);

        var succeeded = new List<string>();
        var skipped = new List<string>();
        var failed = new List<(string, string)>();

        for (var i = 0; i < snapshots.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var snapPath = snapshots[i];
            var outputPath = Path.ChangeExtension(snapPath, extension);
            var label = $"[{i + 1}/{snapshots.Count}] {Path.GetFileName(snapPath)}";
            progress.Report($"=== {label} ===", force: true);

            if (options.SkipExisting && ShouldSkip(snapPath, outputPath))
            {
                progress.Report($"Skipped (up-to-date): {Path.GetFileName(outputPath)}", force: true);
                skipped.Add(snapPath);
                continue;
            }

            var exitCode = ExportRunner.Run(
                snapPath,
                outputPath,
                pipelineOptions,
                options.Destination,
                progress,
                token);

            if (exitCode == 0)
            {
                succeeded.Add(snapPath);
                continue;
            }

            if (exitCode == 2)
                return 2;

            failed.Add((snapPath, $"export exited with code {exitCode}"));
            if (!options.ContinueOnError)
                break;
        }

        var result = new BatchExportResult
        {
            Succeeded = succeeded,
            Skipped = skipped,
            Failed = failed,
        };

        progress.Report(
            $"Batch complete: {result.Succeeded.Count} succeeded, {result.Skipped.Count} skipped, {result.Failed.Count} failed.",
            force: true);

        foreach (var (snapPath, error) in result.Failed)
            Console.Error.WriteLine($"  FAILED {Path.GetFileName(snapPath)}: {error}");

        if (result.Failed.Count > 0)
            return 1;

        return 0;
    }

    private static bool ShouldSkip(string snapPath, string outputPath)
    {
        if (!File.Exists(outputPath))
            return false;

        return File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(snapPath);
    }
}

using System.Diagnostics;

namespace MemorySnapshotDataTools.Report.MultiSnapshotReport;

/// <summary>
/// Options for generating a multi-snapshot HTML report from a directory of database files.
/// </summary>
public sealed class MultiSnapshotReportRunOptions
{
    /// <summary>Directory containing .duckdb or .db files.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Optional case-insensitive substring filter on filenames.</summary>
    public string? NameFilter { get; set; }

    /// <summary>Output HTML path; null writes to temp and opens browser.</summary>
    public string? ReportOutputPath { get; set; }

    /// <summary>HTML document title.</summary>
    public string ReportTitle { get; set; } = "Multi-Snapshot Memory Report";
}

/// <summary>
/// Runs multi-snapshot report generation: query databases, render HTML, write file.
/// </summary>
public static class MultiSnapshotReportRunner
{
    /// <summary>
    /// Builds and writes the multi-snapshot report; returns process exit code (0 = success).
    /// </summary>
    public static int Run(MultiSnapshotReportRunOptions options, IProgressReporter progress)
    {
        if (!Directory.Exists(options.Directory))
        {
            Console.Error.WriteLine($"Directory not found: {options.Directory}");
            return 1;
        }

        progress.Report($"Multi-report: {options.Directory} (filter: {options.NameFilter ?? "(none)"})", force: true);

        var swQuery = Stopwatch.StartNew();
        var model = MultiSnapshotReportBuilder.Build(options.Directory, options.NameFilter, options.ReportTitle);
        swQuery.Stop();

        var swRender = Stopwatch.StartNew();
        var html = MultiSnapshotHtmlRenderer.Render(model);
        swRender.Stop();

        var outPath = options.ReportOutputPath;
        var openBrowser = string.IsNullOrEmpty(outPath);
        if (string.IsNullOrEmpty(outPath))
        {
            outPath = Path.Combine(Path.GetTempPath(), "multi_memsnapshot_" + Guid.NewGuid().ToString("N")[..8] + ".html");
        }
        else
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        var swWrite = Stopwatch.StartNew();
        File.WriteAllText(outPath, html, System.Text.Encoding.UTF8);
        swWrite.Stop();

        progress.Report($"Report written → {outPath}", force: true);
        progress.Report(
            $"Timings: query_ms={swQuery.ElapsedMilliseconds}, render_ms={swRender.ElapsedMilliseconds}, write_ms={swWrite.ElapsedMilliseconds}",
            force: true);

        if (openBrowser)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = new Uri(outPath).AbsoluteUri, UseShellExecute = true });
            }
            catch
            {
                progress.Report($"Could not open browser. Open manually: {outPath}", force: true);
            }
        }

        return 0;
    }
}

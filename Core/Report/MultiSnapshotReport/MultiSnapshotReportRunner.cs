using System.Diagnostics;
using MemorySnapshotDataTools.Report.Queries;

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

    /// <summary>
    /// When true (default), generate a full single-snapshot report for each database so rows can be
    /// previewed in the inline drawer. Set false (<c>--no-reports</c>) for the faster table-only output.
    /// </summary>
    public bool GenerateReports { get; set; } = true;
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

        var (outPath, reportsDir, reportsFolderName, openBrowser) = ResolveOutputLayout(options);

        // Generate one full single-snapshot report per database so rows are clickable in the drawer.
        var generatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
        var swReports = Stopwatch.StartNew();
        var reportLinks = options.GenerateReports
            ? GenerateIndividualReports(model, reportsDir!, reportsFolderName!, generatedAtUtc, progress)
            : new Dictionary<string, string>();
        swReports.Stop();

        var swRender = Stopwatch.StartNew();
        var html = MultiSnapshotHtmlRenderer.Render(model, reportLinks);
        swRender.Stop();

        var swWrite = Stopwatch.StartNew();
        File.WriteAllText(outPath, html, System.Text.Encoding.UTF8);
        swWrite.Stop();

        var totalSnapshots = model.Sessions.Sum(s => s.Snapshots.Count);
        progress.Report($"Report written → {outPath}", force: true);
        progress.Report(
            $"Timings: query_ms={swQuery.ElapsedMilliseconds}, reports_ms={swReports.ElapsedMilliseconds} (reports={reportLinks.Count}/{totalSnapshots}), render_ms={swRender.ElapsedMilliseconds}, write_ms={swWrite.ElapsedMilliseconds}",
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

    /// <summary>
    /// Resolves where the multi-report HTML and the per-snapshot reports are written. With <c>--out</c>,
    /// reports go in a <c>&lt;basename&gt;_reports</c> folder beside it; otherwise a dedicated temp folder
    /// holds <c>index.html</c> plus a <c>reports</c> subfolder (so the drawer's relative iframe links
    /// resolve under one root). When reports are disabled and no <c>--out</c> is given, the original loose
    /// temp file is used.
    /// </summary>
    private static (string OutPath, string? ReportsDir, string? ReportsFolderName, bool OpenBrowser) ResolveOutputLayout(
        MultiSnapshotReportRunOptions options)
    {
        if (!string.IsNullOrEmpty(options.ReportOutputPath))
        {
            var outPath = options.ReportOutputPath;
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            if (!options.GenerateReports)
                return (outPath, null, null, false);

            var folderName = Path.GetFileNameWithoutExtension(outPath) + "_reports";
            var reportsDir = Path.Combine(outDir ?? string.Empty, folderName);
            return (outPath, reportsDir, folderName, false);
        }

        if (!options.GenerateReports)
        {
            // Original behavior: a single loose temp file opened in the browser.
            var loose = Path.Combine(Path.GetTempPath(), "multi_memsnapshot_" + Guid.NewGuid().ToString("N")[..8] + ".html");
            return (loose, null, null, true);
        }

        var rootDir = Path.Combine(Path.GetTempPath(), "multi_memsnapshot_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(rootDir);
        return (Path.Combine(rootDir, "index.html"), Path.Combine(rootDir, "reports"), "reports", true);
    }

    /// <summary>
    /// Builds and writes a full single-snapshot report for each database, returning a map from each
    /// snapshot's database path to the relative href of its report (forward slashes). A database whose
    /// report fails to build is skipped (and simply stays non-clickable), mirroring the per-DB skip in
    /// <see cref="MultiSnapshotReportBuilder"/>.
    /// </summary>
    private static Dictionary<string, string> GenerateIndividualReports(
        MultiSnapshotReportModel model,
        string reportsDir,
        string reportsFolderName,
        string generatedAtUtc,
        IProgressReporter progress)
    {
        Directory.CreateDirectory(reportsDir);

        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = model.Sessions.SelectMany(s => s.Snapshots).ToList();
        var done = 0;

        foreach (var snap in snapshots)
        {
            done++;
            var safeName = UniqueFileName(snap.SnapshotName, usedNames);
            var reportPath = Path.Combine(reportsDir, safeName + ".html");
            try
            {
                using var backend = ReportQueryFactory.Create(snap.DatabasePath);
                var reportModel = ReportBuilder.Build(backend, $"{snap.SnapshotName} — Report", snap.DatabasePath, generatedAtUtc);
                File.WriteAllText(reportPath, ReportRenderer.Render(reportModel), System.Text.Encoding.UTF8);
                links[snap.DatabasePath] = reportsFolderName + "/" + safeName + ".html";
                progress.Report($"Report {done}/{snapshots.Count}: {snap.SnapshotName}");
            }
            catch (Exception ex)
            {
                usedNames.Remove(safeName);
                progress.Report($"Skipping report for {snap.SnapshotName}: {ex.Message}", force: true);
            }
        }

        return links;
    }

    /// <summary>
    /// Produces a filesystem- and URL-safe report filename from a snapshot name (conservative ASCII set),
    /// disambiguating collisions across the batch with a numeric suffix.
    /// </summary>
    private static string UniqueFileName(string snapshotName, HashSet<string> usedNames)
    {
        var chars = snapshotName.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        var baseName = new string(chars).Trim('.', '_');
        if (string.IsNullOrEmpty(baseName))
            baseName = "snapshot";
        if (baseName.Length > 120)
            baseName = baseName[..120];

        var candidate = baseName;
        var suffix = 2;
        while (!usedNames.Add(candidate))
            candidate = $"{baseName}_{suffix++}";
        return candidate;
    }
}

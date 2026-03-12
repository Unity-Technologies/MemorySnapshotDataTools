using System.Diagnostics;
using MemorySnapshotDataTools.Report.Queries;

namespace MemorySnapshotDataTools.Report;

/// <summary>
/// Entry point for report generation: builds a <see cref="ReportModel"/> from the exported database via <see cref="ReportBuilder"/>,
/// renders HTML with <see cref="ReportRenderer"/>, writes to file (or temp + browser), and optionally opens the report in the default browser.
/// </summary>
public static class ReportRunner
{
    /// <summary>
    /// Generates the memory snapshot report: queries the database, builds the model, renders HTML, writes to <see cref="ReportRunOptions.ReportOutputPath"/> (or a temp file), and optionally opens it in the browser.
    /// </summary>
    /// <param name="options">Database path, output path (null = temp + open browser), and report title.</param>
    /// <param name="progress">Progress reporter for status messages.</param>
    /// <returns>Exit code 0 on success.</returns>
    public static int Run(ReportRunOptions options, IProgressReporter progress)
    {
        var generatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";

        progress.Report($"Report: {options.ReportDbPath} -> {options.ReportOutputPath ?? "(temp + browser)"}", force: true);

        using var backend = ReportQueryFactory.Create(options.ReportDbPath);
        progress.Report($"Backend: {backend.Dialect}", force: true);

        var swTotal = Stopwatch.StartNew();
        ReportModel model;

        var swQuery = Stopwatch.StartNew();
        try
        {
            model = ReportBuilder.Build(backend, options.ReportTitle, options.ReportDbPath, generatedAt);
        }
        finally
        {
            swQuery.Stop();
        }

        var swRender = Stopwatch.StartNew();
        var html = ReportRenderer.Render(model);
        swRender.Stop();

        var outPath = options.ReportOutputPath;
        var openBrowser = string.IsNullOrEmpty(outPath);
        if (string.IsNullOrEmpty(outPath))
        {
            outPath = Path.Combine(Path.GetTempPath(), "memsnapshot_report_" + Guid.NewGuid().ToString("N")[..8] + ".html");
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
        swTotal.Stop();

        progress.Report($"Report written → {outPath}", force: true);
        progress.Report(
            $"Timings: query_ms={swQuery.ElapsedMilliseconds}, render_ms={swRender.ElapsedMilliseconds}, write_ms={swWrite.ElapsedMilliseconds}, total_ms={swTotal.ElapsedMilliseconds}", force: true);
        progress.Report($"Report completed in {swTotal.Elapsed.TotalSeconds:F1}s (query {swQuery.Elapsed.TotalSeconds:F1}s, render {swRender.Elapsed.TotalSeconds:F1}s, write {swWrite.Elapsed.TotalSeconds:F1}s)", force: true);

        if (openBrowser)
        {
            try
            {
                var uri = new Uri(outPath);
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            catch
            {
                progress.Report($"Could not open browser. Open manually: {outPath}", force: true);
            }
        }

        return 0;
    }
}

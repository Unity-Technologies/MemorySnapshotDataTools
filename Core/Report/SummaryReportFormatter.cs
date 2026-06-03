using System.Globalization;
using System.Text;

namespace MemorySnapshotDataTools.Report;

/// <summary>
/// Renders a <see cref="SummaryReport"/> as a compact, console-friendly text block: capture metadata,
/// totals, and the Allocated Memory Distribution / Managed Heap Utilization breakdowns.
/// </summary>
public static class SummaryReportFormatter
{
    private const string ResidentUnavailable = "—";

    /// <summary>Formats <paramref name="report"/> into a printable summary (ends with a newline).</summary>
    public static string Format(SummaryReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Memory Usage Summary");
        sb.AppendLine(new string('─', 60));
        AppendMetadata(sb, report);
        sb.AppendLine();

        if (report.SummaryAvailable)
        {
            AppendTotals(sb, report.Metrics);
            sb.AppendLine();
            AppendBreakdown(sb, "Allocated Memory Distribution", report.Metrics.AllocatedMemoryDistribution);
            sb.AppendLine();
            AppendBreakdown(sb, "Managed Heap Utilization", report.Metrics.ManagedHeapUtilization);
        }
        else
        {
            AppendMissingSummary(sb, report);
        }

        if (report.UnityObjectCategories.Count > 0)
        {
            sb.AppendLine();
            AppendUnityObjectCategories(sb, report.UnityObjectCategories);
        }

        return sb.ToString();
    }

    private static void AppendMissingSummary(StringBuilder sb, SummaryReport report)
    {
        var snapshot = string.IsNullOrWhiteSpace(report.Info.SnapshotPath)
            ? "<snapshot.snap>"
            : report.Info.SnapshotPath;

        sb.AppendLine("This database has no summary_metrics table (exported by an older tool version).");
        sb.AppendLine("Re-export the snapshot to populate it, then re-run summary:");
        sb.AppendLine($"  MemorySnapshotDataTools export \"{snapshot}\" \"{report.SourcePath}\"");
        sb.AppendLine($"  MemorySnapshotDataTools summary \"{report.SourcePath}\"");
    }

    private static void AppendMetadata(StringBuilder sb, SummaryReport report)
    {
        var info = report.Info;
        var sourceLabel = report.Source == SummarySource.Snapshot ? "snapshot" : "database";
        AppendField(sb, "Source", $"{Path.GetFileName(report.SourcePath)} ({sourceLabel})");

        if (!string.IsNullOrWhiteSpace(info.ProductName))
            AppendField(sb, "Product", info.ProductName);

        if (!string.IsNullOrWhiteSpace(info.Platform))
            AppendField(sb, "Platform", FormatPlatform(info.Platform));

        if (!string.IsNullOrWhiteSpace(info.UnityVersion))
            AppendField(sb, "Unity", info.UnityVersion);

        if (!string.IsNullOrWhiteSpace(info.RecordDateUtc))
            AppendField(sb, "Captured", info.RecordDateUtc);

        if (info.SnapFormatVersion > 0)
            AppendField(sb, "Snap format", $"v{info.SnapFormatVersion}");

        if (!string.IsNullOrWhiteSpace(report.SchemaVersion))
            AppendField(sb, "Schema", report.SchemaVersion);

        if (info.SessionGuid != 0)
            AppendField(sb, "Session", info.SessionGuid.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendField(StringBuilder sb, string label, string value) =>
        sb.AppendLine($"{label,-12}: {value}");

    private static void AppendTotals(StringBuilder sb, SummaryMetrics metrics)
    {
        sb.AppendLine("Totals");
        sb.AppendLine($"  Total Allocated   {FormatBytes(metrics.TotalAllocatedBytes),12}   ({ExactBytes(metrics.TotalAllocatedBytes)})");
        sb.AppendLine($"  Total Resident    {FormatBytes(metrics.TotalResidentBytes),12}   ({ExactBytes(metrics.TotalResidentBytes)})");
    }

    private static void AppendBreakdown(StringBuilder sb, string title, IReadOnlyList<SummaryCategory> rows)
    {
        sb.AppendLine(title);
        if (rows.Count == 0)
        {
            sb.AppendLine("  (none)");
            return;
        }

        var nameWidth = Math.Max("Category".Length, rows.Max(r => r.Name.Length));
        const int valueWidth = 12;

        sb.AppendLine($"  {"Category".PadRight(nameWidth)}   {"Allocated".PadLeft(valueWidth)}   {"Resident".PadLeft(valueWidth)}");
        sb.AppendLine($"  {new string('─', nameWidth + 3 + valueWidth + 3 + valueWidth)}");

        foreach (var row in rows)
        {
            var allocated = FormatBytes(row.CommittedBytes).PadLeft(valueWidth);
            var resident = (row.ResidentAvailable ? FormatBytes(row.ResidentBytes) : ResidentUnavailable).PadLeft(valueWidth);
            sb.AppendLine($"  {row.Name.PadRight(nameWidth)}   {allocated}   {resident}");
        }
    }

    private static void AppendUnityObjectCategories(StringBuilder sb, IReadOnlyList<UnityObjectCategory> categories)
    {
        sb.AppendLine("Top Unity Object Categories (by native size)");

        ulong total = 0;
        foreach (var category in categories)
            total += category.AllocatedBytes;

        var shown = categories.Count <= Report.UnityObjectCategories.DefaultTopCount
            ? categories
            : categories.Take(Report.UnityObjectCategories.DefaultTopCount).ToList();

        var nameWidth = Math.Max("Type".Length, shown.Max(c => c.TypeName.Length));
        const int countWidth = 9;
        const int sizeWidth = 12;
        const int pctWidth = 6;

        sb.AppendLine($"  {"Type".PadRight(nameWidth)}   {"Objects".PadLeft(countWidth)}   {"Size".PadLeft(sizeWidth)}   {"%".PadLeft(pctWidth)}");
        sb.AppendLine($"  {new string('─', nameWidth + 3 + countWidth + 3 + sizeWidth + 3 + pctWidth)}");

        foreach (var category in shown)
        {
            var count = category.Count.ToString("N0", CultureInfo.InvariantCulture).PadLeft(countWidth);
            var size = FormatBytes(category.AllocatedBytes).PadLeft(sizeWidth);
            var share = total == 0 ? 0 : 100.0 * category.AllocatedBytes / total;
            var pct = (share.ToString("F1", CultureInfo.InvariantCulture) + "%").PadLeft(pctWidth);
            sb.AppendLine($"  {category.TypeName.PadRight(nameWidth)}   {count}   {size}   {pct}");
        }

        var remaining = categories.Count - shown.Count;
        if (remaining > 0)
            sb.AppendLine($"  … and {remaining} more type{(remaining == 1 ? string.Empty : "s")}");
    }

    private static string FormatPlatform(string platform)
    {
        var kind = CapturePlatformKindExtensions.FromPlatformName(platform);
        return kind is CapturePlatformKind.IOS or CapturePlatformKind.Android
            ? $"{kind.ToDisplayLabel()} ({platform})"
            : platform;
    }

    private static string ExactBytes(ulong bytes) => bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";

    private static string FormatBytes(ulong bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        const double tb = gb * 1024;

        if (bytes < kb)
            return $"{bytes} B";
        if (bytes < mb)
            return $"{bytes / kb:F2} KB";
        if (bytes < gb)
            return $"{bytes / mb:F2} MB";
        if (bytes < tb)
            return $"{bytes / gb:F2} GB";
        return $"{bytes / tb:F2} TB";
    }
}

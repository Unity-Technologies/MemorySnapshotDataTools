using System.Globalization;
using System.Text;

namespace MemorySnapshotDataTools.Report;

/// <summary>
/// Helpers for building report HTML: escaping, number/percent formatting, table and KV rendering, insight blocks, and section/group wrappers.
/// </summary>
internal static class ReportHtmlHelper
{
    private static readonly HashSet<string> NumericCols = [
        "obj_count", "edge_count", "root_count", "num_allocations", "inbound_refs", "outbound_refs",
        "duplicate_count", "duplicate_groups", "extra_instances", "total_objects",
        "distinct_types", "objects_with_native_ref", "region_count", "row_count", "log4_bucket",
        "total_orphaned", "total_destroyed", "destroyed_count", "leaked_count", "total_leaked_count"
    ];

    private static readonly HashSet<string> PctCols = [
        "pct_of_total", "pct_of_native_total", "utilization_pct", "overall_utilization_pct"
    ];

    /// <summary>HTML-encodes a value for safe inclusion in the report; null is rendered as styled "null".</summary>
    /// <param name="val">Value to escape (null allowed).</param>
    /// <returns>Encoded string or null placeholder.</returns>
    public static string Escape(object? val)
    {
        if (val == null) return "<em style='color:#bbb'>null</em>";
        return System.Net.WebUtility.HtmlEncode(val.ToString() ?? "");
    }

    /// <summary>Formats a value as a number (N0 for integers, N2 for decimals); NaN/infinity and null are escaped.</summary>
    /// <param name="val">Value to format.</param>
    /// <returns>Formatted string or escaped placeholder.</returns>
    public static string FmtNum(object? val)
    {
        if (val == null) return Escape(val);
        if (val is int i) return i.ToString("N0", CultureInfo.InvariantCulture);
        if (val is long l) return l.ToString("N0", CultureInfo.InvariantCulture);
        if (val is double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return Escape(val);
            if (d == Math.Truncate(d) && Math.Abs(d) < 1e15)
                return ((long)d).ToString("N0", CultureInfo.InvariantCulture);
            return d.ToString("N2", CultureInfo.InvariantCulture);
        }
        if (val is float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return Escape(val);
            return f.ToString("N2", CultureInfo.InvariantCulture);
        }
        if (val is decimal m) return m.ToString("N2", CultureInfo.InvariantCulture);
        return Escape(val);
    }

    /// <summary>Returns true if the column name is treated as numeric (right-aligned, N0/N2 formatting).</summary>
    /// <param name="col">Column name (case-insensitive).</param>
    /// <returns>True if numeric.</returns>
    public static bool IsNumericCol(string col)
    {
        var lower = col.ToLowerInvariant();
        if (NumericCols.Contains(lower) || PctCols.Contains(lower)) return true;
        return lower.EndsWith("_mb", StringComparison.Ordinal) || lower.EndsWith("_gb", StringComparison.Ordinal) ||
               lower.EndsWith("_kb", StringComparison.Ordinal) || lower.EndsWith("_count", StringComparison.Ordinal);
    }

    /// <summary>Returns true if the column is displayed as a percentage (suffix %).</summary>
    /// <param name="col">Column name (case-insensitive).</param>
    /// <returns>True if percentage column.</returns>
    public static bool IsPctCol(string col) =>
        PctCols.Contains(col.ToLowerInvariant()) || col.ToLowerInvariant().EndsWith("_pct", StringComparison.Ordinal);

    /// <summary>Formats a cell value for the given column (percent, number, or escaped text).</summary>
    /// <param name="col">Column name (determines format).</param>
    /// <param name="val">Cell value.</param>
    /// <returns>HTML-safe formatted string.</returns>
    public static string FmtCell(string col, object? val)
    {
        if (val == null) return "<em style='color:#bbb'>null</em>";
        if (IsPctCol(col) && TryDouble(val, out var pct)) return pct.ToString("N1", CultureInfo.InvariantCulture) + "%";
        if (IsNumericCol(col)) return FmtNum(val);
        return Escape(val);
    }

    private static bool TryDouble(object? o, out double d)
    {
        d = 0;
        if (o == null) return false;
        if (o is double x) { d = x; return true; }
        if (o is float f) { d = f; return true; }
        if (o is decimal m) { d = (double)m; return true; }
        if (o is int i) { d = i; return true; }
        if (o is long l) { d = l; return true; }
        return double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d);
    }

    /// <summary>Renders a sortable HTML table from column names and row arrays; optional warn column and truncation set.</summary>
    /// <param name="columns">Column headers.</param>
    /// <param name="rows">Rows of cell values (length may vary per row).</param>
    /// <param name="warnCol">If set, cells in this column with value &gt; 0 get a warning style.</param>
    /// <param name="truncateCols">Column names to truncate with ellipsis and title=full value.</param>
    /// <returns>HTML fragment (table wrapped in div).</returns>
    public static string RenderTable(string[] columns, List<object?[]> rows, string? warnCol = null, IReadOnlySet<string>? truncateCols = null)
    {
        if (rows.Count == 0)
            return "<p class=\"empty\">No data available for this section.</p>";

        var sb = new StringBuilder();
        sb.Append("<div class=\"table-wrap\"><table class=\"sortable\"><thead><tr>");
        foreach (var c in columns)
        {
            var numClass = IsNumericCol(c) ? " num" : "";
            sb.Append($"<th class=\"{numClass.TrimStart()}\">{Escape(c)}</th>");
        }
        sb.Append("</tr></thead><tbody>");

        foreach (var row in rows)
        {
            sb.Append("<tr>");
            for (var i = 0; i < columns.Length; i++)
            {
                var col = columns[i];
                var val = i < row.Length ? row[i] : null;
                var isNum = IsNumericCol(col);
                var isTrunc = truncateCols != null && truncateCols.Contains(col);
                var isWarn = warnCol == col && val != null && TryDouble(val, out var v) && v > 0;
                var classes = new List<string>();
                if (isNum) classes.Add("num");
                if (isTrunc) classes.Add("trunc");
                if (isWarn) classes.Add("warn");
                var cls = classes.Count > 0 ? " class=\"" + string.Join(" ", classes) + "\"" : "";
                var title = isTrunc && val != null ? " title=\"" + Escape(val) + "\"" : "";
                sb.Append($"<td{cls}{title}>{FmtCell(col, val)}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    /// <summary>Renders a key-value grid (e.g. snapshot path, version, generated date).</summary>
    /// <param name="kv">Label-to-value map.</param>
    /// <returns>HTML fragment (kv-grid div).</returns>
    public static string RenderKv(IReadOnlyDictionary<string, object?> kv)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"kv-grid\">");
        foreach (var (label, value) in kv)
        {
            var mono = label.Contains("path", StringComparison.OrdinalIgnoreCase) || label.Contains("version", StringComparison.OrdinalIgnoreCase) || label.Contains("date", StringComparison.OrdinalIgnoreCase);
            var cls = mono ? "kv-value mono" : "kv-value";
            var display = value is int or long or double or float or decimal ? FmtNum(value) : Escape(value);
            sb.Append($"<div><div class=\"kv-label\">{Escape(label)}</div><div class=\"{cls}\">{display}</div></div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Renders an insight block: paragraph plus optional stat pills (label, value, tone class e.g. "warn" or "good").</summary>
    /// <param name="text">Main text (may contain HTML).</param>
    /// <param name="pills">Optional list of (label, value, tone) for pill display.</param>
    /// <returns>HTML fragment (insight div).</returns>
    public static string RenderInsight(string text, List<(string Label, string Value, string Tone)>? pills = null)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"insight\"><p>").Append(text).Append("</p>");
        if (pills != null && pills.Count > 0)
        {
            sb.Append("<div class=\"stat-pills\">");
            foreach (var (label, value, tone) in pills)
            {
                var toneClass = string.IsNullOrEmpty(tone) ? "" : " " + tone;
                sb.Append($"<div class=\"pill{toneClass}\"><div class=\"pill-label\">{Escape(label)}</div><div class=\"pill-value\">{Escape(value)}</div></div>");
            }
            sb.Append("</div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Wraps content in a section div with id, title, optional row-count badge.</summary>
    /// <param name="anchor">Id for the section.</param>
    /// <param name="title">Section title.</param>
    /// <param name="content">Inner HTML.</param>
    /// <param name="rowCount">If set, shows "N rows" badge.</param>
    /// <returns>HTML fragment.</returns>
    public static string Section(string anchor, string title, string content, int? rowCount = null)
    {
        var badge = rowCount.HasValue ? $"<span class=\"badge\">{FmtNum(rowCount.Value)} rows</span>" : "";
        return $"<div class=\"section\" id=\"{Escape(anchor)}\"><div class=\"section-header\"><h3 class=\"section-title\">{Escape(title)}</h3>{badge}</div>{content}</div>";
    }

    /// <summary>Wraps inner HTML in a group div with title and description.</summary>
    /// <param name="groupTitle">Group heading.</param>
    /// <param name="groupDesc">Optional description.</param>
    /// <param name="innerHtml">Inner HTML (sections).</param>
    /// <returns>HTML fragment.</returns>
    public static string Group(string groupTitle, string groupDesc, string innerHtml) =>
        $"<div class=\"group\"><div class=\"group-header\"><h2>{Escape(groupTitle)}</h2><span class=\"group-desc\">{Escape(groupDesc)}</span></div>{innerHtml}</div>";
}

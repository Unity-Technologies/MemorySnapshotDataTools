using System.Globalization;
using System.Text;
using MemorySnapshotDataTools.Report;

namespace MemorySnapshotDataTools.Report.MultiSnapshotReport;

/// <summary>
/// Renders a <see cref="MultiSnapshotReportModel"/> to a self-contained HTML document with one session-grouped table.
/// </summary>
public static class MultiSnapshotHtmlRenderer
{
    private const int ColSnapshot = 0;
    private const int ColAbCount = 1;
    private const int ColAbAlloc = 2;
    private const int ColAbRes = 3;
    private const int ColSfCount = 4;
    private const int ColSfAlloc = 5;
    private const int ColSfRes = 6;
    private const int ColPmrAlloc = 7;
    private const int ColPmrRes = 8;
    private const int ColumnCount = 9;

    /// <summary>
    /// Builds the full HTML report string from the model.
    /// </summary>
    public static string Render(MultiSnapshotReportModel model)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>
            """);
        sb.Append(Escape(model.Title));
        sb.Append("""
            </title>
            <style>
            """);
        sb.Append(Css);
        sb.Append("""
            </style>
            </head>
            <body>
            <main>
            <h1>
            """);
        sb.Append(Escape(model.Title));
        sb.Append("</h1>\n<p class=\"subtitle\">");
        sb.Append(Escape(model.SourceDirectory));
        sb.Append(" · Generated ");
        sb.Append(Escape(model.GeneratedAtUtc));
        sb.Append(" · ");
        sb.Append(model.Sessions.Sum(s => s.Snapshots.Count).ToString(CultureInfo.InvariantCulture));
        sb.Append(" snapshots</p>\n");

        if (model.Sessions.Count == 0)
            sb.Append("<p class=\"empty\">No matching database files found.</p>");
        else
            sb.Append(RenderUnifiedTable(model));

        sb.Append("""
            </main>
            <script>
            """);
        sb.Append(SortableScript);
        sb.Append("""
            </script>
            </body>
            </html>
            """);
        return sb.ToString();
    }

    private static string RenderUnifiedTable(MultiSnapshotReportModel model)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <div class="table-wrap"><table class="multi-snapshot sortable">
            <thead>
            <tr>
            <th rowspan="2" class="col-snapshot" data-col="0">Snapshot</th>
            <th colspan="3" class="group-hdr">Asset Bundle</th>
            <th colspan="3" class="group-hdr">Serialized File</th>
            <th colspan="2" class="group-hdr">PMR</th>
            </tr>
            <tr>
            <th class="num sub" data-col="1">Count</th>
            <th class="num sub" data-col="2">Allocated</th>
            <th class="num sub" data-col="3">Resident</th>
            <th class="num sub" data-col="4">Count</th>
            <th class="num sub" data-col="5">Allocated</th>
            <th class="num sub" data-col="6">Resident</th>
            <th class="num sub" data-col="7">Allocated</th>
            <th class="num sub" data-col="8">Resident</th>
            </tr>
            </thead>
            <tbody>
            """);

        foreach (var session in model.Sessions)
        {
            sb.Append("<tr class=\"session-header\" id=\"");
            sb.Append(Escape(MultiSnapshotSessionKey.ToHtmlId(session.SessionKey)));
            sb.Append("\"><td colspan=\"");
            sb.Append(ColumnCount.ToString(CultureInfo.InvariantCulture));
            sb.Append("\">");
            sb.Append(PlatformIconHtml.Render(session.PlatformKind));
            sb.Append(' ');
            sb.Append(Escape(session.DisplayTitle));
            sb.Append(" <span class=\"session-count\">(");
            sb.Append(session.Snapshots.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(" snapshot");
            if (session.Snapshots.Count != 1)
                sb.Append('s');
            sb.Append(")</span></td></tr>\n");

            foreach (var snap in session.Snapshots)
            {
                var ab = GetTypeMetrics(snap, "AssetBundle");
                var sf = GetTypeMetrics(snap, "SerializedFile");
                var pmr = AggregateRemapper(snap);

                sb.Append("<tr class=\"snapshot-row\">");
                AppendSnapshotCell(sb, snap);
                AppendCountCell(sb, ColAbCount, ab.Count);
                AppendBytesCell(sb, ColAbAlloc, ab.AllocatedBytes);
                AppendBytesCell(sb, ColAbRes, ab.ResidentBytes);
                AppendCountCell(sb, ColSfCount, sf.Count);
                AppendBytesCell(sb, ColSfAlloc, sf.AllocatedBytes);
                AppendBytesCell(sb, ColSfRes, sf.ResidentBytes);
                AppendBytesCell(sb, ColPmrAlloc, pmr.AllocatedBytes);
                AppendBytesCell(sb, ColPmrRes, pmr.ResidentBytes);
                sb.Append("</tr>\n");
            }
        }

        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    private static void AppendSnapshotCell(StringBuilder sb, SnapshotMetricsRow snap)
    {
        sb.Append("<td class=\"snapshot-name\" data-col=\"");
        sb.Append(ColSnapshot.ToString(CultureInfo.InvariantCulture));
        sb.Append("\" data-sort=\"");
        sb.Append(EscapeAttr(snap.SnapshotName));
        sb.Append("\"><span class=\"snapshot-label\">");
        sb.Append(PlatformIconHtml.Render(snap.PlatformKind, snap.Platform));
        sb.Append("<span class=\"snapshot-filename\"");
        if (!string.IsNullOrWhiteSpace(snap.SchemaVersion))
        {
            sb.Append(" title=\"Schema ");
            sb.Append(EscapeAttr(snap.SchemaVersion));
            sb.Append('"');
        }
        sb.Append('>');
        sb.Append(Escape(snap.SnapshotName));
        sb.Append("</span>");
        if (!snap.SchemaUpToDate && !string.IsNullOrWhiteSpace(snap.SchemaVersion))
        {
            sb.Append("<span class=\"schema-warn\" title=\"Schema ");
            sb.Append(EscapeAttr(snap.SchemaVersion));
            sb.Append("\"> ⚠</span>");
        }
        sb.Append("</span></td>");
    }

    private static void AppendCountCell(StringBuilder sb, int col, int count)
    {
        sb.Append("<td class=\"num\" data-col=\"");
        sb.Append(col.ToString(CultureInfo.InvariantCulture));
        sb.Append("\" data-sort=\"");
        sb.Append(count.ToString(CultureInfo.InvariantCulture));
        sb.Append("\">");
        sb.Append(count.ToString("N0", CultureInfo.InvariantCulture));
        sb.Append("</td>");
    }

    private static void AppendBytesCell(StringBuilder sb, int col, long bytes)
    {
        sb.Append("<td class=\"num\" data-col=\"");
        sb.Append(col.ToString(CultureInfo.InvariantCulture));
        sb.Append("\" data-sort=\"");
        sb.Append(bytes.ToString(CultureInfo.InvariantCulture));
        sb.Append("\">");
        sb.Append(ReportHtmlHelper.FmtBytesHtml(bytes));
        sb.Append("</td>");
    }

    private static void AppendBytesCell(StringBuilder sb, int col, long? bytes)
    {
        sb.Append("<td class=\"num\" data-col=\"");
        sb.Append(col.ToString(CultureInfo.InvariantCulture));
        sb.Append('\"');
        if (bytes.HasValue)
        {
            sb.Append(" data-sort=\"");
            sb.Append(bytes.Value.ToString(CultureInfo.InvariantCulture));
            sb.Append("\">");
            sb.Append(ReportHtmlHelper.FmtBytesHtml(bytes.Value));
        }
        else
        {
            sb.Append("><em class=\"na\">N/A</em>");
        }

        sb.Append("</td>");
    }

    private static NativeTypeSnapshotMetrics GetTypeMetrics(SnapshotMetricsRow snap, string typeName) =>
        snap.NativeTypes.TryGetValue(typeName, out var m)
            ? m
            : new NativeTypeSnapshotMetrics { NativeTypeName = typeName };

    private static NativeRootSnapshotMetrics AggregateRemapper(SnapshotMetricsRow snap)
    {
        if (snap.RemapperRoots.Count == 0)
            return new NativeRootSnapshotMetrics();

        long alloc = 0;
        long? resident = 0;
        var hasResident = true;
        foreach (var root in snap.RemapperRoots)
        {
            alloc += root.AllocatedBytes;
            if (root.ResidentBytes.HasValue)
                resident = (resident ?? 0) + root.ResidentBytes.Value;
            else
                hasResident = false;
        }

        return new NativeRootSnapshotMetrics
        {
            AllocatedBytes = alloc,
            ResidentBytes = hasResident ? resident : null,
        };
    }

    private static string Escape(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EscapeAttr(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private const string SortableScript = """
        document.querySelectorAll('table.multi-snapshot th.sub[data-col]').forEach(function(th) {
            th.style.cursor = 'pointer';
            th.addEventListener('click', function() {
                var table = th.closest('table');
                var col = th.getAttribute('data-col');
                var tbody = table.querySelector('tbody');
                var dir = th.dataset.sortDir === 'asc' ? -1 : 1;
                table.querySelectorAll('th.sub[data-col]').forEach(function(h) { delete h.dataset.sortDir; });
                th.dataset.sortDir = dir === 1 ? 'asc' : 'desc';
                var blocks = [];
                var current = null;
                tbody.querySelectorAll('tr').forEach(function(tr) {
                    if (tr.classList.contains('session-header')) {
                        current = { header: tr, rows: [] };
                        blocks.push(current);
                    } else if (tr.classList.contains('snapshot-row') && current) {
                        current.rows.push(tr);
                    }
                });
                blocks.forEach(function(block) {
                    block.rows.sort(function(a, b) {
                        var ac = a.querySelector('td[data-col="' + col + '"]');
                        var bc = b.querySelector('td[data-col="' + col + '"]');
                        var av = ac ? (ac.dataset.sort || ac.textContent.trim()) : '';
                        var bv = bc ? (bc.dataset.sort || bc.textContent.trim()) : '';
                        var an = parseFloat(String(av).replace(/,/g, ''));
                        var bn = parseFloat(String(bv).replace(/,/g, ''));
                        if (!isNaN(an) && !isNaN(bn)) return dir * (an - bn);
                        return dir * String(av).localeCompare(String(bv));
                    });
                    tbody.appendChild(block.header);
                    block.rows.forEach(function(r) { tbody.appendChild(r); });
                });
            });
        });
        """;

    private static readonly string Css = """
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; font-size: 13px; background: #f0f2f5; color: #1a1a2e; padding: 24px; line-height: 1.5; }
        main { max-width: 100%; margin: 0 auto; }
        h1 { font-size: 22px; font-weight: 700; margin-bottom: 4px; }
        .subtitle { font-size: 12px; color: #666; margin-bottom: 24px; font-family: "SF Mono", Consolas, monospace; word-break: break-all; }
        .table-wrap { background: #fff; border-radius: 8px; box-shadow: 0 1px 4px rgba(0,0,0,.08); overflow-x: auto; overflow-y: visible; }
        table.multi-snapshot { width: 100%; border-collapse: separate; border-spacing: 0; table-layout: auto; --header-row1-h: 37px; }
        thead th {
            position: sticky;
            background: #1a1a2e;
            color: #fff;
            font-size: 11px;
            font-weight: 600;
            padding: 8px 10px;
            text-align: left;
            white-space: nowrap;
            border: 1px solid #2d2d44;
            box-shadow: 0 1px 0 #2d2d44;
        }
        thead tr:first-child th { top: 0; z-index: 3; }
        thead tr:nth-child(2) th { top: var(--header-row1-h); z-index: 2; }
        thead th.col-snapshot { vertical-align: bottom; z-index: 4; }
        thead th.group-hdr { text-align: center; text-transform: uppercase; letter-spacing: 0.03em; }
        thead th.sub { text-transform: uppercase; font-size: 10px; font-weight: 500; }
        thead th.num, td.num { text-align: right; }
        tbody tr.session-header td { background: #e8ecf4; font-weight: 600; font-size: 12px; padding: 10px 12px; border-top: 2px solid #c5cee0; border-bottom: 1px solid #c5cee0; }
        tbody tr.session-header:first-child td { border-top: none; }
        .session-count { font-weight: 400; color: #666; }
        tbody tr.snapshot-row:nth-child(even) { background: #f8f9fb; }
        tbody tr.snapshot-row:hover { background: #eef2ff; }
        td { padding: 6px 10px; border-bottom: 1px solid #f0f2f5; vertical-align: top; }
        .platform-icon { display: inline-flex; align-items: center; margin-right: 6px; vertical-align: middle; color: #475569; }
        .platform-icon.ios { color: #1a1a2e; }
        .platform-icon.android { color: #3ddc84; }
        td.snapshot-name { font-size: 11px; white-space: nowrap; min-width: 280px; }
        .snapshot-label { display: inline-flex; align-items: center; gap: 4px; }
        .snapshot-filename { font-family: "SF Mono", Consolas, monospace; }
        td.num { font-variant-numeric: tabular-nums; font-family: "SF Mono", Consolas, monospace; font-size: 12px; white-space: nowrap; }
        .bytes { border-bottom: 1px dotted #94a3b8; cursor: help; }
        em.na { color: #999; font-style: italic; }
        .empty { color: #999; font-style: italic; padding: 24px; }
        """;
}

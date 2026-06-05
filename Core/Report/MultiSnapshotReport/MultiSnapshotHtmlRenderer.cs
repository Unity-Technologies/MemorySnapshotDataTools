using System.Globalization;
using System.Text;
using MemorySnapshotDataTools.Report;

namespace MemorySnapshotDataTools.Report.MultiSnapshotReport;

/// <summary>
/// Renders a <see cref="MultiSnapshotReportModel"/> to a self-contained HTML document with one
/// session-grouped table. The table is driven by a column-descriptor list (<see cref="ColumnDef"/>) so
/// columns — including the high-level Allocated Memory Distribution summary columns — can be toggled on
/// and off, and each snapshot row can open its full single-snapshot report in an inline iframe drawer.
/// </summary>
public static class MultiSnapshotHtmlRenderer
{
    // Allocated Memory Distribution category names, matching SummaryMetricsCalculator. Graphics and
    // Untracked carry no resident value (ResidentAvailable=false) so their resident cells show N/A.
    private const string CatNative = "Native";
    private const string CatManaged = "Managed";
    private const string CatExecutables = "Executables & Mapped";
    private const string CatGraphics = "Graphics (Estimated)";
    private const string CatUntracked = "Untracked";
    private const string CatAndroidRuntime = "Android Runtime";

    // data-group keys (used for column visibility toggling).
    private const string GroupAssetBundle = "ab";
    private const string GroupSerializedFile = "sf";
    private const string GroupPmr = "pmr";
    private const string GroupResidentSummary = "sumRes";
    private const string GroupCommittedSummary = "sumCom";

    /// <summary>One renderable value: a number (count or bytes) or null (rendered as N/A).</summary>
    private readonly record struct CellValue(long? Number, bool IsCount);

    /// <summary>Describes one data column: its group, headers, default visibility, and value selector.</summary>
    private sealed record ColumnDef(
        string Group,
        string GroupHeader,
        string SubHeader,
        bool DefaultVisible,
        Func<SnapshotMetricsRow, CellValue> Value);

    /// <summary>Builds the full HTML report string from the model (no per-snapshot report links).</summary>
    public static string Render(MultiSnapshotReportModel model) => Render(model, null);

    /// <summary>
    /// Builds the full HTML report string. When <paramref name="reportLinks"/> maps a snapshot's
    /// <see cref="SnapshotMetricsRow.DatabasePath"/> to a relative report href, that row becomes
    /// clickable and opens the report in the inline drawer.
    /// </summary>
    public static string Render(MultiSnapshotReportModel model, IReadOnlyDictionary<string, string>? reportLinks)
    {
        var columns = BuildColumns(model);

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
        {
            sb.Append("<p class=\"empty\">No matching database files found.</p>");
        }
        else
        {
            sb.Append(RenderToggleBar(columns));
            sb.Append(RenderUnifiedTable(model, columns, reportLinks));
            sb.Append(DrawerHtml);
        }

        sb.Append("""
            </main>
            <script>
            """);
        sb.Append(SortableScript);
        sb.Append('\n');
        sb.Append(InteractiveScript);
        sb.Append("""
            </script>
            </body>
            </html>
            """);
        return sb.ToString();
    }

    /// <summary>Builds the ordered column descriptors; the Android Runtime columns appear only when present.</summary>
    private static List<ColumnDef> BuildColumns(MultiSnapshotReportModel model)
    {
        var hasAndroidRuntime = model.Sessions
            .SelectMany(s => s.Snapshots)
            .Any(snap => snap.Summary?.AllocatedMemoryDistribution
                .Any(c => string.Equals(c.Name, CatAndroidRuntime, StringComparison.Ordinal)) == true);

        var cols = new List<ColumnDef>
        {
            new(GroupAssetBundle, "Asset Bundle", "Count", true, r => Count(GetTypeMetrics(r, "AssetBundle").Count)),
            new(GroupAssetBundle, "Asset Bundle", "Allocated", true, r => Bytes(GetTypeMetrics(r, "AssetBundle").AllocatedBytes)),
            new(GroupAssetBundle, "Asset Bundle", "Resident", true, r => Bytes(GetTypeMetrics(r, "AssetBundle").ResidentBytes)),
            new(GroupSerializedFile, "Serialized File", "Count", true, r => Count(GetTypeMetrics(r, "SerializedFile").Count)),
            new(GroupSerializedFile, "Serialized File", "Allocated", true, r => Bytes(GetTypeMetrics(r, "SerializedFile").AllocatedBytes)),
            new(GroupSerializedFile, "Serialized File", "Resident", true, r => Bytes(GetTypeMetrics(r, "SerializedFile").ResidentBytes)),
            new(GroupPmr, "PMR", "Allocated", true, r => Bytes(AggregateRemapper(r).AllocatedBytes)),
            new(GroupPmr, "PMR", "Resident", true, r => Bytes(AggregateRemapper(r).ResidentBytes)),

            new(GroupResidentSummary, "Resident Memory", "Total", true, r => Bytes(TotalResident(r))),
            new(GroupResidentSummary, "Resident Memory", "Native", true, r => Bytes(ResidentOf(r, CatNative))),
            new(GroupResidentSummary, "Resident Memory", "Managed", true, r => Bytes(ResidentOf(r, CatManaged))),
            new(GroupResidentSummary, "Resident Memory", "Exec & Mapped", true, r => Bytes(ResidentOf(r, CatExecutables))),
            new(GroupResidentSummary, "Resident Memory", "Graphics", true, r => Bytes(ResidentOf(r, CatGraphics))),
            new(GroupResidentSummary, "Resident Memory", "Untracked", true, r => Bytes(ResidentOf(r, CatUntracked))),
        };
        if (hasAndroidRuntime)
            cols.Add(new(GroupResidentSummary, "Resident Memory", "Android RT", true, r => Bytes(ResidentOf(r, CatAndroidRuntime))));

        cols.AddRange(new ColumnDef[]
        {
            new(GroupCommittedSummary, "Committed Memory", "Total", false, r => Bytes(TotalCommitted(r))),
            new(GroupCommittedSummary, "Committed Memory", "Native", false, r => Bytes(CommittedOf(r, CatNative))),
            new(GroupCommittedSummary, "Committed Memory", "Managed", false, r => Bytes(CommittedOf(r, CatManaged))),
            new(GroupCommittedSummary, "Committed Memory", "Exec & Mapped", false, r => Bytes(CommittedOf(r, CatExecutables))),
            new(GroupCommittedSummary, "Committed Memory", "Graphics", false, r => Bytes(CommittedOf(r, CatGraphics))),
            new(GroupCommittedSummary, "Committed Memory", "Untracked", false, r => Bytes(CommittedOf(r, CatUntracked))),
        });
        if (hasAndroidRuntime)
            cols.Add(new(GroupCommittedSummary, "Committed Memory", "Android RT", false, r => Bytes(CommittedOf(r, CatAndroidRuntime))));

        return cols;
    }

    private static string RenderToggleBar(List<ColumnDef> columns)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"col-toggles\"><span class=\"toggle-label\">Columns:</span>");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var col in columns)
        {
            if (!seen.Add(col.Group))
                continue;
            sb.Append("<label><input type=\"checkbox\" data-group=\"");
            sb.Append(EscapeAttr(col.Group));
            sb.Append('"');
            if (col.DefaultVisible)
                sb.Append(" checked");
            sb.Append("> ");
            sb.Append(Escape(col.GroupHeader));
            sb.Append("</label>");
        }
        sb.Append("</div>\n");
        return sb.ToString();
    }

    private static string RenderUnifiedTable(
        MultiSnapshotReportModel model,
        List<ColumnDef> columns,
        IReadOnlyDictionary<string, string>? reportLinks)
    {
        var initialVisibleLeaves = columns.Count(c => c.DefaultVisible) + 1; // +1 for the snapshot column

        var sb = new StringBuilder();
        sb.Append("<div class=\"table-wrap\"><table class=\"multi-snapshot sortable\">\n<thead>\n<tr>\n");
        sb.Append("<th rowspan=\"2\" class=\"col-snapshot\" data-col=\"0\">Snapshot</th>\n");

        // Group-header row: coalesce consecutive same-group columns; colspan counts visible columns only.
        var i = 0;
        while (i < columns.Count)
        {
            var group = columns[i].Group;
            var header = columns[i].GroupHeader;
            var visible = 0;
            var j = i;
            while (j < columns.Count && columns[j].Group == group)
            {
                if (columns[j].DefaultVisible)
                    visible++;
                j++;
            }

            sb.Append("<th class=\"group-hdr");
            if (visible == 0)
                sb.Append(" col-hidden");
            sb.Append("\" data-group=\"");
            sb.Append(EscapeAttr(group));
            sb.Append("\" colspan=\"");
            sb.Append(Math.Max(visible, 1).ToString(CultureInfo.InvariantCulture));
            sb.Append("\">");
            sb.Append(Escape(header));
            sb.Append("</th>\n");
            i = j;
        }
        sb.Append("</tr>\n<tr>\n");

        // Sub-header row: one sortable cell per column.
        for (var c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            sb.Append("<th class=\"num sub");
            if (!col.DefaultVisible)
                sb.Append(" col-hidden");
            sb.Append("\" data-col=\"");
            sb.Append((c + 1).ToString(CultureInfo.InvariantCulture));
            sb.Append("\" data-group=\"");
            sb.Append(EscapeAttr(col.Group));
            sb.Append("\">");
            sb.Append(Escape(col.SubHeader));
            sb.Append("</th>\n");
        }
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var session in model.Sessions)
        {
            sb.Append("<tr class=\"session-header\" id=\"");
            sb.Append(Escape(MultiSnapshotSessionKey.ToHtmlId(session.SessionKey)));
            sb.Append("\"><td colspan=\"");
            sb.Append(initialVisibleLeaves.ToString(CultureInfo.InvariantCulture));
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
                var hasReport = reportLinks != null
                    && reportLinks.TryGetValue(snap.DatabasePath, out var href)
                    && !string.IsNullOrEmpty(href);

                sb.Append("<tr class=\"snapshot-row");
                if (hasReport)
                    sb.Append(" has-report");
                sb.Append('"');
                if (hasReport)
                {
                    sb.Append(" data-report=\"");
                    sb.Append(EscapeAttr(reportLinks![snap.DatabasePath]));
                    sb.Append('"');
                }
                sb.Append('>');

                AppendSnapshotCell(sb, snap, hasReport);
                for (var c = 0; c < columns.Count; c++)
                    AppendDataCell(sb, c + 1, columns[c], columns[c].Value(snap));
                sb.Append("</tr>\n");
            }
        }

        sb.Append("</tbody></table></div>\n");
        return sb.ToString();
    }

    private static void AppendSnapshotCell(StringBuilder sb, SnapshotMetricsRow snap, bool hasReport)
    {
        sb.Append("<td class=\"snapshot-name\" data-col=\"0\" data-sort=\"");
        sb.Append(EscapeAttr(snap.SnapshotName));
        sb.Append("\"><span class=\"snapshot-label\">");
        if (hasReport)
            sb.Append("<span class=\"report-chevron\" title=\"Click to preview the full report\">▸</span>");
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

    private static void AppendDataCell(StringBuilder sb, int colId, ColumnDef col, CellValue value)
    {
        sb.Append("<td class=\"num");
        if (!col.DefaultVisible)
            sb.Append(" col-hidden");
        sb.Append("\" data-col=\"");
        sb.Append(colId.ToString(CultureInfo.InvariantCulture));
        sb.Append("\" data-group=\"");
        sb.Append(EscapeAttr(col.Group));
        sb.Append('"');

        if (value.IsCount)
        {
            var n = value.Number ?? 0;
            sb.Append(" data-sort=\"");
            sb.Append(n.ToString(CultureInfo.InvariantCulture));
            sb.Append("\">");
            sb.Append(n.ToString("N0", CultureInfo.InvariantCulture));
        }
        else if (value.Number.HasValue)
        {
            sb.Append(" data-sort=\"");
            sb.Append(value.Number.Value.ToString(CultureInfo.InvariantCulture));
            sb.Append("\">");
            sb.Append(ReportHtmlHelper.FmtBytesHtml(value.Number.Value));
        }
        else
        {
            sb.Append("><em class=\"na\">N/A</em>");
        }

        sb.Append("</td>");
    }

    private static CellValue Count(int count) => new(count, true);

    private static CellValue Bytes(long bytes) => new(bytes, false);

    private static CellValue Bytes(long? bytes) => new(bytes, false);

    private static long? TotalResident(SnapshotMetricsRow snap) =>
        snap.Summary == null ? null : ClampToLong(snap.Summary.TotalResidentBytes);

    private static long? TotalCommitted(SnapshotMetricsRow snap) =>
        snap.Summary == null ? null : ClampToLong(snap.Summary.TotalAllocatedBytes);

    private static long? ResidentOf(SnapshotMetricsRow snap, string categoryName)
    {
        var category = FindCategory(snap, categoryName);
        if (category == null || !category.ResidentAvailable)
            return null;
        return ClampToLong(category.ResidentBytes);
    }

    private static long? CommittedOf(SnapshotMetricsRow snap, string categoryName)
    {
        var category = FindCategory(snap, categoryName);
        return category == null ? null : ClampToLong(category.CommittedBytes);
    }

    private static SummaryCategory? FindCategory(SnapshotMetricsRow snap, string categoryName) =>
        snap.Summary?.AllocatedMemoryDistribution
            .FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.Ordinal));

    private static long ClampToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

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

    private const string DrawerHtml = """
        <div id="report-drawer" class="report-drawer" aria-hidden="true">
        <div class="report-drawer-backdrop"></div>
        <div class="report-drawer-panel" role="dialog" aria-label="Snapshot report">
        <div class="report-drawer-header">
        <span class="report-drawer-title">Snapshot report</span>
        <a class="report-popout" href="#" target="_blank" rel="noopener">Open in new tab ↗</a>
        <button class="report-drawer-close" type="button" aria-label="Close report">×</button>
        </div>
        <iframe class="report-drawer-iframe" title="Snapshot report"></iframe>
        </div>
        </div>

        """;

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

    private const string InteractiveScript = """
        function recomputeColspans() {
            var table = document.querySelector('table.multi-snapshot');
            if (!table) return;
            table.querySelectorAll('thead th.group-hdr').forEach(function(gh) {
                var g = gh.getAttribute('data-group');
                var n = table.querySelectorAll('th.sub[data-group="' + g + '"]:not(.col-hidden)').length;
                if (n > 0) { gh.colSpan = n; gh.classList.remove('col-hidden'); }
                else { gh.classList.add('col-hidden'); }
            });
            var leaves = table.querySelectorAll('th.sub:not(.col-hidden)').length + 1;
            table.querySelectorAll('tr.session-header > td').forEach(function(td) { td.colSpan = leaves; });
        }
        function setGroupVisible(group, visible) {
            document.querySelectorAll('td[data-group="' + group + '"], th.sub[data-group="' + group + '"]').forEach(function(el) {
                el.classList.toggle('col-hidden', !visible);
            });
            recomputeColspans();
        }
        (function() {
            document.querySelectorAll('.col-toggles input[type=checkbox]').forEach(function(cb) {
                cb.addEventListener('change', function() { setGroupVisible(cb.getAttribute('data-group'), cb.checked); });
            });
            recomputeColspans();

            var drawer = document.getElementById('report-drawer');
            if (!drawer) return;
            var iframe = drawer.querySelector('.report-drawer-iframe');
            var popout = drawer.querySelector('.report-popout');
            var titleEl = drawer.querySelector('.report-drawer-title');
            function openReport(href, name) {
                popout.setAttribute('href', href);
                titleEl.textContent = name || 'Snapshot report';
                if (iframe.getAttribute('src') !== href) iframe.setAttribute('src', href);
                drawer.classList.add('open');
                drawer.setAttribute('aria-hidden', 'false');
            }
            function closeReport() {
                drawer.classList.remove('open');
                drawer.setAttribute('aria-hidden', 'true');
            }
            document.querySelectorAll('table.multi-snapshot tbody').forEach(function(tb) {
                tb.addEventListener('click', function(e) {
                    var tr = e.target.closest('tr.snapshot-row');
                    if (!tr) return;
                    var href = tr.getAttribute('data-report');
                    if (!href) return;
                    var nameEl = tr.querySelector('.snapshot-filename');
                    openReport(href, nameEl ? nameEl.textContent : '');
                });
            });
            drawer.querySelector('.report-drawer-close').addEventListener('click', closeReport);
            drawer.querySelector('.report-drawer-backdrop').addEventListener('click', closeReport);
            document.addEventListener('keydown', function(e) { if (e.key === 'Escape') closeReport(); });
        })();
        """;

    private static readonly string Css = """
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; font-size: 13px; background: #f0f2f5; color: #1a1a2e; padding: 24px; line-height: 1.5; }
        main { max-width: 100%; margin: 0 auto; }
        h1 { font-size: 22px; font-weight: 700; margin-bottom: 4px; }
        .subtitle { font-size: 12px; color: #666; margin-bottom: 16px; font-family: "SF Mono", Consolas, monospace; word-break: break-all; }
        .col-toggles { display: flex; flex-wrap: wrap; align-items: center; gap: 14px; background: #fff; border-radius: 8px; box-shadow: 0 1px 4px rgba(0,0,0,.08); padding: 10px 14px; margin-bottom: 16px; }
        .col-toggles .toggle-label { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: .06em; color: #888; }
        .col-toggles label { display: inline-flex; align-items: center; gap: 5px; font-size: 12px; color: #333; cursor: pointer; user-select: none; }
        .col-toggles input { cursor: pointer; }
        .col-hidden { display: none !important; }
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
        tbody tr.snapshot-row.has-report { cursor: pointer; }
        tbody tr.snapshot-row.has-report:hover { background: #e3ecff; }
        td { padding: 6px 10px; border-bottom: 1px solid #f0f2f5; vertical-align: top; }
        .platform-icon { display: inline-flex; align-items: center; margin-right: 6px; vertical-align: middle; color: #475569; }
        .platform-icon.ios { color: #1a1a2e; }
        .platform-icon.android { color: #3ddc84; }
        td.snapshot-name { font-size: 11px; white-space: nowrap; min-width: 280px; }
        .snapshot-label { display: inline-flex; align-items: center; gap: 4px; }
        .snapshot-filename { font-family: "SF Mono", Consolas, monospace; }
        .report-chevron { color: #1a73e8; font-weight: 700; }
        td.num { font-variant-numeric: tabular-nums; font-family: "SF Mono", Consolas, monospace; font-size: 12px; white-space: nowrap; }
        .bytes { border-bottom: 1px dotted #94a3b8; cursor: help; }
        em.na { color: #999; font-style: italic; }
        .empty { color: #999; font-style: italic; padding: 24px; }
        .report-drawer { position: fixed; inset: 0; z-index: 200; visibility: hidden; pointer-events: none; }
        .report-drawer.open { visibility: visible; pointer-events: auto; }
        .report-drawer-backdrop { position: absolute; inset: 0; background: rgba(20,20,40,.35); opacity: 0; transition: opacity .2s ease; }
        .report-drawer.open .report-drawer-backdrop { opacity: 1; }
        .report-drawer-panel { position: absolute; top: 0; right: 0; height: 100%; width: min(960px, 88vw); background: #fff; box-shadow: -4px 0 24px rgba(0,0,0,.18); display: flex; flex-direction: column; transform: translateX(100%); transition: transform .22s ease; }
        .report-drawer.open .report-drawer-panel { transform: translateX(0); }
        .report-drawer-header { display: flex; align-items: center; gap: 14px; padding: 12px 16px; border-bottom: 1px solid #e8eaed; background: #1a1a2e; color: #fff; }
        .report-drawer-title { font-size: 13px; font-weight: 600; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: "SF Mono", Consolas, monospace; }
        .report-popout { font-size: 12px; color: #9ec1ff; text-decoration: none; white-space: nowrap; }
        .report-popout:hover { color: #fff; text-decoration: underline; }
        .report-drawer-close { background: transparent; border: none; color: #fff; font-size: 22px; line-height: 1; cursor: pointer; padding: 0 4px; }
        .report-drawer-close:hover { color: #ff9b9b; }
        .report-drawer-iframe { flex: 1; width: 100%; border: none; background: #f0f2f5; }
        """;
}

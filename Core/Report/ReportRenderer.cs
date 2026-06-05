using System.Text;

namespace MemorySnapshotDataTools.Report;

/// <summary>
/// Renders a <see cref="ReportModel"/> to a single self-contained HTML string with embedded CSS and sortable-table script.
/// Produces a fixed nav, main content with groups and sections, and consistent styling.
/// </summary>
internal static class ReportRenderer
{
    private const string SortableScript = """
        document.querySelectorAll('table.sortable thead th').forEach(function(th) {
            th.style.cursor = 'pointer';
            th.addEventListener('click', function() {
                var table = th.closest('table');
                var tbody = table.querySelector('tbody');
                var rows = Array.from(tbody.querySelectorAll('tr'));
                var headerCells = table.querySelectorAll('thead th');
                var col = Array.prototype.indexOf.call(headerCells, th);
                var isNum = th.classList.contains('num');
                var dir = table.dataset.sortDir === 'asc' ? -1 : 1;
                table.dataset.sortDir = table.dataset.sortDir === 'asc' ? 'desc' : 'asc';
                rows.sort(function(a, b) {
                    var ac = a.cells[col];
                    var bc = b.cells[col];
                    var av = ac ? ac.textContent.trim() : '';
                    var bv = bc ? bc.textContent.trim() : '';
                    if (isNum) {
                        var an = parseFloat(av.replace(/,/g, '')) || 0;
                        var bn = parseFloat(bv.replace(/,/g, '')) || 0;
                        return dir * (an - bn);
                    }
                    return dir * (av.localeCompare(bv));
                });
                rows.forEach(function(r) { tbody.appendChild(r); });
            });
        });
        """;

    private static readonly string Css = """
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; font-size: 13px; background: #f0f2f5; color: #1a1a2e; padding: 24px; line-height: 1.5; }
        h1 { font-size: 22px; font-weight: 700; margin-bottom: 4px; color: #1a1a2e; }
        .subtitle { font-size: 12px; color: #666; margin-bottom: 32px; font-family: "SF Mono", "Fira Code", Consolas, monospace; }
        nav { position: fixed; top: 24px; right: 24px; width: 210px; background: #fff; border-radius: 8px; box-shadow: 0 1px 4px rgba(0,0,0,.1); padding: 12px 0; z-index: 100; max-height: calc(100vh - 48px); overflow-y: auto; }
        nav > h3 { font-size: 10px; text-transform: uppercase; letter-spacing: .06em; color: #aaa; padding: 0 14px 8px; }
        .nav-group-label { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: .06em; color: #1a1a2e; padding: 8px 14px 4px; border-top: 1px solid #f0f2f5; margin-top: 4px; }
        .nav-group:first-child .nav-group-label { border-top: none; margin-top: 0; }
        nav a { display: block; font-size: 11px; color: #555; text-decoration: none; padding: 3px 14px 3px 20px; border-left: 2px solid transparent; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        nav a:hover { background: #f0f2f5; border-left-color: #1a73e8; color: #1a73e8; }
        main { max-width: 1100px; }
        .group { margin-bottom: 8px; }
        .group-header { display: flex; align-items: baseline; gap: 10px; padding: 20px 4px 10px; }
        .group-header h2 { font-size: 17px; font-weight: 700; color: #1a1a2e; letter-spacing: -.01em; }
        .group-header .group-desc { font-size: 12px; color: #888; font-style: italic; }
        .section { background: #fff; border-radius: 8px; box-shadow: 0 1px 4px rgba(0,0,0,.08); margin-bottom: 16px; overflow: hidden; }
        .section-header { display: flex; align-items: baseline; gap: 10px; padding: 14px 18px 10px; border-bottom: 1px solid #e8eaed; }
        h3.section-title { font-size: 14px; font-weight: 600; color: #1a1a2e; }
        .badge { font-size: 11px; font-weight: 500; background: #e8f0fe; color: #1a73e8; border-radius: 12px; padding: 2px 8px; }
        .insight { padding: 10px 18px; background: #f8f9fb; border-bottom: 1px solid #e8eaed; font-size: 12px; color: #444; line-height: 1.6; }
        .insight strong { color: #1a1a2e; }
        .insight .stat-pills { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 6px; }
        .insight .pill { background: #fff; border: 1px solid #dde1e9; border-radius: 6px; padding: 4px 10px; font-size: 12px; line-height: 1.3; }
        .insight .pill-label { color: #888; font-size: 10px; text-transform: uppercase; letter-spacing: .04em; }
        .insight .pill-value { font-weight: 600; color: #1a1a2e; }
        .insight .pill.warn .pill-value { color: #c0392b; }
        .insight .pill.good .pill-value { color: #27ae60; }
        .table-wrap { overflow-x: auto; }
        table { width: 100%; border-collapse: collapse; }
        thead th { background: #1a1a2e; color: #fff; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: .04em; padding: 8px 12px; text-align: left; position: sticky; top: 0; white-space: nowrap; }
        thead th.num { text-align: right; }
        tbody tr:nth-child(even) { background: #f8f9fb; }
        tbody tr:hover { background: #eef2ff; }
        td { padding: 6px 12px; border-bottom: 1px solid #f0f2f5; white-space: nowrap; }
        td.num { text-align: right; font-variant-numeric: tabular-nums; font-family: "SF Mono", "Fira Code", Consolas, monospace; font-size: 12px; color: #333; }
        td.warn { color: #c0392b; font-weight: 600; }
        td.trunc { max-width: 320px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; cursor: default; }
        .empty { padding: 18px; color: #999; font-style: italic; }
        .kv-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; padding: 16px 18px; border-bottom: 1px solid #f0f2f5; }
        .kv-label { font-size: 10px; color: #888; text-transform: uppercase; letter-spacing: .05em; }
        .kv-value { font-size: 15px; font-weight: 600; color: #1a1a2e; margin-top: 2px; }
        .kv-value.mono { font-family: "SF Mono", "Fira Code", Consolas, monospace; font-size: 11px; font-weight: 400; color: #444; word-break: break-all; white-space: normal; }
        /* When the viewport is too narrow for the fixed nav to sit in the right gutter (e.g. inside the
           multi-report iframe drawer), drop it into normal flow at the top so it never overlaps content. */
        @media (max-width: 1360px) {
            nav { position: static; width: auto; max-width: 100%; max-height: 220px; margin: 0 0 20px; }
            main { max-width: 100%; }
        }
        """;

    /// <summary>Builds the full HTML document from the report model (nav, title, groups, sections).</summary>
    /// <param name="model">Populated report model from <see cref="ReportBuilder.Build"/>.</param>
    /// <returns>Complete HTML string (UTF-8).</returns>
    public static string Render(ReportModel model)
    {
        var titleEsc = System.Net.WebUtility.HtmlEncode(model.Title);
        var dbPathEsc = System.Net.WebUtility.HtmlEncode(model.DbPath);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"UTF-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>");
        sb.Append(titleEsc);
        sb.Append("</title>\n<style>\n");
        sb.Append(Css);
        sb.Append("\n</style>\n</head>\n<body>\n<nav><h3>Contents</h3>\n");

        foreach (var navGroup in model.NavGroups)
        {
            sb.Append("<div class=\"nav-group\">\n<div class=\"nav-group-label\">").Append(System.Net.WebUtility.HtmlEncode(navGroup.GroupTitle)).Append("</div>\n");
            foreach (var item in navGroup.Items)
            {
                sb.Append("<a href=\"#").Append(System.Net.WebUtility.HtmlEncode(item.Anchor)).Append("\">").Append(System.Net.WebUtility.HtmlEncode(item.Title)).Append("</a>\n");
            }
            sb.Append("</div>\n");
        }

        sb.Append("</nav>\n<main>\n<h1>").Append(titleEsc).Append("</h1>\n<p class=\"subtitle\">").Append(dbPathEsc).Append("</p>\n");

        foreach (var group in model.Groups)
        {
            sb.Append("<div class=\"group\">\n<div class=\"group-header\"><h2>").Append(System.Net.WebUtility.HtmlEncode(group.GroupTitle)).Append("</h2><span class=\"group-desc\">").Append(System.Net.WebUtility.HtmlEncode(group.GroupDesc)).Append("</span></div>\n");
            foreach (var section in group.Sections)
            {
                sb.Append(section.ContentHtml);
            }
            sb.Append("</div>\n");
        }

        sb.Append("</main>\n<script>\n").Append(SortableScript).Append("\n</script>\n</body>\n</html>");
        return sb.ToString();
    }
}

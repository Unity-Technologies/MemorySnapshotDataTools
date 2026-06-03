using System.Globalization;
using System.Linq;
using MemorySnapshotDataTools.Report.Queries;

namespace MemorySnapshotDataTools.Report;

/// <summary>
/// Builds a <see cref="ReportModel"/> by executing report SQL via <see cref="Queries.IReportQueryBackend"/> and assembling groups/sections
/// (snapshot info, native objects, managed heap, connections, roots, memory regions). Uses <see cref="ReportHtmlHelper"/> for table and insight HTML.
/// </summary>
internal static class ReportBuilder
{
    /// <summary>
    /// Runs all report queries against the backend, maps results into sections and groups, and returns a fully populated report model.
    /// </summary>
    /// <param name="backend">Query backend (DuckDB or SQLite) connected to the report database.</param>
    /// <param name="title">Report title.</param>
    /// <param name="dbPath">Database path (for display).</param>
    /// <param name="generatedAtUtc">Generated timestamp string (UTC).</param>
    /// <returns>Populated <see cref="ReportModel"/> ready for <see cref="ReportRenderer.Render"/>.</returns>
    public static ReportModel Build(IReportQueryBackend backend, string title, string dbPath, string generatedAtUtc)
    {
        var model = new ReportModel
        {
            Title = title,
            DbPath = dbPath,
            GeneratedAtUtc = generatedAtUtc,
        };

        var (infoCols, infoRows) = backend.ExecuteQuery(ReportSql.SnapshotInfo);
        var (countCols, countRows) = backend.ExecuteQuery(ReportSql.TableCounts);

        var kv = new Dictionary<string, object?>();
        if (infoRows.Count > 0)
        {
            var r = infoRows[0];
            kv["Snapshot Path"] = r.Length > 0 ? r[0] : null;
            kv["Exported At (UTC)"] = r.Length > 1 ? r[1] : null;
            kv["Unity Version"] = r.Length > 2 ? r[2] : null;
        }
        kv["Schema Version"] = ReadSchemaVersion(backend);
        kv["Report Generated"] = generatedAtUtc;

        var totalRows = countRows.Sum(row => row.Length > 1 && row[1] != null ? Convert.ToInt64(row[1]) : 0);
        var insightSnap = ReportHtmlHelper.RenderInsight(
            $"Snapshot captured from Unity <strong>{ReportHtmlHelper.Escape(kv.GetValueOrDefault("Unity Version") ?? "—")}</strong> " +
            $"containing <strong>{ReportHtmlHelper.FmtNum(totalRows)}</strong> rows across <strong>{ReportHtmlHelper.FmtNum(countRows.Count)}</strong> tables. " +
            "If table counts appear unexpectedly low, verify the snapshot was captured with <em>Capture All Objects</em> and native memory collection enabled.");

        var snapContent = insightSnap + ReportHtmlHelper.RenderKv(kv) + ReportHtmlHelper.RenderTable(countCols, countRows);
        var snapSection = new ReportSection
        {
            Anchor = "snapshot-info",
            SectionTitle = "📋 Source & Table Counts",
            ContentHtml = ReportHtmlHelper.Section("snapshot-info", "📋 Source & Table Counts", snapContent, null),
        };
        var snapGroup = new ReportGroup
        {
            GroupTitle = "📸 Snapshot Info",
            GroupDesc = "",
        };
        snapGroup.Sections.Add(snapSection);
        AddNav(model, snapGroup);
        model.Groups.Add(snapGroup);

        // Native Objects group
        var (natOvCols, natOvRows) = backend.ExecuteQuery(ReportSql.NativeOverview);
        var (natTyCols, natTyRows) = backend.ExecuteQuery(ReportSql.NativeTypes);
        var (bktCols, bktRows) = backend.ExecuteQuery(ReportSql.SizeBucketDistribution(backend.Dialect));
        var (top50Cols, top50Rows) = backend.ExecuteQuery(ReportSql.TopNativeObjects);
        var (dupCols, dupRows) = backend.ExecuteQuery(ReportSql.DuplicateAssets);
        var (dsCols, dsRows) = backend.ExecuteQuery(ReportSql.DuplicateSummary);
        var (t50Cols, t50Rows) = backend.ExecuteQuery(ReportSql.Top50Summary);
        var (_, top5PctRows) = natTyRows.Count > 0 ? backend.ExecuteQuery(ReportSql.NativeTypesTop5Pct) : (Array.Empty<string>(), new List<object?[]>());
        var top5PctVal = top5PctRows.Count > 0 && top5PctRows[0].Length > 0 ? top5PctRows[0][0] : null;

        var natTotalObjects = natOvRows.Count > 0 && natOvRows[0].Length > 0 ? natOvRows[0][0] : 0;
        var natTotalMb = natOvRows.Count > 0 && natOvRows[0].Length > 1 ? natOvRows[0][1] : 0;
        var natAvgKb = natOvRows.Count > 0 && natOvRows[0].Length > 3 ? natOvRows[0][3] : 0;
        var natMaxMb = natOvRows.Count > 0 && natOvRows[0].Length > 4 ? natOvRows[0][4] : 0;
        var natDistinctTypes = natOvRows.Count > 0 && natOvRows[0].Length > 5 ? natOvRows[0][5] : 0;

        var insightNatOv = ReportHtmlHelper.RenderInsight(
            $"<strong>{ReportHtmlHelper.FmtNum(natTotalObjects)}</strong> native objects across " +
            $"<strong>{ReportHtmlHelper.FmtNum(natDistinctTypes)}</strong> types occupy <strong>{ReportHtmlHelper.FmtNum(natTotalMb)} MB</strong> total " +
            $"(avg <strong>{ReportHtmlHelper.FmtNum(natAvgKb)} KB</strong>; largest single object <strong>{ReportHtmlHelper.FmtNum(natMaxMb)} MB</strong>). " +
            "This is your native memory baseline — compare it against your platform budget to gauge whether a reduction pass is needed.");

        var top5Names = natTyRows.Count > 0 ? string.Join(", ", natTyRows.Take(5).Select(r => r[0]?.ToString() ?? "—")) : "—";
        var insightTypes = ReportHtmlHelper.RenderInsight(
            $"The top 5 types — <strong>{ReportHtmlHelper.Escape(top5Names)}</strong> — account for <strong>{ReportHtmlHelper.FmtNum(top5PctVal)}%</strong> of all native memory. " +
            "These types are your highest-leverage optimization targets.");

        var t50Mb = t50Rows.Count > 0 && t50Rows[0].Length > 1 ? t50Rows[0][1] : 0;
        var t50Pct = t50Rows.Count > 0 && t50Rows[0].Length > 2 ? t50Rows[0][2] : 0;
        var insightTop50 = ReportHtmlHelper.RenderInsight(
            $"The 50 largest individual objects account for <strong>{ReportHtmlHelper.FmtNum(t50Mb)} MB</strong> — <strong>{ReportHtmlHelper.FmtNum(t50Pct)}%</strong> of all native memory. " +
            "A small number of objects driving a large share of memory means optimizing even one large asset can have measurable impact.",
            pills: [("Objects shown", "50", ""), ("Combined size", $"{t50Mb}", ""), ("% of native total", $"{t50Pct}%", "")]);

        var dsGroups = dsRows.Count > 0 && dsRows[0].Length > 0 ? dsRows[0][0] : 0;
        var dsWastedMb = dsRows.Count > 0 && dsRows[0].Length > 2 ? dsRows[0][2] : 0;
        var dsPct = dsRows.Count > 0 && dsRows[0].Length > 3 ? dsRows[0][3] : 0;
        var insightDups = ReportHtmlHelper.RenderInsight(
            $"<strong>{ReportHtmlHelper.FmtNum(dsGroups)}</strong> asset name-collision groups were found, " +
            $"with an upper-bound waste estimate of <strong>{ReportHtmlHelper.FmtNum(dsWastedMb)} MB</strong> (<strong>{ReportHtmlHelper.FmtNum(dsPct)}%</strong> of native memory). " +
            "True asset duplication wastes memory proportional to its count.",
            pills: [
                ("Name-collision groups", ReportHtmlHelper.FmtNum(dsGroups), dsGroups is int i && i > 0 ? "warn" : "good"),
                ("Est. wasted memory", $"{dsWastedMb} MB", "warn"),
            ]);

        var nativeGroup = new ReportGroup { GroupTitle = "🧱 Native Objects", GroupDesc = "Native Unity objects — types, sizes, and duplication" };
        nativeGroup.Sections.Add(new ReportSection { Anchor = "native-overview", SectionTitle = "📊 Overview", ContentHtml = ReportHtmlHelper.Section("native-overview", "📊 Overview", insightNatOv + ReportHtmlHelper.RenderTable(natOvCols, natOvRows), null) });
        nativeGroup.Sections.Add(new ReportSection { Anchor = "native-types", SectionTitle = "🏆 Top Types by Size", ContentHtml = ReportHtmlHelper.Section("native-types", "🏆 Top Types by Size", insightTypes + ReportHtmlHelper.RenderTable(natTyCols, natTyRows, truncateCols: new HashSet<string> { "type_name" }), natTyRows.Count), RowCount = natTyRows.Count });
        nativeGroup.Sections.Add(new ReportSection { Anchor = "size-buckets", SectionTitle = "📐 Size Distribution (log₄)", ContentHtml = ReportHtmlHelper.Section("size-buckets", "📐 Size Distribution (log₄)", ReportHtmlHelper.RenderTable(bktCols, bktRows), bktRows.Count), RowCount = bktRows.Count });
        nativeGroup.Sections.Add(new ReportSection { Anchor = "top-objects", SectionTitle = "🔝 Top 50 Largest Objects", ContentHtml = ReportHtmlHelper.Section("top-objects", "🔝 Top 50 Largest Objects", insightTop50 + ReportHtmlHelper.RenderTable(top50Cols, top50Rows, truncateCols: new HashSet<string> { "name" }), top50Rows.Count), RowCount = top50Rows.Count });
        nativeGroup.Sections.Add(new ReportSection { Anchor = "duplicates", SectionTitle = "⚠️ Duplicate Assets", ContentHtml = ReportHtmlHelper.Section("duplicates", "⚠️ Duplicate Assets", insightDups + ReportHtmlHelper.RenderTable(dupCols, dupRows, warnCol: "wasted_mb", truncateCols: new HashSet<string> { "name" }), dupRows.Count), RowCount = dupRows.Count });
        AddNav(model, nativeGroup);
        model.Groups.Add(nativeGroup);

        // Managed Heap
        var (mgOvCols, mgOvRows) = backend.ExecuteQuery(ReportSql.ManagedOverview);
        var (mgTyCols, mgTyRows) = backend.ExecuteQuery(ReportSql.ManagedTypes);
        var mgTotal = mgOvRows.Count > 0 ? mgOvRows[0][0] : 0;
        var mgMb = mgOvRows.Count > 0 ? mgOvRows[0][1] : 0;
        var mgTypes = mgOvRows.Count > 0 && mgOvRows[0].Length > 3 ? mgOvRows[0][3] : 0;
        var mgBridged = mgOvRows.Count > 0 && mgOvRows[0].Length > 4 ? mgOvRows[0][4] : 0;
        var insightMgOv = ReportHtmlHelper.RenderInsight(
            $"<strong>{ReportHtmlHelper.FmtNum(mgTotal)}</strong> managed objects across <strong>{ReportHtmlHelper.FmtNum(mgTypes)}</strong> types occupy <strong>{ReportHtmlHelper.FmtNum(mgMb)} MB</strong>; " +
            $"<strong>{ReportHtmlHelper.FmtNum(mgBridged)}</strong> have a corresponding native object. " +
            "Large managed heaps increase GC pressure.");
        var topMg = mgTyRows.Count > 0 ? mgTyRows[0][0]?.ToString() ?? "—" : "—";
        var topMgMb = mgTyRows.Count > 0 && mgTyRows[0].Length > 2 ? mgTyRows[0][2] : 0;
        var insightMgTy = ReportHtmlHelper.RenderInsight(
            $"<strong>{ReportHtmlHelper.Escape(topMg)}</strong> is the largest managed allocator at <strong>{ReportHtmlHelper.FmtNum(topMgMb)} MB</strong>. " +
            "This type is the primary driver of managed heap size and therefore GC pause duration.");

        var managedGroup = new ReportGroup { GroupTitle = "🧠 Managed Heap", GroupDesc = "GC-managed objects and type allocations" };
        managedGroup.Sections.Add(new ReportSection { Anchor = "managed-overview", SectionTitle = "📊 Overview", ContentHtml = ReportHtmlHelper.Section("managed-overview", "📊 Overview", insightMgOv + ReportHtmlHelper.RenderTable(mgOvCols, mgOvRows), null) });
        managedGroup.Sections.Add(new ReportSection { Anchor = "managed-types", SectionTitle = "🏆 Top Types by Size", ContentHtml = ReportHtmlHelper.Section("managed-types", "🏆 Top Types by Size", insightMgTy + ReportHtmlHelper.RenderTable(mgTyCols, mgTyRows, truncateCols: new HashSet<string> { "type_name" }), mgTyRows.Count), RowCount = mgTyRows.Count });
        AddNav(model, managedGroup);
        model.Groups.Add(managedGroup);

        // Leaked Shells
        var hasIsDestroyed = backend.HasColumn("native_objects", "is_destroyed");
        var (lbCols, lbRows) = backend.ExecuteQuery(ReportSql.LeakedBByType);
        var (lbsCols, lbsRows) = backend.ExecuteQuery(ReportSql.LeakedBStats);
        var bTotal = lbsRows.Count > 0 && lbsRows[0].Length > 0 ? Convert.ToInt64(lbsRows[0][0] ?? 0) : 0L;
        var topBType = lbRows.Count > 0 && lbRows[0].Length > 0 ? lbRows[0][0]?.ToString() ?? "—" : "—";
        var topBCount = lbRows.Count > 0 && lbRows[0].Length > 1 ? lbRows[0][1] : (object?)0;
        var insightLb = ReportHtmlHelper.RenderInsight(
            $"<strong>{ReportHtmlHelper.FmtNum(bTotal)}</strong> orphaned managed wrappers detected — " +
            "C# objects whose native counterpart was completely freed but whose GC references were never cleared. " +
            $"The most common type is <strong>{ReportHtmlHelper.Escape(topBType)}</strong> " +
            $"with <strong>{ReportHtmlHelper.FmtNum(topBCount)}</strong> instances. " +
            "These objects waste managed heap space and GC scan time despite having no functional native backing. " +
            "Fix by hooking <em>OnDestroy</em> (or equivalent) and nulling all strong C# references so the GC " +
            "can collect them.");

        var leakedSections = new List<ReportSection>();
        if (hasIsDestroyed)
        {
            var (lcCols, lcRows) = backend.ExecuteQuery(ReportSql.LeakedCombined);
            var (lasCols, lasRows) = backend.ExecuteQuery(ReportSql.LeakedAStats);
            var laTotal = lasRows.Count > 0 && lasRows[0].Length > 0 ? Convert.ToInt64(lasRows[0][0] ?? 0) : 0L;
            var laMb = lasRows.Count > 0 && lasRows[0].Length > 1 ? ToDouble(lasRows[0][1]) : 0.0;
            var laPct = lasRows.Count > 0 && lasRows[0].Length > 2 ? ToDouble(lasRows[0][2]) : 0.0;
            var combinedTotal = laTotal + bTotal;
            var insightLc = ReportHtmlHelper.RenderInsight(
                $"<strong>{ReportHtmlHelper.FmtNum(combinedTotal)}</strong> leaked C# shell objects detected: " +
                $"<strong>{ReportHtmlHelper.FmtNum(laTotal)}</strong> Pattern A (native destroyed but still occupying " +
                $"<strong>{laMb:N2} MB</strong> of native memory) and " +
                $"<strong>{ReportHtmlHelper.FmtNum(bTotal)}</strong> Pattern B (native fully freed, managed wrapper orphaned). " +
                "Leaked shells waste memory and can cause <em>MissingReferenceException</em> crashes at runtime. " +
                "Prioritise Pattern A by <em>native_mb_retained</em> — each MB is real engine memory the runtime " +
                "cannot reclaim until the C# reference chain is broken.",
                pills: [
                    ("Pattern A (destroyed)", ReportHtmlHelper.FmtNum(laTotal), laTotal > 0 ? "warn" : "good"),
                    ("Native MB retained", $"{laMb:N2} MB", laMb > 0 ? "warn" : "good"),
                    ("Pattern B (orphaned)", ReportHtmlHelper.FmtNum(bTotal), bTotal > 0 ? "warn" : "good"),
                ]);
            leakedSections.Add(new ReportSection
            {
                Anchor = "leaked-summary",
                SectionTitle = "📊 Summary (Both Patterns)",
                ContentHtml = ReportHtmlHelper.Section("leaked-summary", "📊 Summary (Both Patterns)", insightLc + ReportHtmlHelper.RenderTable(lcCols, lcRows, truncateCols: new HashSet<string> { "managed_type_name" }), lcRows.Count),
                RowCount = lcRows.Count,
            });

            var (latCols, latRows) = backend.ExecuteQuery(ReportSql.LeakedAByType);
            var topLatNative = latRows.Count > 0 && latRows[0].Length > 0 ? latRows[0][0]?.ToString() ?? "—" : "—";
            var topLatMb = latRows.Count > 0 && latRows[0].Length > 3 ? ToDouble(latRows[0][3]) : 0.0;
            var insightLat = ReportHtmlHelper.RenderInsight(
                $"<strong>{ReportHtmlHelper.FmtNum(latRows.Count)}</strong> native/managed type pair(s) show Pattern A leaks. " +
                "The worst offender by retained memory is " +
                $"<strong>{ReportHtmlHelper.Escape(topLatNative)}</strong> holding " +
                $"<strong>{topLatMb:N2} MB</strong> despite being destroyed. " +
                "These native objects remain alive because managed C# references block GC collection. " +
                "Track down the code paths that hold a reference to these types after <em>Destroy()</em> — " +
                "common culprits: static caches, event listener captures, and async/coroutine closures.");
            leakedSections.Add(new ReportSection
            {
                Anchor = "leaked-a-types",
                SectionTitle = "💥 Pattern A: Destroyed-but-Retained (by Type)",
                ContentHtml = ReportHtmlHelper.Section("leaked-a-types", "💥 Pattern A: Destroyed-but-Retained (by Type)", insightLat + ReportHtmlHelper.RenderTable(latCols, latRows, truncateCols: new HashSet<string> { "managed_type" }), latRows.Count),
                RowCount = latRows.Count,
            });

            var (laoColsRaw, laoRows) = backend.ExecuteQuery(ReportSql.LeakedATopObjects);
            var augLaoCols = laoColsRaw.Concat(new[] { "downstream_mb", "exclusive_mb", "total_freed_mb" }).ToArray();
            var augLaoRows = new List<object?[]>();
            foreach (var row in laoRows)
            {
                var rootIdx = row.Length > 0 ? Convert.ToInt64(row[0] ?? 0) : 0L;
                var ownMb = row.Length > 4 ? ToDouble(row[4]) : 0.0;
                var (downstreamCols, downstreamRows) = backend.ExecuteQuery(ReportSql.DownstreamStats(rootIdx));
                var dsMb = downstreamRows.Count > 0 && downstreamRows[0].Length > 0 ? ToDouble(downstreamRows[0][0]) : 0.0;
                var exclMb = downstreamRows.Count > 0 && downstreamRows[0].Length > 1 ? ToDouble(downstreamRows[0][1]) : 0.0;
                var totalFreed = Math.Round(ownMb + exclMb, 2);
                augLaoRows.Add(row.Concat(new object?[] { Math.Round(dsMb, 2), Math.Round(exclMb, 2), totalFreed }).ToArray());
            }
            augLaoRows = augLaoRows.OrderByDescending(r => r.Length > 0 ? ToDouble(r[^1]) : 0.0).ToList();

            var topLaoName = augLaoRows.Count > 0 && augLaoRows[0].Length > 1 ? augLaoRows[0][1]?.ToString() ?? "—" : "—";
            var topLaoOwn = augLaoRows.Count > 0 && augLaoRows[0].Length > 4 ? ToDouble(augLaoRows[0][4]) : 0.0;
            var topLaoExcl = augLaoRows.Count > 0 && augLaoRows[0].Length >= 2 ? ToDouble(augLaoRows[0][^2]) : 0.0;
            var topLaoFreed = augLaoRows.Count > 0 && augLaoRows[0].Length > 0 ? ToDouble(augLaoRows[0][^1]) : 0.0;
            var topLaoNameTrunc = topLaoName.Length > 30 ? topLaoName[..30] : topLaoName;
            var insightLao = ReportHtmlHelper.RenderInsight(
                "Top Pattern A leaked objects ranked by <em>total_freed_mb</em> " +
                "(own size + exclusively-owned downstream memory). " +
                "The highest-impact leak is " +
                $"<strong>{ReportHtmlHelper.Escape(topLaoName)}</strong>: " +
                $"fixing it would free <strong>{topLaoFreed:N2} MB</strong> total " +
                $"({topLaoOwn:N2} MB own + {topLaoExcl:N2} MB exclusive downstream). " +
                "<em>exclusive_mb</em> counts only downstream objects reachable solely through this object " +
                "— assets shared with other live objects are excluded, so this is a conservative lower bound. " +
                "Prioritise objects with large <em>total_freed_mb</em>: ensure <em>Destroy()</em> is always " +
                "paired with reference nulling, and that no event listeners or coroutines capture a reference " +
                "past the object's intended lifetime.",
                pills: [
                    ("Top leak", topLaoNameTrunc, "warn"),
                    ("Own size", $"{topLaoOwn:N2} MB", "warn"),
                    ("Excl. downstream", $"{topLaoExcl:N2} MB", "warn"),
                    ("Total freed", $"{topLaoFreed:N2} MB", "warn"),
                ]);
            leakedSections.Add(new ReportSection
            {
                Anchor = "leaked-a-objects",
                SectionTitle = "🔬 Pattern A: Top Individual Leaks + Exclusive Cost",
                ContentHtml = ReportHtmlHelper.Section("leaked-a-objects", "🔬 Pattern A: Top Individual Leaks + Exclusive Cost", insightLao + ReportHtmlHelper.RenderTable(augLaoCols, augLaoRows, warnCol: "total_freed_mb", truncateCols: new HashSet<string> { "name" }), augLaoRows.Count),
                RowCount = augLaoRows.Count,
            });

            var (adnCols, adnRows) = backend.ExecuteQuery(ReportSql.AllDestroyedNatives);
            var (adnsCols, adnsRows) = backend.ExecuteQuery(ReportSql.AllDestroyedStats);
            var adnTotal = adnsRows.Count > 0 && adnsRows[0].Length > 0 ? Convert.ToInt64(adnsRows[0][0] ?? 0) : 0L;
            var adnMb = adnsRows.Count > 0 && adnsRows[0].Length > 1 ? ToDouble(adnsRows[0][1]) : 0.0;
            var adnPct = adnsRows.Count > 0 && adnsRows[0].Length > 2 ? ToDouble(adnsRows[0][2]) : 0.0;
            var insightAdn = ReportHtmlHelper.RenderInsight(
                $"<strong>{ReportHtmlHelper.FmtNum(adnTotal)}</strong> native objects across " +
                $"<strong>{ReportHtmlHelper.FmtNum(adnRows.Count)}</strong> type(s) carry <em>is_destroyed=true</em>, " +
                $"retaining <strong>{adnMb:N2} MB</strong> ({adnPct:N1}% of total native memory). " +
                "This is the full native cost of pending destructions — Pattern A leaks are a subset of this " +
                "(only those with a surviving managed wrapper). " +
                "A high count here that drops significantly after calling " +
                "<em>Resources.UnloadUnusedAssets()</em> + <em>GC.Collect()</em> indicates the allocator " +
                "is cleaning up but GC hasn't run yet; a persistently high count across snapshots points to " +
                "genuine managed-side leaks blocking reclaim.");
            leakedSections.Add(new ReportSection
            {
                Anchor = "all-destroyed",
                SectionTitle = "🗑️ All Destroyed Natives (by Type)",
                ContentHtml = ReportHtmlHelper.Section("all-destroyed", "🗑️ All Destroyed Natives (by Type)", insightAdn + ReportHtmlHelper.RenderTable(adnCols, adnRows), adnRows.Count),
                RowCount = adnRows.Count,
            });
        }
        else
        {
            var schemaNoticeContent = ReportHtmlHelper.RenderInsight(
                "Pattern A analysis (<em>destroyed-but-retained</em> natives) requires the " +
                "<code>is_destroyed</code> column which is not present in this database. " +
                "Re-export the snapshot with the latest version of the exporter to enable this analysis. " +
                "Pattern B (orphaned managed wrappers) below is available without it.");
            leakedSections.Add(new ReportSection
            {
                Anchor = "leaked-schema-notice",
                SectionTitle = "⚠️ Schema Notice",
                ContentHtml = ReportHtmlHelper.Section("leaked-schema-notice", "⚠️ Schema Notice", schemaNoticeContent, null),
            });
        }

        leakedSections.Add(new ReportSection
        {
            Anchor = "leaked-b",
            SectionTitle = "👻 Pattern B: Orphaned Managed Wrappers",
            ContentHtml = ReportHtmlHelper.Section("leaked-b", "👻 Pattern B: Orphaned Managed Wrappers", insightLb + ReportHtmlHelper.RenderTable(lbCols, lbRows), lbRows.Count),
            RowCount = lbRows.Count,
        });

        var leakedGroup = new ReportGroup
        {
            GroupTitle = "🧟 Leaked Shells",
            GroupDesc = "C# managed wrappers alive past their native object's destruction",
        };
        foreach (var sec in leakedSections)
            leakedGroup.Sections.Add(sec);
        AddNav(model, leakedGroup);
        model.Groups.Add(leakedGroup);

        // Native Roots
        var (nrAreaCols, nrAreaRows) = backend.ExecuteQuery(ReportSql.NativeRootsByArea);
        var (nrTopCols, nrTopRows) = backend.ExecuteQuery(ReportSql.NativeRootsTop);
        var insightRoots = ReportHtmlHelper.RenderInsight("Native roots by area and top 30 by retained size.");
        var rootsGroup = new ReportGroup { GroupTitle = "📍 Native Roots", GroupDesc = "Root references and retained size" };
        rootsGroup.Sections.Add(new ReportSection { Anchor = "roots-area", SectionTitle = "📍 By Area", ContentHtml = ReportHtmlHelper.Section("roots-area", "📍 By Area", insightRoots + ReportHtmlHelper.RenderTable(nrAreaCols, nrAreaRows), nrAreaRows.Count), RowCount = nrAreaRows.Count });
        rootsGroup.Sections.Add(new ReportSection { Anchor = "roots-top", SectionTitle = "🥇 Top 30 by Retained Size", ContentHtml = ReportHtmlHelper.Section("roots-top", "🥇 Top 30 by Retained Size", ReportHtmlHelper.RenderTable(nrTopCols, nrTopRows), nrTopRows.Count), RowCount = nrTopRows.Count });
        AddNav(model, rootsGroup);
        model.Groups.Add(rootsGroup);

        // Memory Regions & Allocation Efficiency
        var (regCols, regRows) = backend.ExecuteQuery(ReportSql.MemoryRegions);
        var (aeCols, aeRows) = backend.ExecuteQuery(ReportSql.AllocationEfficiency);
        var regionsGroup = new ReportGroup { GroupTitle = "🗂️ Memory & Allocations", GroupDesc = "Memory regions and allocation efficiency" };
        regionsGroup.Sections.Add(new ReportSection { Anchor = "regions", SectionTitle = "🗂️ Memory Regions", ContentHtml = ReportHtmlHelper.Section("regions", "🗂️ Memory Regions", ReportHtmlHelper.RenderTable(regCols, regRows), regRows.Count), RowCount = regRows.Count });
        regionsGroup.Sections.Add(new ReportSection { Anchor = "alloc-efficiency", SectionTitle = "⚡ Allocation Efficiency", ContentHtml = ReportHtmlHelper.Section("alloc-efficiency", "⚡ Allocation Efficiency", ReportHtmlHelper.RenderTable(aeCols, aeRows), aeRows.Count), RowCount = aeRows.Count });
        AddNav(model, regionsGroup);
        model.Groups.Add(regionsGroup);

        // Connections
        var (ctCols, ctRows) = backend.ExecuteQuery(ReportSql.ConnectionTypes);
        var (mrCols, mrRows) = backend.ExecuteQuery(ReportSql.MostReferenced);
        var (mrExCols, mrExRows) = backend.ExecuteQuery(ReportSql.MostReferencedExclMonoScript);
        var (obCols, obRows) = backend.ExecuteQuery(ReportSql.MostOutbound);
        var insightConn = ReportHtmlHelper.RenderInsight("Connection types and most-referenced / most-outbound native objects.");
        var connGroup = new ReportGroup { GroupTitle = "🔗 Connections", GroupDesc = "Reference graph and connection types" };
        connGroup.Sections.Add(new ReportSection { Anchor = "connection-types", SectionTitle = "Connection Types", ContentHtml = ReportHtmlHelper.Section("connection-types", "Connection Types", insightConn + ReportHtmlHelper.RenderTable(ctCols, ctRows), ctRows.Count), RowCount = ctRows.Count });
        connGroup.Sections.Add(new ReportSection { Anchor = "most-referenced", SectionTitle = "Most Referenced (incl. MonoScript)", ContentHtml = ReportHtmlHelper.Section("most-referenced", "Most Referenced (incl. MonoScript)", ReportHtmlHelper.RenderTable(mrCols, mrRows, truncateCols: new HashSet<string> { "name" }), mrRows.Count), RowCount = mrRows.Count });
        connGroup.Sections.Add(new ReportSection { Anchor = "most-referenced-excl", SectionTitle = "Most Referenced (excl. MonoScript)", ContentHtml = ReportHtmlHelper.Section("most-referenced-excl", "Most Referenced (excl. MonoScript)", ReportHtmlHelper.RenderTable(mrExCols, mrExRows, truncateCols: new HashSet<string> { "name" }), mrExRows.Count), RowCount = mrExRows.Count });
        connGroup.Sections.Add(new ReportSection { Anchor = "most-outbound", SectionTitle = "Most Outbound", ContentHtml = ReportHtmlHelper.Section("most-outbound", "Most Outbound", ReportHtmlHelper.RenderTable(obCols, obRows, truncateCols: new HashSet<string> { "name" }), obRows.Count), RowCount = obRows.Count });
        AddNav(model, connGroup);
        model.Groups.Add(connGroup);

        return model;
    }

    private static void AddNav(ReportModel model, ReportGroup group)
    {
        var navGroup = new NavGroup { GroupTitle = group.GroupTitle };
        foreach (var sec in group.Sections)
            navGroup.Items.Add(new NavItem { Anchor = sec.Anchor, Title = sec.SectionTitle });
        model.NavGroups.Add(navGroup);
    }

    /// <summary>
    /// Reads the schema version via the backend for display in the Snapshot Info section, returning a
    /// re-export advisory for pre-versioning databases that lack <c>schema_meta</c>. Uses the constant
    /// <see cref="ReportSql.SchemaMeta"/> query (no external input).
    /// </summary>
    private static string ReadSchemaVersion(IReportQueryBackend backend)
    {
        if (!backend.HasColumn("schema_meta", "schema_version_major"))
            return DatabaseSchemaInfo.DescribeVersion(0, 0);

        var (_, rows) = backend.ExecuteQuery(ReportSql.SchemaMeta);
        if (rows.Count == 0 || rows[0].Length < 2 || rows[0][0] is null || rows[0][1] is null)
            return DatabaseSchemaInfo.DescribeVersion(0, 0);

        return DatabaseSchemaInfo.DescribeVersion(Convert.ToInt32(rows[0][0]), Convert.ToInt32(rows[0][1]));
    }

    private static double ToDouble(object? o)
    {
        if (o == null) return 0.0;
        if (o is double d) return d;
        if (o is float f) return f;
        if (o is decimal m) return (double)m;
        if (o is int i) return i;
        if (o is long l) return l;
        return double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.0;
    }
}

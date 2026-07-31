using MemorySnapshotDataTools;
using MemorySnapshotDataTools.Report;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Tests for <see cref="SummaryReportFormatter"/> console rendering.
/// </summary>
public sealed class SummaryReportFormatterTests
{
    private static SummaryReport BuildReport()
    {
        var metrics = new SummaryMetrics
        {
            TotalAllocatedBytes = 2_909_880_164,
            TotalResidentBytes = 1_333_444_608,
            TotalSwappedBytes = 104_857_600,
            SwappedAvailable = true,
        };
        metrics.AllocatedMemoryDistribution.Add(new SummaryCategory
        {
            Name = "Native",
            CommittedBytes = 393_851_683,
            ResidentBytes = 301_267_487,
            ResidentAvailable = true,
            SwappedBytes = 52_428_800,
            SwappedAvailable = true,
        });
        metrics.AllocatedMemoryDistribution.Add(new SummaryCategory
        {
            Name = "Graphics (Estimated)",
            CommittedBytes = 127_797_802,
            ResidentBytes = 0,
            ResidentAvailable = false,
        });
        metrics.ManagedHeapUtilization.Add(new SummaryCategory
        {
            Name = "Objects",
            CommittedBytes = 328_328_842,
            ResidentBytes = 257_779_539,
            ResidentAvailable = true,
        });

        return new SummaryReport
        {
            SourcePath = "/captures/Game_IOS.snap",
            Source = SummarySource.Snapshot,
            Info = new SnapshotInfo
            {
                ProductName = "MyApp",
                Platform = "IPhonePlayer",
                UnityVersion = "6000.3.11f1",
                SnapFormatVersion = 17,
                SessionGuid = 2_669_506_182,
            },
            Metrics = metrics,
            UnityObjectCategories =
            [
                new UnityObjectCategory { TypeName = "Texture2D", Count = 1234, AllocatedBytes = 245_000_000 },
                new UnityObjectCategory { TypeName = "Mesh", Count = 567, AllocatedBytes = 120_000_000 },
            ],
            SummaryAvailable = true,
            SchemaVersion = "1.1",
        };
    }

    [Fact]
    public void Format_RendersMetadataTotalsAndBreakdowns()
    {
        var text = SummaryReportFormatter.Format(BuildReport());

        Assert.Contains("Memory Usage Summary", text, StringComparison.Ordinal);
        Assert.Contains("Game_IOS.snap (snapshot)", text, StringComparison.Ordinal);
        Assert.Contains("iOS (IPhonePlayer)", text, StringComparison.Ordinal);
        Assert.Contains("v17", text, StringComparison.Ordinal);
        Assert.Contains("Schema      : 1.1", text, StringComparison.Ordinal);
        Assert.Contains("Allocated Memory Distribution", text, StringComparison.Ordinal);
        Assert.Contains("Managed Heap Utilization", text, StringComparison.Ordinal);
        Assert.Contains("Top Unity Object Categories", text, StringComparison.Ordinal);
        Assert.Contains("Texture2D", text, StringComparison.Ordinal);
        // Exact byte total appears alongside the human-readable size.
        Assert.Contains("2,909,880,164 B", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_UnavailableResident_ShowsDash()
    {
        var text = SummaryReportFormatter.Format(BuildReport());

        // Graphics has no resident measurement.
        Assert.Contains("—", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_SwappedAvailable_RendersSwappedColumnAndTotal()
    {
        var text = SummaryReportFormatter.Format(BuildReport());

        Assert.Contains("Total Swapped", text, StringComparison.Ordinal);
        Assert.Contains("104,857,600 B", text, StringComparison.Ordinal);
        Assert.Contains("Swapped", text, StringComparison.Ordinal);
        // Native has a measured swapped value (50 MB).
        Assert.Contains("50.00 MB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_SwappedUnavailable_ShowsDashForTotalSwapped()
    {
        var report = BuildReport();
        report.Metrics.SwappedAvailable = false;

        var text = SummaryReportFormatter.Format(report);

        var totalSwappedLine = text
            .Split('\n')
            .Single(l => l.Contains("Total Swapped", StringComparison.Ordinal));
        Assert.Contains("—", totalSwappedLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_MissingSummary_PrintsReexportNotice()
    {
        var report = new SummaryReport
        {
            SourcePath = "/captures/old.duckdb",
            Source = SummarySource.Database,
            Info = new SnapshotInfo { ProductName = "Old", SnapshotPath = "/captures/old.snap" },
            Metrics = new SummaryMetrics(),
            SummaryAvailable = false,
        };

        var text = SummaryReportFormatter.Format(report);

        Assert.Contains("no summary_metrics table", text, StringComparison.Ordinal);
        // An explicit, runnable re-export example is offered using the known snapshot/db paths.
        Assert.Contains("export \"/captures/old.snap\" \"/captures/old.duckdb\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Allocated Memory Distribution", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FromNativeObjects_GroupsByType_SumsLiveObjects_SortedDescending()
    {
        var objects = new[]
        {
            new NativeObjectRow { NativeTypeName = "Mesh", SizeBytes = 100, IsDestroyed = false },
            new NativeObjectRow { NativeTypeName = "Texture2D", SizeBytes = 300, IsDestroyed = false },
            new NativeObjectRow { NativeTypeName = "Texture2D", SizeBytes = 500, IsDestroyed = false },
            new NativeObjectRow { NativeTypeName = "Texture2D", SizeBytes = 9999, IsDestroyed = true },
            new NativeObjectRow { NativeTypeName = "", SizeBytes = 50, IsDestroyed = false },
        };

        var categories = UnityObjectCategories.FromNativeObjects(objects);

        Assert.Equal(3, categories.Count);
        // Sorted by allocated bytes descending: Texture2D (800), Mesh (100), (unknown) (50).
        Assert.Equal("Texture2D", categories[0].TypeName);
        Assert.Equal(800UL, categories[0].AllocatedBytes);
        Assert.Equal(2, categories[0].Count); // destroyed Texture2D excluded
        Assert.Equal("Mesh", categories[1].TypeName);
        Assert.Equal("(unknown)", categories[2].TypeName);
    }
}

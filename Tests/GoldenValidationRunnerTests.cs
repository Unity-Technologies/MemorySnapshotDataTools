using System.Text.Json;
using MemorySnapshotDataTools.Validation;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Tests for <see cref="GoldenValidationRunner"/>.
/// </summary>
public sealed class GoldenValidationRunnerTests
{
    /// <summary>
    /// Passing validation when exported SQLite metrics match golden JSON.
    /// </summary>
    [Fact]
    public void Validate_MatchingSqliteExport_Passes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "msdt_golden_validate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var goldenPath = Path.Combine(tempDir, "sample_golden.json");
        var dbPath = Path.Combine(tempDir, "sample.db");

        try
        {
            WriteGolden(goldenPath);
            WriteMatchingDatabase(dbPath);

            var result = GoldenValidationRunner.Validate(goldenPath, dbPath);
            Assert.True(result.Passed);
            Assert.Empty(result.Failures);
        }
        finally
        {
            // Release pooled SQLite handles so the temp directory can be deleted on Windows.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Reports count mismatch when export differs from golden.
    /// </summary>
    [Fact]
    public void Validate_MismatchedCount_Fails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "msdt_golden_validate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var goldenPath = Path.Combine(tempDir, "sample_golden.json");
        var dbPath = Path.Combine(tempDir, "sample.db");

        try
        {
            WriteGolden(goldenPath);
            WriteMatchingDatabase(dbPath, serializedFileCount: 2);

            var result = GoldenValidationRunner.Validate(goldenPath, dbPath);
            Assert.False(result.Passed);
            Assert.Contains(result.Failures, f => f.StartsWith("SerializedFile.Count:", StringComparison.Ordinal));
        }
        finally
        {
            // Release pooled SQLite handles so the temp directory can be deleted on Windows.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Reports a summary-category mismatch when the export's Native committed bytes differ from golden.
    /// </summary>
    [Fact]
    public void Validate_MismatchedSummaryNative_Fails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "msdt_golden_validate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var goldenPath = Path.Combine(tempDir, "sample_golden.json");
        var dbPath = Path.Combine(tempDir, "sample.db");

        try
        {
            WriteGolden(goldenPath);
            WriteMatchingDatabase(dbPath, nativeCommittedOverride: 9000);

            var result = GoldenValidationRunner.Validate(goldenPath, dbPath);
            Assert.False(result.Passed);
            Assert.Contains(result.Failures, f =>
                f.StartsWith("Summary[AllocatedMemoryDistribution].Native.Committed:", StringComparison.Ordinal));
        }
        finally
        {
            // Release pooled SQLite handles so the temp directory can be deleted on Windows.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteGolden(string goldenPath)
    {
        var golden = new
        {
            SnapshotName = "sample",
            SnapshotPath = "/tmp/sample.snap",
            FormatVersion = 17,
            ExtractedAtUtc = "2026-01-01T00:00:00.0000000Z",
            NativeTypeMetrics = new[]
            {
                new { NativeTypeName = "AssetBundle", Count = 1, AllocatedBytes = 100L, ResidentBytes = 50L },
                new { NativeTypeName = "SerializedFile", Count = 1, AllocatedBytes = 200L, ResidentBytes = 80L },
            },
            NativeRootMetrics = new[]
            {
                new
                {
                    AreaName = "PersistentManager.Remapper",
                    ObjectName = "Remapper",
                    AllocatedBytes = 300L,
                    ResidentBytes = 300L,
                },
            },
            TotalAllocatedBytes = 10_000_000L,
            TotalResidentBytes = 4_000_000L,
            AllocatedMemoryDistribution = new[]
            {
                new { Name = "Native", CommittedBytes = 5_000_000L, ResidentBytes = 3_000_000L, ResidentAvailable = true },
                new { Name = "Managed", CommittedBytes = 2_000_000L, ResidentBytes = 1_000_000L, ResidentAvailable = true },
                new { Name = "Executables & Mapped", CommittedBytes = 1_000_000L, ResidentBytes = 0L, ResidentAvailable = true },
                new { Name = "Graphics (Estimated)", CommittedBytes = 1_000_000L, ResidentBytes = 0L, ResidentAvailable = false },
                new { Name = "Untracked", CommittedBytes = 1_000_000L, ResidentBytes = 0L, ResidentAvailable = false },
            },
            ManagedHeapUtilization = new[]
            {
                new { Name = "Virtual Machine", CommittedBytes = 1_500_000L, ResidentBytes = 800_000L, ResidentAvailable = true },
                new { Name = "Objects", CommittedBytes = 400_000L, ResidentBytes = 200_000L, ResidentAvailable = true },
                new { Name = "Empty Heap Space", CommittedBytes = 100_000L, ResidentBytes = 0L, ResidentAvailable = true },
            },
        };
        File.WriteAllText(goldenPath, JsonSerializer.Serialize(golden));
    }

    private static void WriteMatchingDatabase(string dbPath, int serializedFileCount = 1, long? nativeCommittedOverride = null)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE native_objects (
                    native_object_index INTEGER PRIMARY KEY,
                    instance_id TEXT,
                    name TEXT,
                    size_bytes INTEGER,
                    native_object_address INTEGER,
                    root_reference_id INTEGER,
                    type_index INTEGER,
                    native_type_name TEXT,
                    is_destroyed INTEGER,
                    resident_size_bytes INTEGER
                );
                CREATE TABLE native_roots (
                    root_index INTEGER PRIMARY KEY,
                    root_id INTEGER,
                    area_name TEXT,
                    object_name TEXT,
                    accumulated_size_bytes INTEGER,
                    resident_size_bytes INTEGER
                );
                CREATE TABLE summary_metrics (
                    metric_group TEXT NOT NULL,
                    category TEXT NOT NULL,
                    committed_bytes INTEGER NOT NULL,
                    resident_bytes INTEGER NOT NULL,
                    resident_available INTEGER NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        var nativeCommitted = nativeCommittedOverride ?? 5_000_000L;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO summary_metrics VALUES ('Totals','Total',10000000,4000000,1);
                INSERT INTO summary_metrics VALUES ('AllocatedMemoryDistribution','Native',{nativeCommitted},3000000,1);
                INSERT INTO summary_metrics VALUES ('AllocatedMemoryDistribution','Managed',2000000,1000000,1);
                INSERT INTO summary_metrics VALUES ('AllocatedMemoryDistribution','Executables & Mapped',1000000,0,1);
                INSERT INTO summary_metrics VALUES ('AllocatedMemoryDistribution','Graphics (Estimated)',1000000,0,0);
                INSERT INTO summary_metrics VALUES ('AllocatedMemoryDistribution','Untracked',1000000,0,0);
                INSERT INTO summary_metrics VALUES ('ManagedHeapUtilization','Virtual Machine',1500000,800000,1);
                INSERT INTO summary_metrics VALUES ('ManagedHeapUtilization','Objects',400000,200000,1);
                INSERT INTO summary_metrics VALUES ('ManagedHeapUtilization','Empty Heap Space',100000,0,1);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO native_objects VALUES (0,'1','ab',100,0,2,0,'AssetBundle',0,50);
                INSERT INTO native_roots VALUES (1,2,'AssetBundle','bundle',100,50);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO native_roots VALUES (0,1,'SerializedFile','file-a',200,80);
                INSERT INTO native_roots VALUES (2,3,'PersistentManager.Remapper','Remapper',300,300);
                """;
            for (var i = 1; i < serializedFileCount; i++)
                cmd.CommandText += $"INSERT INTO native_roots VALUES ({i + 3},{i + 10},'SerializedFile','file-{i}',0,0);";
            cmd.ExecuteNonQuery();
        }
    }
}

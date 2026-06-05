using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;

namespace MemorySnapshotDataTools.Validation;

/// <summary>
/// Compares an exported DuckDB or SQLite database against a Unity golden JSON file.
/// </summary>
public static class GoldenValidationRunner
{
    private static readonly string[] TrackedTypeNames =
    [
        GoldenValidationQueries.AssetBundleNativeTypeName,
        GoldenValidationQueries.SerializedFileMetricName,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Loads golden JSON, queries <paramref name="databasePath"/>, and returns a validation result.
    /// </summary>
    /// <param name="goldenPath">Path to <c>*_golden.json</c> from Unity.</param>
    /// <param name="databasePath">Path to an exported <c>.duckdb</c> or <c>.db</c> file.</param>
    /// <returns>Validation outcome with any metric mismatches.</returns>
    /// <exception cref="FileNotFoundException">When golden or database file is missing.</exception>
    /// <exception cref="InvalidDataException">When golden JSON cannot be parsed.</exception>
    /// <exception cref="NotSupportedException">When the database extension is not supported.</exception>
    public static GoldenValidationResult Validate(string goldenPath, string databasePath)
    {
        goldenPath = Path.GetFullPath(goldenPath);
        databasePath = Path.GetFullPath(databasePath);

        if (!File.Exists(goldenPath))
            throw new FileNotFoundException($"Golden file not found: {goldenPath}", goldenPath);

        if (!File.Exists(databasePath))
            throw new FileNotFoundException($"Database file not found: {databasePath}", databasePath);

        var golden = LoadGolden(goldenPath);
        var exported = QueryExportedMetrics(databasePath);
        var failures = CompareMetrics(golden, exported);

        return new GoldenValidationResult
        {
            SnapshotName = golden.SnapshotName ?? Path.GetFileNameWithoutExtension(goldenPath),
            GoldenPath = goldenPath,
            DatabasePath = databasePath,
            ValidatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Passed = failures.Count == 0,
            Failures = failures.ToArray(),
        };
    }

    /// <summary>
    /// Validates and writes <paramref name="result"/> JSON to <paramref name="outputPath"/>.
    /// </summary>
    /// <returns>0 when validation passed, 1 when mismatches were found.</returns>
    public static int ValidateAndWriteResult(string goldenPath, string databasePath, string? outputPath)
    {
        var result = Validate(goldenPath, databasePath);
        var json = JsonSerializer.Serialize(result, JsonOptions);

        var resolvedOutput = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(
                Path.GetDirectoryName(goldenPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(goldenPath) + "_validation_result.json")
            : Path.GetFullPath(outputPath);

        var outputDirectory = Path.GetDirectoryName(resolvedOutput);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(resolvedOutput, json);

        Console.WriteLine($"Result written: {resolvedOutput}");
        Console.WriteLine(json);

        if (result.Passed)
        {
            Console.WriteLine($"Validation PASSED for {result.SnapshotName}");
            return 0;
        }

        Console.Error.WriteLine($"Validation FAILED for {result.SnapshotName}");
        foreach (var failure in result.Failures)
            Console.Error.WriteLine($"  - {failure}");
        return 1;
    }

    private static GoldenSnapshotFile LoadGolden(string goldenPath)
    {
        try
        {
            var json = File.ReadAllText(goldenPath);
            var golden = JsonSerializer.Deserialize<GoldenSnapshotFile>(json, JsonOptions);
            if (golden == null)
                throw new InvalidDataException("Golden JSON deserialized to null.");
            return golden;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Failed to parse golden JSON: {ex.Message}", ex);
        }
    }

    private static ExportedMetrics QueryExportedMetrics(string databasePath)
    {
        var extension = Path.GetExtension(databasePath);
        return extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            ? QuerySqlite(databasePath)
            : extension.Equals(".duckdb", StringComparison.OrdinalIgnoreCase)
                ? QueryDuckDb(databasePath)
                : throw new NotSupportedException(
                    $"Unsupported database extension '{extension}'. Use .duckdb or .db.");
    }

    private static ExportedMetrics QueryDuckDb(string databasePath)
    {
        // Validation only reads; open read-only (defense-in-depth, per CLAUDE.md rule 5).
        using var connection = new DuckDBConnection($"Data Source={databasePath};ACCESS_MODE=READ_ONLY");
        connection.Open();
        return Query(connection, isDuckDb: true);
    }

    private static ExportedMetrics QuerySqlite(string databasePath)
    {
        // Validation only reads; open read-only (defense-in-depth, per CLAUDE.md rule 5).
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        return Query(connection, isDuckDb: false);
    }

    private static ExportedMetrics Query(object connection, bool isDuckDb)
    {
        var typeMetrics = new Dictionary<string, GoldenNativeTypeMetric>(StringComparer.Ordinal);
        foreach (var typeName in TrackedTypeNames)
        {
            typeMetrics[typeName] = new GoldenNativeTypeMetric
            {
                NativeTypeName = typeName,
                Count = 0,
                AllocatedBytes = 0,
                ResidentBytes = 0,
            };
        }

        ReadTypeAggregate(connection, isDuckDb, GoldenValidationQueries.AssetBundleSql,
            GoldenValidationQueries.AssetBundleNativeTypeName, typeMetrics);
        ReadTypeAggregate(connection, isDuckDb, GoldenValidationQueries.SerializedFileSql,
            GoldenValidationQueries.SerializedFileMetricName, typeMetrics);

        var remapperSql = isDuckDb
            ? GoldenValidationQueries.RemapperRootsDuckDbSql
            : GoldenValidationQueries.RemapperRootsSqliteSql;
        var remapperRoots = ReadRemapperRoots(connection, isDuckDb, remapperSql);
        var summaryCategories = ReadSummaryCategories(connection, isDuckDb);

        return new ExportedMetrics
        {
            TypeMetrics = typeMetrics,
            RemapperRoots = remapperRoots,
            SummaryCategories = summaryCategories,
        };
    }

    private static Dictionary<string, GoldenSummaryCategory> ReadSummaryCategories(object connection, bool isDuckDb)
    {
        var result = new Dictionary<string, GoldenSummaryCategory>(StringComparer.Ordinal);
        using var cmd = isDuckDb
            ? (System.Data.Common.DbCommand)((DuckDBConnection)connection).CreateCommand()
            : ((SqliteConnection)connection).CreateCommand();
        cmd.CommandText = GoldenValidationQueries.SummaryMetricsSql;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var group = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var category = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            result[SummaryKey(group, category)] = new GoldenSummaryCategory
            {
                Name = category,
                CommittedBytes = DbScalarReader.GetInt64(reader, 2),
                ResidentBytes = DbScalarReader.GetInt64(reader, 3),
                ResidentAvailable = DbScalarReader.GetInt64(reader, 4) != 0,
            };
        }

        return result;
    }

    private static string SummaryKey(string group, string category) => $"{group}/{category}";

    private static void ReadTypeAggregate(
        object connection,
        bool isDuckDb,
        string sql,
        string typeName,
        Dictionary<string, GoldenNativeTypeMetric> typeMetrics)
    {
        if (isDuckDb)
        {
            using var cmd = ((DuckDBConnection)connection).CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return;

            typeMetrics[typeName] = new GoldenNativeTypeMetric
            {
                NativeTypeName = typeName,
                Count = DbScalarReader.GetInt32(reader, 0),
                AllocatedBytes = DbScalarReader.GetInt64(reader, 1),
                ResidentBytes = DbScalarReader.GetInt64(reader, 2),
            };
            return;
        }

        using var sqliteCmd = ((SqliteConnection)connection).CreateCommand();
        sqliteCmd.CommandText = sql;
        using var sqliteReader = sqliteCmd.ExecuteReader();
        if (!sqliteReader.Read())
            return;

        typeMetrics[typeName] = new GoldenNativeTypeMetric
        {
            NativeTypeName = typeName,
            Count = sqliteReader.GetInt32(0),
            AllocatedBytes = sqliteReader.GetInt64(1),
            ResidentBytes = sqliteReader.GetInt64(2),
        };
    }

    private static List<GoldenNativeRootMetric> ReadRemapperRoots(object connection, bool isDuckDb, string sql)
    {
        var roots = new List<GoldenNativeRootMetric>();
        if (isDuckDb)
        {
            using var cmd = ((DuckDBConnection)connection).CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                roots.Add(ReadRootRow(reader));
            return roots;
        }

        using var sqliteCmd = ((SqliteConnection)connection).CreateCommand();
        sqliteCmd.CommandText = sql;
        using var sqliteReader = sqliteCmd.ExecuteReader();
        while (sqliteReader.Read())
            roots.Add(ReadRootRow(sqliteReader));
        return roots;
    }

    private static GoldenNativeRootMetric ReadRootRow(System.Data.Common.DbDataReader reader) =>
        new()
        {
            AreaName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            ObjectName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            AllocatedBytes = DbScalarReader.GetInt64(reader, 2),
            ResidentBytes = DbScalarReader.GetInt64(reader, 3),
        };

    private static List<string> CompareMetrics(GoldenSnapshotFile golden, ExportedMetrics exported)
    {
        var failures = new List<string>();

        foreach (var typeName in TrackedTypeNames)
        {
            var expected = golden.NativeTypeMetrics?.FirstOrDefault(m =>
                string.Equals(m.NativeTypeName, typeName, StringComparison.Ordinal));
            exported.TypeMetrics.TryGetValue(typeName, out var actual);

            expected ??= new GoldenNativeTypeMetric { NativeTypeName = typeName };
            actual ??= new GoldenNativeTypeMetric { NativeTypeName = typeName };

            if (expected.Count != actual.Count)
                failures.Add($"{typeName}.Count: expected={expected.Count}, actual={actual.Count}");

            if (expected.AllocatedBytes != actual.AllocatedBytes)
                failures.Add($"{typeName}.AllocatedBytes: expected={expected.AllocatedBytes}, actual={actual.AllocatedBytes}");

            if (!ResidentBytesMatch(expected.ResidentBytes, actual.ResidentBytes))
                failures.Add($"{typeName}.ResidentBytes: expected={expected.ResidentBytes}, actual={actual.ResidentBytes}");
        }

        var expectedPmrAlloc = golden.NativeRootMetrics?.Sum(r => r.AllocatedBytes) ?? 0;
        var expectedPmrRes = golden.NativeRootMetrics?.Sum(r => r.ResidentBytes) ?? 0;
        var actualPmrAlloc = exported.RemapperRoots.Sum(r => r.AllocatedBytes);
        var actualPmrRes = exported.RemapperRoots.Sum(r => r.ResidentBytes);

        if (expectedPmrAlloc != actualPmrAlloc)
            failures.Add($"PMR.AllocatedBytes: expected={expectedPmrAlloc}, actual={actualPmrAlloc}");

        if (!ResidentBytesMatch(expectedPmrRes, actualPmrRes))
            failures.Add($"PMR.ResidentBytes: expected={expectedPmrRes}, actual={actualPmrRes}");

        CompareSummary(golden, exported, failures);

        return failures;
    }

    /// <summary>
    /// Compares the MemoryProfiler Summary-page metrics. Skips entirely when golden has no summary data
    /// (older golden files), so it remains backward compatible.
    /// </summary>
    private static void CompareSummary(GoldenSnapshotFile golden, ExportedMetrics exported, List<string> failures)
    {
        var hasSummary = golden.AllocatedMemoryDistribution is { Length: > 0 }
            || golden.ManagedHeapUtilization is { Length: > 0 };
        if (!hasSummary)
            return;

        // Totals row.
        if (exported.SummaryCategories.TryGetValue(
                SummaryKey(SummaryMetricsTable.GroupTotals, SummaryMetricsTable.CategoryTotal), out var actualTotal))
        {
            if (!CommittedBytesMatch(golden.TotalAllocatedBytes, actualTotal.CommittedBytes, estimated: false))
                failures.Add($"Summary.TotalAllocated: expected={golden.TotalAllocatedBytes}, actual={actualTotal.CommittedBytes}");
            if (!ResidentBytesMatch(golden.TotalResidentBytes, actualTotal.ResidentBytes))
                failures.Add($"Summary.TotalResident: expected={golden.TotalResidentBytes}, actual={actualTotal.ResidentBytes}");
        }
        else
        {
            failures.Add("Summary.Total: row missing from export");
        }

        CompareSummaryGroup(SummaryMetricsTable.GroupAllocatedMemoryDistribution, golden.AllocatedMemoryDistribution, exported, failures);
        CompareSummaryGroup(SummaryMetricsTable.GroupManagedHeapUtilization, golden.ManagedHeapUtilization, exported, failures);
    }

    private static void CompareSummaryGroup(
        string group,
        GoldenSummaryCategory[]? expectedRows,
        ExportedMetrics exported,
        List<string> failures)
    {
        if (expectedRows == null)
            return;

        foreach (var expected in expectedRows)
        {
            var name = expected.Name ?? string.Empty;
            // Memory Profiler labels Untracked as "Untracked*"; tolerate the asterisk variant.
            var key = SummaryKey(group, name.TrimEnd('*'));
            if (!exported.SummaryCategories.TryGetValue(key, out var actual))
            {
                failures.Add($"Summary[{group}].{name}: row missing from export");
                continue;
            }

            // Graphics and Untracked are estimated from platform stats, so allow a looser committed tolerance.
            var estimated = !expected.ResidentAvailable;
            if (!CommittedBytesMatch(expected.CommittedBytes, actual.CommittedBytes, estimated))
                failures.Add($"Summary[{group}].{name}.Committed: expected={expected.CommittedBytes}, actual={actual.CommittedBytes}");

            if (expected.ResidentAvailable && actual.ResidentAvailable &&
                !ResidentBytesMatch(expected.ResidentBytes, actual.ResidentBytes))
                failures.Add($"Summary[{group}].{name}.Resident: expected={expected.ResidentBytes}, actual={actual.ResidentBytes}");
        }
    }

    /// <summary>
    /// Compares committed bytes with tolerance. Estimated categories (Graphics, Untracked) depend on
    /// platform heuristics, so they get a looser 5% / 1 MB tolerance; others use 1% / 64 KB.
    /// </summary>
    private static bool CommittedBytesMatch(long expected, long actual, bool estimated)
    {
        if (expected == actual)
            return true;

        var delta = Math.Abs(expected - actual);
        var basis = Math.Max(Math.Abs(expected), Math.Abs(actual));
        var relative = estimated ? 0.05 : 0.01;
        var absolute = estimated ? 1_048_576L : 65_536L;
        var tolerance = Math.Max(absolute, (long)(basis * relative));
        return delta <= tolerance;
    }

    /// <summary>
    /// Allows up to 1% relative difference on resident totals to account for minor
    /// divergence from Unity's full memory-map post-processing while catching large regressions.
    /// </summary>
    private static bool ResidentBytesMatch(long expected, long actual)
    {
        if (expected == actual)
            return true;

        var delta = Math.Abs(expected - actual);
        var basis = Math.Max(Math.Abs(expected), Math.Abs(actual));
        var tolerance = Math.Max(65_536L, (long)(basis * 0.01));
        return delta <= tolerance;
    }
}

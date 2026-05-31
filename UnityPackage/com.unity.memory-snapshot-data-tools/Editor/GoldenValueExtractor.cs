using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Extracts golden memory metrics from the Unity Memory Profiler for a .snap file and writes JSON for validation.
/// </summary>
public static class GoldenValueExtractor
{
    private static readonly string[] TrackedTypes =
    {
        MemorySnapshotValidationHelpers.AssetBundleNativeTypeName,
        MemorySnapshotValidationHelpers.SerializedFileMetricName,
    };

    private const string MemoryProfilerEditorAssemblyName = "Unity.MemoryProfiler.Editor";

    /// <summary>
    /// Menu command: pick a .snap file and write <c>{name}_golden.json</c> alongside it.
    /// </summary>
    [MenuItem("Tools/Memory Snapshot Validation/Extract Golden Values")]
    public static void ExtractGoldenValuesMenu()
    {
        var snapshotPath = EditorUtility.OpenFilePanel("Select Memory Snapshot", Application.dataPath, "snap");
        if (string.IsNullOrEmpty(snapshotPath))
            return;

        using var progress = new GoldenExtractionProgress("Extract Golden Values");

        try
        {
            if (!TryExtract(snapshotPath, progress, out var golden, out var error))
            {
                if (!progress.IsCancelled)
                    Debug.LogError($"Failed to extract golden values.\n{error}");
                return;
            }

            progress.Report(0.95f, "Writing golden JSON...");
            var outPath = Path.Combine(
                Path.GetDirectoryName(snapshotPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(snapshotPath) + "_golden.json");

            var json = JsonUtility.ToJson(golden, prettyPrint: true);
            File.WriteAllText(outPath, json);
            progress.Report(1f, "Done");
            Debug.Log($"Golden values written: {outPath}\n{FormatSummary(golden)}");
            EditorUtility.RevealInFinder(outPath);
            EditorUtility.DisplayDialog(
                "Golden values extracted",
                $"Saved next to the snapshot:\n\n{outPath}",
                "OK");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Golden value extraction cancelled.");
        }
        finally
        {
            progress.Clear();
        }
    }

    /// <summary>
    /// Extracts golden metrics from a snapshot file using Memory Profiler <c>ProcessedNativeRoots</c>.
    /// </summary>
    public static bool TryExtract(string snapshotPath, out GoldenSnapshot golden, out string error) =>
        TryExtract(snapshotPath, null, out golden, out error);

    /// <summary>
    /// Extracts golden metrics from a snapshot file using Memory Profiler <c>ProcessedNativeRoots</c>.
    /// </summary>
    public static bool TryExtract(
        string snapshotPath,
        GoldenExtractionProgress progress,
        out GoldenSnapshot golden,
        out string error)
    {
        golden = null;
        error = string.Empty;

        if (!File.Exists(snapshotPath))
        {
            error = $"Snapshot not found: {snapshotPath}";
            return false;
        }

        progress?.Report(0.02f, "Opening snapshot...");
        if (!MemoryProfilerSnapshotLoader.TryLoad(
                snapshotPath,
                out var snapshot,
                out var dispose,
                out error,
                (postProcessRatio, message) =>
                {
                    // Post-process dominates load time; map it to 0.05–0.75 of the overall bar.
                    var mapped = 0.05f + postProcessRatio * 0.70f;
                    progress?.Report(mapped, message);
                    progress?.ThrowIfCancelled();
                }))
            return false;

        using (dispose)
        {
            try
            {
                progress?.ThrowIfCancelled();
                progress?.Report(0.76f, "Reading snapshot metadata...");
                golden = ExtractFromCachedSnapshot(snapshot, snapshotPath, progress);
                progress?.Report(0.94f, "Golden metrics extracted");
                return true;
            }
            catch (OperationCanceledException)
            {
                progress!.IsCancelled = true;
                error = "Cancelled.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
        }
    }

    private static GoldenSnapshot ExtractFromCachedSnapshot(object snapshot, string snapshotPath, GoldenExtractionProgress progress)
    {
        var snapshotType = snapshot.GetType();
        var formatVersion = ReadFormatVersion(snapshot, snapshotType);

        var typeMetrics = ExtractNativeTypeMetrics(snapshot, snapshotType, progress);
        var rootMetrics = ExtractRemapperRootMetrics(snapshot, snapshotType, progress);

        var golden = new GoldenSnapshot
        {
            SnapshotName = Path.GetFileNameWithoutExtension(snapshotPath),
            SnapshotPath = snapshotPath,
            FormatVersion = formatVersion,
            ExtractedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            NativeTypeMetrics = typeMetrics.Values.OrderBy(m => m.NativeTypeName, StringComparer.Ordinal).ToArray(),
            NativeRootMetrics = rootMetrics.ToArray(),
            AllocatedMemoryDistribution = Array.Empty<SummaryCategoryMetric>(),
            ManagedHeapUtilization = Array.Empty<SummaryCategoryMetric>(),
        };

        ExtractSummaryMetrics(snapshot, golden, progress);
        return golden;
    }

    /// <summary>
    /// Populates the All Memory and Managed Memory Summary-page metrics on <paramref name="golden"/> by
    /// invoking the Memory Profiler's own summary model builders, so the golden values match the UI exactly.
    /// On failure the summary arrays are left empty and a warning is logged (native metric extraction still succeeds).
    /// </summary>
    private static void ExtractSummaryMetrics(object snapshot, GoldenSnapshot golden, GoldenExtractionProgress progress)
    {
        try
        {
            progress?.ThrowIfCancelled();
            progress?.Report(0.93f, "Building summary models...");

            var allModel = BuildSummaryModel(snapshot, "Unity.MemoryProfiler.Editor.UI.AllMemorySummaryModelBuilder");
            ReadSummaryModel(allModel, out var allTotalAllocated, out var allRows);

            var managedModel = BuildSummaryModel(snapshot, "Unity.MemoryProfiler.Editor.UI.ManagedMemorySummaryModelBuilder");
            ReadSummaryModel(managedModel, out _, out var managedRows);

            golden.AllocatedMemoryDistribution = allRows.ToArray();
            golden.ManagedHeapUtilization = managedRows.ToArray();
            golden.TotalAllocatedBytes = allTotalAllocated;

            // Total Resident must include the resident pages backing the Graphics and Untracked regions,
            // whose per-category resident is suppressed in the All Memory rows. ResidentMemorySummaryModelBuilder
            // sums resident across every flattened span (the value the "Memory Usage On Device" widget shows),
            // so read it from there rather than summing the All Memory rows.
            var residentModel = BuildSummaryModel(snapshot, "Unity.MemoryProfiler.Editor.UI.ResidentMemorySummaryModelBuilder");
            ReadSummaryModel(residentModel, out _, out var residentRows);
            golden.TotalResidentBytes = residentRows.Count > 0
                ? residentRows[0].ResidentBytes
                : allRows.Sum(r => r.ResidentBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to extract Memory Profiler Summary-page metrics; leaving them empty.\n{ex}");
        }
    }

    /// <summary>
    /// Resolves a summary model builder type by full name from the loaded <c>Unity.MemoryProfiler.Editor</c>
    /// assembly, constructs it with the loaded <c>CachedSnapshot</c> (snapshotB null), invokes <c>Build()</c>,
    /// and returns the resulting <c>MemorySummaryModel</c> instance.
    /// </summary>
    private static object BuildSummaryModel(object snapshot, string builderTypeFullName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == MemoryProfilerEditorAssemblyName);
        if (assembly == null)
            throw new InvalidOperationException($"Could not find assembly '{MemoryProfilerEditorAssemblyName}'.");

        var builderType = assembly.GetType(builderTypeFullName, throwOnError: false);
        if (builderType == null)
            throw new InvalidOperationException($"Could not resolve summary builder type '{builderTypeFullName}'.");

        var builder = Activator.CreateInstance(builderType, snapshot, null);
        var buildMethod = builderType.GetMethod("Build", BindingFlags.Instance | BindingFlags.Public);
        if (buildMethod == null)
            throw new InvalidOperationException($"Could not resolve 'Build' on '{builderTypeFullName}'.");

        return buildMethod.Invoke(builder, Array.Empty<object>());
    }

    /// <summary>
    /// Reads a <c>MemorySummaryModel</c> into its total allocated bytes and per-category metric rows.
    /// </summary>
    private static void ReadSummaryModel(object model, out long totalAllocated, out List<SummaryCategoryMetric> rows)
    {
        totalAllocated = 0;
        rows = new List<SummaryCategoryMetric>();
        if (model == null)
            return;

        var modelType = model.GetType();
        totalAllocated = Convert.ToInt64(GetPropertyValue(model, modelType, "TotalA") ?? 0L);

        if (GetPropertyValue(model, modelType, "Rows") is not IEnumerable rowEnumerable)
            return;

        foreach (var row in rowEnumerable)
        {
            if (row == null)
                continue;

            // Row is a struct; reflect on its boxed runtime type.
            var rowType = row.GetType();
            var name = GetPropertyValue(row, rowType, "Name") as string ?? string.Empty;
            if (name.EndsWith("*", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 1);

            var baseSize = GetPropertyValue(row, rowType, "BaseSize");
            var (committed, resident) = ReadMemorySize(baseSize);
            var residentAvailable = !Convert.ToBoolean(GetPropertyValue(row, rowType, "ResidentSizeUnavailable") ?? false);

            rows.Add(new SummaryCategoryMetric
            {
                Name = name,
                CommittedBytes = (long)committed,
                ResidentBytes = (long)resident,
                ResidentAvailable = residentAvailable,
            });
        }
    }

    /// <summary>
    /// Reads <c>Committed</c> and <c>Resident</c> off a boxed <c>MemorySize</c> (fields or properties).
    /// </summary>
    private static (ulong Committed, ulong Resident) ReadMemorySize(object memorySizeBoxed)
    {
        if (memorySizeBoxed == null)
            return (0, 0);

        var msType = memorySizeBoxed.GetType();
        var committed = Convert.ToUInt64(
            GetFieldValue(memorySizeBoxed, msType, "Committed")
            ?? GetPropertyValue(memorySizeBoxed, msType, "Committed")
            ?? 0UL);
        var resident = Convert.ToUInt64(
            GetFieldValue(memorySizeBoxed, msType, "Resident")
            ?? GetPropertyValue(memorySizeBoxed, msType, "Resident")
            ?? 0UL);
        return (committed, resident);
    }

    private static int ReadFormatVersion(object snapshot, Type snapshotType)
    {
        // The snapshot format version is NOT carried on MetaData (Unity.MemoryProfiler.Editor.Format.MetaData has
        // Unity-version fields but no FormatVersion member). The CachedSnapshot stores it in the private field
        // m_SnapshotVersion (enum Unity.MemoryProfiler.Editor.Format.FormatVersion : uint), assigned from
        // reader.FormatVersion in its constructor. See:
        //   Editor/MemorySnapshot/Cached/CachedSnapshot.cs ("FormatVersion m_SnapshotVersion;")
        //   Editor/MemorySnapshot/Reader/IReader.cs ("enum FormatVersion : uint").
        var versionField = snapshotType.GetField("m_SnapshotVersion", BindingFlags.Instance | BindingFlags.NonPublic);
        if (versionField != null)
        {
            var value = versionField.GetValue(snapshot);
            if (value != null)
                return Convert.ToInt32(value); // enum : uint -> underlying numeric format version
        }

        // Fallback: try MetaData (kept for resilience; historically yields 0 for the current format).
        var meta = snapshotType.GetProperty("MetaData", BindingFlags.Instance | BindingFlags.Public)?.GetValue(snapshot);
        var metaVersionField = meta?.GetType().GetField("FormatVersion", BindingFlags.Instance | BindingFlags.Public);
        if (metaVersionField != null)
            return Convert.ToInt32(metaVersionField.GetValue(meta));

        return 0;
    }

    private static Dictionary<string, NativeTypeMetric> ExtractNativeTypeMetrics(
        object snapshot,
        Type snapshotType,
        GoldenExtractionProgress progress)
    {
        var result = TrackedTypes.ToDictionary(
            t => t,
            t => new NativeTypeMetric { NativeTypeName = t },
            StringComparer.Ordinal);

        var processedRoots = GetFieldValue(snapshot, snapshotType, "ProcessedNativeRoots");
        if (processedRoots == null)
            return result;

        var processedType = processedRoots.GetType();
        var data = GetFieldValue(processedRoots, processedType, "Data");
        if (data == null)
            return result;

        progress?.Report(0.78f, "Materializing processed native roots...");
        var entries = MaterializeArray(data);
        var nativeObjects = GetFieldValue(snapshot, snapshotType, "NativeObjects");
        var nativeTypes = GetFieldValue(snapshot, snapshotType, "NativeTypes");
        var typeNames = GetFieldValue(nativeTypes, nativeTypes.GetType(), "TypeName") as string[] ?? Array.Empty<string>();
        var typeIndices = GetFieldValue(nativeObjects, nativeObjects.GetType(), "NativeTypeArrayIndex");

        var lastReportTicks = 0L;
        var nextReportCount = 1;
        for (var i = 0; i < entries.Length; i++)
        {
            ReportLoopProgress(
                progress,
                "Aggregating native metrics",
                0.80f,
                0.88f,
                i + 1,
                entries.Length,
                ref lastReportTicks,
                ref nextReportCount);

            var entry = entries[i];
            if (entry == null)
                continue;

            var entryType = entry.GetType();
            var sourceIndex = GetFieldValue(entry, entryType, "NativeObjectOrRootIndex");
            if (sourceIndex == null)
                continue;

            var nativeSize = GetMemorySize(GetFieldValue(entry, entryType, "AccumulatedRootSizes"), "NativeSize");
            if (!TryGetNativeObjectIndex(sourceIndex, out var sourceItemIndex))
                continue;

            var typeName = ResolveTypeName(typeIndices, typeNames, sourceItemIndex);
            if (!string.Equals(typeName, MemorySnapshotValidationHelpers.AssetBundleNativeTypeName, StringComparison.Ordinal))
                continue;

            var metric = result[MemorySnapshotValidationHelpers.AssetBundleNativeTypeName];
            metric.Count++;
            metric.AllocatedBytes += (long)nativeSize.Committed;
            metric.ResidentBytes += (long)nativeSize.Resident;
            result[MemorySnapshotValidationHelpers.AssetBundleNativeTypeName] = metric;
        }

        AccumulateSerializedFileSubsystemMetrics(snapshot, snapshotType, processedRoots, result, progress);
        return result;
    }

    /// <summary>
    /// Aggregates SerializedFile under Native → Unity Subsystems by walking <see cref="NativeRootReferences"/>
    /// and resolving processed committed/resident sizes (same approach as Remapper roots).
    /// </summary>
    private static void AccumulateSerializedFileSubsystemMetrics(
        object snapshot,
        Type snapshotType,
        object processedRoots,
        Dictionary<string, NativeTypeMetric> result,
        GoldenExtractionProgress progress)
    {
        var nativeRootRefs = GetFieldValue(snapshot, snapshotType, "NativeRootReferences");
        if (nativeRootRefs == null || processedRoots == null)
            return;

        var rootsType = nativeRootRefs.GetType();
        var areaNames = GetFieldValue(nativeRootRefs, rootsType, "AreaName") as string[] ?? Array.Empty<string>();
        var ids = EnumerateToLongArray(GetFieldValue(nativeRootRefs, rootsType, "Id"));

        var processedType = processedRoots.GetType();
        var rootIdToMapped = processedType.GetMethod("RootIdToMappedIndex", BindingFlags.Instance | BindingFlags.Public);
        var data = GetFieldValue(processedRoots, processedType, "Data");
        var processedEntries = data != null ? MaterializeArray(data) : Array.Empty<object>();
        if (rootIdToMapped == null || processedEntries.Length == 0)
            return;

        var metric = result[MemorySnapshotValidationHelpers.SerializedFileMetricName];
        var lastReportTicks = 0L;
        var nextReportCount = 1;

        for (var i = 0; i < areaNames.Length; i++)
        {
            ReportLoopProgress(
                progress,
                "Aggregating SerializedFile subsystem",
                0.86f,
                0.88f,
                i + 1,
                areaNames.Length,
                ref lastReportTicks,
                ref nextReportCount);

            if (!MemorySnapshotValidationHelpers.IsSerializedFileSubsystemArea(areaNames[i]))
                continue;

            var rootId = i < ids.Length ? ids[i] : 0L;
            if (!TryGetProcessedNativeSize(processedRoots, rootIdToMapped, processedEntries, rootId, out var committed, out var resident))
                continue;

            metric.Count++;
            metric.AllocatedBytes += (long)committed;
            metric.ResidentBytes += (long)resident;
        }

        result[MemorySnapshotValidationHelpers.SerializedFileMetricName] = metric;
    }

    private static bool TryGetProcessedNativeSize(
        object processedRoots,
        MethodInfo rootIdToMapped,
        object[] processedEntries,
        long rootId,
        out ulong committed,
        out ulong resident)
    {
        committed = 0;
        resident = 0;

        if (rootIdToMapped == null)
            return false;

        var mappedIndex = Convert.ToInt64(rootIdToMapped.Invoke(processedRoots, new object[] { rootId }));
        if (mappedIndex < 0 || mappedIndex >= processedEntries.Length)
            return false;

        var entry = processedEntries[mappedIndex];
        if (entry == null)
            return false;

        var nativeSize = GetMemorySize(GetFieldValue(entry, entry.GetType(), "AccumulatedRootSizes"), "NativeSize");
        committed = nativeSize.Committed;
        resident = nativeSize.Resident;
        return true;
    }

    private static List<NativeRootMetric> ExtractRemapperRootMetrics(
        object snapshot,
        Type snapshotType,
        GoldenExtractionProgress progress)
    {
        var roots = new List<NativeRootMetric>();
        var nativeRootRefs = GetFieldValue(snapshot, snapshotType, "NativeRootReferences");
        if (nativeRootRefs == null)
            return roots;

        var rootsType = nativeRootRefs.GetType();
        var areaNames = GetFieldValue(nativeRootRefs, rootsType, "AreaName") as string[] ?? Array.Empty<string>();
        var objectNames = GetFieldValue(nativeRootRefs, rootsType, "ObjectName") as string[] ?? Array.Empty<string>();
        var ids = EnumerateToLongArray(GetFieldValue(nativeRootRefs, rootsType, "Id"));

        var processedRoots = GetFieldValue(snapshot, snapshotType, "ProcessedNativeRoots");
        if (processedRoots == null)
            return roots;

        var processedType = processedRoots.GetType();
        var rootIdToMapped = processedType.GetMethod("RootIdToMappedIndex", BindingFlags.Instance | BindingFlags.Public);
        var data = GetFieldValue(processedRoots, processedType, "Data");
        var processedEntries = data != null ? MaterializeArray(data) : Array.Empty<object>();

        var lastReportTicks = 0L;
        var nextReportCount = 1;
        for (var i = 0; i < areaNames.Length; i++)
        {
            ReportLoopProgress(
                progress,
                "Extracting Remapper root metrics",
                0.89f,
                0.93f,
                i + 1,
                areaNames.Length,
                ref lastReportTicks,
                ref nextReportCount);
            var objectName = i < objectNames.Length ? objectNames[i] ?? string.Empty : string.Empty;
            if (!objectName.Contains("Remapper", StringComparison.OrdinalIgnoreCase))
                continue;

            var areaName = areaNames[i] ?? string.Empty;
            var rootId = i < ids.Length ? ids[i] : 0L;
            long allocated = 0;
            long resident = 0;

            if (rootIdToMapped != null &&
                TryGetProcessedNativeSize(processedRoots, rootIdToMapped, processedEntries, rootId, out var committed, out var residentBytes))
            {
                allocated = (long)committed;
                resident = (long)residentBytes;
            }

            roots.Add(new NativeRootMetric
            {
                AreaName = areaName,
                ObjectName = objectName,
                AllocatedBytes = allocated,
                ResidentBytes = resident,
            });
        }

        return roots;
    }

    private static (ulong Committed, ulong Resident) GetMemorySize(object nativeRootSizeBoxed, string fieldName)
    {
        if (nativeRootSizeBoxed == null)
            return (0, 0);

        var memorySize = GetFieldValue(nativeRootSizeBoxed, nativeRootSizeBoxed.GetType(), fieldName);
        if (memorySize == null)
            return (0, 0);

        var msType = memorySize.GetType();
        var committed = Convert.ToUInt64(GetFieldValue(memorySize, msType, "Committed") ?? 0UL);
        var resident = Convert.ToUInt64(GetFieldValue(memorySize, msType, "Resident") ?? 0UL);
        return (committed, resident);
    }

    private static string ResolveTypeName(object typeIndexArray, string[] typeNames, int objectIndex)
    {
        var indices = EnumerateToIntArray(typeIndexArray);
        if (objectIndex < 0 || objectIndex >= indices.Length)
            return string.Empty;

        var typeIndex = indices[objectIndex];
        return typeIndex >= 0 && typeIndex < typeNames.Length ? typeNames[typeIndex] ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// Returns true when <paramref name="sourceIndex"/> refers to a native object (not a native root reference).
    /// Uses <c>m_Data</c> when property reflection on the boxed <c>SourceIndex</c> struct fails.
    /// </summary>
    private static bool TryGetNativeObjectIndex(object sourceIndex, out int objectIndex)
    {
        objectIndex = -1;
        if (sourceIndex == null)
            return false;

        var sourceType = sourceIndex.GetType();
        var id = GetPropertyValue(sourceIndex, sourceType, "Id");
        if (id != null)
        {
            if (!string.Equals(id.ToString(), "NativeObject", StringComparison.Ordinal))
                return false;

            objectIndex = Convert.ToInt32(GetPropertyValue(sourceIndex, sourceType, "Index") ?? -1);
            return objectIndex >= 0;
        }

        var dataField = sourceType.GetField("m_Data", BindingFlags.Instance | BindingFlags.NonPublic);
        if (dataField == null)
            return false;

        var data = Convert.ToUInt64(dataField.GetValue(sourceIndex) ?? 0UL);
        const int nativeObjectKind = 5;
        if ((byte)(data >> 56) != nativeObjectKind)
            return false;

        objectIndex = (int)(data & 0x00FFFFFFFFFFFFFF);
        return objectIndex >= 0;
    }

    private static object GetFieldValue(object target, Type targetType, string name) =>
        targetType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);

    private static object GetPropertyValue(object target, Type targetType, string name) =>
        targetType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);

    /// <summary>
    /// Copies elements from Memory Profiler <c>DynamicArray&lt;T&gt;</c> via enumeration.
    /// Indexers return by-ref and cannot be read through reflection.
    /// </summary>
    private static object[] MaterializeArray(object container)
    {
        if (container is not IEnumerable enumerable)
            return Array.Empty<object>();

        var list = new List<object>();
        foreach (var item in enumerable)
            list.Add(item);
        return list.ToArray();
    }

    private static void ReportLoopProgress(
        GoldenExtractionProgress progress,
        string label,
        float phaseStart,
        float phaseEnd,
        long processed,
        long total,
        ref long lastReportTicks,
        ref int nextReportCount)
    {
        if (progress == null)
            return;

        var nowTicks = Stopwatch.GetTimestamp();
        var minTicksBetweenReports = Stopwatch.Frequency / 8;
        var done = total <= 0 ? processed > 0 : processed >= total;
        var shouldReportByCount = processed >= nextReportCount;
        var shouldReportByTime = lastReportTicks == 0 || nowTicks - lastReportTicks >= minTicksBetweenReports;

        if (!done && !shouldReportByCount && !shouldReportByTime)
            return;

        var ratio = total <= 0 ? 1f : Mathf.Clamp01((float)processed / total);
        var mapped = phaseStart + (phaseEnd - phaseStart) * ratio;
        var effectiveTotal = total <= 0 ? processed : total;
        progress.Report(mapped, $"{label} ({processed:N0}/{effectiveTotal:N0})");
        progress.ThrowIfCancelled();

        lastReportTicks = nowTicks;
        var step = Math.Max(250, (int)Math.Max(1, effectiveTotal / 200));
        nextReportCount = (int)Math.Min(int.MaxValue, processed + step);
    }

    private static long[] EnumerateToLongArray(object value)
    {
        if (value is not IEnumerable enumerable)
            return Array.Empty<long>();

        var list = new List<long>();
        foreach (var item in enumerable)
            list.Add(Convert.ToInt64(item));
        return list.ToArray();
    }

    private static int[] EnumerateToIntArray(object value)
    {
        if (value is not IEnumerable enumerable)
            return Array.Empty<int>();

        var list = new List<int>();
        foreach (var item in enumerable)
            list.Add(Convert.ToInt32(item));
        return list.ToArray();
    }

    private static string FormatSummary(GoldenSnapshot golden)
    {
        var lines = new List<string>();
        lines.AddRange(golden.NativeTypeMetrics.Select(m =>
            $"{m.NativeTypeName}: count={m.Count}, allocated={m.AllocatedBytes}, resident={m.ResidentBytes}"));

        lines.Add($"Total Allocated: {golden.TotalAllocatedBytes}");
        lines.Add($"Total Resident: {golden.TotalResidentBytes}");

        AppendCategoryLines(lines, "Allocated Memory Distribution", golden.AllocatedMemoryDistribution);
        AppendCategoryLines(lines, "Managed Heap Utilization", golden.ManagedHeapUtilization);

        return string.Join("\n", lines);
    }

    private static void AppendCategoryLines(List<string> lines, string header, SummaryCategoryMetric[] categories)
    {
        if (categories == null || categories.Length == 0)
            return;

        lines.Add($"{header}:");
        lines.AddRange(categories.Select(c =>
            $"  {c.Name}: committed={c.CommittedBytes}, resident={(c.ResidentAvailable ? c.ResidentBytes.ToString(CultureInfo.InvariantCulture) : "n/a")}"));
    }

    /// <summary>
    /// Cancelable Unity progress bar used during golden value extraction.
    /// </summary>
    public sealed class GoldenExtractionProgress : IDisposable
    {
        private readonly string _title;

        /// <summary>
        /// Creates a progress reporter for the given dialog title.
        /// </summary>
        public GoldenExtractionProgress(string title) => _title = title;

        /// <summary>
        /// True when the user cancelled the operation via the progress dialog.
        /// </summary>
        public bool IsCancelled { get; internal set; }

        /// <summary>
        /// Updates the progress bar. <paramref name="progress"/> is expected in [0, 1].
        /// </summary>
        public void Report(float progress, string description)
        {
            if (IsCancelled)
                return;

            IsCancelled = EditorUtility.DisplayCancelableProgressBar(
                _title,
                description,
                Mathf.Clamp01(progress));
        }

        /// <summary>
        /// Throws if the user cancelled the operation.
        /// </summary>
        public void ThrowIfCancelled()
        {
            if (IsCancelled)
                throw new OperationCanceledException();
        }

        /// <summary>
        /// Clears the progress bar from the Editor UI.
        /// </summary>
        public void Clear() => EditorUtility.ClearProgressBar();

        /// <inheritdoc />
        public void Dispose() => Clear();
    }
}

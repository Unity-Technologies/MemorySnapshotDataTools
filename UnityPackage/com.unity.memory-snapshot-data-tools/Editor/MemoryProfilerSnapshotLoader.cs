using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Loads a .snap file through Unity Memory Profiler internals via reflection (same pattern as <see cref="MemorySnapshotSqliteExporter"/>).
/// </summary>
internal static class MemoryProfilerSnapshotLoader
{
    private const string MemoryProfilerEditorAssemblyName = "Unity.MemoryProfiler.Editor";
    private const string FileReaderTypeName = "Unity.MemoryProfiler.Editor.Format.QueriedSnapshot.FileReader";
    private const string CachedSnapshotTypeName = "Unity.MemoryProfiler.Editor.CachedSnapshot";

    /// <summary>
    /// Reports normalized load progress in [0, 1] and a human-readable status message.
    /// </summary>
    public delegate void LoadProgressReporter(float progress, string message);

    /// <summary>
    /// Opens a snapshot, runs <c>PostProcess</c>, and returns the live <c>CachedSnapshot</c> instance.
    /// Caller must invoke <paramref name="dispose"/> when finished.
    /// </summary>
    public static bool TryLoad(
        string snapshotPath,
        out object cachedSnapshot,
        out IDisposable dispose,
        out string error,
        LoadProgressReporter reportProgress = null)
    {
        cachedSnapshot = null;
        dispose = null;
        error = string.Empty;

        object reader = null;
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == MemoryProfilerEditorAssemblyName);
            if (assembly == null)
            {
                error = $"Could not find assembly '{MemoryProfilerEditorAssemblyName}'.";
                return false;
            }

            var fileReaderType = assembly.GetType(FileReaderTypeName, throwOnError: false);
            var cachedSnapshotType = assembly.GetType(CachedSnapshotTypeName, throwOnError: false);
            if (fileReaderType == null || cachedSnapshotType == null)
            {
                error = "Could not resolve Memory Profiler internal types.";
                return false;
            }

            reader = Activator.CreateInstance(fileReaderType);
            var openMethod = fileReaderType.GetMethod("Open", BindingFlags.Instance | BindingFlags.Public);
            var openResult = openMethod?.Invoke(reader, new object[] { snapshotPath });
            if (!string.Equals(openResult?.ToString(), "Success", StringComparison.Ordinal))
            {
                error = $"Failed to open snapshot. ReadError: {openResult}";
                return false;
            }

            cachedSnapshot = Activator.CreateInstance(cachedSnapshotType, reader);
            if (cachedSnapshot == null)
            {
                error = "Failed to construct CachedSnapshot.";
                return false;
            }

            RunPostProcess(cachedSnapshot, cachedSnapshotType, reportProgress);

            var readerRef = reader;
            var snapshotRef = cachedSnapshot;
            dispose = new DisposeAction(() =>
            {
                TryInvoke(snapshotRef, "Dispose");
                TryInvoke(readerRef, "Close");
                TryInvoke(readerRef, "Dispose");
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            TryInvoke(cachedSnapshot, "Dispose");
            TryInvoke(reader, "Close");
            TryInvoke(reader, "Dispose");
            return false;
        }
    }

    private static void RunPostProcess(object snapshot, Type cachedSnapshotType, LoadProgressReporter reportProgress)
    {
        var method = cachedSnapshotType.GetMethod("PostProcess", BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
            return;

        object enumeratorObject;
        try
        {
            enumeratorObject = method.Invoke(snapshot, new object[] { true });
        }
        catch (TargetParameterCountException)
        {
            enumeratorObject = method.Invoke(snapshot, Array.Empty<object>());
        }

        if (enumeratorObject is not IEnumerator enumerator)
            return;

        reportProgress?.Invoke(0f, "Post-processing snapshot...");
        while (enumerator.MoveNext())
        {
            if (enumerator.Current == null)
                continue;

            var status = enumerator.Current;
            var statusType = status.GetType();
            var currentStep = statusType.GetProperty("CurrentStep", BindingFlags.Instance | BindingFlags.Public)?.GetValue(status);
            var stepCount = statusType.GetProperty("StepCount", BindingFlags.Instance | BindingFlags.Public)?.GetValue(status);
            var stepStatus = statusType.GetProperty("StepStatus", BindingFlags.Instance | BindingFlags.Public)?.GetValue(status) as string;

            if (currentStep != null && stepCount != null && Convert.ToInt32(stepCount) > 0)
            {
                var ratio = Mathf.Clamp01(Convert.ToSingle(currentStep) / Convert.ToSingle(stepCount));
                var message = string.IsNullOrEmpty(stepStatus) ? "Post-processing snapshot..." : stepStatus;
                reportProgress?.Invoke(ratio, message);
            }
            else if (!string.IsNullOrEmpty(stepStatus))
            {
                reportProgress?.Invoke(0f, stepStatus);
            }
        }

        reportProgress?.Invoke(1f, "Post-processing complete");
    }

    private static void TryInvoke(object target, string methodName)
    {
        if (target == null)
            return;

        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
            return;

        try
        {
            method.Invoke(target, Array.Empty<object>());
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _action;

        public DisposeAction(Action action) => _action = action;

        public void Dispose() => _action();
    }
}

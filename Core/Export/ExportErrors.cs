namespace MemorySnapshotDataTools.Export;

/// <summary>
/// Exception thrown when an export stage (e.g. extract, write, validate) fails. Wraps the inner exception and records the stage name.
/// </summary>
public sealed class ExportStageException : Exception
{
    /// <summary>Creates an exception for a failed export stage.</summary>
    /// <param name="stage">Name of the stage that failed (e.g. "extract", "write").</param>
    /// <param name="innerException">The underlying exception.</param>
    public ExportStageException(string stage, Exception innerException)
        : base($"Stage '{stage}' failed.", innerException)
    {
        Stage = stage;
    }

    /// <summary>Name of the export stage that failed.</summary>
    public string Stage { get; }
}

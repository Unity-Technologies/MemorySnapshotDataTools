namespace MemorySnapshotDataTools;

/// <summary>
/// Abstraction for progress and status reporting during long-running operations.
/// Implemented by the CLI (e.g. ConsoleProgress) and passed into Core APIs
/// so that extraction, export, and report steps can report progress without depending on the host.
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// Report a progress or status message.
    /// </summary>
    /// <param name="message">Message to report (e.g. "Extracting...", "Written 10000 rows").</param>
    /// <param name="force">If true, report immediately; otherwise the implementation may throttle (e.g. by time).</param>
    void Report(string message, bool force = false);
}

using MemorySnapshotDataTools.Export;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Tests for <see cref="BatchExportRunner"/> snapshot discovery.
/// </summary>
public sealed class BatchExportRunnerTests
{
    /// <summary>
    /// Returns all .snap files when no filter is set.
    /// </summary>
    [Fact]
    public void DiscoverSnapshotFiles_NoFilter_ReturnsAllSnaps()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "msdt_batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "alpha.snap"), string.Empty);
            File.WriteAllText(Path.Combine(tempDir, "beta.snap"), string.Empty);
            File.WriteAllText(Path.Combine(tempDir, "readme.txt"), string.Empty);

            var files = BatchExportRunner.DiscoverSnapshotFiles(tempDir, nameFilter: null);
            Assert.Equal(2, files.Count);
            Assert.Contains(files, f => f.EndsWith("alpha.snap", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("beta.snap", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Applies a case-insensitive substring filter to snapshot filenames.
    /// </summary>
    [Fact]
    public void DiscoverSnapshotFiles_WithFilter_ReturnsMatchingSnapsOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "msdt_batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyGame_menu.snap"), string.Empty);
            File.WriteAllText(Path.Combine(tempDir, "Other_title.snap"), string.Empty);

            var files = BatchExportRunner.DiscoverSnapshotFiles(tempDir, "mygame");
            Assert.Single(files);
            Assert.EndsWith("MyGame_menu.snap", files[0], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}

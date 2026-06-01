using MemorySnapshotDataTools.Report.MultiSnapshotReport;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Tests for <see cref="MultiSnapshotSessionKey"/>.
/// </summary>
public sealed class MultiSnapshotSessionKeyTests
{
    [Fact]
    public void FromFileName_WithContextAndFormat_GroupsByDeviceAndUnity()
    {
        var meta = MultiSnapshotSessionKey.FromFileName(
            "MyGame_2026-05-13_10-53-31_IOS_U63",
            "format:17");

        Assert.Contains("MyGame", meta.SessionKey, StringComparison.Ordinal);
        Assert.Contains("2026-05-13", meta.SessionKey, StringComparison.Ordinal);
        Assert.Contains("IOS_U63", meta.SessionKey, StringComparison.Ordinal);
        Assert.Contains("format:17", meta.SessionKey, StringComparison.Ordinal);
        Assert.Contains("iOS", meta.DisplayTitle, StringComparison.Ordinal);
        Assert.Contains("Snap format 17", meta.DisplayTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFileName_GeneralCapture_SeparatesFromLabeledCaptures()
    {
        var general = MultiSnapshotSessionKey.FromFileName("MyGame_2026-05-13_10-28-31", "format:17");
        var labeled = MultiSnapshotSessionKey.FromFileName("MyGame_2026-05-13_10-53-31_IOS_U63", "format:17");

        Assert.NotEqual(general.SessionKey, labeled.SessionKey);
        Assert.Contains("General capture", general.DisplayTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFileName_UnlabeledSameDayDifferentTimes_SeparateSessions()
    {
        var early = MultiSnapshotSessionKey.FromFileName("MyGame_2026-05-13_10-28-31", "format:17");
        var late = MultiSnapshotSessionKey.FromFileName("MyGame_2026-05-13_14-30-06", "format:17");

        Assert.NotEqual(early.SessionKey, late.SessionKey);
        Assert.Contains("10:28:31", early.DisplayTitle, StringComparison.Ordinal);
        Assert.Contains("14:30:06", late.DisplayTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFileName_DifferentUnityVersions_SeparateSessions()
    {
        var v17 = MultiSnapshotSessionKey.FromFileName("MyGame_2026-05-13_10-28-31", "format:17");
        var v16 = MultiSnapshotSessionKey.FromFileName("MyGame_2026-05-13_10-28-31", "format:16");

        Assert.NotEqual(v17.SessionKey, v16.SessionKey);
    }
}

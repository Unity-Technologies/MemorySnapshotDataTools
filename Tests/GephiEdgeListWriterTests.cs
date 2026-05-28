using MemorySnapshotDataTools.Gephi;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Unit tests for <see cref="GephiEdgeListWriter"/>: edges CSV, optional nodes CSV, and CSV escaping.
/// </summary>
public sealed class GephiEdgeListWriterTests
{
    [Fact]
    public void WriteNativeEdgeList_EdgesOnly_WritesExpectedCsv()
    {
        var edges = new List<(long FromIndex, long ToIndex, string Label)>
        {
            (0, 1, "native_connection"),
            (1, 2, "native_connection"),
        };
        using var tmp = new TempDirectory();
        var edgesPath = Path.Combine(tmp.Path, "edges.csv");

        GephiEdgeListWriter.WriteNativeEdgeList(edges, edgesPath, nodes: null, nodesPath: null);

        var lines = File.ReadAllLines(edgesPath);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Source,Target,Label", lines[0]);
        Assert.Equal("0,1,native_connection", lines[1]);
        Assert.Equal("1,2,native_connection", lines[2]);
    }

    [Fact]
    public void WriteNativeEdgeList_WithNodes_WritesEdgesAndNodesCsv()
    {
        var edges = new List<(long FromIndex, long ToIndex, string Label)>
        {
            (0, 1, "native_connection"),
        };
        var nodes = new List<(long Id, string Label, string Type, ulong Size)>
        {
            (0, "GameObject", "GameObject", 100),
            (1, "Transform", "Transform", 200),
        };
        using var tmp = new TempDirectory();
        var edgesPath = Path.Combine(tmp.Path, "edges.csv");
        var nodesPath = Path.Combine(tmp.Path, "nodes.csv");

        GephiEdgeListWriter.WriteNativeEdgeList(edges, edgesPath, nodes, nodesPath);

        var edgeLines = File.ReadAllLines(edgesPath);
        Assert.Equal(2, edgeLines.Length);
        Assert.Equal("Source,Target,Label", edgeLines[0]);
        Assert.Equal("0,1,native_connection", edgeLines[1]);

        var nodeLines = File.ReadAllLines(nodesPath);
        Assert.Equal(3, nodeLines.Length);
        Assert.Equal("Id,Label,Type,Size", nodeLines[0]);
        Assert.Equal("0,GameObject,GameObject,100", nodeLines[1]);
        Assert.Equal("1,Transform,Transform,200", nodeLines[2]);
    }

    [Fact]
    public void WriteNativeEdgeList_LabelWithComma_EscapesInCsv()
    {
        var edges = new List<(long FromIndex, long ToIndex, string Label)>
        {
            (0, 1, "type,a"),
        };
        using var tmp = new TempDirectory();
        var edgesPath = Path.Combine(tmp.Path, "edges.csv");

        GephiEdgeListWriter.WriteNativeEdgeList(edges, edgesPath, nodes: null, nodesPath: null);

        var content = File.ReadAllText(edgesPath);
        Assert.Contains("\"type,a\"", content);
    }

    [Fact]
    public void WriteNativeEdgeList_LabelWithQuote_EscapesInCsv()
    {
        var edges = new List<(long FromIndex, long ToIndex, string Label)>
        {
            (0, 1, "say \"hello\""),
        };
        using var tmp = new TempDirectory();
        var edgesPath = Path.Combine(tmp.Path, "edges.csv");

        GephiEdgeListWriter.WriteNativeEdgeList(edges, edgesPath, nodes: null, nodesPath: null);

        var content = File.ReadAllText(edgesPath);
        Assert.Contains("\"say \"\"hello\"\"\"", content);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GephiTests_" + Guid.NewGuid().ToString("N")[..8]);

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

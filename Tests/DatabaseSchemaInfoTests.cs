using MemorySnapshotDataTools;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Tests for <see cref="DatabaseSchemaInfo"/> major/minor classification and the re-export command builder.
/// </summary>
public sealed class DatabaseSchemaInfoTests
{
    [Fact]
    public void Evaluate_CurrentVersion_IsNone()
    {
        Assert.Equal(SchemaAction.None,
            DatabaseSchemaInfo.Evaluate(DatabaseSchemaInfo.SchemaMajor, DatabaseSchemaInfo.SchemaMinor));
    }

    [Fact]
    public void Evaluate_SameMajorLowerMinor_IsUpgradeInPlace()
    {
        // A database one minor behind only needs views/indexes re-applied.
        var action = DatabaseSchemaInfo.Evaluate(DatabaseSchemaInfo.SchemaMajor, DatabaseSchemaInfo.SchemaMinor - 1);
        Assert.Equal(SchemaAction.UpgradeInPlace, action);
    }

    [Fact]
    public void Evaluate_LowerMajor_IsReExport()
    {
        Assert.Equal(SchemaAction.ReExport,
            DatabaseSchemaInfo.Evaluate(DatabaseSchemaInfo.SchemaMajor - 1, 99));
    }

    [Fact]
    public void Evaluate_PreVersioningDatabase_IsReExport()
    {
        // Version (0, 0) = no schema_meta table; structure unknown → must re-export.
        Assert.Equal(SchemaAction.ReExport, DatabaseSchemaInfo.Evaluate(0, 0));
    }

    [Fact]
    public void Evaluate_PreSwappedSchema_IsReExport()
    {
        // v1.x databases predate the v2.0 swapped-page columns and must be re-exported.
        Assert.Equal(SchemaAction.ReExport, DatabaseSchemaInfo.Evaluate(1, 3));
    }

    [Fact]
    public void Evaluate_NewerThanTool_IsToolOutdated()
    {
        Assert.Equal(SchemaAction.ToolOutdated,
            DatabaseSchemaInfo.Evaluate(DatabaseSchemaInfo.SchemaMajor + 1, 0));
        Assert.Equal(SchemaAction.ToolOutdated,
            DatabaseSchemaInfo.Evaluate(DatabaseSchemaInfo.SchemaMajor, DatabaseSchemaInfo.SchemaMinor + 1));
    }

    [Theory]
    [InlineData(false, "MemorySnapshotDataTools export \"/snaps/a.snap\" \"/dbs/a.duckdb\"")]
    [InlineData(true, "MemorySnapshotDataTools export \"/snaps/a.snap\" \"/dbs/a.duckdb\" --destination sqlite")]
    public void BuildReExportCommand_FormatsCommand(bool sqlite, string expected)
    {
        Assert.Equal(expected, DatabaseSchemaInfo.BuildReExportCommand("/snaps/a.snap", "/dbs/a.duckdb", sqlite));
    }

    [Fact]
    public void Changelog_CoversCurrentVersion()
    {
        // Guard: bumping SchemaMajor/SchemaMinor without adding a Changelog row would leave the
        // upgrade command unable to describe the new version.
        Assert.Contains(DatabaseSchemaInfo.Changelog,
            e => e.Major == DatabaseSchemaInfo.SchemaMajor && e.Minor == DatabaseSchemaInfo.SchemaMinor);
    }

    [Fact]
    public void ChangesSince_CurrentVersion_IsEmpty()
    {
        Assert.Empty(DatabaseSchemaInfo.ChangesSince(DatabaseSchemaInfo.SchemaMajor, DatabaseSchemaInfo.SchemaMinor));
    }

    [Fact]
    public void ChangesSince_OneMinorBehind_DescribesCurrentVersion()
    {
        // A database one minor behind should be told exactly which (single) version the upgrade applied.
        var changes = DatabaseSchemaInfo.ChangesSince(DatabaseSchemaInfo.SchemaMajor, DatabaseSchemaInfo.SchemaMinor - 1);
        var change = Assert.Single(changes);
        Assert.StartsWith($"v{DatabaseSchemaInfo.SchemaMajor}.{DatabaseSchemaInfo.SchemaMinor}:", change);
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using CClean.Core.Experiments;
using CClean.Core.Monitoring;
using CClean.Core.Storage;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "Storage")]
[Trait("Category", "Foundation")]
public class LocalStoreTests
{
    [Fact]
    public void HardLinkedPathsAcrossRootsCountOnceAndUnknownIsVisible()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-shared-index-" + Guid.NewGuid().ToString("N")));
        using var store = new LocalStore(Path.Combine(box.RunDirectory, "data"));
        var path = box.CreateFixture("first.bin", 100);
        var link = Path.Combine(box.RunDirectory, "alias.bin");
        Assert.True(CreateHardLinkW(link, path, IntPtr.Zero));
        var first = Core.Files.WindowsFileMetadata.Read(path);
        var alias = Core.Files.WindowsFileMetadata.Read(link);
        DirectorySnapshot Snapshot(Core.Files.FileSnapshot file) => new(new Dictionary<string, Core.Files.FileSnapshot> { [file.Path] = file }, [], DateTimeOffset.UtcNow);
        Assert.True(store.SaveSnapshot("a", Snapshot(first), store.LoadSnapshots("a")));
        Assert.True(store.SaveSnapshot("b", Snapshot(alias), store.LoadSnapshots("b")));
        Assert.Equal(100, store.Totals(path).Logical);
        Assert.Equal(0, store.Totals(path).Growth);
        Assert.Equal(2, store.Totals(path).Files);
        Assert.Equal(100, store.ProjectTotals("a", box.RunDirectory).Logical);
        Assert.Equal(100, store.ProjectTotals("b", box.RunDirectory).Logical);
        var unknown = Core.Files.WindowsFileMetadata.Read(Path.Combine(box.RunDirectory, "missing.bin"));
        Assert.True(store.SaveSnapshot("c", Snapshot(unknown), store.LoadSnapshots("c")));
        Assert.Equal(1, store.Totals(path).Unknown);
        File.WriteAllText(path + ":extra", "synthetic", System.Text.Encoding.UTF8);
        Assert.True(store.SaveSnapshot("a", Snapshot(Core.Files.WindowsFileMetadata.Read(path)), store.LoadSnapshots("a")));
        Assert.Equal(2, store.Totals(path).Unknown);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);

    [Fact]
    public void SamePathReplacementUsesNewIdentityBaseline()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-replacement-" + Guid.NewGuid().ToString("N")));
        using var store = new LocalStore(Path.Combine(box.RunDirectory, "data"));
        var path = Path.Combine(box.RunDirectory, "file.bin");
        File.WriteAllBytes(path, new byte[100]);
        var first = Core.Files.WindowsFileMetadata.Read(path);
        var initial = new DirectorySnapshot(new Dictionary<string, Core.Files.FileSnapshot> { [path] = first }, [], DateTimeOffset.UtcNow);
        Assert.True(store.SaveSnapshot("p", initial, store.LoadSnapshots("p")));
        // A captured replacement identity models File ID reuse without relying on allocator timing.
        var replacement = first with { Identity = first.Identity + "replacement", LogicalBytes = 60 };
        Assert.True(store.SaveSnapshot("p", initial with { Files = new Dictionary<string, Core.Files.FileSnapshot> { [path] = replacement } }, store.LoadSnapshots("p")));
        Assert.Equal(60, Assert.Single(store.QueryFiles()).Growth);
        Assert.Equal(-40, store.Totals(path).Growth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaselineGrowthDeletionAndRestartAreAccurate(bool absolutePaths)
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-store-" + Guid.NewGuid().ToString("N")));
        var monitored = Path.Combine(box.RunDirectory, "monitored"); Directory.CreateDirectory(monitored);
        var file = Path.Combine(monitored, "file.bin"); File.WriteAllBytes(file, new byte[100]);
        var data = Path.Combine(box.RunDirectory, "data");
        using (var store = new LocalStore(data))
        {
            Assert.True(store.SaveSnapshot("p", DirectoryScanner.Scan(monitored, absolutePaths: absolutePaths), store.LoadSnapshots("p")));
            Assert.Equal(0, store.Totals(monitored).Growth);
            File.WriteAllBytes(file, new byte[160]);
            Assert.True(store.SaveSnapshot("p", DirectoryScanner.Scan(monitored, absolutePaths: absolutePaths), store.LoadSnapshots("p")));
            Assert.Equal(60, store.Totals(monitored).Growth);
            Assert.Equal(60, store.ProjectTotals("p", monitored).Growth);
            var incomplete = new DirectorySnapshot(new Dictionary<string, Core.Files.FileSnapshot>(), ["Access denied"], DateTimeOffset.UtcNow);
            Assert.True(store.SaveSnapshot("p", incomplete, store.LoadSnapshots("p")));
            Assert.Single(store.QueryFiles());
            File.Delete(file);
            Assert.True(store.SaveSnapshot("p", DirectoryScanner.Scan(monitored, absolutePaths: absolutePaths), store.LoadSnapshots("p")));
            Assert.Empty(store.QueryFiles());
            Assert.Equal(new StoreTotals(0, 0, 0, 0, 0), store.ProjectTotals("p", monitored));
            Assert.Equal(-100, store.Totals(monitored).Growth);
        }
        using var reopened = new LocalStore(data);
        Assert.Equal(-100, reopened.Totals(monitored).Growth);
        Assert.Contains(reopened.Changes(DateTimeOffset.MinValue), change => change.Kind == "初始存量");
    }
    [Theory]
    [InlineData("complete")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("null_boolean")]
    [InlineData("string_number")]
    [InlineData("number_boolean")]
    [InlineData("duplicate_boolean")]
    [InlineData("duplicate_exclusion")]
    [InlineData("negative_links")]
    [InlineData("large_integer")]
    [InlineData("large_attributes")]
    [InlineData("null_record")]
    [InlineData("array_record")]
    [InlineData("malformed")]
    [InlineData("unknown_key")]
    [InlineData("unknown_nested_deep")]
    [InlineData("computed_object")]
    [InlineData("duplicate_computed")]
    [InlineData("unicode_escape")]
    [InlineData("nul_path")]
    [InlineData("lone_high_surrogate_path")]
    [InlineData("lone_high_surrogate_exclusion")]
    [InlineData("lone_low_surrogate_exclusion")]
    public void SnapshotProjectionPreservesLegacyParserSemantics(string shape)
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-json-shape-" + Guid.NewGuid().ToString("N")));
        var data = Path.Combine(box.RunDirectory, "data");
        using var store = new LocalStore(data);
        var path = Path.Combine(box.RunDirectory, "中文.bin");
        var file = new Core.Files.FileSnapshot(path, "synthetic-id", 17, 4096, 1, FileAttributes.Archive, 123, 456, true, false, null);
        var json = JsonSerializer.Serialize(file);
        json = shape switch
        {
            "unknown" => JsonSerializer.Serialize(file with { Identity = null, LogicalBytes = null, AllocatedBytes = null, StreamsComplete = false, Exclusion = "元数据未知" }),
            "missing" => JsonSerializer.Serialize(new { Path = path }),
            "null_boolean" => json.Replace("\"StreamsComplete\":true", "\"StreamsComplete\":null"),
            "string_number" => json.Replace("\"LogicalBytes\":17", "\"LogicalBytes\":\"17\""),
            "number_boolean" => json.Replace("\"StreamsComplete\":true", "\"StreamsComplete\":1"),
            "duplicate_boolean" => json[..^1] + ",\"StreamsComplete\":false}",
            "duplicate_exclusion" => json[..^1] + ",\"Exclusion\":\"protected\"}",
            "negative_links" => json.Replace("\"LinkCount\":1", "\"LinkCount\":-1"),
            "large_integer" => json.Replace("\"LogicalBytes\":17", "\"LogicalBytes\":999999999999999999999999999"),
            "large_attributes" => json.Replace("\"Attributes\":32", "\"Attributes\":2147483648"),
            "null_record" => "null",
            "array_record" => "[]",
            "malformed" => "{",
            "unknown_key" => json[..^1] + ",\"Unexpected\":true}",
            "unknown_nested_deep" => json[..^1] + ",\"Unexpected\":" + string.Concat(Enumerable.Repeat("{\"n\":", 65)) + "0" + new string('}', 65) + "}",
            "computed_object" => json.Replace("\"IsOrdinaryFile\":true", "\"IsOrdinaryFile\":{\"n\":null}"),
            "duplicate_computed" => json[..^1] + ",\"IsOrdinaryFile\":false}",
            "unicode_escape" => json.Replace("\"Exclusion\":null", "\"Exclusion\":\"\\u0061\\u0000\\u4E2D\\uD83D\\uDE00\""),
            "nul_path" => json.Replace(JsonSerializer.Serialize(path), "\"prefix\\u0000suffix\""),
            "lone_high_surrogate_path" => json.Replace(JsonSerializer.Serialize(path), "\"\\uD800\""),
            "lone_high_surrogate_exclusion" => json.Replace("\"Exclusion\":null", "\"Exclusion\":\"\\uD800\""),
            "lone_low_surrogate_exclusion" => json.Replace("\"Exclusion\":null", "\"Exclusion\":\"\\uDC00\""),
            _ => json
        };
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "index.db"), Pooling = false }.ToString()))
        {
            writer.Open();
            using var insert = writer.CreateCommand();
            insert.CommandText = "INSERT INTO files VALUES('p',$path,NULL,NULL,NULL,0,$json)";
            insert.Parameters.AddWithValue("$path", path); insert.Parameters.AddWithValue("$json", json); insert.ExecuteNonQuery();
        }
        Core.Files.FileSnapshot? expected = null;
        var legacyFailure = Record.Exception(() =>
        {
            expected = JsonSerializer.Deserialize<Core.Files.FileSnapshot>(json)!;
            var previous = new Dictionary<string, Core.Files.FileSnapshot> { [expected.Path] = expected };
        });
        if (legacyFailure is not null) Assert.Throws(legacyFailure.GetType(), () => store.LoadSnapshots("p"));
        else Assert.Equal(expected, Assert.Single(store.LoadSnapshots("p")).Value);
    }

    [Fact]
    public void ProjectAndRootScopeApplyBeforePagingAndHistoryLimits()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-scope-" + Guid.NewGuid().ToString("N")));
        using var store = new LocalStore(Path.Combine(box.RunDirectory, "data"));
        var root = Path.Combine(box.RunDirectory, "app"); Directory.CreateDirectory(root);
        var other = Path.Combine(box.RunDirectory, "app-backup"); Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(root, "keep.bin"), new byte[32]);
        for (var i = 0; i < 205; i++) File.WriteAllBytes(Path.Combine(other, $"other-{i}.bin"), new byte[100]);
        Assert.True(store.SaveSnapshot("a", DirectoryScanner.Scan(root), store.LoadSnapshots("a")));
        Assert.True(store.SaveSnapshot("b", DirectoryScanner.Scan(other), store.LoadSnapshots("b")));
        Assert.Single(store.QueryFiles(project: "a", root: root.ToUpperInvariant()));
        Assert.Empty(store.QueryFiles(page: 1, project: "a", root: root));
        Assert.Empty(store.QueryFiles(project: "b", root: root));
        Assert.Single(store.QueryFiles(root: root));
        Assert.Equal(32, store.ProjectTotals("a", root.ToUpperInvariant()).Logical);
        Assert.Equal(1, store.ProjectTotals("a", root).Files);
        Assert.Equal(0, store.ProjectTotals("b", root).Files);
        Assert.Equal(20500, store.ProjectTotals("b", other).Logical);
        Assert.Equal(200, store.QueryFiles(project: "b", root: other).Count);
        Assert.Equal(5, store.QueryFiles(page: 1, project: "b", root: other).Count);
        var change = Assert.Single(store.Changes(DateTimeOffset.MinValue, 1, "a", root));
        Assert.Equal(Path.Combine(root, "keep.bin"), change.Path);
        Assert.Single(store.Changes(DateTimeOffset.MinValue, 10000, root: root));
        Assert.Equal(change.Time, store.EarliestHistory("a", root));
        store.RecordGap("a", "project-a"); store.RecordGap("b", "project-b");
        Assert.Equal("project-a", Assert.Single(store.Gaps(DateTimeOffset.MinValue, "a")).Reason);
    }

}

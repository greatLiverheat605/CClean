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
    public void CurrentObservationKeysExcludeRemovedAndReplacedScopesWithoutLosingHistory()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-current-totals-" + Guid.NewGuid().ToString("N")));
        using var store = new LocalStore(Path.Combine(box.RunDirectory, "data"));
        var path = box.CreateFixture("current.bin", 100);
        var alias = Path.Combine(box.RunDirectory, "alias.bin");
        Assert.True(CreateHardLinkW(alias, path, IntPtr.Zero));
        var removed = box.CreateFixture("removed.bin", 200);
        void Save(string key, Core.Files.FileSnapshot file) => Assert.True(store.SaveSnapshot(key,
            new DirectorySnapshot(new Dictionary<string, Core.Files.FileSnapshot> { [file.Path] = file }, [], DateTimeOffset.UtcNow), store.LoadSnapshots(key)));
        Save("a", Core.Files.WindowsFileMetadata.Read(path));
        Save("b", Core.Files.WindowsFileMetadata.Read(alias));
        Save("removed", Core.Files.WindowsFileMetadata.Read(removed));
        var current = store.CurrentTotals(path, ["a", "b"]);
        Assert.Equal(100, current.Logical);
        Assert.Equal(2, current.Files);
        Assert.Equal(0, current.Growth);
        Assert.Equal(300, store.Totals(path).Logical);
        Assert.Equal(2, store.QueryFiles(observationKeys: ["a", "b"]).Count);
        Assert.Empty(store.QueryFiles(observationKeys: []));
        Assert.Equal(new StoreTotals(0, 0, 0, 0, 0), store.CurrentTotals(path, []));
        File.WriteAllBytes(path, new byte[120]);
        Save("a@2", Core.Files.WindowsFileMetadata.Read(path));
        var revised = store.CurrentTotals(path, ["a@2"]);
        Assert.Equal(120, revised.Logical);
        Assert.Equal(20, revised.Growth);
        Assert.Single(store.QueryFiles(observationKeys: ["a@2"]));
        Assert.Contains(store.Changes(DateTimeOffset.MinValue), c => c.ProjectId == "removed");
        Assert.Single(store.LoadSnapshots("a"));
    }

    [Fact]
    public void InventorySummariesRestartAsBoundedStaleDisplayDataWithNoApproval()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-inventory-summary-" + Guid.NewGuid().ToString("N")));
        var data = Path.Combine(box.RunDirectory, "data");
        var captured = DateTimeOffset.UtcNow;
        var folders = Enumerable.Range(0, LocalStore.MaximumSummaryRows + 5).Select(i =>
        {
            var path = Path.Combine(box.RunDirectory, "folder-" + i);
            return new SpaceFolder(path, i, 1, false, "synthetic", "")
            { Explanation = ContentClassification.Describe(path, decision: new(true, "synthetic", "must not persist")) };
        }).ToArray();
        var result = new SpaceSurveyResult(folders, 205, 500, 1, true, false)
        {
            Root = box.RunDirectory, CapturedAt = captured, KnownAllocatedBytes = 4096,
            KnownUniqueLogicalBytes = 200, UnknownAllocationObjects = 2, DuplicatePaths = 1
        };
        using (var store = new LocalStore(data)) Assert.True(store.SaveInventorySummary(box.RunDirectory, 3, result));
        using var reopened = new LocalStore(data);
        var summary = Assert.IsType<InventorySummary>(reopened.LoadInventorySummary(box.RunDirectory, 3));
        Assert.True(summary.IsStale);
        Assert.Equal(captured, summary.CapturedAt);
        Assert.True(summary.FoldersTruncated);
        Assert.Equal(LocalStore.MaximumSummaryRows, summary.Folders.Count);
        Assert.Equal(4096, summary.KnownAllocatedBytes);
        Assert.Null(reopened.LoadInventorySummary(box.RunDirectory, 4));
        Assert.Null(reopened.LoadInventorySummary(Path.Combine(box.RunDirectory, "other"), 3));
        var display = summary.ToSurveyResult();
        Assert.True(display.IsStale);
        Assert.False(display.AllocationComplete);
        Assert.All(display.Folders, f => Assert.Null(f.Explanation.Decision));
        Assert.Throws<ArgumentException>(() => reopened.SaveInventorySummary(box.RunDirectory, 3, display));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "index.db"), Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM inventory_summaries";
        var json = Assert.IsType<string>(command.ExecuteScalar());
        Assert.DoesNotContain("Eligible", json);
        Assert.DoesNotContain("Decision", json);
        Assert.DoesNotContain("must not persist", json);
        Assert.Empty(reopened.QueryFiles());
    }

    [Fact]
    public void LatestSummaryKeepsTheSavedRootAndScopeRevisionAndSkipsDamagedPayloads()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-latest-summary-" + Guid.NewGuid().ToString("N")));
        var data = Path.Combine(box.RunDirectory, "data");
        using var store = new LocalStore(data);
        Assert.Null(store.LoadLatestInventorySummary());
        var captured = DateTimeOffset.UtcNow;
        var first = Path.Combine(box.RunDirectory, "first");
        var latest = Path.Combine(box.RunDirectory, "latest");
        var damaged = Path.Combine(box.RunDirectory, "damaged");
        var result = new SpaceSurveyResult([], 0, 0, 0, false, false) { Root = first, CapturedAt = captured };
        Assert.True(store.SaveInventorySummary(first, 1, result));
        Assert.True(store.SaveInventorySummary(latest, 4, result with { Root = latest, CapturedAt = captured.AddSeconds(1) }));
        Assert.True(store.SaveInventorySummary(damaged, 1, result with { Root = damaged, CapturedAt = captured.AddSeconds(2) }));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "index.db"), Pooling = false }.ToString());
        db.Open(); using var corrupt = db.CreateCommand();
        corrupt.CommandText = "UPDATE inventory_summaries SET payload='damaged' WHERE root=$root";
        corrupt.Parameters.AddWithValue("$root", damaged); corrupt.ExecuteNonQuery();
        var restored = Assert.IsType<InventorySummary>(store.LoadLatestInventorySummary());
        Assert.Equal(latest, restored.Root);
        Assert.Equal(4, restored.ScopeRevision);
        Assert.True(restored.IsStale);
        Assert.True(restored.ToSurveyResult().IsStale);
    }

    [Fact]
    public void SummarySaveFailureAndOversizeDoNotOverwritePreviousSnapshot()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-summary-rollback-" + Guid.NewGuid().ToString("N")));
        var data = Path.Combine(box.RunDirectory, "data");
        using var store = new LocalStore(data);
        var initial = new SpaceSurveyResult([], 1, 10, 0, false, false) { Root = box.RunDirectory, CapturedAt = DateTimeOffset.UtcNow };
        Assert.True(store.SaveInventorySummary(box.RunDirectory, 1, initial));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "index.db"), Pooling = false }.ToString());
        db.Open(); using var trigger = db.CreateCommand();
        trigger.CommandText = "CREATE TRIGGER refuse_summary BEFORE UPDATE ON inventory_summaries BEGIN SELECT RAISE(ABORT,'controlled summary write failure'); END";
        trigger.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.SaveInventorySummary(box.RunDirectory, 1, initial with { Bytes = 20 }));
        Assert.Equal(10, store.LoadInventorySummary(box.RunDirectory, 1)!.LogicalBytes);
        var huge = new SpaceFolder(box.RunDirectory, 10, 1, false, "", new string('x', LocalStore.MaximumSummaryBytes));
        Assert.Throws<IOException>(() => store.SaveInventorySummary(box.RunDirectory, 1, initial with { Folders = [huge] }));
        Assert.Equal(10, store.LoadInventorySummary(box.RunDirectory, 1)!.LogicalBytes);
    }

    [Fact]
    public void DiscoverySummariesHaveBoundedRootsAndGlobalRetention()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-discovery-summary-" + Guid.NewGuid().ToString("N")));
        using var store = new LocalStore(Path.Combine(box.RunDirectory, "data"));
        var first = Path.Combine(box.RunDirectory, "scope-0");
        for (var i = 0; i <= LocalStore.MaximumSummaries; i++)
        {
            var root = Path.Combine(box.RunDirectory, "scope-" + i);
            var roots = Enumerable.Range(0, LocalStore.MaximumSummaryRows + 1).Select(n => Path.Combine(root, "project-" + n)).ToArray();
            Assert.True(store.SaveDiscoverySummary(root, 1, new Core.Projects.ProjectDiscoveryResult(roots, 250, 2, true, false)));
        }
        Assert.Null(store.LoadDiscoverySummary(first, 1));
        var last = Path.Combine(box.RunDirectory, "scope-" + LocalStore.MaximumSummaries);
        var summary = Assert.IsType<DiscoverySummary>(store.LoadDiscoverySummary(last, 1));
        Assert.True(summary.IsStale);
        Assert.True(summary.RootsTruncated);
        Assert.Equal(LocalStore.MaximumSummaryRows, summary.Roots.Count);
        Assert.Equal(250, summary.ToDiscoveryResult().Inspected);
        Assert.True(summary.ToDiscoveryResult().HasMore);
    }

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

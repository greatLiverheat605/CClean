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

    [Fact]
    public void BaselineGrowthDeletionAndRestartAreAccurate()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-store-" + Guid.NewGuid().ToString("N")));
        var monitored = Path.Combine(box.RunDirectory, "monitored"); Directory.CreateDirectory(monitored);
        var file = Path.Combine(monitored, "file.bin"); File.WriteAllBytes(file, new byte[100]);
        var data = Path.Combine(box.RunDirectory, "data");
        using (var store = new LocalStore(data))
        {
            Assert.True(store.SaveSnapshot("p", DirectoryScanner.Scan(monitored), store.LoadSnapshots("p")));
            Assert.Equal(0, store.Totals(monitored).Growth);
            File.WriteAllBytes(file, new byte[160]);
            Assert.True(store.SaveSnapshot("p", DirectoryScanner.Scan(monitored), store.LoadSnapshots("p")));
            Assert.Equal(60, store.Totals(monitored).Growth);
            var incomplete = new DirectorySnapshot(new Dictionary<string, Core.Files.FileSnapshot>(), ["Access denied"], DateTimeOffset.UtcNow);
            Assert.True(store.SaveSnapshot("p", incomplete, store.LoadSnapshots("p")));
            Assert.Single(store.QueryFiles());
            File.Delete(file);
            Assert.True(store.SaveSnapshot("p", DirectoryScanner.Scan(monitored), store.LoadSnapshots("p")));
            Assert.Empty(store.QueryFiles());
            Assert.Equal(-100, store.Totals(monitored).Growth);
        }
        using var reopened = new LocalStore(data);
        Assert.Equal(-100, reopened.Totals(monitored).Growth);
        Assert.Contains(reopened.Changes(DateTimeOffset.MinValue), change => change.Kind == "初始存量");
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

using CClean.Core.Attribution;
using CClean.Core.History;
using CClean.Core.Sessions;
using CClean.Core.Storage;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "Attribution")]
[Trait("Category", "Timeline")]
[Trait("Category", "Foundation")]
public class AttributionTests
{
    [Fact]
    public async Task RealConcurrentWritesRemainSharedWithoutInventedAllocation()
    {
        using var box = new Core.Experiments.ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-attribution-" + Guid.NewGuid().ToString("N")));
        var shared = Path.Combine(box.RunDirectory, "shared"); Directory.CreateDirectory(shared);
        using var store = new LocalStore(Path.Combine(box.RunDirectory, "data"));
        var project = Guid.NewGuid(); var time = DateTimeOffset.UtcNow;
        Assert.True(store.SaveSnapshot(project.ToString(), Core.Monitoring.DirectoryScanner.Scan(shared), store.LoadSnapshots(project.ToString())));
        var sessions = new[] { "Codex", "Claude Code" }.Select(tool => new DevelopmentSession(Guid.NewGuid(), project, tool, tool, time, null, SessionState.Active, [shared])).ToArray();
        await Task.WhenAll(File.WriteAllBytesAsync(Path.Combine(shared, "a.bin"), new byte[100]), File.WriteAllBytesAsync(Path.Combine(shared, "b.bin"), new byte[200]));
        Assert.True(store.SaveSnapshot(project.ToString(), Core.Monitoring.DirectoryScanner.Scan(shared), store.LoadSnapshots(project.ToString())));
        var result = TimelineQuery.Build(store.Changes(time), sessions, [], time, DateTimeOffset.UtcNow, time);
        Assert.Equal(300, result.Nodes.Sum(n => n.NetBytes));
        Assert.All(result.Nodes, n => Assert.Equal(EvidenceKind.Shared, n.Evidence.Kind));
        Assert.Equal(300, store.Totals(shared).Logical);
        store.RecordGap(project.ToString(), "合成暂停");
        Assert.Contains(store.Gaps(time), g => g.Reason == "合成暂停");
    }

    [Fact]
    public void ConcurrentSessionsAreSharedAndAliasesDoNotDoubleGrowth()
    {
        var time = DateTimeOffset.UtcNow;
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        DevelopmentSession Session(Guid project, string tool) => new(Guid.NewGuid(), project, "合成", tool, time.AddMinutes(-5), null, SessionState.Active, ["D:/synthetic-cache"]);
        var sessions = new[] { Session(a, "Codex"), Session(b, "ZCode") };
        var first = new StoredChange(1, time, a.ToString(), "D:/synthetic-cache/file.bin", "same-identity", 100, 200, "变化");
        var alias = first with { Id = 2, ProjectId = b.ToString(), Path = "D:/synthetic-cache/alias.bin" };
        Assert.Equal(EvidenceKind.Shared, AttributionRules.Classify(first, sessions).Kind);
        var result = TimelineQuery.Build([first, alias], sessions, [], time.AddMinutes(-10), time.AddMinutes(1), time.AddMinutes(-10));
        Assert.Equal(100, Assert.Single(result.Nodes).NetBytes);
        Assert.Equal(EvidenceKind.Shared, result.Nodes[0].Evidence.Kind);
        Assert.Equal(2, result.Nodes[0].Evidence.Sessions.Count);
    }

    [Fact]
    public void MissingEvidenceGapsAndInitialStockStayExplicit()
    {
        var time = DateTimeOffset.UtcNow; var project = Guid.NewGuid();
        var session = new DevelopmentSession(Guid.NewGuid(), project, "合成", "Claude Code", time.AddMinutes(-1), null, SessionState.Active, []);
        var change = new StoredChange(1, time, project.ToString(), "D:/synthetic/file.bin", "identity", 20, 30, "变化");
        Assert.Equal(EvidenceKind.Context, AttributionRules.Classify(change, [session]).Kind);
        Assert.Equal(EvidenceKind.Unknown, AttributionRules.Classify(change, []).Kind);
        Assert.Equal(EvidenceKind.Unknown, AttributionRules.Classify(change, [session with { HasGap = true }]).Kind);
        var result = TimelineQuery.Build([change], [session], [new(time, project.ToString(), "溢出")], time.AddMinutes(-5), time.AddMinutes(1), time.AddMinutes(-2));
        Assert.Null(Assert.Single(result.Nodes).NetBytes);
        Assert.Equal(EvidenceKind.Unknown, result.Nodes[0].Evidence.Kind);
        var separateGap = TimelineQuery.Build([change], [session], [new(time.AddSeconds(5), project.ToString(), "后续暂停")], time.AddMinutes(-5), time.AddMinutes(1), time.AddMinutes(-2));
        Assert.Equal(EvidenceKind.Context, Assert.Single(separateGap.Nodes).Evidence.Kind);
        var initial = change with { Kind = "初始存量", Before = null };
        Assert.Equal(0, Assert.Single(TimelineQuery.Build([initial], [session], [], time.AddMinutes(-5), time.AddMinutes(1), time).Nodes).NetBytes);
    }

    [Fact]
    public void VisibleNodesAreBoundedAndTruncationIsReported()
    {
        var time = DateTimeOffset.UtcNow;
        var changes = Enumerable.Range(0, 600).Select(i => new StoredChange(i, time.AddMinutes(i), "unknown-project", $"D:/synthetic/{i}/file", i.ToString(), 0, 1, "新增")).ToArray();
        var result = TimelineQuery.Build(changes, [], [], time, time.AddDays(1), time);
        Assert.Equal(500, result.Nodes.Count); Assert.True(result.Truncated);
    }
}

using CClean.Core.Experiments;
using CClean.Core.Monitoring;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "Monitoring")]
[Trait("Category", "M0")]
[Trait("Category", "Foundation")]
public class MonitoringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalibrationReusesOnlyUnchangedMetadata(bool absolutePaths)
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-reuse-" + Guid.NewGuid().ToString("N")));
        box.CreateFixture("same.bin", 10);
        box.CreateFixture("changed.bin", 10);
        var initial = DirectoryScanner.Scan(box.RunDirectory, absolutePaths: absolutePaths);
        File.AppendAllText(Path.Combine(box.RunDirectory, "changed.bin"), "changed", System.Text.Encoding.UTF8);
        var current = DirectoryScanner.Scan(box.RunDirectory, previous: initial.Files, absolutePaths: absolutePaths);
        var same = absolutePaths ? Path.Combine(box.RunDirectory, "same.bin") : "same.bin";
        var changed = absolutePaths ? Path.Combine(box.RunDirectory, "changed.bin") : "changed.bin";
        if (absolutePaths) Assert.Same(current.Files, current.FilesByPath);
        Assert.Same(initial.Files[same], current.Files[same]);
        Assert.NotSame(initial.Files[changed], current.Files[changed]);
        Assert.True(current.Files[changed].LogicalBytes > initial.Files[changed].LogicalBytes);
    }

    [Fact]
    public async Task RestartSharesPersistedBaselineButRevalidatesOfflineChanges()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-restart-seed-" + Guid.NewGuid().ToString("N")));
        var root = Path.Combine(box.RunDirectory, "project"); Directory.CreateDirectory(root);
        var same = Path.Combine(root, "same.bin"); File.WriteAllBytes(same, [1]);
        var changed = Path.Combine(root, "changed.bin"); File.WriteAllBytes(changed, [1]);
        var deleted = Path.Combine(root, "deleted.bin"); File.WriteAllBytes(deleted, [1]);
        using var store = new Core.Storage.LocalStore(Path.Combine(box.RunDirectory, "data"));
        Assert.True(store.SaveSnapshot("p", DirectoryScanner.Scan(root, absolutePaths: true), store.LoadSnapshots("p")));
        var baseline = store.LoadSnapshots("p");
        File.WriteAllBytes(changed, [1, 2, 3]); File.Delete(deleted);
        using var monitor = new DirectoryMonitor(root, absolutePaths: true, initialFiles: baseline);
        // A seed remains explicitly stale until the first live scan completes.
        var initial = monitor.Snapshot;
        if (initial.CapturedAt == DateTimeOffset.MinValue)
        {
            Assert.Same(baseline, initial.Files);
            Assert.NotEmpty(initial.Gaps);
        }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (monitor.Revision == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(monitor.Revision > 0);
        var live = monitor.Snapshot;
        Assert.Empty(live.Gaps);
        Assert.Same(baseline[same], live.Files[same]);
        Assert.NotSame(baseline[changed], live.Files[changed]);
        Assert.Equal(3, live.Files[changed].LogicalBytes);
        Assert.False(live.Files.ContainsKey(deleted));
        Assert.True(baseline.ContainsKey(deleted));
        Assert.Null(monitor.LastActivity);
        Assert.True(store.SaveSnapshot("p", live, baseline));
        Assert.DoesNotContain(store.QueryFiles(), f => f.Path == deleted);
        Assert.Contains(store.Changes(DateTimeOffset.MinValue), change => change.Path == deleted && change.Kind == "消失");
    }

    [Fact]
    public async Task CalibrationDoesNotInventFileActivity()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-activity-" + Guid.NewGuid().ToString("N")));
        using var monitor = new DirectoryMonitor(box.RunDirectory);
        async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.True(condition());
        }
        await WaitFor(() => monitor.Revision > 0);
        Assert.Null(monitor.LastActivity);
        var revision = monitor.Revision;
        monitor.Rescan();
        await WaitFor(() => monitor.Revision > revision);
        Assert.Null(monitor.LastActivity);
        box.CreateFixture("activity.bin", 10);
        await WaitFor(() => monitor.LastActivity is not null);
    }

    [Fact]
    public async Task ReportedWatcherFailureSurvivesCompleteRescanUntilMonitorIsRebuilt()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-watcher-health-" + Guid.NewGuid().ToString("N")));
        box.CreateFixture("known.bin", 10);
        async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.True(condition());
        }
        using var monitor = new DirectoryMonitor(box.RunDirectory);
        await WaitFor(() => monitor.Revision > 0);
        Assert.True(monitor.HasContinuousObservation);

        // Exercise the real error handler's state transition without claiming actual native overflow.
        monitor.ReportWatcherError(new IOException("Synthetic non-overflow watcher failure"));
        Assert.Equal(1, monitor.WatcherErrors);
        Assert.Equal(0, monitor.NativeGaps);
        Assert.True(monitor.ScanGaps > 0);
        Assert.False(monitor.HasContinuousObservation);
        var revision = monitor.Revision;
        monitor.Rescan();
        await WaitFor(() => monitor.Revision > revision);
        Assert.Empty(monitor.Snapshot.Gaps);
        Assert.Single(monitor.Snapshot.Files);
        Assert.Equal(0, monitor.ApplicationGaps);
        Assert.False(monitor.HasContinuousObservation);

        monitor.Dispose();
        Assert.False(monitor.HasContinuousObservation);
        using var rebuilt = new DirectoryMonitor(box.RunDirectory);
        await WaitFor(() => rebuilt.Revision > 0);
        Assert.Equal(0, rebuilt.WatcherErrors);
        Assert.True(rebuilt.HasContinuousObservation);
        rebuilt.Dispose();
        Assert.False(rebuilt.HasContinuousObservation);
    }

    [Fact]
    public async Task ControlledCollectorFailurePublishesGapAndDoesNotBreakDisposal()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-collector-failure-" + Guid.NewGuid().ToString("N")));
        box.CreateFixture("retained.bin", 10);
        using var monitor = new DirectoryMonitor(box.RunDirectory);
        async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.True(condition());
        }
        await WaitFor(() => monitor.Revision > 0);
        Assert.True(monitor.HasContinuousObservation);
        var revision = monitor.Revision;
        var before = monitor.Snapshot;
        monitor.BeforeScan = () => throw new InvalidOperationException("controlled collector failure");
        monitor.Rescan();
        await WaitFor(() => monitor.Revision > revision);
        Assert.False(monitor.HasContinuousObservation);
        Assert.True(monitor.ScanGaps > 0);
        Assert.Equal(0, monitor.NativeGaps);
        Assert.Equal(0, monitor.WatcherErrors);
        Assert.Same(before.Files, monitor.Snapshot.Files);
        Assert.Contains(monitor.Snapshot.Gaps, gap => gap.Contains("controlled collector failure"));
        Assert.Null(Record.Exception(monitor.Dispose));
        Assert.False(monitor.HasContinuousObservation);
        Assert.Null(Record.Exception(monitor.Dispose));
    }

    [Fact]
    public async Task ChangesConvergeAndApplicationOverflowRemainsVisible()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-monitor-" + Guid.NewGuid().ToString("N")));
        using var monitor = new DirectoryMonitor(box.RunDirectory, queueCapacity: 1);
        for (var i = 0; i < 100; i++) box.CreateFixture($"{i}.bin", 10);
        File.AppendAllText(Path.Combine(box.RunDirectory, "0.bin"), "append", System.Text.Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(box.RunDirectory, "1.bin"), [1]);
        File.Move(Path.Combine(box.RunDirectory, "2.bin"), Path.Combine(box.RunDirectory, "renamed.bin"));
        File.Delete(Path.Combine(box.RunDirectory, "3.bin"));
        for (var i = 0; i < 100; i++) monitor.Rescan();
        var truth = Directory.GetFiles(box.RunDirectory).ToDictionary(p => Path.GetFileName(p)!, p => new FileInfo(p).Length);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var current = monitor.Snapshot;
            if (current.Files.Count == truth.Count && current.Files.All(p => truth.TryGetValue(p.Key, out var n) && n == p.Value.LogicalBytes)) break;
            await Task.Delay(50);
        }
        Assert.Equal(truth.Count, monitor.Snapshot.Files.Count);
        Assert.All(monitor.Snapshot.Files, pair => Assert.Equal(truth[pair.Key], pair.Value.LogicalBytes));
        Assert.True(monitor.ApplicationGaps > 0);
        Assert.Empty(monitor.Snapshot.Gaps);
        Assert.False(monitor.HasContinuousObservation);
    }
}

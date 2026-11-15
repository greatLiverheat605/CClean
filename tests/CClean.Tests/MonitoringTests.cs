using CClean.Core.Experiments;
using CClean.Core.Monitoring;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "Monitoring")]
[Trait("Category", "M0")]
[Trait("Category", "Foundation")]
public class MonitoringTests
{
    [Fact]
    public async Task ObservationHistoryRemainsImmutableAndBoundedWithoutCopyingItOnEveryRead()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-history-" + Guid.NewGuid().ToString("N")));
        using var monitor = new DirectoryMonitor(box.RunDirectory);
        Assert.True(SpinWait.SpinUntil(() => monitor.Revision > 0, TimeSpan.FromSeconds(10)));
        var original = monitor.Observe();
        for (var i = 0; i < 2100; i++)
        {
            var sequence = monitor.Observe().Sequence;
            File.WriteAllBytes(Path.Combine(box.RunDirectory, $"notification-{i:D4}.bin"), [1]);
            Assert.True(SpinWait.SpinUntil(() => monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(5)));
        }
        await Task.Delay(100); // Drain the final file's native notifications before measuring reads.
        var before = monitor.Observe();
        var saved = before.Events.ToArray();
        Assert.NotEmpty(saved);
        Assert.True(before.DroppedThrough > 0);
        Assert.InRange(saved.Length, 1, 2048);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 512; i++) _ = monitor.Observe();
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 512 * 1024,
            "Reading a bounded notification history repeatedly must not copy the full history each time.");
        var prior = monitor.Observe().Sequence;
        File.WriteAllBytes(Path.Combine(box.RunDirectory, "later.bin"), [2]);
        Assert.True(SpinWait.SpinUntil(() => monitor.Observe().Sequence > prior, TimeSpan.FromSeconds(5)));
        Assert.Equal(saved, before.Events);
        Assert.Empty(original.Events);
        Assert.False(monitor.IsObservationCurrent(before));
        Assert.True(monitor.Observe().DroppedThrough >= before.DroppedThrough);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalibrationReusesOnlyUnchangedMetadata(bool absolutePaths)
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-reuse-" + Guid.NewGuid().ToString("N")));
        box.CreateFixture("same.bin", 10);
        box.CreateFixture("changed.bin", 10);
        var initial = DirectoryScanner.Scan(box.RunDirectory, absolutePaths: absolutePaths);
        var unchanged = DirectoryScanner.Scan(box.RunDirectory, previous: initial.Files, absolutePaths: absolutePaths);
        Assert.Same(initial.Files, unchanged.Files);
        File.AppendAllText(Path.Combine(box.RunDirectory, "changed.bin"), "changed", System.Text.Encoding.UTF8);
        var current = DirectoryScanner.Scan(box.RunDirectory, previous: initial.Files, absolutePaths: absolutePaths);
        var same = absolutePaths ? Path.Combine(box.RunDirectory, "same.bin") : "same.bin";
        var changed = absolutePaths ? Path.Combine(box.RunDirectory, "changed.bin") : "changed.bin";
        if (absolutePaths) Assert.Same(current.Files, current.FilesByPath);
        Assert.NotSame(initial.Files, current.Files);
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

    [Fact]
    public async Task ResearchScanDeferralKeepsRawNotificationsAndScansAfterRelease()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-research-scan-" + Guid.NewGuid().ToString("N")));
        using var monitor = new DirectoryMonitor(box.RunDirectory);
        Assert.True(SpinWait.SpinUntil(() => monitor.HasContinuousObservation, TimeSpan.FromSeconds(10)));
        var revision = monitor.Revision;
        var sequence = monitor.Observe().Sequence;
        Task scanned;
        lock (monitor.ScanGate)
        {
            monitor.BeginResearchScanDeferral();
            scanned = monitor.ResearchScanCompleted;
        }
        try
        {
            box.CreateFixture("during.bin", 10);
            Assert.True(SpinWait.SpinUntil(() => monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(10)));
            monitor.Rescan();
            await Task.Delay(350);
            Assert.Equal(revision, monitor.Revision);
            Assert.False(scanned.IsCompleted);
            Assert.Contains(monitor.Observe().Events, e => e.Path.EndsWith("during.bin", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            lock (monitor.ScanGate)
            {
                monitor.EndResearchScanDeferral();
                Assert.Throws<InvalidOperationException>(monitor.BeginResearchScanDeferral);
            }
        }
        await scanned.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(monitor.Revision > revision);
        Assert.Contains(monitor.Snapshot.Files, f => f.Key == "during.bin");
        Assert.True(monitor.HasContinuousObservation);
    }

    [Fact]
    public async Task ResearchScanCompletionWaitsForNotificationsArrivingDuringPostBatchScan()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-research-followup-" + Guid.NewGuid().ToString("N")));
        using var monitor = new DirectoryMonitor(box.RunDirectory);
        Assert.True(SpinWait.SpinUntil(() => monitor.HasContinuousObservation, TimeSpan.FromSeconds(10)));
        var baseline = monitor.Revision;
        lock (monitor.ScanGate) monitor.BeginResearchScanDeferral();
        var completion = monitor.ResearchScanCompleted;
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var scans = 0;
        monitor.BeforeScan = () =>
        {
            if (Interlocked.Increment(ref scans) == 1)
            {
                var sequence = monitor.Observe().Sequence;
                box.CreateFixture("during-post-batch.bin", 10);
                Assert.True(SpinWait.SpinUntil(() => monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(5)));
            }
            else { secondEntered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
        };
        try
        {
            monitor.EndResearchScanDeferral();
            Assert.True(await Task.Run(() => secondEntered.Wait(TimeSpan.FromSeconds(10))));
            Assert.False(completion.IsCompleted);
        }
        finally { release.Set(); monitor.BeforeScan = null; }
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(monitor.Revision >= baseline + 2);
        Assert.Contains(monitor.Snapshot.Files, file => file.Key == "during-post-batch.bin");
    }

    [Fact]
    public async Task ResearchScanDeferralDoesNotHideWatcherFailure()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-research-fault-" + Guid.NewGuid().ToString("N")));
        using var monitor = new DirectoryMonitor(box.RunDirectory);
        Assert.True(SpinWait.SpinUntil(() => monitor.HasContinuousObservation, TimeSpan.FromSeconds(10)));
        lock (monitor.ScanGate) monitor.BeginResearchScanDeferral();
        var scanned = monitor.ResearchScanCompleted;
        try
        {
            monitor.ReportWatcherError(new InternalBufferOverflowException("controlled research failure"));
            Assert.False(monitor.HasContinuousObservation);
            Assert.True(monitor.NativeGaps > 0);
        }
        finally { monitor.EndResearchScanDeferral(); }
        await scanned.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(monitor.HasContinuousObservation);
    }
}

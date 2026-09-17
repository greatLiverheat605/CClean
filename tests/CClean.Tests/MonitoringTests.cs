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
    public void CalibrationReusesOnlyUnchangedMetadata()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-reuse-" + Guid.NewGuid().ToString("N")));
        box.CreateFixture("same.bin", 10);
        box.CreateFixture("changed.bin", 10);
        var initial = DirectoryScanner.Scan(box.RunDirectory);
        File.AppendAllText(Path.Combine(box.RunDirectory, "changed.bin"), "changed", System.Text.Encoding.UTF8);
        var current = DirectoryScanner.Scan(box.RunDirectory, previous: initial.Files);
        Assert.Same(initial.Files["same.bin"], current.Files["same.bin"]);
        Assert.NotSame(initial.Files["changed.bin"], current.Files["changed.bin"]);
        Assert.True(current.Files["changed.bin"].LogicalBytes > initial.Files["changed.bin"].LogicalBytes);
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
        var activity = monitor.LastActivity;
        revision = monitor.Revision;
        monitor.Rescan();
        await WaitFor(() => monitor.Revision > revision);
        Assert.Equal(activity, monitor.LastActivity);
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
    }
}

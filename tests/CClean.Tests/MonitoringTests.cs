using CClean.Core.Experiments;
using CClean.Core.Monitoring;

namespace CClean.Tests;

[Trait("Category", "Monitoring")]
[Trait("Category", "M0")]
[Trait("Category", "Foundation")]
public class MonitoringTests
{
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

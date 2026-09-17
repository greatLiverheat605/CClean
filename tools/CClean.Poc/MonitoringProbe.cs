using System.Diagnostics;
using CClean.Core.Experiments;
using CClean.Core.Monitoring;

namespace CClean.Poc;

internal static class MonitoringProbe
{
    public static void Run(ExperimentSandbox box, bool overflow, List<object> checks)
    {
        using var arrived = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim(!overflow);
        var blocked = 0;
        using var monitor = new DirectoryMonitor(box.RunDirectory, queueCapacity: 1, beforeNotification: () =>
        {
            if (overflow && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                arrived.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        });
        box.CreateFixture("trigger.bin", 1);
        if (overflow && !arrived.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Notification barrier not reached.");
        try
        {
            var count = overflow ? 12_000 : 100;
            for (var i = 0; i < count; i++) box.CreateFixture($"sample-{i:D6}.bin", 1);
            var original = Path.Combine(box.RunDirectory, "sample-000000.bin");
            File.AppendAllText(original, "append", System.Text.Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(box.RunDirectory, "sample-000001.bin"), [4, 3, 2, 1]);
            using (var truncate = File.OpenWrite(Path.Combine(box.RunDirectory, "sample-000002.bin"))) truncate.SetLength(0);
            File.Move(Path.Combine(box.RunDirectory, "sample-000003.bin"), Path.Combine(box.RunDirectory, "renamed.bin"));
            File.Delete(Path.Combine(box.RunDirectory, "sample-000004.bin"));
            var directory = Path.Combine(box.RunDirectory, "before");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "nested.bin"), [1]);
            Directory.Move(directory, Path.Combine(box.RunDirectory, "after"));
        }
        finally { release.Set(); }
        var truth = Directory.GetFiles(box.RunDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(box.RunDirectory, p), p => new FileInfo(p).Length, StringComparer.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew();
        bool Converged()
        {
            var current = monitor.Snapshot;
            return current.Gaps.Count == 0 && current.Files.Count == truth.Count &&
                current.Files.All(p => truth.TryGetValue(p.Key, out var length) && length == p.Value.LogicalBytes);
        }
        while ((!Converged() || (overflow && monitor.NativeGaps == 0)) && timer.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(50);
        var success = Converged();
        checks.Add(new { name = "final-state", status = success ? "passed" : "failed", fileCount = truth.Count,
            convergenceMilliseconds = timer.ElapsedMilliseconds, applicationGaps = monitor.ApplicationGaps, nativeGaps = monitor.NativeGaps });
        if (!success) throw new InvalidOperationException("Directory state did not converge within 60 seconds.");
        if (overflow)
        {
            if (monitor.NativeGaps == 0) throw new ExperimentRefusedException("Actual native overflow not observed; unverified.", 2);
            if (monitor.ApplicationGaps == 0) throw new InvalidOperationException("Application overflow not observed.");
            checks.Add(new { name = "real-overflow", status = "passed", monitor.NativeGaps, monitor.ApplicationGaps });
        }
    }
}

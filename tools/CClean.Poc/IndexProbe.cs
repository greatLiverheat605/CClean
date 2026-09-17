using System.Diagnostics;
using CClean.Core.Experiments;
using CClean.Core.Monitoring;
using CClean.Core.Storage;

namespace CClean.Poc;

public static class IndexProbe
{
    public static void Run(ExperimentSandbox sandbox, int count, long bytes, List<object> checks, string? existingSample = null)
    {
        if (checked(count * 4096L + bytes) > sandbox.Budget)
            throw new ExperimentRefusedException("Allocation allowance exceeds fixture budget.", 2);
        var timer = Stopwatch.StartNew();
        var sample = existingSample is null ? sandbox.RunDirectory : ExperimentSandbox.Normalize(existingSample);
        if (!sample.StartsWith(sandbox.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ExperimentRefusedException("Reused sample must remain inside the owned sandbox.", 3);
        using var lease = new DirectoryLease(sample);
        for (var i = 0; existingSample is null && i < count; i++)
        {
            sandbox.CreateFixture($"file-{i:D6}.bin", bytes / count + (i < bytes % count ? 1 : 0));
            if ((i + 1) % 10000 == 0) Console.WriteLine($"Created {i + 1}/{count}");
        }
        var creationMs = timer.ElapsedMilliseconds;
        timer.Restart();
        var snapshot = DirectoryScanner.Scan(sample);
        var scanMs = timer.ElapsedMilliseconds;
        if (snapshot.Files.Count != count || snapshot.Gaps.Count != 0 || snapshot.Files.Values.Sum(f => f.LogicalBytes) != bytes)
            throw new InvalidOperationException("Index scan differs from independent fixture truth.");
        var data = Path.Combine(sandbox.Root, "index-" + sandbox.RunId);
        using var store = new LocalStore(data);
        timer.Restart();
        if (!store.SaveSnapshot("benchmark", snapshot, store.LoadSnapshots("benchmark"))) throw new IOException("Index budget stopped baseline.");
        var indexMs = timer.ElapsedMilliseconds;
        var totals = store.Totals(sample);
        if (totals.Files != count || totals.Logical != bytes || totals.Growth != 0 || totals.Unknown != 0)
            throw new InvalidOperationException("Stored totals differ from fixture truth.");
        checks.Add(new { name = "index-scale", status = "passed", count, bytes, creationMs, scanMs, indexMs,
            database = RuntimeBudget.Measure(data), totals, fixture = sample, reusedSample = existingSample is not null });
    }
}

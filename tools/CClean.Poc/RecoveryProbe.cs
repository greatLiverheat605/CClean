using System.Diagnostics;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;

namespace CClean.Poc;

internal static class RecoveryProbe
{
    public static void Child(ExperimentSandbox box, string sample, string checkpoint)
    {
        sample = ExperimentSandbox.Normalize(sample);
        if (!sample.Equals(Path.GetDirectoryName(box.Root), StringComparison.OrdinalIgnoreCase)) throw new ExperimentRefusedException("Crash sample must own child sandbox.", 3);
        if (checkpoint is not ("before-delete" or "after-delete" or "after-result")) throw new ArgumentException("Unknown checkpoint.");
        using var lease = new DirectoryLease(sample);
        var root = Path.Combine(sample, "cache"); var data = Path.Combine(sample, "audit");
        var context = new CleanupContext(1, [new(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)], new(1, [new(root, false, true, "owned crash fixture")]), [data]);
        var plan = CleanupPlanner.Preview(new[] { "a", "b" }.Select(n => Path.Combine(root, "_cacache", "content-v2", n)), context);
        if (plan.EligibleCount != 2) throw new InvalidOperationException("Crash fixture was not eligible.");
        using var journal = new CleanupJournal(data);
        new CleanupExecutor(journal) { TestCheckpoint = point => { if (point == checkpoint) Environment.Exit(73); } }
            .ExecuteApproved(plan, () => Task.FromResult(context)).GetAwaiter().GetResult();
        throw new InvalidOperationException("Crash checkpoint was not reached.");
    }
    public static void Run(ExperimentSandbox box, List<object> checks)
    {
        foreach (var checkpoint in new[] { "before-delete", "after-delete", "after-result" })
        {
            var root = Path.Combine(box.RunDirectory, checkpoint); var cache = Path.Combine(root, "cache", "_cacache", "content-v2"); Directory.CreateDirectory(cache);
            foreach (var name in new[] { "a", "b" }) { var path = Path.Combine(cache, name); File.WriteAllText(path, "owned crash fixture", System.Text.Encoding.UTF8); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1)); }
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(RecoveryProbe).Assembly.Location);
            foreach (var arg in new[] { "--case", "cleanup-crash-child", "--sandbox", Path.Combine(root, "child-sandbox"), "--sample-root", root, "--checkpoint", checkpoint, "--output", "artifacts/cleanup-crash-child.json" }) info.ArgumentList.Add(arg);
            using var child = Process.Start(info)!;
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(60000)) { child.Kill(true); throw new IOException("Owned crash child timed out."); }
            if (child.ExitCode != 73) throw new IOException("Crash child failed: " + stderr.GetAwaiter().GetResult());
            using var journal = new CleanupJournal(Path.Combine(root, "audit"));
            var run = journal.Runs().Single();
            if (run.State != "Interrupted" || !File.Exists(Path.Combine(cache, "b"))) throw new InvalidOperationException("Restart resumed or lost next object.");
            var firstExists = File.Exists(Path.Combine(cache, "a"));
            if (firstExists != (checkpoint == "before-delete")) throw new InvalidOperationException("Unexpected crash boundary result.");
            var observed = CleanupRecovery.Inspect(journal, run.Id);
            if (checkpoint == "after-delete" && observed[0].State != "RecoveryObservation") throw new InvalidOperationException("Invented committed deletion.");
            checks.Add(new { name = checkpoint, status = "passed", childExitCode = child.ExitCode, firstExists, nextObjectPreserved = true, state = run.State, recovery = observed.Select(o => o.State).ToArray() });
        }
    }
}

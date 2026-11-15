using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Sessions;
using CClean.Core.Storage;

namespace CClean.Poc;

internal static class CleanupComponentsProbe
{
    public static void Run(ExperimentSandbox sandbox, List<object> checks)
    {
        const int count = 2505;
        if (sandbox.Budget < 32L * 1024 * 1024)
            throw new ExperimentRefusedException("Component probe requires a 32 MiB fixture/audit budget.", 2);
        if (Directory.EnumerateFileSystemEntries(sandbox.RunDirectory).Any() ||
            Directory.EnumerateFileSystemEntries(sandbox.Root).Any(path => path != sandbox.RunDirectory && Path.GetFileName(path) != ".cclean-sandbox"))
            throw new ExperimentRefusedException("Component probe requires a fresh sandbox root without retained runs.", 3);
        var root = Path.Combine(sandbox.RunDirectory, "exclusive");
        var content = Path.Combine(root, "_cacache", "content-v2");
        var audit = Path.Combine(sandbox.RunDirectory, "audit");
        Directory.CreateDirectory(content);
        using var lease = new DirectoryLease(content);
        var paths = Enumerable.Range(0, count).Select(i =>
        {
            var source = sandbox.CreateFixture($"sample-{i:D6}", 16);
            var destination = Path.Combine(content, Path.GetFileName(source));
            File.Move(source, destination); return destination;
        }).ToArray();
        foreach (var path in paths) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        var context = new CleanupContext(1, [new(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)],
            new ActivitySnapshot(1, [new(root, false, true, "fixed synthetic context; no live watcher or real npm consumer")]), [audit]);
        var rootIdentity = WindowsFileMetadata.Read(root).Identity ?? throw new IOException("Root identity missing.");
        var timer = Stopwatch.StartNew();
        var items = paths.Select(path =>
        {
            var file = WindowsFileMetadata.Read(path);
            return new CleanupItem(file, ProtectionPolicy.Classify(file, context.Rules, context.Activity, context.ProtectedRoots, DateTimeOffset.UtcNow), rootIdentity);
        }).ToList();
        Require(items.All(i => i.Decision.Eligible), "All generated samples must qualify in the synthetic context.");
        checks.Add(new { name = "component-fixtures", status = "passed", root, count, logicalBytes = count * 16,
            identityMilliseconds = timer.Elapsed.TotalMilliseconds, liveWatcherCovered = false, existingUserFilesTouched = false });

        using var journal = new CleanupJournal(audit);
        void CheckFixtureBudget()
        {
            var auditBytes = Directory.EnumerateFiles(audit).Sum(path => new FileInfo(path).Length);
            // Reserve sample clusters plus 1 MiB for the next bounded SQLite/manifest write.
            if (auditBytes + count * 4096L + 1024 * 1024 > sandbox.Budget)
                throw new ExperimentRefusedException("Component fixture/audit budget exhausted.", 2);
        }
        var research = new CleanupPlan(DateTimeOffset.UtcNow, context.Fingerprint(), items, false);
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(research).Length;
        timer.Restart();
        var compactBytes = Encoding.UTF8.GetByteCount(CleanupManifestCodec.Encode(research));
        var encodeMilliseconds = timer.Elapsed.TotalMilliseconds;
        var before = RuntimeBudget.Measure(audit);
        CheckFixtureBudget(); timer.Restart(); journal.Begin(research);
        var beginMilliseconds = timer.Elapsed.TotalMilliseconds;
        // Diagnostic audit rows describe observations only; they never claim a physical deletion.
        timer.Restart();
        for (var i = 0; i < 600; i++)
        { if (i % 100 == 0) CheckFixtureBudget(); journal.Record(research.Id, "", "DiagnosticOnly", "Synthetic pagination load; no deletion"); }
        foreach (var item in items)
        { CheckFixtureBudget(); journal.Record(research.Id, item.Path, "DiagnosticOnly", "Generated fixture remains present"); }
        journal.Finish(research.Id, "DiagnosticOnly", "Research manifest, never submitted to ExecuteApproved");
        var auditMilliseconds = timer.Elapsed.TotalMilliseconds;
        var auditRows = journal.Events(research.Id.ToString());
        Require(auditRows.Count == count + 600 && auditRows[^1].Path == paths[^1], "Complete audit tail missing.");
        int eventPages = 0, eventCount = 0; long sequence = 0; long? through = null;
        timer.Restart();
        while (true)
        {
            var page = journal.EventsPage(research.Id.ToString(), sequence, 200, through);
            eventPages++; eventCount += page.Items.Count; sequence = page.NextSequence; through = page.ThroughSequence;
            if (!page.HasMore) break;
        }
        var eventPageMilliseconds = timer.Elapsed.TotalMilliseconds;
        Require(eventCount == auditRows.Count, "Paged audit differs from complete audit.");
        int ordinal = -1, inspected = 0, observations = 0, recoveryPages = 0;
        through = null; timer.Restart();
        while (true)
        {
            var page = CleanupRecovery.InspectPage(journal, research.Id.ToString(), ordinal, 200, through);
            recoveryPages++; inspected += page.CheckedItems; observations += page.Items.Count;
            ordinal = page.NextOrdinal; through = page.ThroughSequence;
            if (!page.HasMore) break;
        }
        var recoveryPageMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart(); var complete = CleanupRecovery.Inspect(journal, research.Id.ToString());
        var recoveryCompleteMilliseconds = timer.Elapsed.TotalMilliseconds;
        Require(inspected == count && observations == count && complete.Count == count && complete[^1].Path == paths[^1], "Recovery tail missing.");
        checks.Add(new { name = "component-manifest-audit-recovery", status = "passed", productionPlan = false, executionAttempted = false,
            count, legacyBytes, compactBytes, encodeMilliseconds, beginMilliseconds, auditRows = auditRows.Count, auditMilliseconds,
            durableRowsPerSecond = auditRows.Count * 1000 / Math.Max(1, auditMilliseconds), before, after = RuntimeBudget.Measure(audit),
            eventPages, eventPageMilliseconds, recoveryPages, recoveryPageMilliseconds, recoveryCompleteMilliseconds,
            checkedItems = inspected, observations, finalOrdinal = ordinal, tailPath = complete[^1].Path });

        // Codec-only size research reuses metadata shapes, not 100,000 real files or executable approvals.
        foreach (var size in new[] { 1000, 1001, 10_000, 100_000 })
        {
            var virtualItems = Enumerable.Range(0, size).Select(i => items[0] with
                { File = items[0].File with { Path = Path.Combine(content, $"virtual-{i:D6}"), Identity = $"synthetic-only-{i}" } }).ToList();
            var virtualPlan = new CleanupPlan(DateTimeOffset.UtcNow, context.Fingerprint(), virtualItems, false);
            timer.Restart(); int? bytes = null; var refused = false;
            try { bytes = Encoding.UTF8.GetByteCount(CleanupManifestCodec.Encode(virtualPlan)); }
            catch (IOException error) when (error.Message == "审计清单超过 8 MiB，请缩小选择。") { refused = true; }
            checks.Add(new { name = "component-codec-size-research", status = "passed", count = size, compactBytes = bytes,
                refusedAt8MiB = refused, milliseconds = timer.Elapsed.TotalMilliseconds,
                syntheticMetadataOnly = true, executionAttempted = false, productionCapacityProven = false });
        }

        var bounded = CleanupPlanner.Preview(paths.Take(1000), context);
        timer.Restart();
        var result = new CleanupExecutor(journal).ExecuteApproved(bounded, () => Task.FromResult(context)).GetAwaiter().GetResult();
        var executionMilliseconds = timer.Elapsed.TotalMilliseconds;
        Require(result.State == "Completed" && result.Deleted == 1000 && paths.Take(1000).All(p => !File.Exists(p)), "Bounded execution failed.");
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var cancelledPlan = CleanupPlanner.Preview(paths.Skip(1000).Take(1000), context);
        var executor = new CleanupExecutor(journal) { TestCheckpoint = point =>
        {
            if (point != "before-delete") return;
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Cancellation barrier timed out.");
        } };
        var running = executor.ExecuteApproved(cancelledPlan, () => Task.FromResult(context), cancellation.Token);
        Exception? barrierError = null;
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Cancellation checkpoint not reached.");
            timer.Restart(); cancellation.Cancel();
        }
        catch (Exception error) { barrierError = error; cancellation.Cancel(); }
        finally { release.Set(); }
        var cancelled = running.GetAwaiter().GetResult();
        if (barrierError is not null) throw new IOException("Cancellation barrier failed after draining execution.", barrierError);
        var cancellationMilliseconds = timer.Elapsed.TotalMilliseconds;
        Require(cancelled.State == "Cancelled" && cancelled.Deleted == 1 && paths.Skip(1001).All(File.Exists), "Cancellation did not preserve unstarted samples.");
        var truncated = CleanupPlanner.Preview(paths.Skip(1001), context);
        Require(truncated.Truncated && truncated.Items.Count == 1000, "Production planner boundary changed.");
        checks.Add(new { name = "component-execution-cancellation", status = "passed", result, executionMilliseconds,
            deletedPerSecond = result.Deleted * 1000 / Math.Max(1, executionMilliseconds), cancelled, cancellationMilliseconds,
            preservedUnstarted = count - 1001, after = RuntimeBudget.Measure(audit), requiredConfirmationsPerBatch = 1,
            productionBatchLimit = 1000, estimatedConfirmationsFor100000 = 100, actualUiConfirmationsObserved = 0, productAcceptance = "not-passed",
            liveWatcherCovered = false, fullFiveMinuteRunCovered = false,
            limitation = "Fixed synthetic activity context and 16-byte samples; not real watcher, npm reconstruction, original benchmark eligibility, installed UI or resource acceptance." });
        CheckFixtureBudget();
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

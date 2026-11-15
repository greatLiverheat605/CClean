using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Sessions;
using CClean.Core.Storage;

namespace CClean.Poc;

internal static class CleanupBatchProbe
{
    private const int Count = 100_000;
    private const int SampleBytes = 16;
    private static readonly int[] sizes = [1000, 1001, 10_000, Count];

    public static void Run(ExperimentSandbox sandbox, List<object> checks)
    {
        // Metadata fixtures only; reserve one cluster per file plus audit/manifest headroom.
        if (Count * (4096L + SampleBytes) + 32L * 1024 * 1024 > sandbox.Budget)
            throw new ExperimentRefusedException("Cleanup batch probe needs an explicit non-system fixture budget of at least 444754432 bytes.", 2);
        var root = Path.Combine(sandbox.RunDirectory, "exclusive");
        var content = Path.Combine(root, "_cacache", "content-v2");
        var data = Path.Combine(sandbox.RunDirectory, "audit");
        Directory.CreateDirectory(content);
        var context = new CleanupContext(1, [new(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)],
            new ActivitySnapshot(1, [new(root, false, true, "synthetic metadata-only probe; no live watcher or real consumer")]), [data]);
        var timer = Stopwatch.StartNew();
        var sample = new byte[SampleBytes];
        var drive = new DriveInfo(Path.GetPathRoot(content)!);
        using var fixtureLease = new DirectoryLease(content);
        for (var i = 0; i < Count; i++)
        {
            if (i % 1000 == 0)
                ExperimentSandbox.CheckCapacity(sandbox.Budget, sandbox.Reserve, drive.AvailableFreeSpace, drive.DriveFormat,
                    drive.Name.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
            var path = Path.Combine(content, $"sample-{i:D6}");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(sample);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            if ((i + 1) % 10_000 == 0) Console.WriteLine($"Batch probe created {i + 1}/{Count}");
        }
        checks.Add(new { name = "cleanup-fixtures", status = "passed", fixture = root, count = Count,
            logicalBytes = Count * (long)SampleBytes, generationMilliseconds = timer.ElapsedMilliseconds,
            fixtureWritesForcedToDisk = false,
            contentReadCostCovered = false, liveWatcherCovered = false,
            limitation = "16-byte synthetic npm-layout metadata fixtures; not evidence for reading/deleting the existing 20 GiB benchmark or npm reconstruction" });

        timer.Restart();
        var paths = Directory.EnumerateFiles(content).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length != Count) throw new InvalidOperationException("Fixture enumeration count differs.");
        var enumerationMilliseconds = timer.ElapsedMilliseconds;
        var metadata = new List<CleanupItem>(Count);
        var rootIdentity = WindowsFileMetadata.Read(root).Identity ?? throw new IOException("Fixture root identity unknown.");
        timer.Restart();
        foreach (var path in paths)
        {
            var file = WindowsFileMetadata.Read(path);
            var decision = ProtectionPolicy.Classify(file, context.Rules, context.Activity, context.ProtectedRoots, DateTimeOffset.UtcNow);
            if (!decision.Eligible) throw new InvalidOperationException("Synthetic fixture is not eligible: " + decision.Reason);
            metadata.Add(new(file, decision, rootIdentity));
            if (sizes.Contains(metadata.Count))
                checks.Add(new { name = "cleanup-identity-cost", status = "passed", count = metadata.Count,
                    cumulativeIdentityMilliseconds = timer.ElapsedMilliseconds, enumerationMilliseconds,
                    logicalBytes = metadata.Count * (long)SampleBytes, allocatedBytes = metadata.Sum(i => i.File.AllocatedBytes ?? 0),
                    memory = Memory() });
        }

        using var journal = new CleanupJournal(data);
        var executor = new CleanupExecutor(journal);
        foreach (var size in sizes)
        {
            timer.Restart();
            var plan = CleanupPlanner.Preview(paths.Take(size), context);
            var previewMilliseconds = timer.ElapsedMilliseconds;
            var manifestBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(plan));
            var refusal = "not-attempted; bounded execution measured separately";
            if (size > 1000)
            {
                try
                {
                    executor.ExecuteApproved(plan, () => Task.FromResult(context)).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Truncated production plan unexpectedly accepted.");
                }
                catch (InvalidOperationException error) when (error.Message == "清单为空、截断或已失效，请重新预览。")
                { refusal = error.Message; }
            }
            checks.Add(new { name = "cleanup-original-limit", status = "passed", requested = size, plan.Items.Count,
                plan.EligibleCount, plan.Truncated, previewMilliseconds, manifestBytes, refusal,
                expectedConfirmationsAtOriginalLimit = (size + 999) / 1000, memory = Memory() });
        }

        // Research serialization only. These internal plans never reach ExecuteApproved.
        foreach (var size in sizes)
        {
            var research = new CleanupPlan(DateTimeOffset.UtcNow, context.Fingerprint(), metadata.Take(size).ToList(), false);
            timer.Restart();
            var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(research));
            var serializationMilliseconds = timer.ElapsedMilliseconds;
            var refusal = "codec-only research; never submitted for execution";
            int? compactBytes = null;
            try { compactBytes = Encoding.UTF8.GetByteCount(CleanupManifestCodec.Encode(research)); }
            catch (IOException error) when (error.Message == "审计清单超过 8 MiB，请缩小选择。")
            { refusal = error.Message; }
            checks.Add(new { name = "cleanup-full-manifest-research", status = "passed", count = size,
                productionPlan = false, executionAttempted = false, legacyManifestBytes = bytes, compactBytes, serializationMilliseconds,
                originalManifestLimitBytes = 8 * 1024 * 1024, refusal, memory = Memory() });
        }

        var bounded = CleanupPlanner.Preview(paths.Take(1000), context);
        var before = RuntimeBudget.Measure(data);
        timer.Restart();
        var result = executor.ExecuteApproved(bounded, () => Task.FromResult(context)).GetAwaiter().GetResult();
        var executionMilliseconds = timer.ElapsedMilliseconds;
        var events = journal.Events(bounded.Id.ToString());
        var recovery = CleanupRecovery.Inspect(journal, bounded.Id.ToString());
        if (result.State != "Completed" || result.Deleted != 1000 || events.Count != 2002 || recovery.Count != 1000)
            throw new InvalidOperationException("Bounded production execution/audit/recovery differs.");
        checks.Add(new { name = "cleanup-bounded-execution", status = "passed", result, executionMilliseconds,
            deletedPerSecond = result.Deleted * 1000d / Math.Max(1, executionMilliseconds),
            before, after = RuntimeBudget.Measure(data), files = DataFiles(data),
            events = events.Count, recoveryObjects = recovery.Count, expiresAt = bounded.Expires,
            remainingValidityMilliseconds = (bounded.Expires - DateTimeOffset.UtcNow).TotalMilliseconds,
            expiryObserved = false, confirmationCount = 1, liveWatcherCovered = false, memory = Memory() });

        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cancelled = CleanupPlanner.Preview(paths.Skip(1000).Take(1000), context);
        var cancellationExecutor = new CleanupExecutor(journal)
        {
            TestCheckpoint = point =>
            {
                if (point != "before-delete") return;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Cancellation probe barrier timed out.");
            }
        };
        var running = cancellationExecutor.ExecuteApproved(cancelled, () => Task.FromResult(context), cancellation.Token);
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Cancellation probe did not start an item.");
            timer.Restart();
            cancellation.Cancel();
        }
        finally { release.Set(); }
        var cancelledResult = running.GetAwaiter().GetResult();
        var cancellationMilliseconds = timer.Elapsed.TotalMilliseconds;
        if (cancelledResult.State != "Cancelled" || cancelledResult.Deleted != 1 || !paths.Skip(1001).Take(999).All(File.Exists))
            throw new InvalidOperationException("Cancellation did not preserve all unstarted objects.");
        checks.Add(new { name = "cleanup-cancellation", status = "passed", result = cancelledResult, cancellationMilliseconds,
            inFlightItemReleasedImmediately = true, preservedUnstartedObjects = 999,
            limitation = "Cancellation latency includes one metadata-only item and durable audit; not a slow-drive or UI measurement" });

        // Use the unchanged five-minute boundary without making this probe wait five minutes.
        var expirySource = CleanupPlanner.Preview(paths.Skip(2000).Take(3), context);
        var nearExpiry = new CleanupPlan(DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(2), context.Fingerprint(), expirySource.Items.ToList(), false);
        var expiryExecutor = new CleanupExecutor(journal)
        {
            TestCheckpoint = point =>
            {
                if (point == "after-result")
                    while (DateTimeOffset.UtcNow < nearExpiry.Expires) Thread.Sleep(10);
            }
        };
        timer.Restart();
        var expiryResult = expiryExecutor.ExecuteApproved(nearExpiry, () => Task.FromResult(context)).GetAwaiter().GetResult();
        if (expiryResult.State != "NeedsPreview" || expiryResult.Deleted != 1 || !paths.Skip(2001).Take(2).All(File.Exists))
            throw new InvalidOperationException("Expiry boundary did not preserve unstarted objects.");
        checks.Add(new { name = "cleanup-expiry-boundary", status = "passed", result = expiryResult,
            elapsedMilliseconds = timer.ElapsedMilliseconds, createdArtificiallyEarlier = true,
            expiresAt = nearExpiry.Expires, stoppedAt = DateTimeOffset.UtcNow, preservedUnstartedObjects = 2,
            fullFiveMinuteRunCovered = false, after = RuntimeBudget.Measure(data), files = DataFiles(data) });
        checks.Add(new { name = "cleanup-feasibility-conclusion", status = "passed", productionBatchLimit = 1000,
            confirmationsFor100000 = 100, productAcceptance = "not-passed",
            blockers = new[] { "live watcher self-delete still invalidates the next object", "100 confirmations fail the planned interaction burden", "compact manifest and paged audit/recovery require separate component measurements; unchanged planner still caps at 1000" },
            protocolChanged = false, existingUserFilesDeleted = false, memory = Memory() });
    }

    private static object Memory()
    {
        using var process = Process.GetCurrentProcess();
        return new { workingSetBytes = process.WorkingSet64, peakWorkingSetBytes = process.PeakWorkingSet64,
            managedBytes = GC.GetTotalMemory(false) };
    }

    private static object[] DataFiles(string root) => Directory.EnumerateFiles(root)
        .Select(path => (object)new { name = Path.GetFileName(path), bytes = new FileInfo(path).Length }).ToArray();
}

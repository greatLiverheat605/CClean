using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CClean.Core.Cleanup;
using CClean.Core.Storage;

namespace CClean.Tests;

public partial class CleanupWorkflowTests
{
    private static CleanupContext SegmentContext(Fixture fixture) => fixture.Context() with { Contract = CleanupContract.SegmentReviewResearch };

    [Theory]
    [Trait("Category", "SegmentResearchScale")]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(10000)]
    public async Task SegmentResearchScaleUsesOneFixedDurableManifest(int count)
        => await RunSegmentScale(count, count + 1);

    [Fact]
    [Trait("Category", "SegmentResearchRootScale")]
    public async Task SegmentResearchTenThousandApprovedWithinHundredThousandObserved()
        => await RunSegmentScale(10000, 100000);

    [Fact]
    [Trait("Category", "SegmentResearchTwoBatchScale")]
    public async Task SegmentResearchTwoBatchesWithinHundredThousandObserved()
        => await RunSegmentScale(10000, 100000, batches: 2);

    [Fact]
    [Trait("Category", "SegmentResearchBatchScale")]
    public async Task SegmentResearchHundredThousandUsesTenSeparatelyConfirmedManifests()
        => await RunSegmentScale(10000, 100000, batches: 10);

    private async Task RunSegmentScale(int count, int observedCount, int batches = 1)
    {
        void SampleMemory(string phase)
        {
            if (observedCount < 100_000) return;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            output.WriteLine(JsonSerializer.Serialize(new { phase, workingSetBytes = process.WorkingSet64,
                peakWorkingSetBytes = process.PeakWorkingSet64, privateBytes = process.PrivateMemorySize64,
                managedBytes = GC.GetTotalMemory(false), heapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
                allocatedBytes = GC.GetTotalAllocatedBytes() }));
        }
        SampleMemory("before-fixture");
        using var fixture = new Fixture(count: observedCount) { ObjectReview = true };
        SampleMemory("fixture-created");
        await fixture.Ready(timeoutSeconds: observedCount > 10001 ? 60 : 10);
        SampleMemory("first-scan-ready");
        var baselineWorkingSet = Process.GetCurrentProcess().WorkingSet64;
        using var journal = new CleanupJournal(fixture.Data);
        long last = 0, beforeDelete = 0, afterDelete = 0;
        long reviewTicks = 0, prepareTicks = 0, deleteTicks = 0, resultTicks = 0;
        var executor = new CleanupExecutor(journal)
        {
            AllowSegmentResearch = true,
            TestCheckpoint = point =>
            {
                var now = Stopwatch.GetTimestamp();
                switch (point)
                {
                    case "after-plan": last = now; break;
                    case "after-review": reviewTicks += now - last; last = now; break;
                    case "before-delete": prepareTicks += now - last; beforeDelete = now; break;
                    case "after-delete": deleteTicks += now - beforeDelete; afterDelete = now; break;
                    case "after-result": resultTicks += now - afterDelete; last = now; break;
                }
            }
        };
        Guid? previous = null;
        var total = Stopwatch.StartNew();
        for (var batch = 0; batch < batches; batch++)
        {
            var baselineScan = fixture.Monitor.ScanTime;
            var baselineRevision = fixture.Monitor.Revision;
            var baselineReview = reviewTicks; var baselinePrepare = prepareTicks;
            var baselineDelete = deleteTicks; var baselineResult = resultTicks;
            var allocated = GC.GetTotalAllocatedBytes();
            var paths = fixture.Paths.Skip(batch * count).Take(count).ToArray();
            var timer = Stopwatch.StartNew();
            var plan = batch == 0 ? CleanupPlanner.PreviewSegmentResearch(paths, SegmentContext(fixture))
                : await executor.PreviewNextSegmentResearch(paths, () => Task.FromResult(SegmentContext(fixture)));
            SampleMemory($"batch-{batch + 1}-preview");
            var previewMs = timer.ElapsedMilliseconds;
            var bytes = Encoding.UTF8.GetByteCount(CleanupManifestCodec.Encode(plan));
            Assert.True(bytes <= CleanupJournal.MaximumManifestBytes);
            Assert.Equal(count, plan.EligibleCount);
            Assert.Equal(previous, plan.PreviousRunId);
            Assert.DoesNotContain(journal.Runs(), r => r.Id == plan.Id.ToString());
            Assert.All(paths, path => Assert.True(File.Exists(path))); // Still unapproved until the call below.
            var validityUsedBeforeExecutionMs = (DateTimeOffset.UtcNow - plan.Created).TotalMilliseconds;
            timer.Restart();
            var result = await executor.ExecuteApproved(plan, () => Task.FromResult(SegmentContext(fixture)));
            SampleMemory($"batch-{batch + 1}-execution");
            var executionMs = timer.ElapsedMilliseconds;
            var usage = RuntimeBudget.Measure(fixture.Data);
            var peak = Process.GetCurrentProcess().PeakWorkingSet64;
            output.WriteLine(JsonSerializer.Serialize(new { batch = batch + 1, requestedBatches = batches, count, observedCount, previewMs, executionMs, bytes, result, executor.ReviewedObjects,
                baselineWorkingSet, peakWorkingSetBytes = peak, componentMemoryWithin200MiB = peak <= 200L * 1024 * 1024,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, heapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
                usage, fixture.Monitor.ApplicationGaps, fixture.Monitor.NativeGaps,
                retainedResearchReceipts = executor.RetainedResearchReceiptCount,
                scanGateWaitMs = executor.ScanGateWait.TotalMilliseconds,
                scanCount = fixture.Monitor.Revision - baselineRevision,
                scanMs = (fixture.Monitor.ScanTime - baselineScan).TotalMilliseconds,
                validityUsedBeforeExecutionMs,
                reviewMs = (reviewTicks - baselineReview) * 1000d / Stopwatch.Frequency,
                prepareAndGateMs = (prepareTicks - baselinePrepare) * 1000d / Stopwatch.Frequency,
                deleteMarkMs = (deleteTicks - baselineDelete) * 1000d / Stopwatch.Frequency,
                resultCommitMs = (resultTicks - baselineResult) * 1000d / Stopwatch.Frequency,
                remainingValidityMs = (plan.Expires - DateTimeOffset.UtcNow).TotalMilliseconds,
                syntheticThreeByteFixtures = true, realWatcher = true, installedUiAcceptance = false }));
            if (result.State != "Completed")
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    failedBatch = batch + 1, remainingApproved = paths.Count(File.Exists),
                    unapprovedRemaining = fixture.Paths.Skip((batch + 1) * count).Count(File.Exists),
                    unapprovedExpected = observedCount - (batch + 1) * count,
                    durableRun = journal.Runs().Single(run => run.Id == plan.Id.ToString()),
                    recordedDeletions = journal.Events(plan.Id.ToString()).Count(e => e.State == "Deleted")
                }));
            Assert.Equal("Completed", result.State); Assert.Equal(count, result.Deleted);
            Assert.True(DateTimeOffset.UtcNow < plan.Expires);
            Assert.All(fixture.Paths.Skip(count * (batch + 1)), path => Assert.True(File.Exists(path)));
            Assert.Equal(0, executor.RetainedReceiptCount); // Research must not feed ordinary cross-batch approval.
            Assert.InRange(executor.RetainedResearchReceiptCount, count, count + 1000);
            var segmentStarts = 0; var contractFound = false; var ordinaryReviewFound = false;
            long eventAfter = 0; long? eventThrough = null;
            while (true)
            {
                var page = journal.EventsPage(plan.Id.ToString(), eventAfter, 1000, eventThrough);
                Assert.True(page.RunFound);
                segmentStarts += page.Items.Count(e => e.State == "SegmentStart");
                contractFound |= page.Items.Any(e => e.State == "Contract" && e.Reason.Contains("SegmentReviewResearch"));
                ordinaryReviewFound |= page.Items.Any(e => e.State == "ObjectReview");
                if (!page.HasMore) break;
                eventAfter = page.NextSequence; eventThrough = page.ThroughSequence;
            }
            Assert.Equal(count, segmentStarts);
            Assert.True(contractFound); Assert.False(ordinaryReviewFound);
            SampleMemory($"batch-{batch + 1}-audit-events");
            var recovered = 0; var recoveryAfter = -1; long? recoveryThrough = null;
            while (true)
            {
                var page = CleanupRecovery.InspectPage(journal, plan.Id.ToString(), recoveryAfter, 1000, recoveryThrough);
                Assert.True(page.RunFound);
                recovered += page.Items.Count(i => i.State == "Deleted");
                if (recoveryAfter == -1) SampleMemory($"batch-{batch + 1}-recovery-first-page");
                if (!page.HasMore) break;
                recoveryAfter = page.NextOrdinal; recoveryThrough = page.ThroughSequence;
            }
            Assert.Equal(count, recovered);
            SampleMemory($"batch-{batch + 1}-audit-recovery");
            if (batches > 1) Assert.True(Process.GetCurrentProcess().PeakWorkingSet64 <= 200L * 1024 * 1024,
                "隔离研究宿主已超过 200 MiB，停止扩展批次；此测量不能代替完整产品资源验收。");
            previous = plan.Id;
        }
        Assert.All(fixture.Paths.Take(count * batches), path => Assert.False(File.Exists(path)));
        Assert.All(fixture.Paths.Skip(count * batches), path => Assert.True(File.Exists(path)));
        Assert.Equal(batches, journal.Runs().Count);
        output.WriteLine(JsonSerializer.Serialize(new { separatelyConfirmedManifests = batches, deleted = count * batches,
            unapprovedPreserved = observedCount - count * batches, totalMs = total.ElapsedMilliseconds,
            usage = RuntimeBudget.Measure(fixture.Data), peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.PreviewNext(fixture.Paths.TakeLast(1), () => Task.FromResult(SegmentContext(fixture))));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("current-write")]
    [InlineData("current-replace")]
    [InlineData("current-ads")]
    [InlineData("replace")]
    [InlineData("ads")]
    [InlineData("new-file")]
    [InlineData("rename")]
    [InlineData("unapproved-delete")]
    [InlineData("parent-ads")]
    [InlineData("consumer")]
    [InlineData("configuration")]
    [InlineData("watcher-fault")]
    [InlineData("application-overflow")]
    [InlineData("observer-removed")]
    [InlineData("cancel")]
    [InlineData("expiry")]
    [InlineData("result-audit")]
    [InlineData("segment-audit")]
    [InlineData("started-audit")]
    public async Task SegmentResearchStopsOnChangesInFutureSegmentsOrControlFailure(string change)
    {
        using var fixture = new Fixture(unapproved: true, count: 201) { ObjectReview = true };
        await fixture.Ready();
        var snapshotFilesBeforeExecution = fixture.Monitor.Snapshot.Files.Count;
        var plan = CleanupPlanner.PreviewSegmentResearch(fixture.Paths, SegmentContext(fixture));
        if (change == "expiry") plan = new(DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(3), plan.ContextFingerprint,
            plan.Items.ToList(), false, CleanupContract.SegmentReviewResearch);
        using var journal = new CleanupJournal(fixture.Data);
        using var cancel = new CancellationTokenSource();
        journal.BeforeRecordWrite = state =>
        {
            if (change == "result-audit" && state == "Deleted" || change == "segment-audit" && state == "SegmentStart" || change == "started-audit" && state == "Started")
                throw new IOException("controlled durable audit failure");
        };
        var injected = false;
        var executor = new CleanupExecutor(journal)
        {
            AllowSegmentResearch = true,
            TestCheckpoint = point =>
            {
                if (point != "after-result" || injected) return;
                injected = true;
                Assert.Equal(snapshotFilesBeforeExecution, fixture.Monitor.Snapshot.Files.Count);
                var sequence = fixture.Monitor.Observe().Sequence;
                var future = fixture.Paths[150];
                switch (change)
                {
                    case "current-write": File.AppendAllText(fixture.Paths[1], "external", Encoding.UTF8); break;
                    case "current-replace": File.Delete(fixture.Paths[1]); File.WriteAllBytes(fixture.Paths[1], [9]); break;
                    case "current-ads": File.WriteAllBytes(fixture.Paths[1] + ":external", [9]); break;
                    case "write": File.AppendAllText(future, "external", Encoding.UTF8); break;
                    case "replace": File.Delete(future); File.WriteAllBytes(future, [9]); break;
                    case "ads": File.WriteAllBytes(future + ":external", [9]); break;
                    case "new-file": File.WriteAllBytes(fixture.Unapproved + ".new", [9]); break;
                    case "rename": File.Move(fixture.Unapproved, fixture.Unapproved + ".renamed"); break;
                    case "unapproved-delete": File.Delete(fixture.Unapproved); break;
                    case "parent-ads": File.WriteAllBytes(Path.GetDirectoryName(future)! + ":external", [9]); break;
                    case "consumer": fixture.ConsumerActive = true; break;
                    case "configuration": fixture.ConfigurationVersion++; break;
                    case "watcher-fault": fixture.Monitor.ReportWatcherError(new IOException("controlled gap")); break;
                    case "application-overflow":
                        for (var i = 0; i < 2000; i++) fixture.Monitor.Rescan();
                        Assert.True(fixture.Monitor.ApplicationGaps > 0); break;
                    case "cancel": cancel.Cancel(); break;
                    case "expiry": Thread.Sleep(TimeSpan.FromSeconds(3)); break;
                }
                if (change is "write" or "replace" or "ads" or "new-file" or "rename" or "unapproved-delete" or "parent-ads" or "current-write" or "current-replace" or "current-ads")
                    Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(5)));
            }
        };
        var result = await executor.ExecuteApproved(plan, () => Task.FromResult(injected && change == "observer-removed"
            ? SegmentContext(fixture) with { Observations = [] } : SegmentContext(fixture)), cancel.Token);
        Assert.Equal(change == "cancel" ? "Cancelled" : change.EndsWith("audit") ? "Failed" : "NeedsPreview", result.State);
        Assert.Equal(change is "segment-audit" or "started-audit" ? 0 : 1, result.Deleted);
        if (change == "started-audit")
        {
            Assert.True(File.Exists(fixture.Paths[0]));
            Assert.DoesNotContain(journal.Events(plan.Id.ToString()), e => e.State is "SegmentStart" or "Started");
        }
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
        Assert.DoesNotContain(journal.Events(plan.Id.ToString()), e => e.State == "Started" && e.Path == fixture.Paths[1]);
        Assert.Equal(0, executor.RetainedReceiptCount);
    }

    [Fact]
    public async Task SegmentResearchRequiresExplicitExecutorAndNeverRestoresApproval()
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var context = SegmentContext(fixture);
        var plan = CleanupPlanner.PreviewSegmentResearch(fixture.Paths, context);
        using (var journal = new CleanupJournal(fixture.Data))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CleanupExecutor(journal).ExecuteApproved(plan, () => Task.FromResult(context)));
            Assert.Empty(journal.Runs()); Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
            journal.Begin(plan); journal.Record(plan.Id, plan.Items[0].Path, "Started", "controlled interruption, no deletion");
        }
        using var reopened = new CleanupJournal(fixture.Data);
        Assert.Equal("Interrupted", Assert.Single(reopened.Runs()).State);
        Assert.Equal(3, reopened.Manifest(plan.Id.ToString()).Count);
        Assert.Equal(3, CleanupRecovery.Inspect(reopened, plan.Id.ToString()).Count);
        Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
        await Assert.ThrowsAnyAsync<Exception>(() => new CleanupExecutor(reopened) { AllowSegmentResearch = true }
            .ExecuteApproved(plan, () => Task.FromResult(context))); // Existing durable ID is not replayable.
        Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task SegmentResearchChecksANewSegmentEvenWithoutNotifications()
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var context = SegmentContext(fixture);
        var plan = CleanupPlanner.PreviewSegmentResearch(fixture.Paths, context);
        using var journal = new CleanupJournal(fixture.Data);
        journal.Begin(plan);
        var review = new ObjectCleanupReview(plan, context);
        var stale = plan.Items[1] with { File = plan.Items[1].File with { ChangeTime = plan.Items[1].File.ChangeTime - 1 } };
        Assert.True(review.Validate(context, [plan.Items[0]], journal, CancellationToken.None, out _, forceObjectReview: true));
        Assert.False(review.Validate(context, [stale], journal, CancellationToken.None, out _, forceObjectReview: true));
        Assert.Equal(2, review.ReviewedObjects);
        Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
        output.WriteLine("Synthetic stale snapshot with unchanged notification baseline: entering a segment forces metadata revalidation. No silent external-write detection claim.");
    }

    [Fact]
    public async Task SegmentResearchStillChecksLateNotificationsAtTheDeletionHandle()
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.PreviewSegmentResearch(fixture.Paths, SegmentContext(fixture));
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal)
        {
            AllowSegmentResearch = true,
            TestCheckpoint = point =>
            {
                if (point != "before-delete") return;
                var sequence = fixture.Monitor.Observe().Sequence;
                File.WriteAllBytes(fixture.Unapproved + ".late", [9]);
                Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(5)));
            }
        };
        var result = await executor.ExecuteApproved(plan, () => Task.FromResult(SegmentContext(fixture)));
        Assert.Equal("NeedsPreview", result.State); Assert.Equal(0, result.Deleted);
        Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task SegmentResearchLargeSnapshotPacingKeepsNotificationsAndOverflowImmediate()
    {
        using var fixture = new Fixture(count: 2001) { ObjectReview = true };
        await fixture.Ready();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Monitor.BeforeScan = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        try
        {
            fixture.Monitor.Rescan();
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            var before = fixture.Monitor.Observe();
            File.AppendAllText(fixture.Paths[0], "changed", Encoding.UTF8);
            Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > before.Sequence, TimeSpan.FromSeconds(5)));
            Assert.False(fixture.Monitor.IsObservationCurrent(before));
            Assert.Contains(fixture.Monitor.Observe().Events, e => e.Path == fixture.Paths[0]);
            for (var i = 0; i < 2000; i++) fixture.Monitor.Rescan();
            Assert.True(fixture.Monitor.ApplicationGaps > 0);
            Assert.False(fixture.Monitor.HasContinuousObservation);
            Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
        }
        finally { fixture.Monitor.BeforeScan = null; release.Set(); }
    }

    [Fact]
    public async Task SegmentResearchRejectsOversizedFixedSetsWithoutPartialApproval()
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var context = SegmentContext(fixture);
        // Distinct nonexistent paths test admission only; no large real-directory execution is claimed.
        var many = Enumerable.Range(0, 10001).Select(i => Path.Combine(fixture.Root, "not-created-" + i));
        Assert.Throws<InvalidOperationException>(() => CleanupPlanner.PreviewSegmentResearch(many, context));
        var ordinary = CleanupPlanner.Preview(fixture.Paths.Concat(many), context);
        Assert.True(ordinary.Truncated); Assert.Equal(1000, ordinary.Items.Count);
        var small = CleanupPlanner.PreviewSegmentResearch(fixture.Paths, context);
        var huge = new CleanupPlan(small.Created, small.ContextFingerprint,
            small.Items.Select(i => i with { Decision = i.Decision with { Reason = new string('x', CleanupJournal.MaximumManifestBytes) } }).ToList(), false, context.Contract);
        using var journal = new CleanupJournal(fixture.Data);
        await Assert.ThrowsAsync<IOException>(() => new CleanupExecutor(journal) { AllowSegmentResearch = true }
            .ExecuteApproved(huge, () => Task.FromResult(context)));
        Assert.Empty(journal.Runs()); Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
    }
}

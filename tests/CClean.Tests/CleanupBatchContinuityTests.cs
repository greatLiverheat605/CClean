using CClean.Core.Cleanup;

namespace CClean.Tests;

public partial class CleanupWorkflowTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(100, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    public async Task BatchContinuityCreatesFreshUnapprovedManifestsAndPreservesUnselectedFiles(int batchSize, bool research)
    {
        using var fixture = new Fixture(count: batchSize * 3 + 1) { ObjectReview = true };
        await fixture.Ready();
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal) { AllowSegmentResearch = research };
        var ids = new List<Guid>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var batch = 0; batch < 3; batch++)
        {
            var paths = fixture.Paths.Skip(batch * batchSize).Take(batchSize).ToArray();
            var plan = batch == 0 ? PreviewBatch(paths, BatchContext(fixture, research)) :
                await NextBatch(executor, research, paths, () => Task.FromResult(BatchContext(fixture, research)));
            Assert.Equal(batchSize, plan.EligibleCount);
            Assert.Equal(batch == 0 ? (Guid?)null : ids[^1], plan.PreviousRunId);
            Assert.DoesNotContain(plan.Id, ids);
            Assert.All(paths, path => Assert.True(File.Exists(path))); // Preview is not confirmation.
            Assert.DoesNotContain(journal.Runs(), r => r.Id == plan.Id.ToString());
            Assert.Equal(TimeSpan.FromMinutes(5), plan.Expires - plan.Created);
            var result = await executor.ExecuteApproved(plan, () => Task.FromResult(BatchContext(fixture, research)));
            Assert.Equal("Completed", result.State); Assert.Equal(batchSize, result.Deleted);
            if (batch > 0) Assert.Contains(journal.Events(plan.Id.ToString()), e => e.State == "ContinuationFrom" && e.Reason == ids[^1].ToString());
            ids.Add(plan.Id);
            Assert.Equal(0, research ? executor.RetainedReceiptCount : executor.RetainedResearchReceiptCount);
            Assert.True(executor.RetainedReceiptCount + executor.RetainedResearchReceiptCount <= (research ? 11000 : 2000));
            await Settled(fixture, fixture.Paths.Length - (batch + 1) * batchSize);
            Assert.True(BatchContext(fixture, research).Activity.Scopes[0].Active);
        }
        Assert.True(File.Exists(fixture.Paths[^1]));
        Assert.Equal(3, journal.Runs().Count);
        Assert.All(ids, id => Assert.Equal(batchSize, CleanupRecovery.Inspect(journal, id.ToString()).Count(e => e.State == "Deleted")));
        output.WriteLine($"Real watcher ({(research ? "segment research" : "object review")}): 3 separately approved batches x {batchSize}, {watch.ElapsedMilliseconds} ms including snapshot settling; unselected file retained; raw activity remains active.");
    }

    [Fact]
    public async Task ResearchNextPreviewWaitsForPostBatchScan()
    {
        using var fixture = new Fixture(count: 2) { ObjectReview = true };
        await fixture.Ready();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal)
        {
            AllowSegmentResearch = true,
            TestCheckpoint = point =>
            {
                if (point == "after-result") fixture.Monitor.BeforeScan = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
            }
        };
        try
        {
            var first = CleanupPlanner.PreviewSegmentResearch(fixture.Paths.Take(1), SegmentContext(fixture));
            Assert.Equal("Completed", (await executor.ExecuteApproved(first, () => Task.FromResult(SegmentContext(fixture)))).State);
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            var next = executor.PreviewNextSegmentResearch(fixture.Paths.Skip(1), () => Task.FromResult(SegmentContext(fixture)));
            await Task.Delay(150);
            Assert.False(next.IsCompleted);
            Assert.Single(journal.Runs());
            release.Set();
            var plan = await next.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(first.Id, plan.PreviousRunId);
            Assert.True(File.Exists(fixture.Paths[1]));
            Assert.Equal(1, plan.EligibleCount);
        }
        finally { fixture.Monitor.BeforeScan = null; release.Set(); }
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("new-file", false)]
    [InlineData("new-file", true)]
    [InlineData("rename", false)]
    [InlineData("rename", true)]
    [InlineData("unapproved-delete", false)]
    [InlineData("unapproved-delete", true)]
    [InlineData("consumer", false)]
    [InlineData("consumer", true)]
    [InlineData("configuration", false)]
    [InlineData("configuration", true)]
    [InlineData("watcher-fault", false)]
    [InlineData("watcher-fault", true)]
    [InlineData("observer-removed", false)]
    [InlineData("observer-removed", true)]
    [InlineData("audit", false)]
    [InlineData("audit", true)]
    public async Task BatchContinuityRejectsInterveningChangesAndAuditFailure(string change, bool research)
    {
        using var fixture = new Fixture(unapproved: true) { ObjectReview = true };
        await fixture.Ready();
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal) { AllowSegmentResearch = research };
        var first = PreviewBatch(fixture.Paths.Take(1), BatchContext(fixture, research));
        Assert.Equal("Completed", (await executor.ExecuteApproved(first, () => Task.FromResult(BatchContext(fixture, research)))).State);
        await Settled(fixture, 3);
        var sequence = fixture.Monitor.Observe().Sequence;
        switch (change)
        {
            case "write": File.AppendAllText(fixture.Paths[1], "external", System.Text.Encoding.UTF8); break;
            case "new-file": File.WriteAllBytes(fixture.Unapproved + ".new", [8]); break;
            case "rename": File.Move(fixture.Unapproved, fixture.Unapproved + ".renamed"); break;
            case "unapproved-delete": File.Delete(fixture.Unapproved); break;
            case "consumer": fixture.ConsumerActive = true; break;
            case "configuration": fixture.ConfigurationVersion++; break;
            case "watcher-fault": fixture.Monitor.ReportWatcherError(new IOException("controlled")); break;
            case "audit": journal.BeforeRecordWrite = state => { if (state == "NextPreview") throw new IOException("controlled preview audit failure"); }; break;
        }
        if (change is "write" or "new-file" or "rename" or "unapproved-delete")
            Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<Exception>(() => NextBatch(executor, research, fixture.Paths.Skip(1), () =>
        {
            var context = BatchContext(fixture, research);
            return Task.FromResult(change == "observer-removed" ? context with { Observations = [] } : context);
        }));
        Assert.Equal(0, executor.RetainedReceiptCount + executor.RetainedResearchReceiptCount);
        Assert.Single(journal.Runs());
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
        journal.BeforeRecordWrite = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => NextBatch(executor, research, fixture.Paths.Skip(1), () => Task.FromResult(BatchContext(fixture, research))));
    }

    [Theory]
    [InlineData("new-file", false)]
    [InlineData("new-file", true)]
    [InlineData("consumer", false)]
    [InlineData("consumer", true)]
    [InlineData("configuration", false)]
    [InlineData("configuration", true)]
    public async Task BatchContinuityRejectsChangesAfterPreviewBeforeApproval(string change, bool research)
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal) { AllowSegmentResearch = research };
        var first = PreviewBatch(fixture.Paths.Take(1), BatchContext(fixture, research));
        Assert.Equal("Completed", (await executor.ExecuteApproved(first, () => Task.FromResult(BatchContext(fixture, research)))).State);
        await Settled(fixture, 2);
        var next = await NextBatch(executor, research, fixture.Paths.Skip(1), () => Task.FromResult(BatchContext(fixture, research)));
        if (change == "consumer") fixture.ConsumerActive = true;
        else if (change == "configuration") fixture.ConfigurationVersion++;
        else
        {
            var sequence = fixture.Monitor.Observe().Sequence;
            File.WriteAllBytes(fixture.Paths[0] + ".new", [8]);
            Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(5)));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteApproved(next, () => Task.FromResult(BatchContext(fixture, research))));
        Assert.Single(journal.Runs());
        Assert.Equal(0, executor.RetainedReceiptCount + executor.RetainedResearchReceiptCount);
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchContinuityCannotBeRestoredByAnotherExecutorOrReplay(bool research)
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal) { AllowSegmentResearch = research };
        var first = PreviewBatch(fixture.Paths.Take(1), BatchContext(fixture, research));
        await executor.ExecuteApproved(first, () => Task.FromResult(BatchContext(fixture, research)));
        await Settled(fixture, 2);
        var next = await NextBatch(executor, research, fixture.Paths.Skip(1).Take(1), () => Task.FromResult(BatchContext(fixture, research)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CleanupExecutor(journal) { AllowSegmentResearch = research }.ExecuteApproved(next, () => Task.FromResult(BatchContext(fixture, research))));
        Assert.True(File.Exists(fixture.Paths[1])); Assert.Single(journal.Runs());
        Assert.Equal("Completed", (await executor.ExecuteApproved(next, () => Task.FromResult(BatchContext(fixture, research)))).State);
        await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteApproved(next, () => Task.FromResult(BatchContext(fixture, research))));
        Assert.True(File.Exists(fixture.Paths[2]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BatchContinuityDoesNotExtendExpiredEvidenceByCreatingAPreview(bool previewBeforeExpiry, bool research)
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal) { AllowSegmentResearch = research };
        var original = PreviewBatch(fixture.Paths.Take(1), BatchContext(fixture, research));
        var first = new CleanupPlan(DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(3), original.ContextFingerprint,
            original.Items.ToList(), false, BatchContext(fixture, research).Contract);
        Assert.Equal("Completed", (await executor.ExecuteApproved(first, () => Task.FromResult(BatchContext(fixture, research)))).State);
        await Settled(fixture, 2);
        var next = previewBeforeExpiry ? await NextBatch(executor, research, fixture.Paths.Skip(1), () => Task.FromResult(BatchContext(fixture, research))) : null;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (next is null)
            await Assert.ThrowsAsync<InvalidOperationException>(() => NextBatch(executor, research, fixture.Paths.Skip(1), () => Task.FromResult(BatchContext(fixture, research))));
        else
        {
            Assert.True(DateTimeOffset.UtcNow < next.Expires);
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteApproved(next, () => Task.FromResult(BatchContext(fixture, research))));
        }
        Assert.Single(journal.Runs()); Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
    }

    [Theory]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("failed-result", false)]
    [InlineData("failed-result", true)]
    public async Task BatchContinuityRequiresACompletelySuccessfulPreviousRun(string failure, bool research)
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        using var journal = new CleanupJournal(fixture.Data);
        using var cancel = new CancellationTokenSource();
        journal.BeforeRecordWrite = state => { if (failure == "failed-result" && state == "Deleted") throw new IOException("controlled"); };
        var executor = new CleanupExecutor(journal)
        {
            AllowSegmentResearch = research,
            TestCheckpoint = point => { if (failure == "cancel" && point == "after-result") cancel.Cancel(); }
        };
        var first = PreviewBatch(fixture.Paths.Take(2), BatchContext(fixture, research));
        var result = await executor.ExecuteApproved(first, () => Task.FromResult(BatchContext(fixture, research)), cancel.Token);
        Assert.Equal(failure == "cancel" ? "Cancelled" : "Failed", result.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NextBatch(executor, research, fixture.Paths.Skip(1), () => Task.FromResult(BatchContext(fixture, research))));
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
    }

    [Theory]
    [InlineData(false, 1000)]
    [InlineData(true, 10000)]
    public async Task BatchContinuityReceiptRetentionIsBoundedAcrossManySyntheticGenerations(bool research, int count)
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var context = BatchContext(fixture, research);
        var plan = PreviewBatch(fixture.Paths.Take(1), context);
        ObjectCleanupReview? previous = null;
        for (var generation = 0; generation < 5; generation++)
        {
            var review = new ObjectCleanupReview(plan, context, previous);
            Assert.True(review.ReceiptCount <= 1000);
            for (var i = 0; i < count; i++)
            {
                var item = plan.Items[0] with { File = plan.Items[0].File with { Path = Path.Combine(Path.GetDirectoryName(fixture.Paths[0])!, $"receipt-only-{generation}-{i}") } };
                review.Confirm(item, context);
            }
            Assert.True(review.ReceiptCount <= count + 1000);
            previous = review;
        }
        Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
        output.WriteLine($"Synthetic state-only retention check: 5 x {count} receipts, max {count + 1000} retained; no deletion or real receipt attribution claimed.");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task BatchContinuityCannotCrossContracts(bool previousResearch, bool contextResearch, bool executeForgedPlan)
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal) { AllowSegmentResearch = true };
        var first = PreviewBatch(fixture.Paths.Take(1), BatchContext(fixture, previousResearch));
        Assert.Equal("Completed", (await executor.ExecuteApproved(first, () => Task.FromResult(BatchContext(fixture, previousResearch)))).State);
        await Settled(fixture, 2);
        if (executeForgedPlan)
        {
            var context = BatchContext(fixture, contextResearch);
            var candidate = PreviewBatch(fixture.Paths.Skip(1), context with { Activity = context.IndependentActivity! });
            var forged = new CleanupPlan(candidate.Created, context.Fingerprint(), candidate.Items.ToList(), false, context.Contract, first.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteApproved(forged, () => Task.FromResult(context)));
        }
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => NextBatch(executor, !previousResearch, fixture.Paths.Skip(1),
                () => Task.FromResult(BatchContext(fixture, contextResearch))));
        Assert.Single(journal.Runs());
        Assert.Equal(0, executor.RetainedReceiptCount + executor.RetainedResearchReceiptCount);
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
    }

    private static CleanupContext BatchContext(Fixture fixture, bool research) => research ? SegmentContext(fixture) : fixture.Context();

    private static CleanupPlan PreviewBatch(IEnumerable<string> paths, CleanupContext context) => context.Contract == CleanupContract.SegmentReviewResearch
        ? CleanupPlanner.PreviewSegmentResearch(paths, context) : CleanupPlanner.Preview(paths, context);

    private static Task<CleanupPlan> NextBatch(CleanupExecutor executor, bool research, IEnumerable<string> paths, Func<Task<CleanupContext>> context)
        => research ? executor.PreviewNextSegmentResearch(paths, context) : executor.PreviewNext(paths, context);

    private static async Task Settled(Fixture fixture, int files)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (fixture.Monitor.Snapshot.Files.Count != files && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(files, fixture.Monitor.Snapshot.Files.Count);
        Assert.True(fixture.Monitor.HasContinuousObservation);
    }
}

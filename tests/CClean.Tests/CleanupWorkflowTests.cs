using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Monitoring;
using CClean.Core.Sessions;

namespace CClean.Tests;

[Trait("Category", "CleanupWorkflow")]
[Trait("Category", "MvpP0")]
public partial class CleanupWorkflowTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task CompletedObjectApprovalDoesNotReleaseRecencyProtectionForANewManifest()
    {
        using var fixture = new Fixture(count: 4) { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths.Take(3), fixture.Context());
        using var journal = new CleanupJournal(fixture.Data);
        var result = await new CleanupExecutor(journal).ExecuteApproved(plan, () => Task.FromResult(fixture.Context()));
        Assert.Equal("Completed", result.State);
        Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.LastActivity is not null, TimeSpan.FromSeconds(5)));
        var next = CleanupPlanner.Preview(fixture.Paths.Skip(3), fixture.Context());
        Assert.Equal(0, next.EligibleCount);
        Assert.Contains("活动保护", Assert.Single(next.Items).Reason);
        Assert.True(File.Exists(fixture.Paths[3]));
        output.WriteLine("The new contract is scoped to one approval. A subsequent manifest still encounters 30-minute recency protection; cross-batch usability is not qualified.");
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(10000)]
    public async Task ObjectContractDoesNotTurnOversizedSelectionIntoImplicitBatches(int count)
    {
        using var fixture = new Fixture(count: count) { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        Assert.True(plan.Truncated); Assert.Equal(1000, plan.Items.Count);
        using var journal = new CleanupJournal(fixture.Data);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CleanupExecutor(journal)
            .ExecuteApproved(plan, () => Task.FromResult(fixture.Context())));
        Assert.Empty(journal.Runs());
        Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
        output.WriteLine($"Real generated objects: {count}; truncated approval rejected; no automatic next batch; all objects retained.");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task ObjectContractRunsWithoutNotificationBarriers(int count)
    {
        using var fixture = new Fixture(count: count) { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        using var journal = new CleanupJournal(fixture.Data);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new CleanupExecutor(journal).ExecuteApproved(plan, () => Task.FromResult(fixture.Context()));
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { count, result, elapsedMs = watch.ElapsedMilliseconds,
            fixture.Monitor.ApplicationGaps, fixture.Monitor.NativeGaps, observation = fixture.Monitor.Observe().Sequence }));
        Assert.Equal("Completed", result.State);
        Assert.Equal(count, result.Deleted);
        Assert.Equal(count, CleanupRecovery.Inspect(journal, plan.Id.ToString()).Count(e => e.State == "Deleted"));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("result-audit")]
    [InlineData("observation-audit")]
    [InlineData("expiry")]
    [InlineData("contract-change")]
    [InlineData("missing-observer")]
    public async Task ObjectContractKeepsRemainderOnControlOrAuditFailure(string failure)
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        if (failure == "expiry") plan = new(DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(3), plan.ContextFingerprint,
            plan.Items.ToList(), false, CleanupContract.ObjectReviewV1);
        using var journal = new CleanupJournal(fixture.Data);
        using var cancel = new CancellationTokenSource();
        var completed = false;
        journal.BeforeRecordWrite = state =>
        {
            if ((failure == "result-audit" && state == "Deleted") || (failure == "observation-audit" && state == "Observation"))
                throw new IOException("controlled audit failure");
        };
        var executor = new CleanupExecutor(journal)
        {
            TestCheckpoint = point =>
            {
                if (point != "after-result" || completed) return;
                completed = true;
                Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Snapshot.Files.Count == 2, TimeSpan.FromSeconds(10)));
                if (failure == "cancel") cancel.Cancel();
                if (failure == "expiry") Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        };
        var result = await executor.ExecuteApproved(plan, () =>
        {
            var context = fixture.Context();
            if (completed && failure == "contract-change") context = context with { Contract = CleanupContract.StrictActivity };
            if (completed && failure == "missing-observer") context = context with { Observations = [] };
            return Task.FromResult(context);
        }, cancel.Token);
        Assert.Equal(failure == "cancel" ? "Cancelled" : failure.EndsWith("audit") ? "Failed" : "NeedsPreview", result.State);
        Assert.Equal(1, result.Deleted);
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
        Assert.DoesNotContain(journal.Events(plan.Id.ToString()), e => e.State == "Started" && e.Path == fixture.Paths[1]);
        if (failure == "result-audit") Assert.DoesNotContain(journal.Events(plan.Id.ToString()), e => e.State == "Deleted");
    }

    [Fact]
    public async Task ObjectContractChecksLateNotificationsWhileHoldingTheCurrentObjectHandle()
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal)
        {
            TestCheckpoint = point =>
            {
                if (point != "before-delete") return;
                var sequence = fixture.Monitor.Observe().Sequence;
                File.WriteAllBytes(fixture.Unapproved + ".late", [7]);
                Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(10)));
            }
        };
        var result = await executor.ExecuteApproved(plan, () => Task.FromResult(fixture.Context()));
        Assert.Equal("NeedsPreview", result.State); Assert.Equal(0, result.Deleted);
        Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task ObjectContractRefreshesConsumerStateAfterRemainingObjectReview()
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        using var journal = new CleanupJournal(fixture.Data);
        var contexts = 0;
        var result = await new CleanupExecutor(journal).ExecuteApproved(plan, () =>
        {
            if (++contexts == 3) fixture.ConsumerActive = true;
            return Task.FromResult(fixture.Context());
        });
        Assert.Equal("NeedsPreview", result.State); Assert.Equal(0, result.Deleted);
        Assert.DoesNotContain(journal.Events(plan.Id.ToString()), e => e.State == "Started");
        Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task WaitingForOurSnapshotScanIsCancellableAndDoesNotDisableNotifications()
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        using var journal = new CleanupJournal(fixture.Data);
        using var releaseScan = new ManualResetEventSlim();
        using var scanEntered = new ManualResetEventSlim();
        using var deletionEntered = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        Task<CleanupResult>? execution = null;
        try
        {
            fixture.Monitor.BeforeScan = () => { scanEntered.Set(); releaseScan.Wait(TimeSpan.FromSeconds(10)); };
            fixture.Monitor.Rescan();
            Assert.True(scanEntered.Wait(TimeSpan.FromSeconds(10)));
            journal.BeforeRecordWrite = state => { if (state == "Started") deletionEntered.Set(); };
            execution = new CleanupExecutor(journal).ExecuteApproved(plan, () => Task.FromResult(fixture.Context()), cancel.Token);
            Assert.True(deletionEntered.Wait(TimeSpan.FromSeconds(10)));
            var sequence = fixture.Monitor.Observe().Sequence;
            File.WriteAllBytes(fixture.Unapproved + ".during-scan", [8]);
            Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(5)));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            cancel.Cancel();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("Cancelled", result.State); Assert.Equal(0, result.Deleted);
            Assert.All(fixture.Paths, path => Assert.True(File.Exists(path)));
            output.WriteLine($"Controlled scan wait cancellation: {watch.ElapsedMilliseconds} ms; raw notifications stayed enabled.");
        }
        finally
        {
            releaseScan.Set(); fixture.Monitor.BeforeScan = null;
            if (execution is not null) await execution;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObjectContractContinuesWithRealNotificationsAndDisclosesExternalParentMetadata(bool externalParentMetadata)
    {
        using var fixture = new Fixture { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        using var journal = new CleanupJournal(fixture.Data);
        var completed = 0;
        var executor = new CleanupExecutor(journal)
        {
            TestCheckpoint = point =>
            {
                if (point != "after-result") return;
                completed++;
                Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Snapshot.Files.Count == 3 - completed, TimeSpan.FromSeconds(10)));
                if (!externalParentMetadata || completed != 1) return;
                var sequence = fixture.Monitor.Observe().Sequence;
                var parent = Path.GetDirectoryName(fixture.Paths[0])!;
                File.SetAttributes(parent, File.GetAttributes(parent) | FileAttributes.Hidden);
                Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(10)));
            }
        };
        var result = await executor.ExecuteApproved(plan, () => Task.FromResult(fixture.Context()));
        Assert.Equal("Completed", result.State);
        Assert.Equal(3, result.Deleted);
        Assert.All(fixture.Paths, path => Assert.False(File.Exists(path)));
        Assert.NotNull(fixture.Monitor.LastActivity);
        Assert.True(fixture.Context().Activity.Scopes[0].Active); // Observation remains truthful after cleanup.
        var events = journal.Events(plan.Id.ToString());
        Assert.Contains(events, e => e.State == "Contract" && e.Reason.Contains("ObjectReviewV1"));
        Assert.Contains(events, e => e.State == "Observation" && e.Reason.Contains("undetermined"));
        Assert.Contains(events, e => e.State == "ObjectReview" && e.Reason.Contains("不证明"));
    }

    [Theory]
    [InlineData("external-write")]
    [InlineData("same-path-replacement")]
    [InlineData("transient-create-delete")]
    [InlineData("new-file")]
    [InlineData("rename")]
    [InlineData("unapproved-delete")]
    [InlineData("consumer")]
    [InlineData("application-overflow")]
    [InlineData("watcher-fault")]
    [InlineData("native-overflow")]
    [InlineData("event-history-overflow")]
    [InlineData("configuration")]
    [InlineData("file-attributes")]
    [InlineData("named-stream")]
    [InlineData("parent-stream")]
    public async Task ObjectContractStopsOnDangerousOrMissingEvidence(string change)
    {
        using var fixture = new Fixture(unapproved: true) { ObjectReview = true };
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        using var journal = new CleanupJournal(fixture.Data);
        var injected = false;
        var executor = new CleanupExecutor(journal)
        {
            TestCheckpoint = point =>
            {
                if (point != "after-result" || injected) return;
                injected = true;
                Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Snapshot.Files.Count == 3, TimeSpan.FromSeconds(10)));
                var sequence = fixture.Monitor.Observe().Sequence;
                switch (change)
                {
                    case "consumer": fixture.ConsumerActive = true; return;
                    case "configuration": fixture.ConfigurationVersion++; return;
                    case "application-overflow":
                        for (var i = 0; i < 2000; i++) fixture.Monitor.Rescan();
                        Assert.True(fixture.Monitor.ApplicationGaps > 0); return;
                    case "watcher-fault": fixture.Monitor.ReportWatcherError(new IOException("controlled")); return;
                    case "native-overflow": fixture.Monitor.ReportWatcherError(new InternalBufferOverflowException("controlled")); return;
                    case "external-write": File.AppendAllText(fixture.Paths[1], "external", System.Text.Encoding.UTF8); break;
                    case "file-attributes": File.SetAttributes(fixture.Paths[1], FileAttributes.Hidden); break;
                    case "named-stream": File.WriteAllBytes(fixture.Paths[1] + ":external", [6]); break;
                    case "parent-stream":
                        var parent = Path.GetDirectoryName(fixture.Paths[0])!;
                        File.WriteAllBytes(parent + ":external", [6]);
                        Directory.SetLastWriteTimeUtc(parent, DateTime.UtcNow); break;
                    case "same-path-replacement":
                        File.Delete(fixture.Paths[1]); File.WriteAllBytes(fixture.Paths[1], [9]);
                        File.SetLastWriteTimeUtc(fixture.Paths[1], DateTime.UtcNow.AddHours(-1)); break;
                    case "rename": File.Move(fixture.Unapproved, fixture.Unapproved + ".moved"); break;
                    case "unapproved-delete": File.Delete(fixture.Unapproved); break;
                    case "new-file": File.WriteAllBytes(fixture.Unapproved + ".new", [8]); break;
                    case "event-history-overflow":
                        // Real notifications, deliberately paced to overflow evidence retention, not FSW.
                        for (var i = 0; i < 2100; i++)
                        {
                            var before = fixture.Monitor.Observe().Sequence;
                            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(fixture.Paths[0])!, DateTime.UtcNow.AddSeconds(i));
                            Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > before, TimeSpan.FromSeconds(5)));
                            Thread.Sleep(2);
                        }
                        Assert.True(fixture.Monitor.Observe().DroppedThrough > sequence);
                        Assert.Equal(0, fixture.Monitor.NativeGaps);
                        Assert.Equal(0, fixture.Monitor.ApplicationGaps);
                        return;
                    default:
                        var transient = fixture.Unapproved + ".transient";
                        File.WriteAllBytes(transient, [8]); File.Delete(transient); break;
                }
                Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Observe().Sequence > sequence, TimeSpan.FromSeconds(10)));
            }
        };
        var result = await executor.ExecuteApproved(plan, () => Task.FromResult(fixture.Context()));
        Assert.True(injected);
        Assert.Equal("NeedsPreview", result.State);
        Assert.Equal(1, result.Deleted);
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
        Assert.DoesNotContain(journal.Events(plan.Id.ToString()), e => e.State == "Started" && e.Path == fixture.Paths[1]);
    }

    [Fact]
    public async Task RealWatcherCurrentlyStopsAfterItsOwnFirstDeleteAndKeepsRemainingObjects()
    {
        using var fixture = new Fixture();
        await fixture.Ready();
        Assert.Null(fixture.Monitor.LastActivity);
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        Assert.True(plan.EligibleCount == 3, string.Join("; ", plan.Items.Select(i => i.Reason)));
        using var journal = new CleanupJournal(fixture.Data);
        var completed = 0;
        var executor = new CleanupExecutor(journal)
        {
            TestCheckpoint = point =>
            {
                if (point == "after-result")
                {
                    completed++;
                    Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Snapshot.Files.Count == 3 - completed, TimeSpan.FromSeconds(10)));
                }
            }
        };
        var result = await executor.ExecuteApproved(plan, () => Task.FromResult(fixture.Context()));
        // Known R1-003 failure: event source cannot safely be attributed with the current protocol.
        Assert.Equal("NeedsPreview", result.State);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(2, fixture.Monitor.Snapshot.Files.Count);
        Assert.NotNull(fixture.Monitor.LastActivity);
        Assert.True(fixture.Monitor.HasContinuousObservation);
        Assert.Equal(1, journal.Events(plan.Id.ToString()).Count(e => e.State == "Deleted"));
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
    }

    [Theory]
    [InlineData("external-write")]
    [InlineData("same-path-replacement")]
    [InlineData("transient-create-delete")]
    [InlineData("consumer")]
    [InlineData("application-overflow")]
    [InlineData("watcher-fault")]
    public async Task ExternalOrUnknownChangeBeforeSecondObjectKeepsLaterObjects(string change)
    {
        using var fixture = new Fixture();
        await fixture.Ready();
        var plan = CleanupPlanner.Preview(fixture.Paths, fixture.Context());
        using var journal = new CleanupJournal(fixture.Data);
        var injected = false;
        var executor = new CleanupExecutor(journal)
        {
            TestCheckpoint = point =>
            {
                if (point != "after-result" || injected) return;
                injected = true;
                Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.Snapshot.Files.Count == 2, TimeSpan.FromSeconds(10)));
                var activity = fixture.Monitor.LastActivity;
                if (change == "consumer") fixture.ConsumerActive = true;
                else if (change == "application-overflow")
                {
                    for (var i = 0; i < 1000; i++) fixture.Monitor.Rescan();
                    Assert.True(fixture.Monitor.ApplicationGaps > 0);
                }
                else if (change == "watcher-fault") fixture.Monitor.ReportWatcherError(new IOException("controlled watcher fault"));
                else
                {
                    if (change == "external-write") File.AppendAllText(fixture.Paths[1], "external", System.Text.Encoding.UTF8);
                    else if (change == "same-path-replacement")
                    {
                        File.Delete(fixture.Paths[1]);
                        File.WriteAllBytes(fixture.Paths[1], [8, 9, 10]);
                        File.SetLastWriteTimeUtc(fixture.Paths[1], DateTime.UtcNow.AddHours(-1));
                    }
                    else
                    {
                        var transient = Path.Combine(fixture.Root, "transient");
                        File.WriteAllBytes(transient, [1]);
                        File.Delete(transient);
                    }
                    Assert.True(SpinWait.SpinUntil(() => fixture.Monitor.LastActivity != activity, TimeSpan.FromSeconds(10)));
                }
            }
        };
        var result = await executor.ExecuteApproved(plan, () => Task.FromResult(fixture.Context()));
        Assert.True(injected);
        Assert.Equal("NeedsPreview", result.State);
        Assert.Equal(1, result.Deleted);
        Assert.All(fixture.Paths.Skip(1), path => Assert.True(File.Exists(path)));
        Assert.DoesNotContain(journal.Events(plan.Id.ToString()), e => e.Path == fixture.Paths[1] || e.Path == fixture.Paths[2]);
        if (change == "same-path-replacement") Assert.Equal(new byte[] { 8, 9, 10 }, File.ReadAllBytes(fixture.Paths[1]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ExperimentSandbox box = new(Path.Combine(Path.GetTempPath(), "cclean-workflow-" + Guid.NewGuid().ToString("N")));
        public string Root { get; }
        public string Data { get; }
        public string[] Paths { get; }
        public DirectoryMonitor Monitor { get; }
        public bool ConsumerActive { get; set; }
        public bool ObjectReview { get; init; }
        public long ConfigurationVersion { get; set; } = 1;
        public string Unapproved { get; }
        public Fixture(bool unapproved = false, int count = 3)
        {
            Root = Path.Combine(box.RunDirectory, "cache");
            Data = Path.Combine(box.RunDirectory, "audit");
            var content = Path.Combine(Root, "_cacache", "content-v2");
            Directory.CreateDirectory(content);
            Paths = new string[count];
            Unapproved = Path.Combine(Root, "retained.bin");
            if (unapproved) File.WriteAllBytes(Unapproved, [7]);
            for (var i = 0; i < count; i++)
            {
                var path = Path.Combine(content, $"sample-{i}");
                File.WriteAllBytes(path, [1, 2, 3]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            }
            // Seed values remain untrusted until the watcher completes its own live scan.
            var seed = DirectoryScanner.Scan(Root, absolutePaths: true);
            foreach (var file in seed.Files.Values)
            {
                var name = Path.GetFileName(file.Path.AsSpan());
                if (name.StartsWith("sample-", StringComparison.Ordinal) &&
                    int.TryParse(name[7..], out var index) && index >= 0 && index < Paths.Length)
                    Paths[index] = file.Path;
            }
            if (Paths.Any(string.IsNullOrEmpty)) throw new IOException("Synthetic fixture scan missed a sample.");
            Monitor = new(Root, absolutePaths: true, initialFiles: seed.Files);
        }
        public async Task Ready(int timeoutSeconds = 10)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (Monitor.Revision == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(Monitor.HasContinuousObservation);
        }
        // Same dynamic activity decision as WorkspaceViewModel.Activity(), with real watcher state.
        public CleanupContext Context()
        {
            var active = ConsumerActive || Monitor.LastActivity > DateTimeOffset.UtcNow.AddMinutes(-30);
            var context = new CleanupContext(ConfigurationVersion, [new(Root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)],
                new ActivitySnapshot(1, [new(Root, active, Monitor.HasContinuousObservation,
                    active ? "近期目录发生变化，保持活动保护" : "仅观察状态；结束不授予删除资格")]), [Data]);
            return !ObjectReview ? context : context with
            {
                Contract = CleanupContract.ObjectReviewV1,
                IndependentActivity = new(1, [new(Root, ConsumerActive, Monitor.HasContinuousObservation,
                    ConsumerActive ? "known consumer" : "observed")]),
                Observations = [new(Monitor, Monitor.Observe())]
            };
        }
        public void Dispose() { Monitor.Dispose(); box.Dispose(); }
    }
}

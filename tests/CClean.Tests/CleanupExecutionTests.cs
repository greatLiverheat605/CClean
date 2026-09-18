using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Sessions;

namespace CClean.Tests;

[Trait("Category", "CleanupExecution")]
[Trait("Category", "MvpP0")]
public class CleanupExecutionTests
{
    [Fact]
    public async Task FailedResultCommitPreservesNextFileAndDoesNotInventCommittedDeletion()
    {
        using var fixture = new Fixture(); var a = fixture.File("a"); var b = fixture.File("b");
        var plan = CleanupPlanner.Preview([a, b], fixture.Context);
        using (var journal = new CleanupJournal(fixture.Data))
        {
            journal.BeforeRecordWrite = state => { if (state == "Deleted") throw new IOException("controlled result commit failure"); };
            var result = await new CleanupExecutor(journal).ExecuteApproved(plan, () => Task.FromResult(fixture.Context));
            Assert.Equal("Failed", result.State); Assert.False(File.Exists(a)); Assert.True(File.Exists(b));
            Assert.DoesNotContain(journal.Events(plan.Id.ToString()), e => e.State == "Deleted" || e.Path == b);
        }
        using var reopened = new CleanupJournal(fixture.Data);
        Assert.All(CleanupRecovery.Inspect(reopened, plan.Id.ToString()), e => Assert.Equal("RecoveryObservation", e.State));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task OnlyApprovedUnchangedUnlockedObjectsAreDeletedAndPlanCannotReplay()
    {
        using var fixture = new Fixture();
        var normal = fixture.File("normal"); var changed = fixture.File("changed"); var locked = fixture.File("locked");
        var outside = fixture.File("not-selected");
        var plan = CleanupPlanner.Preview([normal, changed, locked], fixture.Context);
        Assert.Equal(3, plan.EligibleCount);
        File.AppendAllText(changed, "changed", System.Text.Encoding.UTF8);
        using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var journal = new CleanupJournal(fixture.Data);
        var executor = new CleanupExecutor(journal);
        var result = await executor.ExecuteApproved(plan, () => Task.FromResult(fixture.Context));
        Assert.Equal(1, result.Deleted); Assert.Equal(2, result.Skipped); Assert.Equal("Partial", result.State);
        Assert.False(File.Exists(normal)); Assert.True(File.Exists(changed)); Assert.True(File.Exists(locked)); Assert.True(File.Exists(outside));
        Assert.Equal(6, journal.Events(plan.Id.ToString()).Count(e => e.Path.Length > 0));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => executor.ExecuteApproved(plan, () => Task.FromResult(fixture.Context)));
    }
    [Fact]
    public async Task AuditBudgetFailureStopsBeforeTheNextFile()
    {
        using var fixture = new Fixture(); var a = fixture.File("a"); var b = fixture.File("b");
        var plan = CleanupPlanner.Preview([a, b], fixture.Context);
        using var journal = new CleanupJournal(fixture.Data); var calls = 0;
        var result = await new CleanupExecutor(journal).ExecuteApproved(plan, () =>
        {
            if (++calls == 3) { using var log = File.Create(Path.Combine(fixture.Data, "pressure.log")); log.SetLength(40L * 1024 * 1024); }
            return Task.FromResult(fixture.Context);
        });
        Assert.Equal("Failed", result.State); Assert.False(File.Exists(a)); Assert.True(File.Exists(b));
    }
    [Fact]
    public async Task ActivityChangeAfterApprovalStopsAllObjects()
    {
        using var fixture = new Fixture(); var a = fixture.File("a");
        var plan = CleanupPlanner.Preview([a], fixture.Context); var calls = 0;
        using var journal = new CleanupJournal(fixture.Data);
        var result = await new CleanupExecutor(journal).ExecuteApproved(plan, () => Task.FromResult(++calls == 1 ? fixture.Context : fixture.Context with { ConfigurationVersion = 2 }));
        Assert.Equal("NeedsPreview", result.State); Assert.True(File.Exists(a));
    }
    [Fact]
    public async Task CancellationStopsBeforeAnUnstartedItemAndRestartDoesNotResume()
    {
        using var fixture = new Fixture(); var a = fixture.File("a");
        var plan = CleanupPlanner.Preview([a], fixture.Context); var calls = 0; using var cancel = new CancellationTokenSource();
        using (var journal = new CleanupJournal(fixture.Data))
        {
            var result = await new CleanupExecutor(journal).ExecuteApproved(plan, () =>
            {
                if (++calls == 2) cancel.Cancel(); return Task.FromResult(fixture.Context);
            }, cancel.Token);
            Assert.Equal("Cancelled", result.State); Assert.True(File.Exists(a));
            var interrupted = CleanupPlanner.Preview([a], fixture.Context); journal.Begin(interrupted);
        }
        using var recovered = new CleanupJournal(fixture.Data);
        Assert.Contains(recovered.Runs(), r => r.State == "Interrupted"); Assert.True(File.Exists(a));
    }
    private sealed class Fixture : IDisposable
    {
        private readonly ExperimentSandbox box = new(Path.Combine(Path.GetTempPath(), "cclean-execute-" + Guid.NewGuid().ToString("N")));
        public string Data { get; }
        public string Root { get; }
        public CleanupContext Context { get; }
        public Fixture()
        {
            Root = Path.Combine(box.RunDirectory, "exclusive"); Directory.CreateDirectory(Path.Combine(Root, "_cacache", "content-v2"));
            Data = Path.Combine(box.RunDirectory, "audit"); Directory.CreateDirectory(Data);
            Context = new(1, [new(Root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)], new ActivitySnapshot(1, [new(Root, false, true, "synthetic")]), [Data]);
        }
        public string File(string name)
        {
            var path = Path.Combine(Root, "_cacache", "content-v2", name);
            System.IO.File.WriteAllText(path, "synthetic", System.Text.Encoding.UTF8); System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1)); return path;
        }
        public void Dispose() => box.Dispose();
    }
}

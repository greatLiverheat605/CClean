using CClean.Core.Cleanup;
using CClean.Core.Experiments;

namespace CClean.Tests;

[Trait("Category", "CleanupRecovery")]
[Trait("Category", "Safety")]
[Trait("Category", "MvpP0")]
public class CleanupRecoveryTests
{
    [Fact]
    public async Task AdsAddedAfterPreviewAndReplacedRootAreNeverDeleted()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-recovery-" + Guid.NewGuid().ToString("N")));
        var root = Path.Combine(box.RunDirectory, "cache"); var directory = Path.Combine(root, "_cacache", "content-v2"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "abcdef"); File.WriteAllText(path, "protected", System.Text.Encoding.UTF8); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        var data = Path.Combine(box.RunDirectory, "audit");
        var context = new CleanupContext(1, [new(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)], new(1, [new(root, false, true, "synthetic")]), [data]);
        var adsPlan = CleanupPlanner.Preview([path], context);
        File.WriteAllText(path + ":secret", "must survive", System.Text.Encoding.UTF8);
        using var journal = new CleanupJournal(data); var executor = new CleanupExecutor(journal);
        var ads = await executor.ExecuteApproved(adsPlan, () => Task.FromResult(context));
        Assert.Equal(0, ads.Deleted); Assert.Equal("must survive", File.ReadAllText(path + ":secret"));
        var normal = Path.Combine(directory, "normal"); File.WriteAllText(normal, "same identity", System.Text.Encoding.UTF8); File.SetLastWriteTimeUtc(normal, DateTime.UtcNow.AddHours(-1));
        var rootPlan = CleanupPlanner.Preview([normal], context);
        var retired = root + "-retired"; Directory.Move(root, retired); Directory.CreateDirectory(directory);
        File.Move(Path.Combine(retired, "_cacache", "content-v2", "normal"), normal);
        var replaced = await executor.ExecuteApproved(rootPlan, () => Task.FromResult(context));
        Assert.Equal(0, replaced.Deleted); Assert.Equal("same identity", File.ReadAllText(normal));
    }
    [Fact]
    public async Task ConcurrentClickCannotAcquireSecondExecutionLease()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-concurrent-" + Guid.NewGuid().ToString("N")));
        var root = Path.Combine(box.RunDirectory, "cache"); var directory = Path.Combine(root, "_cacache", "content-v2"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "abcdef"); File.WriteAllText(path, "fixture", System.Text.Encoding.UTF8); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        var context = new CleanupContext(1, [new(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)], new(1, [new(root, false, true, "synthetic")]), []);
        var plan = CleanupPlanner.Preview([path], context); using var journal = new CleanupJournal(Path.Combine(box.RunDirectory, "audit")); var executor = new CleanupExecutor(journal);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = executor.ExecuteApproved(plan, async () => { entered.TrySetResult(); await release.Task; return context; });
        await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteApproved(plan, () => Task.FromResult(context)));
        release.SetResult(); Assert.Equal(1, (await first).Deleted);
    }
}

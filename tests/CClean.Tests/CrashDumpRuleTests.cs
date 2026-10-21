using CClean.Core.Cleanup;
using CClean.Core.Experiments;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "Safety")]
public class CrashDumpRuleTests
{
    [Fact]
    public async Task OnlyOldClosedDumpsAreDeletedAndNewestRecentSourceAndNestedFilesSurvive()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-dumps-" + Guid.NewGuid().ToString("N")));
        var root = Path.Combine(box.RunDirectory, "wsl-crashes"); Directory.CreateDirectory(root);
        string Write(string name, int hours)
        {
            var path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic diagnostic", System.Text.Encoding.UTF8);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hours)); return path;
        }
        var old = Write("wsl-crash-old.dmp", 96);
        var recent = Write("wsl-crash-recent.dmp", 12);
        var newest = Write("wsl-crash-newest.dmp", 1);
        var source = Write("project.cs", 96);
        var nested = Write("nested/wsl-crash-old.dmp", 96);
        var locked = Write("wsl-crash-locked.dmp", 96);
        using var writer = new FileStream(locked, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var context = CrashDumpRule.Context(root, []);
        var plan = CleanupPlanner.Preview([old, recent, newest, source, nested, locked], context);
        Assert.Equal(2, plan.EligibleCount);
        using var journal = new CleanupJournal(Path.Combine(box.RunDirectory, "data"));
        var result = await new CleanupExecutor(journal).ExecuteApproved(plan, () => Task.FromResult(CrashDumpRule.Context(root, [])));
        Assert.Equal(1, result.Deleted);
        Assert.Equal("Partial", result.State);
        Assert.False(File.Exists(old));
        Assert.All(new[] { recent, newest, source, nested, locked }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void NewestIsPreservedEvenWhenOldAndNewArrivalInvalidatesPreview()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-dump-retain-" + Guid.NewGuid().ToString("N")));
        var root = Path.Combine(box.RunDirectory, "wsl-crashes"); Directory.CreateDirectory(root);
        var old = Path.Combine(root, "wsl-crash-1.dmp");
        var latest = Path.Combine(root, "wsl-crash-2.dmp");
        File.WriteAllText(old, "old", System.Text.Encoding.UTF8); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-5));
        File.WriteAllText(latest, "newer", System.Text.Encoding.UTF8); File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddDays(-3));
        var plan = CleanupPlanner.Preview([old, latest], CrashDumpRule.Context(root, []));
        Assert.True(plan.Items[0].Decision.Eligible);
        Assert.False(plan.Items[1].Decision.Eligible);
        File.WriteAllText(Path.Combine(root, "wsl-crash-3.dmp"), "newest", System.Text.Encoding.UTF8);
        Assert.False(plan.IsCurrent(CrashDumpRule.Context(root, []), DateTimeOffset.UtcNow));
        Assert.Throws<IOException>(() => CrashDumpRule.Context(box.RunDirectory, []));
    }
}

using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Sessions;

namespace CClean.Tests;

[Trait("Category", "CleanupPlan")]
[Trait("Category", "MvpP0")]
public class CleanupPlanTests
{
    [Fact]
    public void PreviewIsBoundedCurrentAndNeverDeletesOrAddsLaterFiles()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-plan-" + Guid.NewGuid().ToString("N")));
        var root = Path.Combine(box.RunDirectory, "exclusive"); var directory = Path.Combine(root, "_cacache", "index-v5"); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "abcdef"); File.WriteAllText(file, "fixture", System.Text.Encoding.UTF8); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        var context = new CleanupContext(1, [new(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)], new ActivitySnapshot(1, [new(root, false, true, "observed")]), []);
        var plan = CleanupPlanner.Preview([file, file, Path.Combine(directory, "missing")], context);
        Assert.Equal(2, plan.Items.Count); Assert.Equal(1, plan.EligibleCount); Assert.NotNull(plan.Items[0].RootIdentity);
        Assert.True(File.Exists(file)); Assert.True(plan.IsCurrent(context, plan.Created.AddSeconds(1)));
        Assert.False(plan.IsCurrent(context, plan.Expires));
        Assert.False(plan.IsCurrent(context with { ConfigurationVersion = 2 }, plan.Created));
        Assert.False(plan.IsCurrent(context with { Activity = new(1, [new(root, true, true, "active")]) }, plan.Created));
        Assert.False(plan.IsCurrent(context with { ProtectedRoots = [root] }, plan.Created));
        File.WriteAllText(Path.Combine(directory, "new"), "new fixture", System.Text.Encoding.UTF8);
        Assert.Equal(2, plan.Items.Count);
        var manual = CleanupPlanner.Preview([file], context with { Rules = [] });
        Assert.Equal(0, manual.EligibleCount);
        var all = CleanupPlanner.Preview(Directory.EnumerateFiles(directory), context with { Rules = [] });
        Assert.Equal(0, all.EligibleCount);
    }
}

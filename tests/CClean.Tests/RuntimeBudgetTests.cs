using CClean.Core.Storage;

namespace CClean.Tests;

[Trait("Category", "RuntimeBudget")]
[Trait("Category", "M0")]
[Trait("Category", "Foundation")]
public class RuntimeBudgetTests
{
    [Fact]
    public void AuditFailureAndPressurePreventDeletion()
    {
        Assert.False(RuntimeBudget.Evaluate(new(0, 0, 0), long.MaxValue, false).Delete);
        var low = RuntimeBudget.Evaluate(new(0, 0, 0), 4096, true);
        Assert.False(low.Collect);
        Assert.False(low.Delete);
        Assert.False(low.Vacuum);
        foreach (var usage in new[] { new RuntimeUsage(RuntimeBudget.DatabaseLimit, 0, 0), new(0, RuntimeBudget.LogLimit, 0), new(0, 0, RuntimeBudget.TemporaryLimit) })
            Assert.False(RuntimeBudget.Evaluate(usage, long.MaxValue, true).Collect);
        Assert.False(RuntimeBudget.Evaluate(new(0, 0, 0), long.MaxValue, true, long.MaxValue).Collect);
        Assert.True(RuntimeBudget.Evaluate(new(0, 0, 0), long.MaxValue, true, 4096).Collect);
    }
}

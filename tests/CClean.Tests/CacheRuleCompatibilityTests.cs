using CClean.Core.Cleanup;
using CClean.Core.Files;

namespace CClean.Tests;

[Trait("Category", "CacheCompatibility")]
[Trait("Category", "M0")]
[Trait("Category", "Safety")]
public class CacheRuleCompatibilityTests
{
    [Fact]
    public void OnlyEnabledExclusiveInactiveSupportedContentIsEligible()
    {
        var context = new CacheContext("C:/synthetic/cache", "11.19.0", true, true, true, false, []);
        var file = new FileSnapshot(Path.GetFullPath("C:/synthetic/cache/_cacache/content-v2/sha512/a"), "fixture", 1, 4096, 1, FileAttributes.Normal, 1, 1, true, false, null);
        var rule = CacheRule.NpmContent;
        Assert.Null(rule.Exclude(context, file));
        Assert.NotNull(rule.Exclude(context with { Enabled = false }, file));
        Assert.NotNull(rule.Exclude(context with { ToolVersion = "unknown" }, file));
        Assert.NotNull(rule.Exclude(context with { ExplicitExclusiveRoot = false }, file));
        Assert.NotNull(rule.Exclude(context with { ObservationComplete = false }, file));
        Assert.NotNull(rule.Exclude(context with { ActiveConsumer = true }, file));
        Assert.NotNull(rule.Exclude(context with { ProtectedRoots = ["C:/synthetic"] }, file));
        Assert.NotNull(rule.Exclude(context, file with { Exclusion = "Named stream" }));
        Assert.NotNull(rule.Exclude(context, file with { Path = "C:/synthetic/cache-other/content.bin" }));
    }
}

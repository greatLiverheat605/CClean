using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Sessions;

namespace CClean.Tests;

[Trait("Category", "Protection")]
[Trait("Category", "MvpP0")]
public class ProtectionPolicyTests
{
    [Fact]
    public void RealFixtureRequiresExplicitVerifiedRuleAndCompleteInactiveObservation()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-policy-" + Guid.NewGuid().ToString("N")));
        var root = Path.Combine(box.RunDirectory, "exclusive");
        var directory = Path.Combine(root, "_cacache", "content-v2"); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "abcdef"); File.WriteAllText(file, "synthetic", System.Text.Encoding.UTF8);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        var snapshot = WindowsFileMetadata.Read(file);
        var data = Path.Combine(box.RunDirectory, "data"); Directory.CreateDirectory(data);
        var catalog = new RuleCatalog(data);
        var activity = new ActivitySnapshot(1, [new(root, false, true, "synthetic")]);
        ProtectionDecision Classify(FileSnapshot f, ActivitySnapshot? a = null) => ProtectionPolicy.Classify(f, catalog.Scopes, a ?? activity, [], DateTimeOffset.UtcNow);
        Assert.False(Classify(snapshot).Eligible);
        catalog.ConfigureNpm(root, "11.19.0", false, true);
        Assert.False(Classify(snapshot).Eligible);
        catalog.ConfigureNpm(root, "11.19.0", true, true);
        Assert.True(Classify(snapshot).Eligible);
        Assert.Equal(2, new RuleCatalog(data).ConfigurationVersion);
        Assert.False(Classify(snapshot, new(2, [new(root, true, true, "active")])).Eligible);
        Assert.False(Classify(snapshot, new(2, [new(root, false, false, "unknown")])).Eligible);
        Assert.False(Classify(snapshot, new(2, [])).Eligible);
        Assert.False(Classify(snapshot with { HasNamedStreams = true }).Eligible);
        Assert.False(Classify(snapshot with { Identity = null }).Eligible);
        Assert.False(Classify(snapshot with { LinkCount = 2 }).Eligible);
        Assert.False(Classify(snapshot with { ModifiedTime = DateTime.UtcNow.ToFileTimeUtc() }).Eligible);
        Assert.False(Classify(snapshot with { Path = file + ".cs" }).Eligible);
        Assert.False(Classify(snapshot with { Path = Path.Combine(directory, ".codex", "abcdef") }).Eligible);
        Assert.False(Classify(snapshot with { Path = Path.Combine(root + "-other", "_cacache", "content-v2", "abcdef") }).Eligible);
        Assert.False(ProtectionPolicy.Classify(snapshot, catalog.Scopes, activity, [root], DateTimeOffset.UtcNow).Eligible);
        Assert.False(ProtectionPolicy.Classify(snapshot, [catalog.Scopes[0] with { RuleVersion = 999 }], activity, [], DateTimeOffset.UtcNow).Eligible);
        Assert.Throws<ArgumentException>(() => catalog.ConfigureNpm(root, "unknown", true, true));
        Assert.Throws<ArgumentException>(() => catalog.ConfigureNpm(root, "11.19.0", true, false));
        Assert.True(File.Exists(file));
    }
}

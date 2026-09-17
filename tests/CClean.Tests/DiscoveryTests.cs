using CClean.Core.Experiments;
using CClean.Core.Monitoring;
using CClean.Core.Projects;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "Discovery")]
[Trait("Category", "Foundation")]
public class DiscoveryTests
{
    [Fact]
    public void DiscoveryIsBoundedResumableAndDoesNotRegisterAnything()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-discovery-" + Guid.NewGuid().ToString("N")));
        for (var i = 0; i < 15; i++) box.CreateFixture($"{i}.bin", 10);
        using var discovery = new DirectoryDiscovery(box.RunDirectory);
        var first = discovery.Next(default, 5);
        Assert.Equal(5, first.Inspected);
        Assert.True(first.HasMore);
        Assert.Throws<OperationCanceledException>(() => discovery.Next(new CancellationToken(true)));
        var last = discovery.Next(default);
        Assert.False(last.HasMore);
        Assert.Equal(15, last.Candidates.Single().Files);
        Assert.Equal(150, last.Candidates.Single().KnownBytes);
    }

    [Fact]
    public void RegistryPersistsAndRejectsOverlap()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-registry-" + Guid.NewGuid().ToString("N")));
        var data = Path.Combine(box.RunDirectory, "data");
        var registry = new ProjectRegistry(data);
        var root = Path.Combine(box.RunDirectory, "project"); Directory.CreateDirectory(root);
        var project = registry.Register("合成项目", root, "ZCode");
        Assert.Throws<InvalidOperationException>(() => registry.Register("重复", root, "Codex"));
        Assert.Throws<InvalidOperationException>(() => registry.Register("自身索引", box.RunDirectory, "Codex"));
        registry.SetPaused(project.Id, true);
        Assert.True(new ProjectRegistry(data).Projects.Single().Paused);
        Assert.False(ProjectRegistry.Overlaps("C:/a", "C:/ab"));
    }
}

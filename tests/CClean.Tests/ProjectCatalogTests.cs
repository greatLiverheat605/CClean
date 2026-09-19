using CClean.Core.Projects;
using CClean.Core.Sessions;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
public class ProjectCatalogTests
{
    [Fact]
    public void ModulesDeduplicatePathsAndKeepDifferentVersionFolders()
    {
        using var box = new CClean.Core.Experiments.ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-catalog-" + Guid.NewGuid().ToString("N")));
        var root = box.RunDirectory;
        var a = Path.Combine(root, "a", "project"); var b = Path.Combine(root, "b", "project");
        Directory.CreateDirectory(Path.Combine(a, "child")); Directory.CreateDirectory(b);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        DiscoveredSession Session(string key, string path) => new(key, "Codex", path, now, now, "synthetic.jsonl");
        var result = ProjectCatalog.Build([new(id, "Registered", a, "Codex")],
            [Session("1", a.ToUpperInvariant()), Session("2", b), Session("3", Path.Combine(a, "child")),
             Session("4", Environment.SystemDirectory), Session("5", "relative"), Session("6", "bad\0path"), Session("7", Path.GetPathRoot(a)!)]);
        Assert.Equal(3, result.Count);
        var registered = Assert.Single(result, p => p.Name == "Registered");
        Assert.Equal(2, registered.SessionCount);
        Assert.Equal(id, Assert.Single(result, p => p.Name == "child").RegistrationId);
        Assert.Null(Assert.Single(result, p => p.Root == b).RegistrationId);
        Assert.DoesNotContain(result, p => p.Root == Environment.SystemDirectory);
    }
}

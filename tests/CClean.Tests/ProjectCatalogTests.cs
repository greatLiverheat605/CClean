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
        Assert.Equal(2, result.Count);
        var registered = Assert.Single(result, p => p.Name == "Registered");
        Assert.Equal(2, registered.SessionCount);
        Assert.Equal(id, registered.RegistrationId);
        Assert.DoesNotContain(result, p => p.Name == "child");
        Assert.Null(Assert.Single(result, p => p.Root == b).RegistrationId);
        Assert.DoesNotContain(result, p => p.Root == Environment.SystemDirectory);
    }

    [Fact]
    public void CodeRootKeepsPartialMonitoringExplicitAndDoesNotGroupUnderHomeWorkspace()
    {
        using var box = new CClean.Core.Experiments.ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-catalog-scope-" + Guid.NewGuid().ToString("N")));
        var repo = Path.Combine(box.RunDirectory, "repo");
        var source = Path.Combine(repo, "src");
        var version = Path.Combine(box.RunDirectory, "repo-v1");
        Directory.CreateDirectory(source); Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(repo, "package.json"), "{}", System.Text.Encoding.UTF8);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        DiscoveredSession Session(string key, string path) => new(key, "Codex", path, now, now, "synthetic.jsonl");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sessions = new[] { Session("home", profile), Session("root", repo), Session("child", source) };
        var result = ProjectCatalog.Build([new(id, "Source", source, "Codex")], sessions, [repo.ToUpperInvariant(), version]);
        var code = Assert.Single(result, p => p.Root.Equals(repo, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProjectModuleKind.CodeProject, code.Kind);
        Assert.True(code.HasMonitoredDescendants);
        Assert.Null(code.RegistrationId);
        Assert.Contains("部分子目录", code.Status);
        Assert.Equal(2, code.SessionCount);
        var monitored = Assert.Single(result, p => p.RegistrationId == id);
        Assert.Equal(repo.ToUpperInvariant(), monitored.ParentRoot, ignoreCase: true);
        var home = Assert.Single(result, p => p.Root == profile);
        Assert.Equal(ProjectModuleKind.ToolWorkspace, home.Kind);
        Assert.Equal(1, home.SessionCount);
        Assert.Single(sessions, s => ProjectCatalog.IncludesSession(home, s));
        Assert.Equal(2, sessions.Count(s => ProjectCatalog.IncludesSession(code, s)));
        Assert.False(ProjectCatalog.IncludesSession(home, Session("relative", "relative")));
        Assert.False(ProjectCatalog.IncludesSession(home, Session("invalid", "bad\0path")));
        Assert.False(home.HasMonitoredDescendants);
        Assert.DoesNotContain(result, p => p.ParentRoot == profile);
        Assert.Contains(result, p => p.Root == version && p.RegistrationId is null);
    }

    [Fact]
    public void ExplicitNestedVersionRemainsSeparateFromItsParentProject()
    {
        using var box = new CClean.Core.Experiments.ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-catalog-version-" + Guid.NewGuid().ToString("N")));
        var repo = Path.Combine(box.RunDirectory, "repo");
        var version = Path.Combine(repo, "versions", "repo-v1");
        Directory.CreateDirectory(version);
        var result = ProjectCatalog.Build([], [], [repo, version]);
        Assert.Equal(2, result.Count);
        Assert.Equal(repo, Assert.Single(result, p => p.Root == version).ParentRoot);
    }
}

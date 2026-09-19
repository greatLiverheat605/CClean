using CClean.Core.Experiments;
using CClean.Core.Projects;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
public class ProjectDiscoveryTests
{
    private static string Fixture(ExperimentSandbox box, string relative)
    {
        var path = Path.Combine(box.RunDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", System.Text.Encoding.UTF8);
        return Path.GetDirectoryName(path)!;
    }

    [Fact]
    public void DiscoveryResumesAndKeepsSiblingVersionsWithoutDependencyProjects()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-project-discovery-" + Guid.NewGuid().ToString("N")));
        var a = Fixture(box, "app-v1/package.json");
        var b = Fixture(box, "app-v2/CClean.slnx");
        Fixture(box, "app-v1/src/child/child.csproj");
        Fixture(box, "app-v1/node_modules/library/package.json");
        Fixture(box, "container/node_modules/library/package.json");
        Fixture(box, "container/.venv/library/pyproject.toml");
        using var discovery = new ProjectDiscovery(box.RunDirectory);
        var first = discovery.Next(default, maxObjects: 1);
        Assert.True(first.HasMore);
        Assert.Throws<OperationCanceledException>(() => discovery.Next(new CancellationToken(true)));
        var final = discovery.Next(default);
        Assert.False(final.HasMore);
        Assert.False(final.Limited);
        Assert.Equal(2, final.Roots.Count);
        Assert.Contains(a, final.Roots);
        Assert.Contains(b, final.Roots);
        var modules = ProjectCatalog.Build([], [], final.Roots);
        Assert.All(modules, p => { Assert.Equal(ProjectModuleKind.CodeProject, p.Kind); Assert.Null(p.RegistrationId); });
        Assert.True(File.Exists(Path.Combine(a, "package.json")));
    }

    [Fact]
    public void DiscoveryAndCatalogExcludeSystemsInstallationsLinksAndDependencyRoots()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-project-exclusion-" + Guid.NewGuid().ToString("N")));
        var project = Fixture(box, "scan/project/package.json");
        var system = Fixture(box, "scan/protected/child/package.json");
        var outside = Fixture(box, "outside/package.json");
        var dependency = Fixture(box, "scan/node_modules/library/package.json");
        var systemRoot = Path.GetDirectoryName(system)!;
        File.SetAttributes(systemRoot, File.GetAttributes(systemRoot) | FileAttributes.System);
        var linked = Path.Combine(box.RunDirectory, "scan", "linked");
        Directory.CreateSymbolicLink(linked, outside);
        try
        {
            using var discovery = new ProjectDiscovery(Path.Combine(box.RunDirectory, "scan"));
            var result = discovery.Next(default);
            Assert.Equal(project, Assert.Single(result.Roots));
            Assert.True(result.Skipped >= 3);
            Assert.Throws<ExperimentRefusedException>(() => new ProjectDiscovery(system));
            Assert.Throws<ExperimentRefusedException>(() => new ProjectDiscovery(dependency));
            Assert.Throws<ExperimentRefusedException>(() => new ProjectDiscovery(Environment.SystemDirectory));
            var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            Assert.Throws<ExperimentRefusedException>(() => new ProjectDiscovery(install));
            Assert.Empty(ProjectCatalog.Build([], [], [system, dependency, linked, install, Environment.SystemDirectory]));
            Assert.True(File.Exists(Path.Combine(system, "package.json")));
        }
        finally { File.SetAttributes(systemRoot, FileAttributes.Directory); }
    }
}

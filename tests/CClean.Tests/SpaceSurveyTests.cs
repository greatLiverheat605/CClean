using CClean.Core.Experiments;
using CClean.Core.Monitoring;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
public class SpaceSurveyTests
{
    private static void Fixture(ExperimentSandbox box, string relative, int length)
    {
        var path = Path.Combine(box.RunDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    [Fact]
    public void BatchesResumeCountDescendantsAndIgnoreDependencyManifests()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-survey-" + Guid.NewGuid().ToString("N")));
        Fixture(box, "app-v1/package.json", 10);
        Fixture(box, "app-v1/src/main.txt", 20);
        Fixture(box, "app-v1/node_modules/library/package.json", 30);
        Fixture(box, "app-v2/pyproject.toml", 40);
        using var survey = new SpaceSurvey(box.RunDirectory);
        var first = survey.Next(default, 1);
        Assert.True(first.HasMore);
        Assert.Throws<OperationCanceledException>(() => survey.Next(new CancellationToken(true)));
        var final = survey.Next(default);
        Assert.False(final.HasMore);
        Assert.Equal(100, final.Bytes);
        Assert.Equal(4, final.Files);
        var projects = final.Folders.Where(f => f.Project).ToArray();
        Assert.Equal(2, projects.Length);
        Assert.Equal(projects[0].VersionFamily, projects[1].VersionFamily);
        Assert.Equal(60, projects.Single(f => f.Name == "app-v1").Bytes);
        Assert.True(File.Exists(Path.Combine(box.RunDirectory, "app-v1/src/main.txt")));
    }

    [Fact]
    public void LinksAreNotTraversedAndDoNotContributeOutsideBytes()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-survey-link-" + Guid.NewGuid().ToString("N")));
        Fixture(box, "outside/package.json", 25);
        var root = Path.Combine(box.RunDirectory, "scan"); Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), Path.Combine(box.RunDirectory, "outside"));
        using var survey = new SpaceSurvey(root);
        var result = survey.Next(default);
        Assert.False(result.HasMore);
        Assert.Equal(0, result.Bytes);
        Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain(result.Folders, f => f.Project);
    }

    [Fact]
    public void VersionHintDoesNotMergeUnrelatedParentsOrPlainNames()
    {
        Assert.NotEqual(SpaceSurvey.VersionFamily("C:/one/app-v1"), SpaceSurvey.VersionFamily("C:/two/app-v2"));
        Assert.NotEqual(SpaceSurvey.VersionFamily("C:/one/app"), SpaceSurvey.VersionFamily("C:/one/apple"));
        Assert.Equal(SpaceSurvey.VersionFamily("C:/one/app-v1.2"), SpaceSurvey.VersionFamily("C:/one/app-backup"));
    }
}

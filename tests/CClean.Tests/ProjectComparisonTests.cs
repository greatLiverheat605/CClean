using System.Text;
using CClean.Core.Experiments;
using CClean.Core.Projects;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
public class ProjectComparisonTests
{
    private static ExperimentSandbox Sandbox() => new(Path.Combine(Path.GetTempPath(), "cclean-compare-" + Guid.NewGuid().ToString("N")));
    private static string Folder(string root, string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    private static string FileAt(string root, string name, string content)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false)); return path;
    }

    [Fact]
    public void CaseSensitiveNamesAndDirectoryPrefixesCannotOverwriteObservedSpellings()
    {
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.Null(ProjectComparison.ObserveRelativePath(spellings, "src\\a.txt"));
        Assert.Null(ProjectComparison.ObserveRelativePath(spellings, "src\\a.txt"));
        Assert.Equal("src\\a.txt", ProjectComparison.ObserveRelativePath(spellings, "src\\A.txt"));
        Assert.Equal("src\\a.txt", spellings["src\\A.txt"]);
        Assert.Equal("src", ProjectComparison.ObserveRelativePath(spellings, "SRC\\another.txt"));
        Assert.Equal("src", ProjectComparison.ObserveRelativePath(spellings, "SRC\\A.txt"));
        Assert.Null(ProjectComparison.ObserveRelativePath(spellings, "src-backup\\a.txt"));
        Assert.Null(ProjectComparison.ObserveRelativePath(spellings, "other\\a.txt"));
    }

    [Fact]
    public void ComparisonSeparatesUniqueSizeAndContentWithoutReadingSecretsOrWritingFiles()
    {
        using var box = Sandbox(); var left = Folder(box.RunDirectory, "v1"); var right = Folder(box.RunDirectory, "v2");
        FileAt(left, "src\\same.cs", "same"); FileAt(right, "src\\same.cs", "same");
        FileAt(left, "src\\changed.cs", "old!"); FileAt(right, "src\\changed.cs", "new!");
        FileAt(left, "size.txt", "small"); FileAt(right, "size.txt", "bigger");
        FileAt(left, "unique-left.txt", "keep me"); FileAt(right, "unique-right.txt", "keep me too");
        var secret = FileAt(left, ".env", "synthetic secret"); FileAt(right, ".env", "other synthetic secret");
        FileAt(left, ".codex\\sessions\\private.jsonl", "synthetic private session");
        FileAt(left, ".git\\config", "synthetic private config");
        var before = Directory.EnumerateFiles(left, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        using var secretLock = new FileStream(secret, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = ProjectComparison.Compare(left, right);
        ProjectComparisonItem Row(string path) => Assert.Single(result.Items, i => i.RelativePath == path);
        Assert.Equal(ProjectDifference.ContentEqual, Row("src\\same.cs").Difference);
        Assert.Equal(ProjectDifference.ContentDifferent, Row("src\\changed.cs").Difference);
        Assert.Equal(ProjectDifference.SizeDifferent, Row("size.txt").Difference);
        Assert.Equal(ProjectDifference.LeftOnly, Row("unique-left.txt").Difference);
        Assert.Equal(ProjectDifference.RightOnly, Row("unique-right.txt").Difference);
        Assert.Equal(ProjectDifference.Unknown, Row(".env").Difference);
        Assert.Contains("敏感", Row(".env").Reason);
        Assert.Equal(ProjectDifference.Unknown, Row(".codex").Difference);
        Assert.DoesNotContain(result.Items, i => i.RelativePath.Contains("private.jsonl"));
        Assert.Equal(16, result.ReadBytes);
        Assert.False(result.LeftComplete); Assert.False(result.Limited);
        Assert.All(before, pair => Assert.Equal(pair.Value, File.GetLastWriteTimeUtc(pair.Key)));
    }

    [Fact]
    public void LimitsNeverTurnUnobservedFilesIntoUniqueOrEqualEvidence()
    {
        using var box = Sandbox(); var left = Folder(box.RunDirectory, "v1"); var right = Folder(box.RunDirectory, "v2");
        for (var i = 0; i < 4; i++) { FileAt(left, $"{i}.txt", "same"); FileAt(right, $"{i}.txt", "same"); }
        var noContent = ProjectComparison.Compare(left, right, maxReadBytes: 0);
        Assert.True(noContent.Limited); Assert.Equal(0, noContent.ReadBytes);
        Assert.All(noContent.Items, item => Assert.Equal(ProjectDifference.Unknown, item.Difference));
        var partial = ProjectComparison.Compare(left, right, maxEntries: 1);
        Assert.True(partial.Limited); Assert.False(partial.LeftComplete); Assert.False(partial.RightComplete);
        Assert.DoesNotContain(partial.Items, i => i.Difference is ProjectDifference.LeftOnly or ProjectDifference.RightOnly);
        var expired = ProjectComparison.Compare(left, right, timeBudget: TimeSpan.FromTicks(1));
        Assert.True(expired.Limited); Assert.Empty(expired.Items);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectComparison.Compare(left, right, cancellation.Token));
    }

    [Fact]
    public void LockedLinkedAndSystemObjectsCannotBecomeEqualOrUniqueResults()
    {
        using var box = Sandbox(); var left = Folder(box.RunDirectory, "v1"); var right = Folder(box.RunDirectory, "v2");
        var locked = FileAt(left, "locked.bin", "same"); FileAt(right, "locked.bin", "same");
        var target = FileAt(box.RunDirectory, "target.bin", "outside");
        File.CreateSymbolicLink(Path.Combine(left, "link.bin"), target); FileAt(right, "link.bin", "outside");
        var system = FileAt(left, "hidden-system.bin", "system"); File.SetAttributes(system, FileAttributes.System);
        FileAt(right, "hidden-system.bin", "regular on other side");
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = ProjectComparison.Compare(left, right);
        Assert.All(result.Items, item => Assert.Equal(ProjectDifference.Unknown, item.Difference));
        Assert.DoesNotContain(result.Items, i => i.RelativePath == "hidden-system.bin");
        Assert.Equal(1, result.HiddenSystemObjects); Assert.Equal(0, result.ReadBytes);
    }

    [Fact]
    public void FolderFileConflictsAndUnsafeRootsRemainProtected()
    {
        using var box = Sandbox(); var left = Folder(box.RunDirectory, "v1"); var right = Folder(box.RunDirectory, "v2");
        FileAt(left, "src\\file.txt", "source"); FileAt(right, "src", "a file instead of a directory");
        var result = ProjectComparison.Compare(left, right);
        Assert.Equal(ProjectDifference.Unknown, Assert.Single(result.Items, i => i.RelativePath == "src").Difference);
        Assert.Throws<ArgumentException>(() => ProjectComparison.Compare(left, left));
        Assert.Throws<ArgumentException>(() => ProjectComparison.Compare(left, Path.Combine(left, "src")));
        Assert.Throws<ArgumentException>(() => ProjectComparison.Compare(left, Environment.SystemDirectory));
        Assert.Throws<ArgumentException>(() => ProjectComparison.Compare(left, Folder(box.RunDirectory, ".codex")));
    }
}

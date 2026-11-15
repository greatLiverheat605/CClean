using CClean.Core.Experiments;
using CClean.Core.Monitoring;
using CClean.Core.Cleanup;
using CClean.Core.Files;
using CClean.Core.Sessions;

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
    public void SystemFilesAndDirectoriesAreExcludedAndCannotBeSelectedDirectly()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-system-" + Guid.NewGuid().ToString("N")));
        Fixture(box, "project/package.json", 10);
        Fixture(box, "protected/child/package.json", 20);
        Fixture(box, "system.dat", 30);
        var protectedRoot = Path.Combine(box.RunDirectory, "protected");
        var systemFile = Path.Combine(box.RunDirectory, "system.dat");
        File.SetAttributes(protectedRoot, File.GetAttributes(protectedRoot) | FileAttributes.System);
        File.SetAttributes(systemFile, FileAttributes.System);
        try
        {
            using var survey = new SpaceSurvey(box.RunDirectory);
            var result = survey.Next(default);
            Assert.Equal(10, result.Bytes);
            Assert.Equal(1, result.Files);
            Assert.Equal(2, result.Skipped);
            Assert.DoesNotContain(result.Folders, f => f.Path.StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase));
            Assert.Throws<ExperimentRefusedException>(() => new SpaceSurvey(Path.Combine(protectedRoot, "child")));
            Assert.Throws<ExperimentRefusedException>(() => new SpaceSurvey(Environment.SystemDirectory));
            Assert.True(File.Exists(systemFile));
        }
        finally
        {
            File.SetAttributes(protectedRoot, FileAttributes.Directory);
            File.SetAttributes(systemFile, FileAttributes.Normal);
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AccessDeniedSubtreeRemainsACoverageGapRatherThanAnEmptyCompleteResult()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-survey-denied-" + Guid.NewGuid().ToString("N")));
        Fixture(box, "visible.txt", 10); Fixture(box, "denied/private.txt", 20);
        var denied = new DirectoryInfo(Path.Combine(box.RunDirectory, "denied"));
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(denied);
        var restricted = System.IO.FileSystemAclExtensions.GetAccessControl(denied);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        restricted.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
            System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny));
        try
        {
            System.IO.FileSystemAclExtensions.SetAccessControl(denied, restricted);
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(denied.FullName));
            using var survey = new SpaceSurvey(box.RunDirectory);
            var result = survey.Next(default);
            Assert.Equal(10, result.Bytes); Assert.Equal(1, result.Files);
            Assert.Equal(1, result.Skipped); Assert.False(result.HasMore);
            Assert.False(result.AllocationComplete);
        }
        finally { System.IO.FileSystemAclExtensions.SetAccessControl(denied, original); }
        Assert.True(File.Exists(Path.Combine(denied.FullName, "private.txt")));
    }

    [Fact]
    public void MixedToolRootsKeepDistinctPurposesWithoutCreatingNestedProjects()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-purpose-" + Guid.NewGuid().ToString("N")));
        Fixture(box, "app/.git/objects/data", 10);
        Fixture(box, "app/.codex/sessions/task/package.json", 20);
        Fixture(box, "app/.cache/runtime/package.json", 30);
        Fixture(box, "app/target/debug/package.json", 40);
        Fixture(box, "app/node_modules/library/package.json", 50);
        using var survey = new SpaceSurvey(Path.Combine(box.RunDirectory, "app"));
        var result = survey.Next(default);
        Assert.Single(result.Folders, f => f.Project);
        Assert.Equal(ContentPurpose.History, result.Folders.Single(f => f.Name == ".git").Explanation.Purpose);
        Assert.Equal(ContentPurpose.History, result.Folders.Single(f => f.Name == "sessions").Explanation.Purpose);
        Assert.Equal(ContentPurpose.Mixed, result.Folders.Single(f => f.Name == ".cache").Explanation.Purpose);
        Assert.Equal(ContentPurpose.BuildOutput, result.Folders.Single(f => f.Name == "target").Explanation.Purpose);
        Assert.Equal(ContentPurpose.Dependencies, result.Folders.Single(f => f.Name == "node_modules").Explanation.Purpose);
        Assert.All(result.Folders, f => Assert.Null(f.Explanation.Decision));
        // Directly surveying a dependency/tool root must not promote its manifests to projects either.
        using var nested = new SpaceSurvey(Path.Combine(box.RunDirectory, "app", ".codex"));
        Assert.DoesNotContain(nested.Next(default).Folders, f => f.Project);
        Assert.Equal(150, result.Bytes);
        Fixture(box, "build/actual-project/package.json", 10);
        using var explicitProject = new SpaceSurvey(Path.Combine(box.RunDirectory, "build", "actual-project"));
        Assert.Single(explicitProject.Next(default).Folders, f => f.Project);
    }

    [Theory]
    [InlineData(@"C:\Users\tester\.codex\sessions\task.jsonl", ContentPurpose.History, "重新安装不能保证恢复")]
    [InlineData(@"C:\Users\tester\.codex\archived_sessions", ContentPurpose.History, "重新安装不能保证恢复")]
    [InlineData(@"C:\Users\tester\.codex\thread_history_1.sqlite", ContentPurpose.History, "不推断与 JSONL 重复")]
    [InlineData(@"D:\unknown\database.sqlite", ContentPurpose.Unknown, "未知")]
    [InlineData(@"C:\Users\tester\Claude\LocalCache\sessiondata.vhdx", ContentPurpose.History, "会话磁盘")]
    [InlineData(@"C:\Users\tester\Claude\LocalCache\rootfs.vhdx", ContentPurpose.ToolRuntime, "运行")]
    [InlineData(@"C:\Users\tester\Claude\LocalCache", ContentPurpose.Mixed, "不能按 LocalCache")]
    [InlineData(@"G:\BuildCache\controlCoding\target", ContentPurpose.BuildOutput, "无已验证清理规则")]
    [InlineData(@"D:\CCleanLab\CCLEAN-20260917-DEV\benchmark", ContentPurpose.Mixed, "100,002")]
    [InlineData(@"D:\CCleanLab\CCLEAN-20260917-DEV\benchmark\file-00000.bin", ContentPurpose.TestSamples, "未提交改动")]
    [InlineData(@"D:\CCleanLab\CCLEAN-20260917-DEV\benchmark\index.db", ContentPurpose.WorkProduct, "锁定保留")]
    [InlineData(@"D:\CCleanLab\CCLEAN-20260917-DEV\benchmark\.cclean-sandbox", ContentPurpose.WorkProduct, "锁定保留")]
    [InlineData(@"D:\CCleanLab\CCLEAN-20260917-DEV\benchmark\reports\result.md", ContentPurpose.WorkProduct, "验收")]
    [InlineData(@"D:\other\benchmark\file-00000.bin", ContentPurpose.Unknown, "未知")]
    [InlineData(@"D:\unknown\payload.bin", ContentPurpose.Unknown, "未知")]
    [InlineData(@"D:\npm-cache\_cacache\content-v2\abcdef", ContentPurpose.DownloadCache, "专用独占")]
    [InlineData(@"D:\app\node_modules\package\index.js", ContentPurpose.Dependencies, "依赖")]
    [InlineData(@"C:\Users\tester\.nuget\packages", ContentPurpose.Dependencies, "依赖")]
    [InlineData(@"C:\Users\tester\.codex\logs\run.log", ContentPurpose.LogsAndDumps, "诊断")]
    public void ExplanationIsMetadataOnlyAndNeverGrantsExecution(string path, ContentPurpose purpose, string expected)
    {
        var explanation = ContentClassification.Describe(path);
        Assert.Equal(purpose, explanation.Purpose);
        Assert.Contains(expected, explanation.Summary);
        Assert.Null(explanation.Decision);
        Assert.Contains("未进行", explanation.Qualification);
        Assert.NotEmpty(explanation.Association);
        // Even a plausible build/cache/sample purpose cannot replace a configured, verified rule.
        var file = new FileSnapshot(path, "synthetic-identity", 10, 4096, 1, FileAttributes.Normal,
            DateTime.UtcNow.AddDays(-3).ToFileTimeUtc(), 0, true, false, null);
        var decision = ProtectionPolicy.Classify(file, [], new ActivitySnapshot(1, [new(Path.GetPathRoot(path)!, false, true, "synthetic")]), [], DateTimeOffset.UtcNow);
        Assert.False(decision.Eligible);
        Assert.Same(decision, decision.Explain(path).Decision);
        Assert.Contains("当前保留", decision.Explain(path).Qualification);
    }

    [Fact]
    public void AllocationDeduplicatesHardLinksWithinOneRootAndKeepsPathLogicalBytes()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-survey-allocation-" + Guid.NewGuid().ToString("N")));
        Fixture(box, "files/original.bin", 8000);
        var root = Path.Combine(box.RunDirectory, "files");
        var original = Path.Combine(root, "original.bin");
        Directory.CreateDirectory(Path.Combine(root, "child"));
        Assert.True(CreateHardLinkW(Path.Combine(root, "child", "alias.bin"), original, IntPtr.Zero));
        using var survey = new SpaceSurvey(root);
        var result = survey.Next(default);
        Assert.Equal(16000, result.Bytes);
        Assert.Equal(8000, result.KnownUniqueLogicalBytes);
        Assert.Equal(CClean.Core.Files.WindowsFileMetadata.Read(original).AllocatedBytes, result.KnownAllocatedBytes);
        Assert.Equal(1, result.DuplicatePaths);
        Assert.True(result.AllocationComplete);
        Assert.Equal(16000, result.Folders.Single(f => f.Path == root).Bytes);
        Assert.Contains("不可直接相加", result.AllocationBasis);
    }

    [Fact]
    public void IdentityBudgetAndNamedStreamsKeepAllocationGapsVisible()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-survey-budget-" + Guid.NewGuid().ToString("N")));
        Fixture(box, "files/a.bin", 10); Fixture(box, "files/b.bin", 10); Fixture(box, "files/c.bin", 10);
        using var limited = new SpaceSurvey(Path.Combine(box.RunDirectory, "files"), identityLimit: 2);
        var result = limited.Next(default);
        Assert.False(result.HasMore);
        Assert.Equal(30, result.Bytes);
        Assert.Equal(20, result.KnownUniqueLogicalBytes);
        Assert.True(result.IdentityBudgetReached);
        Assert.Equal(1, result.UnknownAllocationObjects);
        Assert.False(result.AllocationComplete);
        Fixture(box, "streams/named.bin", 10);
        File.WriteAllText(Path.Combine(box.RunDirectory, "streams", "named.bin:extra"), "synthetic", System.Text.Encoding.UTF8);
        using var streams = new SpaceSurvey(Path.Combine(box.RunDirectory, "streams"));
        var named = streams.Next(default);
        Assert.Equal(1, named.UnknownAllocationObjects);
        Assert.False(named.AllocationComplete);
        Assert.NotNull(named.KnownAllocatedBytes);
    }

    [Fact]
    public void PendingAllocationIsUnknownWhileAnInspectedEmptyRootIsZero()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-survey-empty-" + Guid.NewGuid().ToString("N")));
        var root = Path.Combine(box.RunDirectory, "empty"); Directory.CreateDirectory(root);
        using var survey = new SpaceSurvey(root);
        Assert.Null(survey.Snapshot().KnownAllocatedBytes);
        Assert.False(survey.Snapshot().AllocationComplete);
        var result = survey.Next(default);
        Assert.Equal(0, result.KnownAllocatedBytes);
        Assert.True(result.AllocationComplete);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);

    [Fact]
    public void VersionHintDoesNotMergeUnrelatedParentsOrPlainNames()
    {
        Assert.NotEqual(SpaceSurvey.VersionFamily("C:/one/app-v1"), SpaceSurvey.VersionFamily("C:/two/app-v2"));
        Assert.NotEqual(SpaceSurvey.VersionFamily("C:/one/app"), SpaceSurvey.VersionFamily("C:/one/apple"));
        Assert.Equal(SpaceSurvey.VersionFamily("C:/one/app-v1.2"), SpaceSurvey.VersionFamily("C:/one/app-backup"));
    }
}

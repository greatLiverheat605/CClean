using System.Text;
using System.Text.Json;
using CClean.Core.Experiments;
using CClean.Core.Projects;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "ScopeRegistry")]
public class ProjectRegistryTests
{
    [Fact]
    public void LegacyConfigKeepsItsHistoryKeyAndOnlyRootChangesCreateANewScopeVersion()
    {
        using var box = Sandbox();
        var data = Path.Combine(box.RunDirectory, "data"); Directory.CreateDirectory(data);
        var root = Root(box, "old"); var destination = Root(box, "new"); var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(data, "projects.json"), JsonSerializer.Serialize(new[]
            { new { Id = id, Name = "旧项目", Root = root, Tool = "Codex", Paused = false } }), Encoding.UTF8);
        var registry = new ProjectRegistry(data);
        var original = Assert.Single(registry.Projects);
        Assert.Equal(1, original.Revision); Assert.Equal(1, original.ScopeRevision);
        Assert.Equal(id.ToString(), original.ObservationKey);
        var renamed = registry.Update(id, "新名称", root, "自定义 Agent", original.Revision);
        Assert.Equal(2, renamed.Revision); Assert.Equal(original.ObservationKey, renamed.ObservationKey);
        Assert.Throws<InvalidOperationException>(() => registry.SetPaused(id, true, original.Revision));
        var paused = registry.SetPaused(id, true, renamed.Revision);
        Assert.Equal(original.ObservationKey, paused.ObservationKey);
        var moved = registry.Update(id, paused.Name, destination, paused.Tool, paused.Revision);
        Assert.Equal(2, moved.ScopeRevision); Assert.Equal($"{id:D}@2", moved.ObservationKey);
        Assert.False(registry.IsCurrent(id, paused.Revision));
        var loaded = Assert.Single(new ProjectRegistry(data).Projects);
        Assert.Equal(moved.ObservationKey, loaded.ObservationKey);
        Assert.Equal(moved.Revision, loaded.Revision);
        Assert.True(Directory.Exists(root)); Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public void ProjectsShareOneRegisteredRootWithExplicitReferencesAndDoNotFollowRootMoves()
    {
        using var box = Sandbox();
        var registry = new ProjectRegistry(Path.Combine(box.RunDirectory, "data"));
        var first = registry.Register("项目一", Root(box, "first"), "Codex");
        var second = registry.Register("项目二", Root(box, "second"), "Claude Code");
        var shared = registry.Register("共享工具数据", Root(box, "shared"), "通用工具");
        var child = Path.Combine(shared.Root, "confirmed-child"); Directory.CreateDirectory(child);
        registry.LinkScope(first.Id, shared.Id, child, "用户确认目录关联，不证明独占", first.Revision);
        registry.LinkScope(second.Id, shared.Id, child, "另一项目也使用此目录", second.Revision);
        Assert.Equal(3, registry.Projects.Count);
        Assert.Equal(shared.Id, registry.CoveringScope(child)!.Id);
        Assert.Throws<InvalidOperationException>(() => registry.Register("重复子根", child, "Codex"));
        Assert.Throws<ArgumentException>(() => registry.LinkScope(first.Id, shared.Id, second.Root, "范围外关联"));
        Assert.All(ProjectCatalog.Build(registry.Projects, []).Where(p => p.RegistrationId == first.Id || p.RegistrationId == second.Id),
            p => Assert.True(Assert.Single(p.RelatedScopes).IsCurrent));
        var renamed = registry.Update(shared.Id, "共享目录改名", shared.Root, shared.Tool, shared.Revision);
        Assert.All(ProjectCatalog.Build(registry.Projects, []).SelectMany(p => p.RelatedScopes), r => Assert.True(r.IsCurrent));
        registry.Update(shared.Id, renamed.Name, Root(box, "other-shared"), shared.Tool, renamed.Revision);
        var references = ProjectCatalog.Build(registry.Projects, []).SelectMany(p => p.RelatedScopes).ToArray();
        Assert.Equal(2, references.Length);
        Assert.All(references, r => { Assert.False(r.IsCurrent); Assert.Equal(child, r.Path); });
        Assert.True(Directory.Exists(child));
    }

    [Fact]
    public void FailedSaveLeavesEveryMutationAndItsReferencesUnpublished()
    {
        using var box = Sandbox();
        var data = Path.Combine(box.RunDirectory, "data");
        var registry = new ProjectRegistry(data);
        var first = registry.Register("项目一", Root(box, "first"), "Codex");
        var second = registry.Register("项目二", Root(box, "second"), "Codex");
        var shared = registry.Register("共享", Root(box, "shared"), "通用工具");
        first = registry.LinkScope(first.Id, shared.Id, shared.Root, "用户确认");
        registry.LinkScope(second.Id, shared.Id, shared.Root, "用户确认");
        var extra = Root(box, "extra");
        var before = JsonSerializer.Serialize(registry.Projects);
        var file = Path.Combine(data, "projects.json"); var bytes = File.ReadAllBytes(file);
        Directory.CreateDirectory(file + ".new");
        Action[] changes =
        [
            () => registry.Register("新增", extra, "Codex"),
            () => registry.Update(first.Id, "改名", first.Root, first.Tool, first.Revision),
            () => registry.Update(shared.Id, shared.Name, extra, shared.Tool, shared.Revision),
            () => registry.SetPaused(first.Id, true, first.Revision),
            () => registry.Remove(shared.Id, shared.Revision),
            () => registry.LinkScope(first.Id, second.Id, second.Root, "新关联", first.Revision),
            () => registry.UnlinkScope(first.Id, shared.Id, shared.Root, first.Revision)
        ];
        foreach (var change in changes)
        {
            var error = Record.Exception(change);
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Equal(before, JsonSerializer.Serialize(registry.Projects));
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        Assert.Equal(before, JsonSerializer.Serialize(new ProjectRegistry(data).Projects));
    }

    [Fact]
    public void RemovingMonitoringCleansReferencesWithoutRemovingDirectoriesOrReusingIdentity()
    {
        using var box = Sandbox();
        var registry = new ProjectRegistry(Path.Combine(box.RunDirectory, "data"));
        var project = registry.Register("项目", Root(box, "project"), "Codex");
        var shared = registry.Register("共享数据", Root(box, "shared"), "通用工具");
        var file = Path.Combine(shared.Root, "keep.txt"); File.WriteAllText(file, "用户文件", Encoding.UTF8);
        project = registry.LinkScope(project.Id, shared.Id, shared.Root, "用户确认");
        registry.Remove(shared.Id, shared.Revision);
        var retained = Assert.Single(registry.Projects);
        Assert.Empty(retained.RelatedScopes); Assert.Equal(project.Revision + 1, retained.Revision);
        Assert.Equal(project.ObservationKey, retained.ObservationKey);
        Assert.False(registry.IsCurrent(shared.Id, shared.Revision));
        Assert.Equal("用户文件", File.ReadAllText(file, Encoding.UTF8));
        Assert.NotEqual(shared.Id, registry.Register("重新确认", shared.Root, shared.Tool).Id);
    }

    [Fact]
    public void SharedReferencesDoNotConsumeAdditionalPhysicalRootSlots()
    {
        using var box = Sandbox();
        var registry = new ProjectRegistry(Path.Combine(box.RunDirectory, "data"));
        for (var i = 0; i < 64; i++) registry.Register("范围 " + i, Root(box, "root-" + i), "通用工具");
        var first = registry.Projects[0]; var shared = registry.Projects[1];
        var linked = registry.LinkScope(first.Id, shared.Id, shared.Root, "用户确认共享");
        Assert.Single(linked.RelatedScopes); Assert.Equal(64, registry.Projects.Count);
        Assert.Throws<InvalidOperationException>(() => registry.Register("第 65 根", Root(box, "extra"), "Codex"));
        Assert.Equal(64, new ProjectRegistry(Path.Combine(box.RunDirectory, "data")).Projects.Count);
    }

    private static ExperimentSandbox Sandbox() => new(Path.Combine(Path.GetTempPath(), "cclean-registry-edit-" + Guid.NewGuid().ToString("N")));
    private static string Root(ExperimentSandbox box, string name)
    {
        var path = Path.Combine(box.RunDirectory, name); Directory.CreateDirectory(path); return path;
    }
}

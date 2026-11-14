using System.Text.Json;
using CClean.Core.Experiments;
using CClean.Core.Projects;
using CClean.Core.Sessions;
using CClean.Core.Tools;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "ToolRegistration")]
public class ToolRegistrationTests
{
    [Fact]
    public void KnownAndCustomToolsPersistWithoutGainingAutomaticCapabilities()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-tools-" + Guid.NewGuid().ToString("N")));
        var projectData = Path.Combine(box.RunDirectory, "projects-data");
        var sessionData = Path.Combine(box.RunDirectory, "sessions-data");
        var registry = new ProjectRegistry(projectData);
        var labels = KnownTools.RegistrationLabels.Append("本地 Agent").ToArray();
        using (var sessions = new SessionRegistry(sessionData))
        {
            for (var i = 0; i < labels.Length; i++)
            {
                var root = Path.Combine(box.RunDirectory, "project-" + i); Directory.CreateDirectory(root);
                var project = registry.Register(labels[i], root, " " + labels[i] + " ");
                Assert.Equal(labels[i], project.Tool);
                Assert.Equal(labels[i], sessions.Begin(project, project.Tool, "手动上下文").Tool);
            }
        }
        Assert.Equal(labels, new ProjectRegistry(projectData).Projects.Select(p => p.Tool));
        using var recovered = new SessionRegistry(sessionData);
        Assert.Equal(labels, recovered.All.Select(s => s.Tool));
        Assert.All(recovered.All, s => Assert.Equal(SessionState.Interrupted, s.State));
        Assert.Equal("Codex", KnownTools.NormalizeLabel(" codex "));
        Assert.Equal(new[] { "Codex", "Claude Code" }, KnownTools.Baseline.Where(t => t.SupportsAutomaticSessionMetadata).Select(t => t.DisplayName));
        Assert.False(KnownTools.Describe("本地 Agent").SupportsAutomaticSessionMetadata);
        Assert.Equal(ToolCapability.ManualRegistration, KnownTools.Describe("本地 Agent").Capability);
    }

    [Fact]
    public void InvalidLabelsCannotMutateProjectOrSessionRecords()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-invalid-tools-" + Guid.NewGuid().ToString("N")));
        var registry = new ProjectRegistry(Path.Combine(box.RunDirectory, "projects-data"));
        var root = Path.Combine(box.RunDirectory, "project"); Directory.CreateDirectory(root);
        var project = registry.Register("项目", root, "通用工具");
        using var sessions = new SessionRegistry(Path.Combine(box.RunDirectory, "sessions-data"));
        foreach (var label in new[] { null, "", " ", "Agent\n", "Agent\0", new string('x', KnownTools.MaxLabelLength + 1) })
        {
            Assert.Throws<ArgumentException>(() => registry.Register("无效", root, label!));
            Assert.Throws<ArgumentException>(() => sessions.Begin(project, label!, "无效"));
        }
        Assert.Single(registry.Projects);
        Assert.Empty(sessions.All);
        Assert.Equal(0, sessions.Version);
    }

    [Fact]
    public void UnknownToolsCannotBorrowTheClaudeMetadataParser()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-tool-metadata-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(box.RunDirectory, "session.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(new { sessionId = "claude-session", cwd = box.RunDirectory }) + "\n", System.Text.Encoding.UTF8);
        Assert.NotNull(SessionDiscovery.ReadMetadata(path, "Claude Code"));
        foreach (var tool in KnownTools.RegistrationLabels.Where(t => t != "Codex" && t != "Claude Code").Append("本地 Agent"))
            Assert.Null(SessionDiscovery.ReadMetadata(path, tool));
    }
}

using System.Text.Json;
using CClean.Core.Experiments;
using CClean.Core.Sessions;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
public class SessionDiscoveryTests
{
    private static string Write(string root, string name, object value)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, JsonSerializer.Serialize(value) + "\n", System.Text.Encoding.UTF8);
        return path;
    }
    [Fact]
    public void DiscoversMetadataIncrementallyWithoutImportingMessagesAndKeepsUnknownState()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-discovery-" + Guid.NewGuid().ToString("N")));
        var path = Write(box.RunDirectory, "rollout-one.jsonl", new { type = "session_meta", payload = new { id = "one", cwd = box.RunDirectory, timestamp = "2026-09-19T00:00:00Z", base_instructions = "not imported" } });
        File.AppendAllText(path, "not valid JSON chat body should never be parsed\n");
        var discovery = new SessionDiscovery([new("Codex", box.RunDirectory)]);
        var result = discovery.Next(default, 1);
        while (result.HasMore) result = discovery.Next(default, 1);
        var session = Assert.Single(result.Sessions);
        Assert.Equal("one", session.Id);
        Assert.Equal(box.RunDirectory, session.ProjectRoot);
        Assert.Contains("未知", session.StateLabel);
        Write(box.RunDirectory, "rollout-two.jsonl", new { type = "session_meta", payload = new { id = "two", cwd = box.RunDirectory } });
        result = discovery.Next(default);
        Assert.Equal(2, result.Sessions.Count);
        // A concurrent/truncated write cannot silently remove an established protection scope.
        File.WriteAllText(path, "{", System.Text.Encoding.UTF8);
        Write(box.RunDirectory, "rollout-three.jsonl", new { type = "session_meta", payload = new { id = "three", cwd = box.RunDirectory } });
        result = discovery.Next(default);
        Assert.Equal(3, result.Sessions.Count);
        Assert.True(result.Skipped > 0);
        Assert.Throws<OperationCanceledException>(() => discovery.Next(new CancellationToken(true)));
    }
    [Fact]
    public void ClaudeMetadataSkipsUnrelatedRecordsAndRejectsRelativePathsAndOversizeHeaders()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-claude-meta-" + Guid.NewGuid().ToString("N")));
        var path = Write(box.RunDirectory, "one.jsonl", new { type = "file-history-snapshot" });
        File.AppendAllText(path, JsonSerializer.Serialize(new { sessionId = "claude-one", cwd = box.RunDirectory, timestamp = "2026-09-19T00:00:00Z", message = new { content = "not retained" } }) + "\n");
        Assert.Equal("claude-one", SessionDiscovery.ReadMetadata(path, "Claude Code")!.Id);
        Write(box.RunDirectory, "one.jsonl", new { sessionId = "bad", cwd = "relative" });
        Assert.Null(SessionDiscovery.ReadMetadata(path, "Claude Code"));
        File.WriteAllText(path, new string('x', 262145), System.Text.Encoding.UTF8);
        Assert.Null(SessionDiscovery.ReadMetadata(path, "Claude Code"));
    }
    [Fact]
    public void LinkedSourcesAreNotRead()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-session-link-" + Guid.NewGuid().ToString("N")));
        var outside = Path.Combine(box.RunDirectory, "outside"); Directory.CreateDirectory(outside);
        Write(outside, "rollout-one.jsonl", new { type = "session_meta", payload = new { id = "one", cwd = outside } });
        var linked = Path.Combine(box.RunDirectory, "linked"); Directory.CreateSymbolicLink(linked, outside);
        var result = new SessionDiscovery([new("Codex", linked)]).Next(default);
        Assert.Empty(result.Sessions); Assert.True(result.Skipped > 0);
    }
}

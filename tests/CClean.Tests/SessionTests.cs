using System.Text.Json;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Projects;
using CClean.Core.Sessions;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "Sessions")]
[Trait("Category", "Foundation")]
public class SessionTests
{
    [Fact]
    public void AllToolsSupportConcurrentManualSessionsAndRestartIsNotSilentResume()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-sessions-" + Guid.NewGuid().ToString("N")));
        var project = new RegisteredProject(Guid.NewGuid(), "项目", box.RunDirectory, "Codex");
        var data = Path.Combine(box.RunDirectory, "data");
        Guid interrupted;
        using (var sessions = new SessionRegistry(data))
        {
            foreach (var tool in new[] { "Codex", "Claude Code", "ZCode" }) sessions.Begin(project, tool, tool + " 合成会话", [box.RunDirectory]);
            Assert.Equal(3, sessions.All.Count);
            Assert.All(sessions.Activity([project], _ => true).Scopes, s => Assert.True(s.Active));
            interrupted = sessions.All[0].Id;
            sessions.End(sessions.All[1].Id);
            Assert.Contains("不代表可删除", sessions.All[1].StateLabel);
        }
        using var recovered = new SessionRegistry(data);
        Assert.Equal(2, recovered.All.Count(s => s.State == SessionState.Interrupted));
        Assert.DoesNotContain(recovered.All, s => s.State == SessionState.Active);
        Assert.False(recovered.Activity([project], _ => true).Scopes[0].ObservationKnown);
        var resumed = recovered.Resume(interrupted, project);
        Assert.Equal(interrupted, resumed.ResumedFrom);
        Assert.NotEqual(interrupted, resumed.Id);
        Assert.Throws<InvalidOperationException>(() => recovered.Resume(interrupted, project));
        recovered.End(interrupted);
        Assert.True(recovered.All.Single(s => s.Id == interrupted).HasGap);
    }

    [Fact]
    public void ChangedRootKeepsHistoricalContextAndCannotResumeOldSession()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-session-scope-" + Guid.NewGuid().ToString("N")));
        var project = new RegisteredProject(Guid.NewGuid(), "项目", box.RunDirectory, "Codex");
        var data = Path.Combine(box.RunDirectory, "data");
        Guid oldSession;
        using (var sessions = new SessionRegistry(data))
        {
            oldSession = sessions.Begin(project, "Codex", "旧范围会话").Id;
            Assert.Equal(project.ObservationKey, sessions.All[0].ObservationKey);
        }
        var moved = project with { Root = Path.Combine(box.RunDirectory, "moved"), ScopeRevision = 2, Revision = 2 };
        using var recovered = new SessionRegistry(data);
        Assert.Throws<InvalidOperationException>(() => recovered.Resume(oldSession, moved));
        var current = recovered.Begin(moved, "Codex", "新范围会话");
        Assert.Equal(moved.ObservationKey, current.ObservationKey);
        var time = DateTimeOffset.UtcNow;
        var oldChange = new Core.Storage.StoredChange(1, time, project.ObservationKey,
            Path.Combine(project.Root, "old.bin"), "old-identity", 0, 1, "新增");
        var currentChange = oldChange with { ProjectId = moved.ObservationKey, Path = Path.Combine(moved.Root, "new.bin") };
        Assert.Equal(Core.Attribution.EvidenceKind.Unknown, Core.Attribution.AttributionRules.Classify(oldChange, [current]).Kind);
        Assert.Equal(Core.Attribution.EvidenceKind.Context, Core.Attribution.AttributionRules.Classify(currentChange, [current]).Kind);
        Assert.Equal(1, recovered.All.Single(s => s.Id == oldSession).ScopeRevision);
    }

    [Fact]
    public void ProcessReuseAndUnknownObservationCannotBecomeEvidence()
    {
        var instance = ProcessInstance.Read(Environment.ProcessId);
        Assert.NotNull(instance);
        Assert.NotEqual(instance, instance with { StartedUtcTicks = instance.StartedUtcTicks + 1 });
        Assert.Null(ProcessInstance.Read(int.MaxValue));
        Assert.DoesNotContain(Core.Tools.KnownTools.Baseline, t => t.Capability == Core.Tools.ToolCapability.ProcessEvidence);
        Assert.Equal(Core.Tools.ToolCapability.ManualRegistration, Core.Tools.KnownTools.Baseline.Single(t => t.Id == "zcode").Capability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingMonitorRetainsDependencyProtectionUntilSessionExplicitlyEnds(bool restart)
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-session-removal-" + Guid.NewGuid().ToString("N")));
        var projectRoot = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "project")).FullName;
        var cacheRoot = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "cache")).FullName;
        var data = Path.Combine(box.RunDirectory, "data");
        var registry = new ProjectRegistry(data);
        var project = registry.Register("项目", projectRoot, "Codex");
        registry.Register("专用缓存", cacheRoot, "Codex");
        var rule = Core.Cleanup.CacheRule.NpmContent;
        var scopes = new Core.Cleanup.RuleScope[] { new(cacheRoot, rule.Id, rule.Version, rule.SupportedVersion, true, true) };
        var file = new Core.Files.FileSnapshot(Path.Combine(cacheRoot, "_cacache", "content-v2", "sha512", "a"),
            "fixture", 1, 4096, 1, FileAttributes.Normal, DateTime.UtcNow.AddHours(-1).ToFileTimeUtc(), 1, true, false, null);
        var sessions = new SessionRegistry(data);
        try
        {
            Core.Cleanup.ProtectionDecision Classify() => Core.Cleanup.ProtectionPolicy.Classify(file, scopes,
                sessions.Activity(registry.Projects, _ => true), [], DateTimeOffset.UtcNow);
            Assert.True(Classify().Eligible);
            var session = sessions.Begin(project, "Codex", "依赖仍在使用", [cacheRoot]);
            project = registry.SetPaused(project.Id, true, project.Revision);
            Assert.Equal("活动保护", Classify().Category);
            registry.Remove(project.Id, project.Revision);
            if (restart)
            {
                sessions.Dispose();
                sessions = new SessionRegistry(data);
            }
            Assert.False(Classify().Eligible);
            Assert.Equal(restart ? "活动未知" : "活动保护", Classify().Category);
            sessions.End(session.Id);
            Assert.True(Classify().Eligible);
        }
        finally { sessions.Dispose(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OriginalProjectRootStaysProtectedAfterRemovalOrMoveAndNewRegistration(bool move, bool restart)
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-session-original-root-" + Guid.NewGuid().ToString("N")));
        var originalRoot = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "original-cache")).FullName;
        var movedRoot = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "moved-cache")).FullName;
        var data = Path.Combine(box.RunDirectory, "data");
        var registry = new ProjectRegistry(data);
        var original = registry.Register("原范围", originalRoot, "Codex");
        var rule = CacheRule.NpmContent;
        RuleScope[] rules = [new(originalRoot, rule.Id, rule.Version, rule.SupportedVersion, true, true)];
        var file = new FileSnapshot(Path.Combine(originalRoot, "_cacache", "content-v2", "sha512", "a"),
            "fixture", 1, 4096, 1, FileAttributes.Normal, DateTime.UtcNow.AddHours(-1).ToFileTimeUtc(), 1, true, false, null);
        var sessions = new SessionRegistry(data);
        try
        {
            ProtectionDecision Classify() => ProtectionPolicy.Classify(file, rules,
                sessions.Activity(registry.Projects, _ => true), [], DateTimeOffset.UtcNow);
            Assert.True(Classify().Eligible);
            var session = sessions.Begin(original, "Codex", "仍在使用原位置");
            Assert.Equal(originalRoot, session.ProjectRoot);
            if (move) registry.Update(original.Id, original.Name, movedRoot, original.Tool, original.Revision);
            else registry.Remove(original.Id, original.Revision);
            var replacement = registry.Register("同路径新登记", originalRoot, "Codex");
            Assert.NotEqual(original.Id, replacement.Id);
            if (restart)
            {
                sessions.Dispose();
                sessions = new SessionRegistry(data, registry.Projects);
            }
            Assert.Equal(originalRoot, sessions.All.Single().ProjectRoot);
            Assert.Equal(restart ? "活动未知" : "活动保护", Classify().Category);
            if (move)
                Assert.All(sessions.Activity(registry.Projects, _ => true).Scopes.Where(s => s.Root == movedRoot), s =>
                { Assert.False(s.Active); Assert.True(s.ObservationKnown); });
            sessions.End(session.Id);
            Assert.True(Classify().Eligible);
        }
        finally { sessions.Dispose(); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void LegacyRootMigrationRequiresMatchingIdAndScopeAndPersistsWithRestartGap(bool sameId, bool sameScope)
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-session-legacy-root-" + Guid.NewGuid().ToString("N")));
        var currentRoot = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "current")).FullName;
        var otherRoot = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "other-cache")).FullName;
        var dependency = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "dependency")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "data")).FullName;
        var current = new RegisteredProject(Guid.NewGuid(), "当前范围", currentRoot, "Codex")
            { ScopeRevision = sameScope ? 1 : 2, Revision = sameScope ? 1 : 2 };
        var other = new RegisteredProject(Guid.NewGuid(), "无关范围", otherRoot, "Codex");
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(data, "sessions.json"), JsonSerializer.Serialize(new
        {
            Schema = 1, Version = 7,
            Sessions = new[] { new { Id = id, ProjectId = sameId ? current.Id : Guid.NewGuid(), Label = "旧版会话", Tool = "Codex",
                Started = DateTimeOffset.UtcNow.AddHours(-2), Ended = (DateTimeOffset?)null, State = SessionState.Active,
                Dependencies = new[] { dependency }, HasGap = false } }
        }), new System.Text.UTF8Encoding(false));
        var bound = sameId && sameScope;
        using (var sessions = new SessionRegistry(data, [current, other]))
        {
            var restored = Assert.Single(sessions.All);
            Assert.Equal(bound ? currentRoot : null, restored.ProjectRoot);
            Assert.Equal(1, restored.ScopeRevision);
            Assert.Equal(SessionState.Interrupted, restored.State);
            Assert.True(restored.HasGap);
            Assert.Equal(8, sessions.Version); // Root binding and interrupted state commit together.
            var activity = sessions.Activity([current, other], _ => true);
            Assert.Equal(bound, Assert.Single(activity.Scopes, s => s.Root == otherRoot).ObservationKnown);
            if (!bound) Assert.All(activity.Scopes, s =>
            { Assert.False(s.ObservationKnown); Assert.Contains("原项目根无法可靠核验", s.Reason); });
            var rule = CacheRule.NpmContent;
            RuleScope[] rules = [new(otherRoot, rule.Id, rule.Version, rule.SupportedVersion, true, true)];
            var file = new FileSnapshot(Path.Combine(otherRoot, "_cacache", "content-v2", "sha512", "a"),
                "fixture", 1, 4096, 1, FileAttributes.Normal, DateTime.UtcNow.AddHours(-1).ToFileTimeUtc(), 1, true, false, null);
            Assert.Equal(bound, ProtectionPolicy.Classify(file, rules, activity, [], DateTimeOffset.UtcNow).Eligible);
        }
        using (var payload = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "sessions.json"))))
            Assert.Equal(bound ? currentRoot : null, payload.RootElement.GetProperty("Sessions")[0].GetProperty("ProjectRoot").GetString());
        using var recovered = new SessionRegistry(data, [current, other]);
        Assert.Equal(8, recovered.Version);
        recovered.End(id);
        Assert.All(recovered.Activity([current, other], _ => true).Scopes, s => Assert.True(s.ObservationKnown));
    }

    [Fact]
    public void EndedLegacySessionWithoutKnownRootDoesNotBlockUnrelatedCurrentObservation()
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-session-ended-legacy-" + Guid.NewGuid().ToString("N")));
        var data = Directory.CreateDirectory(Path.Combine(box.RunDirectory, "data")).FullName;
        var project = new RegisteredProject(Guid.NewGuid(), "当前项目", box.RunDirectory, "Codex");
        File.WriteAllText(Path.Combine(data, "sessions.json"), JsonSerializer.Serialize(new
        {
            Schema = 1, Version = 7,
            Sessions = new[] { new { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Label = "已明确结束", Tool = "Codex",
                Started = DateTimeOffset.UtcNow.AddHours(-2), Ended = DateTimeOffset.UtcNow.AddHours(-1), State = SessionState.Ended,
                Dependencies = new[] { box.RunDirectory }, HasGap = false } }
        }), new System.Text.UTF8Encoding(false));
        using var sessions = new SessionRegistry(data, [project]);
        Assert.Null(Assert.Single(sessions.All).ProjectRoot);
        Assert.Equal(7, sessions.Version);
        Assert.All(sessions.Activity([project], _ => true).Scopes, s => Assert.True(s.ObservationKnown));
    }
}

using CClean.Core.Experiments;
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
    public void ProcessReuseAndUnknownObservationCannotBecomeEvidence()
    {
        var instance = ProcessInstance.Read(Environment.ProcessId);
        Assert.NotNull(instance);
        Assert.NotEqual(instance, instance with { StartedUtcTicks = instance.StartedUtcTicks + 1 });
        Assert.Null(ProcessInstance.Read(int.MaxValue));
        Assert.All(Core.Tools.KnownTools.Baseline, t => Assert.Equal(Core.Tools.ToolCapability.ManualRegistration, t.Capability));
    }
}

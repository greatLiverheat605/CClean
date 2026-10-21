using CClean.Core.Experiments;
using CClean.Core.Monitoring;
using CClean.Core.Projects;
using CClean.Core.Sessions;
using CClean.Core.Storage;

namespace CClean.Poc;

public static class TimelineProbe
{
    public static void Run(ExperimentSandbox sandbox, List<object> checks)
    {
        var data = Path.Combine(sandbox.RunDirectory, "data");
        var registry = new ProjectRegistry(data);
        using var store = new LocalStore(data); using var sessions = new SessionRegistry(data);
        var shared = Path.Combine(sandbox.RunDirectory, "shared"); Directory.CreateDirectory(shared);
        var cache = registry.Register("合成共享目录", shared, "通用工具");
        Save(cache);
        var active = new List<DevelopmentSession>();
        foreach (var tool in new[] { "Codex", "Claude Code", "ZCode" })
        {
            var root = Path.Combine(sandbox.RunDirectory, tool.Replace(" ", "-")); Directory.CreateDirectory(root);
            var project = registry.Register("合成 " + tool + " 项目", root, tool);
            File.Move(sandbox.CreateFixture(tool.Replace(" ", "-") + "-base.bin", 100), Path.Combine(root, "initial.bin")); Save(project);
            active.Add(sessions.Begin(project, tool, "合成开发会话", [shared]));
            File.Move(sandbox.CreateFixture(tool.Replace(" ", "-") + "-change.bin", 300), Path.Combine(root, "observed.bin")); Save(project);
        }
        File.Move(sandbox.CreateFixture("shared.bin", 512), Path.Combine(shared, "shared.bin")); Save(cache);
        foreach (var session in active) sessions.End(session.Id);
        store.RecordGap(cache.Id.ToString(), "合成测试：暂停区间示例，不补造离线历史。");
        checks.Add(new { name = "timeline-fixture", status = "passed", dataDirectory = data, projects = registry.Projects.Count, sessions = active.Count });
        void Save(RegisteredProject project)
        {
            if (!store.SaveSnapshot(project.Id.ToString(), DirectoryScanner.Scan(project.Root), store.LoadSnapshots(project.Id.ToString())))
                throw new IOException("Synthetic timeline budget stopped.");
        }
    }
}

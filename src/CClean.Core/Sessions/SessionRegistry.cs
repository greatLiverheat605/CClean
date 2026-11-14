using System.Diagnostics;
using System.Text.Json;
using CClean.Core.Experiments;
using CClean.Core.Projects;
using CClean.Core.Storage;
using CClean.Core.Tools;

namespace CClean.Core.Sessions;

public enum SessionState { Active, Ended, Interrupted }
public sealed record DevelopmentSession(Guid Id, Guid ProjectId, string Label, string Tool, DateTimeOffset Started,
    DateTimeOffset? Ended, SessionState State, string[] Dependencies, Guid? ResumedFrom = null, bool HasGap = false)
{
    public long ScopeRevision { get; init; } = 1;
    public string? ProjectRoot { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ObservationKey => ScopeRevision == 1 ? ProjectId.ToString() : $"{ProjectId:D}@{ScopeRevision}";
    public string StateLabel => State switch { SessionState.Active => "活动", SessionState.Ended => "已结束（不代表可删除）", _ => "中断，结束时间未知" };
    public string Evidence => "A 级手动上下文，未经进程证实";
    [System.Text.Json.Serialization.JsonIgnore]
    public string StartedLocal => Started.ToLocalTime().ToString("MM-dd HH:mm:ss");
}
public sealed record ProcessInstance(int Pid, long StartedUtcTicks)
{
    public static ProcessInstance? Read(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return new(pid, process.StartTime.ToUniversalTime().Ticks); }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
public sealed record ActivityScope(string Root, bool Active, bool ObservationKnown, string Reason);
public sealed record ActivitySnapshot(long Version, IReadOnlyList<ActivityScope> Scopes);

public sealed class SessionRegistry : IDisposable
{
    private readonly object sync = new();
    private readonly string root, file;
    private readonly FileStream owner;
    private List<DevelopmentSession> sessions = [];
    public long Version { get; private set; }
    public SessionRegistry(string dataDirectory, IEnumerable<RegisteredProject>? knownProjects = null)
    {
        root = dataDirectory;
        Directory.CreateDirectory(root);
        owner = new FileStream(Path.Combine(root, "session-owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        file = Path.Combine(root, "sessions.json");
        try
        {
            if (!File.Exists(file)) return;
            if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new IOException("会话文件超出预算。");
            var saved = JsonSerializer.Deserialize<SavedSessions>(File.ReadAllText(file)) ?? throw new IOException("会话文件无效。");
            if (saved.Schema != 1 || saved.Sessions.Count > 1000) throw new IOException("不支持的会话格式或数量。");
            var projects = knownProjects?.ToArray() ?? [];
            var changed = false;
            sessions = saved.Sessions.Select(s =>
            {
                var projectRoot = NormalizeProjectRoot(s.ProjectRoot);
                if (s.ProjectRoot is null)
                    projectRoot = NormalizeProjectRoot(projects.FirstOrDefault(p => p.Id == s.ProjectId && p.ScopeRevision == s.ScopeRevision)?.Root);
                var restored = s with { Tool = KnownTools.NormalizeLabel(s.Tool), ProjectRoot = projectRoot };
                if (restored.State == SessionState.Active) restored = restored with { State = SessionState.Interrupted, HasGap = true };
                changed |= restored != s;
                return restored;
            }).ToList();
            Version = saved.Version;
            if (changed) Save();
        }
        catch { owner.Dispose(); throw; }
    }
    public IReadOnlyList<DevelopmentSession> All { get { lock (sync) return sessions.ToArray(); } }

    public DevelopmentSession Begin(RegisteredProject project, string tool, string label, IEnumerable<string>? dependencies = null, Guid? resumedFrom = null)
    {
        lock (sync)
        {
            tool = KnownTools.NormalizeLabel(tool);
            if (string.IsNullOrWhiteSpace(label) || label.Length > 120) throw new ArgumentException("会话名称须为 1–120 字符。");
            var projectRoot = NormalizeProjectRoot(project.Root) ?? throw new ArgumentException("会话需要完整、明确的本地项目根。");
            var roots = (dependencies ?? []).Select(ExperimentSandbox.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length > 64) throw new ArgumentException("单会话最多关联 64 个依赖目录。");
            foreach (var path in roots) { using var lease = new DirectoryLease(path); }
            var session = new DevelopmentSession(Guid.NewGuid(), project.Id, label.Trim(), tool, DateTimeOffset.UtcNow, null, SessionState.Active, roots, resumedFrom)
                { ScopeRevision = project.ScopeRevision, ProjectRoot = projectRoot };
            var previous = sessions;
            sessions = sessions.Where(s => s.State != SessionState.Ended || s.Ended >= DateTimeOffset.UtcNow.AddDays(-30)).ToList();
            if (sessions.Count >= 1000) { sessions = previous; throw new IOException("会话记录已达上限，请等待历史保留期结束。"); }
            sessions.Add(session);
            try { Save(); } catch { sessions = previous; throw; }
            return session;
        }
    }
    public void End(Guid id)
    {
        lock (sync)
        {
            var index = sessions.FindIndex(s => s.Id == id);
            if (index < 0) throw new ArgumentException("会话不存在。");
            var previous = sessions[index];
            if (previous.State == SessionState.Ended) return;
            sessions[index] = previous with { State = SessionState.Ended, Ended = DateTimeOffset.UtcNow };
            try { Save(); } catch { sessions[index] = previous; throw; }
        }
    }
    public DevelopmentSession Resume(Guid id, RegisteredProject project)
    {
        lock (sync)
        {
            var session = sessions.Single(s => s.Id == id);
            if (session.State != SessionState.Interrupted || session.ProjectId != project.Id || session.ScopeRevision != project.ScopeRevision)
                throw new InvalidOperationException("仅可恢复同一项目与范围版本的中断会话；范围调整后请新建会话。");
            if (sessions.Any(s => s.ResumedFrom == id && s.State == SessionState.Active)) throw new InvalidOperationException("该中断会话已恢复。");
            return Begin(project, session.Tool, session.Label, session.Dependencies, id);
        }
    }
    public ActivitySnapshot Activity(IEnumerable<RegisteredProject> projects, Func<string, bool> observationKnown)
    {
        lock (sync)
        {
            var scopes = new List<ActivityScope>();
            var unbound = sessions.Any(s => s.State != SessionState.Ended && s.ProjectRoot is null);
            const string unboundReason = "旧会话原项目根无法可靠核验，当前范围与依赖保持未知保护；请明确结束该会话";
            foreach (var project in projects)
            {
                var linked = sessions.Where(s => s.ProjectId == project.Id && s.ScopeRevision == project.ScopeRevision).ToArray();
                var active = linked.Any(s => s.State == SessionState.Active);
                var known = !unbound && !project.Paused && observationKnown(project.Root) && !linked.Any(s => s.State == SessionState.Interrupted);
                scopes.Add(new(project.Root, active, known, unbound ? unboundReason : active ? "手动会话活动" : known ? "仅观察状态；结束不授予删除资格" : "活动观察不完整"));
            }
            // Registration edits do not move a session's original root or release its dependency protection.
            foreach (var session in sessions)
            {
                if (session.State != SessionState.Ended && session.ProjectRoot is string projectRoot)
                    scopes.Add(new(projectRoot, session.State == SessionState.Active,
                        !unbound && session.State != SessionState.Interrupted && observationKnown(projectRoot),
                        unbound ? unboundReason : session.State == SessionState.Active ? "手动会话原项目根，移除或调整监测不结束会话" : "原项目根会话中断，保持未知保护"));
                foreach (var path in session.Dependencies)
                    scopes.Add(new(path, session.State == SessionState.Active,
                        !unbound && session.State != SessionState.Interrupted && observationKnown(path), unbound ? unboundReason : "会话关联依赖"));
            }
            return new(Version, scopes);
        }
    }
    private static string? NormalizeProjectRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl)) return null;
        try { return ExperimentSandbox.Normalize(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or ExperimentRefusedException) { return null; }
    }
    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedSessions(1, checked(Version + 1), sessions));
        if (bytes.Length > 2 * 1024 * 1024) throw new IOException("会话配置达到 2 MiB 上限。");
        var space = VolumeSpaceReader.Read(root);
        if (!RuntimeBudget.Evaluate(RuntimeBudget.Measure(root), (long)space.UserAvailableBytes, true, bytes.Length, (long)space.TotalBytes).Collect)
            throw new IOException("空间预算不足，未保存会话变更。");
        var temporary = file + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, file, overwrite: true);
        Version++;
    }
    public void Dispose() => owner.Dispose();
    private sealed record SavedSessions(int Schema, long Version, List<DevelopmentSession> Sessions);
}

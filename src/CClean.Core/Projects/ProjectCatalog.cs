using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Sessions;

namespace CClean.Core.Projects;

public enum ProjectModuleKind { Registered, CodeProject, ToolWorkspace }

public sealed record RelatedScopeDescription(Guid ScopeId, string Name, string Path, string Evidence, bool IsCurrent, bool Paused)
{
    public string Status => !IsCurrent ? "原范围已调整或移除，需重新确认关联" :
        Paused ? "已确认关联 · 观察暂停 · 不证明独占" : "已确认关联 · 不证明独占或删除资格";
}

public sealed record ProjectModule(string Name, string Root, Guid? RegistrationId, string Tool, int SessionCount, bool Paused)
{
    public ProjectModuleKind Kind { get; init; } = RegistrationId is null ? ProjectModuleKind.ToolWorkspace : ProjectModuleKind.Registered;
    public string? ParentRoot { get; init; }
    public bool HasMonitoredDescendants { get; init; }
    public long Revision { get; init; } = 1;
    public long ScopeRevision { get; init; } = 1;
    public string? ObservationKey { get; init; } = RegistrationId?.ToString();
    public string? AccessProblem { get; init; }
    public RelatedScopeDescription[] RelatedScopes { get; init; } = [];
    public string KindLabel => Kind switch { ProjectModuleKind.Registered => "已登记项目", ProjectModuleKind.CodeProject => "代码项目", _ => "工具工作目录" };
    public string Status => RegistrationId is not null ? AccessProblem is not null ? "已登记 · 不可访问，当前状态未知" :
        Paused ? "已登记 · 监测暂停" : "已登记 · 监测范围" :
        HasMonitoredDescendants ? $"{KindLabel} · 部分子目录已监测" : $"{KindLabel} · 尚未监测";
    public string SessionLabel => $"{SessionCount} 个工具会话";
}

public static class ProjectCatalog
{
    public static IReadOnlyList<ProjectModule> Build(IEnumerable<RegisteredProject> registered, IEnumerable<DiscoveredSession> discovered,
        IEnumerable<string>? discoveredRoots = null)
    {
        // Registered data roots are not code-discovery candidates: .codex/target and offline roots remain visible.
        var projects = registered.Where(p => ValidRegisteredRoot(p.Root)).Select(p => p with { Root = Normalize(p.Root) }).ToArray();
        var access = projects.ToDictionary(p => p.Id, p => AccessProblem(p.Root));
        var sessions = discovered.Where(s => ValidRoot(s.ProjectRoot)).Select(s => s with { ProjectRoot = Normalize(s.ProjectRoot) }).ToArray();
        var explicitRoots = (discoveredRoots ?? []).Where(ValidRoot).Select(Normalize).Where(r => !ProjectDiscovery.IsContainerRoot(r))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = projects.Select(p => p.Root).Concat(sessions.Select(s => s.ProjectRoot)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(ProjectDiscovery.HasProjectMarker).ToArray();
        // Separate explicit scans remain distinct (including nested version folders); inferred source roots can be grouped.
        var codeRoots = explicitRoots.Concat(candidates.Where(root => !explicitRoots.Any(parent => ProtectionPolicy.Contains(parent, root)) &&
            !candidates.Any(parent => !parent.Equals(root, StringComparison.OrdinalIgnoreCase) && ProtectionPolicy.Contains(parent, root))))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var anchors = projects.Select(p => p.Root).Where(r => !ProjectDiscovery.IsContainerRoot(r)).Concat(codeRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var roots = projects.Select(p => p.Root).Concat(codeRoots).Concat(sessions.Select(s => s.ProjectRoot)
            .Where(root => !anchors.Any(parent => ProtectionPolicy.Contains(parent, root)))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return roots
            .Select(root =>
            {
                var project = projects.Where(p => ProtectionPolicy.Contains(p.Root, root)).OrderByDescending(p => p.Root.Length).FirstOrDefault();
                var kind = project is not null ? ProjectModuleKind.Registered : codeRoots.Contains(root, StringComparer.OrdinalIgnoreCase)
                    ? ProjectModuleKind.CodeProject : ProjectModuleKind.ToolWorkspace;
                var related = sessions.Where(s => IncludesSession(root, kind, s.ProjectRoot)).ToArray();
                return new ProjectModule(project?.Root.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? project.Name : Path.GetFileName(root),
                    root, project?.Id, project?.Tool ?? related.FirstOrDefault()?.Tool ?? "通用工具", related.Length, project?.Paused ?? false)
                {
                    Kind = kind,
                    Revision = project?.Revision ?? 1,
                    ScopeRevision = project?.ScopeRevision ?? 1,
                    ObservationKey = project?.ObservationKey,
                    AccessProblem = project is null ? null : access[project.Id],
                    RelatedScopes = project is null ? [] : project.RelatedScopes.Select(reference =>
                    {
                        var scope = projects.FirstOrDefault(p => p.Id == reference.ScopeId);
                        return new RelatedScopeDescription(reference.ScopeId, scope?.Name ?? "已移除范围", reference.Path, reference.Evidence,
                            scope is not null && scope.ScopeRevision == reference.ScopeRevision && ProtectionPolicy.Contains(scope.Root, reference.Path),
                            scope?.Paused ?? true);
                    }).ToArray(),
                    ParentRoot = anchors.Where(parent => !parent.Equals(root, StringComparison.OrdinalIgnoreCase) && ProtectionPolicy.Contains(parent, root))
                        .OrderByDescending(parent => parent.Length).FirstOrDefault(),
                    HasMonitoredDescendants = project is null && !ProjectDiscovery.IsContainerRoot(root) && projects.Any(p => ProtectionPolicy.Contains(root, p.Root))
                };
            }).OrderByDescending(p => p.RegistrationId.HasValue).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static bool IncludesSession(ProjectModule project, DiscoveredSession session) => IncludesSession(project.Root, project.Kind, session.ProjectRoot);
    private static bool IncludesSession(string root, ProjectModuleKind kind, string sessionRoot)
    {
        if (!Path.IsPathFullyQualified(sessionRoot)) return false;
        try
        {
            return Normalize(root).Equals(Normalize(sessionRoot), StringComparison.OrdinalIgnoreCase) ||
                (kind != ProjectModuleKind.ToolWorkspace && !ProjectDiscovery.IsContainerRoot(root) && ProtectionPolicy.Contains(root, sessionRoot));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool ValidRegisteredRoot(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && !path.Any(char.IsControl) &&
                !Normalize(path).Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase) &&
                !Monitoring.SpaceSurvey.IsSystemPath(ExperimentSandbox.Normalize(path));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or ExperimentRefusedException) { return false; }
    }
    private static string? AccessProblem(string path)
    {
        try
        {
            using var lease = new DirectoryLease(path);
            return Monitoring.SpaceSurvey.IsSystemPath(path, checkAncestors: true) ? "当前路径属性属于系统范围，不建立观察。" : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or ExperimentRefusedException)
        { return error.Message; }
    }
    private static bool ValidRoot(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || Normalize(path).Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase) ||
                ProjectDiscovery.IsExcludedPath(path, checkAncestors: true)) return false;
            using var lease = new DirectoryLease(ExperimentSandbox.Normalize(path));
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or ExperimentRefusedException) { return false; }
    }

}

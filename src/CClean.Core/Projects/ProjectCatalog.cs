using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Sessions;

namespace CClean.Core.Projects;

public enum ProjectModuleKind { Registered, CodeProject, ToolWorkspace }

public sealed record ProjectModule(string Name, string Root, Guid? RegistrationId, string Tool, int SessionCount, bool Paused)
{
    public ProjectModuleKind Kind { get; init; } = RegistrationId is null ? ProjectModuleKind.ToolWorkspace : ProjectModuleKind.Registered;
    public string? ParentRoot { get; init; }
    public bool HasMonitoredDescendants { get; init; }
    public string KindLabel => Kind switch { ProjectModuleKind.Registered => "已登记项目", ProjectModuleKind.CodeProject => "代码项目", _ => "工具工作目录" };
    public string Status => RegistrationId is not null ? Paused ? "已登记 · 监测暂停" : "已登记 · 监测范围" :
        HasMonitoredDescendants ? $"{KindLabel} · 部分子目录已监测" : $"{KindLabel} · 尚未监测";
    public string SessionLabel => $"{SessionCount} 个工具会话";
}

public static class ProjectCatalog
{
    public static IReadOnlyList<ProjectModule> Build(IEnumerable<RegisteredProject> registered, IEnumerable<DiscoveredSession> discovered,
        IEnumerable<string>? discoveredRoots = null)
    {
        var projects = registered.Where(p => ValidRoot(p.Root)).Select(p => p with { Root = Normalize(p.Root) }).ToArray();
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

using CClean.Core.Cleanup;
using CClean.Core.Sessions;

namespace CClean.Core.Projects;

public sealed record ProjectModule(string Name, string Root, Guid? RegistrationId, string Tool, int SessionCount, bool Paused)
{
    public string Status => RegistrationId is null ? "已发现 · 尚未监测" : Paused ? "已登记 · 监测暂停" : "已登记 · 监测范围";
    public string SessionLabel => $"{SessionCount} 个工具会话";
}

public static class ProjectCatalog
{
    public static IReadOnlyList<ProjectModule> Build(IEnumerable<RegisteredProject> registered, IEnumerable<DiscoveredSession> discovered)
    {
        var projects = registered.Where(p => ValidRoot(p.Root)).ToArray();
        var sessions = discovered.Where(s => ValidRoot(s.ProjectRoot)).ToArray();
        var roots = projects.Select(p => p.Root).Concat(sessions.Select(s => s.ProjectRoot))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase);
        return roots.Where(r => !Monitoring.SpaceSurvey.IsSystemPath(r) && !r.Equals(Path.GetPathRoot(r), StringComparison.OrdinalIgnoreCase))
            .Select(root =>
            {
                var project = projects.Where(p => ProtectionPolicy.Contains(p.Root, root)).OrderByDescending(p => p.Root.Length).FirstOrDefault();
                var related = sessions.Where(s => ProtectionPolicy.Contains(root, s.ProjectRoot)).ToArray();
                return new ProjectModule(project?.Root.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? project.Name : Path.GetFileName(root),
                    root, project?.Id, project?.Tool ?? related.FirstOrDefault()?.Tool ?? "通用工具", related.Length, project?.Paused ?? false);
            }).OrderByDescending(p => p.RegistrationId.HasValue).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static bool ValidRoot(string path)
    {
        try { return Path.IsPathFullyQualified(path) && !Monitoring.SpaceSurvey.IsSystemPath(path, checkAncestors: true); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

}

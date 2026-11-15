using System.Text.Json;
using CClean.Core.Experiments;
using CClean.Core.Tools;

namespace CClean.Core.Projects;

public sealed record ScopeReference(Guid ScopeId, string Path, string Evidence, long ScopeRevision = 1);
public sealed record RegisteredProject(Guid Id, string Name, string Root, string Tool, bool Paused = false)
{
    public long Revision { get; init; } = 1;
    public long ScopeRevision { get; init; } = 1;
    public ScopeReference[] RelatedScopes { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string ObservationKey => ScopeRevision == 1 ? Id.ToString() : $"{Id:D}@{ScopeRevision}";
}

public sealed class ProjectRegistry
{
    private readonly string file;
    private readonly string dataRoot;
    private readonly object sync = new();
    private List<RegisteredProject> projects = [];
    public IReadOnlyList<RegisteredProject> Projects { get { lock (sync) return projects.ToArray(); } }
    public ProjectRegistry(string dataDirectory)
    {
        dataRoot = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        file = Path.Combine(dataDirectory, "projects.json");
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 1024 * 1024) throw new IOException("Project configuration exceeds limit.");
            projects = (JsonSerializer.Deserialize<List<RegisteredProject>>(File.ReadAllText(file)) ?? [])
                .Select(p => p with { Root = NormalizeRoot(p.Root), Tool = KnownTools.NormalizeLabel(p.Tool) }).ToList();
            if (projects.Count > 64 || projects.Select(p => p.Id).Distinct().Count() != projects.Count)
                throw new IOException("登记根数量超限或标识重复。");
            foreach (var project in projects)
            {
                if (project.Id == Guid.Empty || project.Revision < 1 || project.ScopeRevision < 1 || project.ScopeRevision > project.Revision ||
                    project.RelatedScopes is null || project.RelatedScopes.Length > 64)
                    throw new IOException("登记范围修订或关联格式无效。");
                ValidateRootBoundary(project.Root, project.Id);
                if (project.RelatedScopes.GroupBy(r => (r.ScopeId, Path.GetFullPath(r.Path).ToUpperInvariant())).Any(g => g.Count() > 1))
                    throw new IOException("关联范围重复。");
                foreach (var reference in project.RelatedScopes)
                {
                    var scope = projects.FirstOrDefault(p => p.Id == reference.ScopeId);
                    if (reference.ScopeId == project.Id || scope is null || reference.ScopeRevision < 1 || reference.ScopeRevision > scope.ScopeRevision)
                        throw new IOException("关联范围不存在或修订无效。");
                    var path = NormalizeRoot(reference.Path); NormalizeText(reference.Evidence, 240, "关联依据");
                    if (reference.ScopeRevision == scope.ScopeRevision && !Contains(scope.Root, path))
                        throw new IOException("关联路径超出已登记范围。");
                }
            }
        }
    }

    public RegisteredProject Register(string name, string root, string tool)
    {
        lock (sync)
        {
            name = NormalizeText(name, 120, "项目名称"); tool = KnownTools.NormalizeLabel(tool); root = NormalizeRoot(root);
            ValidateRootBoundary(root);
            using var lease = OpenRoot(root);
            if (projects.Count >= 64) throw new InvalidOperationException("最多登记 64 个物理根目录；共享范围请关联已有根。");
            var project = new RegisteredProject(Guid.NewGuid(), name, root, tool);
            Commit([.. projects, project]);
            return project;
        }
    }

    public RegisteredProject Update(Guid id, string name, string root, string tool, long expectedRevision)
    {
        lock (sync)
        {
            var previous = Require(id, expectedRevision);
            name = NormalizeText(name, 120, "项目名称"); tool = KnownTools.NormalizeLabel(tool); root = NormalizeRoot(root);
            ValidateRootBoundary(root, id);
            var changedRoot = !previous.Root.Equals(root, StringComparison.OrdinalIgnoreCase);
            using var lease = changedRoot ? OpenRoot(root) : null;
            if (!changedRoot && previous.Name == name && previous.Tool == tool) return previous;
            return Replace(previous with { Name = name, Root = root, Tool = tool, Revision = checked(previous.Revision + 1),
                ScopeRevision = changedRoot ? checked(previous.ScopeRevision + 1) : previous.ScopeRevision });
        }
    }

    public RegisteredProject SetPaused(Guid id, bool paused, long? expectedRevision = null)
    {
        lock (sync)
        {
            var previous = Require(id, expectedRevision);
            return previous.Paused == paused ? previous : Replace(previous with { Paused = paused, Revision = checked(previous.Revision + 1) });
        }
    }

    public RegisteredProject Remove(Guid id, long? expectedRevision = null)
    {
        lock (sync)
        {
            var previous = Require(id, expectedRevision);
            Commit(projects.Where(p => p.Id != id).Select(p => p.RelatedScopes.Any(r => r.ScopeId == id)
                ? p with { RelatedScopes = p.RelatedScopes.Where(r => r.ScopeId != id).ToArray(), Revision = checked(p.Revision + 1) } : p).ToList());
            return previous;
        }
    }

    public RegisteredProject LinkScope(Guid projectId, Guid scopeId, string path, string evidence, long? expectedRevision = null)
    {
        lock (sync)
        {
            var project = Require(projectId, expectedRevision); var scope = Require(scopeId);
            if (projectId == scopeId) throw new ArgumentException("不能将范围关联到自身。");
            path = NormalizeRoot(path); evidence = NormalizeText(evidence, 240, "关联依据");
            if (!Contains(scope.Root, path)) throw new ArgumentException("关联子范围必须位于所选已登记物理根内。");
            using var lease = OpenRoot(path);
            var references = project.RelatedScopes.Where(r => r.ScopeId != scopeId || !r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (references.Length >= 64) throw new InvalidOperationException("每个项目最多关联 64 个范围。");
            var reference = new ScopeReference(scopeId, path, evidence, scope.ScopeRevision);
            if (project.RelatedScopes.Contains(reference)) return project;
            return Replace(project with { RelatedScopes = [.. references, reference], Revision = checked(project.Revision + 1) });
        }
    }

    public RegisteredProject UnlinkScope(Guid projectId, Guid scopeId, string path, long? expectedRevision = null)
    {
        lock (sync)
        {
            var project = Require(projectId, expectedRevision); path = NormalizeRoot(path);
            var references = project.RelatedScopes.Where(r => r.ScopeId != scopeId || !r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            return references.Length == project.RelatedScopes.Length ? project :
                Replace(project with { RelatedScopes = references, Revision = checked(project.Revision + 1) });
        }
    }

    public bool IsCurrent(Guid id, long revision)
    {
        lock (sync) return projects.Any(p => p.Id == id && p.Revision == revision);
    }

    public RegisteredProject? CoveringScope(string path)
    {
        path = NormalizeRoot(path);
        lock (sync) return projects.FirstOrDefault(p => Contains(p.Root, path));
    }

    private RegisteredProject Require(Guid id, long? expectedRevision = null)
    {
        var project = projects.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("范围不存在或已移除。");
        if (expectedRevision is not null && project.Revision != expectedRevision)
            throw new InvalidOperationException("范围已改变，请刷新后重新确认。");
        return project;
    }

    private RegisteredProject Replace(RegisteredProject project)
    {
        Commit(projects.Select(p => p.Id == project.Id ? project : p).ToList());
        return project;
    }

    private void ValidateRootBoundary(string root, Guid? exceptId = null)
    {
        if (Overlaps(root, dataRoot)) throw new InvalidOperationException("监测范围不能包含应用自身索引目录，请选择更具体的范围。");
        if (root.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase) ||
            Monitoring.SpaceSurvey.IsSystemPath(root))
            throw new InvalidOperationException("系统范围或整个磁盘不能登记监测，请选择具体目录。");
        if (projects.Any(p => p.Id != exceptId && Overlaps(p.Root, root)))
            throw new InvalidOperationException("该范围已登记或与现有目录重叠；请关联已有范围，不重复创建观察器。");
    }

    private static string NormalizeRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.Any(char.IsControl)) throw new ArgumentException("请选择完整的本地目录路径。");
        return ExperimentSandbox.Normalize(root);
    }

    private static DirectoryLease OpenRoot(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录不存在或不可访问。");
        if (Monitoring.SpaceSurvey.IsSystemPath(root, checkAncestors: true)) throw new InvalidOperationException("系统范围不能登记或关联。");
        return new DirectoryLease(root);
    }

    private static string NormalizeText(string text, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text.Trim().Length > maximum)
            throw new ArgumentException($"{label}须为 1–{maximum} 字符，不能包含控制字符。");
        return text.Trim();
    }

    public static bool Overlaps(string a, string b)
    {
        a = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar);
        b = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string root, string path) => root.Equals(path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void Commit(List<RegisteredProject> next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        if (bytes.Length > 1024 * 1024) throw new IOException("登记配置超过 1 MiB 上限，未保存变更。");
        var temporary = file + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, file, overwrite: true);
        projects = next;
    }
}

using System.Text.Json;
using CClean.Core.Experiments;

namespace CClean.Core.Projects;

public sealed record RegisteredProject(Guid Id, string Name, string Root, string Tool, bool Paused = false);

public sealed class ProjectRegistry
{
    private readonly string file;
    private readonly string dataRoot;
    public List<RegisteredProject> Projects { get; } = [];
    public ProjectRegistry(string dataDirectory)
    {
        dataRoot = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        file = Path.Combine(dataDirectory, "projects.json");
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 1024 * 1024) throw new IOException("Project configuration exceeds limit.");
            Projects.AddRange(JsonSerializer.Deserialize<List<RegisteredProject>>(File.ReadAllText(file)) ?? []);
            if (Projects.Count > 64) throw new IOException("Too many registered roots.");
            if (Projects.Any(p => Overlaps(p.Root, dataRoot))) throw new IOException("监测范围与应用数据目录重叠，需调整配置以避免自我监测。");
        }
    }

    public RegisteredProject Register(string name, string root, string tool)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入项目名称。");
        root = ExperimentSandbox.Normalize(root);
        if (Overlaps(root, dataRoot)) throw new InvalidOperationException("监测范围不能包含应用自身索引目录，请选择更具体的项目范围。");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录不存在。");
        using var lease = new DirectoryLease(root);
        if (Projects.Any(p => Overlaps(p.Root, root))) throw new InvalidOperationException("该范围已登记或与现有目录重叠；请使用已有范围。");
        if (Projects.Count >= 64) throw new InvalidOperationException("最多登记 64 个根目录。");
        if (tool is not ("Codex" or "Claude Code" or "ZCode" or "通用工具")) throw new ArgumentException("未知工具标签。");
        var project = new RegisteredProject(Guid.NewGuid(), name.Trim(), root, tool);
        Projects.Add(project);
        try { Save(); } catch { Projects.Remove(project); throw; }
        return project;
    }

    public void SetPaused(Guid id, bool paused)
    {
        var index = Projects.FindIndex(p => p.Id == id);
        if (index < 0) throw new ArgumentException("项目不存在。");
        var previous = Projects[index];
        Projects[index] = previous with { Paused = paused };
        try { Save(); } catch { Projects[index] = previous; throw; }
    }

    public static bool Overlaps(string a, string b)
    {
        a = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar);
        b = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void Save()
    {
        var temporary = file + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, Projects);
            stream.Flush(true);
        }
        File.Move(temporary, file, overwrite: true);
    }
}

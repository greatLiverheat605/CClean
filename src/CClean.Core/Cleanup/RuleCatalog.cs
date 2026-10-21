using System.Text.Json;
using CClean.Core.Experiments;
using CClean.Core.Storage;

namespace CClean.Core.Cleanup;

public sealed record RuleScope(string Root, string RuleId, int RuleVersion, string ToolVersion, bool Enabled, bool Exclusive);

public sealed class RuleCatalog
{
    public const int Version = 1;
    private readonly string file, dataRoot;
    private List<RuleScope> scopes = [];
    public long ConfigurationVersion { get; private set; }
    public IReadOnlyList<RuleScope> Scopes => scopes.ToArray();
    public RuleCatalog(string dataRoot)
    {
        this.dataRoot = dataRoot; file = Path.Combine(dataRoot, "cleanup-rules.json");
        if (!File.Exists(file)) return;
        if (new FileInfo(file).Length > 65536) throw new IOException("清理规则配置超过 64 KiB。");
        var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) ?? throw new IOException("清理规则配置无效。");
        if (saved.Schema != Version || saved.Scopes.Count > 64) throw new IOException("清理规则版本或数量不支持。");
        scopes = saved.Scopes; ConfigurationVersion = saved.Revision;
        // Loading never silently upgrades or enables a rule.
    }
    public void ConfigureNpm(string root, string toolVersion, bool enabled, bool exclusive)
    {
        root = ExperimentSandbox.Normalize(root);
        using var lease = new DirectoryLease(root);
        var broadRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm") };
        if (ProtectionPolicy.IsSensitiveRoot(root) || broadRoots.Any(r => r.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) || Projects.ProjectRegistry.Overlaps(root, dataRoot))
            throw new ArgumentException("不能把系统、应用数据或敏感目录配置为缓存根。");
        if (enabled && (!exclusive || toolVersion != CacheRule.NpmContent.SupportedVersion))
            throw new ArgumentException("启用需要确认独占根及已验证的 npm 11.19.0 版本。");
        if (scopes.Any(s => !s.Root.Equals(root, StringComparison.OrdinalIgnoreCase) && Projects.ProjectRegistry.Overlaps(s.Root, root)))
            throw new ArgumentException("缓存规则根不能重叠。");
        var next = scopes.Where(s => !s.Root.Equals(root, StringComparison.OrdinalIgnoreCase)).ToList();
        next.Add(new(root, CacheRule.NpmContent.Id, CacheRule.NpmContent.Version, toolVersion, enabled, exclusive));
        if (next.Count > 64) throw new IOException("最多配置 64 个缓存根。");
        var revision = checked(ConfigurationVersion + 1);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(Version, revision, next));
        var space = VolumeSpaceReader.Read(dataRoot);
        if (bytes.Length > 65536 || !RuntimeBudget.Evaluate(RuntimeBudget.Measure(dataRoot), (long)space.UserAvailableBytes, true, bytes.Length, (long)space.TotalBytes).Collect)
            throw new IOException("清理规则配置预算不足。");
        var temporary = file + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, file, true); scopes = next; ConfigurationVersion = revision;
    }
    private sealed record Saved(int Schema, long Revision, List<RuleScope> Scopes);
}

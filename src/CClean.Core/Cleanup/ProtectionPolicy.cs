using CClean.Core.Files;
using CClean.Core.Sessions;

namespace CClean.Core.Cleanup;

public sealed record ProtectionDecision(bool Eligible, string Category, string Reason, RuleScope? Scope = null);

public static class ProtectionPolicy
{
    public const int Version = 2;
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> sensitiveParts = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".codex", ".claude", ".zcode", ".ssh", ".aws", ".azure", ".gnupg", "credentials", "sessions", "checkpoints", "memories" };
    private static readonly HashSet<string> sourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".sln", ".slnx", ".js", ".ts", ".tsx", ".jsx", ".py", ".rs", ".go", ".c", ".cpp", ".h", ".java", ".kt", ".swift", ".vue", ".svelte", ".html", ".css", ".scss", ".sql", ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".xml", ".lock", ".pem", ".key", ".pfx", ".db", ".sqlite", ".vhd", ".vhdx", ".docx", ".xlsx", ".pdf" };
    public static bool Contains(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        path = Path.GetFullPath(path);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsSensitiveRoot(string root)
    {
        root = Path.GetFullPath(root);
        var systemRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) };
        return root.TrimEnd('\\').Equals(Path.GetPathRoot(root)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            systemRoots.Where(r => r.Length > 0).Any(r => Contains(r, root)) ||
            root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(sensitiveParts.Contains);
    }
    public static ProtectionDecision Classify(FileSnapshot file, IReadOnlyList<RuleScope> rules, ActivitySnapshot activity,
        IReadOnlyList<string> protectedRoots, DateTimeOffset now)
    {
        ProtectionDecision Deny(string category, string reason) => new(false, category, reason);
        if (file.Identity is null || file.LogicalBytes is null || file.AllocatedBytes is null || !file.IsOrdinaryFile ||
            !file.StreamsComplete || file.HasNamedStreams || file.LinkCount != 1 ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.SparseFile | FileAttributes.Compressed | FileAttributes.Offline | FileAttributes.System | FileAttributes.Encrypted)) != 0)
            return Deny("特殊或未知", file.Exclusion ?? "身份、数据流或普通文件条件不满足");
        try
        {
            if (IsSensitiveRoot(file.Path) || protectedRoots.Any(r => Contains(r, file.Path))) return Deny("受保护", "系统、Agent 持久数据或显式保护范围");
            var name = Path.GetFileName(file.Path);
            if (sourceExtensions.Contains(Path.GetExtension(name)) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                name is "Dockerfile" or "Makefile" || name.EndsWith("rc", StringComparison.OrdinalIgnoreCase)) return Deny("源码或用户数据", "源码、配置、文档或凭据默认保护");
            var matching = rules.Where(s => Contains(s.Root, file.Path)).ToArray();
            if (matching.Length != 1) return Deny("未知来源", "未匹配唯一已配置规则");
            var scope = matching[0];
            var crashDump = scope.RuleId == CrashDumpRule.Id && scope.RuleVersion == CrashDumpRule.Version;
            if (!crashDump && (scope.RuleId != CacheRule.NpmContent.Id || scope.RuleVersion != CacheRule.NpmContent.Version))
                return Deny("未知规则", "规则版本未经验证");
            var related = activity.Scopes.Where(s => Contains(s.Root, file.Path)).ToArray();
            if (related.Length == 0 || related.Any(s => !s.ObservationKnown)) return Deny("活动未知", "没有完整的当前活动观察");
            if (related.Any(s => s.Active)) return Deny("活动保护", "活动项目、会话或近期写入");
            if (DateTime.FromFileTimeUtc(file.ModifiedTime) > now.UtcDateTime - MinimumAge) return Deny("近期变化", "至少保留 30 分钟，未来时间也排除");
            if (crashDump)
            {
                var exclusion = CrashDumpRule.Exclude(scope, file, now);
                return exclusion is not null ? Deny("转储保护", exclusion) : new(true, "旧故障转储", "超过 48 小时；保留最新转储，删除后失去该次故障诊断内容", scope);
            }
            var reason = CacheRule.NpmContent.Exclude(new(scope.Root, scope.ToolVersion, scope.Enabled, scope.Exclusive, true, false, protectedRoots), file);
            return reason is not null ? Deny("规则排除", reason) : new(true, "独占可重建缓存", "npm 11.19.0 已验证规则；仍须预览与逐项执行复核", scope);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return Deny("未知来源", "路径或时间无法可靠判断"); }
    }
}

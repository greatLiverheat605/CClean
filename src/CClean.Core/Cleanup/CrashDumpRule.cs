using CClean.Core.Experiments;
using CClean.Core.Files;

namespace CClean.Core.Cleanup;

public static class CrashDumpRule
{
    public const string Id = "wsl-host-crash-dumps";
    public const int Version = 1;
    public static string Root => Path.Combine(Path.GetTempPath(), "wsl-crashes");
    public static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    public static bool MatchesName(string path) => Path.GetFileName(path).StartsWith("wsl-crash-", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(path).Equals(".dmp", StringComparison.OrdinalIgnoreCase);

    public static string[] GetPaths() => Paths(Root);
    private static string[] Paths(string root)
    {
        using var lease = new DirectoryLease(root);
        var paths = Directory.EnumerateFiles(root).Take(1001).ToArray();
        if (paths.Length > 1000) throw new IOException("转储目录超过 1000 项，请先缩小范围。");
        return paths.Where(MatchesName).ToArray();
    }

    public static CleanupContext Context(IReadOnlyList<string> protectedRoots) => Context(Root, protectedRoots);
    internal static CleanupContext Context(string root, IReadOnlyList<string> protectedRoots)
    {
        if (!ValidRoot(root)) throw new IOException("不是当前用户临时目录中的 wsl-crashes 目录。");
        var snapshots = Paths(root).Select(WindowsFileMetadata.Read).ToArray();
        // An unreadable entry might be the newest dump: stop instead of guessing retention.
        if (snapshots.Any(f => f.Identity is null || f.ModifiedTime == 0)) throw new IOException("转储元数据不完整，无法确认需保留的最新文件。");
        var newest = snapshots.Length == 0 ? 0 : snapshots.Max(f => f.ModifiedTime);
        var preserved = protectedRoots.Concat(snapshots.Where(f => f.ModifiedTime == newest).Select(f => f.Path)).ToArray();
        return new(Version, [new(root, Id, Version, "host-dump-v1", true, true)],
            new(Version, [new(root, false, true, "仅已完成的旧故障转储；执行时禁止并发写入")]), preserved);
    }

    private static bool ValidRoot(string root) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals("wsl-crashes", StringComparison.OrdinalIgnoreCase) &&
        ProtectionPolicy.Contains(Path.GetTempPath(), root);

    public static string? Exclude(RuleScope scope, FileSnapshot file, DateTimeOffset now)
    {
        if (!scope.Enabled || !scope.Exclusive || scope.RuleVersion != Version || scope.ToolVersion != "host-dump-v1") return "规则未启用或版本不受支持";
        if (!ValidRoot(scope.Root) || !Path.GetDirectoryName(file.Path)!.Equals(Path.TrimEndingDirectorySeparator(scope.Root), StringComparison.OrdinalIgnoreCase) || !MatchesName(file.Path))
            return "只支持临时目录 wsl-crashes 中的直接子文件 wsl-crash-*.dmp";
        if (DateTime.FromFileTimeUtc(file.ModifiedTime) > now.UtcDateTime - Retention) return "保留最近 48 小时的转储";
        return null;
    }
}

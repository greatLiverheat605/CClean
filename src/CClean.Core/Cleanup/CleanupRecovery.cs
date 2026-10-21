using CClean.Core.Files;

namespace CClean.Core.Cleanup;

public static class CleanupRecovery
{
    public static IReadOnlyList<CleanupEvent> Inspect(CleanupJournal journal, string run)
    {
        var events = journal.Events(run);
        return journal.Manifest(run).Take(1000).Where(i => i.Decision.Eligible).Select(item =>
        {
            var last = events.LastOrDefault(e => e.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
            if (last?.State == "Deleted") return last;
            var current = WindowsFileMetadata.Read(item.Path);
            // Read failure is unknown: File.Exists(false) alone does not prove absence.
            var reason = current.Identity is null ? "当前对象无法读取或缺失，不能确认删除来源或释放量" :
                current.Identity == item.File.Identity ? "原身份仍存在；重新预览后才能处理" : "路径现在是另一对象，不能沿用旧批准";
            return new CleanupEvent(item.Path, "RecoveryObservation", reason, DateTimeOffset.UtcNow.ToString("O"));
        }).ToArray();
    }
}

using CClean.Core.Files;

namespace CClean.Core.Cleanup;

public sealed record CleanupRecoveryPage(IReadOnlyList<CleanupEvent> Items, int NextOrdinal, int TotalItems, int CheckedItems,
    long ThroughSequence, bool HasMore, bool RunFound)
{
    public bool IsComplete => !HasMore;
}

public static class CleanupRecovery
{
    public static IReadOnlyList<CleanupEvent> Inspect(CleanupJournal journal, string run) => Inspect(journal, run, CancellationToken.None);

    public static IReadOnlyList<CleanupEvent> Inspect(CleanupJournal journal, string run, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var items = journal.Manifest(run, cancellation);
        var window = journal.EventWindow(run, 0, null, cancellation);
        // One history pass and a path dictionary replace the per-item linear LastOrDefault lookup.
        var latest = journal.LatestEvents(run, items.Where(i => i.Decision.Eligible).Select(i => i.Path), window.ThroughSequence, cancellation);
        var observations = new List<CleanupEvent>();
        foreach (var item in items)
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Decision.Eligible) observations.Add(Observe(item, latest, cancellation));
        }
        return observations.AsReadOnly();
    }

    public static CleanupRecoveryPage InspectPage(CleanupJournal journal, string run, int afterOrdinal = -1, int pageSize = 200,
        long? throughSequence = null, CancellationToken cancellation = default)
    {
        if (afterOrdinal >= 0 && throughSequence is null) throw new ArgumentException("恢复核查后续页必须保留首轮事件快照上界。", nameof(throughSequence));
        var page = journal.ManifestPage(run, afterOrdinal, pageSize, cancellation);
        var window = journal.EventWindow(run, 0, throughSequence, cancellation);
        var latest = journal.LatestEvents(run, page.Items.Where(i => i.Decision.Eligible).Select(i => i.Path), window.ThroughSequence, cancellation);
        var observations = new List<CleanupEvent>();
        foreach (var item in page.Items)
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Decision.Eligible) observations.Add(Observe(item, latest, cancellation));
        }
        return new(observations.AsReadOnly(), page.NextOrdinal, page.TotalItems, page.Items.Count, window.ThroughSequence, page.HasMore, page.RunFound);
    }

    private static CleanupEvent Observe(CleanupItem item, IReadOnlyDictionary<string, CleanupEvent> latest, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (latest.TryGetValue(item.Path, out var last) && last.State == "Deleted") return last;
        var current = WindowsFileMetadata.Read(item.Path);
        cancellation.ThrowIfCancellationRequested();
        // Read failure is unknown: File.Exists(false) alone does not prove absence.
        var reason = current.Identity is null ? "当前对象无法读取或缺失，不能确认删除来源或释放量" :
            current.Identity == item.File.Identity ? "原身份仍存在；重新预览后才能处理" : "路径现在是另一对象，不能沿用旧批准";
        return new(item.Path, "RecoveryObservation", reason, DateTimeOffset.UtcNow.ToString("O"));
    }
}

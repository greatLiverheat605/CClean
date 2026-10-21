using CClean.Core.Attribution;
using CClean.Core.Sessions;
using CClean.Core.Storage;

namespace CClean.Core.History;

public sealed record TimelineNode(DateTimeOffset Time, string Project, string Directory, string Kind, AttributionEvidence Evidence,
    long? NetBytes, IReadOnlyList<StoredChange> Files)
{
    public string LocalTime => Time.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string EvidenceLabel => Evidence.Label;
    public string NetDisplay => NetBytes is null ? "未知" : $"{NetBytes:+#,##0;-#,##0;0} B";
    public string Summary => $"{Files.Count} 条观察；{Kind}；净变化 {NetDisplay}";
}
public sealed record TimelineResult(IReadOnlyList<TimelineNode> Nodes, bool Truncated, DateTimeOffset EarliestAvailable,
    IReadOnlyList<CoverageGap> Gaps);

public static class TimelineQuery
{
    public static TimelineResult Build(IReadOnlyList<StoredChange> changes, IReadOnlyList<DevelopmentSession> sessions,
        IReadOnlyList<CoverageGap> gaps, DateTimeOffset since, DateTimeOffset until, DateTimeOffset earliestAvailable,
        string? project = null, string? tool = null, EvidenceKind? evidence = null, int bucketSeconds = 60, int maxNodes = 500,
        bool inputTruncated = false)
    {
        if (until < since || bucketSeconds < 1 || maxNodes is < 1 or > 500) throw new ArgumentException("无效的时间线查询范围。");
        var inRange = changes.Where(c => c.Time >= since && c.Time <= until).OrderBy(c => c.Time).ThenBy(c => c.Id).ToArray();
        var objects = inRange.GroupBy(c => (Bucket: c.Time.ToUnixTimeSeconds() / bucketSeconds,
            Identity: c.Identity ?? c.ProjectId + ":" + c.Path));
        var nodes = new List<TimelineNode>();
        var gapTimes = gaps.Select(g => (g.ProjectId, g.Time)).ToHashSet();
        foreach (var file in objects)
        {
            var first = file.First(); var last = file.Last();
            var incomplete = file.Any(c => gapTimes.Contains((c.ProjectId, c.Time)));
            var classifications = file.Select(c => AttributionRules.Classify(c, sessions, incomplete)).ToArray();
            var related = classifications.SelectMany(a => a.Sessions).Distinct().ToArray();
            var projects = file.Select(c => c.ProjectId).Distinct().ToArray();
            var hasUnknown = file.Zip(classifications).Any(pair => pair.Second.Kind == EvidenceKind.Unknown && pair.First.Kind != "初始存量");
            var attributed = hasUnknown ? new AttributionEvidence(EvidenceKind.Unknown, related, "存在未知或覆盖缺口，不能确认归属") :
                related.Length > 1 || projects.Length > 1 ? new(EvidenceKind.Shared, related, "多个关联；全局只计一次") : classifications.Last();
            if (project is not null && !projects.Contains(project)) continue;
            if (tool is not null && !related.Any(id => sessions.Any(s => s.Id == id && s.Tool == tool))) continue;
            if (evidence is not null && attributed.Kind != evidence) continue;
            long? before = first.Kind == "初始存量" ? first.After : first.Kind == "新增" ? 0 : first.Before;
            long? after = last.Kind == "消失" ? 0 : last.After;
            long? delta = incomplete || before is null || after is null ? null : checked(after - before);
            var kind = file.All(c => c.Kind == "初始存量") ? "初始存量" : "观察到的变化";
            nodes.Add(new(DateTimeOffset.FromUnixTimeSeconds(file.Key.Bucket * bucketSeconds),
                attributed.Kind == EvidenceKind.Shared ? "共享" : attributed.Kind == EvidenceKind.Unknown ? "未知" : first.ProjectId,
                Path.GetDirectoryName(last.Path)!, kind, attributed, delta, file.ToArray()));
        }
        // Aggregate bounded identity deltas into directory/session lanes; never sum the same object's aliases.
        var aggregated = nodes.GroupBy(n => new { n.Time, n.Project, n.Directory, n.Kind, EvidenceKind = n.Evidence.Kind,
            Sessions = string.Join(",", n.Evidence.Sessions.Order()) }).Select(group => new TimelineNode(group.Key.Time,
                group.Key.Project, group.Key.Directory, group.Key.Kind,
                group.First().Evidence, group.Any(n => n.NetBytes is null) ? null : group.Sum(n => n.NetBytes),
                group.SelectMany(n => n.Files).ToArray())).OrderByDescending(n => n.Time).ToArray();
        return new(aggregated.Take(maxNodes).ToArray(), inputTruncated || aggregated.Length > maxNodes, earliestAvailable,
            gaps.Where(g => g.Time >= since && g.Time <= until && (project is null || g.ProjectId == project)).ToArray());
    }
}

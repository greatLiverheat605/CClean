using CClean.Core.Sessions;
using CClean.Core.Storage;

namespace CClean.Core.Attribution;

public enum EvidenceKind { ProcessConfirmed, Context, Shared, Unknown }
public sealed record AttributionEvidence(EvidenceKind Kind, IReadOnlyList<Guid> Sessions, string Reason)
{
    public string Label => Kind switch { EvidenceKind.ProcessConfirmed => "进程证实", EvidenceKind.Context => "上下文关联", EvidenceKind.Shared => "共享", _ => "未知" };
}

public static class AttributionRules
{
    public static AttributionEvidence Classify(StoredChange change, IReadOnlyList<DevelopmentSession> sessions, bool hasCoverageGap = false)
    {
        if (hasCoverageGap || change.Kind == "未知" || change.Identity is null)
            return new(EvidenceKind.Unknown, [], "观察不完整，无法确认来源");
        if (change.Kind == "初始存量") return new(EvidenceKind.Unknown, [], "观察前已存在，不归入本次会话创建");
        var matches = sessions.Where(s => s.Started <= change.Time && (s.Ended is null || change.Time <= s.Ended) &&
            (s.ProjectId.ToString() == change.ProjectId || s.Dependencies.Any(root => Contains(root, change.Path)))).ToArray();
        if (matches.Length == 0) return new(EvidenceKind.Unknown, [], "没有匹配的手动会话或依赖关联");
        if (matches.Any(s => s.HasGap || s.State == SessionState.Interrupted))
            return new(EvidenceKind.Unknown, matches.Select(s => s.Id).ToArray(), "会话包含中断区间，来源未知");
        return new(matches.Length > 1 ? EvidenceKind.Shared : EvidenceKind.Context, matches.Select(s => s.Id).ToArray(),
            matches.Length > 1 ? "多个会话可能关联，计量一次，不分摊独占量" : "登记路径与会话时间相交；没有直接进程写入证据");
    }
    public static bool Contains(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        path = Path.GetFullPath(path);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

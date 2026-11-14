using System.Text.Json;
using CClean.Core.Files;
using CClean.Core.Monitoring;
using CClean.Core.Experiments;

namespace CClean.Core.Cleanup;

// D-13: an execution permission under accepted uncertainty, never proof of event origin.
internal sealed class ObjectCleanupReview
{
    private readonly Guid runId;
    private readonly CleanupContract contract;
    private readonly DateTimeOffset created;
    private readonly DateTimeOffset expires;
    private readonly string fingerprint;
    private readonly DirectoryMonitor[] monitors;
    private readonly Dictionary<Guid, long> cursors;
    private readonly Dictionary<string, Dictionary<Guid, long>> receipts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> parentIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> receiptParents = new(StringComparer.OrdinalIgnoreCase);
    internal Guid RunId => runId;
    internal CleanupContract Contract => contract;
    internal int ReceiptCount => receipts.Count;
    internal Task PostBatchScan => Task.WhenAll(monitors.Select(m => m.ResearchScanCompleted));
    internal long ReviewedObjects { get; private set; }

    internal ObjectCleanupReview(CleanupPlan plan, CleanupContext initial, ObjectCleanupReview? previous = null)
    {
        runId = plan.Id;
        contract = plan.Contract;
        created = plan.Created;
        expires = plan.Expires;
        if (initial.Contract is not (CleanupContract.ObjectReviewV1 or CleanupContract.SegmentReviewResearch) || initial.Contract != plan.Contract || initial.IndependentActivity is null ||
            initial.Observations.Count == 0 || initial.Observations.Any(o => !o.Evidence.Reliable || !o.Monitor.IsObservationCurrent(o.Evidence)))
            throw new InvalidOperationException("对象级契约缺少可靠的活动与通知基线。");
        fingerprint = initial.Fingerprint(true);
        monitors = initial.Observations.Select(o => o.Monitor).ToArray();
        cursors = initial.Observations.ToDictionary(o => o.Evidence.Id, o => o.Evidence.Sequence);
        if (previous is not null)
        {
            if (previous.Contract != plan.Contract) throw new InvalidOperationException("跨批证据不能切换清理契约。");
            // Bounded evidence, not prior permission: 1000 inherited + the current finite manifest.
            // Older delayed notifications that no longer have a receipt stop execution.
            foreach (var receipt in previous.receipts.TakeLast(1000))
            {
                receipts.Add(receipt.Key, receipt.Value);
                for (var parent = Path.GetDirectoryName(receipt.Key); parent is not null; parent = Path.GetDirectoryName(parent))
                    if (previous.parentIdentities.TryGetValue(parent, out var identity))
                    { parentIdentities[parent] = identity; receiptParents.Add(parent); }
            }
        }
        var checkedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Items.Where(i => i.Decision.Eligible))
        {
            if (!initial.Observations.Any(o => ProtectionPolicy.Contains(o.Evidence.Root, item.Decision.Scope!.Root)))
                throw new InvalidOperationException("批准根缺少完整通知覆盖。");
            foreach (var parent in Parents(item))
            {
                if (!checkedParents.Add(parent)) continue;
                var file = WindowsFileMetadata.Read(parent);
                if (file.Identity is null || (file.Attributes & FileAttributes.Directory) == 0 ||
                    (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("批准父目录无法绑定身份。");
                if (parentIdentities.TryGetValue(parent, out var priorIdentity) && priorIdentity != file.Identity)
                    throw new InvalidOperationException("跨批父目录身份变化。");
                parentIdentities[parent] = file.Identity;
                if (!ParentStillBound(parent)) throw new InvalidOperationException("批准父目录含数据流或元数据无法完整检查。");
            }
        }
    }

    internal void Confirm(CleanupItem item, CleanupContext before)
    {
        // Called only after same-handle success AND durable per-item result persistence.
        receipts.Add(item.Path, before.Observations.ToDictionary(o => o.Evidence.Id, o => o.Evidence.Sequence));
        foreach (var parent in Parents(item)) receiptParents.Add(parent);
    }

    internal bool Validate(CleanupContext context, IEnumerable<CleanupItem> remaining, CleanupJournal journal,
        CancellationToken cancellation, out string reason, bool forceObjectReview = false)
    {
        reason = "活动、规则、范围、观察代次或有效期变化";
        // Capture available evidence even when a consumer/configuration change will reject continuation.
        foreach (var observed in context.Observations)
        {
            cancellation.ThrowIfCancellationRequested();
            var evidence = observed.Evidence;
            var after = cursors.GetValueOrDefault(evidence.Id);
            var events = EventsAfter(evidence, after);
            if (events.Length > 0 || evidence.DroppedThrough > after || !evidence.Reliable)
                journal.Record(runId, evidence.Root, "Observation", JsonSerializer.Serialize(new
                { evidence.Id, After = after, evidence.Sequence, evidence.DroppedThrough, evidence.ScanGaps, evidence.Reliable, Source = "undetermined", Events = events }));
        }
        if (DateTimeOffset.UtcNow < created || DateTimeOffset.UtcNow >= expires ||
            context.Contract != contract || context.IndependentActivity is null ||
            context.Fingerprint(true) != fingerprint || context.Observations.Count != cursors.Count) return false;
        var sawEvents = false;
        foreach (var observed in context.Observations)
        {
            cancellation.ThrowIfCancellationRequested();
            var evidence = observed.Evidence;
            if (!cursors.TryGetValue(evidence.Id, out var after) || !evidence.Reliable ||
                evidence.DroppedThrough > after || evidence.Sequence < after)
            { reason = "通知记录缺失、观察失败或代次变化"; return false; }
            var events = EventsAfter(evidence, after);
            sawEvents |= events.Length > 0;
            if (events.LongLength != evidence.Sequence - after)
            { reason = "通知序列不完整"; return false; }
            foreach (var entry in events)
            {
                if (entry.Kind == WatcherChangeTypes.Deleted && receipts.TryGetValue(entry.Path, out var receipt) &&
                    receipt.TryGetValue(evidence.Id, out var beforeDelete) && entry.Sequence > beforeDelete) continue;
                if (entry.Kind == WatcherChangeTypes.Changed && entry.OldPath is null && receiptParents.Contains(entry.Path) &&
                    ParentStillBound(entry.Path)) continue;
                reason = $"相关通知不满足对象级继续条件：{entry.Kind} {entry.Path}";
                return false;
            }
        }
        if (sawEvents || forceObjectReview)
        {
            // D-13 reviews all remaining objects; isolated segment research supplies only its current segment.
            var items = remaining.ToArray();
            if (items.Length == 1)
            {
                cancellation.ThrowIfCancellationRequested();
                var item = items[0];
                var rootIdentity = WindowsFileMetadata.Read(item.Decision.Scope!.Root).Identity;
                if (rootIdentity is null || rootIdentity != item.RootIdentity)
                { reason = "剩余批准根身份复核失败"; return false; }
                using var parent = new DirectoryLease(Path.GetDirectoryName(item.Path)!);
                if (DateTimeOffset.UtcNow >= expires) { reason = "清单到期"; return false; }
                ReviewedObjects++;
                if (WindowsFileMetadata.Read(item.Path, parent, item.File) != item.File ||
                    !ProtectionPolicy.Classify(item.File, context.Rules, context.IndependentActivity, context.ProtectedRoots, DateTimeOffset.UtcNow).Eligible)
                { reason = "剩余批准对象或保护条件复核失败"; return false; }
            }
            else
            {
                foreach (var group in items.GroupBy(i => i.Decision.Scope!.Root, StringComparer.OrdinalIgnoreCase))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var rootIdentity = WindowsFileMetadata.Read(group.Key).Identity;
                    if (group.Any(i => rootIdentity is null || rootIdentity != i.RootIdentity))
                    { reason = "剩余批准根身份复核失败"; return false; }
                }
                foreach (var group in items.GroupBy(i => Path.GetDirectoryName(i.Path)!, StringComparer.OrdinalIgnoreCase))
                {
                    // Reuse the existing stable-parent metadata reader; no subtree-lock inference.
                    using var parent = new DirectoryLease(group.Key);
                    foreach (var item in group)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (DateTimeOffset.UtcNow >= expires) { reason = "清单到期"; return false; }
                        ReviewedObjects++;
                        if (WindowsFileMetadata.Read(item.Path, parent, item.File) != item.File ||
                            !ProtectionPolicy.Classify(item.File, context.Rules, context.IndependentActivity, context.ProtectedRoots, DateTimeOffset.UtcNow).Eligible)
                        { reason = "剩余批准对象或保护条件复核失败"; return false; }
                    }
                }
            }
            // For a one-object research segment, its validated review is recorded with the durable execution intent.
            // Raw observations above still persist independently, including on rejection.
            if (contract != CleanupContract.SegmentReviewResearch || items.Length != 1)
                journal.Record(runId, "", contract == CleanupContract.SegmentReviewResearch ? "SegmentObjectReview" : "ObjectReview",
                contract == CleanupContract.SegmentReviewResearch
                    ? "隔离研究；来源未确定；只复核当前段，未复核未来段；不证明不存在外部消费者"
                    : "来源未确定；通知符合 D-13 继续条件，剩余批准对象已复核；不证明不存在外部消费者");
        }
        foreach (var observed in context.Observations) cursors[observed.Evidence.Id] = observed.Evidence.Sequence;
        reason = "";
        return true;
    }

    internal bool IsCurrent(CleanupContext context) => context.Observations.All(o => o.Monitor.IsObservationCurrent(o.Evidence));

    private static DirectoryNotification[] EventsAfter(DirectoryObservation evidence, long after)
    {
        // Native history is immutable and sequence ordered. Read only the new suffix;
        // missing history is still audited and rejected by Validate's sequence checks.
        var events = evidence.Events;
        if (events.Count == 0 || events[^1].Sequence <= after) return [];
        var first = 0; var end = events.Count;
        while (first < end)
        {
            var middle = first + (end - first) / 2;
            if (events[middle].Sequence <= after) first = middle + 1;
            else end = middle;
        }
        var result = new DirectoryNotification[events.Count - first];
        for (var i = 0; i < result.Length; i++) result[i] = events[first + i];
        return result;
    }

    private bool ParentStillBound(string path)
    {
        if (!parentIdentities.TryGetValue(path, out var identity)) return false;
        return WindowsFileMetadata.IsStreamlessDirectory(path, identity);
    }

    private static IEnumerable<string> Parents(CleanupItem item)
    {
        var root = item.Decision.Scope!.Root;
        for (var parent = Path.GetDirectoryName(item.Path); parent is not null && ProtectionPolicy.Contains(root, parent); parent = Path.GetDirectoryName(parent))
            yield return parent;
    }
}

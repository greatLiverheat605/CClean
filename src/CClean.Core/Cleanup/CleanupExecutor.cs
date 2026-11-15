using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Monitoring;
using CClean.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace CClean.Core.Cleanup;

public sealed record CleanupResult(Guid Id, string State, int Deleted, int Skipped, long EstimatedDeletedBytes, string Message);

public sealed class CleanupExecutor(CleanupJournal journal)
{
    private readonly SemaphoreSlim single = new(1, 1);
    private ObjectCleanupReview? completedReview;
    internal int RetainedReceiptCount => completedReview?.Contract == CleanupContract.ObjectReviewV1 ? completedReview.ReceiptCount : 0;
    internal int RetainedResearchReceiptCount => completedReview?.Contract == CleanupContract.SegmentReviewResearch ? completedReview.ReceiptCount : 0;
    internal Action<string>? TestCheckpoint { get; init; }
    internal bool AllowSegmentResearch { get; init; }
    internal long ReviewedObjects { get; private set; }
    internal TimeSpan ScanGateWait { get; private set; }
    internal const int ResearchSegmentSize = 1;
    // Produces a new, unapproved finite manifest; only ExecuteApproved can delete its objects.
    public Task<CleanupPlan> PreviewNext(IEnumerable<string> paths, Func<Task<CleanupContext>> currentContext,
        CancellationToken cancellation = default) => PreviewNextCore(paths, currentContext, CleanupContract.ObjectReviewV1, cancellation);

    internal Task<CleanupPlan> PreviewNextSegmentResearch(IEnumerable<string> paths, Func<Task<CleanupContext>> currentContext,
        CancellationToken cancellation = default) => PreviewNextCore(paths, currentContext, CleanupContract.SegmentReviewResearch, cancellation);

    private async Task<CleanupPlan> PreviewNextCore(IEnumerable<string> paths, Func<Task<CleanupContext>> currentContext, CleanupContract contract,
        CancellationToken cancellation = default)
    {
        if (!await single.WaitAsync(0, cancellation)) throw new InvalidOperationException("已有清理或跨批预览进行中。");
        try
        {
            var previous = completedReview ?? throw new InvalidOperationException("没有当前进程内完整成功的对象级清理证据，请重新建立正常预览。");
            if (contract == CleanupContract.SegmentReviewResearch)
                await previous.PostBatchScan.WaitAsync(cancellation);
            var context = await currentContext();
            if (previous.Contract != contract || context.Contract != contract ||
                contract == CleanupContract.SegmentReviewResearch && !AllowSegmentResearch)
                throw new InvalidOperationException("前批证据、当前上下文与跨批入口契约不一致；研究证据不能进入普通入口。");
            var reason = "";
            if (!await Task.Run(() => previous.Validate(context, [], journal, cancellation, out reason)))
                throw new InvalidOperationException("跨批证据失效：" + reason);
            var candidate = await Task.Run(() => contract == CleanupContract.SegmentReviewResearch
                ? CleanupPlanner.PreviewSegmentResearch(paths, context with { Activity = context.IndependentActivity! }, cancellation)
                : CleanupPlanner.Preview(paths, context with { Activity = context.IndependentActivity! }, cancellation));
            var refreshed = await currentContext();
            if (context.Fingerprint() != refreshed.Fingerprint() ||
                !await Task.Run(() => previous.Validate(refreshed, candidate.Items.Where(i => i.Decision.Eligible), journal, cancellation, out reason)) ||
                !previous.IsCurrentOrReceiptOnly(refreshed))
                throw new InvalidOperationException("跨批预览期间状态变化，请重新核查。");
            cancellation.ThrowIfCancellationRequested();
            var plan = new CleanupPlan(candidate.Created, refreshed.Fingerprint(), candidate.Items.ToList(), candidate.Truncated,
                contract, previous.RunId);
            journal.Record(previous.RunId, "", "NextPreview", System.Text.Json.JsonSerializer.Serialize(new
            { NextRunId = plan.Id, plan.Created, plan.Expires, plan.EligibleCount, plan.Truncated, Approved = false }));
            return plan;
        }
        catch { completedReview = null; throw; }
        finally { single.Release(); }
    }
    // Approval is supplied only by the UI's explicit confirmation of this immutable plan.
    public async Task<CleanupResult> ExecuteApproved(CleanupPlan plan, Func<Task<CleanupContext>> currentContext, CancellationToken cancellation = default)
    {
        if (!await single.WaitAsync(0, cancellation)) throw new InvalidOperationException("已有清理任务执行中。");
        var deleted = 0; var skipped = 0; long estimate = 0; var began = false; var beginAttempted = false;
        var deferredScans = new Stack<DirectoryMonitor>();
        try
        {
            var previous = completedReview;
            completedReview = null; // Any attempted execution consumes the old continuation slot.
            var segmented = plan.Contract == CleanupContract.SegmentReviewResearch;
            if (segmented && (!AllowSegmentResearch || plan.Items.Count > 10000))
                throw new InvalidOperationException("分段契约仅供独立隔离研究，不能由普通入口或超限清单启用。");
            if (!Enum.IsDefined(plan.Contract)) throw new InvalidOperationException("未知清理契约。");
            ReviewedObjects = 0;
            ScanGateWait = TimeSpan.Zero;
            if (plan.Truncated || plan.EligibleCount == 0)
                throw new InvalidOperationException("清单为空、截断或已失效，请重新预览。");
            var initialContext = await currentContext();
            cancellation.ThrowIfCancellationRequested();
            if (!plan.IsCurrent(initialContext, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("清单为空、截断或已失效，请重新预览。");
            if (plan.PreviousRunId is Guid previousId)
            {
                if (previous is null || previous.RunId != previousId || previous.Contract != plan.Contract)
                    throw new InvalidOperationException("跨批清单缺少本进程的前批证据；不能重放或自动恢复。");
                var reason = "";
                if (!await Task.Run(() => previous.Validate(initialContext, plan.Items.Where(i => i.Decision.Eligible), journal, cancellation, out reason)) ||
                    !previous.IsCurrent(initialContext)) throw new InvalidOperationException("前批证据到期或变化：" + reason);
            }
            var review = plan.Contract is CleanupContract.ObjectReviewV1 or CleanupContract.SegmentReviewResearch
                ? new ObjectCleanupReview(plan, initialContext, plan.PreviousRunId.HasValue ? previous : null) : null;
            previous = null; // The new review copied the bounded continuation evidence.
            // Once persistence is attempted, drain it fully; cancellation must not obscure its commit result.
            beginAttempted = true;
            await Task.Run(() => journal.Begin(plan), CancellationToken.None); began = true;
            if (plan.PreviousRunId is Guid priorRun)
                journal.Record(plan.Id, "", "ContinuationFrom", priorRun.ToString());
            if (review is not null)
                journal.Record(plan.Id, "", "Contract", segmented
                    ? $"SegmentReviewResearch；固定全集已落盘；每段最多{ResearchSegmentSize}项；未来段无通知变化可能更晚发现；不续期、不自动追加"
                    : "ObjectReviewV1 / D-13；来源未确定的回执删除与父元数据通知经复核可继续；接受未知消费者遗漏风险");
            var volumes = plan.Items.Where(i => i.Decision.Eligible).Select(i => Path.GetPathRoot(i.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            journal.Record(plan.Id, "", "SpaceBefore", System.Text.Json.JsonSerializer.Serialize(volumes.Select(VolumeSpaceReader.Read).ToArray()));
            TestCheckpoint?.Invoke("after-plan");
            var eligible = plan.Items.Where(i => i.Decision.Eligible).ToArray();
            if (segmented)
            {
                foreach (var observed in initialContext.Observations.OrderBy(o => o.Evidence.Id))
                {
                    var gate = observed.Monitor.ScanGate;
                    var started = Stopwatch.GetTimestamp();
                    var entered = false;
                    try
                    {
                        while (!(entered = Monitor.TryEnter(gate, 25)))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (DateTimeOffset.UtcNow >= plan.Expires)
                            {
                                journal.Finish(plan.Id, "NeedsPreview", "等待完整扫描时清单到期");
                                return new(plan.Id, "NeedsPreview", deleted, skipped, estimate, "等待完整扫描时清单到期。");
                            }
                        }
                        if (DateTimeOffset.UtcNow >= plan.Expires || !observed.Monitor.HasContinuousObservation)
                        {
                            journal.Finish(plan.Id, "NeedsPreview", "扫描完成后清单到期或观察不可靠");
                            return new(plan.Id, "NeedsPreview", deleted, skipped, estimate, "扫描完成后清单到期或观察不可靠。");
                        }
                        observed.Monitor.BeginResearchScanDeferral();
                        deferredScans.Push(observed.Monitor);
                    }
                    finally
                    {
                        ScanGateWait += Stopwatch.GetElapsedTime(started);
                        if (entered) Monitor.Exit(gate);
                    }
                }
            }
            for (var ordinal = 0; ordinal < eligible.Length; ordinal++)
            {
                var item = eligible[ordinal];
                if (cancellation.IsCancellationRequested) break;
                var context = await currentContext();
                if (cancellation.IsCancellationRequested) break;
                var reason = "活动、规则或有效期变化，后续项未执行";
                IEnumerable<CleanupItem> ToReview() => segmented ? eligible.Skip(ordinal).Take(ResearchSegmentSize - ordinal % ResearchSegmentSize) : eligible.Skip(ordinal);
                var valid = review is null ? plan.IsCurrent(context, DateTimeOffset.UtcNow) :
                    await Task.Run(() => review.Validate(context, ToReview(), journal, cancellation, out reason, segmented && ordinal % ResearchSegmentSize == 0));
                if (valid && review is not null)
                {
                    // Remaining-object reads can be slow: refresh consumers/session/config after them.
                    var refreshed = await currentContext();
                    valid = await Task.Run(() => review.Validate(refreshed, ToReview(), journal, cancellation, out reason));
                    context = refreshed;
                }
                if (!valid)
                {
                    journal.Finish(plan.Id, "NeedsPreview", reason);
                    return new(plan.Id, "NeedsPreview", deleted, skipped, estimate, "状态已变化，需重新预览：" + reason);
                }
                if (review is not null) ReviewedObjects = review.ReviewedObjects;
                if (cancellation.IsCancellationRequested) break;
                TestCheckpoint?.Invoke("after-review");
                var executionContext = review is null ? context : context with { Activity = context.IndependentActivity! };
                var result = await Task.Run(() => DeleteOne(plan, item, executionContext,
                    review is null ? null : () => !cancellation.IsCancellationRequested && review.IsCurrentOrReceiptOnly(context), cancellation,
                    segmented ? ordinal : null), CancellationToken.None);
                if (result.DeleteMarked) { deleted++; estimate = checked(estimate + item.File.AllocatedBytes!.Value); }
                else skipped++;
                // A failed result commit escapes the loop; it must never be treated as an ordinary file skip.
                await Task.Run(() => journal.Record(plan.Id, item.Path, result.DeleteMarked ? "Deleted" : "Skipped", result.Reason));
                if (result.DeleteMarked) review?.Confirm(item, context);
                else if (review is not null && !cancellation.IsCancellationRequested)
                {
                    var stoppedContext = await currentContext();
                    await Task.Run(() => review.Validate(stoppedContext, ToReview(), journal, cancellation, out _));
                    journal.Finish(plan.Id, "NeedsPreview", result.Reason);
                    return new(plan.Id, "NeedsPreview", deleted, skipped, estimate, "对象复核或通知状态变化，后续项未执行：" + result.Reason);
                }
                TestCheckpoint?.Invoke("after-result");
            }
            if (review is not null && !cancellation.IsCancellationRequested)
            {
                var finalContext = await currentContext();
                var finalReason = "";
                if (!await Task.Run(() => review.Validate(finalContext, [], journal, cancellation, out finalReason)))
                {
                    journal.Finish(plan.Id, "NeedsPreview", finalReason);
                    return new(plan.Id, "NeedsPreview", deleted, skipped, estimate, "结束复核发现变化；已完成项保留，需核查记录：" + finalReason);
                }
            }
            var state = cancellation.IsCancellationRequested ? "Cancelled" : skipped == 0 ? "Completed" : "Partial";
            var message = $"删除标记 {deleted} 项，跳过 {skipped} 项；系统释放须独立采样。";
            journal.Record(plan.Id, "", "SpaceAfter", System.Text.Json.JsonSerializer.Serialize(volumes.Select(VolumeSpaceReader.Read).ToArray()));
            journal.Finish(plan.Id, state, message);
            if (state == "Completed") completedReview = review;
            return new(plan.Id, state, deleted, skipped, estimate, message);
        }
        catch (OperationCanceledException) when (!beginAttempted && cancellation.IsCancellationRequested)
        {
            return new(plan.Id, "Cancelled", 0, 0, 0, "已在开始前取消，未执行删除。");
        }
        catch (OperationCanceledException) when (began && cancellation.IsCancellationRequested)
        {
            try { journal.Finish(plan.Id, "Cancelled", "已取消，后续项未执行。"); }
            catch (Exception error)
            { return new(plan.Id, "Failed", deleted, skipped, estimate, "已停止，但取消状态写入失败，需核查审计：" + error.Message); }
            return new(plan.Id, "Cancelled", deleted, skipped, estimate, "已取消，后续项未执行。");
        }
        catch (Exception error)
        {
            if (!began) throw;
            // Leave a visible interrupted/failed run; never continue after audit failure.
            try { journal.Finish(plan.Id, "Failed", "已停止；最后一项可能已删除但结果未记录：" + error.Message); } catch { }
            return new(plan.Id, "Failed", deleted, skipped, estimate, "执行已停止，需核查审计：" + error.Message);
        }
        finally
        {
            try { while (deferredScans.TryPop(out var monitor)) monitor.EndResearchScanDeferral(); }
            finally { single.Release(); }
        }
    }
    private DeletionResult DeleteOne(CleanupPlan plan, CleanupItem item, CleanupContext context, Func<bool>? observationCurrent = null,
        CancellationToken cancellation = default, int? researchOrdinal = null)
    {
        // Audit failures are intentionally outside the per-file catch.
        if (researchOrdinal is int ordinal)
            journal.RecordPreparation(plan.Id, System.Text.Json.JsonSerializer.Serialize(new
            { FirstOrdinal = ordinal, Count = 1, plan.Expires, CurrentObjectReviewed = true, Source = "undetermined", FutureObjectsReviewed = false }), item.Path);
        else journal.Record(plan.Id, item.Path, "Started", "已持久化逐项执行意图；尚不代表删除成功");
        var heldScanGates = new Stack<object>();
        try
        {
            if (observationCurrent is not null)
            {
                // A disappearing approved object must not manufacture a gap in our own snapshot scan.
                // Native notifications stay enabled and external writers are not constrained.
                foreach (var observed in context.Observations.OrderBy(o => o.Evidence.Id))
                {
                    var gate = observed.Monitor.ScanGate;
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        while (!Monitor.TryEnter(gate, 25))
                            if (cancellation.IsCancellationRequested || DateTimeOffset.UtcNow >= plan.Expires)
                                return new(false, "等待本机扫描结束时取消或到期");
                    }
                    finally { ScanGateWait += Stopwatch.GetElapsedTime(started); }
                    heldScanGates.Push(gate);
                }
            }
            var scope = item.Decision.Scope!;
            using var ancestors = new DirectoryLease(Path.GetDirectoryName(item.Path)!);
            if (!ProtectionPolicy.Contains(scope.Root, item.Path) || WindowsFileMetadata.Read(scope.Root).Identity != item.RootIdentity)
                return new(false, "批准根身份或边界变化");
            using var handle = WindowsFileMetadata.Open(item.Path, access: 0x10080, share: 1);
            var current = WindowsFileMetadata.Read(handle, item.Path, item.File);
            if (current != item.File || !ProtectionPolicy.Classify(current, context.Rules, context.Activity, context.ProtectedRoots, DateTimeOffset.UtcNow).Eligible)
                return new(false, "文件或保护条件已变化");
            if (DateTimeOffset.UtcNow >= plan.Expires) return new(false, "清单到期");
            TestCheckpoint?.Invoke("before-delete");
            if (DateTimeOffset.UtcNow >= plan.Expires) return new(false, "清单到期");
            if (observationCurrent is not null && !observationCurrent()) return new(false, "复核期间通知变化、观察失效或取消");
            if (!SetFileInformationByHandle(handle, 4, [1], 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            TestCheckpoint?.Invoke("after-delete");
            return new(true, "同句柄删除标记成功；物理释放可能延迟");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ExperimentRefusedException)
        { return new(false, error.Message); }
        finally { while (heldScanGates.TryPop(out var gate)) Monitor.Exit(gate); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] info, uint size);
}

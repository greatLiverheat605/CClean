using System.ComponentModel;
using System.Runtime.InteropServices;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace CClean.Core.Cleanup;

public sealed record CleanupResult(Guid Id, string State, int Deleted, int Skipped, long EstimatedDeletedBytes, string Message);

public sealed class CleanupExecutor(CleanupJournal journal)
{
    private readonly SemaphoreSlim single = new(1, 1);
    internal Action<string>? TestCheckpoint { get; init; }
    // Approval is supplied only by the UI's explicit confirmation of this immutable plan.
    public async Task<CleanupResult> ExecuteApproved(CleanupPlan plan, Func<Task<CleanupContext>> currentContext, CancellationToken cancellation = default)
    {
        if (!await single.WaitAsync(0, cancellation)) throw new InvalidOperationException("已有清理任务执行中。");
        var deleted = 0; var skipped = 0; long estimate = 0; var began = false;
        try
        {
            if (plan.Truncated || plan.EligibleCount == 0 || !plan.IsCurrent(await currentContext(), DateTimeOffset.UtcNow))
                throw new InvalidOperationException("清单为空、截断或已失效，请重新预览。");
            await Task.Run(() => journal.Begin(plan), cancellation); began = true;
            var volumes = plan.Items.Where(i => i.Decision.Eligible).Select(i => Path.GetPathRoot(i.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            journal.Record(plan.Id, "", "SpaceBefore", System.Text.Json.JsonSerializer.Serialize(volumes.Select(VolumeSpaceReader.Read).ToArray()));
            TestCheckpoint?.Invoke("after-plan");
            foreach (var item in plan.Items.Where(i => i.Decision.Eligible))
            {
                if (cancellation.IsCancellationRequested) break;
                var context = await currentContext();
                if (cancellation.IsCancellationRequested) break;
                if (!plan.IsCurrent(context, DateTimeOffset.UtcNow))
                {
                    journal.Finish(plan.Id, "NeedsPreview", "活动、规则或有效期变化，后续项未执行");
                    return new(plan.Id, "NeedsPreview", deleted, skipped, estimate, "状态已变化，需重新预览。");
                }
                var result = await Task.Run(() => DeleteOne(plan, item, context), CancellationToken.None);
                if (result.DeleteMarked) { deleted++; estimate = checked(estimate + item.File.AllocatedBytes!.Value); }
                else skipped++;
                // A failed result commit escapes the loop; it must never be treated as an ordinary file skip.
                await Task.Run(() => journal.Record(plan.Id, item.Path, result.DeleteMarked ? "Deleted" : "Skipped", result.Reason));
                TestCheckpoint?.Invoke("after-result");
            }
            var state = cancellation.IsCancellationRequested ? "Cancelled" : skipped == 0 ? "Completed" : "Partial";
            var message = $"删除标记 {deleted} 项，跳过 {skipped} 项；系统释放须独立采样。";
            journal.Record(plan.Id, "", "SpaceAfter", System.Text.Json.JsonSerializer.Serialize(volumes.Select(VolumeSpaceReader.Read).ToArray()));
            journal.Finish(plan.Id, state, message);
            return new(plan.Id, state, deleted, skipped, estimate, message);
        }
        catch (Exception error)
        {
            if (!began) throw;
            // Leave a visible interrupted/failed run; never continue after audit failure.
            try { journal.Finish(plan.Id, "Failed", "已停止；最后一项可能已删除但结果未记录：" + error.Message); } catch { }
            return new(plan.Id, "Failed", deleted, skipped, estimate, "执行已停止，需核查审计：" + error.Message);
        }
        finally { single.Release(); }
    }
    private DeletionResult DeleteOne(CleanupPlan plan, CleanupItem item, CleanupContext context)
    {
        // Audit failures are intentionally outside the per-file catch.
        journal.Record(plan.Id, item.Path, "Started", "已持久化逐项执行意图；尚不代表删除成功");
        try
        {
            var scope = item.Decision.Scope!;
            using var ancestors = new DirectoryLease(Path.GetDirectoryName(item.Path)!);
            if (!ProtectionPolicy.Contains(scope.Root, item.Path) || WindowsFileMetadata.Read(scope.Root).Identity != item.RootIdentity)
                return new(false, "批准根身份或边界变化");
            using var handle = WindowsFileMetadata.Open(item.Path, access: 0x10080, share: 1);
            var current = WindowsFileMetadata.Read(handle, item.Path);
            if (current != item.File || !ProtectionPolicy.Classify(current, context.Rules, context.Activity, context.ProtectedRoots, DateTimeOffset.UtcNow).Eligible)
                return new(false, "文件或保护条件已变化");
            if (DateTimeOffset.UtcNow >= plan.Expires) return new(false, "清单到期");
            TestCheckpoint?.Invoke("before-delete");
            if (!SetFileInformationByHandle(handle, 4, [1], 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            TestCheckpoint?.Invoke("after-delete");
            return new(true, "同句柄删除标记成功；物理释放可能延迟");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ExperimentRefusedException)
        { return new(false, error.Message); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] info, uint size);
}

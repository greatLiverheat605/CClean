using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using Microsoft.Data.Sqlite;

namespace CClean.Poc;

internal static class ProductQuotaProbe
{
    public static void Run(ExperimentSandbox box, string quotaRoot, List<object> checks, bool afterDelete = false)
    {
        quotaRoot = ExperimentSandbox.Normalize(quotaRoot);
        var drive = new DriveInfo(Path.GetPathRoot(quotaRoot)!);
        if (box.Budget < 5L * 1024 * 1024 * 1024 || drive.Name.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase) ||
            drive.DriveFormat != "NTFS" || drive.TotalSize > 4L * 1024 * 1024 * 1024 || drive.TotalSize < 3L * 1024 * 1024 * 1024 ||
            drive.VolumeLabel != "CCLEAN_QUOT_P" || File.ReadAllText(Path.Combine(drive.Name, ".cclean-quota")).Trim() != ExperimentSandbox.PlanId || Directory.Exists(quotaRoot))
            throw new ExperimentRefusedException("Fresh marked 4 GiB product quota volume and explicit 5 GiB host budget required.", 3);
        using var boundary = new DirectoryLease(quotaRoot, createMissing: true);
        var cache = Path.Combine(box.RunDirectory, "cache");
        var content = Path.Combine(cache, "_cacache", "content-v2"); Directory.CreateDirectory(content);
        var candidate = Path.Combine(content, "owned"); File.WriteAllText(candidate, "owned quota fixture", System.Text.Encoding.UTF8);
        File.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddHours(-1));
        var context = new CleanupContext(1, [new(cache, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)], new(1, [new(cache, false, true, "owned quota fixture")]), [quotaRoot]);
        var nextCandidate = Path.Combine(content, "next"); File.Copy(candidate, nextCandidate); File.SetLastWriteTimeUtc(nextCandidate, DateTime.UtcNow.AddHours(-1));
        var plan = CleanupPlanner.Preview([candidate, nextCandidate], context);
        if (plan.EligibleCount != 2) throw new InvalidOperationException("Positive quota candidate required.");
        var nativeError = 0; var sqliteError = 0;
        using (var filler = new FileStream(Path.Combine(quotaRoot, "owned-filler.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
        using (var journal = new CleanupJournal(quotaRoot))
        {
            void FillVolume()
            {
                var block = new byte[1024 * 1024];
                try
                {
                    for (var n = 0; n <= 4096; n++)
                    {
                        if (new DriveInfo(Path.GetPathRoot(box.Root)!).AvailableFreeSpace < box.Reserve) throw new ExperimentRefusedException("Host reserve reached.", 2);
                        filler.Write(block); filler.Flush(true);
                    }
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 112 or 39) { nativeError = error.HResult & 0xffff; }
                if (nativeError == 0) throw new IOException("Actual full-volume I/O required.");
                try { for (var n = 0; n < 257; n++) { filler.Write(block, 0, 4096); filler.Flush(true); } }
                catch (IOException error) when ((error.HResult & 0xffff) is 112 or 39) { }
                if (afterDelete) return;
                using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(quotaRoot, "raw-io.db"), Pooling = false }.ToString());
                try { raw.Open(); using var command = raw.CreateCommand(); command.CommandText = "CREATE TABLE probe(value BLOB); INSERT INTO probe VALUES(zeroblob(2097152));"; command.ExecuteNonQuery(); }
                catch (SqliteException error) { sqliteError = error.SqliteErrorCode; }
            }
            if (afterDelete) journal.BeforeRecordWrite = state => { if (state == "Deleted") FillVolume(); };
            var result = new CleanupExecutor(journal) { TestCheckpoint = point => { if (!afterDelete && point == "after-plan") FillVolume(); } }
                .ExecuteApproved(plan, () => Task.FromResult(context)).GetAwaiter().GetResult();
            if (afterDelete && result.Message.Contains("SQLite Error 13", StringComparison.Ordinal)) sqliteError = 13;
            if (nativeError == 0 || sqliteError is not (13 or 14) || result.State != "Failed" || result.Deleted != (afterDelete ? 1 : 0) || File.Exists(candidate) == afterDelete || !File.Exists(nextCandidate))
                throw new InvalidOperationException("Real full-volume product guard failed.");
        }
        using var reopened = new CleanupJournal(quotaRoot);
        if (reopened.Runs().Single().State != "Interrupted" || File.Exists(candidate) == afterDelete || !File.Exists(nextCandidate)) throw new InvalidOperationException("Quota restart state incorrect.");
        if (afterDelete && CleanupRecovery.Inspect(reopened, plan.Id.ToString()).Any(e => e.State != "RecoveryObservation")) throw new InvalidOperationException("Uncommitted deletion was invented.");
        checks.Add(new { name = afterDelete ? "product-post-delete-audit-full" : "product-real-full-volume", status = "passed", nativeError, sqliteError, candidatePreserved = !afterDelete, nextCandidatePreserved = true,
            productStoppedByBudgetGuard = !afterDelete, productSqliteWriteFailureObserved = afterDelete, restartState = "Interrupted", hostReservePreserved = true });
    }
}

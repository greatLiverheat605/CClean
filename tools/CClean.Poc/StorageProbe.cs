using CClean.Core.Experiments;
using CClean.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CClean.Poc;

internal static class StorageProbe
{
    public static void Run(ExperimentSandbox box, string? quotaRoot, List<object> checks)
    {
        if (box.Budget < 1024L * 1024 * 1024) throw new ExperimentRefusedException("Storage pressure requires an explicit 1 GiB non-system budget.", 2);
        var runtime = Path.Combine(box.RunDirectory, "runtime");
        Directory.CreateDirectory(runtime);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(runtime, "index.db"), Pooling = false }.ToString());
        writer.Open();
        Execute(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE sample(id INTEGER PRIMARY KEY, payload BLOB); INSERT INTO sample(payload) VALUES (zeroblob(1));");
        using var reader = new SqliteConnection(writer.ConnectionString);
        reader.Open();
        using var transaction = reader.BeginTransaction(deferred: true);
        using (var command = reader.CreateCommand()) { command.Transaction = transaction; command.CommandText = "SELECT COUNT(*) FROM sample"; command.ExecuteScalar(); }
        long peak = 0;
        var batches = 0;
        while (RuntimeBudget.Evaluate(RuntimeBudget.Measure(runtime), (long)VolumeSpaceReader.Read(runtime).UserAvailableBytes, true, 2 * 1024 * 1024).Collect)
        {
            if (++batches > 1000) throw new InvalidOperationException("Budget did not stop WAL growth.");
            Execute(writer, "INSERT INTO sample(payload) VALUES (zeroblob(1048576));");
            peak = Math.Max(peak, RuntimeBudget.Measure(runtime).DatabaseBytes);
        }
        if (peak >= RuntimeBudget.DatabaseLimit || peak < 200L * 1024 * 1024) throw new InvalidOperationException("Budget threshold behavior unexpected.");
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
            using var state = command.ExecuteReader();
            state.Read();
            if (state.GetInt32(2) >= state.GetInt32(1)) throw new InvalidOperationException("Long reader did not retain WAL.");
        }
        transaction.Rollback();
        reader.Close();
        Execute(writer, "PRAGMA wal_checkpoint(TRUNCATE);");
        var wal = Path.Combine(runtime, "index.db-wal");
        if (File.Exists(wal) && new FileInfo(wal).Length != 0) throw new InvalidOperationException("WAL did not truncate after reader released.");
        checks.Add(new { name = "wal-budget", status = "passed", batches, peakDatabaseFamilyBytes = peak, checkpointTruncated = true });
        var injected = RuntimeBudget.Evaluate(new(0, 0, 0), 0, false);
        if (injected.Delete || injected.Collect || injected.Vacuum) throw new InvalidOperationException("Injected low-space guard failed.");
        checks.Add(new { name = "injected-low-space-audit", status = "passed", realIo = false });
        if (quotaRoot is null) throw new ExperimentRefusedException("Real I/O quota volume is required via --quota-root; injected checks alone do not pass G4.", 2);
        RealDiskFull(box, quotaRoot, checks);
    }

    private static void RealDiskFull(ExperimentSandbox box, string quotaRoot, List<object> checks)
    {
        quotaRoot = ExperimentSandbox.Normalize(quotaRoot);
        var drive = new DriveInfo(Path.GetPathRoot(quotaRoot)!);
        if (drive.Name.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase) ||
            drive.DriveFormat != "NTFS" || drive.TotalSize > 256L * 1024 * 1024 || drive.TotalSize < 200L * 1024 * 1024 ||
            !drive.VolumeLabel.StartsWith("CCLEAN_QUOT", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(drive.Name, ".cclean-quota")) ||
            File.ReadAllText(Path.Combine(drive.Name, ".cclean-quota")).Trim() != ExperimentSandbox.PlanId)
            throw new ExperimentRefusedException("Not the explicitly prepared bounded quota volume.", 3);
        if (Directory.Exists(quotaRoot)) throw new ExperimentRefusedException("Quota run directory already exists.", 3);
        using var lease = new DirectoryLease(quotaRoot, createMissing: true);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(quotaRoot, "audit.db"), Pooling = false }.ToString());
        connection.Open();
        Execute(connection, "CREATE TABLE audit(value BLOB);");
        var ioFailed = false;
        var errorCode = 0;
        using (var filler = new FileStream(Path.Combine(quotaRoot, "bounded-filler.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var block = new byte[1024 * 1024];
            try
            {
                for (var i = 0; i <= 256; i++)
                {
                    if (VolumeSpaceReader.Read(box.Root).UserAvailableBytes < (ulong)box.Reserve)
                        throw new ExperimentRefusedException("Host reserve reached.", 2);
                    filler.Write(block);
                    filler.Flush(true);
                }
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 112 or 39)
            { ioFailed = true; errorCode = error.HResult & 0xffff; }
        }
        if (!ioFailed) throw new InvalidOperationException("Actual disk-full I/O was not observed.");
        var auditFailed = false;
        try { Execute(connection, "INSERT INTO audit VALUES (zeroblob(2097152));"); }
        catch (SqliteException error) when (error.SqliteErrorCode == 13) { auditFailed = true; }
        if (!auditFailed || RuntimeBudget.Evaluate(new(0, 0, 0), (long)drive.AvailableFreeSpace, !auditFailed).Delete)
            throw new InvalidOperationException("Actual full-volume audit did not fail closed.");
        checks.Add(new { name = "real-quota-io", status = "passed", realIo = true, nativeError = errorCode,
            sqliteError = 13, volumeBytes = drive.TotalSize, hostReservePreserved = true });
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

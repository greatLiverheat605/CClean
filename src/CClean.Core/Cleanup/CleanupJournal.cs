using System.Text.Json;
using CClean.Core.Experiments;
using CClean.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CClean.Core.Cleanup;

public sealed record CleanupRun(string Id, string Created, string State, string Summary);
public sealed record CleanupEvent(string Path, string State, string Reason, string Time);

public sealed class CleanupJournal : IDisposable
{
    // Probe-only barrier after the budget check; production never installs it.
    internal Action<string>? BeforeRecordWrite { get; set; }
    private readonly object sync = new();
    private readonly string root;
    private readonly DirectoryLease directory;
    private readonly FileStream owner;
    private readonly SqliteConnection connection;
    public CleanupJournal(string root)
    {
        Directory.CreateDirectory(root); this.root = root;
        directory = new(root);
        try { owner = new(Path.Combine(root, "cleanup-owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch { directory.Dispose(); throw; }
        connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "cleanup.db"), Pooling = false }.ToString());
        try
        {
            EnsureBudget(65536); connection.Open();
            using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(version.ExecuteScalar()) > 1) throw new IOException("审计格式高于当前版本，拒绝写入。");
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=100; CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY,created TEXT NOT NULL,state TEXT NOT NULL,manifest TEXT NOT NULL,summary TEXT NOT NULL); CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY,run TEXT NOT NULL,path TEXT NOT NULL,state TEXT NOT NULL,reason TEXT NOT NULL,time TEXT NOT NULL); CREATE INDEX IF NOT EXISTS event_run ON events(run);");
            Execute("PRAGMA user_version=1");
            Execute("UPDATE runs SET state='Interrupted',summary='上次运行中断；结果可能未落盘，不自动续删，需重新预览' WHERE state='Running'");
        }
        catch { connection.Dispose(); owner.Dispose(); directory.Dispose(); throw; }
    }
    private void EnsureBudget(long bytes)
    {
        var space = VolumeSpaceReader.Read(root);
        if (!RuntimeBudget.Evaluate(RuntimeBudget.Measure(root), (long)space.UserAvailableBytes, true, bytes, (long)space.TotalBytes).Delete)
            throw new IOException("持久审计空间预算不足，停止后续删除。");
    }
    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
    public void Begin(CleanupPlan plan)
    {
        lock (sync)
        {
            var manifest = JsonSerializer.Serialize(plan);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(manifest);
            if (bytes > 8 * 1024 * 1024) throw new IOException("审计清单超过 8 MiB，请缩小选择。");
            EnsureBudget(bytes * 3L + 65536);
            Execute("INSERT INTO runs VALUES($id,$time,'Running',$manifest,'已确认永久删除具体候选集合')", ("$id", plan.Id.ToString()), ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$manifest", manifest));
        }
    }
    public void Record(Guid run, string path, string state, string reason)
    {
        lock (sync)
        {
            EnsureBudget(65536 + path.Length * 4L);
            BeforeRecordWrite?.Invoke(state);
            Execute("INSERT INTO events(run,path,state,reason,time) VALUES($run,$path,$state,$reason,$time)", ("$run", run.ToString()), ("$path", path), ("$state", state), ("$reason", reason), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        }
    }
    public void Finish(Guid run, string state, string summary)
    {
        lock (sync) { EnsureBudget(65536); Execute("UPDATE runs SET state=$state,summary=$summary WHERE id=$run", ("$state", state), ("$summary", summary), ("$run", run.ToString())); }
    }
    public IReadOnlyList<CleanupRun> Runs()
    {
        lock (sync)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT id,created,state,summary FROM runs ORDER BY created DESC LIMIT 200";
            using var reader = command.ExecuteReader(); var rows = new List<CleanupRun>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return rows;
        }
    }
    public IReadOnlyList<CleanupEvent> Events(string run)
    {
        lock (sync)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT path,state,reason,time FROM events WHERE run=$run ORDER BY seq LIMIT 3000"; command.Parameters.AddWithValue("$run", run);
            using var reader = command.ExecuteReader(); var rows = new List<CleanupEvent>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return rows;
        }
    }
    public IReadOnlyList<CleanupItem> Manifest(string run)
    {
        lock (sync)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT manifest FROM runs WHERE id=$run"; command.Parameters.AddWithValue("$run", run);
            if (command.ExecuteScalar() is not string json) return [];
            if (json.Length > 8 * 1024 * 1024) throw new IOException("审计清单超出读取上限。");
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("Items").Deserialize<CleanupItem[]>() ?? [];
        }
    }
    public void Dispose() { lock (sync) { connection.Dispose(); owner.Dispose(); directory.Dispose(); } }
}

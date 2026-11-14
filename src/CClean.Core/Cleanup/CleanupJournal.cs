using System.Buffers;
using System.Text;
using CClean.Core.Experiments;
using CClean.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CClean.Core.Cleanup;

public sealed record CleanupRun(string Id, string Created, string State, string Summary);
public sealed record CleanupEvent(string Path, string State, string Reason, string Time)
{
    public long Sequence { get; init; }
}
public sealed record CleanupRunCursor(string Created, string Id);
public sealed record CleanupRunPage(IReadOnlyList<CleanupRun> Items, CleanupRunCursor? NextCursor, bool HasMore)
{
    public bool IsComplete => !HasMore;
}
public sealed record CleanupEventPage(IReadOnlyList<CleanupEvent> Items, long NextSequence, long ThroughSequence, bool HasMore, bool RunFound)
{
    public bool IsComplete => !HasMore;
}
public sealed record CleanupManifestPage(IReadOnlyList<CleanupItem> Items, int NextOrdinal, int TotalItems, bool HasMore, bool RunFound)
{
    public bool IsComplete => !HasMore;
}

public sealed class CleanupJournal : IDisposable
{
    public const int MaximumManifestBytes = CleanupManifestCodec.MaximumBytes;
    public const int MaximumPageSize = 1000;
    // Probe-only barrier after the budget check; production never installs it.
    internal Action<string>? BeforeRecordWrite { get; set; }
    private readonly object sync = new();
    private readonly string root;
    private readonly DirectoryLease directory;
    private readonly FileStream owner;
    private readonly SqliteConnection connection;
    // One immutable manifest only. Comparing its persisted text also detects external corruption.
    private byte[]? cachedManifestBytes;
    private string? cachedManifestRun;
    private IReadOnlyList<CleanupItem>? cachedManifest;
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
            var manifest = CleanupManifestCodec.Encode(plan);
            var bytes = Encoding.UTF8.GetByteCount(manifest);
            EnsureBudget(bytes * 3L + 65536);
            var run = plan.Id.ToString();
            Execute("INSERT INTO runs VALUES($id,$time,'Running',$manifest,'已确认永久删除具体候选集合')", ("$id", run), ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$manifest", manifest));
            cachedManifestBytes = Encoding.UTF8.GetBytes(manifest); cachedManifestRun = run; cachedManifest = plan.Items.ToArray();
        }
    }
    public void Record(Guid run, string path, string state, string reason)
    {
        lock (sync)
        {
            EnsureBudget(65536 + path.Length * 4L + reason.Length * 12L);
            BeforeRecordWrite?.Invoke(state);
            Execute("INSERT INTO events(run,path,state,reason,time) VALUES($run,$path,$state,$reason,$time)", ("$run", run.ToString()), ("$path", path), ("$state", state), ("$reason", reason), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        }
    }
    public void Finish(Guid run, string state, string summary)
    {
        lock (sync) { EnsureBudget(65536); Execute("UPDATE runs SET state=$state,summary=$summary WHERE id=$run", ("$state", state), ("$summary", summary), ("$run", run.ToString())); }
    }
    // Only preparation records share a commit. No deletion may begin until this returns.
    internal void RecordPreparation(Guid run, string segment, string path)
    {
        lock (sync)
        {
            EnsureBudget(checked(131072 + path.Length * 4L + segment.Length * 12L));
            using var transaction = connection.BeginTransaction();
            Add("", "SegmentStart", segment);
            Add(path, "Started", "已持久化逐项执行意图；尚不代表删除成功");
            transaction.Commit();
            void Add(string itemPath, string state, string reason)
            {
                BeforeRecordWrite?.Invoke(state);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO events(run,path,state,reason,time) VALUES($run,$path,$state,$reason,$time)";
                command.Parameters.AddWithValue("$run", run.ToString());
                command.Parameters.AddWithValue("$path", itemPath);
                command.Parameters.AddWithValue("$state", state);
                command.Parameters.AddWithValue("$reason", reason);
                command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }
        }
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
    public CleanupRunPage RunsPage(CleanupRunCursor? after = null, int pageSize = 200, CancellationToken cancellation = default)
    {
        CheckPageSize(pageSize); cancellation.ThrowIfCancellationRequested();
        lock (sync)
        {
            cancellation.ThrowIfCancellationRequested();
            if (after is not null)
            {
                if (string.IsNullOrEmpty(after.Id) || string.IsNullOrEmpty(after.Created)) throw new ArgumentException("任务分页游标无效。", nameof(after));
                using var cursor = connection.CreateCommand();
                cursor.CommandText = "SELECT 1 FROM runs WHERE id=$id AND created=$created";
                cursor.Parameters.AddWithValue("$id", after.Id); cursor.Parameters.AddWithValue("$created", after.Created);
                if (cursor.ExecuteScalar() is null)
                    throw new ArgumentException("任务分页游标不属于现有任务。", nameof(after));
            }
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,created,state,summary FROM runs WHERE $created IS NULL OR created<$created OR (created=$created AND id<$id) ORDER BY created DESC,id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$created", (object?)after?.Created ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", (object?)after?.Id ?? DBNull.Value); command.Parameters.AddWithValue("$limit", pageSize + 1);
            using var reader = command.ExecuteReader(); var rows = new List<CleanupRun>();
            while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))); }
            var more = rows.Count > pageSize; if (more) rows.RemoveAt(rows.Count - 1);
            var last = rows.LastOrDefault();
            cancellation.ThrowIfCancellationRequested();
            return new(rows.AsReadOnly(), last is null ? after : new(last.Created, last.Id), more);
        }
    }
    public IReadOnlyList<CleanupEvent> Events(string run)
    {
        var rows = new List<CleanupEvent>(); long after = 0; long? through = null;
        while (true)
        {
            var page = EventsPage(run, after, MaximumPageSize, through); rows.AddRange(page.Items);
            if (!page.HasMore) return rows.AsReadOnly();
            after = page.NextSequence; through = page.ThroughSequence;
        }
    }
    public CleanupEventPage EventsPage(string run, long afterSequence = 0, int pageSize = 200, long? throughSequence = null, CancellationToken cancellation = default)
    {
        CheckPageSize(pageSize); cancellation.ThrowIfCancellationRequested();
        lock (sync)
        {
            var window = EventWindow(run, afterSequence, throughSequence, cancellation);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT seq,path,state,reason,time FROM events WHERE run=$run AND seq>$after AND seq<=$through ORDER BY seq LIMIT $limit";
            command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$after", afterSequence);
            command.Parameters.AddWithValue("$through", window.ThroughSequence); command.Parameters.AddWithValue("$limit", pageSize + 1);
            using var reader = command.ExecuteReader(); var rows = new List<CleanupEvent>();
            while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); rows.Add(ReadEvent(reader)); }
            var more = rows.Count > pageSize; if (more) rows.RemoveAt(rows.Count - 1);
            cancellation.ThrowIfCancellationRequested();
            return new(rows.AsReadOnly(), rows.LastOrDefault()?.Sequence ?? afterSequence, window.ThroughSequence, more, window.RunFound);
        }
    }
    public IReadOnlyList<CleanupItem> Manifest(string run)
    {
        return Manifest(run, CancellationToken.None);
    }
    public IReadOnlyList<CleanupItem> Manifest(string run, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (sync) return ReadManifest(run, cancellation) ?? [];
    }
    public CleanupManifestPage ManifestPage(string run, int afterOrdinal = -1, int pageSize = 200, CancellationToken cancellation = default)
    {
        CheckPageSize(pageSize); cancellation.ThrowIfCancellationRequested();
        lock (sync)
        {
            var items = ReadManifest(run, cancellation);
            if (afterOrdinal < -1 || afterOrdinal >= (items?.Count ?? 0))
                throw new ArgumentOutOfRangeException(nameof(afterOrdinal), "清单游标必须是此任务已读取的对象序号。");
            var rows = items?.Skip(afterOrdinal + 1).Take(pageSize).ToArray() ?? [];
            var next = afterOrdinal + rows.Length;
            cancellation.ThrowIfCancellationRequested();
            return new(rows, next, items?.Count ?? 0, next + 1 < (items?.Count ?? 0), items is not null);
        }
    }
    private IReadOnlyList<CleanupItem>? ReadManifest(string run, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (cachedManifest is not null && run == cachedManifestRun && cachedManifestBytes is not null)
        {
            using var compare = connection.CreateCommand();
            compare.CommandText = "SELECT rowid,length(CAST(manifest AS BLOB)) FROM runs WHERE id=$run";
            compare.Parameters.AddWithValue("$run", run);
            long rowId;
            using (var stored = compare.ExecuteReader())
            {
                if (!stored.Read()) return null;
                if (stored.IsDBNull(1) || stored.GetInt64(1) > MaximumManifestBytes) throw new IOException("审计清单超出读取上限。");
                rowId = stored.GetInt64(0);
            }
            using var blob = new SqliteBlob(connection, "runs", "manifest", rowId, readOnly: true);
            if (ManifestBytesEqual(blob, cachedManifestBytes))
            { cancellation.ThrowIfCancellationRequested(); return cachedManifest; }
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(CAST(manifest AS BLOB)),manifest FROM runs WHERE id=$run"; command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.IsDBNull(0) || reader.GetInt64(0) > MaximumManifestBytes) throw new IOException("审计清单超出读取上限。");
        var json = reader.GetString(1);
        var decoded = CleanupManifestCodec.Decode(json, run, cancellation);
        cachedManifestBytes = Encoding.UTF8.GetBytes(json); cachedManifestRun = run; cachedManifest = decoded;
        return decoded;
    }

    private static bool ManifestBytesEqual(Stream stored, byte[] expected)
    {
        if (stored.Length != expected.Length) return false;
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            for (var offset = 0; offset < expected.Length;)
            {
                var count = Math.Min(buffer.Length, expected.Length - offset);
                var read = stored.Read(buffer, 0, count);
                if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, read))) return false;
                offset += read;
            }
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    internal (bool RunFound, long ThroughSequence) EventWindow(string run, long afterSequence, long? throughSequence, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (afterSequence < 0 || throughSequence < 0 || afterSequence > 0 && throughSequence is null)
            throw new ArgumentException("事件分页必须使用非负游标及首轮返回的快照上界。");
        lock (sync)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM runs WHERE id=$run),COALESCE(MAX(seq),0) FROM events WHERE run=$run";
            command.Parameters.AddWithValue("$run", run);
            bool found; long maximum;
            using (var reader = command.ExecuteReader()) { reader.Read(); found = reader.GetInt64(0) != 0; maximum = reader.GetInt64(1); }
            if (!found && maximum != 0) throw new IOException("事件存在但原任务缺失，审计不完整。");
            var through = throughSequence ?? maximum;
            if (through > maximum || afterSequence > through || !Belongs(through) || !Belongs(afterSequence))
                throw new ArgumentException("事件游标或快照上界不属于此任务。");
            cancellation.ThrowIfCancellationRequested();
            return (found, through);
            bool Belongs(long sequence)
            {
                if (sequence == 0) return true;
                using var cursor = connection.CreateCommand(); cursor.CommandText = "SELECT 1 FROM events WHERE seq=$seq AND run=$run";
                cursor.Parameters.AddWithValue("$seq", sequence); cursor.Parameters.AddWithValue("$run", run);
                return cursor.ExecuteScalar() is not null;
            }
        }
    }
    internal IReadOnlyDictionary<string, CleanupEvent> LatestEvents(string run, IEnumerable<string> paths, long throughSequence, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var remaining = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) { cancellation.ThrowIfCancellationRequested(); remaining.Add(path); }
        var latest = new Dictionary<string, CleanupEvent>(StringComparer.OrdinalIgnoreCase);
        if (remaining.Count == 0) return latest;
        lock (sync)
        {
            cancellation.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT seq,path,state,reason,time FROM events WHERE run=$run AND seq<=$through ORDER BY seq DESC";
            command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$through", throughSequence);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                var path = reader.GetString(1);
                if (!remaining.Remove(path)) continue;
                latest.Add(path, ReadEvent(reader));
                if (remaining.Count == 0) break;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return latest;
    }
    private static CleanupEvent ReadEvent(SqliteDataReader reader) => new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)) { Sequence = reader.GetInt64(0) };
    internal static void CheckPageSize(int pageSize)
    { if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize), "每页必须为 1～1000 项。"); }
    public void Dispose()
    {
        lock (sync)
        {
            connection.Dispose(); owner.Dispose(); directory.Dispose();
            cachedManifestBytes = null; cachedManifestRun = null; cachedManifest = null;
        }
    }
}

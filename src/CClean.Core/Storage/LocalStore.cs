using System.Text.Json;
using CClean.Core.Files;
using CClean.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace CClean.Core.Storage;

public sealed record IndexedFile(string ProjectId, string Path, string? Identity, long? LogicalBytes, long? AllocatedBytes, long InitialBytes, string? Exclusion, FileSnapshot? Snapshot = null)
{
    public string DisplayPath { get; init; } = Path;
    public long? Growth => LogicalBytes is null ? null : LogicalBytes - InitialBytes;
    public string LogicalDisplay => FormatBytes(LogicalBytes);
    public string AllocationDisplay => FormatBytes(AllocatedBytes);
    public string GrowthDisplay => (Growth > 0 ? "+" : "") + FormatBytes(Growth);
    public static string FormatBytes(long? bytes) => bytes is null ? "未知" : Math.Abs((double)bytes.Value) < 1024 ? $"{bytes} B" :
        Math.Abs((double)bytes.Value) < 1048576 ? $"{bytes / 1024d:F2} KiB" :
        Math.Abs((double)bytes.Value) < 1073741824 ? $"{bytes / 1048576d:F2} MiB" : $"{bytes / 1073741824d:F2} GiB";
}

public sealed record StoredChange(long Id, DateTimeOffset Time, string ProjectId, string Path, string? Identity, long? Before, long? After, string Kind);
public sealed record StoreTotals(long Logical, long Allocated, long Growth, int Unknown, int Files);
public sealed record CoverageGap(DateTimeOffset Time, string ProjectId, string Reason);

public sealed class LocalStore : IDisposable
{
    private readonly object sync = new();
    private readonly SqliteConnection connection;
    public string Root { get; }
    public LocalStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "index.db"), Pooling = false }.ToString());
        try
        {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=1000;
            CREATE TABLE IF NOT EXISTS roots(id TEXT PRIMARY KEY, initialized INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS baselines(identity TEXT PRIMARY KEY,logical INTEGER NOT NULL,volume TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(project TEXT NOT NULL,path TEXT NOT NULL COLLATE NOCASE,identity TEXT,logical INTEGER,allocated INTEGER,initial INTEGER NOT NULL,snapshot TEXT NOT NULL,PRIMARY KEY(project,path));
            CREATE TABLE IF NOT EXISTS changes(id INTEGER PRIMARY KEY,time TEXT NOT NULL,project TEXT NOT NULL,path TEXT NOT NULL,identity TEXT,before_size INTEGER,after_size INTEGER,kind TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS changes_time ON changes(time);
            CREATE TABLE IF NOT EXISTS gaps(id INTEGER PRIMARY KEY,time TEXT NOT NULL,project TEXT NOT NULL,reason TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(version.ExecuteScalar()) > 1) throw new IOException("索引版本高于当前应用，拒绝降级写入。");
        command.ExecuteNonQuery();
        }
        catch { connection.Dispose(); throw; }
    }

    public Dictionary<string, FileSnapshot> LoadSnapshots(string project)
    {
        lock (sync)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path,snapshot FROM files WHERE project=$p LIMIT 100000";
            command.Parameters.AddWithValue("$p", project);
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var file = JsonSerializer.Deserialize<FileSnapshot>(reader.GetString(1))!;
                result[file.Path] = file;
            }
            return result;
        }
    }

    public bool SaveSnapshot(string project, DirectorySnapshot next, IReadOnlyDictionary<string, FileSnapshot> previous)
    {
        lock (sync)
        {
            var space = VolumeSpaceReader.Read(Root);
            if (!RuntimeBudget.Evaluate(RuntimeBudget.Measure(Root), (long)space.UserAvailableBytes, true, 1024 * 1024, (long)space.TotalBytes).Collect) return false;
            using var initializedCommand = connection.CreateCommand();
            initializedCommand.CommandText = "SELECT initialized FROM roots WHERE id=$p";
            initializedCommand.Parameters.AddWithValue("$p", project);
            var initial = initializedCommand.ExecuteScalar() is null;
            var current = next.FilesByPath;
            var changed = current.Values.Where(f => !previous.TryGetValue(f.Path, out var old) || f != old);
            foreach (var batch in changed.Chunk(500))
            {
                space = VolumeSpaceReader.Read(Root);
                var allowance = batch.Sum(f => checked(JsonSerializer.Serialize(f).Length * 8L + f.Path.Length * 16L + 8192));
                if (!RuntimeBudget.Evaluate(RuntimeBudget.Measure(Root), (long)space.UserAvailableBytes, true, allowance, (long)space.TotalBytes).Collect) return false;
                using var transaction = connection.BeginTransaction();
                foreach (var file in batch)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO files VALUES($p,$path,$id,$logical,$allocated,$initial,$json) ON CONFLICT(project,path) DO UPDATE SET identity=excluded.identity,logical=excluded.logical,allocated=excluded.allocated,snapshot=excluded.snapshot";
                    command.Parameters.AddWithValue("$p", project); command.Parameters.AddWithValue("$path", file.Path);
                    command.Parameters.AddWithValue("$id", (object?)file.Identity ?? DBNull.Value);
                    command.Parameters.AddWithValue("$logical", (object?)file.LogicalBytes ?? DBNull.Value);
                    command.Parameters.AddWithValue("$allocated", (object?)file.AllocatedBytes ?? DBNull.Value);
                    command.Parameters.AddWithValue("$initial", initial ? file.LogicalBytes ?? 0 : 0);
                    command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(file));
                    command.ExecuteNonQuery();
                    if (file.Identity is not null && file.LogicalBytes is not null)
                    {
                        using var baseline = connection.CreateCommand(); baseline.Transaction = transaction;
                        baseline.CommandText = "INSERT OR IGNORE INTO baselines VALUES($id,$initial,$volume)";
                        baseline.Parameters.AddWithValue("$id", file.Identity); baseline.Parameters.AddWithValue("$initial", initial ? file.LogicalBytes.Value : 0);
                        baseline.Parameters.AddWithValue("$volume", Path.GetPathRoot(file.Path)!); baseline.ExecuteNonQuery();
                    }
                    var before = previous.GetValueOrDefault(file.Path);
                    var kind = initial ? "初始存量" : next.Gaps.Count != 0 || file.LogicalBytes is null ? "未知" : before is null ? "新增" : before.LogicalBytes is null ? "未知" : "变化";
                    AddChange(transaction, next.CapturedAt, project, file.Path, file.Identity, before?.LogicalBytes, file.LogicalBytes, kind);
                }
                transaction.Commit();
            }
            if (next.Gaps.Count == 0)
            {
                foreach (var batch in previous.Values.Where(f => !current.ContainsKey(f.Path)).Chunk(500))
                {
                    space = VolumeSpaceReader.Read(Root);
                    var allowance = batch.Sum(f => checked(f.Path.Length * 16L + 8192));
                    if (!RuntimeBudget.Evaluate(RuntimeBudget.Measure(Root), (long)space.UserAvailableBytes, true, allowance, (long)space.TotalBytes).Collect) return false;
                    using var transaction = connection.BeginTransaction();
                    foreach (var old in batch)
                    {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "DELETE FROM files WHERE project=$p AND path=$path";
                    command.Parameters.AddWithValue("$p", project); command.Parameters.AddWithValue("$path", old.Path); command.ExecuteNonQuery();
                    AddChange(transaction, next.CapturedAt, project, old.Path, old.Identity, old.LogicalBytes, null, "消失");
                    }
                    transaction.Commit();
                }
            }
            using var finish = connection.CreateCommand();
            finish.CommandText = "INSERT OR IGNORE INTO roots VALUES($p,1); DELETE FROM changes WHERE time < $cutoff; DELETE FROM changes WHERE id NOT IN (SELECT id FROM changes ORDER BY id DESC LIMIT 100000); PRAGMA wal_checkpoint(PASSIVE);";
            finish.Parameters.AddWithValue("$p", project); finish.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-7).ToString("O")); finish.ExecuteNonQuery();
            return true;
        }
    }

    private void AddChange(SqliteTransaction transaction, DateTimeOffset time, string project, string path, string? identity, long? before, long? after, string kind)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO changes(time,project,path,identity,before_size,after_size,kind) VALUES($time,$p,$path,$id,$before,$after,$kind)";
        command.Parameters.AddWithValue("$time", time.ToString("O")); command.Parameters.AddWithValue("$p", project); command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$id", (object?)identity ?? DBNull.Value); command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value); command.Parameters.AddWithValue("$kind", kind); command.ExecuteNonQuery();
    }

    public IReadOnlyList<IndexedFile> QueryFiles(string search = "", int page = 0, string? project = null, string sort = "allocated", bool? special = null, string? root = null)
    {
        lock (sync)
        {
            using var command = connection.CreateCommand();
            var order = sort switch { "growth" => "files.logical-COALESCE(baselines.logical,0) DESC", "modified" => "json_extract(snapshot,'$.ModifiedTime') DESC", _ => "allocated DESC" };
            command.CommandText = "SELECT project,path,files.identity,files.logical,allocated,COALESCE(baselines.logical,0),snapshot FROM files LEFT JOIN baselines ON files.identity=baselines.identity WHERE instr(lower(path),lower($search))>0 AND ($project IS NULL OR project=$project) AND " + PathScope + " AND ($special IS NULL OR (json_extract(snapshot,'$.Exclusion') IS NOT NULL)=$special) ORDER BY " + order + ",path LIMIT 200 OFFSET $offset";
            BindPathScope(command, root);
            command.Parameters.AddWithValue("$search", search); command.Parameters.AddWithValue("$offset", checked(Math.Max(page, 0) * 200));
            command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
            command.Parameters.AddWithValue("$special", special is null ? DBNull.Value : special.Value ? 1 : 0);
            using var reader = command.ExecuteReader(); var files = new List<IndexedFile>();
            while (reader.Read())
            {
                var snapshot = JsonSerializer.Deserialize<FileSnapshot>(reader.GetString(6));
                files.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt64(5),
                snapshot?.Exclusion, snapshot));
            }
            return files;
        }
    }

    public StoreTotals Totals(string volumeRoot) => ReadTotals(null, null, Path.GetPathRoot(Path.GetFullPath(volumeRoot))!);

    // Project growth describes files still in this scope; it is not the volume's historical net change.
    public StoreTotals ProjectTotals(string project, string? root = null) => ReadTotals(project, root, null);

    private StoreTotals ReadTotals(string? project, string? root, string? volume)
    {
        lock (sync)
        {
            using var command = connection.CreateCommand();
            // Aggregate in SQLite so every overview refresh does not allocate an identity string and
            // a HashSet entry for every indexed file. The first stored path supplies a shared identity.
            command.CommandText = """
                WITH scoped AS (
                    SELECT rowid,identity,logical,allocated,
                        json_extract(snapshot,'$.StreamsComplete') AS streams_complete,
                        json_extract(snapshot,'$.HasNamedStreams') AS named_streams
                    FROM files
                    WHERE ($project IS NULL OR project=$project)
                      AND ($volume IS NULL OR substr(path,1,length($volume))=$volume COLLATE NOCASE)
                      AND
                """ + PathScope + """
                ), unique_files AS (
                    SELECT identity,MIN(rowid),logical,allocated FROM scoped
                    WHERE identity IS NOT NULL AND logical IS NOT NULL AND allocated IS NOT NULL
                    GROUP BY identity
                )
                SELECT COALESCE(SUM(logical),0),COALESCE(SUM(allocated),0),
                    CASE WHEN $volume IS NULL THEN
                        COALESCE(SUM(logical-COALESCE((SELECT logical FROM baselines WHERE baselines.identity=unique_files.identity),0)),0)
                    ELSE COALESCE(SUM(logical),0)-(SELECT COALESCE(SUM(logical),0) FROM baselines WHERE volume=$volume COLLATE NOCASE) END,
                    (SELECT COUNT(*) FROM scoped WHERE identity IS NULL OR logical IS NULL OR allocated IS NULL
                        OR streams_complete IS NOT 1 OR named_streams IS NOT 0),
                    (SELECT COUNT(*) FROM scoped)
                FROM unique_files
                """;
            command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
            command.Parameters.AddWithValue("$volume", (object?)volume ?? DBNull.Value);
            BindPathScope(command, root);
            using var reader = command.ExecuteReader();
            reader.Read();
            return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4));
        }
    }

    public IReadOnlyList<StoredChange> Changes(DateTimeOffset since, int limit = 5000, string? project = null, string? root = null)
    {
        lock (sync)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,time,project,path,identity,before_size,after_size,kind FROM changes WHERE time >= $since AND ($project IS NULL OR project=$project) AND " + PathScope + " ORDER BY id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
            BindPathScope(command, root);
            command.Parameters.AddWithValue("$since", since.ToString("O")); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
            using var reader = command.ExecuteReader(); var changes = new List<StoredChange>();
            while (reader.Read()) changes.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetString(7)));
            return changes;
        }
    }
    public DateTimeOffset EarliestHistory(string? project = null, string? root = null)
    {
        lock (sync)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT MIN(time) FROM changes WHERE ($project IS NULL OR project=$project) AND " + PathScope;
            command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
            BindPathScope(command, root);
            return command.ExecuteScalar() is string time ? DateTimeOffset.Parse(time) : DateTimeOffset.UtcNow;
        }
    }
    public void RecordGap(string project, string reason, DateTimeOffset? observedAt = null)
    {
        lock (sync)
        {
            var space = VolumeSpaceReader.Read(Root);
            if (!RuntimeBudget.Evaluate(RuntimeBudget.Measure(Root), (long)space.UserAvailableBytes, true, 16384, (long)space.TotalBytes).Collect)
                throw new IOException("预算不足，覆盖缺口未能持久化。");
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO gaps(time,project,reason) VALUES($t,$p,$r); DELETE FROM gaps WHERE time<$cutoff; DELETE FROM gaps WHERE id NOT IN (SELECT id FROM gaps ORDER BY id DESC LIMIT 10000);";
            command.Parameters.AddWithValue("$t", (observedAt ?? DateTimeOffset.UtcNow).ToString("O")); command.Parameters.AddWithValue("$p", project);
            command.Parameters.AddWithValue("$r", reason.Length > 1000 ? reason[..1000] : reason);
            command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-7).ToString("O")); command.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<CoverageGap> Gaps(DateTimeOffset since, string? project = null)
    {
        lock (sync)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT time,project,reason FROM gaps WHERE time >= $since AND ($project IS NULL OR project=$project) ORDER BY id DESC LIMIT 10000";
            command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
            command.Parameters.AddWithValue("$since", since.ToString("O")); using var reader = command.ExecuteReader();
            var gaps = new List<CoverageGap>();
            while (reader.Read()) gaps.Add(new(DateTimeOffset.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
            return gaps;
        }
    }
    public void Dispose() { lock (sync) connection.Dispose(); }
    private const string PathScope = "($root IS NULL OR path=$root COLLATE NOCASE OR substr(path,1,length($prefix))=$prefix COLLATE NOCASE)";
    private static void BindPathScope(Microsoft.Data.Sqlite.SqliteCommand command, string? root)
    {
        root = root is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        command.Parameters.AddWithValue("$root", (object?)root ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefix", root is null ? DBNull.Value : root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
    }
}

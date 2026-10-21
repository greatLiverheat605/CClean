using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CClean.Core.Experiments;

namespace CClean.Core.Sessions;

public sealed record DiscoveredSession(string Id, string Tool, string ProjectRoot, DateTimeOffset Started,
    DateTimeOffset Updated, string SourceFile)
{
    public string ProjectName => Path.GetFileName(Path.TrimEndingDirectorySeparator(ProjectRoot));
    public string UpdatedLocal => Updated.ToLocalTime().ToString("MM-dd HH:mm");
    public string StateLabel => "已发现 · 运行状态未知";
}
public sealed record SessionSource(string Tool, string Root);
public sealed record SessionDiscoveryResult(IReadOnlyList<DiscoveredSession> Sessions, bool HasMore, int Skipped, string Status);

// Read only bounded metadata prefixes. Never open tool databases or retain message bodies.
public sealed class SessionDiscovery : IDisposable
{
    private readonly SessionSource[] sources;
    private readonly Queue<(SessionSource Source, string Directory)> pending = new();
    private readonly Dictionary<string, (long Length, DateTime Modified, DiscoveredSession? Session)> cache = new(StringComparer.OrdinalIgnoreCase);
    private IEnumerator<string>? entries;
    private SessionSource? current;
    private string currentDirectory = "";
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    private bool started;
    private int skipped;
    private bool incomplete;
    public SessionDiscovery(IEnumerable<SessionSource>? sources = null)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.sources = (sources ?? new[] {
            new SessionSource("Codex", Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(profile, ".codex"), "sessions")),
            new SessionSource("Claude Code", Path.Combine(profile, ".claude", "projects")) }).ToArray();
    }

    public SessionDiscoveryResult Next(CancellationToken cancellation, int maxFiles = 200, TimeSpan? budget = null)
    {
        if (maxFiles is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        if (!started)
        {
            started = true; skipped = 0; incomplete = false; seen.Clear();
            foreach (var source in sources)
                if (Directory.Exists(source.Root)) pending.Enqueue((source, source.Root));
                else { skipped++; incomplete = true; }
        }
        var timer = Stopwatch.StartNew(); var inspected = 0;
        while (inspected < maxFiles && timer.Elapsed < (budget ?? TimeSpan.FromSeconds(2)))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (entries is null)
                {
                    if (!pending.TryDequeue(out var next)) break;
                    current = next.Source; currentDirectory = next.Directory;
                    using var lease = new DirectoryLease(currentDirectory);
                    entries = Directory.EnumerateFileSystemEntries(currentDirectory).GetEnumerator();
                }
                using var boundary = new DirectoryLease(currentDirectory);
                if (!entries.MoveNext()) { entries.Dispose(); entries = null; continue; }
                inspected++;
                try
                {
                    var path = entries.Current;
                    var attributes = File.GetAttributes(path);
                    if (((int)attributes & (0x400 | 0x1000 | 0x40000 | 0x400000)) != 0) { skipped++; incomplete = true; continue; }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (Path.GetFileName(path).Equals("subagents", StringComparison.OrdinalIgnoreCase)) continue;
                        if (pending.Count < 2000) pending.Enqueue((current!, path)); else { skipped++; incomplete = true; }
                        continue;
                    }
                    if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                        (current!.Tool == "Codex" && !Path.GetFileName(path).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))) continue;
                    if (cache.Count >= 2000 && !cache.ContainsKey(path)) { skipped++; incomplete = true; continue; }
                    var info = new FileInfo(path);
                    if (cache.TryGetValue(path, out var old) && old.Length == info.Length && old.Modified == info.LastWriteTimeUtc) { seen.Add(path); continue; }
                    var session = ReadMetadata(path, current.Tool, cancellation);
                    if (session is not null) { cache[path] = (info.Length, info.LastWriteTimeUtc, session); seen.Add(path); }
                    else { skipped++; incomplete = true; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExperimentRefusedException or JsonException or ArgumentException)
                { skipped++; incomplete = true; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExperimentRefusedException or JsonException or ArgumentException)
            { skipped++; incomplete = true; entries?.Dispose(); entries = null; }
        }
        var more = entries is not null || pending.Count > 0;
        if (!more)
        {
            // Failed or bounded rounds cannot prove that a previously discovered project disappeared.
            if (!incomplete) foreach (var path in cache.Keys.Where(p => !seen.Contains(p)).ToArray()) cache.Remove(path);
            started = false;
        }
        var rows = cache.Values.Where(v => v.Session is not null).Select(v => v.Session!)
            .GroupBy(s => (s.Tool, s.Id)).Select(g => g.OrderByDescending(s => s.Updated).First())
            .OrderByDescending(s => s.Updated).ToArray();
        return new(rows, more, skipped, $"{(more ? "分批发现中" : "本轮发现完成")} · {rows.Length} 个会话 · 跳过 / 不支持 {skipped} 项。最多缓存 2,000 个文件；ZCode 尚无已验证元数据适配。运行状态未知，不能据此判断可删除。");
    }

    public static DiscoveredSession? ReadMetadata(string path, string tool, CancellationToken cancellation = default)
    {
        using var lease = new DirectoryLease(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096);
        var remaining = 262144;
        for (var lineNumber = 0; lineNumber < (tool == "Codex" ? 1 : 16); lineNumber++)
        {
            cancellation.ThrowIfCancellationRequested();
            var line = new StringBuilder();
            while (remaining-- > 0)
            {
                var character = reader.Read();
                if (character < 0 || character == '\n') break;
                line.Append((char)character);
            }
            if (remaining <= 0 || line.Length == 0) return null;
            using var json = JsonDocument.Parse(line.ToString());
            var top = json.RootElement;
            if (top.ValueKind != JsonValueKind.Object) return null;
            var data = top;
            if (tool == "Codex")
            {
                if (Text(top, "type") != "session_meta" || !top.TryGetProperty("payload", out data) || data.ValueKind != JsonValueKind.Object) return null;
            }
            var id = Text(data, tool == "Codex" ? "id" : "sessionId");
            var root = Text(data, "cwd");
            if (id is null || root is null) continue;
            if (id.Length > 160 || root.Length > 32760 || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return null;
            root = Path.GetFullPath(root);
            var started = DateTimeOffset.TryParse(Text(data, "timestamp"), out var timestamp) ? timestamp : new DateTimeOffset(File.GetCreationTimeUtc(path));
            return new(id, tool, root, started, new DateTimeOffset(File.GetLastWriteTimeUtc(path)), path);
        }
        return null;
    }
    private static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public void Dispose() { entries?.Dispose(); entries = null; pending.Clear(); }
}

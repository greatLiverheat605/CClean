using System.Diagnostics;
using CClean.Core.Experiments;
using CClean.Core.Files;

namespace CClean.Core.Monitoring;

public sealed record DiscoveryCandidate(string Path, long KnownBytes, int Files, int Unknown);
public sealed record DiscoveryBatch(IReadOnlyList<DiscoveryCandidate> Candidates, int Inspected, bool HasMore, IReadOnlyList<string> Gaps);

public sealed class DirectoryDiscovery : IDisposable
{
    private readonly Stack<string> pending = new();
    private readonly Dictionary<string, DiscoveryCandidate> candidates = new(StringComparer.OrdinalIgnoreCase);
    private IEnumerator<string>? entries;
    private string? currentDirectory;
    public DirectoryDiscovery(string root)
    {
        root = ExperimentSandbox.Normalize(root);
        using var lease = new DirectoryLease(root);
        pending.Push(root);
    }

    public DiscoveryBatch Next(CancellationToken cancellation, int maxObjects = 100_000, TimeSpan? timeBudget = null)
    {
        if (maxObjects <= 0 || maxObjects > 100_000) throw new ArgumentOutOfRangeException(nameof(maxObjects));
        var timer = Stopwatch.StartNew();
        var inspected = 0;
        var gaps = new List<string>();
        while (inspected < maxObjects && timer.Elapsed < (timeBudget ?? TimeSpan.FromSeconds(30)))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (entries is null)
                {
                    if (!pending.TryPop(out currentDirectory)) break;
                    using var lease = new DirectoryLease(currentDirectory);
                    entries = Directory.EnumerateFileSystemEntries(currentDirectory).GetEnumerator();
                }
                using var boundary = new DirectoryLease(currentDirectory!);
                if (!entries.MoveNext()) { entries.Dispose(); entries = null; continue; }
                inspected++;
                var entry = entries.Current;
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) { gaps.Add("链接已排除"); continue; }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (pending.Count >= 100_000) throw new IOException("待扫描目录超过上限。");
                    pending.Push(entry);
                    continue;
                }
                var file = WindowsFileMetadata.Read(entry);
                var folder = Path.GetDirectoryName(entry)!;
                var previous = candidates.GetValueOrDefault(folder) ?? new(folder, 0, 0, 0);
                if (candidates.Count >= 100_000 && !candidates.ContainsKey(folder)) throw new IOException("候选目录超过上限。");
                candidates[folder] = previous with { KnownBytes = checked(previous.KnownBytes + (file.LogicalBytes ?? 0)),
                    Files = previous.Files + 1, Unknown = previous.Unknown + (file.LogicalBytes is null ? 1 : 0) };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExperimentRefusedException)
            {
                if (gaps.Count < 1000) gaps.Add(error.Message);
                entries?.Dispose(); entries = null;
            }
        }
        return new(candidates.Values.OrderByDescending(c => c.KnownBytes).Take(200).ToArray(), inspected,
            entries is not null || pending.Count > 0, gaps);
    }

    public void Dispose() { entries?.Dispose(); entries = null; pending.Clear(); }
}

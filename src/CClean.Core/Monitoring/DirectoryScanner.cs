using CClean.Core.Files;

namespace CClean.Core.Monitoring;

public sealed record DirectorySnapshot(IReadOnlyDictionary<string, FileSnapshot> Files, IReadOnlyList<string> Gaps, DateTimeOffset CapturedAt)
{
    public bool PathsAreAbsolute { get; init; }
    public IReadOnlyDictionary<string, FileSnapshot> FilesByPath => PathsAreAbsolute ? Files :
        Files.Values.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
}

public static class DirectoryScanner
{
    public static DirectorySnapshot Scan(string root, CancellationToken cancellation = default, int maxFiles = 100_000,
        IReadOnlyDictionary<string, FileSnapshot>? previous = null, bool absolutePaths = false)
    {
        var files = new Dictionary<string, FileSnapshot>(Math.Min(previous?.Count ?? 0, Math.Max(0, maxFiles)), StringComparer.OrdinalIgnoreCase);
        var gaps = new List<string>();
        var pending = new Stack<string>();
        var directories = 0;
        pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested();
            if (++directories > maxFiles)
            {
                gaps.Add("Snapshot directory limit exceeded.");
                break;
            }
            try
            {
                using var lease = new Experiments.DirectoryLease(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        if (gaps.Count < 1000) gaps.Add("Reparse point excluded");
                        continue;
                    }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (pending.Count >= maxFiles) throw new IOException("Pending directory limit exceeded.");
                        pending.Push(entry);
                        continue;
                    }
                    if (files.Count >= maxFiles) throw new IOException("Snapshot file limit exceeded.");
                    var file = WindowsFileMetadata.Read(entry, lease);
                    var key = absolutePaths ? file.Path : Path.GetRelativePath(root, entry);
                    if (previous is not null && previous.TryGetValue(key, out var old) && old == file) file = old;
                    files.Add(absolutePaths ? file.Path : key, file);
                    if (file.Identity is null && gaps.Count < 1000) gaps.Add("Metadata unavailable");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Experiments.ExperimentRefusedException)
            {
                if (gaps.Count < 1000) gaps.Add(error.Message);
                if (files.Count >= maxFiles) break;
            }
        }
        return new(files, gaps, DateTimeOffset.UtcNow) { PathsAreAbsolute = absolutePaths };
    }
}

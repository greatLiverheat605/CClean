using CClean.Core.Experiments;
using CClean.Core.Files;

namespace CClean.Core.Monitoring;

internal static class IncrementalRefresh
{
    internal static DirectorySnapshot? TryApply(string root, DirectorySnapshot current, IReadOnlyList<DirectoryNotification> events,
        bool absolutePaths, CancellationToken cancellation, int maxFiles = 100_000)
    {
        if (current.Gaps.Count != 0 || current.CapturedAt == DateTimeOffset.MinValue || current.PathsAreAbsolute != absolutePaths)
            return null;

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in events)
        {
            if (entry.OldPath is string oldPath &&
                (oldPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) || !Add(oldPath))) return null;
            if (!Add(entry.Path)) return null;
        }

        try
        {
            var files = current.Files;
            var changes = new List<(string Key, FileSnapshot? File)>();
            foreach (var group in paths.GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                using var parent = new DirectoryLease(group.Key);
                foreach (var path in group)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var key = absolutePaths ? path : path[prefix.Length..];
                    files.TryGetValue(key, out var previous);
                    if (previous is { LinkCount: not 1 }) return null;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (FileNotFoundException)
                    {
                        // An unindexed path could have been a directory with descendants.
                        if (previous is null) return null;
                        changes.Add((key, null));
                        continue;
                    }
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
                    var file = WindowsFileMetadata.Read(path, parent, previous);
                    if (file.Identity is null || file.LinkCount != 1 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                    if (!ReferenceEquals(file, previous)) changes.Add((key, file));
                }
            }

            var capturedAt = DateTimeOffset.UtcNow;
            if (changes.Count == 0) return current with { CapturedAt = capturedAt };
            // ponytail: Copy once per changed snapshot. Revisit if profiling shows this drives peak memory.
            var next = new Dictionary<string, FileSnapshot>(files, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, file) in changes)
            {
                if (file is null) next.Remove(key);
                else next[key] = file;
            }
            if (next.Count > maxFiles) return null;
            return new(next, [], capturedAt) { PathsAreAbsolute = absolutePaths };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExperimentRefusedException)
        {
            return null;
        }

        bool Add(string path)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length == prefix.Length) return false;
            // Short names cannot be matched to the long-name keys of a complete scan.
            for (var i = prefix.Length; i + 1 < path.Length; i++)
                if (path[i] == '~' && char.IsAsciiDigit(path[i + 1])) return false;
            if (paths.TryGetValue(path, out var existing)) return path.Equals(existing, StringComparison.Ordinal);
            paths.Add(path);
            return true;
        }
    }
}

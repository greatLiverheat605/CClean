using System.ComponentModel;
using System.Diagnostics;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Monitoring;

namespace CClean.Core.Projects;

public enum ProjectDifference { LeftOnly, RightOnly, SizeDifferent, ContentEqual, ContentDifferent, Unknown }
public sealed record ProjectComparisonItem(string RelativePath, long? LeftBytes, long? RightBytes,
    ProjectDifference Difference, string Reason);
public sealed record ProjectComparisonResult(IReadOnlyList<ProjectComparisonItem> Items,
    bool LeftComplete, bool RightComplete, bool Limited, long ReadBytes, int HiddenSystemObjects);

// Comparison is evidence for manual review, never a cleanup rule or deletion authorization.
public static class ProjectComparison
{
    private sealed record Entry(FileSnapshot? File, string? Exclusion);
    private sealed class Inventory
    {
        public readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DateTime> Directories = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> PathSpellings = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> UnreadScopes = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> HiddenScopes = new(StringComparer.OrdinalIgnoreCase);
        public bool Complete = true;
        public bool EnumerationComplete = true;
    }
    private const FileAttributes Unreadable = FileAttributes.ReparsePoint | FileAttributes.Offline |
        FileAttributes.Device | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    public static ProjectComparisonResult Compare(string leftRoot, string rightRoot, CancellationToken cancellation = default,
        int maxEntries = 10_000, long maxReadBytes = 512L * 1024 * 1024, TimeSpan? timeBudget = null)
    {
        if (maxEntries is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxReadBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxReadBytes));
        var budget = timeBudget ?? TimeSpan.FromSeconds(30);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeBudget));
        var left = ValidateRoot(leftRoot); var right = ValidateRoot(rightRoot);
        if (ProtectionPolicy.Contains(left, right) || ProtectionPolicy.Contains(right, left))
            throw new ArgumentException("请选择两个互不包含的版本文件夹，不能比较同一目录或父子目录。");
        using var leftLease = new DirectoryLease(left);
        using var rightLease = new DirectoryLease(right);
        var timer = Stopwatch.StartNew();
        var limited = false; var hidden = 0; long readBytes = 0;
        bool WithinBudget()
        {
            cancellation.ThrowIfCancellationRequested();
            if (timer.Elapsed < budget) return true;
            limited = true; return false;
        }
        Inventory Scan(string root)
        {
            var inventory = new Inventory(); var pending = new Stack<string>(); pending.Push(root); var inspected = 0;
            while (pending.TryPop(out var directory))
            {
                if (!WithinBudget()) { inventory.Complete = inventory.EnumerationComplete = false; break; }
                if (WithinScope(inventory.UnreadScopes, Path.GetRelativePath(root, directory))) continue;
                try
                {
                    using var lease = new DirectoryLease(directory);
                    inventory.Directories[directory] = Directory.GetLastWriteTimeUtc(directory);
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (!WithinBudget() || ++inspected > maxEntries)
                        { inventory.Complete = inventory.EnumerationComplete = false; limited = true; return inventory; }
                        var relative = Path.GetRelativePath(root, path);
                        try
                        {
                            var attributes = File.GetAttributes(path);
                            if (attributes.HasFlag(FileAttributes.System) || SpaceSurvey.IsSystemPath(path))
                            { hidden++; inventory.HiddenScopes.Add(relative); inventory.UnreadScopes.Add(relative); inventory.Complete = false; continue; }
                            var collision = ObserveRelativePath(inventory.PathSpellings, relative);
                            if (collision is not null)
                            {
                                inventory.Entries[collision] = new(null, "存在仅大小写不同的名称，对应范围未比较");
                                inventory.UnreadScopes.Add(collision); inventory.Complete = false; continue;
                            }
                            if (Sensitive(path))
                            { inventory.Entries[relative] = new(null, "敏感目录、凭据或工具私有数据，未读取内容"); inventory.UnreadScopes.Add(relative); inventory.Complete = false; continue; }
                            if ((attributes & Unreadable) != 0)
                            { inventory.Entries[relative] = new(null, "链接、云占位或特殊对象，未读取内容"); inventory.UnreadScopes.Add(relative); inventory.Complete = false; continue; }
                            if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(path); continue; }
                            var file = WindowsFileMetadata.Read(path, lease);
                            inventory.Entries[relative] = new(file, file.IsOrdinaryFile ? null : "元数据、数据流或普通文件条件不完整，未读取内容");
                            if (!file.IsOrdinaryFile) inventory.Complete = false;
                        }
                        catch (Exception error) when (ReadFailure(error))
                        { inventory.Entries[relative] = new(null, "无法完整读取此位置"); inventory.UnreadScopes.Add(relative); inventory.Complete = false; }
                    }
                }
                catch (Exception error) when (ReadFailure(error))
                {
                    inventory.Entries[Path.GetRelativePath(root, directory)] = new(null, "目录读取不完整");
                    inventory.UnreadScopes.Add(Path.GetRelativePath(root, directory));
                    inventory.Complete = false;
                }
            }
            return inventory;
        }
        var a = Scan(left); var b = Scan(right);
        var rows = new List<ProjectComparisonItem>();
        var hiddenScopes = a.HiddenScopes.Union(b.HiddenScopes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leftBuffer = new byte[64 * 1024]; var rightBuffer = new byte[64 * 1024];
        void ReadChunk(FileStream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                cancellation.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0) throw new EndOfStreamException();
                readBytes += read; offset += read;
            }
        }
        foreach (var relative in a.Entries.Keys.Union(b.Entries.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            if (WithinScope(hiddenScopes, relative)) continue;
            a.Entries.TryGetValue(relative, out var first); b.Entries.TryGetValue(relative, out var second);
            var x = first?.File; var y = second?.File;
            var state = ProjectDifference.Unknown;
            var reason = first?.Exclusion ?? second?.Exclusion ?? "尚未比较";
            var unread = WithinScope(a.UnreadScopes, relative) || WithinScope(b.UnreadScopes, relative);
            if (unread && first?.Exclusion is null && second?.Exclusion is null) reason = "对应范围存在读取缺口或大小写名称冲突，未比较";
            if (!unread && first?.Exclusion is null && second?.Exclusion is null && WithinBudget())
            {
                if (x is null || y is null)
                {
                    var other = x is null ? a : b; var otherRoot = x is null ? left : right;
                    var otherComplete = other.EnumerationComplete && !WithinScope(other.UnreadScopes, relative) &&
                        !other.Directories.ContainsKey(Path.Combine(otherRoot, relative));
                    if (otherComplete) { state = x is null ? ProjectDifference.RightOnly : ProjectDifference.LeftOnly; reason = "另一侧对应范围已完整盘点，未发现同相对路径；须保留此文件"; }
                    else reason = "另一侧盘点有缺口，不能确认独有，保持保护";
                }
                else if (x.LogicalBytes != y.LogicalBytes)
                { state = ProjectDifference.SizeDifferent; reason = "同相对路径的文件大小不同，须人工核对"; }
                else if (x.LogicalBytes!.Value > (maxReadBytes - readBytes) / 2)
                { limited = true; reason = "达到内容读取预算，未确认内容是否相同"; }
                else
                {
                    try
                    {
                        using var lp = new DirectoryLease(Path.GetDirectoryName(x.Path)!);
                        using var rp = new DirectoryLease(Path.GetDirectoryName(y.Path)!);
                        // Share-read only prevents concurrent writers for the duration of the content read.
                        using var lh = WindowsFileMetadata.Open(x.Path, 0x80000080, 1);
                        using var rh = WindowsFileMetadata.Open(y.Path, 0x80000080, 1);
                        if (WindowsFileMetadata.Read(lh, x.Path) != x || WindowsFileMetadata.Read(rh, y.Path) != y)
                            throw new IOException("File changed before comparison.");
                        using var ls = new FileStream(lh, FileAccess.Read); using var rs = new FileStream(rh, FileAccess.Read);
                        var equal = true; var complete = true; long remaining = x.LogicalBytes.Value;
                        while (remaining > 0)
                        {
                            if (!WithinBudget()) { complete = false; break; }
                            var count = (int)Math.Min(remaining, leftBuffer.Length);
                            ReadChunk(ls, leftBuffer, count); ReadChunk(rs, rightBuffer, count);
                            remaining -= count;
                            equal &= leftBuffer.AsSpan(0, count).SequenceEqual(rightBuffer.AsSpan(0, count));
                        }
                        if (!complete) reason = "达到时间预算，内容读取不完整";
                        else if (WindowsFileMetadata.Read(lh, x.Path) != x || WindowsFileMetadata.Read(rh, y.Path) != y)
                            reason = "比较期间文件发生变化，结果未知";
                        else
                        { state = equal ? ProjectDifference.ContentEqual : ProjectDifference.ContentDifferent; reason = equal ? "本次逐字节比较相同；不代表目录可删除" : "同相对路径的内容不同，须人工核对"; }
                    }
                    catch (Exception error) when (ReadFailure(error)) { reason = "文件被占用、变化或读取不完整，结果未知"; }
                }
            }
            else if (!unread && first?.Exclusion is null && second?.Exclusion is null) reason = "达到时间预算，未比较";
            rows.Add(new(relative, x?.LogicalBytes, y?.LogicalBytes, state, reason));
        }

        // A late file or directory change invalidates earlier observations, including claims of absence.
        var directoriesStable = true;
        foreach (var pair in a.Directories.Concat(b.Directories))
        {
            if (!WithinBudget()) { directoriesStable = false; break; }
            try
            {
                using var lease = new DirectoryLease(pair.Key);
                if (Directory.GetLastWriteTimeUtc(pair.Key) != pair.Value) directoriesStable = false;
            }
            catch (Exception error) when (ReadFailure(error)) { directoriesStable = false; }
        }
        if (!directoriesStable) { a.Complete = false; b.Complete = false; }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Difference == ProjectDifference.Unknown) continue;
            if (!directoriesStable || !WithinBudget() || !Stable(a, row.RelativePath) || !Stable(b, row.RelativePath))
                rows[i] = row with { Difference = ProjectDifference.Unknown, Reason = "盘点后目录或文件有变化，或无法完成末次复核；保持保护" };
        }
        return new(rows, a.Complete, b.Complete, limited, readBytes, hidden);
    }

    private static bool Stable(Inventory inventory, string relative) =>
        !inventory.Entries.TryGetValue(relative, out var entry) || entry.File is null || WindowsFileMetadata.Read(entry.File.Path) == entry.File;

    // NTFS may opt individual directories into case-sensitive names. Preserve the first spelling
    // and reject the broadest colliding prefix before an ignore-case inventory can overwrite it.
    internal static string? ObserveRelativePath(Dictionary<string, string> spellings, string relative)
    {
        string? collision = null;
        for (string? current = relative; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (spellings.TryGetValue(current, out var original))
            { if (!original.Equals(current, StringComparison.Ordinal)) collision = original; }
            else spellings.Add(current, current);
        }
        return collision;
    }

    private static bool WithinScope(HashSet<string> scopes, string path)
    {
        if (scopes.Contains(".")) return true;
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (scopes.Contains(current)) return true;
        return false;
    }

    private static string ValidateRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择完整的本地目录路径。");
        var root = ExperimentSandbox.Normalize(path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("版本文件夹不存在。");
        if (SpaceSurvey.IsSystemPath(root, true) || Sensitive(root) || root.Equals(Path.GetPathRoot(root)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("系统目录、磁盘根目录和敏感数据目录不能作为版本比较范围。");
        var attributes = File.GetAttributes(root);
        if ((attributes & Unreadable) != 0) throw new ArgumentException("链接或云占位目录不能作为版本比较范围。");
        return root;
    }
    private static bool Sensitive(string path)
    {
        if (ProtectionPolicy.IsSensitiveRoot(path)) return true;
        var name = Path.GetFileName(path);
        return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("credential", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("id_", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".npmrc", ".pypirc", ".netrc", ".git-credentials", ".gitconfig", "auth.json", "tokens.json", "token.json", "NuGet.Config" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            new[] { ".pem", ".key", ".pfx", ".p12", ".kdbx", ".db", ".sqlite", ".sqlite3", ".jsonl" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }
    private static bool ReadFailure(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or ExperimentRefusedException;
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using CClean.Core.Experiments;
using CClean.Core.Storage;

namespace CClean.Core.Monitoring;

public sealed record SpaceFolder(string Path, long Bytes, int Files, bool Project, string Category, string VersionFamily)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    public string Size => IndexedFile.FormatBytes(Bytes);
    public double Share { get; init; }
}
public sealed record SpaceSurveyResult(IReadOnlyList<SpaceFolder> Folders, long Files, long Bytes,
    long Skipped, bool HasMore, bool Limited);

// A bounded, metadata-only inventory. It neither registers watchers nor authorizes deletion.
public sealed class SpaceSurvey : IDisposable
{
    private sealed class Folder(string path, string? parent, bool generated)
    {
        public string Path = path;
        public string? Parent = parent;
        public bool Generated = generated, Project;
        public long Bytes;
        public int Files;
    }
    private static readonly HashSet<string> GeneratedNames = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", ".venv", "venv", "site-packages", "bin", "obj", "__pycache__", "_cacache", ".codex", ".claude", ".zcode", ".vscode", ".cache", ".npm", ".nuget", "vendor" };
    private readonly Dictionary<string, Folder> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> pending = new();
    private readonly HashSet<string> cacheRoots;
    private IEnumerator<string>? entries;
    private string current = "";
    private long files, bytes, skipped;
    private bool limited;
    public string Root { get; }

    public SpaceSurvey(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (IsSystemPath(Root, checkAncestors: true)) throw new ExperimentRefusedException("系统文件和系统目录不参与空间盘点，请选择项目或用户目录。", 3);
        using var lease = new DirectoryLease(Root);
        cacheRoots = new(CommonCacheRoots().Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))), StringComparer.OrdinalIgnoreCase);
        folders.Add(Root, new(Root, null, false));
        pending.Enqueue(Root);
    }

    public static IEnumerable<string> CommonCacheRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "wsl-crashes"), Path.Combine(local, "npm-cache"), Path.Combine(local, "pip", "Cache"),
            Path.Combine(local, "NuGet", "v3-cache"), Path.Combine(profile, ".nuget", "packages"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")];
    }

    public SpaceSurveyResult Next(CancellationToken cancellation, int maxObjects = 20_000, TimeSpan? timeBudget = null)
    {
        if (maxObjects is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxObjects));
        var timer = Stopwatch.StartNew();
        var inspected = 0;
        while (inspected < maxObjects && timer.Elapsed < (timeBudget ?? TimeSpan.FromSeconds(2)) && !limited)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (entries is null)
                {
                    if (!pending.TryDequeue(out current!)) break;
                }
                using var lease = new DirectoryLease(current);
                entries ??= Directory.EnumerateFileSystemEntries(current).GetEnumerator();
                while (inspected < maxObjects && timer.Elapsed < (timeBudget ?? TimeSpan.FromSeconds(2)))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!entries.MoveNext()) { entries.Dispose(); entries = null; break; }
                    inspected++;
                    try
                    {
                        var path = entries.Current;
                        var attributes = File.GetAttributes(path);
                        if ((attributes.HasFlag(FileAttributes.System) && !IsUserCacheContainer(path)) || IsSystemPath(path)) { skipped++; continue; }
                        // Do not traverse links or hydrate cloud placeholders to calculate their size.
                        if (((int)attributes & (0x400 | 0x1000 | 0x40000 | 0x400000)) != 0) { skipped++; continue; }
                        var name = Path.GetFileName(path);
                        var parent = folders[current];
                        if (!parent.Generated && IsProjectMarker(name)) parent.Project = true;
                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            if (folders.Count >= 50_000) { limited = true; break; }
                            folders.Add(path, new(path, current, parent.Generated || GeneratedNames.Contains(name) || cacheRoots.Contains(path)));
                            pending.Enqueue(path);
                        }
                        else
                        {
                            var length = new FileInfo(path).Length;
                            files++; bytes = checked(bytes + length);
                            for (Folder? folder = parent; folder is not null; folder = folder.Parent is null ? null : folders[folder.Parent])
                            { folder.Bytes = checked(folder.Bytes + length); folder.Files++; }
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExperimentRefusedException)
            { skipped++; entries?.Dispose(); entries = null; }
        }
        return Snapshot();
    }

    public SpaceSurveyResult Snapshot() => new(folders.Values.Where(f => !IsUserCacheContainer(f.Path)).Select(f => new SpaceFolder(f.Path, f.Bytes, f.Files, f.Project,
        cacheRoots.Contains(f.Path) ? "缓存 / 临时文件" : f.Project ? "项目" : f.Generated ? "工具 / 临时目录" : "目录",
        f.Project ? VersionFamily(f.Path) : "")).ToArray(), files, bytes, skipped,
        !limited && (entries is not null || pending.Count > 0), limited);

    public static bool IsSystemPath(string path, bool checkAncestors = false)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var roots = new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0);
        var drive = Path.GetPathRoot(full)!;
        roots = roots.Concat(new[] { "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI",
            "$WinREAgent", "$WINDOWS.~BT", "$WINDOWS.~WS", "Windows.old" }.Select(n => Path.Combine(drive, n)));
        if (roots.Any(p => full.Equals(p, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return true;
        // Also reject direct selection of a child beneath a system-attributed directory.
        for (var ancestor = checkAncestors ? full : null; ancestor is not null && !ancestor.Equals(drive, StringComparison.OrdinalIgnoreCase);
            ancestor = Path.GetDirectoryName(ancestor))
            if (File.GetAttributes(ancestor).HasFlag(FileAttributes.System) && !IsUserCacheContainer(ancestor)) return true;
        return false;
    }

    // Windows can mark LocalAppData as System; traverse this container for user caches, but never list it.
    private static bool IsUserCacheContainer(string path) => path.Equals(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectMarker(string name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("package.json", StringComparison.OrdinalIgnoreCase) || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) || name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) || name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
        new[] { ".sln", ".slnx", ".csproj", ".fsproj" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    public static string VersionFamily(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var family = Regex.Replace(name, @"(?:[ _\-]+(?:v?\d+(?:[.\-_]\d+)*|old|backup|copy|副本|备份|旧版))+$", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return Path.Combine(Path.GetDirectoryName(path) ?? "", string.IsNullOrWhiteSpace(family) ? name : family).ToUpperInvariant();
    }

    public void Dispose() { entries?.Dispose(); entries = null; pending.Clear(); }
}

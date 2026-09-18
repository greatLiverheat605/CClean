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
                        // Do not traverse links or hydrate cloud placeholders to calculate their size.
                        if (((int)attributes & (0x400 | 0x1000 | 0x40000 | 0x400000)) != 0) { skipped++; continue; }
                        var name = Path.GetFileName(path);
                        var parent = folders[current];
                        if (!parent.Generated && IsProjectMarker(name)) parent.Project = true;
                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            if (folders.Count >= 50_000) { limited = true; break; }
                            folders.Add(path, new(path, current, parent.Generated || GeneratedNames.Contains(name) || cacheRoots.Contains(path) || IsInstalledSystemFolder(path)));
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

    public SpaceSurveyResult Snapshot() => new(folders.Values.Select(f => new SpaceFolder(f.Path, f.Bytes, f.Files, f.Project,
        cacheRoots.Contains(f.Path) ? "缓存 / 临时文件" : f.Project ? "项目" : f.Generated ? "工具 / 临时目录" : "目录",
        f.Project ? VersionFamily(f.Path) : "")).ToArray(), files, bytes, skipped,
        !limited && (entries is not null || pending.Count > 0), limited);

    private static bool IsInstalledSystemFolder(string path) => new[]
        { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData }
        .Select(Environment.GetFolderPath).Where(p => p.Length > 0).Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));

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

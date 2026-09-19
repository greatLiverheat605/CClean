using System.Diagnostics;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Monitoring;

namespace CClean.Core.Projects;

public sealed record ProjectDiscoveryResult(IReadOnlyList<string> Roots, long Inspected, long Skipped, bool HasMore, bool Limited);

// Explicit, bounded discovery reads names and attributes only; it never registers a monitor.
public sealed class ProjectDiscovery : IDisposable
{
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", ".venv", "venv", "site-packages", "bin", "obj", "__pycache__", "_cacache",
          ".codex", ".claude", ".zcode", ".vscode", ".cache", ".npm", ".nuget", "vendor", "target", "dist", "build" };
    private static readonly string[] MarkerNames = [".git", "package.json", "pyproject.toml", "Cargo.toml", "go.mod", "pom.xml", "CMakeLists.txt"];
    private static readonly string[] MarkerExtensions = [".sln", ".slnx", ".csproj", ".fsproj"];
    private readonly Queue<string> pending = new();
    private readonly List<string> children = [];
    private readonly HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
    private IEnumerator<string>? entries;
    private string? current;
    private long inspected, skipped;
    private int directories = 1;
    private bool limited, disposed;
    public string Root { get; }

    public ProjectDiscovery(string root)
    {
        Root = ExperimentSandbox.Normalize(root);
        if (IsExcludedPath(Root, checkAncestors: true))
            throw new ExperimentRefusedException("系统、安装或依赖目录不作为项目发现范围。", 3);
        using var lease = new DirectoryLease(Root);
        pending.Enqueue(Root);
    }

    public ProjectDiscoveryResult Next(CancellationToken cancellation, int maxObjects = 20_000, TimeSpan? timeBudget = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (maxObjects is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxObjects));
        var timer = Stopwatch.StartNew();
        var batchCount = 0;
        while (batchCount < maxObjects && timer.Elapsed < (timeBudget ?? TimeSpan.FromSeconds(2)) && !limited)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (entries is null)
                {
                    if (!pending.TryDequeue(out current)) break;
                    children.Clear();
                }
                using var lease = new DirectoryLease(current!);
                if (IsExcludedPath(current!, checkAncestors: true)) { skipped++; CompleteDirectory(false); continue; }
                entries ??= Directory.EnumerateFileSystemEntries(current!).GetEnumerator();
                if (!entries.MoveNext()) { CompleteDirectory(true); continue; }
                batchCount++; inspected++;
                var path = entries.Current;
                var attributes = File.GetAttributes(path);
                // Avoid links, cloud hydration and system entries before treating a name as evidence.
                if (((int)attributes & (0x4 | 0x400 | 0x1000 | 0x40000 | 0x400000)) != 0 || SpaceSurvey.IsSystemPath(path))
                { skipped++; continue; }
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                if (!IsContainerRoot(current!) && IsMarker(Path.GetFileName(path), isDirectory))
                {
                    if (roots.Count == 2_000) { limited = true; break; }
                    roots.Add(current!);
                    // A project is one module; nested build/manifest folders do not create extra projects.
                    CompleteDirectory(false);
                }
                else if (isDirectory)
                {
                    if (IsExcludedPath(path)) { skipped++; continue; }
                    if (directories == 50_000) { limited = true; break; }
                    directories++; children.Add(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExperimentRefusedException)
            { skipped++; CompleteDirectory(true); }
        }
        return Snapshot();
    }

    public ProjectDiscoveryResult Snapshot() => new(roots.Order(StringComparer.OrdinalIgnoreCase).ToArray(), inspected, skipped,
        !limited && (entries is not null || pending.Count > 0), limited);

    private void CompleteDirectory(bool descend)
    {
        entries?.Dispose(); entries = null;
        if (descend) foreach (var child in children) pending.Enqueue(child);
        children.Clear();
    }

    internal static bool HasProjectMarker(string root)
    {
        if (IsContainerRoot(root)) return false;
        try
        {
            using var lease = new DirectoryLease(root);
            // Common markers are checked directly so large source directories do not require enumeration.
            foreach (var name in MarkerNames)
            {
                var path = Path.Combine(root, name);
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                var attributes = File.GetAttributes(path);
                if (((int)attributes & (0x4 | 0x400 | 0x1000 | 0x40000 | 0x400000)) == 0 &&
                    IsMarker(name, attributes.HasFlag(FileAttributes.Directory))) return true;
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(root).Take(256))
            {
                if (!MarkerExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(path);
                if (((int)attributes & (0x4 | 0x10 | 0x400 | 0x1000 | 0x40000 | 0x400000)) == 0) return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExperimentRefusedException) { }
        return false;
    }

    private static bool IsMarker(string name, bool directory) => name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        (!directory && (MarkerNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                       MarkerExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)));

    internal static bool IsContainerRoot(string root) => root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) ||
        new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0).Any(p => root.Equals(p, StringComparison.OrdinalIgnoreCase));

    internal static bool IsExcludedPath(string path, bool checkAncestors = false)
    {
        if (SpaceSurvey.IsSystemPath(path, checkAncestors)) return true;
        var full = Path.GetFullPath(path);
        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ExcludedNames.Contains)) return true;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length > 0 && new[] { "Programs", "Packages", "Microsoft/WindowsApps" }
            .Any(p => ProtectionPolicy.Contains(Path.Combine(local, p), full));
    }

    public void Dispose() { disposed = true; entries?.Dispose(); entries = null; children.Clear(); pending.Clear(); }
}

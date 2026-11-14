using System.Diagnostics;
using System.Text.RegularExpressions;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Storage;

namespace CClean.Core.Monitoring;

public sealed record SpaceFolder(string Path, long Bytes, int Files, bool Project, string Category, string VersionFamily)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    public string Size => IndexedFile.FormatBytes(Bytes);
    public double Share { get; init; }
    public ContentExplanation Explanation { get; init; } = ContentClassification.Describe(Path, Project);
}
public sealed record SpaceSurveyResult(IReadOnlyList<SpaceFolder> Folders, long Files, long Bytes,
    long Skipped, bool HasMore, bool Limited)
{
    public string Root { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public long? KnownUniqueLogicalBytes { get; init; }
    public long? KnownAllocatedBytes { get; init; }
    public long UnknownAllocationObjects { get; init; }
    public long DuplicatePaths { get; init; }
    public bool IdentityBudgetReached { get; init; }
    public bool IsStale { get; init; }
    public bool AllocationComplete => !IsStale && KnownAllocatedBytes is not null && !HasMore && !Limited &&
        Skipped == 0 && UnknownAllocationObjects == 0 && !IdentityBudgetReached;
    public string AllocationBasis => "仅本轮根内已读取身份的对象去重；独立根及父子摘要不可直接相加，分配量不是可释放承诺。";
}

// A bounded, metadata-only inventory. It neither registers watchers nor authorizes deletion.
public sealed class SpaceSurvey : IDisposable
{
    public const int MaximumIdentities = 200_000;
    private sealed class Folder(string path, string? parent, bool excludedFromProjectDetection)
    {
        public string Path = path;
        public string? Parent = parent;
        public bool ExcludedFromProjectDetection = excludedFromProjectDetection, Project;
        public long Bytes;
        public int Files;
    }
    // Prevent dependency/tool manifests being mistaken for projects. This is not a content or cleanup classification.
    private static readonly HashSet<string> ProjectDetectionExclusions = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", ".venv", "venv", "site-packages", "bin", "obj", "__pycache__", "_cacache", ".codex", ".claude", ".zcode", ".vscode", ".cache", ".npm", ".nuget", "vendor", "target", "dist", "build" };
    private static readonly HashSet<string> BuildDirectoryNames = new(StringComparer.OrdinalIgnoreCase) { "target", "dist", "build", "bin", "obj" };
    private readonly Dictionary<string, Folder> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> pending = new();
    private readonly HashSet<string> cacheRoots;
    // ponytail: retain identities, not file snapshots; stop accounting new identities at this bounded ceiling.
    private readonly Dictionary<string, (long Logical, long Allocated)> identities = new(StringComparer.Ordinal);
    private readonly int identityLimit;
    private IEnumerator<string>? entries;
    private string current = "";
    private long files, bytes, skipped;
    private long knownUniqueLogical, knownAllocated, unknownAllocation, duplicatePaths;
    private bool limited, identityBudgetReached;
    public string Root { get; }

    public SpaceSurvey(string root, int identityLimit = MaximumIdentities)
    {
        if (identityLimit is < 1 or > MaximumIdentities) throw new ArgumentOutOfRangeException(nameof(identityLimit));
        this.identityLimit = identityLimit;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (IsSystemPath(Root, checkAncestors: true)) throw new ExperimentRefusedException("系统文件和系统目录不参与空间盘点，请选择项目或用户目录。", 3);
        using var lease = new DirectoryLease(Root);
        cacheRoots = new(CommonCacheRoots().Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))), StringComparer.OrdinalIgnoreCase);
        folders.Add(Root, new(Root, null, Root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(p => ProjectDetectionExclusions.Contains(p) && !BuildDirectoryNames.Contains(p)) ||
            BuildDirectoryNames.Contains(Path.GetFileName(Root)) || cacheRoots.Contains(Root)));
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
                        if (!parent.ExcludedFromProjectDetection && IsProjectMarker(name)) parent.Project = true;
                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            if (folders.Count >= 50_000) { limited = true; break; }
                            folders.Add(path, new(path, current, parent.ExcludedFromProjectDetection || ProjectDetectionExclusions.Contains(name) || cacheRoots.Contains(path)));
                            pending.Enqueue(path);
                        }
                        else
                        {
                            var length = new FileInfo(path).Length;
                            files++; bytes = checked(bytes + length);
                            AccountAllocation(WindowsFileMetadata.Read(path, lease));
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

    private void AccountAllocation(FileSnapshot file)
    {
        if (file.Identity is null || file.LogicalBytes is null || file.AllocatedBytes is null ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { unknownAllocation++; return; }
        var incomplete = !file.StreamsComplete || file.HasNamedStreams;
        if (identities.TryGetValue(file.Identity, out var previous))
        {
            duplicatePaths++;
            // A changing hard-linked object is not a consistent allocation observation.
            if (incomplete || previous.Logical != file.LogicalBytes || previous.Allocated != file.AllocatedBytes) unknownAllocation++;
            return;
        }
        if (identities.Count == identityLimit) { identityBudgetReached = true; unknownAllocation++; return; }
        if (incomplete) unknownAllocation++;
        identities.Add(file.Identity, (file.LogicalBytes.Value, file.AllocatedBytes.Value));
        knownUniqueLogical = checked(knownUniqueLogical + file.LogicalBytes.Value);
        knownAllocated = checked(knownAllocated + file.AllocatedBytes.Value);
    }

    public SpaceSurveyResult Snapshot() => new(folders.Values.Where(f => !IsUserCacheContainer(f.Path)).Select(f =>
    {
        var explanation = ContentClassification.Describe(f.Path, f.Project);
        return new SpaceFolder(f.Path, f.Bytes, f.Files, f.Project, explanation.Category, f.Project ? VersionFamily(f.Path) : "") { Explanation = explanation };
    }).ToArray(), files, bytes, skipped,
        !limited && (entries is not null || pending.Count > 0), limited)
    {
        Root = Root, CapturedAt = DateTimeOffset.UtcNow,
        KnownUniqueLogicalBytes = identities.Count > 0 || (files == 0 && skipped == 0 && !limited && entries is null && pending.Count == 0) ? knownUniqueLogical : null,
        KnownAllocatedBytes = identities.Count > 0 || (files == 0 && skipped == 0 && !limited && entries is null && pending.Count == 0) ? knownAllocated : null,
        UnknownAllocationObjects = unknownAllocation, DuplicatePaths = duplicatePaths, IdentityBudgetReached = identityBudgetReached
    };

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

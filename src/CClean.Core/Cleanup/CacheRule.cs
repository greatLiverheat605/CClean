using CClean.Core.Files;

namespace CClean.Core.Cleanup;

public sealed record CacheContext(string Root, string ToolVersion, bool Enabled, bool ExplicitExclusiveRoot,
    bool ObservationComplete, bool ActiveConsumer, IReadOnlyList<string> ProtectedRoots);

public sealed record CacheRule(string Id, int Version, string Tool, string SupportedVersion, string RelativeRoot,
    string RebuildCost, string Prerequisite)
{
    public static CacheRule NpmContent { get; } = new("npm-exclusive-content", 1, "npm", "11.19.0",
        "_cacache", "May require downloads; offline reinstall can fail until rebuilt. Content and index entries are previewed together.",
        "An explicitly configured exclusive npm cache is required; default/global/shared caches are excluded.");

    public string? Exclude(CacheContext context, FileSnapshot file)
    {
        if (!context.Enabled) return "Rule disabled";
        if (context.ToolVersion != SupportedVersion) return "Unsupported tool version";
        if (!context.ExplicitExclusiveRoot) return "Shared or unproven cache root";
        if (!context.ObservationComplete || context.ActiveConsumer) return "Active or unknown consumer";
        if (!file.IsOrdinaryFile) return file.Exclusion ?? "Unknown object";
        var root = Path.GetFullPath(context.Root).TrimEnd(Path.DirectorySeparatorChar);
        var candidateRoot = Path.Combine(root, RelativeRoot);
        if (!file.Path.StartsWith(candidateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "Outside rule content root";
        var relative = Path.GetRelativePath(candidateRoot, file.Path);
        if (!relative.StartsWith("content-v2" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !relative.StartsWith("index-v5" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "Unknown cache layout";
        foreach (var protection in context.ProtectedRoots)
        {
            var protectedRoot = Path.GetFullPath(protection).TrimEnd(Path.DirectorySeparatorChar);
            if (file.Path.Equals(protectedRoot, StringComparison.OrdinalIgnoreCase) ||
                file.Path.StartsWith(protectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "Protected project or session";
        }
        return null;
    }
}

using System.Text.Json.Serialization;
using CClean.Core.Monitoring;
using CClean.Core.Projects;

namespace CClean.Core.Storage;

// Display-only summaries deliberately contain no file snapshots, decisions, selections or approval tokens.
public sealed record InventoryFolderSummary(string Path, long Bytes, int Files, bool Project, string VersionFamily);

public sealed record InventorySummary(string Root, long ScopeRevision, DateTimeOffset CapturedAt,
    long Files, long LogicalBytes, long? KnownUniqueLogicalBytes, long? KnownAllocatedBytes,
    long UnknownAllocationObjects, long DuplicatePaths, long Skipped, bool HasMore, bool Limited,
    bool IdentityBudgetReached, IReadOnlyList<InventoryFolderSummary> Folders, bool FoldersTruncated)
{
    [JsonIgnore] public bool IsStale => true;
    public SpaceSurveyResult ToSurveyResult() => new(Folders.Select(f => new SpaceFolder(f.Path, f.Bytes, f.Files,
        f.Project, ContentClassification.Describe(f.Path, f.Project).Category, f.VersionFamily)).ToArray(),
        Files, LogicalBytes, Skipped, HasMore, Limited)
    {
        Root = Root, CapturedAt = CapturedAt, KnownUniqueLogicalBytes = KnownUniqueLogicalBytes,
        KnownAllocatedBytes = KnownAllocatedBytes, UnknownAllocationObjects = UnknownAllocationObjects,
        DuplicatePaths = DuplicatePaths, IdentityBudgetReached = IdentityBudgetReached, IsStale = true
    };
}

public sealed record DiscoverySummary(string Root, long ScopeRevision, DateTimeOffset CapturedAt,
    IReadOnlyList<string> Roots, long Inspected, long Skipped, bool HasMore, bool Limited, bool RootsTruncated)
{
    [JsonIgnore] public bool IsStale => true;
    public ProjectDiscoveryResult ToDiscoveryResult() => new(Roots, Inspected, Skipped, HasMore, Limited);
}

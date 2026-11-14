using System.Security.Cryptography;
using System.Text.Json;
using CClean.Core.Files;
using CClean.Core.Sessions;
using CClean.Core.Monitoring;

namespace CClean.Core.Cleanup;

public enum CleanupContract { StrictActivity, ObjectReviewV1, SegmentReviewResearch }
public sealed record CleanupObservation(DirectoryMonitor Monitor, DirectoryObservation Evidence);
public sealed record CleanupContext(long ConfigurationVersion, IReadOnlyList<RuleScope> Rules, ActivitySnapshot Activity, IReadOnlyList<string> ProtectedRoots)
{
    public CleanupContract Contract { get; init; }
    // Includes sessions, consumers, scope/observer generations and coverage, excluding only directory recency.
    public ActivitySnapshot? IndependentActivity { get; init; }
    public IReadOnlyList<CleanupObservation> Observations { get; init; } = [];
    public string Fingerprint() => Fingerprint(false);
    internal string Fingerprint(bool execution) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        ProtectionVersion = ProtectionPolicy.Version, CatalogVersion = RuleCatalog.Version, ConfigurationVersion, Contract,
        Rules = Rules.OrderBy(r => r.Root, StringComparer.OrdinalIgnoreCase).ToArray(), Activity.Version,
        Scopes = (execution ? IndependentActivity ?? Activity : Activity).Scopes.OrderBy(s => s.Root, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Reason).ToArray(),
        IndependentActivity,
        Observations = Observations.OrderBy(o => o.Evidence.Root, StringComparer.OrdinalIgnoreCase).Select(o => new
        { o.Evidence.Id, o.Evidence.Root, o.Evidence.Reliable, o.Evidence.ScanGaps, Sequence = execution ? 0 : o.Evidence.Sequence }).ToArray(),
        ProtectedRoots = ProtectedRoots.Order(StringComparer.OrdinalIgnoreCase).ToArray()
    })));
}
public sealed record CleanupItem(FileSnapshot File, ProtectionDecision Decision, string? RootIdentity)
{
    public string Path => File.Path;
    public string Status => Decision.Eligible ? "候选，待确认" : "排除";
    public string Reason => Decision.Category + "：" + Decision.Reason;
    public string Estimate => Storage.IndexedFile.FormatBytes(File.AllocatedBytes);
}
public sealed class CleanupPlan
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset Created { get; }
    public DateTimeOffset Expires => Created.AddMinutes(5);
    public string ContextFingerprint { get; }
    public IReadOnlyList<CleanupItem> Items { get; }
    public bool Truncated { get; }
    public CleanupContract Contract { get; }
    // In-memory evidence linkage, never an executable authorization restored from a manifest.
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? PreviousRunId { get; }
    public int EligibleCount => Items.Count(i => i.Decision.Eligible);
    public long EstimatedBytes => Items.Where(i => i.Decision.Eligible).Sum(i => i.File.AllocatedBytes ?? 0);
    internal CleanupPlan(DateTimeOffset created, string fingerprint, List<CleanupItem> items, bool truncated,
        CleanupContract contract = CleanupContract.StrictActivity, Guid? previousRunId = null)
    { Created = created; ContextFingerprint = fingerprint; Items = items.AsReadOnly(); Truncated = truncated; Contract = contract; PreviousRunId = previousRunId; }
    public bool IsCurrent(CleanupContext context, DateTimeOffset now) => now >= Created && now < Expires && ContextFingerprint == context.Fingerprint();
}
public static class CleanupPlanner
{
    public static CleanupPlan Preview(IEnumerable<string> paths, CleanupContext context, CancellationToken cancellation = default)
        => PreviewCore(paths, context, 1000, cancellation);

    // Isolated research only; never reached from the ordinary app or a restored manifest.
    internal static CleanupPlan PreviewSegmentResearch(IEnumerable<string> paths, CleanupContext context, CancellationToken cancellation = default)
    {
        if (context.Contract != CleanupContract.SegmentReviewResearch) throw new InvalidOperationException("研究清单须显式绑定分段复核契约。");
        var plan = PreviewCore(paths, context, 10000, cancellation);
        if (plan.Truncated) throw new InvalidOperationException("研究清单超过 10000 项，整批拒绝。");
        _ = CleanupManifestCodec.Encode(plan); // The entire fixed set must fit before it can be confirmed.
        cancellation.ThrowIfCancellationRequested();
        return plan;
    }
    private static CleanupPlan PreviewCore(IEnumerable<string> paths, CleanupContext context, int limit, CancellationToken cancellation)
    {
        var created = DateTimeOffset.UtcNow; var fingerprint = context.Fingerprint();
        var selected = paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(limit + 1).ToArray();
        var items = new List<CleanupItem>(); var identities = new HashSet<string>();
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var observedFiles = context.Observations.Select(o => o.Monitor.Snapshot.Files).ToArray();
        Experiments.DirectoryLease? parent = null;
        string? parentPath = null;
        try
        {
            foreach (var path in selected.Take(limit))
            {
                cancellation.ThrowIfCancellationRequested();
                var directory = Path.IsPathFullyQualified(path) ? Path.GetDirectoryName(path) : null;
                if (!string.Equals(directory, parentPath, StringComparison.OrdinalIgnoreCase))
                {
                    parent?.Dispose(); parent = null; parentPath = directory;
                    if (directory is not null)
                        try { parent = new Experiments.DirectoryLease(directory); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Experiments.ExperimentRefusedException) { }
                }
                FileSnapshot? old = null;
                foreach (var files in observedFiles)
                    if (files.TryGetValue(path, out old)) break;
                var file = parent is null ? WindowsFileMetadata.Read(path) : WindowsFileMetadata.Read(path, parent, old);
                var decision = ProtectionPolicy.Classify(file, context.Rules, context.Activity, context.ProtectedRoots, created);
                string? rootIdentity = null;
                if (decision.Eligible)
                {
                    var root = decision.Scope!.Root;
                    if (!roots.TryGetValue(root, out rootIdentity)) roots[root] = rootIdentity = WindowsFileMetadata.Read(root).Identity;
                    if (rootIdentity is null) decision = new(false, "根目录未知", "无法绑定批准根身份");
                    else if (!identities.Add(file.Identity!)) decision = new(false, "重复对象", "同一身份只允许一次");
                }
                items.Add(new(file, decision, rootIdentity));
            }
        }
        finally { parent?.Dispose(); }
        return new(created, fingerprint, items, selected.Length > limit, context.Contract);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using CClean.Core.Files;
using CClean.Core.Sessions;

namespace CClean.Core.Cleanup;

public sealed record CleanupContext(long ConfigurationVersion, IReadOnlyList<RuleScope> Rules, ActivitySnapshot Activity, IReadOnlyList<string> ProtectedRoots)
{
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        ProtectionVersion = ProtectionPolicy.Version, CatalogVersion = RuleCatalog.Version, ConfigurationVersion,
        Rules = Rules.OrderBy(r => r.Root, StringComparer.OrdinalIgnoreCase).ToArray(), Activity.Version,
        Scopes = Activity.Scopes.OrderBy(s => s.Root, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Reason).ToArray(),
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
    public int EligibleCount => Items.Count(i => i.Decision.Eligible);
    public long EstimatedBytes => Items.Where(i => i.Decision.Eligible).Sum(i => i.File.AllocatedBytes ?? 0);
    internal CleanupPlan(DateTimeOffset created, string fingerprint, List<CleanupItem> items, bool truncated)
    { Created = created; ContextFingerprint = fingerprint; Items = items.AsReadOnly(); Truncated = truncated; }
    public bool IsCurrent(CleanupContext context, DateTimeOffset now) => now >= Created && now < Expires && ContextFingerprint == context.Fingerprint();
}
public static class CleanupPlanner
{
    public static CleanupPlan Preview(IEnumerable<string> paths, CleanupContext context, CancellationToken cancellation = default)
    {
        var created = DateTimeOffset.UtcNow; var fingerprint = context.Fingerprint();
        var selected = paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(1001).ToArray();
        var items = new List<CleanupItem>(); var identities = new HashSet<string>();
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in selected.Take(1000))
        {
            cancellation.ThrowIfCancellationRequested();
            var file = WindowsFileMetadata.Read(path);
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
        return new(created, fingerprint, items, selected.Length > 1000);
    }
}

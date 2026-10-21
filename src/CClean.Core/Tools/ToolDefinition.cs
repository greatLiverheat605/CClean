namespace CClean.Core.Tools;

public enum ToolCapability { ManualRegistration, DocumentedDiscovery, ProcessEvidence, LocalMetadataDiscovery }
public sealed record ToolDefinition(string Id, string DisplayName, string? TestedVersion, ToolCapability Capability, string Limitation);

public static class KnownTools
{
    public static IReadOnlyList<ToolDefinition> Baseline { get; } =
    [
        new("codex", "Codex", "0.153.2", ToolCapability.LocalMetadataDiscovery, "Bounded session_meta headers; no process-state proof or private database access."),
        new("claude", "Claude Code", null, ToolCapability.LocalMetadataDiscovery, "Bounded JSONL prefix metadata; unknown formats skipped; process state unknown."),
        new("zcode", "ZCode", null, ToolCapability.ManualRegistration, "Version not detected; user-selected directories and manual sessions only.")
    ];
}

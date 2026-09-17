namespace CClean.Core.Tools;

public enum ToolCapability { ManualRegistration, DocumentedDiscovery, ProcessEvidence }
public sealed record ToolDefinition(string Id, string DisplayName, string? TestedVersion, ToolCapability Capability, string Limitation);

public static class KnownTools
{
    public static IReadOnlyList<ToolDefinition> Baseline { get; } =
    [
        new("codex", "Codex", "0.153.2", ToolCapability.ManualRegistration, "CLI version detected; no chat, credentials or internal database access."),
        new("claude", "Claude Code", null, ToolCapability.ManualRegistration, "Version not detected; user-selected directories and manual sessions only."),
        new("zcode", "ZCode", null, ToolCapability.ManualRegistration, "Version not detected; user-selected directories and manual sessions only.")
    ];
}

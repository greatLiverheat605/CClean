namespace CClean.Core.Tools;

public enum ToolCapability { ManualRegistration, DocumentedDiscovery, ProcessEvidence, LocalMetadataDiscovery }
public sealed record ToolDefinition(string Id, string DisplayName, string? TestedVersion, ToolCapability Capability, string Limitation)
{
    public string InstallationEvidence { get; init; } = "未识别安装或目录；可手动指定范围，不代表工具不存在。";
    public string DirectoryObservation => "用户确认的 Windows 本地目录可登记观察；受权限、特殊对象和范围预算限制，不证明已覆盖所有目录。";
    public bool SupportsAutomaticSessionMetadata => Capability == ToolCapability.LocalMetadataDiscovery;
    public string SessionMetadata => SupportsAutomaticSessionMetadata ? Limitation : "尚无自动会话元数据适配；支持手动会话上下文，不读取聊天正文。";
    public string ProcessEvidence => "未实现文件写入进程归因；历史进程路径或手动会话均不能证明写入者。";
    public string CleanupCapability => "工具数据没有首批清理规则；登记、目录名称或会话结束均不授予删除资格。";
    public string Summary => $"安装 / 目录线索：{InstallationEvidence}\n目录观察：{DirectoryObservation}\n会话元数据：{SessionMetadata}\n进程证据：{ProcessEvidence}\n清理能力：{CleanupCapability}";
}

public static class KnownTools
{
    public const int MaxLabelLength = 80;
    public static IReadOnlyList<ToolDefinition> Baseline { get; } =
    [
        new("codex", "Codex", "0.153.2", ToolCapability.LocalMetadataDiscovery, "仅解析已验证的 Codex JSONL 首行 session_meta；有界读取项目路径、ID 与时间，未知格式跳过。CLI 0.153.2 为旧实测版本，不保证所有桌面版本格式。")
            { InstallationEvidence = "2026-10-22 历史盘点：商店包 26.1013.3656.0、.codex 目录及进程路径；不是当前安装探测。" },
        new("claude", "Claude Code", null, ToolCapability.LocalMetadataDiscovery, "仅解析 .claude/projects 中已验证的 JSONL 前缀元数据；桌面 LocalCache/会话磁盘未适配，CLI 版本未知。")
            { InstallationEvidence = "2026-10-22 历史盘点：Claude 桌面商店包 2.42304.2.0、目录及进程路径；不证明 Claude Code CLI 已安装。" },
        new("zcode", "ZCode", null, ToolCapability.ManualRegistration, "目录线索不等于完整安装；自动会话格式未适配。")
            { InstallationEvidence = "2026-10-22 历史盘点有 .zcode、Programs/ZCode 和 Roaming/ZCode 线索；版本未核验，remote-assets-cache 混有运行时和组件。" },
        new("cc-switch", "CC Switch", null, ToolCapability.ManualRegistration, "仅手动目录与会话上下文；备份保持保护。")
            { InstallationEvidence = "2026-10-22 历史盘点有 .cc-switch 目录和 D:\\ccswitch\\cc-switch.exe 进程路径；目录含备份，版本未知。" },
        new("gemini", "Gemini", null, ToolCapability.ManualRegistration, "自动会话格式未适配。")
            { InstallationEvidence = "2026-10-22 历史盘点有 .gemini 目录线索；完整安装、版本与当前活动未知。" },
        new("pi", "Pi", null, ToolCapability.ManualRegistration, "自动会话格式未适配。")
            { InstallationEvidence = "2026-10-22 历史盘点有 .pi 目录线索；完整安装、版本与当前活动未知。" },
        new("vscode", "VS Code", null, ToolCapability.ManualRegistration, "编辑器目录登记不代表识别其所有 Agent 扩展。")
            { InstallationEvidence = "2026-10-22 历史盘点有 .vscode 和 Roaming/Code 目录线索；扩展、Agent 与版本覆盖未知。" },
        new("opencode", "OpenCode", null, ToolCapability.ManualRegistration, "自动会话格式未适配；已知路径未发现不证明未安装。")
            { InstallationEvidence = "2026-10-22 历史盘点 H:\\OpenCode 仅少量文件；不能证明完整安装或活跃。" }
    ];

    public static IReadOnlyList<string> RegistrationLabels { get; } = Baseline.Select(t => t.DisplayName).Append("通用工具").ToArray();

    public static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Any(char.IsControl))
            throw new ArgumentException("工具标签须为 1–80 字符，不能包含换行或控制字符。", nameof(label));
        label = label.Trim();
        if (label.Length > MaxLabelLength) throw new ArgumentException("工具标签最多 80 字符。", nameof(label));
        return RegistrationLabels.FirstOrDefault(t => t.Equals(label, StringComparison.OrdinalIgnoreCase)) ?? label;
    }

    public static ToolDefinition Describe(string label)
    {
        label = NormalizeLabel(label);
        return Baseline.FirstOrDefault(t => t.DisplayName == label) ??
            new ToolDefinition("custom:" + label.ToLowerInvariant(), label, null, ToolCapability.ManualRegistration,
                "自定义标签只支持手动目录登记与会话上下文；不具备自动识别、进程归因或工具数据清理适配。");
    }
}

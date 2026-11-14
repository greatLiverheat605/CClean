using CClean.Core.Cleanup;

namespace CClean.Core.Monitoring;

public enum ContentPurpose { WorkProduct, Configuration, History, Dependencies, BuildOutput, DownloadCache, LogsAndDumps, TestSamples, ToolRuntime, Mixed, Unknown }
public enum ContentEvidence { Unknown, PathHint, ProjectMarker, KnownScope }

// Read-only explanations describe value and impact; only ProtectionPolicy decides execution eligibility.
public sealed record ContentExplanation(ContentPurpose Purpose, string Title, string Use, string Association,
    ContentEvidence Evidence, string Basis, string Value, string Impact, string Rebuild, ProtectionDecision? Decision = null)
{
    public string Category => Purpose switch
    {
        ContentPurpose.WorkProduct => "源码与工作成果", ContentPurpose.Configuration => "配置与凭据",
        ContentPurpose.History => "Git / Agent 历史", ContentPurpose.Dependencies => "已安装依赖",
        ContentPurpose.BuildOutput => "疑似构建产物", ContentPurpose.DownloadCache => "下载缓存线索",
        ContentPurpose.LogsAndDumps => "日志与故障转储", ContentPurpose.TestSamples => "合成测试样本（待验证）",
        ContentPurpose.ToolRuntime => "工具运行资源", ContentPurpose.Mixed => "混合用途，需展开", _ => "尚未识别"
    };
    public string Qualification => Decision is null ? "未进行当前清理资格核验；用途识别不授予删除权限。"
        : Decision.Eligible ? "本次核验为候选，仍须具体清单确认及执行复核。"
        : $"当前保留：{Decision.Category}；{Decision.Reason}";
    public string NextStep => Decision?.Eligible == true ? "查看具体清单与影响，再决定是否加入本次处理。"
        : "查看明细与依据、打开位置或保留；未知条件不能通过全选绕过。";
    public string Summary => $"{Title}\n用途：{Use}\n关联：{Association}\n依据：{Basis}\n保留价值：{Value}\n处理影响：{Impact}\n重建条件：{Rebuild}\n{Qualification}\n下一步：{NextStep}";
}

public static class ContentClassification
{
    public const string BenchmarkRoot = @"D:\CCleanLab\CCLEAN-20260917-DEV\benchmark";

    public static ContentExplanation Describe(string path, bool project = false, ProtectionDecision? decision = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parts = full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var name = Path.GetFileName(full);
        var extension = Path.GetExtension(name);
        bool Has(params string[] names) => parts.Any(p => names.Contains(p, StringComparer.OrdinalIgnoreCase));
        ContentExplanation Explain(ContentPurpose purpose, string title, string use, string value, string impact, string rebuild,
            string association = "具体项目、会话与消费者未知；目录位置不能证明写入者。", string? basis = null,
            ContentEvidence evidence = ContentEvidence.PathHint) => new(purpose, title, use, association, evidence,
                basis ?? "仅依据路径/名称线索，未读取正文，未证明来源、活动或可再生成性。", value, impact, rebuild, decision);

        if (ProtectionPolicy.Contains(BenchmarkRoot, full))
        {
            var association = "本次 CClean benchmark 范围；依据生成入口与实验记录，历史文件逐项来源仍待核验。";
            if (full.Equals(BenchmarkRoot, StringComparison.OrdinalIgnoreCase))
                return Explain(ContentPurpose.Mixed, "CClean 合成压测数据与复现资料", "父范围同时包含样本、index.db 与实验标记。",
                    "测试报告和复现方法必须保留，未知文件保留。", "父范围占用不能作为可释放承诺；不得整根处理。",
                    "仅已证明的生成样本可按记录重新生成；不能恢复原文件身份、时间或原测试现场。", association,
                    "已知范围与生成入口；100,002 个历史文件不代表 100,002 个可再生成样本。", ContentEvidence.KnownScope);
            if (name.Equals("index.db", StringComparison.OrdinalIgnoreCase) || name.Equals(".cclean-sandbox", StringComparison.OrdinalIgnoreCase))
                return Explain(ContentPurpose.WorkProduct, "测试索引或实验标记", "保存索引、范围身份或复现依据。", "属于验收与复现资料，首条规则锁定保留。",
                    "删除会破坏实验依据。", "不能用新建标记倒推历史来源已证实。", association);
            if (Path.GetDirectoryName(full)!.Equals(BenchmarkRoot, StringComparison.OrdinalIgnoreCase) &&
                name.StartsWith("file-", StringComparison.OrdinalIgnoreCase) && extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))
                return Explain(ContentPurpose.TestSamples, "CClean 合成压测样本线索", "用于性能与可靠性测试的候选样本，尚未证明本对象的生成来源。",
                    "需核对具体运行、生成代码/参数、内容与用户保留项。", "处理后该样本不再存在，复测需要重新生成。",
                    "逐项证明生成来源后才可判断；历史运行有未提交改动，名称和大小不能单独证明来源，重建耗时未知。", association);
        }

        if (Has(".git"))
            return Explain(ContentPurpose.History, "Git 版本与工作历史", "版本对象、索引、引用和仓库元数据。", "可能含唯一提交、暂存或恢复线索。",
                "删除可能丢失本地历史或损坏仓库。", "远端和备份未核验，不能承诺重装可恢复。", "与所在仓库的位置关联；不等于已证明创建者。");
        if (Has(".codex", ".claude", ".zcode") && Has("sessions", "archived_sessions", "projects", "checkpoints", "memories"))
            return Explain(ContentPurpose.History, "Agent 会话与任务历史", "保留对话、任务及开发上下文；会话与数据库不能视为可删重复副本。",
                "可能是唯一历史，首批保持保护。", "删除可能永久丢失历史。", "重新安装不能保证恢复；需独立核验备份。",
                "已知 Agent 持久数据路径线索；具体会话状态与写入进程未知。");
        if (Has(".codex") && name.StartsWith("thread_history_", StringComparison.OrdinalIgnoreCase) && extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase))
            return Explain(ContentPurpose.History, "Codex 历史数据库线索", "保存任务/对话历史相关数据；不读取数据库正文，也不推断与 JSONL 重复。",
                "可能是唯一历史，首批保持保护。", "删除可能永久丢失历史或破坏引用。", "重新安装不能保证恢复；需独立核验备份。",
                "已知 Codex 数据位置与历史数据库名称线索；具体内容和关联完整性未核验。");
        if (Has("LocalCache") && parts.Any(p => p.Contains("claude", StringComparison.OrdinalIgnoreCase)))
        {
            if (name.Equals("sessiondata.vhdx", StringComparison.OrdinalIgnoreCase))
                return Explain(ContentPurpose.History, "Claude 会话磁盘", "保存会话环境及持久数据；首批不进入虚拟磁盘内部。", "可能包含唯一会话与工作内容。",
                    "删除可能永久丢失会话现场。", "重新下载运行镜像不能恢复会话磁盘。");
            if (name.Equals("rootfs.vhdx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".zst", StringComparison.OrdinalIgnoreCase))
                return Explain(ContentPurpose.ToolRuntime, "Claude 运行镜像线索", "用于运行环境，不能与会话磁盘或普通下载缓存混为一类。",
                    "工具运行可能依赖，首批保留。", "处理可能令工具无法启动或需要重新配置。", "恢复方式、下载条件和耗时未验证。");
            return Explain(ContentPurpose.Mixed, "Claude LocalCache 混合数据", "包含运行镜像、会话磁盘，也可能包含普通 Cache / Code Cache。",
                "子范围价值不同，必须分别查看。", "不能按 LocalCache 名称整体处理。", "会话历史不等于可下载资源；各子范围恢复条件待验证。");
        }
        if (Has(".ssh", ".aws", ".azure", ".gnupg", "credentials") || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".pem", ".key", ".pfx", ".toml", ".yaml", ".yml", ".config" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Explain(ContentPurpose.Configuration, "配置与凭据线索", "配置工具、项目环境或访问权限；不读取秘密内容。", "可能是唯一配置或密钥，默认保留。",
                "删除可能导致配置或访问权限丢失。", "必须有可用备份；不能承诺自动重建。");
        if (new[] { ".db", ".sqlite", ".vhd", ".vhdx" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Explain(ContentPurpose.Unknown, "数据库或虚拟磁盘", "可能承载持久数据，不能按父目录名称认定为缓存。", "内容与恢复方式未知，首批保护。",
                "处理可能造成不可恢复的数据丢失。", "未验证，保持未知。", evidence: ContentEvidence.Unknown);
        if (Has("node_modules", ".venv", "venv", "site-packages", "vendor") || (Has(".nuget") && Has("packages")))
            return Explain(ContentPurpose.Dependencies, "已安装依赖线索", "项目或工具使用的依赖，不等于下载缓存。", "离线工作或本地修改可能依赖这些内容。",
                "下次运行可能失败或需要重新安装。", "需核验清单、锁文件、源与网络条件，耗时未知。");
        if (Has("wsl-crashes", "logs") || new[] { ".dmp", ".log" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Explain(ContentPurpose.LogsAndDumps, "日志与故障诊断线索", "用于诊断与复现故障；Windows 侧 WSL 转储不代表 WSL 内部支持。",
                "可能是唯一故障证据，最新及近期转储必须保留。", "处理后失去该次诊断内容。", "旧故障现场通常不能原样重建；规则和保留条件须重新核验。");
        if (Has("artifacts") || new[] { ".cs", ".js", ".ts", ".py", ".rs", ".go", ".md", ".txt", ".pdf", ".docx", ".xlsx" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Explain(ContentPurpose.WorkProduct, "源码、报告与工作成果", "包含开发源码、验收证据或用户资料；artifacts 也可能混有生成物。",
                "可能是唯一成果与复现方法，默认保留。", "处理可能失去成果、验收或复现依据。", "生成物须逐项证明；不能整根承诺可重建。");
        if (Has(".cache") && Has("codex", "codex-runtime"))
            return Explain(ContentPurpose.ToolRuntime, "Codex 运行资源线索", "可能是工具运行时或安装资源。", "缓存名称不代表工具不再需要，首批保留。",
                "处理可能导致工具无法运行或重新下载。", "工具来源、版本与恢复方式未验证。");
        if (Has("_cacache", ".npm", "npm-cache"))
            return Explain(ContentPurpose.DownloadCache, "npm 下载缓存线索", "内容与索引配套保存下载资源。", "默认或共享根不能沿用专用独占规则。",
                "可能需要联网重新下载，离线安装可能失败；保留子集会影响组合完整性。", "仅实测 npm 11.19.0 专用独占条件有现有规则，当前对象仍须独立核验。");
        if (Has(".codex", ".claude", ".zcode", ".vscode", ".cc-switch", ".cache", ".nuget"))
            return Explain(ContentPurpose.Mixed, "工具数据与运行资源", "可能混有配置、历史、备份、运行时与缓存，需展开子范围。", "持久数据与恢复条件不能由父目录名称判断。",
                "整根处理可能同时丢失历史、配置和运行资源。", "各子范围分别验证；首批无通用清空规则。");
        if (Has("target", "bin", "obj", "build", "dist", "__pycache__"))
            return Explain(ContentPurpose.BuildOutput, "疑似构建输出", "名称符合构建产物习惯；项目配置与消费者尚未核验。", "可能混有非生成内容，回退与未知项需保留。",
                "处理后可能需要重新编译，不能保证不影响开发。", "首批只读解释；无已验证清理规则，重建方法和代价未知。");
        if (project)
            return Explain(ContentPurpose.Mixed, "项目源码与关联内容", "已观察到项目标记；目录可能同时包含源码、配置、历史、依赖和输出。",
                "项目标记不证明所有子项用途，需展开查看。", "不得整项目清理。", "各子范围分别验证；源码和未知内容保留。",
                "由本轮盘点的项目标记建立位置关联，不证明写入者。", "本轮观察到项目标记，仅为项目识别证据。", ContentEvidence.ProjectMarker);
        return Explain(ContentPurpose.Unknown, "尚未识别内容", "没有足够依据判断用途；Temp、cache 或 benchmark 等名称也可能混有用户资料。",
            "未知内容保留。", "处理影响未知，不形成清理建议。", "恢复方式与代价未知。", evidence: ContentEvidence.Unknown);
    }
}

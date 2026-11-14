# 工具兼容与外部规则

## R1-001 开发状态（2026-10-23）

本次扩展现有 `ToolDefinition` 与登记入口，没有新增安装扫描器、插件平台或进程追踪器。下表安装/目录线索来自 [2026-10-22 只读盘点](../audit/2026-10-22-local-space-inventory.md)，是历史观察，不是每次启动的当前探测，也不保证找齐本机全部工具。原始根和进程路径见 `artifacts/local-space-inventory-20261022/agent-directories.json`；本次未重新全机扫描。

| 工具标签 | 安装 / 目录历史线索 | 目录观察 | 自动会话元数据 | 文件写入进程证据 | 工具数据清理 |
| --- | --- | --- | --- | --- | --- |
| Codex | 商店包 `26.1013.3656.0`、`.codex` 和进程路径；CLI `0.153.2` 是 09-17 旧实测，不能当作桌面版本 | 手动确认本地目录 | 已验证的 `sessions/rollout-*.jsonl` 首行 `session_meta`；归档、私有数据库和未知桌面格式未适配 | 未实现；进程存在不证明写入者 | 无；会话、历史数据库、插件与工作副本保持保护 |
| Claude Code / Claude 桌面线索 | 桌面商店包 `2.42304.2.0`、`.claude` 与桌面进程路径；CLI 版本未核验 | 手动确认本地目录 | 已验证的 `.claude/projects` JSONL 前缀元数据；桌面 `LocalCache`、运行镜像和会话磁盘未适配 | 未实现 | 无；普通缓存、运行镜像和 `sessiondata.vhdx` 不能混为一类 |
| ZCode | `.zcode`、`Local/Programs/ZCode`、`Roaming/ZCode`；版本未核验 | 手动确认本地目录 | 未适配；手动会话可用 | 未实现 | 无；`remote-assets-cache` 混有运行时与组件 |
| CC Switch | `.cc-switch`、`D:\ccswitch\cc-switch.exe` 历史进程路径；版本未知 | 手动确认本地目录 | 未适配；手动会话可用 | 未实现 | 无；配置与备份保留 |
| Gemini | `.gemini` 目录线索；完整安装、版本与活动未知 | 手动确认本地目录 | 未适配；手动会话可用 | 未实现 | 无 |
| Pi | `.pi` 目录线索；完整安装、版本与活动未知 | 手动确认本地目录 | 未适配；手动会话可用 | 未实现 | 无 |
| VS Code | `.vscode`、`Roaming/Code` 目录线索；Agent 扩展覆盖与版本未知 | 手动确认本地目录 | 未适配；手动会话可用 | 未实现 | 无；编辑器登记不等于其全部 Agent 扩展已适配 |
| OpenCode | `H:\OpenCode` 仅少量文件；已知路径存在性补查不能证明完整安装或未安装 | 手动确认本地目录 | 未适配；手动会话可用 | 未实现 | 无 |
| 通用工具 / 自定义标签 | 未识别；用户手动说明，不推断安装、版本或缺失 | 手动确认本地目录 | 仅手动会话；未知标签不能借用 Claude 解析器 | 未实现 | 无 |

“目录观察”统一受 Windows 本地路径、权限、重解析/特殊对象、数量与观察预算约束；标签不会绕过原有系统范围、根重叠或自身索引保护。WSL/Docker、虚拟磁盘内部、其它用户和未确认自定义位置仍是覆盖缺口。观察只记录范围内变化，手动会话是上下文关联；自动发现记录仍标为运行状态未知。

项目登记与手动会话统一接受 1–80 字符工具标签，去除首尾空白、规范已知工具大小写并拒绝控制字符。标签沿用原 JSON 字符串字段和 schema；旧三工具、通用工具和自定义标签均可重启读取，未结束手动会话仍标为中断，不自动恢复。登记与会话入口提供可编辑下拉及五项独立能力说明；时间线使用同一已知列表并纳入实际登记/会话中的自定义标签。

自动元数据读取仍限 Codex / Claude Code 两种已验证格式、最多 2,000 个文件、每文件最多 256 KiB 前缀（Codex 1 行，Claude 最多 16 行）；不导入聊天正文、凭据或内部数据库。元数据文件中的名称与目录不是进程归因，也不改变清理资格。本次实现验证与安装版独立使用验收分别记录，尚未据此宣布 A-09 或整批交付完成。

本次隔离验证：`dotnet test tests/CClean.Tests -c Release --filter 'Category=ToolRegistration|Category=Sessions|Category=Discovery'` 共 7/7 通过；新增测试覆盖 8 款工具、通用与自定义标签的项目/会话保存重启，非法标签不改变状态，以及未知工具不借用 Claude 解析器。`dotnet build src/CClean.App -c Release --no-restore` 以 0 警告、0 错误通过。未执行安装、原目录文件清理或真实 UI 用户验收；旧任务状态未改。

## DEV-005 历史基线（2026-09-17）

以下保留当时的 npm 验证条件和结果，不能替代 R1-005 的新版真实 UI、watcher、保留组合与消费者重建闭环。当时实测 Codex CLI 0.153.2、Node 24.20.0、npm 11.19.0；Claude Code / ZCode 未在 PATH 检出版本，不代表系统未安装。当时三工具仅承诺 A 级通用目录登记和手动会话，B/C 级自动发现与进程证据尚未验证。后来的有界 Codex/Claude 元数据能力如上，不回写旧实验结论。

### 外部规则

`npm-exclusive-content` v1 仅支持 npm 11.19.0、用户明确配置且独占的缓存根。具体白名单为 `_cacache/content-v2` 与 `_cacache/index-v5` 中预览列出的普通文件；未知布局、特殊对象、共享/默认缓存、未知版本、未启用规则、活跃或不可观察的消费者、保护项目范围一律排除。产品必须显示独立缓存这一前提，不修改用户 npm 配置。

重新下载可能有网络成本，离线重装可能失败。内容与索引要一起预览；仅删除内容会造成索引指向缺失对象，第一次真实实验已因此报 ENOENT，失败报告保留。修正后逐文件删除内容和索引，第二次真实 npm 重新生成缓存并安装包成功。

### 复现与结果

```powershell
. ./tools/Enter-Dev.ps1
dotnet test tests/CClean.Tests -c Release --filter Category=CacheCompatibility
dotnet run --project tools/CClean.Poc -c Release -- --case cache-rule --sandbox artifacts/m0/cache-sandbox --output artifacts/m0/cache-rule.json
```

兼容保护测试 1 项通过（含版本、共享、活动、未知、禁用、保护范围与特殊对象断言）。真实工具在新缓存生成 `is-number@7.0.0`；按 DEV-004 句柄方案删除批准对象；再次 npm cache add 后内容 SHA-256 相同；npm install 成功生成可用依赖文件。预览后新增对象未被删除。运行 `20260917T085034453-96a6933ac74247e39a0459b6ca884829` 通过。

独占证据来自 PoC 新建、只传给已结束子进程的专用路径；这不证明任意用户目录独占。产品活动观察与执行时复核由 DEV-011/014/016 实现。原生 npm cache clean 未作为执行器；所有删除按预览逐文件完成。npm 使用固定已安装 node/npm 路径、独立空 user/global 配置、禁用安装脚本，避免读取用户凭据和运行包脚本。

G3 在上述显式专用缓存前提下通过；不宣称三款 Agent 默认数据目录可一键清理。

# 子计划：一键清理闭环

## 元信息

- 子计划 ID：`sub-028`；父计划：[CClean 开发计划](../2026-09-17_cclean-development.md)。
- 更新日期：2026-10-06（Asia/Shanghai）。来源：用户要求“继续规划开发任务，保证可以一键清理”。
- 状态：规划完成，DEV-023～028 均待执行；本轮不代表功能或验收完成。
- 检查基线：`7da7c63830fb6c77f646eacccf5d7ee243d72a91`，开始时工作区干净；最近交付记录为 0.1.7，本轮未重新核验安装状态。
- 优先级：6 项 P0；先推进一键清理，原 DEV-019 内存失败及 DEV-013/017/018/020 缺项保留。
- 本地任务表：[开发任务 JSON](../2026-09-17_cclean-development.tasks.json)。MCP 接口不可用，未同步，不生成远端任务 ID。
- 估算：6～10 个工程人日，另计真实缓存静置、独立 Windows 环境和长期观察等待；不是交付时间承诺。

## 交付定义

首次在“清理规则”显式启用已验证规则，之后从首页点击“一键清理”，自动汇集符合条件的候选，展示统一预览；用户一次确认，执行本次已列出的文件，显示结果并可查审计。无需逐项目、逐目录重复进入清理页面。继续遵守既有 UX 定义：先预览，再一次确认永久删除。

首版交付单位是**一个明确的有限批次**：每次至多 1,000 个合格文件，全部包含在确认清单内。较大缓存自动形成可执行的首批，展示“仅本批，仍有未扫描或剩余内容”；后续批次必须重新预览和确认。不承诺一次处理完整大目录，也不把只处理一批显示成“全部清理完成”。

清理范围为用户启用、满足条件的专用独占 npm 缓存及旧 WSL 故障转储。至少一个真实外部 npm 规则必须完成实际 UI 删除和重建验收。没有候选、环境不支持或规则受保护时，说明原因及下一步，不承诺每次都释放空间。

验收通过后才可宣布“一键清理可用”；当前规划、已有 Core 测试及 Windows 清理工具入口均不能代替该结论。

## 代码核查与缺口

| 观察 | 已核查位置 | 对下一轮开发的影响 |
| --- | --- | --- |
| 首页“快速清理临时文件”启动 Windows 工具 | `src/CClean.App/Views/OverviewView.xaml`、同名 `.xaml.cs` | 需要应用内一键入口；系统工具保留独立名称 |
| npm 批量预览与 WSL 转储使用两个上下文和入口 | `CleanupPreviewView.xaml.cs`、`CrashDumpRule.cs` | 要统一汇集；不能简单拼接保护范围 |
| 保存 npm 规则时直接写入 `11.19.0` | `CleanupPreviewView.SaveRule` | 版本常量不能冒充本机已检测结果，需预检并绑定证据 |
| 每根先执行最多 100,000 文件的扫描，再取前 1,001 项；计划只容纳 1,000 项且截断就禁用执行 | `DirectoryScanner.cs`、`CleanupPreviewView.Preview`、`CleanupPlanner.cs` | 扫描开销与展示上限不匹配；大目录需要明确选取本批 |
| 任何 Node/npm/npx 进程都会保护所有 npm 根 | `CleanupConsumers.cs` | 开发宿主可能长期无候选；本轮保留保守策略，在独立 Windows 环境验证正向路径 |
| 删除通知也刷新 `LastActivity`；执行器逐项重新计算活动上下文 | `DirectoryMonitor.cs`、`WorkspaceViewModel.Activity/CleanupContext`、`CleanupExecutor.cs` | 有首项删除触发自身活动保护的代码风险，必须先在真实 watcher 链路复现，不能用固定上下文验证掩盖 |
| WSL 最新转储依赖完整元数据；项目根用于 WSL 保护，npm 根本身又必须登记监测 | `CrashDumpRule.Context`、`WorkspaceViewModel.CleanupContext` | 合并时保护边界必须按规则生效，否则所有登记的 npm 根可能被一并排除 |
| 既有确认文案对混合规则只选择一种后果说明 | `CleanupPreviewView.Execute` | 统一确认必须列出各规则后果与本批总数 |
| 0.1.7 记录 93 项回归通过，但 npm UI 正向未完成，内存峰值 200.73 MiB 超出 200 MiB | `docs/testing/reliability-017.md` | 新验收独立记录；历史失败不改成通过 |

上述 watcher 风险来自静态调用链核查，本轮没有运行复现或修改应用。项目没有 `.codex/lessons/`；全局 `search_nodes` 不可用。已参考仓库可靠性、清理及个人验收记录，不将其标为全局经验检索结果。

## 技术约束

1. 继续使用 WPF、现有 ViewModel、`ProtectionPolicy`、`CleanupPlanner`、`CleanupExecutor` 和 SQLite 审计。不引入另一套删除器、规则插件框架或任务调度器。
2. 所有入口共享相同规则和逐项身份校验。已批准对象以卷/文件身份去重，绑定根身份、规则版本、真实工具版本、配置修订及保护状态。清单有效期仍为 5 分钟。
3. 只扫描规则允许的范围：npm 的 `_cacache/content-v2`、`_cacache/index-v5`，WSL 的指定直接子文件。普通目录占用、相同内容和版本目录名称不构成清理资格。
4. 活动或未知 npm 消费者继续保护 npm 规则；不得影响独立合格的 WSL 规则，也不得从未启用规则扩展候选。没有充分证据时不做“无关 Node 进程”推断，不强行结束用户进程。
5. 初始批次预算：1,000 个合格对象、最多检查 100,000 个目录项/10,000 个目录、30 秒枚举预算；待扫描目录与排除原因样本也有上限。仍受现有 8 MiB 审计清单、运行数据预算和低空间守卫限制。这些是待实现的保守上限，须经 DEV-027/028 实测，不能预填性能通过。
6. “本批选择结束”和“扫描错误/规则条件未知”分开表示。npm 可对明确选取的本批逐项验证，未扫描部分始终不执行；规则根身份、必要活动观察或所选范围读取不完整时，移除该根候选。WSL 无法完成最新转储判定时排除整个 WSL 规则。禁止直接把既有 `Truncated` 清零来绕过执行检查。

## 任务顺序

`DEV-023 → DEV-024 → DEV-025 → DEV-026 → DEV-027 → DEV-028`

这一队列是 DEV-017/018 的补齐工作，不依赖尚未通过的发行性能门槛才能开始修复。完成后再回到 DEV-019；完整发行仍受原有门槛约束。关联关系使用任务表 `followUpFor`，不倒写成循环依赖。

### DEV-023：验证并修复清理触发自身活动保护

- 状态：pending。依赖 DEV-014/016；G2/G3 已有窄范围证据。
- 涉及文件：`src/CClean.Core/Monitoring/DirectoryMonitor.cs`、`src/CClean.App/ViewModels/WorkspaceViewModel.cs`、`src/CClean.Core/Cleanup/CleanupExecutor.cs`、`tests/CClean.Tests/MonitoringTests.cs`、`tests/CClean.Tests/CleanupExecutionTests.cs`；按需要新增 `tests/CClean.Tests/CleanupWorkflowTests.cs`。
- 先做可重复的生产链路回归：真实监视器首扫完成、至少 3 个旧合格对象，首项删除通知已送达后再执行下一项。上下文每次从真实活动状态重新生成，不使用恒定的 `CleanupContext`。
- 如风险复现，为当前单次执行建立有限的已批准删除记录，关联清单、文件身份、同句柄删除结果与通知；仅能被证实属于当前已批准删除的变化不触发“外部活动”保护，历史索引仍记录删除。
- 通知没有进程/文件身份，不能仅凭路径或“正在清理”豁免。新增、写入、改名、同路径替换、无法解释的通知、观察缺口、原生溢出、watcher 故障与消费者变化继续使执行停止；必要时重新核对对象状态，无法判定时保守停止。
- 验证批量删除的通知数量不会通过现有有界提示队列制造未处理的活动缺口；如需合并纯扫描提示，保留真正故障和缺口的保护语义。
- 通过标准：真实 watcher 下 ≥3 个对象完成整批；在第二项前注入外部写入/同路径替换/监视器故障，后续对象保持，审计及历史正确。证据不足时不宣称已修复，更不全局忽略活动。

### DEV-024：规则启用、版本预检和统一上下文

- 状态：pending。依赖 DEV-023。
- 涉及文件：`src/CClean.Core/Cleanup/RuleCatalog.cs`、`CleanupPlanner.cs`、`CleanupConsumers.cs`、`CrashDumpRule.cs`、`ProtectionPolicy.cs`；`src/CClean.App/ViewModels/WorkspaceViewModel.cs`、`src/CClean.App/Views/CleanupPreviewView.xaml`、同名 `.xaml.cs`；`tests/CClean.Tests/CacheRuleCompatibilityTests.cs`、`CrashDumpRuleTests.cs`、`ProtectionPolicyTests.cs`。
- 使用现有清理规则配置持久化用户启用项，加入 WSL 规则的显式选择；外部规则首次默认关闭。兼容读取 0.1.7 配置，不因升级自动开启规则；格式升级时保留原数据并拒绝未知高版本。
- npm 引导显示“专用根、实测版本、独占确认、持续观察、静置时长、消费者”各项状态。核实已安装 npm 的版本，失败或不匹配则不启用；预览及执行遇到工具来源/版本变化使清单失效。检查不改写用户全局 npm 配置，也不自动把共享缓存登记为独占。
- 将规则上下文获取从页面内委托整理到 WorkspaceViewModel 的统一路径。按规则保留保护根、活动要求和排除原因，扩展现有上下文记录即可；指纹顺序稳定，规则或保护变更必然失效。
- npm 消费者只生成 npm 活动范围；WSL 的最新/48 小时保留以及项目保护不污染 npm 的独占缓存根。不存在的 WSL 目录表示该规则无内容；无法读取则显示未知并排除该规则。
- 通过标准：启用/禁用后重启恢复；未知版本不能被硬编码成受支持；禁用一条后只剩另一条；两规则独立保护正确，保护状态改变会使原计划失效。

### DEV-025：有界候选扫描和明确的批次清单

- 状态：pending。依赖 DEV-024。
- 涉及文件：`src/CClean.Core/Cleanup/CleanupPlanner.cs`、`CrashDumpRule.cs`；必要时新增 `src/CClean.Core/Cleanup/CleanupCandidateScanner.cs`；`src/CClean.App/Views/CleanupPreviewView.xaml.cs`；`tests/CClean.Tests/CleanupPlanTests.cs`。
- 从 UI 移出“先整根扫描再 Take”逻辑，按已启用规则流式枚举允许的内容范围，调用同一保护策略收集合格对象；按规则轮换取候选，避免第一个大根饿死后面的规则。
- 在预算内形成至多 1,000 个对象的明确选择，再交由现有 Planner 绑定不可变清单。扫描诊断独立汇总，保留有限样本，不能让海量排除项先占满候选名额。
- 批次达到上限时结束本批，不把未列出的文件加入执行。展示已检查量、本批合格量、排除原因、覆盖是否完整；未全扫不能声称剩余为 0 或全目录候选总量已知。
- 缺口只影响相应根，其他可靠且独立的规则可保留；根重叠或无法唯一归属仍全部保护。预览后新增文件不会入单；直接向原 Planner 传超额选择仍保持拒绝，不通过削弱截断保护交付。
- 通过标准：0/1/1,000/1,001 候选及混合规则场景正确；1,001 场景可确认首批且清单外对象保留；取消、无权限、符号链接、深目录、预算耗尽不产生隐藏执行。10 万文件目录不需构造整根快照才能给出首批。

### DEV-026：首页一键入口、一次确认和结果反馈

- 状态：pending。依赖 DEV-025。
- 涉及文件：`src/CClean.App/Views/OverviewView.xaml`、同名 `.xaml.cs`、`src/CClean.App/MainWindow.xaml.cs`、`src/CClean.App/Views/CleanupPreviewView.xaml`、同名 `.xaml.cs`、`CleanupHistoryView.xaml`、同名 `.xaml.cs`、`src/CClean.App/ViewModels/WorkspaceViewModel.cs`。
- 首页主入口“一键清理”导航并生成统一预览；首次未配置时显示规则引导。现有 Windows 工具按钮明确写成“打开 Windows 磁盘清理”，不统计成应用内一键成功。
- 状态固定为：待配置/可开始 → 扫描中 → 待确认 → 执行中 → 完成/部分完成/已取消/需重新预览/失败。无候选展示可操作原因，不提供可用的删除确认按钮。
- 统一预览按规则列出路径、本批数量、分配估算、排除原因、剩余未知提示和到期时间。一次确认列明每条规则的影响，含 npm 重建与旧转储诊断内容损失；确认默认选择取消。
- 执行中跨页面只允许一个任务，进度和停止操作可见；换页、双击和重开预览不创建第二个执行器。规则变更、到期或活动变化要求重新预览，不能沿用旧确认。
- 结果分列成功、跳过、失败、未执行；对象分配估算与逐卷系统可用量变化分开。保留负值及测量失败，不用估算冒充实际释放；可进入持久审计。仅“本批完成”可标完成，有剩余时提供新预览。
- 通过标准：已配置用户只需首页入口与一次确认完成本批；混合规则不弹出逐规则确认；无候选、过期、取消、重入、换页及系统空间采样失败均有准确状态。键盘、焦点、浅深主题和 125%/150%/200% DPI 验证通过。

### DEV-027：真实 npm 与混合规则端到端验收

- 状态：pending。依赖 DEV-026。
- 涉及文件：`tests/CClean.Tests/CleanupWorkflowTests.cs`（拟新增）、既有 CleanupPlan/Execution/Recovery、DeletionSafety、Monitoring、CrashDumpRule 测试；`tools/CClean.Poc/CacheProbe.cs`、`tools/CClean.UiProbe/Program.cs`；`docs/testing/cleanup-acceptance.md`，按需要新增 `docs/testing/one-click-cleanup.md`。
- 自动回归复用现有测试工程；新增的工作流测试标 `Category=CleanupWorkflow` 和 `MvpP0`。验收命令必须选中大于 0 个测试，跳过必测不算通过。
- 使用独立 Windows 普通用户环境，从正式 UI 配置专用 npm 11.19.0 缓存。真实 npm 生成内容，消费者自然退出，实际静置至少 30 分钟并完成可靠首扫，再执行首页预览、确认、实际删除、审计与重建/安装。
- 用受控旧 WSL fixture 加入第二条规则验证统一批量。WSL 年龄分支可人工设置时间，但报告明确区分合成旧时间戳和真实静置，不作为真实 48 小时观察证据。
- 为 npm 缓存内容和安装产物记录验证用哈希；至少 3 个真实 npm 候选通过生产 watcher/活动链路整批执行，随后 cache add 和消费者安装成功。原 `cache-product` 的固定上下文结果只作回归参考。
- 下表 OC-01～12 全部执行并保存结果、审计、UI 证据、版本、代码提交、二进制哈希、样本数及未测范围；本机原始路径与会话信息留在忽略的 artifacts 中，仓库仅写脱敏摘要。
- 无独立 Windows 环境时保留 pending_validation/blocked 原因，完成其他可做验证；不关闭宿主消费者、不伪造观察，也不标 FR-14 完成。

### DEV-028：性能复核、使用说明与内部交付

- 状态：pending。依赖 DEV-027；关联 DEV-019/020，不自动关闭它们。
- 涉及文件：`tools/Measure-AppIdle.ps1`、`tools/Build-InternalPackage.ps1`、`packaging/CClean.iss`、`src/CClean.App/CClean.App.csproj`、`README.md`、`CHANGELOG.md`、`docs/user-guide.md`、`docs/audit/implemented-features.md`、`docs/testing/release-acceptance.md` 与计划状态。
- Release 构建及受影响回归通过后，使用相同受控数据复核首批预览时间、取消响应、执行进度和内存；10 万文件压力数据遵守原有实验卷/容量预算，禁止为验收填满 C 盘。
- 30 秒是扫描预算，不是已达到的性能结果；取消后目标 2 秒内返回可操作状态，结果以实测为准。执行每项校验不因性能优化变成只在开头检查一次；超过 5 分钟必须停止并重新预览。
- 运行既有 10 分钟/10 万文件空闲测量并记录实际峰值及 CPU，原 ≤200 MiB 和 ≤1% 门槛不放宽。若仍失败，保留 DEV-019/G4Full failed；可准备明确标注限制的内部包，但不得宣布完整 MVP 或正式发行验收通过。
- 文档说明两种支持规则、专用缓存前置、Node 消费者保护、批次上限、永久删除、剩余内容、失败与取消恢复。把 `docs/05-delivery-plan.md` 的历史“尚未开始”总述更新为当前事实并保留历史证据。
- 从可追溯干净提交构建内部包，验证新鲜安装/升级、旧规则配置迁移、数据保留和安装版一键 UI；不要复用旧版本 UI 截图当新版结果。交付仅覆盖个人内部测试，公开发行仍按原门槛。
- 通过标准：功能证据对应同一交付版本，随包说明与行为一致；更新 DEV-017/018 相关子项，只有其全部原验收也通过才改成 passed；DEV-013 DPI、DEV-019 性能及新版多日观察逐项独立判定。

## 必测矩阵

| 编号 | 场景 | 必须观察的结果 |
| --- | --- | --- |
| OC-01 | 初次启动、规则关闭、不支持 npm 版本、重启 | 不自动启用；版本真实；设置保持；显示明确原因 |
| OC-02 | 无候选、路径不存在、权限不足、没有连续观察 | 清楚区分空目录与未知；相应根不执行 |
| OC-03 | 真实 npm 多文件及真实监视器 | 至少 3 个对象整批完成；不因自身删除通知中断；重建和安装成功 |
| OC-04 | npm 与旧 WSL 同时合格，或其中一条禁用/受保护 | 一张统一清单、一次确认；规则保护独立；最新和 48 小时转储保留 |
| OC-05 | 1,000/1,001 候选、10 万目录项、海量排除项 | 明确首批可执行；新文件与未批准项保留；未全扫不伪称已清空 |
| OC-06 | 清单到期、根/叶替换、配置/版本/最新转储变化 | 旧批准失效；下一对象不删；需要重新预览 |
| OC-07 | 外部写入、同路径重建、消费者启动、通知溢出与 watcher 故障 | 外部/未知活动不被自身事件豁免；停止后续清理 |
| OC-08 | 链接、ADS、硬链接、源码/凭据/会话及清单外文件 | 删除数为 0；符合条件的普通文件仍可执行 |
| OC-09 | 连点、跨页、扫描取消、执行取消、关闭窗口 | 单实例执行；取消有界；只停止后续对象；重开不重放 |
| OC-10 | 审计预写/结果提交失败、低空间、进程异常退出 | 停止后续删除；未知结果不伪造成功；重启不自动续删 |
| OC-11 | 并发占盘、可用量变为零/负增长、空间测量失败 | 对象结果与逐卷容量独立显示，不伪造“释放”数字 |
| OC-12 | 安装/升级、历史规则配置、主题/键盘/三个 DPI | 配置和数据保留；未确认规则仍关闭；按钮/进度/确认可用 |

## 执行阶段验证命令

以下命令本轮未运行；`CleanupWorkflow` 测试文件需由 DEV-023/027 实现。先通过 `--list-tests` 核对场景确实存在，再执行，环境或必测场景缺失不记通过。

```powershell
dotnet build CClean.slnx -c Release
dotnet test tests/CClean.Tests -c Release --list-tests --filter 'Category=CleanupWorkflow|Category=CleanupPlan|Category=CleanupExecution|Category=CleanupRecovery|Category=Safety|Category=Monitoring'
dotnet test tests/CClean.Tests -c Release --filter 'Category=CleanupWorkflow|Category=CleanupPlan|Category=CleanupExecution|Category=CleanupRecovery|Category=Safety|Category=Monitoring'
dotnet test tests/CClean.Tests -c Release --filter Category=MvpP0
```

真实 UI、npm 重建和安装验收另按 OC 矩阵执行，不能由单元测试替代。打包脚本需要实测存在的 Inno Setup 7.1.0 路径和干净提交，执行时提供，不在规划里虚构本机路径。

## 风险与失败处理

| 风险 | 处理 |
| --- | --- |
| 自身活动和外部写入无法可靠区分 | DEV-023 先证明生产链路；证据不足继续停止，不关闭 watcher 或批量豁免整个根 |
| 日常宿主长期存在 Node 消费者 | 保留保护、给出具体原因；独立环境先验正向。精确消费者归因单独研究，本轮不靠猜测放行 |
| 大缓存首批处理后仍有剩余 | 结果明确本批范围；后续新清单需重新确认，不承诺全量一键 |
| 合并规则造成保护范围污染 | 规则各自保存保护上下文；混合正向与单规则阻塞场景都必须验 |
| 合格文件因真实竞态/到期无法删除 | 记录跳过或停止原因；不改时间戳、不延长旧批准、不扩大选择 |
| 新功能引入额外内存/索引压力 | 流式有界收集，复用现有执行器和预算；保留 200 MiB 历史失败直至实测通过 |

## 完成检查与记录

- [ ] DEV-023～028 验收和 OC-01～12 证据齐全，未测项不冒充通过。
- [ ] 已启用规则下，首页入口 → 统一预览 → 一次确认 → 本批执行 → 结果/审计完整可用。
- [ ] 真实外部 npm UI 删除、生产 watcher 链路和重建均通过，混合规则通过。
- [ ] 父计划与任务表同步，原性能/发行及其他缺项按证据分别保留或关闭。
- [ ] MCP 可用后按稳定任务 ID 追加/更新，无接口时继续标记未同步。

| 日期 | 操作 | 结果 |
| --- | --- | --- |
| 2026-10-06 | 核查 0.1.7 代码、历史验收并追加 6 项任务 | 规划完成；源代码未改、未运行清理；MCP 不可用 |

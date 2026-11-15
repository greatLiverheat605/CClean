# R1-007：分类工作台与范围管理页面实施记录

日期：2026-10-23（Asia/Shanghai）。计划：`CCLEAN-20261023-R1`。基线 HEAD：`7d63047fefb10645181c3e69c0479a940bbe50fa` 加未提交工作树，版本仍为 0.1.7；未安装、commit 或 push。

当前状态：pending_validation，已有实现与组件证据，尚未 passed。依赖 R1-001/002 的组件出口已通过；R1-003 连续清理未通过，本页面没有新增执行授权。偏好错误处理已修补并完成隔离验证；真实 UI、系统 DPI 与独立用户操作仍待验。

依据：[子计划](../../.codex/plans/current/2026-10-23_cclean-first-release/sub-007-workbench.md)、[页面规格](../../.codex/plans/current/2026-10-23_cclean-first-release/page-spec.md)、[验收基线](../08-first-release-plan.md)。

## 已实现内容及边界

| 内容 | 实现与限制 |
| --- | --- |
| 三个工作区 | 默认“空间与清理”；“开发活动”和“项目与版本”复用既有时间线、会话及只读比较。范围、处理历史和设置为从属页面，返回保留原页面实例与上下文。 |
| 多卷和范围 | 逐卷容量、已登记根的逻辑/分配/未知计量、观察状态、范围管理及项目/工具筛选。工具标签和物理根关联不证明写入者、独占或覆盖全部 Agent。 |
| 分类与说明 | 复用 ContentExplanation，分别呈现用途、关联依据、证据程度、价值、影响、重建条件与资格。分类侧栏的目录线索逻辑合计明确重叠未去重，父子和各类不能相加，也不是释放承诺。 |
| 有界列表 | 目录每页 100 条；根内已核验去重逻辑/分配与逐目录线索分开。清楚标记部分、过期、未知、跳过、未盘点、空范围与筛选无匹配。 |
| 只读文件入口 | 当前范围和所选目录复用 FilesView；按真实目录边界和当前 observation keys 查询既有索引，每页最多 200 项。不自动登记或扫描未登记目录，无索引不等于目录为空；隐藏预览且事件再次拒绝清理。 |
| 扫描与切换 | 显示阶段、耗时和最近盘点快照中的已读/跳过数，不编造百分比。取消先显示收尾；用 generation、登记 Id 和 ScopeRevision 防止旧请求覆盖新根。退出 drain 等待统计、盘点和文件读取至空，Stopping 后拒绝新请求。 |
| 暂停时间 | 保留本次 monitor 实际捕获时间；没有本次捕获时只显示当前 observation key 最近索引事件时间，并注明完整扫描时间未持久化。没有记录就显示无法确认。 |
| 交互修补 | 首次 Loaded 回顶部，返回文件页恢复滚动偏移；长路径选择项单行省略并带完整 Tooltip；非键盘目标不触发自动 BringIntoView，保留键盘目标滚入行为。左栏可滚动，窄/矮窗仍有退出入口。 |
| 清理边界 | 本页没有具体保留、批准或执行功能。Windows 存储和磁盘清理明确是独立外部窗口，不算 CClean 执行结果；页面不展示开发任务 ID 作为产品操作说明。 |

## 已取得的验证

- [最终完整 Release 构建](../../artifacts/r1-ui-lifecycle-tests/build-final.log) 为 0 warning / 0 error；新页面 XAML、事件和 C# 已实际编译，不再沿用最初“尚未构建”的静态结论。
- [主窗组件报告](../../artifacts/ui-probe-20261023T110242151/workbench-lifecycle.json)：20 个必需断言全部出现且为 true，errors 为空，safeExitCompleted=true。其中页面检查覆盖 124 条中文目录线索的 100/24 分页、范围从属页、返回保留根/页/选中路径、重扫后立即换根且旧结果不覆盖新根。生命周期细节见 [R1-009](first-release-r1-009.md)。
- [相关回归](../../artifacts/r1-ui-lifecycle-tests/r1-ui-lifecycle-final-recheck.trx)：MvpP0 / CleanupWorkflow 选择 152 项，全部通过，无失败或跳过。包含首次上下文读取期间取消、无删除/无审计执行记录及执行锁可重新取得的回归。
- [开发活动探针](../../artifacts/ui-probe-20261023T105231918/latency.json)：真实写入 → watcher → SQLite → 新开发活动时间线 OnRender，30 个小事件，P95 376.617 ms，最大 658.9888 ms；safeExitCompleted=true。它没有测操作系统实际呈现、人的输入或长时负载。
- [首次真实窗口](../../artifacts/ui-probe-20261023T102755762/workbench-lifecycle.json)：实际输入完成隔离中文项目选择、3 文件 / 48 B 盘点、深色主题和明确退出。实际 DPI=100%，不是安装版；该轮发现初始滚动偏移和长路径撑高，修补后的实际操作仍待复验。
- [修补后 UI 复测中断](../../artifacts/ui-probe-20261023T104951394/desktop-interruption.json)：Windows 桌面锁定，未发出界面输入；已核实并结束本轮隔离窗口进程。此记录不表示 UI 或安全退出通过。构建文件占用失败及随后旧构建报告不作为验收依据。

一次回归因断言仍匹配旧英文 dirty 而失败，原 [失败记录](../../artifacts/r1-ui-lifecycle-tests/r1-ui-lifecycle-final.trx) 保留。已将断言同步为“未提交改动”，上列 152 项复验通过；没有为了过测试删除对历史来源不充分和执行资格的校验。

## 尚未通过的出口

1. 修补后的顶部位置、长路径、分类侧栏、只读文件入口、返回位置、浅深主题和矮窗退出可达性仍需真实输入复测；锁定桌面不尝试绕过。
2. 三张说明卡、无权限/离线/摘要损坏/保存失败的实际页面反馈、慢读取/保存取消延迟、暂停及重启时间来源、文件相似前缀隔离、后台刷新时焦点稳定尚未全部实测。
3. Tab / Shift+Tab、方向键、可聚焦禁用原因、屏幕阅读器、对比度及实际 125% / 150% / 200% DPI、跨屏仍待验。离屏放大或组件调用不能替代系统 DPI 和键盘使用。
4. 当前未形成按清理对象区分用户保留、规则启用/工具版本、活动未知和重新预览的完整无候选矩阵；这些选择与规则状态由后续 R1-008 接入，不能据此反向要求 R1-007 依赖尚未开发的 R1-008。007 自身仍负责准确显示未盘点、部分、空、筛选无匹配、无适用规则及读取/配置失败。
5. 安装版、用户独立理解和首批完整清理闭环均未验收。原 200.73 MiB 失败、新版资源预算及 72 小时观察保持原状态；没有远端 MCP 同步。

主要源码：MainWindow、OverviewView、FilesView、ProjectSetupView、ProjectWorkbenchView 及 ContentExplanation。MainWindow.xaml/.cs 保持原 UTF-8 BOM / LF，其他本轮页面按各文件原风格保存；ProjectWorkbenchView.xaml.cs 最终恢复为原 UTF-8 无 BOM / LF，早期记录的 CRLF 判断不作为最终编码结论。

## 配置失败修补验证

AppPreferences 仅在确实缺少文件或父目录时使用首次默认值。通过真实 FileStream 有界读取最多 16 KiB + 1，兼容原 Save 的 UTF-8 BOM；损坏、null、超限、独占锁及访问错误均抛出，不吞为默认配置。MainWindow 在创建 Workspace 前加载，失败保留错误页；导航/设置不清掉错误，主题只能临时切换且不保存。

[14 项偏好检查](../../artifacts/ui-probe-20261023T110313101/preferences.json) 的固定必需名单完整、全部 true、errors 为空；最终坏文件仍为原 19 B，SHA-256 未变，Workspace 未创建，safeExitCompleted=true。检查调用真实 WPF 处理器但没有显示窗口或输入，不是实际 UI 使用验收。探针首次因继承的 OnLoaded 重载造成反射歧义，已限定为本类声明方法；[失败报告](../../artifacts/ui-probe-20261023T110242148/preferences.json) 保留，修正后取得上述完整报告。

[最终核对记录](../../artifacts/r1-ui-lifecycle-tests/final-verification.json) 保存证据哈希、112 个源码文件哈希、实际状态和未验边界；工作树仍为未提交状态，不能把 HEAD 单独当作本轮构建来源。

## 2026-10-24 桌面解锁后的实际输入复测

使用同一未提交开发构建、新建隔离配置与 3 × 16 B fixture，通过 Computer Use 实际鼠标/部分方向键操作；[运行报告](../../artifacts/ui-probe-20261024T024958386/workbench-lifecycle.json) 记录 exit 0、errors 为空、safeExitCompleted=true、实际 DPI=100%、installedUiAcceptance=false、passed=null。[逐项观察与未验范围](../../artifacts/ui-probe-20261024T024958386/desktop-observations.json) 另存，人工观察不伪装为自动完整验收。

已复验初次顶部可见、中文长路径选择项单行省略、只读 3 文件/48 B 盘点及 144 B 重叠目录线索提示、只读文件索引和可滚动用途详情、返回保留范围/结果/阅读位置、深色主题及明确退出。新 fixture 位于 artifacts，按 WorkProduct 优先分类；不能据此宣称 npm/target 或三张指定说明卡验收通过。

本次工作台矮窗口拖动未成功改变尺寸，未计通过；随后历史页实际缩矮发现表格塌陷，修复与复验另见 [历史分页证据](2026-10-24-history-pagination.md)。不能将历史页复验扩大为工作台全部矮窗通过。完整键盘、读屏、多 DPI、安装版和用户独立理解仍待进行。因此仅收窄上方待验第 1 项中的已复验部分，R1-007 保持 pending_validation。

## 2026-10-25 首屏与实际使用补验

首屏布局、24 项主窗组件以及实际隔离目录输入/盘点/Tab/主题/明确退出的新增证据见 [吞吐与工作台复验](2026-10-25-r1-003-throughput.md)。实际 DPI 为 100%；只收窄已完成的检查，不替代完整键盘/读屏、多 DPI、系统会话与安装版验收。任务仍 pending_validation。

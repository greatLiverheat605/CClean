# R1-009：托盘、单实例与观察生命周期证据

日期：2026-10-23（Asia/Shanghai）。基线 HEAD 为 7d63047fefb10645181c3e69c0479a940bbe50fa 加未提交工作树；版本号仍为 0.1.7，没有安装、commit 或 push。

状态：已有实现，pending_validation。组件与隔离窗口证据不能替代实际系统会话、完整安装版及用户验收。

## 实现

- 在创建 MainWindow / Workspace / SQLite 之前取得当前用户、Windows 会话及规范数据目录对应的本地 Mutex；次进程只发恢复 Event。交接失败不建立第二个工作区，所有权保留至完整收尾后的 App.OnExit。
- WPF 原生托盘提供打开、观察状态、临时暂停/恢复、设置与退出。不新增网络通道、服务或开机启动。Explorer 重建时尝试重新加入，托盘不可用时恢复可见主窗与退出入口。
- 普通关窗仅隐藏，保留页面、观察及当前清理。明确退出先同步 BeginShutdown 拒绝新任务，再取消并等待页面扫描、历史查询、Workspace 清理与保存，最后释放 watcher、SQLite、审计及单实例锁。
- 临时全局暂停不改各根 Paused 偏好；恢复只启动未单独暂停的根。观察代次变化使旧启动和旧活动批准失效。
- DirectoryMonitor.Dispose 的 watcher、取消、worker 等待、CTS 释放各阶段独立执行并保存异常，前一步异常不跳过后续释放。Workspace 的退休观察器异常不会丢失引用。
- 系统 SessionEnding 请求相同的安全退出。若异步收尾尚未完成，取消本次会话结束；收尾后退出 CClean，不自动重放系统注销，用户需要重新发起。这是当前实现的明确限制，尚未实测注销或关机。

## 已取得证据

| 证据 | 实际结果与边界 |
| --- | --- |
| [重复退出补测](../../artifacts/ui-probe-20261023T110242151/workbench-lifecycle.json) | 20 个完整必需断言全部通过，errors 为空，safeExitCompleted=true。含原 13 项、5 项目录分页/返回/旧扫描取消，以及重复 RequestExit 复用同一未完成 Task、两次 Close 不提前 Closed 或释放原 Workspace/Store/Running 审计。清理仍仅为自建样本和固定合成上下文。 |
| [首次主窗生命周期报告](../../artifacts/ui-probe-20261023T102719883/workbench-lifecycle.json) | 13 个组件断言通过：真实第二进程 RestoreSignaled、一次 Workspace、三导航和默认页、原生 tray 创建、关窗/恢复保页、全局暂停保留单根偏好；受控首项 after-delete 屏障下明确退出等待审计，结果 Cancelled / Deleted=1，另两项保留，watcher 释放。该清理为 3 个自建 16 B fixture、固定合成上下文，仅验证退出顺序，不能证明真实活动归属。 |
| [启动与保存竞争报告](../../artifacts/ui-probe-20261023T105037403/workspace-shutdown.json) | 两个真实 Dispatcher 屏障场景通过：启动未完成时拒绝登记/执行且 Resume 不新增观察；实际 500 ms timer 到达写入前保存屏障时 Store 与 watcher 仍可用，释放屏障后正常退出。屏障位于 SQLite 写入之前，不证明数据库写入正在执行时退出。没有执行清理，fixture 字节完整。 |
| [范围生命周期复测](../../artifacts/ui-probe-20261023T102737634/scope-lifecycle.json) | 10 个范围组件场景通过，包括迟到启动、保存中移除、跨卷、历史与遗留手动保护；此次 finally 已等待 ShutdownAsync。 |
| [新活动入口延迟](../../artifacts/ui-probe-20261023T105231918/latency.json) | 30 个顺序合成 100 B 事件，由真实 watcher → SQLite → 新开发活动项目时间线 OnRender；P95 376.617 ms，最大 658.9888 ms，安全退出完成。未测桌面呈现时刻、人的输入延迟或长期压力，不能关闭 R1-011。 |
| [回归记录](../../artifacts/r1-ui-lifecycle-tests/r1-ui-lifecycle-final-recheck.trx) | Release 下 MvpP0 / CleanupWorkflow 合并选择实际 152 项通过，无失败或跳过。数字只作为回归证据。 |
| [第一次真实窗口](../../artifacts/ui-probe-20261023T102755762/workbench-lifecycle.json) | @oai/sky 实际输入：选择隔离已登记中文项目，盘点得到 3 文件 / 48 B，切换深色主题，明确退出。实际 DPI=100%，不是安装版。此次暴露首载滚动和长路径下拉框增高，修复后另行复测。 |

辅助单实例测试使用普通 Application，并在清理退出屏障之前释放测试 Mutex；不能证明生产 App.OnExit 在审计完成后才释放所有权。主窗源码接线与组件时序分别记录，不能合并冒充生产 App 全路径。最新新增开始前取消回归覆盖首次 context 期间取消，返回 Cancelled / 0 且文件与执行锁完整；Begin 一旦尝试仍完整收尾，审计错误不被正常取消吞掉。

## 尚未通过的必需出口

- 尚未在同版安装版验证生产 App.OnStartup 的双启动全路径。helper 的真实进程交接不能代替这一项。
- 尚未实际操作托盘菜单的恢复/暂停/设置/退出、键盘菜单及 Explorer 重建。组件创建和事件调用不等于真实托盘可用性验收。
- 尚未实测系统注销/关机、异常终止及重启恢复核查；已有安全恢复测试不能冒充本轮真实进程故障。
- 主窗首次 Loaded 完成前退出、SQLite 写入进行中退出、慢页面查询/扫描的完整退出竞争、浅深主题与 125% / 150% / 200% 系统 DPI 仍需按页面证据逐项复验。未安装、未长跑，不关闭旧 200.73 MiB 失败或新版 72 小时验收。

本任务不改变 5 分钟、8 MiB、未知/活动保护、逐项审计或重新启动不续删协议；所有现有用户文件清理仍需具体清单确认。R1-003 的自阻断问题保持未通过，无远端 MCP 同步。

## 本轮中断记录

[修补后实际窗口复测](../../artifacts/ui-probe-20261023T104951394/desktop-interruption.json) 因 Windows 桌面锁定停止，未发出界面输入。为释放构建输出，已核实并结束本轮隔离窗口进程；不记为安全退出或异常重启恢复验收。一次构建因该探针文件占用失败，其后旧构建报告不用于验收；结束后重新完整 Release 构建为 0 warning / 0 error，再运行上列 20 项报告。

## 最终相关复验

[最终 Release 构建](../../artifacts/r1-ui-lifecycle-tests/build-final.log) 0 warning / 0 error；偏好加载前置后已重新取得上列 20 项完整主窗报告。[14 项偏好隔离检查](../../artifacts/ui-probe-20261023T110313101/preferences.json) 另证实错误初始化时不创建 Workspace、保留原文件且可安全退出；不是首次正常 Loaded 尚未完成时退出的专门竞争测试。

[最终核对记录](../../artifacts/r1-ui-lifecycle-tests/final-verification.json) 保存证据哈希、112 个源码文件哈希、实际状态和未验边界；工作树仍为未提交状态，不能把 HEAD 单独当作本轮构建来源。
## 2026-10-24 解锁后与历史读取复验

桌面已经解锁，本轮继续真实输入。[主窗组件复验](../../artifacts/ui-probe-20261024T031456156/workbench-lifecycle.json) 的 20 项必需名单逐项完整、顺序一致、全部 true，errors 为空、safeExitCompleted=true。与昨日相同，它只证明组件时序和合成 fixture 退出，不是生产 App / 安装版全部生命周期验收。

[工作台实际窗口](../../artifacts/ui-probe-20261024T024958386/workbench-lifecycle.json) 经实际鼠标和部分方向键操作后明确退出；exit 0、errors 为空、safeExitCompleted=true，DPI=100%、passed=null。[人工观察](../../artifacts/ui-probe-20261024T024958386/desktop-observations.json) 记录中文长路径、盘点、详情与返回，不扩大为托盘菜单或完整键盘验收。

历史页面后台读取已纳入 MainWindow.ExitCore 的取消及 Task.WhenAll 等待。[17 项历史组件](../../artifacts/ui-probe-20261024T031427425/history-paging.json) 验证 pending 查询不提前关闭 Store、停止后不接受新读取、最终 drain 和 fixture 完整；没有创建清理任务。[历史实际窗口](../../artifacts/ui-probe-20261024T031513498/history-desktop.json) 实际切换三种只读模式和分页后明确退出，safeExitCompleted=true、fixtureContentPreserved=true、cleanupTaskCreated=false、passed=null。矮窗出现表格塌陷，失败与修复复验统一见 [历史分页证据](2026-10-24-history-pagination.md)，不能把正常退出当布局通过。

本节仅更新当前已有证据；前面的锁屏、缺报告及故障记录保留为历史。生产双启动、真实托盘菜单、系统会话 / 异常重启、SQLite 写入中的退出、安装版和多 DPI 等尚未通过的出口继续保留，R1-009 仍为 pending_validation。

## 2026-10-25 首屏与实际使用补验

首屏布局、24 项主窗组件以及实际隔离目录输入/盘点/Tab/主题/明确退出的新增证据见 [吞吐与工作台复验](2026-10-25-r1-003-throughput.md)。实际 DPI 为 100%；只收窄已完成的检查，不替代完整键盘/读屏、多 DPI、系统会话与安装版验收。任务仍 pending_validation。

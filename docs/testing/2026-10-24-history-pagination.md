# 历史分页：组件与真实桌面证据

日期：2026-10-24（Asia/Shanghai）。基线为 `7d63047fefb10645181c3e69c0479a940bbe50fa` 加未提交工作树，开发版本仍为 0.1.7；不是安装版验收。只使用新建隔离显示样本：202 次任务、405 个对象、3,205 条初始审计；不调用执行器或清理现有用户文件。

## 实现与组件边界

[CleanupHistoryView](../../src/CClean.App/Views/CleanupHistoryView.xaml.cs) 每页最多 200 项，任务及批准清单 / 原始审计 / 恢复核查各自保存游标。恢复按清单序号推进，排除项也占检查进度；不是按显示行数判断末尾。原始审计始终可以单独查阅，当前缺失不能倒推历史删除来源，原任务不能重新执行。

查询在后台运行；切换模式、任务或卸载时取消旧请求并用代次拒绝迟到结果。明确退出先拒绝新操作，再等待读取任务收尾，然后释放 Workspace / SQLite。取消请求不能强行中断已经开始的 SQLite 或原生读取。

首次组件进程虽然退出 0，却没有生成最终报告：[缺报告记录](../../artifacts/ui-probe-20261024T030724392/validation-incomplete.json)，故未计通过。原因是生产 `Application.Shutdown` 终止 Dispatcher，探针的 await 续体来不及落盘；只调整探针在 `Application.Exit` 同步验证和保存，没有改变生产退出流程。

修正后的 [17 项组件报告](../../artifacts/ui-probe-20261024T031427425/history-paging.json) 必需名单完整且顺序一致、全部 true、errors 为空、safeExitCompleted=true、cleanupTaskCreated=false。覆盖完整尾页、审计高水位、排除项推进、三模式切换、迟到请求、读取错误与退出 drain。组件调用不等于人的操作或真实消费者证据。

## 真实桌面首次检查与失败

通过 Computer Use `@oai/sky` 对实际显示的主窗输入；[运行报告](../../artifacts/ui-probe-20261024T031513498/history-desktop.json) 和 [逐项观察](../../artifacts/ui-probe-20261024T031513498/desktop-observations.json) 分别保存进程事实与人工判定。

- 侧栏打开历史后，任务首页 200 项且可翻页；批准清单实际翻过 200 / 200 / 5 项，末页下一页禁用。
- 原始审计首页 200 条，界面说明意图不代表删除。通过 ComboBox 与方向键选择恢复核查，页面显示检查 200、显示 199、总计 405，明确只读、不续删。
- 系统菜单“大小”及底边拖动将真实窗口缩至约 1,226 × 600 px，实际 DPI 为 100%。此时两张表格被星号布局挤成细线，看不到数据行，**矮窗布局失败**。侧栏退出仍可见，实际点击“退出 CClean”后正常收尾。

报告 `passed=null`，不自动宣布 UI 验收。`safeExitCompleted=true / fixtureContentPreserved=true / cleanupTaskCreated=false`；原失败证据保留，后续修复另记。

## 尚未通过的出口

安装版、多 DPI、完整键盘和读屏、真实预览 / 清理链路、用户独立理解及大样本确认负担均未由上述记录验收。R1-007 / 009 继续 pending_validation；历史组件属于 R1-006 / 008 的前置工作，不能越过 R1-003 / 004 / 005 的依赖。

## 矮窗修复及实际复验

根因是主窗固定头部占用高度后，两个星号行分配不到可阅读空间。[XAML](../../src/CClean.App/Views/CleanupHistoryView.xaml) 改为整页像素滚动、Auto 行，两张 DataGrid 分别保持 180 / 260 DIP 有限视口，保留行 / 列虚拟化；没有引入布局 code-behind 或依赖。

[最终 Release 构建](../../artifacts/r1-20261024-tests/build-history-layout.log) 为 0 warning / 0 error。[18 项历史复验](../../artifacts/ui-probe-20261024T032528759/history-paging.json) 必需名单完整且顺序一致、全部 true、errors 为空、safeExitCompleted=true、cleanupTaskCreated=false。新增检查实际 Measure / Arrange / Render 一个 960 × 240 DIP 页面：外层 ViewportHeight=240、ScrollableHeight=416.96、底部按钮可达；任务 / 明细表高 180 / 260，分别只实体化 4 / 6 个 DataGridRow，200 项没有全部实体化。此组件检查不冒充系统 DPI。

随后新开实际主窗，[运行报告](../../artifacts/ui-probe-20261024T032612177/history-desktop.json) 与 [人工观察](../../artifacts/ui-probe-20261024T032612177/desktop-observations.json) 分开记录。通过系统菜单及拖动实际缩至约 1,226 × 600 px（100% DPI）：任务行正常可见，外层滚动可到明细与底部按钮；实际将批准清单翻过 200 / 200 / 5 项，尾页下一页禁用。滚回模式区后实际选择原始审计，并用方向键切换恢复核查；检查 200 / 显示 199 / 总计 405 的说明正常。深色主题保持内容与模式，侧栏退出始终可达，实际明确退出后 exit 0，safeExitCompleted=true、fixtureContentPreserved=true、cleanupTaskCreated=false、errors 为空。

本次只通过“历史页在所测 100% DPI / 约 600 px 矮窗下可阅读、滚动和翻页”的回归；原失败报告不改写。长路径表格内省略后的完整路径交互、完整键盘 / 读屏、滚动条深色一致性、其他页面尺寸和多 DPI 仍未据此验收。报告的 passed 继续为 null，安装版 / 用户独立验收未通过。

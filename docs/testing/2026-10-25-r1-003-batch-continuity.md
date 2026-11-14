# R1-003：跨批证据衔接（2026-10-25）

## 实施边界

沿用 D-13/B 与用户“继续”的授权。本轮增加核心执行器的 `PreviewNext`：前批完整成功后，在同一进程和执行器内复核前批证据，重新读取调用方明确选择的对象，产生新的有限清单。每批仍需单独明确确认，预览不删除文件、不创建执行任务、不沿用前批批准。

前批证据必须在其原有 5 分钟期限内通过下一次预览和执行入场复核；新清单有独立 ID、时间、对象及 5 分钟期限。原始近期活动显示不变，新增文件不自动加入。失败、部分成功、取消、重启及其他执行器不提供连续权限。消费者、配置、观察代次/缺口、写入、新增、改名和未批准删除继续阻断。

审计在前批记录 `NextPreview`（Approved=false），新批开始时记录 `ContinuationFrom`。关联 ID 仅存于内存，恢复读取不能恢复可执行批准。最多继承 1000 个回执，加当前批最多 1000 个回执；未保留回执对应的延迟通知会保守停止。通知来源仍为 undetermined，不声称证明唯一来源。

## 验证范围

新增 20 项检查，包括真实 watcher 下 3 批 × 1 项和 3 批 × 100 项的分别确认执行、未选对象保留、原始活动可见、各批审计与恢复；危险变化和审计失败；预览后确认前的新增/消费者/配置变化；不同执行器、重放、前批到期、取消和失败任务不得衔接。

正向跨批测试等待每批目录快照稳定，耗时包含等待；不是无等待吞吐测量。样本仅为隔离生成的小文件，不是原用户文件或真实 npm 内容/索引兼容性验收。5 代 × 1000 回执仅为合成状态有界性测试，没有实际删除 5000 项的含义。

## 尚未满足的出口

- 本轮只有核心 API 与隔离验证；未新增 Workspace 或 UI 连续预览入口，普通产品入口仍采用严格契约。此前普通 Preview 的近期活动阻断仍正确存在。
- 未完成实际用户连续选择、保留、确认和取消的 UI 闭环；自动化调用 ExecuteApproved 不等于用户点击确认。
- 单批 1000 项、8 MiB 和 5 分钟限制未变；100000 项至少需 100 次明确确认。逐批对象复核的 O(n²) 成本未解决。
- 1001/10000 项仍只验证过截断拒绝，真实规则重建/组合与资源成本尚未验收。
- R1-003 保持 in_progress；004/005/006 不越过依赖。安装版、多 DPI、200.73 MiB 旧失败和 72 小时验收保留。

本轮未安装、提交、推送或清理既有用户文件，原有未提交工作及历史失败证据保留。

## 最终实测与证据

- `dotnet build CClean.slnx -c Release --nologo`：0 警告、0 错误；日志 `artifacts/r1-20261025-continuity/build-final.log`。
- `dotnet test tests/CClean.Tests -c Release --no-build --filter "Category=MvpP0|Category=CleanupWorkflow"`：210/210 通过、0 跳过；`artifacts/r1-20261025-continuity/regression.trx`。
- `dotnet run --project tools/CClean.UiProbe -c Release --no-build -- --object-contract`：13项保护检查通过；`artifacts/ui-probe-20261025T094339038/object-contract.json`。真实 Node 消费者使候选为零，productionExecutionQualified=false。

跨批实测输出：

- Real watcher: 3 separately approved batches x 1, 356 ms including snapshot settling; unselected file retained; raw activity remains active.
- Real watcher: 3 separately approved batches x 100, 5281 ms including snapshot settling; unselected file retained; raw activity remains active.

旧28项任务表 SHA256：616e70ff601e231793092ead029ea3d2b486a6a2d062b8f8e577ddfdecb359b8，复核未变。构建源码及证据哈希见 `artifacts/r1-20261025-continuity/validation-summary.json`；上轮摘要保持历史原值。

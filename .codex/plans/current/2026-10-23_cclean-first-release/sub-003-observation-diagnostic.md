# R1-003：隔离原生观察诊断

状态：隔离诊断已完成；来源证明不成立，R1-003 原生产出口保持未通过。2026-10-23 用户已明确允许前置此前提出的窄范围诊断；授权仅为新建 fixture、读取已有 journal、无 journal 配置写入、不默认提权、不接入生产事件豁免，不改变 5 分钟、8 MiB、活动与未知保护。旧 ETW/USN 产品任务的 deferred 状态不因此改写。

- [x] 复用 ExperimentSandbox，新增仅在 PoC 使用的扩展通知与只读 USN helper；固定缓冲区、记录、时间预算，保留 API 失败和完整性缺口。
- [x] 在独立新运行中记录真实 watcher、原生 FileId/ParentFileId、Core 身份映射与执行审计的时间顺序，复现原执行器首项后停止。
- [x] 核验父目录直接时间修改、瞬时外部对象、长寿命句柄重复原因和嵌套移入/移出等反例，不根据路径、时间或父引用推断操作来源。
- [x] 运行有意义的 parser 边界检查、隔离诊断与现有回归；按证据报告权限、journal 游标、记录版本、退出及范围映射缺口，更新本地任务表。诊断完成不等于 R1-003 生产出口 passed。

实现所有权：UsnDiagnosticReader、NativeDirectoryNotifications、诊断 driver 分开；统一构建运行。产品 DirectoryMonitor/Activity/Executor/Journal 不改动，不读取原 benchmark 或用户文件内容。方案：[具体边界](../../../../docs/testing/2026-10-23-r1-003-options.md)。


结果：[正式报告](../../../../docs/testing/2026-10-23-r1-003-observation-diagnostic.md)。8 组动作、26 阶段与 parser 检查完成，实际捕获重复修改有 Changed 而新 USN 为 0 的反例。混合交错、扫描屏障、平面对照及原生故障等未覆盖项在报告中明确列出；不把各组完成当作来源或产品验收。

# DEV-006 存储预算与真实磁盘满

2026-09-17 / SDK 10.0.401 / Microsoft.Data.Sqlite 10.0.12。旧的 10.0.0 依赖链收到 NuGet 高风险告警，最终改用当日稳定补丁 10.0.12，恢复不再报告该漏洞。

默认预算 256 MiB 的 storage-budget 调用返回 2，未偷偷扩大实验。正向调用使用核实的 D 盘 NTFS、1 GiB 总预算、10 GiB 宿主保留量：

```powershell
dotnet run --project tools/CClean.Poc -c Release -- --case storage-budget --sandbox D:/CCleanLab/CCLEAN-20260917-DEV/storage-budget --max-fixture-bytes 1073741824 --reserve-free-bytes 10737418240 --quota-root Z:/run-001 --output artifacts/m0/storage-budget.json
```

`--quota-root` 必须先准备独立的小型虚拟卷；不能直接复用上述 Z 盘字母。真实运行前已创建全新 256 MiB 动态 VHDX，核实 Associated disk、Microsoft 虚拟磁盘型号、非启动/分页盘和大小，才分区并格式化。卷有专用 label/归属标记；代码拒绝系统卷、未标记卷和超过 256 MiB 的卷。实验结束已卸载 VHDX，保留证据文件，宿主磁盘未写满。缺少该能力返回 2，不把注入结果当实机结果。

长读事务期间，264 个 1 MiB 插入批次使 DB/WAL/SHM 总量达到 281,557,544 字节，守卫在 300 MiB 上限前停止增长；长读释放前 checkpoint 无法完成，释放后 TRUNCATE 将 WAL 截断。RuntimeBudget 同时约束数据库 300 MiB、日志 50 MiB、临时 20 MiB、总体 400 MiB，在 90% 开始降级。

注入测试验证低空间关闭采集、删除与 VACUUM；真实限额卷写入失败返回 Win32 112（磁盘满），随后审计 INSERT 返回 SQLITE_FULL 13，删除准入关闭。宿主保留量始终满足。注入与真实 I/O 在 JSON 中分别标注。

结果：`20260917T085546979-568056e4b811419bb0d4c91f5ef278a7`，3 passed / 0 failed / 0 skipped；RuntimeBudget 回归 1 项通过。G4 的预算与错误处理部分通过；完整 UI CPU、内存、100,000 文件性能仍待 DEV-019。

# R1-003：隔离原生观察诊断结果

日期：2026-10-23（Asia/Shanghai）。用户已明确批准前置隔离诊断；实施只涉及新建的 fixture 与读取已有 NTFS journal。未配置 journal、申请提权、安装服务或接入生产通知豁免。当前进程继承的有效管理员角色为 true，因此本次成功不证明普通权限模式可读。原 benchmark、Agent 数据和其他现有用户文件未读正文、未清理。

**结论：扩展通知和 USN 提供了对象身份及原因信息，但本次未形成父目录变化的生产归属证明。已复现“重复修改产生 Changed，但新增 USN 区间没有记录”。原执行器仍在删除 1 项后停止，R1-003 保持 in_progress，全部原出口尚未通过。** 本结果否定按父路径/时间或空 USN 区间直接豁免，不声称穷尽了所有可能的观察协议。

## 1. 可复测产物与边界

- 源码基线：`7d63047fefb10645181c3e69c0479a940bbe50fa` 加未提交工作树，版本仍为 0.1.7。未 commit/push 或安装。
- 新增 `tools/CClean.Poc/DeletionObservationProbe.cs`、`NativeDirectoryNotifications.cs`、`UsnDiagnosticReader.cs`；通过原 PoC 的 `--case deletion-observation` 调用，不添加依赖。生产 DirectoryMonitor、Activity、Executor、Journal 未因本诊断放宽。
- 最终报告：[result.json](../../artifacts/r1-003-observation-final-20261023T100323137-d1af2536823247adad03061977d6336f/result.json)，344,268 B；SHA-256 `7a1824121e11a5e0cc0a3da929567a6113dc73bfd3e626d2668286d14d05d339`。
- 最终 fixture：`D:\CCleanLab\CCLEAN-20260917-DEV\r1-003-observation-282cb559a37343db9eebc3a27b94e555\20261023T100323173-3212383d8a51475591a7861d4bb18d8b`。保留样本与 SQLite 审计，不清理旧实验。
- 首轮报告：[初版 result.json](../../artifacts/r1-003-observation-20261023T095858935-b12745a6d2ae4641988fdcf24e4451e8/result.json) 保留。初版协商得到 V3 身份，不能证明 Ex64 桥；最终分别显式请求 `(2,2)` 与 `(3,3)`，同时保留两次结果。初版 `ScanGaps/scanGaps` JSON 大小写冲突已修为 `snapshotGaps`，未改写初版产物。

复测命令（必须换新 GUID，不复用用户目录）：

```powershell
. .\tools\Enter-Dev.ps1
dotnet build tools\CClean.Poc -c Release --nologo
dotnet run --project tools/CClean.Poc -c Release --no-build -- `
  --case deletion-observation `
  --sandbox ('D:\CCleanLab\CCLEAN-20260917-DEV\r1-003-observation-' + [guid]::NewGuid().ToString('N')) `
  --output 'C:\Users\Administrator\Desktop\CClean\artifacts\r1-003-observation-final.json' `
  --max-fixture-bytes 4194304
```

沿用 ExperimentSandbox 的所有者标记、目录租约、NTFS/范围检查、4 MiB fixture/审计预算与至少 10 GiB 空闲余量。每场景只生成 3 个 16 B 样本及一个小型嵌套移动样本；时间人工设旧，仅用于合成规则资格，不证明自然静置或真实 npm 重建。

## 2. 采集方式与真实能力

每场景同时启用生产 DirectoryMonitor、独立原始 FileSystemWatcher、ReadDirectoryChangesExW；根为场景 `cache`，被改父目录为根内 `_cacache/content-v2`。因此父目录变化在通知覆盖内；未用“监测根自身无通知”伪造安全结论。审计放在 watcher 根外的同场景 `audit`。

生产/原始 FSW 使用 `FileName|DirectoryName|Size|LastWrite`、4 KiB 缓冲；扩展通知使用 `0x17F`、64 KiB 缓冲、4,096 事件上限、256 条缺口明细及累计缺口。后者包含额外属性等过滤器，其能力不冒称产品已有能力。只有原生请求成功排队后才开始动作；Dispose 使用 CancelIoEx，实测全部 worker 退出。

USN 只用现有卷的查询和读取：单区间 4 MiB、16,384 记录、2 秒、64 KiB 缓冲，`Timeout=0`、`BytesToWaitFor=0`，检查代次、裁剪、游标、版本和记录边界。同步系统调用本身不能强制在时间预算内抢占，报告已明确此限制。不保存未匹配卷内记录的名字；未知父引用保留计数，不能当作已证明范围外。

最终 8 场景共 26 个捕获阶段，读取返回共 4,144 B、40 条记录；单区间最大 1,464 B、读取最长 6 ms。所有捕获区间均读完，无本次原生/原始 FSW 缺口；未知父计数合计 21，不据此证明完整 fixture 覆盖。73 个预先只读打开的身份都验证 V2/V3 桥。完整 Core 身份、卷、创建时间、原生 64/128 位 ID 与精确 USN/FILETIME 字符串被分别保留。

这些桥接来自诊断预先打开的同一只读句柄，**不是在实际生产删除句柄上新增了归属回执**。FSCTL_READ_FILE_USN_DATA 只使用身份及父字段，其无效的 TimeStamp、Reason、SourceInfo 不作活动证据。扩展通知和 journal 记录均无可信执行器 PID/清单 ID。

## 3. 八组实际结果

| 场景 | 实际结果 | 可支持的结论 |
| --- | --- | --- |
| 原执行器真实链路 | 预览 3 项；删除首项后 FSW 为该项 Deleted + 父 Changed，扩展为 Action 2 + 3；USN 记录样本 FILE_DELETE\|CLOSE；结果 NeedsPreview、Deleted=1，余 2 项保留 | 自阻断仍存在；同父 Changed 不能自动当自身变化 |
| 父目录直接改时间 | 无删除；同一相对父路径 Changed / Action 3；USN 为 BASIC_INFO_CHANGE 与其 CLOSE，实际时间等于请求时间 | 父路径和动作类型不唯一决定原因；两组完整轨迹未声称相同 |
| 父目录属性切换/恢复 | 扩展报告 Action 3、USN 有 BASIC_INFO_CHANGE；生产过滤器未报告 Changed | 扩展与产品过滤能力存在差别，未通知不等于未修改 |
| 进入范围后删除的瞬时对象 | 记录 Created、Changed、Deleted 和对应 journal 原因；最终文件不存在 | 终态不存在不能代替活动历史；没有冻结中间扫描，不称“生命周期严格位于两次扫描之间” |
| 父目录同一长寿命句柄 | 第一次 SetFileTime 有 BASIC_INFO_CHANGE；第二次实际时间已改变，FSW/扩展均有 Changed，但新区间 RecordsRead=0 且 IntervalComplete=true；关闭后才有 BASIC_INFO_CHANGE\|CLOSE | 空的新 USN 区间不能排除重复父修改 |
| 文件同一长寿命写句柄 | 16 B 文件位置 0 先写 0x51 再写 0x52，各自 Flush；两次均有 Changed。第一次有 DATA_OVERWRITE，第二次新区间 0 条；关闭才汇总 DATA_OVERWRITE\|CLOSE，最终首字节 82 | 空的新 USN 区间不能排除重复同长度写入 |
| 嵌套目录移入/移出 | 各有目录 Created/Deleted 及父 Changed、旧/新名 journal 原因；没有对子树全部成员逐项发通知；保留未知父计数 | 静态初始身份表不证明全部动态子树覆盖；两次间有取样等待，不称瞬时移入即移出 |
| 空闲取消 | 无注入动作；所有原生 worker 返回 cancelled，8 场景总收尾 0～2 ms，包含空闲 pending 请求 | 本配置的取消退出有效；不证明所有系统/权限条件 |

总运行约 5.62 秒，PoC 峰值工作集 53,215,232 B（约 50.75 MiB）。这是小诊断进程，**不替代新版产品 200 MiB 门槛**。报告的 9 passed 是 parser 检查和 8 组动作/特征断言完成；各组 `evidenceStatus=diagnostic-data-collected-source-unproven`，来源证明与生产豁免均为 false。

## 4. 验证与未覆盖

首轮统一 solution Release 构建 0 警告/错误；最终 PoC Release 构建 0 警告/错误。两个 parser 自检覆盖 V2/V3、截断、奇数 UTF-16 字节长、越界、未知版本、非法时间、journal 代次/裁剪/回退、扩展代理项及跨记录名字；真实小隔离运行退出 0。现有 CleanupWorkflow 回归选中 7 项，7/7 通过，其中保留首项后停止的特征断言，不称生产连续清理通过。

本轮没有做父=监测根平面对照、自删与父/文件修改混合交错、受控冻结扫描的瞬时对象、动态未知父完整重建、真实内核溢出/权限丢失/journal 重建/裁剪或删除后审计失败的原生专门场景。既有受控 watcher/外部变化保护测试仍保留，但不能冒充这些新场景实测。未读取整卷名字，不建立全机来源索引，不为补测配置 journal。

后续原生产品协议仍须证明如何同时处理具体删除、父元数据、未知父、长寿命句柄及观察缺口；若进一步研究更严格目录共享租约，其对开发程序的影响和覆盖边界必须另有具体方案与决定。本次没有批准或实现此租约，也不假定它能锁住子树。

R1-004/005/006 的依赖尚未满足，不绕过状态开通真实用户文件执行。按既有依赖继续独立的 R1-007 页面工作；5 分钟、8 MiB、活动/未知保护保持。旧任务表 SHA-256 仍为 `616e70ff601e231793092ead029ea3d2b486a6a2d062b8f8e577ddfdecb359b8`。MCP 任务工具不可用，仅更新本地记录。

# 决策、待验证事项与资料来源

日期：2026-09-17。文档依据本次需求澄清和实际读取的公开资料编制；尚未运行 Windows 监测/清理 PoC。

## 1. 用户已确认

| 编号 | 决定 | 对实施的影响 |
| --- | --- | --- |
| D-01 | 以项目 → Agent 会话 → 文件写入为分支时间线，可按时间回看 | 不要求与真实 Git 分支一一对应 |
| D-02 | 先预览，跳过活跃项目和来源不明文件，一键只执行确认过的规则 | 所有清理入口遵守保护，低空间不自动授权删除 |
| D-03 | 主要工具为 Codex、Claude Code、ZCode | 三工具有通用适配，深层识别逐版本验证 |
| D-04 | ZCode 是 https://zcode.z.ai/cn 对应产品 | 已核实网站有 Windows 下载；未据此推断缓存结构 |
| D-05 | 当前尚未建立 GitHub 仓库，需要同步项目 | 本次建立仓库并提交规划文档 |

## 2. 已告知的默认值与工程建议

| 编号 | 决定 | 性质 |
| --- | --- | --- |
| A-01 | Windows 11 本地桌面工具，首版 x64、NTFS | 已告知默认，符合当前环境 |
| A-02 | 当前 GitHub 账户下新建名为 CClean 的私有仓库 | 已告知默认，用户未指定其他仓库/可见性 |
| A-03 | .NET 10 LTS + WPF + SQLite | 工程推荐，需 M0 验证，不代表用户指定技术栈 |
| A-04 | 普通模式先做有范围的目录监测与全盘容量采样 | 以可用性与可验证性控制首版范围 |
| A-05 | ETW / USN 增强作为独立里程碑 | 精确归因宣传以实际验证为准 |
| A-06 | 本次仅需求、架构、UX、执行文档和 GitHub 初始化 | 不把应用功能标成已开发，不生成虚假测试通过记录 |
| A-07 | 公开发布前另行决定许可证、开源策略与签名渠道 | 私有规划阶段不擅自指定开源许可证 |

## 3. 待验证问题及解决时机

这些是开发阶段需要实证的事项，目前不阻塞规划同步，也不能静默当作已解决。

| 编号 | 未决问题 | 解决方式 | 阻塞范围 |
| --- | --- | --- | --- |
| Q-01 | 三个工具的具体安装版本、进程形态、用户目录和公开适配接口 | M0 在测试环境记录版本与官方/实测依据，不读取凭据或聊天内容 | 自动识别、深层会话适配 |
| Q-02 | 哪一个真实外部独占缓存可在活动排除后进入常规一键 | M0 逐工具验证规则；仅共享缓存的整体清理不满足该门槛 | 完整一键清理 MVP |
| Q-03 | Windows 句柄方案能否防住叶子/祖先替换、并发写入和流变化 | M0 对抗用例，明确支持对象类型与跳过行为 | 任何真实删除入口 |
| Q-04 | 目录范围、文件量和事件频率下的资源预算是否合理 | 基准与压力测试，记录硬件/负载/版本 | 性能承诺与默认参数 |
| Q-05 | ETW 权限、线程到进程/文件映射和特殊写入覆盖 | M6 分场景验证；无法确定的保留未知 | 精确进程归因 |
| Q-06 | USN 权限、现有 journal 可读性和游标恢复 | M6 只读验证；拒绝权限或失效时补扫 | 广域增量恢复 |
| Q-07 | 用户是否需要 WSL/Docker 内部清理或跨盘迁移 | P2 单独澄清；当前只展示不透明大文件 | 后续扩展，不影响普通模式 |
| Q-08 | 安装包签名、升级机制和发布渠道 | M5 前结合发行范围确定 | 对外发行 |

## 4. 参考资料

下列页面在规划阶段已实际读取。资料证明 API 的语义和约束，不证明 CClean 已达到性能或安全指标。引用编号在架构文档中使用。

| 编号 | 资料 | 支持的结论 |
| --- | --- | --- |
| S1 | [FileSystemWatcher](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0)；[ReadDirectoryChangesW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw) | 变化通知重复、缓冲溢出、重新枚举以及目录访问要求 |
| S2 | [USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)；[Change Journal Records](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records) | USN 字段不含 PID/写入字节，记录可能合并，不能用于回滚内容 |
| S3 | [Using the Change Journal Identifier](https://learn.microsoft.com/en-us/windows/win32/fileio/using-the-change-journal-identifier) | Journal ID 与历史范围改变需要检测与恢复 |
| S4 | [FileIo_ReadWrite](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite)；[FileIo](https://learn.microsoft.com/en-us/windows/win32/etw/fileio) | 文件 I/O 请求事件的线程、对象、请求字节及起始语义 |
| S5 | [Thread_V2_TypeGroup1](https://learn.microsoft.com/en-us/windows/win32/etw/thread-v2-typegroup1)；[Process_V2_TypeGroup1](https://learn.microsoft.com/en-us/windows/win32/etw/process-v2-typegroup1) | 线程和进程生命周期关联所需元数据 |
| S6 | [EVENT_TRACE_PROPERTIES](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties) | ETW 丢事件及丢缓冲计数 |
| S7 | [StartTraceW](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew) | 跟踪会话与权限相关约束 |
| S8 | [File security and access rights](https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights) | 枚举、读取元数据与删除具有不同访问要求 |
| S9 | [FILE_STANDARD_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info) | EndOfFile 与 AllocationSize 的区别 |
| S10 | [GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw) | 当前用户可用空间与卷剩余量需区分 |
| S11 | [GetCompressedFileSizeW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getcompressedfilesizew) | 压缩、稀疏与普通文件返回值的区别 |
| S12 | [Hard links and junctions](https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions)；[FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)；[Reparse points](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points) | 多路径身份、跨卷链接及路径解析边界 |
| S13 | [DeleteFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-deletefilew) | 删除、共享句柄和延迟释放的关系 |
| S14 | [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) | .NET 10 的 LTS 支持策略，开发时仍需固定补丁版本 |
| S15 | [ZCode 官网](https://zcode.z.ai/cn) | 产品身份与页面列出的 Windows 平台下载；不能证明缓存删除规则 |
| S16 | [File streams](https://learn.microsoft.com/en-us/windows/win32/fileio/file-streams) | NTFS 数据流具有各自的大小与分配属性，可枚举数据流 |

## 5. 需要始终保留的限制说明

- 同期发生变化不等于由指定 Agent 写入；上下文关联与直接进程证据始终分开。
- 文件大小、请求写入字节、分配估算和系统可用空间不是同一个指标。
- 工具数据目录可能包含高价值持久数据，不能凭目录名批量清除。
- 回收站/同盘隔离通常不释放 C 盘；永久删除无通用撤销。
- 历史只记录观察到的元数据和聚合结果，不提供 Git 式内容恢复。
- 当前普通模式依赖登记范围和能力覆盖；无法保证抵消任意进程的持续写入。
- 规划文档不构成上述 API 已被实现或通过实机测试的证据。

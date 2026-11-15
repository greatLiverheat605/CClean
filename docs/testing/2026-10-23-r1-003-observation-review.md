# R1-003：隔离观察诊断独立审阅

日期：2026-10-23。审阅对象为现有 `DirectoryMonitor`、`WindowsFileMetadata`、`VerifiedFileDeletion`、`CleanupExecutor`、`CleanupWorkflowTests` 与 [方案边界](2026-10-23-r1-003-options.md)。本次只读审阅源码及 Microsoft 官方说明，仅新增本文；未改产品、生成样本、执行删除、构建或运行诊断。用户已经批准隔离 USN / 扩展通知研究，但没有批准生产事件豁免、journal 配置变更或新的删除协议。

**预先结论：相同父路径的 `Changed` 不包含因果信息。身份与 journal 连续性可以增加反例的可见性，但不能先验地把父变化归为当前子项删除。本文的场景是待实测清单，不是已观察到的系统行为；正式结论须对照本次原始报告复核。**

## 1. 必须先固定的观察边界

- 产品 `DirectoryMonitor` 使用 `FileName | DirectoryName | Size | LastWrite`，所有通知直接推进 `LastActivity`，提示队列约 100 ms 后触发扫描；快照没有父目录元数据历史，也不会保存已经消失的瞬时对象。诊断必须记录每路 `NotifyFilter`，不能把扩展通知额外启用 `Attributes` 后的覆盖说成产品已有覆盖。
- `ReadDirectoryChangesExW` 明确不报告被监测目录自身的变化。父时间 / 属性实验应以 `root/subdir/sample` 为主，使父 `subdir` 位于监测根之下；`root/sample` 只作平面对照。根自身未通知不能证明父元数据没有变化。
- 观察器应在动作前完成原生请求排队和基线；打开目录句柄不等于已成功提交通知请求。停止或重新提交间存在缺口时必须记录，不能仅靠一段静默等待宣称完整。
- 同步成功且返回 0 B、解析失败、队列丢失、未知父身份、未配对 rename、游标失效、journal 代次改变、权限或文件系统不支持，都属于未知。普通模式不可用时记未覆盖，不提权、不创建或重建 journal。
- 两条异步观察链之间没有通知顺序保证。每个动作与接收点记录 UTC、单调时钟和本地序号；对象的 `LastModificationTime` / `LastChangeTime` 不是“通知产生时刻”，USN 的关闭汇总时间也不能充当调用者时间。

## 2. 最小八组场景

每组使用本次独立 fixture 与独立监测代次，除明确需要的长寿命句柄外先结束准备句柄。保留原始事件及所有失败；对照组不要复用已经带未知缺口的 watcher。

| 编号 | 操作与对照 | 要观察的最小事实 | 允许形成的结论 |
| --- | --- | --- | --- |
| O-01 | 三个旧合格样本：先平面父 = 根，再嵌套父；只做当前执行器的同句柄删除。 | 文件 `Deleted`、父 `Changed`、扩展身份、USN 原因、删除标记 / 句柄关闭 / 审计提交的相对阶段；保留剩余文件与索引。 | 复现自阻断及通知形状；不能凭“没有外部脚本”推出生产归属充分。诊断不绕过活动保护让产品强行连续三项。 |
| O-02 | 与 O-01 使用相同嵌套位置；独立直接设置父的 LastWriteTime，再做“自删 + 父时间修改”；属性单独开关作补充。 | 直接元数据操作是否同样投影为父路径 `Changed` / `FILE_ACTION_MODIFIED`；各过滤器、原生原因和元数据是否不同。属性未触发产品通知也如实记录。 | 两种因果能产生相同父路径 / 类型的反例即可否定按路径和时间豁免；不要求两组完整原始字节完全相同。 |
| O-03 | 先打开父目录可写属性句柄，修改时间或属性以累积 `BASIC_INFO_CHANGE`；读到预热水位后保持该句柄，执行一次自删并重复同类父修改，最后才关闭。 | 预热、操作、关闭前后分别读 journal；第二次同类变化是否没有独立新 USN 原因记录；扩展通知是否仍能看见及能否确定因果。 | 官方允许同类原因合并；若本机复现，则反驳“新游标中没有父 BASIC_INFO 就没有外部父变化”。未复现只记该配置未复现，不把反例排除。 |
| O-04 | 首项结果之后，在同父下创建、写入、删除一个非批准瞬时对象；受控屏障保证其生命周期位于两次扫描之间。 | 最终快照相对自删对照相同，但原始通知 / USN 是否含瞬时对象；记录屏障是受控调度，不冒充真实内核故障。 | 证明扫描净结果不能代替活动历史；新身份不得被同父自删回执吞掉。 |
| O-05 | 同父或嵌套位置的非批准文件保持写句柄：先固定长度覆盖一次并预热水位，之后自删与第二次同长度覆盖交错，结束窗口后再关闭。 | 重复 `DATA_OVERWRITE` 是否合并；避免追加触发新的 `DATA_EXTEND`；分别记录 flush 与 close，比较每阶段元数据和通知。 | 检查“只读当前 USN 窗口”能否漏掉长寿命句柄的重复外部写入；同类原因已存在不代表第二次没有发生。 |
| O-06 | 将 fixture 内但监测根外的嵌套目录移入根内，再在扫描前移出；并以根内新建子目录作对照。 | 移动目录身份、old/new parent、rename 配对、内部子项是否逐项通知；新父身份链能否建立。保留移动后的自产资料。 | 不能按启动时目录表或文件名过滤 USN；未知父、只收到目录 rename、未覆盖子树都不能按“范围外”静默丢弃。 |
| O-07 | 首项审计提交之后、第二项打开之前，同路径替换第二项；另改动未批准的同父对象。 | 同名新对象的完整身份与批准身份不同；第二项及后续均保留，外部对象不产生自身回执。 | 删除回执仅属于被同句柄证实的对象；不扩展到路径、父目录、同卷或整个批次。不能把替换注入放在已持有拒绝共享写 / 删除的叶子句柄期间再宣称覆盖成功。 |
| O-08 | 明确未知与结果失败：现有 watcher 受控故障 / 队列缺口；解析器输入不支持版本、短记录或 0 B；另令首项 `Deleted` 审计提交失败。 | 后续项不执行；每项区分真实返回、受控注入、未覆盖；审计失败时首项可能已消失但不得确认其通知归属。 | 失败即保留未知。受控 journal ID / 游标异常不冒充本机 journal 真被重建；本次禁止为验证而改 journal。真实内核溢出未发生就不声称已测。 |

优先核对 O-01 / O-02 的父通知形状，再核对 O-03。O-04 / O-06 用来检验“末态相同”和“目录映射静态”两个常见错误前提。O-05 不能由一次 append 后立刻 close 代替。

## 3. 现有删除钩子能证明到哪

- `CleanupExecutor.before-delete` 位于文件身份、规则与到期校验之后，删除句柄和祖先租约仍持有；这是动作前屏障，不是新的一轮活动复核。
- `after-delete` 位于 `SetFileInformationByHandle` 成功后、`using` 句柄释放前；它只能证明删除标记成功，不能充当“叶子最后句柄已关闭”。
- `after-result` 位于 `DeleteOne` 返回、执行句柄关闭、逐项 `Deleted` / `Skipped` 审计写入成功之后。外部活动应在首项该钩子注入，使下一项的动态上下文正常检查。
- `VerifiedFileDeletion.afterValidation` 同样在实际句柄关闭前，且 `Action` 不暴露句柄。现有 `DeletionResult` 只有布尔值和原因；从路径重开另一个句柄取得的身份，不能被描述为“对实际删除句柄做的身份桥接”。诊断工具可使用 Core 已向 `CClean.Poc` 暴露的内部句柄读接口，但独立 native 动作不能冒充生产执行器已经具备新协议。
- 祖先 `DirectoryLease` 的共享模式为读 + 写，拒绝共享删除；它阻止相应祖先重命名 / 删除，却允许父目录属性写入，也不锁住整个子树。叶子删除句柄只共享读，文件写入 / 替换被拒绝本身不说明所有父活动均不可发生。

## 4. 同句柄的 128 / 64 位身份桥接

现有 `WindowsFileMetadata` 读取 `FileIdInfo` 的 24 B，`Identity` 为完整缓冲区的十六进制再加创建时间：`Hex(VolumeSerial64 || FileId128) + ':' + CreationTime64`。前 8 B 是卷序号，后 16 B 才是 opaque `FILE_ID_128`；十六进制串沿用内存字节序，不能直接截取为数值 FRN。

建议在同一实际打开句柄、尚未关闭时保存下列独立字段：完整原 `Identity`、`VolumeSerial64`、16 B `FileId128`、创建时间；同句柄的 `FSCTL_READ_FILE_USN_DATA` 返回版本和完整文件 / 父引用。父目录也以固定句柄独立读映射。**该 ioctl 返回的 `TimeStamp`、`Reason`、`SourceInfo` 明确无效；它提供身份桥接，不能提供这次变化的原因或执行者。**

具体拒绝条件与格式风险：

1. USN V2 的文件 / 父引用是 64 位，V3 是 128 位，记录字段位置不同；必须按 `MajorVersion` 和 `RecordLength` 分派，未知版本保留未知。不能把 V3 按 V2 偏移解析。
2. 扩展通知的 `FileId` / `ParentFileId` 使用 `LARGE_INTEGER` 字段；与 V2 比较时保留完整 64 位 bit pattern。不得只取 MFT 序号的低 48 位、丢失序列高位，也不得假定 `FileId128` 高位为零就形成跨文件系统通用规则。64 位 file index 在 ReFS 不保证唯一；本诊断只覆盖实际支持的 NTFS。
3. 若用 `GetFileInformationByHandle` 的 `nFileIndexHigh/Low` 作额外对照，应读同一 held handle，完整拼接 64 位并保存其 32 位卷字段；不能把这个卷字段与 `FILE_ID_INFO` 的 64 位卷字段无条件互换。以同句柄实际桥接的对应关系为证据，不以 hex 长得像作为规范。
4. File ID 不保证跨删除后的时间唯一。当前句柄、创建时间、范围 / 观察代次、卷身份及 journal ID 都应保留；关闭后再次出现同 ID 或同路径时不能仅沿用旧映射。回执仍须绑定批准清单及项序号，不能因数据源多一条身份就转移授权。
5. ID、USN、FILETIME 不经 JavaScript `Number` / 浮点值。以十六进制或精确十进制字符串输出，USN 作为有符号 64 位游标单独处理；返回缓冲区开头的续读游标不是一条 USN_RECORD。
6. 按 `NextEntryOffset`、`RecordLength`、文件名的字节长度和版本头界定记录；UTF-16 字节数必须为偶数，下一记录必须在实际返回字节以内。扩展通知的名字没有强制结尾 NUL，不能靠字符串扫描越过记录。未知 / 0 身份不得补成已知对象。

## 5. 报告边界与后续复核

报告应分别给出 API 可用性、各场景是否真实执行、原始接收与解析完整度、身份桥接成功 / 失败、父通知可解释性及未解决歧义。每组记录有效过滤器、监测根 / 目标父关系、句柄访问和共享模式、动作阶段、journal 前后水位、记录版本、缺口与保留对象；受控注入使用单独标记。

USN 按卷返回，可能包含 fixture 之外的元数据。不要持久化整卷原始名字或建立全机活动索引；保存本次可证明归属的 fixture 记录、缓冲边界和不泄露外部路径的计数。若未知父引用无法排除与 fixture 有关，应明确无法完成范围证明，不能凭丢弃后“只剩自己删除”判通过。不得为补齐映射启动整卷枚举。

“看到 FILE_DELETE 与回执身份相符”“父变化符合删除副作用”“排除了父外部活动”是三个不同结论。前两项不能替代第三项；如果仅完成诊断，R1-003 连续清理出口仍未通过。研究应保留反例，而不是只保留能跑通的三个删除。

本审阅尚无实际 native 运行结果。待主代理报告到位后，逐场景核对：是否确有父=root 对照、长寿命重复原因、瞬时 / 嵌套活动、同句柄桥接、明确未知和审计失败证据；未执行部分保持未覆盖。

## 6. 官方依据（本次已阅读）

- [ReadDirectoryChangesExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesexw)：不报告指定目录自身；0 B 成功返回的含义；当前仅支持 NTFS。
- [FILE_NOTIFY_EXTENDED_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-file_notify_extended_information)：动作、FileId / ParentFileId、UTF-16 名字字节长度与 NextEntryOffset。
- [FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)：卷序号与 128 位文件标识组合。
- [BY_HANDLE_FILE_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information)：64 位 file index、ReFS 限制、文件标识可随时间复用。
- [FSCTL_READ_FILE_USN_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_read_file_usn_data)：同句柄返回 V2 / V3 身份；TimeStamp / Reason / SourceInfo 无效。
- [Change Journal Records](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records)：同一打开期同类原因合并，关闭记录是部分历史的汇总。
- [USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)：完整文件 / 父引用、原因位、版本和记录边界。

## 7. 首轮真实报告复核（追加）

本节只读核对了首轮 [result.json](../../artifacts/r1-003-observation-20261023T095858935-b12745a6d2ae4641988fdcf24e4451e8/result.json)，运行于 UTC 09:58:58.935～09:59:04.624，Windows 10.0.26100 / X64 / SDK 10.0.401，报告为 dirty 工作树。它对应当时二进制，不能据后来修改过的源码倒推当次已包含新增校验。原报告保留，不回填、更改其通过数或字段。

**真实负面证据已经成立，但生产归属出口未通过。** 报告顶层 `passed=9` 表示解析自检加八组脚本动作未抛异常；初版 driver 的 scenario 状态只按动作异常决定，不能解释为九项安全验收通过。初版同时存在 `ScanGaps` / `scanGaps` 仅大小写不同的 JSON 字段，读取时需保留键大小写；主代理正在更正后续输出为不同名称，并区分动作结果、观察完整性与资格验收。

| 实测场景 | 原始报告中的事实 | 可采用的结论 |
| --- | --- | --- |
| executor-self-stop | 首项后返回 `NeedsPreview`，删除计数 1、剩余 2；同阶段 FSW 为样本 `Deleted` + 父 `Changed`，扩展通知为样本 Action 2 + 父 Action 3；V3 journal 记录样本 `FILE_DELETE \| CLOSE`（`0x80000200`）。 | 原有自阻断仍在；子项删除记录没有提供父 `Changed` 的执行者证明。 |
| parent-time / parent-attributes | 直接设置父 LastWriteTime 同样出现父 `Changed` / Action 3，journal 包含 `BASIC_INFO_CHANGE` 及关闭汇总；只改 Hidden 属性时扩展通知与 USN 可见，产品过滤器下没有 FSW 通知。 | 相同父路径 / 类型可由不同操作产生；额外 Attributes 过滤器的覆盖不能算作产品已有能力。 |
| parent-open-handle | 第一次修改在 journal 中出现 `BASIC_INFO_CHANGE`；第二次同句柄修改仍有真实 FSW 父 `Changed` 与扩展 Action 3，但该阶段 `RecordsRead=0`、`IntervalComplete=true`；关闭后才出现 `BASIC_INFO_CHANGE \| CLOSE`（`0x80008000`）。 | 已在本机复现“外部父变化存在，而新的完整读取窗口没有独立原因记录”。不能用 journal 空窗口给父变化做豁免。 |
| file-open-handle | 第一次同长度写入出现 `DATA_OVERWRITE`（`0x1`）；第二次仍有 FSW / 扩展修改通知，但新的完整窗口 `RecordsRead=0`；关闭后出现 `DATA_OVERWRITE \| CLOSE`（`0x80000001`）。 | 已复现同类数据写入的原因合并；flush 不等于结束打开期，关闭汇总也不保留所有操作次序。 |
| transient | 原始事件保留进入范围、修改与删除；阶段结束文件数仍为 3，生产 Revision 从 1 到 3。 | 证明了末态计数不变而原始活动存在；没有冻结扫描或记录所有中间快照，不能证明对象整个生命周期位于两次扫描之间。后续将改名，不继续使用该未证明表述。 |
| nested-move | 扩展通知主要是目录进入 / 离开和父修改；两次移动各有三条匹配 V3 rename / close 记录；进入后快照 4 文件，移出后 3 文件。 | 移入子树不保证逐个子文件通知；本轮移动两步之间刻意采样 250 ms，不是“扫描间移入立即移出”验证。 |
| idle-cancel | 八组场景均 `Armed=true`，退出为 cancelled，原生 gap 列表为空；空闲场景没有注入动作。 | 当前运行下原生 API 能排队并正常取消；它不是 watcher 故障、真实内核溢出或 72 小时连续性验收。 |

同句柄映射仍有一个明确限制：首轮所有 `FSCTL_READ_FILE_USN_DATA` 都返回 V3，128 位身份比较成功，但每组初始九个对象的 `LegacyReferenceBridgeProven=false`；瞬时对象加入后为十个。实际 journal 也返回 V3，因此该流的 128 位匹配记录有效；**不能把它写成已经证实了扩展通知 64 位 ID 与 V2 FRN 的桥接。** 主代理正在补同一 held handle 的显式 V2 请求，后续报告应分别记录 V2 / V3 请求及结果，不回填首轮的未证明标志。对象映射由准备期独立打开句柄取得，现有执行器钩子没有暴露实际删除句柄，因此也不能声称已经生成生产执行句柄的完整原生回执。

本轮 journal 查询和扩展通知在当前进程令牌下成功，报告没有提权操作；这不证明所有普通用户令牌都可用。报告保留 `FixtureCoverageProven=false`、`ProcessAttributionProven=false` 和生产豁免关闭，符合诊断范围。未匹配 / 未知父引用的卷级记录没有被证明位于 fixture 之外，不能把过滤后只剩一个样本删除解释为完整因果证明。

截至首轮报告，以下项目仍未覆盖：

- 父 = watcher 根的平面对照；自删与直接父修改在同一观察窗口的交错。现有独立对照只证明通知形状相同，**没有证明两种原因的完整原始轨迹完全相同**。
- 有受控扫描屏障的瞬时对象；同一扫描间的嵌套移入 / 移出；运行时新增且未在准备期登记的父身份。首轮 staging、nested 及其样本均预先映射，不能验收动态范围归属。
- 删除后审计提交失败、原生通知真实丢失、权限丢失、journal 代次 / 游标失效等 native 链路。解析器的内存坏记录检查及正常取消仅覆盖各自的受控边界；原有单测证据可继续保留，但不冒充本轮 native 失败实验。
- 同路径替换、消费者变化与三项安全连续完成。生产仍第一项后自阻断；本轮没有接入或批准新的事件忽略逻辑。

上述未覆盖项不妨碍采用已观察到的反例，否定“仅凭 journal 没有新原因即可排除外部变化”。后续更正报告状态、精确字段、场景名称并补 V2 映射，仍只能增加诊断证据；不能据此自动关闭 R1-003 或启用父目录活动豁免。

## 8. 末轮报告复核（追加）

已只读核对末轮 [result.json](../../artifacts/r1-003-observation-final-20261023T100323137-d1af2536823247adad03061977d6336f/result.json)，SHA-256 为 `7A1824121E11A5E0CC0A3DA929567A6113DC73BFD3E626D2668286D14D05D339`。八组场景的 73 个初始或补充对象均具有同一 held handle 上显式请求 V2 / V3 的成功身份桥接，`LegacyReferenceBridgeProven`、`Full128BridgeProven` 及两路 `MatchesNativeIdentity` 均为 true；八个扩展通知 worker 均以 cancelled 正常退出，耗时 0～2 ms。**这些句柄由准备期或补充期独立只读打开，并非生产执行器的实际删除句柄；本轮没有新增生产删除回执或事件豁免。** 首轮未证实 V2 的记录继续保留，不回填首轮结论。

末轮再次确认两个关键负面窗口：长寿命父句柄的第二次 `BASIC_INFO_CHANGE`，以及文件同一打开期第二次同长度覆盖，各产生一条真实 FSW 修改通知和一条扩展修改通知，而各自新 journal 窗口仍为 `RecordsRead=0`、`IntervalComplete=true`。因此，补齐 64 / 128 位身份映射仍不能把 journal 空窗口用作排除外部活动的依据。

输出已更正大小写冲突字段，并明确 `evidenceStatus`、`statusMeaning` 与实际过滤器；USN 游标和扩展通知 FILETIME 以精确字符串保留。瞬时对象阶段已改用实际动作名称，不再宣称整个生命周期处于两次扫描之间；执行器阶段区分 `after-plan`、`before-delete` 与执行后。上述修正提升报告可读性和精确度，未补齐第 7 节列出的其他未覆盖实验。顶层动作 / 解析检查成功仍不代表 R1-003 的连续清理资格出口通过；范围完整归属和执行者归属继续保持未证明。

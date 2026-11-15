# R1-001 用途分类与工具能力实施证据

日期：2026-10-23；开发基线 `7d63047fefb10645181c3e69c0479a940bbe50fa`，`codex/p0-implementation`，工作树含未提交实现及原有规划。没有安装、commit/push 或清理现有用户文件。

组件出口：passed。A-01/A-09 的完整页面与用户使用验收仍由 R1-007/010/012 完成。

`ContentExplanation` 将用途、关联依据、证据类型、保留价值、处理影响、重建条件和资格分开。`SpaceSurvey` 保留排除工具/依赖内伪项目的机制，不再把该排除集合当作“临时”分类。原 `ProtectionPolicy.Classify` 的资格判定未改动；说明通过 `Explain` 关联原决策，盘点结果本身不产生执行权限。

验证覆盖混合 `.git/.codex/.cache`、直接扫描工具根、泛 build 祖先下的真实项目、Codex sessions/archived_sessions/历史数据库、Claude LocalCache 混合根/会话磁盘/运行镜像、benchmark 父根/样本/索引/标记/报告、G 盘 target、依赖/日志与未知对象。benchmark 仅说明已知范围与待核验来源，不把名称、大小、历史 dirty 提交或父范围占用当作可释放证明。

工具表分别说明历史安装/目录线索、确认目录观察、会话元数据、进程证据和清理限制，包含八款已知工具与自定义标签。项目/手动会话共用 1–80 字符校验，拒绝空值和控制字符；重启持久化保持兼容。未知工具不能借用 Claude JSONL 解析器。工具下拉、能力说明和时间线筛选已接入；原盘点筛选改按用途，详情展示用途说明，避免旧标签导致空表。

| 验证 | 实际结果 | 证据 |
| --- | --- | --- |
| 开始前既有 MvpP0 基线 | 93/93 通过 | 本次终端运行，未修改业务代码前 |
| 工具/会话/发现定向验证 | 7/7 通过 | `Category=ToolRegistration\|Category=Sessions\|Category=Discovery` |
| 全 solution Release 构建 | 0 警告、0 错误 | `dotnet build CClean.slnx -c Release --nologo` |
| 当前 MvpP0 回归（含并行 R1-003 现状保护测试） | 123/123 通过 | [TRX](../../artifacts/first-release-r1-001/r1-001-regression.trx) |
| 旧任务表保全 | SHA-256 与规划基线一致 | `616e70ff601e231793092ead029ea3d2b486a6a2d062b8f8e577ddfdecb359b8` |

测试均为隔离合成样本或纯路径说明。未读取用户聊天/凭据正文，未进行新的全机扫描。实际 DPI、安装版用户独立理解和完整分类工作台尚未验证，不能据组件通过宣称整组验收通过。当前仍有 R1-003 真实 watcher 自身活动失效问题，单独记录，未降低活动保护。

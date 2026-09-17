# DEV-001 环境与实验边界子计划

状态：已完成。涉及 6 个以上文件和安全边界，按 do-plan 拆分；不创建嵌套子计划。

1. 已完成：核实卷容量，从 Microsoft 官方源下载、验证并安装 .NET 10 x64 SDK。
2. 已完成：创建 Core、PoC、xUnit 工程并锁定真实 SDK。
3. 已完成：实现参数、实验归属、路径与容量守卫、唯一运行报告。
4. 已完成：构建、环境用例和保护边界测试，记录证据并回写父计划。

实验预算默认 256 MiB；安装与 NuGet 独立放置 D 盘，保留至少 10 GiB。不运行真实缓存删除。

自检：Release 构建和 9 项 Environment 测试、6 个 CLI 场景通过；证据见 docs/research/environment.md。

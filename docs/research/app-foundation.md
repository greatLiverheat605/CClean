# DEV-008 WPF 外壳

2026-09-17：复用 Core 创建 .NET 10 WPF，六个导航入口、容量刷新、错误呈现、浅深主题和可见焦点。目录枚举不在主线程执行，清理执行未开放。manifest 使用 asInvoker / PerMonitorV2。

Release 构建 0 警告、0 错误。Foundation 验证仅覆盖只读能力，特权符号链接生成用例不进入普通 Foundation CI。Windows GitHub Actions 检查实际执行数量，零测试不能通过。

通过 Windows `runas /trustlevel:0x20000` 启动受限权限进程；窗口可用且显示真实容量。Computer Use 检查浅色、深色、导航选择与 Tab 焦点。主题初测发现隐式 Window 样式未生效，已显式应用并复验，未以第一次截图作为成功证据。

初始代码提交：`9c45417`。CI 运行：[Windows foundation](https://github.com/greatLiverheat605/CClean/actions/runs/35203175438)，结果通过；耗时 1 分 43 秒。本步骤页面为只读底座，目录发现、索引、会话及清理工作流由后续步骤实现。

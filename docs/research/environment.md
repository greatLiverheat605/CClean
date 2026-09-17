# DEV-001 环境与实验边界

2026-09-17 实测：Windows 11 x64（10.0.26100），NTFS，.NET SDK 10.0.401 / runtime 10.0.12。
SDK 来自 Microsoft `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json` 指定的 win-x64 ZIP，下载后 SHA-512 与官方元数据一致。安装、下载及 NuGet 缓存位于独立 D 盘计划实验根；安装前该卷可用约 58 GiB。

## 复现

在仓库根目录的 PowerShell 中执行：

```powershell
. ./tools/Enter-Dev.ps1
dotnet --info
./tools/Test-Environment.ps1
```

`Enter-Dev.ps1 -LabRoot <路径>` 可选择已安装锁定 SDK 的实验根，只影响当前进程环境。首次 SDK 启动自动生成了 .NET 开发证书，没有执行信任证书操作。

Release 构建 0 警告、0 错误。Environment 分类实际执行 9 项测试，全部通过、无跳过；6 个 CLI 正负场景均返回预期退出码。覆盖普通样本、未知 case、未授权根、路径越界、超预算和零文件请求；单元测试另覆盖已有普通目录、目录符号链接、祖先重命名锁、容量溢出与独立运行目录。

实验默认总预算 256 MiB，至少保留 10 GiB；重复运行保留的 fixture 计入同根总预算。大实验要求非系统 NTFS。报告只写入仓库 artifacts 下的唯一运行目录，保留原始证据，不覆盖前次结果。普通环境用例生成 1 个 4096 字节文件，独立枚举验证实际大小。

退出码：0 通过，1 实现/验证失败，2 能力/环境/参数不满足，3 路径/归属保护拒绝。未实现 case 明确失败，不返回空成功。JSON 记录 SDK、OS、架构、源码提交、脏工作区标志、样本、起止时间、进程资源峰值、检查项与退出码。

本步骤没有产品 UI、真实缓存删除或删除安全结论。实验目录句柄锁不替代 DEV-004 的逐文件授权与同句柄删除验证。原始报告、SDK、下载、测试 fixture 不进入 Git。

# DEV-002 文件身份与空间计量

2026-09-17，SDK 10.0.401，Windows 11 x64 / NTFS。Release 构建无警告；FileMetadata 5 项、Environment 9 项测试均通过。

```powershell
. ./tools/Enter-Dev.ps1
dotnet test tests/CClean.Tests -c Release --filter Category=FileMetadata
dotnet run --project tools/CClean.Poc -c Release -- --case metadata --sandbox artifacts/m0/metadata-complete --output artifacts/m0/metadata.json
```

metadata PoC 的 12 个检查全部通过：稳定身份、硬链接去重、ADS 排除、文件符号链接、链接祖先拒绝、junction 拒绝、稀疏/压缩分配信息、中文长路径、ACL 拒绝、卷容量和缺失对象未知状态。符号链接是单独标注的权限相关场景；本机成功不代表所有普通账户都能创建符号链接，普通账户端到端验收仍在 DEV-019。

身份来自句柄 FileIdInfo（卷序号 + 128 位 File ID），逻辑量与分配量来自 FileStandardInfo；用户可用量和卷总剩余分别来自 GetDiskFreeSpaceEx。硬链接按身份只计一次；读取失败返回空数值，统计同时返回未知对象数。ADS 默认流的已知字节不代表所有流的完整占用，统计会保留不完整标记。

清理资格保守排除多硬链接、reparse、稀疏、压缩、offline、加密、目录和流枚举失败/命名流；祖先链接被拒绝，长路径转为 Win32 扩展路径打开。未实现产品删除能力。

ACL 调试：仅拒绝 ReadAttributes / Read 时，本机仍允许属性查询，前两次原始报告失败且保留。第三次使用明确拒绝 FullControl 的合成文件，读取返回未知，随后恢复原 DACL；没有改变真实用户文件权限。最终完整运行 ID：`20260917T083911265-55ebd70938a847e4a386d58f3746d4f9`，12 passed / 0 failed / 0 skipped。

结论：普通 NTFS 文件身份基线通过；DEV-004 仍必须独立验证删除竞态，不能由本报告推导 G2 通过。

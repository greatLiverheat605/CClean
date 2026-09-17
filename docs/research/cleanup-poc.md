# DEV-004 同句柄删除与竞态

2026-09-17 / Windows 11 x64 / NTFS / SDK 10.0.401。

```powershell
. ./tools/Enter-Dev.ps1
dotnet test tests/CClean.Tests -c Release --filter Category=DeletionSafety
dotnet run --project tools/CClean.Poc -c Release -- --case deletion-safety --sandbox artifacts/m0/deletion-sandbox --output artifacts/m0/deletion-safety.json
```

5 项 DeletionSafety 测试通过；与 FileMetadata 联合回归 10 项通过。最终 PoC 运行 `20260917T084716381-cc7bc335a29842c4970c6f6bdaa12122`：30 轮、30 个普通批准对象成功删除、150 个保护对象内容 SHA-256 保持一致、120 次验证后的替换/重命名/ADS/写入尝试被 Windows 拒绝，清单外删除为 0。

实现仅接受本次 ExperimentSandbox.RunDirectory 内的明确 FileSnapshot。祖先目录持有不共享删除的句柄；叶子以 DELETE + READ_ATTRIBUTES、仅 FILE_SHARE_READ 打开，拒绝共享写入/删除冲突。最终路径、卷/File ID、大小、变更时间、链接数、属性及全部可枚举流在同一身份句柄复核，再以 FileDispositionInfo 设置删除标记。

测试覆盖预览后叶子替换、新对象、新 ADS、文件占用、相似路径前缀、验证后叶子/祖先改名、验证后 ADS 与覆盖写。同步点直接控制交错，不用 sleep 猜竞态。Metadata PoC 独立覆盖 junction、符号链接、硬链接及特殊属性排除。

结论：G2 在上述受控普通 NTFS 文件范围通过。删除标记不等于物理空间已经释放；产品审计、活动保护、规则批准和用户确认仍未实现，PoC 不能作为真实缓存删除入口。全部动作限于生成的合成实验对象。

# DEV-003 监听与收敛

2026-09-17 / Windows 11 x64 / NTFS / SDK 10.0.401。

```powershell
. ./tools/Enter-Dev.ps1
dotnet test tests/CClean.Tests -c Release --filter Category=Monitoring
dotnet run --project tools/CClean.Poc -c Release -- --case watcher --sandbox artifacts/m0/watcher-sandbox --output artifacts/m0/watcher.json
dotnet run --project tools/CClean.Poc -c Release -- --case watcher-overflow --sandbox artifacts/m0/watcher-sandbox --output artifacts/m0/watcher-overflow.json
```

监听先启动，再扫描；事件仅作为有界提示，不推断写字节或进程。队列压力和原生缓冲溢出累计保留，补扫不抹去历史缺口；快照字典由单个 worker 替换。扫描限制文件和目录规模，跳过链接，未知元数据保持未知。

普通集合最终 101 文件，路径与大小对独立 Directory.GetFiles/FileInfo 真值 100% 一致，静默后 118 ms 收敛。覆盖创建、追加、覆盖、截断、重命名、子目录移动和删除。Monitoring 回归测试 1 项通过。

真实溢出实验阻塞首次通知回调，让真实文件操作填满 4096 字节原生缓冲；没有注入 Error 或伪造 InternalBufferOverflowException。最终 12,001 文件；实际观测原生溢出 1 次、应用队列压力 5,863 次；静默后 6,372 ms 完成 100% 真值收敛，缺口计数仍保留。进程峰值工作集 71,086,080 字节；这是含 fixture 生成的 PoC 进程数据，不是产品空闲性能。

运行 ID：`20260917T084201896-17f45b468b5d43b587f377aa0021b71b`，2 passed / 0 failed / 0 skipped。

G1 在该受控规模通过；12,001 文件不替代 DEV-019 的 100,000 文件 / 20 GiB 产品压力目标。无界事件历史与精确进程归因均未实现；缺口前的历史不可由补扫恢复。

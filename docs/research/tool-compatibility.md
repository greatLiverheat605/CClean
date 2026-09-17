# DEV-005 工具兼容与外部缓存

2026-09-17 实测。Codex CLI 0.153.2，Node 24.20.0，npm 11.19.0。Claude Code / ZCode 未在 PATH 检出版本，不代表系统未安装；目前三工具均仅承诺 A 级通用目录登记和手动会话，B/C 级自动发现与进程证据尚未验证。

未读取三工具的聊天、凭据、内部数据库；它们的数据目录均不作为清理规则。

## 外部规则

`npm-exclusive-content` v1 仅支持 npm 11.19.0、用户明确配置且独占的缓存根。具体白名单为 `_cacache/content-v2` 与 `_cacache/index-v5` 中预览列出的普通文件；未知布局、特殊对象、共享/默认缓存、未知版本、未启用规则、活跃或不可观察的消费者、保护项目范围一律排除。产品必须显示独立缓存这一前提，不修改用户 npm 配置。

重新下载可能有网络成本，离线重装可能失败。内容与索引要一起预览；仅删除内容会造成索引指向缺失对象，第一次真实实验已因此报 ENOENT，失败报告保留。修正后逐文件删除内容和索引，第二次真实 npm 重新生成缓存并安装包成功。

## 复现与结果

```powershell
. ./tools/Enter-Dev.ps1
dotnet test tests/CClean.Tests -c Release --filter Category=CacheCompatibility
dotnet run --project tools/CClean.Poc -c Release -- --case cache-rule --sandbox artifacts/m0/cache-sandbox --output artifacts/m0/cache-rule.json
```

兼容保护测试 1 项通过（含版本、共享、活动、未知、禁用、保护范围与特殊对象断言）。真实工具在新缓存生成 `is-number@7.0.0`；按 DEV-004 句柄方案删除批准对象；再次 npm cache add 后内容 SHA-256 相同；npm install 成功生成可用依赖文件。预览后新增对象未被删除。运行 `20260917T085034453-96a6933ac74247e39a0459b6ca884829` 通过。

独占证据来自 PoC 新建、只传给已结束子进程的专用路径；这不证明任意用户目录独占。产品活动观察与执行时复核由 DEV-011/014/016 实现。原生 npm cache clean 未作为执行器；所有删除按预览逐文件完成。npm 使用固定已安装 node/npm 路径、独立空 user/global 配置、禁用安装脚本，避免读取用户凭据和运行包脚本。

G3 在上述显式专用缓存前提下通过；不宣称三款 Agent 默认数据目录可一键清理。

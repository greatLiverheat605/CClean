# DEV-002 文件身份与空间子计划

状态：已完成。涉及 Win32 句柄和 4 个以上文件。

1. 已完成：实现句柄身份、分配量、链接/流/特殊属性和卷容量读取。
2. 已完成：沙箱内普通、硬链接、junction、symlink、ADS、稀疏、压缩、中文长路径与 ACL 用例。
3. 已完成：运行 FileMetadata 测试与 metadata PoC；将特权场景和普通基线分别记录。

证据：docs/research/storage-poc.md。

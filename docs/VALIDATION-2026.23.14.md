# TreeGit 2026.23.14 验证记录

源码提交：`901be6196525c131d912ca4becd5cbc924cb6908`。

本版修改“描述”列提交引用中的 Git 标签图标颜色。

| 主题 | 标签图标颜色 |
| --- | --- |
| 跟随系统（系统浅色或深色） | `#16836D` |
| Light | `#16836D` |
| Dark | `#16836D` |
| Blue | 原有 `#0747A6` |

资源核对确认 Light/Dark 只改标签图标颜色，Blue 的主题字典保持原样。绘制仍仅对 Git 标签使用专用图标画刷。

[同源云端 CI](https://github.com/JonSun2020/TreeGit/actions/runs/42152986296) 的 portable、cancellation、updater-ui 三个作业均通过，包含原有便携包、Git、MCP、二进制对比和更新回归流程。

已核对 ZIP 完整性、程序版本和源码提交，以及 381 个配套 Git 文件的 SHA-256。包内 data/ 为空，不包含本机个人配置。

- ZIP SHA-256：`60accec488e9d359328e97f218227aadb27cb89a489ff4d59854ef94ae2558d6`。
- EXE SHA-256：`328b16eb6b0d77eac5108ef75253b8e08ad37b737ea0e95ea6fc63acc41e35a7`。

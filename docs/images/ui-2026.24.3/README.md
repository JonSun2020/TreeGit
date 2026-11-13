# TreeGit 2026.24.3 界面截图

这些 PNG 均由 Windows 上的正式便携程序真实渲染，未经图像修改。拍摄只使用隔离示例仓库、虚拟作者、示例代码和生成的二进制文件；上传前逐张检查可见内容与 PNG 元数据，不包含业务仓库、真实账号、个人路径或凭据。

| 图片 | 内容 | 来源 |
| --- | --- | --- |
| `theme-light.png`、`theme-dark.png`、`theme-blue.png` | 已确认的三套主题及主／从分支、变更文件、数字角标的选中色 | [正式包截图](https://github.com/JonSun2020/TreeGit/actions/runs/43435595894) |
| `commit-branch-highlights.png` | 选中“调整示例参数”，本地 main 深色、origin/main 浅色、无关分支不高亮 | 同上 |
| `containing-branches.png` | 搜索结果的包含分支数量和侧栏联动 | 同上 |
| `remote-selected-blue.png` | 未合入的远程提交选中后使用完整白字，侧栏远程分支高亮 | 同上 |
| `binary-diff.png` | 内置二进制左右对比，无需外部工具 | [正式包检查](https://github.com/JonSun2020/TreeGit/actions/runs/43431194729) |
| `merge-unresolved.png`、`merge-resolved.png` | 三方合并的未解决与解决后界面 | 同上 |

正式包来自产品源码 `c7c097bd87e2e6f45ab208ecbe77ac4303fde47a`。截图作业直接下载并校验同次 CI 的正常便携包，未重新编译，未启用集成测试接口。单独的拍摄脚本仅调整窗口以适合 CI 桌面，其 `src` 与产品提交一致。完整检查见[验收记录](../../VALIDATION-2026.24.3.md)。

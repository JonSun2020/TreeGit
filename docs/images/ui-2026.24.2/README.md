# TreeGit 2026.24.2 界面截图

这些 PNG 是 TreeGit 实际渲染的界面，使用隔离测试仓库、虚拟作者与示例文件；不包含用户业务数据、真实账号、个人路径或凭据。保留原始截图像素，上传前逐张检查可见内容与 PNG 元数据。

| 图片 | 内容 | 来源 |
| --- | --- | --- |
| `theme-light.png`、`theme-dark.png`、`theme-blue.png` | 三种主题及连续选择提交信息 | [Windows 原生界面检查](https://github.com/JonSun2020/TreeGit/actions/runs/43358248610) |
| `commit-branch-highlights.png` | 历史提交选中后，本地跟踪分支深色、远程分支浅色 | [同源隔离桌面验证](https://github.com/JonSun2020/TreeGit/actions/runs/43362199801) |
| `containing-branches.png` | 搜索、包含分支数量和左栏联动 | [正式便携包检查](https://github.com/JonSun2020/TreeGit/actions/runs/43361132858) |
| `binary-diff.png` | 内置二进制左右对比 | [正式便携包检查](https://github.com/JonSun2020/TreeGit/actions/runs/43361132858) |
| `merge-unresolved.png`、`merge-resolved.png` | 三方合并的未解决及解决后示例 | [正式便携包检查](https://github.com/JonSun2020/TreeGit/actions/runs/43361132858) |

主题截图来自预览源码 `9d4fac43263750ca6497ebdd6ec47f7104441236`；其产品源码 `src` 与正式发布提交 `9213682b17438653304ba1537251bb2cbfb4e6a6` 逐文件一致。其余 Windows 截图由正式发布提交生成的便携程序捕获。版本和检查范围见[验收记录](../../VALIDATION-2026.24.2.md)。

`commit-branch-highlights.png` 来自同一正式发布源码在 Linux 隔离桌面的真实鼠标单击验证；窗口外观由该平台渲染，展示的是与 Windows 相同的提交选择和分支覆盖联动逻辑。其余非主题截图来自 Windows 正式便携程序。

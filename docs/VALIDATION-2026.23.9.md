# TreeGit 2026.23.9 验收记录

本次范围为主题选项、旧配置兼容和深色顶栏，不改动主界面布局或 Git 操作实现。

使用正常发布程序（未启用集成测试入口）实际打开临时 Git 仓库，完成 30 项主题检查：

- 首次启动默认 Light；偏好设置主题列表仅有 Light、Dark、Blue。
- 在同一窗口反复切换 Light、Dark、Blue，核对实际截图的顶栏像素与白色菜单文字。Light 为 `#16836D`，Dark 为 `#1F1F1F`，Blue 为 `#0747A6`。
- 逐一查看三种主题的外观设置窗口，核对当前主题及深浅背景。
- Dark 保存后重启仍为 Dark，顶栏保持深灰色。
- 旧配置 `Default` 启动后转为 Light，保存后也是 Light；旧版蓝色主题名称 继续兼容并保存为 Blue。

MCP 协议检查 18 项通过。源码 [CI 41977837077](https://github.com/JonSun2020/TreeGit/actions/runs/41977837077) 的 portable 和 cancellation 两个作业通过，包括便携构建、Git 自动发现、内置 Git、无已安装 Git 的 GUI 启动和进程取消检查。

发布包 ZIP 完整性、程序及说明文件一致性、内置 Git 清单中 380 个文件的 SHA256 均已核对。包内不含测试仓库、个人配置或凭据。本次未重新执行全部 Git 业务场景；之前的功能验收见 [2026.23.6 记录](VALIDATION-2026.23.6.md)。

- 版本：`2026.23.9`
- 程序源提交：`d2ed3544b3945446eccd0ea73ea136ea73b01d53`
- ZIP：`TreeGit-2026.23.9-Windows-x64.zip`
- ZIP 大小：69,891,856 字节；解压文件合计 150,600,619 字节。
- ZIP SHA256：`c7132f1ae9e568f0739091df246b769b8794c778110c23f222813847030a92b7`
- EXE SHA256：`b722762f7157167af0202a4ea918aedec43c55f1204297c7c1f43da8b33b6e2f`

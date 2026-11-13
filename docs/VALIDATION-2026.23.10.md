# TreeGit 2026.23.10 验收记录

本次恢复系统主题自动适配，界面显示为“跟随系统”；保留 Light 默认值、已有选择和 2026.23.9 的 Dark 深灰色顶栏。

正常发布程序实际打开临时 Git 仓库，完成 42 项主题检查：

- 主题列表包含跟随系统、Light、Dark、Blue；已查看中文外观设置的实际截图，跟随系统不再显示含义不明的 Default。
- 初次启动仍为 Light；在同一窗口反复切换四种主题，核对实际顶栏像素、白色菜单文字及 MCP 返回的实际主题。
- 本机 Windows 应用主题当前为浅色，选择跟随系统时实际为 Light，顶栏 `#16836D`。单独选择 Dark 时实际为 Dark，顶栏 `#1F1F1F`；Blue 为 `#0747A6`。
- 跟随系统保存为兼容标识 `Default`，重启后仍为 Default 并正确适配当前系统；Dark、Blue 及旧版蓝色主题名称 的保存、重启兼容检查通过。

系统自动适配使用 Avalonia 的 `ThemeVariant.Default`，由框架响应系统主题变化。此次未修改用户的 Windows 全局主题；系统浅色与固定 Dark 的实际渲染均已核对。

MCP 协议检查 18 项通过。[源码 CI 41982682486](https://github.com/JonSun2020/TreeGit/actions/runs/41982682486) 的 portable 和 cancellation 两个作业通过，涵盖便携构建、Git 发现、内置 Git、无已安装 Git 的 GUI 启动和进程取消检查。

ZIP 完整性、程序及说明文件一致性、内置 Git 清单中 380 个文件的 SHA256 均已核对。包内不含测试仓库、个人配置或凭据。未重新执行全部 Git 业务场景；之前的功能验收见 [2026.23.6 记录](VALIDATION-2026.23.6.md)。

- 版本：`2026.23.10`
- 程序源提交：`06d83233c106a4411056cfd95cacdbf65e6adeb4`
- ZIP：`TreeGit-2026.23.10-Windows-x64.zip`
- ZIP 大小：69,891,950 字节；解压文件合计 150,601,238 字节。
- ZIP SHA256：`adcc7820f869cac8aaffca42cd5d730af2aa06bd83b2cff1dab392b209557da6`
- EXE SHA256：`5b20294d0780c96352d7c3a9254745eaec1a7e19a67c07324902f5ea133800c1`

# TreeGit 2026.23.10

- 恢复主题的系统自动适配选项，并将含义不明确的 `Default` 显示为“跟随系统”。系统浅色时显示 Light，系统深色时显示 Dark，并随系统切换。
- Light、Dark、Blue 仍可单独选择；保留已修好的 Dark 深灰色顶栏和已确认的 Light 深绿色顶栏。初次启动仍默认为 Light，升级保留现有选择。
- 配置和 MCP 中继续使用兼容的 `Default` 标识；MCP `ui_window` 额外返回实际渲染的 `effectiveTheme`，便于 AI 判断系统自动适配后的结果。

完整解压 `TreeGit-2026.23.10-Windows-x64.zip` 后运行 `TreeGit/TreeGit.exe`。升级前退出程序，并保留个人 `data/`。程序包已包含 Git 组件。

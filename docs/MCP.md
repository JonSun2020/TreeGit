# MCP：让 AI 控制 TreeGit

TreeGit 是一个 **MCP 服务端**。AI 客户端通过 stdio 连接，再由本机通信通道控制实际运行的 TreeGit 窗口。未启动界面时会自动启动。

在 **工具 → MCP · AI 连接** 复制配置。也可手工填写：

```json
{
  "mcpServers": {
    "treegit": {
      "command": "D:\\Tools\\TreeGit\\TreeGit.exe",
      "args": ["--mcp"]
    }
  }
}
```

请把 `command` 替换为自己的绝对路径；目录移动后需更新。连接默认完整读写，兼容旧参数 `--allow-write`。需要只读连接时使用 `["--mcp", "--read-only"]`；只读连接保留界面导航和查询，隐藏并拒绝 Git 写入工具。

## 能力

| 工具 | 用途 |
| --- | --- |
| `ui_state` / `ui_screenshot` | 查看真实窗口、仓库标签、分支、图谱及弹窗状态；获取窗口图片 |
| `ui_open_repository` | 打开或切换已有仓库 |
| `ui_select_view` / `ui_select_commit` | 切换历史、工作副本、贮藏及提交详情 |
| `ui_working_copy` | 选中图谱顶部“未提交的更改”，下方显示暂存/未暂存文件与差异 |
| `ui_search` | 打开搜索页，按提交信息、作者、SHA、路径或内容搜索并选择结果 |
| `ui_tabs` | 列出标签及加载状态、打开仓库下拉列表、选择、移动或关闭标签 |
| `ui_graph` | 指定最左侧分支、显示当前分支/所有分支，以及对应远端分支 |
| `ui_history_columns` | 读取、调整、重置图谱、描述、日期、作者、提交等列宽；按仓库保存 |
| `ui_refresh` / `ui_window` | 刷新、调整窗口尺寸、主题和侧栏 |
| `ui_fonts` | 列出本机可选字体、设置界面或代码 / Diff 字体、恢复默认 |
| `ui_update` | 检查更新、查询进度、下载、取消，以及准备好后的重启更新 |
| `ui_command` | 打开关于、偏好、仓库设置、账号及原有 Git 操作对话框 |
| `git_status` / `git_log` / `git_diff` / `git_refs` / `git_show` | 查询状态、历史、差异和引用 |
| `git_clone` | 使用原有克隆流程；完成后打开仓库 |
| `git_action` | 暂存、提交、推拉、检出、合并、变基、贮藏和冲突恢复等常用操作 |
| `git_run` | 传入 Git 参数数组，执行全部 Git 子命令，包括强制推送、硬重置、清理以及删除分支/标签 |

例如调用 `git_run`：

```json
{
  "repo": "D:\\Projects\\example",
  "arguments": ["commit", "-m", "修复串口超时"]
}
```

`repo` 必须先通过 `ui_open_repository` 打开。参数直接传给 Git，不经过系统 shell；已有 Git hooks、过滤器和自定义 Git 配置仍按 Git 本身的行为执行。长输出有大小限制，并返回 `truncated` 标志。

修改完成后界面自动刷新，命令记录进入 TreeGit 日志。认证使用软件现有的仓库账号和正常输入框；服务端不提供读取已保存密码的工具。多个 AI 连接的 Git 写入操作在 GUI 端依次执行。

界面刷新异步完成，`ui_refresh` 表示已发起刷新。外部 Git 或文件操作之后，应等待对应文件、版本和比较结果更新再读取界面；需要直接核对 Git 数据时可调用 `git_status`、`git_diff` 等查询工具。二进制比较的 `ready` 状态包含首个差异定位完成。

MCP 的完整读写意味着 AI 可以修改甚至删除 Git 数据。应在 AI 客户端按自己的习惯设置工具审批；TreeGit 不额外限制 `git_run` 的子命令。

`ui_diff`：选中暂存区、工作区或当前提交中的变更文件，并读取二进制对比状态、两侧最多 256 字节可见内容，执行 first / previous / next / last / goto（offset 为绝对字节偏移）。例如：`{"repo":"D:\\repo","action":"select","source":"unstaged","file":"firmware.bin"}`。历史文件先用 ui_select_commit 选中提交，再指定 source=history。
`ui_command` 还支持 check_updates、external_diff（需要先选择变更文件），与界面对应按钮使用同一处理流程。

`ui_update` 的 `action` 为 `check` / `state` / `download` / `cancel` / `apply`。先 `check` 打开更新窗口，再通过 `state` 读取状态与进度；准备好后才可 `apply`。应用更新会重启 TreeGit、断开 AI 连接，客户端在更新完成后重新连接即可。Git 命令执行中不会开始替换文件；替换和新版启动期间暂停其他 MCP 操作。此工具属于写入工具，只读连接不提供。

`ui_stashes`：查看、展开 / 收起、筛选和选择侧栏的贮藏条目。`ui_diff` 的 source=stash 可选择当前贮藏中的变更文件。ui_command 支持 stash_apply_dialog / stash_drop_dialog / stash_branch_dialog，仅打开原有确认窗口，不自动执行操作。

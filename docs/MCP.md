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
| `ui_working_copy` | 打开工作区概览并筛选文件、编辑提交草稿；有更改时显示图谱顶部“未提交的更改”，不自动提交 |
| `ui_search` | 打开搜索页，按提交信息、作者、SHA、路径或内容搜索并选择结果 |
| `ui_tabs` | 列出标签及加载状态、打开仓库下拉列表、选择、移动或关闭标签 |
| `ui_graph` | 显示当前分支/所有分支及对应远端；所有分支模式可跳到分支头，图谱主线始终跟随当前检出分支 |
| `ui_history_columns` | 读取、调整、重置图谱、描述、日期、作者、提交等列宽；按仓库保存 |
| `ui_merge_result` | 读取、编辑和解决已打开的冲突结果，撤销/重做，显式保存并暂存；不自动提交 |
| `ui_layout` | 读取本软件窗口的分栏和滚动区域，拖动分隔条、恢复分栏尺寸、滚动或调整窗口大小 |
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

二进制对比读取失败时，可调用 `ui_diff` 的 `retry` 动作在原视图重新读取；随后查询 `state` 等待完成。界面显示简明提示，MCP 状态另提供错误信息用于诊断。

搜索结果导航：`ui_search` 选中结果后传入 `"open": true`，等同双击，退出搜索并自动切换“所有分支 + 显示远程分支”，定位并选中提交。

搜索结果的“包含分支”统计全部已知本地分支和远程跟踪分支，不受图谱显示范围限制；远程信息以最近一次获取为准。本地与对应远程分别统计。`ui_state.searchResults` 中的 `branch` 是摘要名称，`branchCount` 是数量，`branches` 是完整引用数组。它们表示历史包含关系，不表示提交创建时的分支，也不自动判定改动当前是否生效。

先用 `ui_search` 的 `select` 选中结果，再传 `"showBranches": true` 打开分组列表；`branchFilter` 筛选该列表，空字符串清除筛选。返回的 `branchPopup` 包含当前筛选、总数、分组和分支行；`ui_screenshot` 的 `window=active` 可读取此展开面板。

传入 `"open": true, "branch": "refs/heads/example"` 可定位原提交并选中指定的包含分支；远程使用 `refs/remotes/origin/example`。指定引用必须确实包含该结果提交，否则拒绝请求。此操作不检出分支；若分支在搜索后发生变化，仍保留原提交定位，并提示重新搜索。

`ui_layout` 先使用 `action=list` 取得当前窗口的分隔条和滚动区域索引，再使用 `drag`、`restore`、`resize` 或 `scroll`。`window` 可选 `main` 或 `active`；`drag` 使用 `splitter`、`dx` / `dy`，`scroll` 使用 `scroller`、`dx` / `dy`。索引随页面改变，切页后应重新查询。`twoColumnHistory` 和 `twoColumnDiff` 切换历史、文本差异的左右布局。这些操作只作用于本 TreeGit 进程的窗口，不修改 Git 数据。

`ui_window` 的 `zoom` 支持 0.75 至 2.5。`ui_working_copy` 的可选 `filter` 按文件路径筛选；空字符串清除。`ui_state` 返回搜索结果的 `branch`、工作区操作状态和当前提交的已加载变更文件，读取异步视图前可据此确认内容就绪。

`ui_command` 还可打开 SSH 密钥管理（`ssh_keys`）、文件 / 目录历史（`file_history` / `directory_history`，指定相对路径 `file`）、分支 / 提交比较（`compare_branches` / `compare_commits`，指定 `base` 和 `to`）、子模块版本比较（`submodule_compare`，先选中可比较的子模块变更），以及命令日志（`git_logs`）。`interactive_rebase` 仅打开配置页面，`commit_info` / `commit_changes` / `commit_files` 切换当前提交详情页。这些命令不自动提交 Git 操作。

当前开发分支的冲突编辑入口：先用 `ui_command` 的 `merge_conflicts` 打开指定文件，再调用 `ui_merge_result`。`read` 返回编辑内容、未解决数量和保存状态；`replace` 按字符偏移修改结果；`resolve` 指定原始冲突的 `region` 和 `choice`；`undo` / `redo` 与结果编辑器共用历史，`save` 保存并暂存，成功后关闭窗口。外部文件或冲突状态已变化时会拒绝覆盖，编辑内容仍留在窗口中。

`choice=left` / `right`、`left_then_right` / `right_then_left` 对应界面中的 Local / Remote 和排列顺序。`ours` / `theirs`、`ours_then_theirs` / `theirs_then_ours` 保留 Git 原生两侧的含义，变基时与显示左右相反；`restore` 恢复原始冲突块。AI 按界面操作时优先使用 `left` / `right`。`ui_state` 的冲突窗口同时给出是否正在变基、左右内容摘要和固定的冲突总数。

`ui_refresh` 发起后，可等待仓库的 `workingCopyRefreshVersion` 增加且 `workingCopyStatusKnown=true`，再读取文件列表。`pendingDialogAttemptVersion` 在确认操作被界面受理时增加，包括表单验证未通过的情况；先观察该值变化，再等待 `pendingDialogInProgress=false` 或弹框关闭，避免把点击前的空闲状态当成执行结束。

合并、变基、挑选提交和回退提交未完成时，`pendingDialogError` 返回当前弹框的恢复提示。弹框保持打开时先处理该提示，不能仅凭确认动作已受理判断操作成功。

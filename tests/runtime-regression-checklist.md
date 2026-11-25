# TreeGit 运行效率与整机回归

测试前记录提交号、未提交源码差异的 SHA-256、程序集或便携包的 SHA-256，以及 Git 版本。旧包可作为对照。性能对照使用同一台机器、同一份演示数据、相同构建模式和相同的运行顺序。分别记录冷启动、预热后的操作和空闲数据。

| 检查范围 | 操作及核验 | 现有入口 |
| --- | --- | --- |
| 启动和 Git 配置 | 检查系统 Git、内置 Git、无效路径、运行中丢失、更新关闭和离线启动。核对就绪时的文案、版本和实际使用的 Git。记录启动耗时及 `git --version` 次数。 | `git_runtime.py`、`startup_update.py`、`branch_refresh_performance.py` |
| 工作区和多仓库 | 打开、恢复、切换、移动、关闭和重开标签，覆盖标签溢出及加载期间关闭。核对仓库名称、选中页、滚动位置和草稿。 | `desktop_soak.py`、`deferred_loading.py`、`tab_lifecycle.py` |
| 历史、分支和搜索 | 检查全部与当前分支图谱、远程分支、分支覆盖、上游变化、标签变化、搜索并打开结果。连续刷新时核对查询次数、最终提交 SHA、侧栏、详情和搜索结果。 | `branch_refresh_performance.py`、`desktop_soak.py`、`search_interaction.py`、`query_failure.py` |
| 暂存、取消暂存与提交 | 覆盖两边无文件、列表有文件但未选中、选中单项和多项。用 Git 核对索引和最终提交。检查空提交、钩子拒绝、署名、立即推送和失败后草稿。 | `commit_page.py`、`amend_unstage.py`、`commit_failures.py`、`push_hook_failure.py` |
| 文本、二进制和三方合并 | 覆盖内嵌和独立窗口、切换文件、关闭、冲突编辑及撤销。核对实际差异字节、冲突索引和临时目录清理。 | `desktop_soak.py`、`localization_ui.py`、`merge_preview_readonly.py`、`external_tools.py` |
| 设置、缩放和本地化 | 检查设置保存及取消、各语言下拉选项与收起文字、复选框主题、窗口尺寸和分栏拖动。 | `settings_persistence.py`、`dropdown_audit.py`、`localization_ui.py`、`layout_audit.py` |
| Git 进程与失败恢复 | 覆盖大量输出、静默失败、命令启动失败、取消和远程操作。验证只读请求不会修改 refs、索引、草稿或工作文件，写入命令不会自动重放。 | `command_pipes.py`、`remote_query_cancellation.py`、`interaction_safety.py` |
| 长时资源 | 同时记录耗时、CPU、私有内存、工作集、句柄类型、Git 子进程和临时数据。在操作后立即、等长空闲后、关闭全部标签后采样。检查磁盘句柄、已退出线程句柄、子进程和快照是否持续累积。 | `desktop_soak.py`、`handle_lifetime.py` |
| 便携包和尺寸 | 分别核对 SystemGit、BundledGit 的压缩及解压体积、校验值、正式启动和实际配置。确认更新和回退路径，核对正式入口指向的版本。 | `package_smoke.py`、`updater_end_to_end.py`、`build/publish-treegit.py` |

- 串行复用一份隔离程序。构建和测试前检查系统盘、工作盘及内存余量，记录测试进程峰值和临时占用。退出后核对进程、测试副本、独立配置和 Git 临时仓库均已清理。
- 保留每个测试的实际完成量、失败字段和关键状态。修复后先复测失败场景，再用同一构建跑整轮。缺少报告、未跑到目标循环数或因资源中止的项标为未完成。
- Windows 长测设置 `TREEGIT_SOAK_CLOSE_TRACE=1`。关闭最后一个仓库页后，检查是否还有历史、差异或工作区查询启动。欢迎页状态列的查询单独记录，并检查子进程最终退出。
- 便携包的验证单独记录包的校验值和结果。集成构建的结果附在相同源码的回归记录中。

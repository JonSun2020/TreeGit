# 跟随 SourceGit 更新

TreeGit 的目标是保持 SourceGit 的 Git 工作流，持续维护独立的界面、品牌、便携构建、仓库凭据和 MCP 控制层。它与 Atlassian SourceTree 没有隶属关系，不包含 SourceTree 的二进制文件或图像资产。

当前上游基线：

- 仓库：https://github.com/sourcegit-scm/sourcegit
- 分支：`master`
- 版本：`2026.23`
- 提交：`c0f40a2e78d81a7474628847727644fc0c56f558`
- AvaloniaEdit 基线：`ddf00a91dc0da29b85c79b85c9583860213a99ff`

原始提交历史和 `upstream` 远端可以用于后续合并。`VERSION` 保留 SourceGit 基线版本；TreeGit 发布使用 `treegit-v...` 标签，避免混淆双方版本。实际已合并的上游提交应记录在本文件，而不能只修改版本号。

## 每次更新

1. 保持工作区干净，为当前可运行版本创建检查点/标签。
2. 在自己的克隆中添加并更新远端：

   ```powershell
   git remote add upstream https://github.com/sourcegit-scm/sourcegit.git
   git fetch upstream master --tags
   git switch -c sync/sourcegit-YYYYMMDD
   git merge --no-ff upstream/master
   ```

   已存在 `upstream` 时跳过添加。冲突未解决时先修复并编译；不要用上游整目录覆盖 TreeGit。

3. 重点审查：`src/Automation/`、Git 命令进程与认证、`Models/CommitGraph*`、主窗口/侧栏/历史/工作副本视图、主题和资源、依赖版本、构建脚本。
4. 当前发布使用 Avalonia 11 的兼容组合；上游可能使用不同的大版本。升级 UI 框架时须一并验证 DataGrid、编辑器、主题和剪裁发布。不要仅合并包版本号。
5. `depends/AvaloniaEdit` 当前是随源码保存的依赖快照。若上游更新该依赖，应对照记录的提交单独合并；不要同时保留 gitlink 和同路径的普通源码文件。
6. 在临时仓库验证常见 Git 工作流、完整 MCP 读写、认证弹窗、图谱左侧主线、分支隐藏、顶部工作区行、列宽保存及便携构建。不得在实际业务仓库运行破坏性验收命令。
7. 更新本文件基线及变更记录，通过 PR 审查后合入主分支。打新的 `treegit-v...` 标签，发布经过验证的 Windows ZIP。

## 已保留的兼容边界

- 原 SourceGit 命名空间、Avalonia 资源 URI 和 Git 配置键保留，降低后续合并成本。
- 原有 MIT 和第三方许可证保留。原版说明见 [UPSTREAM-README.md](UPSTREAM-README.md)，其中原版下载、包名和平台说明不代表 TreeGit 已提供相同构建。
- 当前 TreeGit 发布和主要验证平台是 Windows x64；不对未经验证的其他平台发布承诺。
- 本机对照资料、个人仓库、账号凭据、测试工作目录、SDK 和打包中间文件不进入公开源代码仓库。

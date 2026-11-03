# TreeGit

基于 [SourceGit](https://github.com/sourcegit-scm/sourcegit) 的桌面 Git 客户端，面向工程师日常使用。保留 SourceGit 的 Git 工作流，调整主界面为接近 SourceTree Windows 的布局，并增加让 AI 直接控制软件的 MCP 服务。

**无需安装、无需登录软件账号。** 解压 Windows x64 便携版后运行 `TreeGit.exe`。访问需要授权的 Git 仓库时，使用对应仓库的账号或 SSH 密钥。

> TreeGit 是独立衍生项目，与 Atlassian SourceTree 没有隶属关系。界面和图谱独立实现，不包含 SourceTree 程序或图像资产。SourceGit 原版权和 MIT 许可证保留。

## 界面与功能

- 墨青色菜单和不透明仓库标签、整行操作栏、左侧文件状态/分支/标签/远程/贮藏，下方文件与差异区。
- 颜色风格提供 `Default`、`Light`、`Dark` 和独立的 `SourceTree`；SourceTree 选项使用蓝色顶栏及灰白界面，浅色模式保留 TreeGit 的墨青配色。
- 独立“图谱”列；单击分支将其主线放在最左侧，不会检出该分支。双击仍保留原有检出操作。
- 可显示所有分支、仅所选分支主线，或所选分支及合并历史；过滤模式隐藏其他分支标签。
- 图谱顶部为“未提交的更改”。选中后，下方显示已暂存、未暂存文件及差异，上方保留提交图谱。
- 图谱、描述、日期、作者、提交等表头可拖动调整列宽，并按仓库保存。
- 保留 SourceGit 的克隆、推拉、分支、标签、合并、变基、交互式变基、贮藏、冲突处理、补丁、文件历史、Blame、子模块、Worktree、GitFlow、LFS、Bisect、自定义操作和命令日志等功能。部分高级入口位于“更多”、工具和上下文菜单。
- 使用 TreeGit 自有图标，帮助/关于注明上游来源及 MCP 支持。

界面仍在迭代；目标是跟随 SourceGit 保持功能，同时持续完善熟悉的桌面 Git 操作布局。

## 下载安装

从本仓库的 **Releases** 下载 `TreeGit-Windows-x64.zip`，完整解压后运行 `TreeGit/TreeGit.exe`。预发布构建也可从 **Actions → TreeGit Windows portable** 下载。

- 自带裁剪和压缩后的 .NET 运行时，无需安装 .NET。
- 主包不重复捆绑完整 Git，默认检测电脑上已有的 Git，与 SourceGit 的使用方式一致。
- 没有 Git 时，可将 Git for Windows 便携组件放在程序目录的 `git/` 下，使 `git/cmd/git.exe` 存在；也可在偏好设置中指定现有 Git。
- 偏好和缓存保存在旁边的 `data/` 中。复制旧版偏好即可保留个人配置；仓库内的 SourceGit 设置继续兼容。
- 当前发布和主要验收平台为 **Windows x64**。

## 仓库账号与 SSH

每个仓库可以独立配置 HTTPS 账号和密码；需要认证、账号缺失或密码失效时弹出输入框。已保存密码使用当前 Windows 用户的系统凭据库，不随便携目录复制。

SSH 使用 OpenSSH，并支持每个远程独立选择私钥。HTTPS 使用 OpenSSL 后端；Git for Windows 自带的 OpenSSH 使用其配套加密组件。SSH 与 OpenSSL 分别承担连接协议和加密支持，不需要把它们作为同一种协议配置。

## MCP：AI 控制软件

在 **工具 → MCP · AI 连接** 复制配置，添加到支持 stdio MCP 的 AI 客户端。默认完整读写，可以查询、控制界面和执行全部 Git 操作。未启动时会自动打开 TreeGit。

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

将路径替换为自己的实际路径。需要只读时，把 `args` 改成 `["--mcp", "--read-only"]`。工具表、调用示例、认证与操作行为见 [MCP 使用说明](docs/MCP.md)。

## 从源码构建

需要 Git、Windows、PowerShell 和 .NET 10 SDK。依赖源码已保存在仓库中。

```powershell
git clone https://github.com/JonSun2020/TreeGit.git
cd TreeGit
./build-treegit.ps1 -Destination ./artifacts/TreeGit
```

该脚本构建压缩、裁剪的自包含单文件程序，并生成 `data/`、许可证、构建信息和 MCP 配置。内部程序集暂保留 `SourceGit` 名称，以兼容资源和上游代码；对外程序名为 `TreeGit.exe`。

普通开发构建：

```powershell
dotnet build src/SourceGit.csproj -c Release -r win-x64 -p:PublishAot=false
```

构建脚本也支持本机已有的便携 SDK。测试用驱动仅在显式启用 `-IntegrationTests` 时编入，正常发布不包含这些入口。

全部功能的界面位置见 [SourceGit 功能入口对照](docs/FUNCTIONALITY.md)。仓库设置统一为远程仓库与高级两页，完整的原有配置仍可从高级页进入。

## 跟随上游迭代

当前基线为 SourceGit **2026.23**，提交 `c0f40a2e78d81a7474628847727644fc0c56f558`。保留上游提交历史，后续通过独立同步分支合并上游更新，再验证 TreeGit 的界面、认证和 MCP。

具体步骤、依赖兼容边界和需要审查的修改点见 [上游同步维护说明](docs/UPSTREAM.md)。TreeGit 发布使用 `treegit-v...` 标签，与 SourceGit 的版本区分。

## 许可证与贡献

遵循 [MIT](LICENSE)，第三方声明见 [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md)。原版文档保存在 [上游原始 README](docs/UPSTREAM-README.md)。欢迎通过 Issues 报告问题，或通过 Pull Request 改进界面、兼容性和工程师常用工作流。

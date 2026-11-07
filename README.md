# TreeGit

基于 [SourceGit](https://github.com/sourcegit-scm/sourcegit) 的桌面 Git 客户端，面向工程师日常使用。保留 SourceGit 的 Git 工作流，调整主界面为接近 SourceTree Windows 的布局，并增加让 AI 直接控制软件的 MCP 服务。

**无需安装、无需登录软件账号。** 解压 Windows x64 便携版后运行 `TreeGit.exe`。访问需要授权的 Git 仓库时，使用对应仓库的账号或 SSH 密钥。

**内置二进制文件对比，无需外部工具。** 在 TreeGit 中直接左右对比旧版与新版二进制内容，查看十六进制、ASCII、字节偏移和差异高亮，并跳转前后差异；无需安装 Beyond Compare 等外部比较软件。

> TreeGit 是独立衍生项目，与 Atlassian SourceTree 没有隶属关系。界面和图谱独立实现，不包含 SourceTree 程序或图像资产。SourceGit 原版权和 MIT 许可证保留。

## 界面与功能

- 内置二进制 Diff：旧版／新版左右窗口、十六进制与 ASCII、差异高亮和跳转，支持独立对比窗口，不依赖外部工具。
- 深绿色、深灰色或 Blue 蓝色顶栏、不透明仓库标签和仓库下拉列表；左侧分支/标签/远程/贮藏，底部文件状态、日志 / 历史及搜索标签。
- 主题提供“跟随系统”、`Light`、`Dark` 和 `Blue`。跟随系统时，系统浅色使用 Light，系统深色使用 Dark，并响应系统主题变化。Light 顶栏为已确认的深绿色 `#16836D`，Dark 顶栏为深灰色 `#1F1F1F`；Blue 保持蓝白配色。初次启动默认为 Light，升级保留现有选择。
- 独立“图谱”列，当前检出分支的主线始终在最左侧。单击左栏分支先返回日志 / 历史；“所有分支”下定位对应最新提交，“当前分支”下点击其它分支不改变图谱。双击当前本地分支不切换，其他本地分支先确认；远程分支使用独立的检出弹框。
- 分支显示提供“当前分支”和“所有分支”；勾选显示远程分支时包含对应上游历史。本地分支头加粗，并显示与拉取/推送一致的同步数量。
- 图谱顶部为“未提交的更改”。选中后，下方显示已暂存、未暂存文件及差异，上方保留提交图谱。
- 图谱、描述、日期、作者、提交等表头可拖动调整列宽，并按仓库保存。
- 保留 SourceGit 的克隆、推拉、分支、标签、合并、变基、交互式变基、贮藏、冲突处理、补丁、文件历史、Blame、子模块、Worktree、GitFlow、LFS、Bisect和命令日志等功能。高级操作位于“操作 → 高级操作”、偏好设置及对应的上下文菜单。
- 使用 TreeGit 自有图标，帮助/关于注明上游来源及 MCP 支持。

当前正式版为 **2026.23.23（Windows x64）**。后续继续跟随 SourceGit 上游更新，维护独立的界面、便携构建与 MCP 控制。

本版统一左栏分支的单击和双击行为：单击先返回日志 / 历史；双击其他本地分支先显示“切换本地分支”确认框，远程分支显示“检出远程分支”，优先复用已有跟踪分支并保留本地提交。搜索结果的“包含分支”列表和原提交定位继续保留。具体改动见[发行说明](docs/RELEASE-2026.23.23.md)，验证记录见[验收记录](docs/VALIDATION-2026.23.23.md)，Git 功能入口见[功能对照](docs/FUNCTIONALITY.md)。

二进制变更提供旧版本 / 新版本左右对比，显示十六进制、ASCII、偏移与差异高亮，支持前后差异跳转及独立窗口。按相同偏移比较，插入 / 删除后的字节不自动重新对齐；图片和 LFS 保留原有专用视图。“使用外部合并程序查看”目前仍显示“暂未支持”。

## 下载安装

从 [最新正式版](https://github.com/JonSun2020/TreeGit/releases/latest) 下载 [TreeGit-2026.23.23-Windows-x64.zip](https://github.com/JonSun2020/TreeGit/releases/download/treegit-v2026.23.23/TreeGit-2026.23.23-Windows-x64.zip)，完整解压后运行 `TreeGit/TreeGit.exe`。标准包含 Git，约 70 MB。校验值见同页的 `SHA256SUMS.txt`。2026.23.10 及更早版本需先手动升级一次，此后可使用“帮助 → 检查更新”。开发构建仍可从 **Actions → TreeGit Windows portable** 下载。

- 自带裁剪和压缩后的 .NET 运行时，无需安装 .NET。
- 自动优先使用电脑上已有且可用的 Git；没有安装、路径失效或版本不兼容时，自动使用随包的便携 Git，无需另行安装或联网下载。
- 请完整解压并保留 `git/` 目录。内置 MinGit、OpenSSH、OpenSSL、CA 证书、Git LFS 和 Git Flow；组件及许可证见[随包 Git 说明](docs/GIT-COMPONENTS.md)。仍可在偏好设置指定自己的 Git。
- 偏好和缓存保存在旁边的 `data/` 中。复制旧版偏好即可保留个人配置；仓库内的 SourceGit 设置继续兼容。
- 当前发布和主要验收平台为 **Windows x64**。

## 仓库账号与 SSH

每个仓库可以独立配置 HTTPS 账号和密码；认证时在同一个置顶窗口输入账号和密码（密码可留空），右侧眼睛可切换密码明文与 `*`；需要认证、账号缺失或密码失效时弹出输入框。已保存密码使用当前 Windows 用户的系统凭据库，不随便携目录复制。

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
./package-treegit.ps1 -Source ./artifacts/TreeGit -Destination ./artifacts
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

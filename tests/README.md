# TreeGit 验证说明

主要环境为 Windows x64、Python 3、Git for Windows。构建源码需要 .NET 10 SDK；正常便携程序自带运行时。LFS 和 Git Flow 专项需要各自外部组件。

测试在临时仓库写入，部分脚本会关闭所连接的 TreeGit 桌面。每组桌面测试使用独立的程序副本和空 `data/`，不要使用日常运行目录。切换需要不同环境变量的测试时，先等待上一组测试进程完全退出，或换一个新副本；MCP 会连接同目录已经运行的桌面。

## 正常发布构建

```powershell
./build-treegit.ps1 -Destination ./artifacts/TreeGit
```

将输出的程序文件复制到独立测试目录，再运行相应脚本。例如：

```powershell
python tests/mcp_protocol.py <测试副本>/TreeGit.exe <结果目录>/protocol.json
python tests/mcp_integration.py <测试副本>/TreeGit.exe <结果目录>/core
python tests/binary_diff.py <另一测试副本>/TreeGit.exe <结果目录>/binary
python tests/binary_refresh.py <另一测试副本>/TreeGit.exe <结果目录>/binary-refresh
python tests/desktop_soak.py <另一测试副本>/TreeGit.exe <结果目录>/soak 3600
```

`ui_integration.py` 需要前序核心测试生成的 `mcp-results.json`：复制该文件到 UI 结果目录，再传入这个目录。Git 核心测试使用实际提交、文件及引用核对；二进制检查比较实际字节，并等待后台刷新和首个差异定位完成。

二进制测试和持续测试给桌面设置独立 TEMP/TMP，跟踪自己的快照并检查关闭后的清理。正常构建没有测试专用快照字段，不能用空集合代替清理验证。持续报告保留进程 ID、资源采样、请求延迟、快照和失败内容；测试脚本的版本也应与记录对应。

## 集成测试构建

```powershell
./build-treegit.ps1 -IntegrationTests -Destination ./artifacts/TreeGit-QA
```

该构建增加真实控件和工作流的驱动，供设置持久化、认证输入、索引占用、连续冲突、消息恢复、子模块选择等专项脚本使用。正常发布不包含驱动，打包脚本拒绝该构建。

`--integration-probe <临时目录> <报告.json>` 验证原始命令、图谱、bundle、子模块、工作树、LFS 对象传输和 Git Flow。其 Git Flow 检查要求 `git flow version` 可找到 Git Flow Next。不要在业务仓库运行这些写入专项。

## 真实 SSH

`ssh_transport.py` 使用真实 OpenSSH、回环 SSH 服务及实际 Git 上传/接收协议。服务端测试依赖 Paramiko；不会加入主程序发布包。

先安装测试依赖并构建 `tests/GitSshProxy.csproj`，将 `TREEGIT_TEST_GIT_PROXY` 指向构建后的 `GitSshProxy.exe`。代理运行真实 Git，仅给程序生成的 SSH 命令追加隔离的配置文件，保留原私钥参数，并转发原始输出。代理是测试工具，需可用的 .NET 10 运行时或 SDK。

正常程序使用 `--plain-only` 检查 URI、配置别名、两个身份的克隆、获取和推送。集成构建还检查加密私钥、置顶输入和错误口令重试；`--trust-first-host` 额外检查首次主机指纹确认。SSH 密钥、known_hosts 和配置均在测试目录生成，不修改用户 SSH 文件。

## 记录失败

给每次失败及修复后的复测使用不同结果目录。先区分程序缺陷、异步操作未完成、环境依赖及测试进程互相干扰，再修改相应部分；不删除首次失败来代替调查。公开验收说明应标明构建源码，不能把旧候选或测试构建的通过记录写成最终发布程序的结果。

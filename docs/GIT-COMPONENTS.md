# 随包 Git 运行组件

TreeGit 优先使用电脑上可用的 Git；没有可用版本时，使用本目录的便携运行组件。无需安装、修改注册表或配置系统 PATH，也无需联网下载后才能使用。

| 组件 | 版本 | 发布及对应源码 |
| --- | --- | --- |
| Git for Windows / MinGit x64 | 2.56.0.windows.4 | [官方发行版](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.4)、[对应源码](https://github.com/git-for-windows/git/tree/v2.56.0.windows.4) |
| Git LFS | 3.8.1 | [官方发行版](https://github.com/git-lfs/git-lfs/releases/tag/v3.8.1)、[对应源码](https://github.com/git-lfs/git-lfs/tree/v3.8.1) |
| Git Flow Next | 2.1.0 | [官方发行版](https://github.com/gittower/git-flow-next/releases/tag/v2.1.0)、[对应源码](https://github.com/gittower/git-flow-next/tree/v2.1.0) |

MinGit 是 Git for Windows 为第三方程序提供的便携运行组件，包含 Git、OpenSSH、OpenSSL、HTTPS CA 证书及配套依赖。随包保留官方二进制及许可证，额外加入 LFS 和 Git Flow；仅在系统 Git 配置末尾增加可随目录移动的 CA 证书路径。下载地址和校验值固定在源码的 `build/git-runtime.lock.json`；本目录 `runtime-manifest.json` 记录逐文件校验值。

Git 及配套软件分别遵循自己的许可证，不因 TreeGit 的 MIT 许可证而改变。Git 许可证见 `LICENSE.txt`，LFS 和 Git Flow 见 `licenses/`，其他组件的版权、许可证及版本见 `ucrt64/share/licenses/`、`usr/share/licenses/`、`etc/package-versions.txt` 及 Git for Windows 官方发布资料。

内置组件不提供交互式 Git Bash 窗口、Perl 或 Tcl/Tk。TreeGit 的终端入口可使用 Windows PowerShell / cmd；依赖额外解释器的自定义 hooks、aliases 或外部工具仍需要其对应运行环境。需要完整 Git Bash 等环境时，可使用已安装的 Git for Windows，或在偏好设置指定自己的完整 Git。

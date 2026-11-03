"""Package the verified TreeGit build without SDKs, Git or test preferences."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import zipfile

repo = Path(__file__).resolve().parents[1]
base = repo.parent/'output/TreeGit'
delivery = base/'交付'
app = delivery/'TreeGit'
qa = base/'验收记录'
upstream = 'c0f40a2e78d81a7474628847727644fc0c56f558'
build = json.loads((app/'build-info.json').read_text(encoding='utf-8-sig'))
assert not build['integrationTests']
production = build['sourceCommit']

def read(name): return json.loads((qa/name).read_text(encoding='utf-8-sig'))
counts = {}
for name, count in [('protocol-results.json',18), ('mcp-results.json',45), ('ui-results.json',31), ('graph-results.json',31), ('preserve-upstream.json',28), ('upstream2-results.json',35)]:
    result = read(name)
    assert result['passed'] == count, (name,result)
    counts[name] = count
assert read('concurrency-results.json')['all_passed']
assert read('credentials-results.json')['actual_gui_git_fetch_authentication']
assert read('credentials-prompt-results.json')['expired_credential_reprompts_and_retries']
assert (app/'TreeGit.exe').exists() and not (app/'git').exists()

helptext = '''TreeGit 2026.23 · Windows x64 便携版

双击 TreeGit.exe 启动，无需安装 .NET 或登录软件账号。
默认使用电脑上已有的 Git，与原版 SourceGit 的使用方式相同。
没有 Git 时，将单独提供的 TreeGit-便携Git组件.zip 解压到本目录，形成 git/cmd/git.exe；不需要运行安装程序。

主界面按 SourceTree Windows 布局重排：菜单与标签栏、操作工具栏、工作区/分支侧栏、路线图与提交表、下方文件列表和差异预览。
单击左侧分支即可将它的主线放在路线图最左侧，不会切换检出分支。
历史上方下拉框：全部分支 / 仅所选分支（仅一条主线）/ 所选分支及合并历史。
双击分支仍执行原来的检出操作。更多、工具及上下文菜单保留补丁、LFS、二分查找、日志、统计等功能。

仓库账号：右上角钥匙按钮。不同仓库独立保存 HTTPS 账号；缺少账号或密码失效时自动弹出输入窗。
密码保存在当前 Windows 用户的系统凭据库，不随便携目录复制。SSH 使用 OpenSSH；便携 Git 组件中的 OpenSSH 使用 OpenSSL。

MCP：工具 → MCP · AI 连接，复制配置到 AI 客户端。默认只读；需要 Git 写操作时在连接配置中启用读写。
目录移动或重新解压后，请在新位置重新复制 MCP 配置。旁边的 JSON 仅适用于生成时的可执行文件路径。
用户偏好保存在 data 目录。复制旧版 data/preference.json 可保留原偏好；仓库中的 SourceGit 配置和 UI 状态继续兼容。

本项目基于 SourceGit，保留原项目及第三方许可证。上游源码：https://github.com/sourcegit-scm/sourcegit
'''
(app/'使用说明.txt').write_text(helptext,encoding='utf-8-sig')
(app/'TreeGit-build.txt').write_text(f'TreeGit 2026.23\nUpstream: {upstream}\nProduction source: {production}\nWindows x64 / .NET 10 self-contained / integration drivers disabled\n',encoding='utf-8')

def archive(folder, target, prefix, empty_data=False):
    with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
        for p in sorted(folder.rglob('*')):
            if not p.is_file(): continue
            relative=p.relative_to(folder)
            if relative.parts[0]=='data': continue
            z.write(p,prefix+'/'+relative.as_posix())
        if empty_data:z.writestr(prefix+'/data/','')

portable = delivery/'TreeGit-Windows-x64.zip'
archive(app,portable,'TreeGit',True)
gitpart = delivery/'TreeGit-便携Git组件.zip'
archive(base/'optional/git',gitpart,'git')
preview=delivery/'界面预览'
preview.mkdir(exist_ok=True)
for name in ['main-interface.png','only-main.png','main-1024.png','main-dark.png','working-copy.png','about.png']:
    shutil.copy2(qa/name,preview/name)
backup=delivery/'开发备份'
backup.mkdir(exist_ok=True)
subprocess.run(['git','archive','--format=zip','--prefix=TreeGit-source/','-o',str(backup/'TreeGit-定制源码.zip'),'HEAD'],cwd=repo,check=True)
subprocess.run(['git','bundle','create',str(backup/'TreeGit-源码检查点.bundle'),'--all'],cwd=repo,check=True)

payload = sum(p.stat().st_size for p in app.rglob('*') if p.is_file() and p.relative_to(app).parts[0]!='data')
oldzip=repo.parent/'output/SourceGit-Portable/交付/SourceGit-SourceTree风格-Windows-x64.zip'
with zipfile.ZipFile(portable) as z:
    assert z.testzip() is None
    assert not any('/git/' in n or n.endswith('.pdb') for n in z.namelist())
    assert z.read('TreeGit/TreeGit.exe') == (app/'TreeGit.exe').read_bytes()
assert payload < 45*1024*1024 and portable.stat().st_size < 35*1024*1024
manifest={'name':'TreeGit','upstream':upstream,'production_source':production,'source_checkpoint':subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip(),'portable_bytes':portable.stat().st_size,'extracted_program_bytes':payload,'optional_git_zip_bytes':gitpart.stat().st_size,'previous_zip_bytes':oldzip.stat().st_size,'checks':counts,'default_package_includes_git':False,'zip_crc_verified':True,'executable_matches_tested_build':True}
(delivery/'package-verification.json').write_text(json.dumps(manifest,indent=2,ensure_ascii=False),encoding='utf-8')
mb=lambda n:f'{n/1024/1024:.1f} MiB'
report=f'''TreeGit 验收说明 · 2026-11-03

主程序：TreeGit/TreeGit.exe
压缩包：TreeGit-Windows-x64.zip
主程序包 {mb(portable.stat().st_size)}；解压后程序文件共 {mb(payload)}。
上一版主压缩包 {mb(oldzip.stat().st_size)}，本次缩小 {(1-portable.stat().st_size/oldzip.stat().st_size)*100:.1f}%。
标准包不捆绑 Git，默认复用现有 Git；可选便携 Git 组件单独提供，大小 {mb(gitpart.stat().st_size)}。

本次修正
1. 软件名称改为 TreeGit；主界面整体按 SourceTree Windows 的布局重排。使用独立的深青色树形 Logo；关于窗口明确注明基于 SourceGit 开发并修改界面，保留原项目链接、版权和 MIT 许可证。
2. 淡蓝色菜单/仓库标签栏；浅灰底、淡蓝图标操作栏。以本机小米办公 Pro 快捷方式图标的 #497BFF 取色并提亮，功能栏底色参考本机 SourceTree 的 #F4F5F7；保留提交、推送、拉取、获取、分支、合并、贮藏、丢弃、标签及终端/浏览/设置/工具入口。
3. 左侧工作区及分支树；右侧独立路线图、描述、提交时间、作者、提交指纹列。
4. 下方默认显示紧凑提交信息、变更文件和差异；文件状态页改为文件/差异区及底部提交区。
5. 单击分支后，该分支 first-parent 主线固定在最左侧；选择仅所选分支时隐藏其他提交和分支标签。
6. 另保留所选分支及合并历史模式，切回全部分支可恢复其他历史；图形选择不会检出分支。
7. 修复所选分支被删除后的 HEAD 回退，以及 Windows 本地 bundle 克隆的默认账号作用域。
8. .NET 自包含运行库裁剪、压缩；完整 Git 与开发/源码文件从主程序包中分离。

验证结果
- MCP 协议 18 项；真实 Git/MCP 操作 45 项；界面/弹窗 31 项：通过。
- 多分支图形 31 项，包括远端分支、仅主线、合并历史、删除所选分支回退：通过。
- 28 项原有控件/操作/绑定及语言资源保留检查：通过。
- 35 项原有命令与图形模型检查，含子模块、工作树、LFS、Git Flow、80 个复杂 DAG 选择、单色主题：通过。
- 12 个并发客户端、60 次请求及 1 MiB 输出上限：通过。
- 不同仓库同一服务器的账号隔离、系统凭据保存/删除、缺失与失效凭据自动弹窗重试：通过。
- 便携 Git：2.56.0.windows.4；OpenSSH 10.6p1 / OpenSSL 3.5.9；Git LFS 3.8.1；Git Flow Next 2.1.0。
- 在中文目录下使用 OpenSSL 验证 GitHub HTTPS 证书并查询上游 HEAD：通过，未关闭证书校验。

构建与检查点
SourceGit 上游版本 2026.23，提交 {upstream}。
程序源码提交 {production}；最新源码/测试检查点 {manifest['source_checkpoint']}。
保留 SourceGit 功能代码、命令和兼容配置键。内部程序集名和资源 URI 继续使用 SourceGit，以保持资源与插件兼容。
发行包不包含测试用自动输入驱动。验收记录位于上一级“验收记录”目录；预览位于“界面预览”。
压缩包 CRC 及程序字节一致性已校验，SHA256 见 SHA256.txt。
'''
(delivery/'验收说明.txt').write_text(report,encoding='utf-8-sig')
targets=[portable,gitpart,backup/'TreeGit-定制源码.zip',backup/'TreeGit-源码检查点.bundle']
(delivery/'SHA256.txt').write_text('\n'.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.relative_to(delivery).as_posix() for p in targets)+'\n',encoding='utf-8')
print(json.dumps(manifest,ensure_ascii=False,indent=2))

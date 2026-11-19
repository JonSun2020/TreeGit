"""Verify real Git index and file state through the external-tool command path."""
import json
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import traceback
from qa_cleanup import new_fixture, remove_fixture

exe, out = (Path(arg).resolve() for arg in sys.argv[1:3])
verify_state_change = '--recovery-state-change' in sys.argv[3:]
filename = next((arg for arg in sys.argv[3:] if not arg.startswith('--')), "-sample O'Brien 中文.txt")
assert all(not arg.startswith('--') or arg == '--recovery-state-change' for arg in sys.argv[3:])
ci_profile = None
if verify_state_change:
    from qa_profile import IsolatedProfile
    ci_profile = IsolatedProfile(exe, out)
root = new_fixture(out, 'TreeGit external tools ')
temporary = new_fixture(Path('/tmp') if os.name == 'posix' else Path(tempfile.gettempdir()), 'TreeGit ext tmp ')
(root / 'treegit-owned-test').write_text('Owned disposable test repository\n')
repo = root / "repository O'Brien 中文"
repo.mkdir()
env = {**os.environ, 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': str(root / 'gitconfig'),
       'XDG_CONFIG_HOME': str(root / 'config'), 'XDG_CACHE_HOME': str(root / 'cache'),
       'TMPDIR': str(temporary), 'TMP': str(temporary), 'TEMP': str(temporary)}
for path in [root / 'config', root / 'cache']: path.mkdir()
checks, records, failure = [], [], None

def check(name, value):
    assert value, name
    checks.append(name)

def git(*args, expect=0):
    result = subprocess.run(['git', '-C', str(repo), *args], env=env, capture_output=True, text=True, encoding='utf-8')
    assert result.returncode == expect, (args, result.stdout, result.stderr)
    return result.stdout.strip()

def tool_program(name):
    path = shutil.which(name)
    if path: return path
    # Portable Git includes the same standard command-line programs on Windows.
    git_exe = Path(shutil.which('git')).resolve()
    for parent in list(git_exe.parents)[:4]:
        candidate = parent / 'usr/bin' / (name + '.exe')
        if candidate.is_file(): return str(candidate)
    raise AssertionError('Standard test utility unavailable: ' + name)

def invoke(name, program, arguments, success, kind='merge', keep_recovery=False, adapter=None, path=None, use_supported=False):
    plan = root / 'plan.json'
    settings={'executable': program, 'arguments': arguments, 'kind': kind}
    if adapter:settings['adapter']=adapter
    if use_supported:settings['useSupportedArguments']=True
    plan.write_text(json.dumps(settings), encoding='utf-8')
    report = out / (name + '.json')
    result = subprocess.run([str(exe), '--integration-external-tool', str(repo), path or filename, str(plan), str(report)],
                            env=env, capture_output=True, timeout=40)
    check(name + ' produces an actual result', result.returncode == 0 and report.exists())
    details = json.loads(report.read_text(encoding='utf-8-sig'))
    check(name + ' expected adapter result', details.get('success') is success)
    check(name + ' TreeGit remains the first adapter', details['builtinFirst'])
    if not keep_recovery:
        check(name + ' releases its temporary empty diff input', not list(temporary.glob('TreeGit-external-diff-*')))
        check(name + ' releases its temporary merge session', not list(temporary.glob('TreeGit-external-merge-*')))
        check(name + ' leaves no redundant recovery draft', not list(repo.glob('.treegit-merge-draft-*')))
    records.append(details)

def conflict():
    git('reset', '--hard', 'main')
    git('merge', '--no-edit', 'feature/demo', expect=1)
    check('fixture has a real unresolved index', bool(git('ls-files', '--unmerged')))

try:
    git('init', '-b', 'main')
    git('config', 'user.name', 'TreeGit review')
    git('config', 'user.email', 'demo@example.invalid')
    git('config', 'core.autocrlf', 'false')
    (repo / filename).write_bytes(b'original\n')
    (repo / '.gitattributes').write_bytes(b'*.txt conflict-marker-size=11\n')
    git('add', '.'); git('commit', '-m', 'Initial')
    git('checkout', '-b', 'feature/demo')
    (repo / filename).write_bytes(b'remote change\n'); git('commit', '-am', 'Remote')
    git('checkout', 'main')
    (repo / filename).write_bytes(b'local change\n'); git('commit', '-am', 'Local')
    original_head = git('rev-parse', 'HEAD')
    conflict()
    original_index = git('ls-files', '--unmerged')
    original_bytes = (repo / filename).read_bytes()
    invoke('no-save', tool_program('true'), '', False)
    check('no-save preserves conflict index', git('ls-files', '--unmerged') == original_index)
    check('no-save preserves working bytes', (repo / filename).read_bytes() == original_bytes)
    invoke('cancel', tool_program('false'), '', False)
    invoke('kdiff3-merge-cancel', tool_program('false'), '', False,adapter='kdiff3')
    check('cancel preserves conflict index', git('ls-files', '--unmerged') == original_index)
    check('cancel preserves working bytes', (repo / filename).read_bytes() == original_bytes)
    native_arguments = root / 'native-arguments.json'
    native_script = root / 'native-tool.py'
    native_script.write_text('import json,sys\nfrom pathlib import Path\nPath('+repr(str(native_arguments))+').write_text(json.dumps({"switch":sys.argv[-1],"inputsExist":[Path(p).is_file() for p in sys.argv[1:5]]}))\nraise SystemExit(1)\n','utf-8')
    invoke('native-slash-switch', sys.executable, '"'+native_script.as_posix()+'" "$LOCAL" "$BASE" "$REMOTE" "$MERGED" /m', False)
    native_result=json.loads(native_arguments.read_text('utf-8'))
    check('native tools receive slash switches without MSYS path rewriting',native_result['switch']=='/m' and all(native_result['inputsExist']))
    check('native tool cancellation preserves conflict data',git('ls-files','--unmerged')==original_index and (repo/filename).read_bytes()==original_bytes)
    invoke('cancel-after-edit', tool_program('cp'), '"$LOCAL" "$MERGED"; false', False)
    check('a nonzero tool exit does not stage its edited result', git('ls-files', '--unmerged') == original_index)
    check('an unsuccessful tool keeps its edited draft', (repo / filename).read_bytes() == b'local change\n')
    (repo / filename).write_bytes(original_bytes)
    marker = root / 'unresolved.txt'
    marker.write_bytes(b'edited header\n' + original_bytes)
    marker_path = str(marker).replace('\\', '/')
    invoke('markers-remain', tool_program('cp'), '"' + marker_path + '" "$MERGED"', False)
    check('custom-size markers prevent staging', git('ls-files', '--unmerged') == original_index)
    check('an unresolved edited draft is preserved', (repo / filename).read_bytes() == marker.read_bytes())
    for label, encoding in [('partial-marker', 'utf-8'), ('utf16-le-marker', 'utf-16-le'), ('utf16-be-marker', 'utf-16-be'), ('utf32-marker', 'utf-32-le')]:
        marker.write_bytes(('合并结果\n' * 400 + '<' * 11 + ' Local\nlocal change\n').encode(encoding))
        invoke(label, tool_program('cp'), '"' + marker_path + '" "$MERGED"', False)
        check(label + ' preserves the unresolved index', git('ls-files', '--unmerged') == original_index)
        check(label + ' preserves the edited draft', (repo / filename).read_bytes() == marker.read_bytes())
    marker.write_bytes(b'A' * 65575 + b'\n' + b'<' * 11 + b' Local\n')
    invoke('marker-across-buffers', tool_program('cp'), '"' + marker_path + '" "$MERGED"', False)
    check('a marker crossing the streaming buffer also prevents staging', git('ls-files', '--unmerged') == original_index)
    check('the large unresolved draft is preserved', (repo / filename).read_bytes() == marker.read_bytes())
    invoke('saved-local', tool_program('cp'), '"$LOCAL" "$MERGED"', True)
    check('saved result resolves actual index', not git('ls-files', '--unmerged'))
    check('saved bytes match the selected result', (repo / filename).read_bytes() == b'local change\n')
    check('no operation creates an unexpected commit', git('rev-parse', 'HEAD') == original_head)
    git('merge', '--abort')
    before = [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')]
    native_diff_arguments=root/'native-diff-arguments.json'
    native_diff_script=root/'native-diff-tool.py'
    native_diff_script.write_text('import json,sys\nfrom pathlib import Path\nvalues={"switch":sys.argv[-1],"old":Path(sys.argv[1]).read_bytes().hex(),"new":Path(sys.argv[2]).read_bytes().hex()}\nPath('+repr(str(native_diff_arguments))+').write_text(json.dumps(values))\nraise SystemExit(0 if values["switch"]=="/readonly" else 2)\n','utf-8')
    invoke('native-diff-slash-switch',sys.executable,'"'+native_diff_script.as_posix()+'" "$LOCAL" "$REMOTE" /readonly',True,'diff')
    native_diff_result=json.loads(native_diff_arguments.read_text('utf-8'))
    check('native comparisons preserve switches and the exact two revisions',native_diff_result=={'switch':'/readonly','old':b'original\n'.hex(),'new':b'local change\n'.hex()})
    invoke('diff-success', tool_program('true'), '"$LOCAL" "$REMOTE"', True, 'diff')
    exported = root / 'compared remote.txt'
    invoke('diff-correct-files', tool_program('cp'), '"$REMOTE" "' + str(exported).replace('\\', '/') + '"', True, 'diff')
    check('external comparison receives the requested revision bytes', exported.read_bytes() == b'local change\n')
    invoke('diff-failure', tool_program('false'), '"$LOCAL" "$REMOTE"', False, 'diff')
    for code in [0,1,2,11,12,13,14,100,101,102,103,104,107,127]:
        invoke('beyond-diff-exit-'+str(code),tool_program('sh'),'-c "exit '+str(code)+'"',code<100,'diff',adapter='beyond_compare')
    for code in [0,1,2,3,127]:
        invoke('winmerge-diff-exit-'+str(code),tool_program('sh'),'-c "exit '+str(code)+'"',code<2,'diff',adapter='win_merge')
        invoke('kdiff3-diff-exit-'+str(code),tool_program('sh'),'-c "exit '+str(code)+'"',code<2,'diff',adapter='kdiff3')
    if os.environ.get('TREEGIT_TEST_BC_EXECUTABLE'):
        installed=Path(os.environ['TREEGIT_TEST_BC_EXECUTABLE']).resolve();assert os.name=='nt' and installed.name=='BComp.exe' and installed.is_file()
        settings_before={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in installed.parent.glob('*.xml')}
        names=['BComp.exe','BCompare.exe','BCompare.tr','7z.dll','BCUnRAR.dll','BC4Key.txt']
        metadata_before={name:(p.stat().st_mtime_ns,p.stat().st_mode,p.stat().st_size) for name in names if (p:=installed.parent/name).is_file()}
        # Installed application files are copied so fixture cleanup cannot alter
        # their shared attributes. Keep the private portable profile and optional
        # license in the outer temporary root, also cleaned if this suite fails.
        bc=temporary/'isolated BC';bc.mkdir()
        for name in names:
            source=installed.parent/name
            if source.is_file():
                shutil.copy2(source,bc/name)
        (bc/'BCState.xml').write_text('','utf-8')
        (bc/'BCPreferences.xml').write_text('<BCPreferences Version="1" MinVersion="1"><TBcPrefs><CheckForUpdatesDayAmt Value="0"/></TBcPrefs></BCPreferences>','utf-8')
        invoke('installed-bc-native-difference',str(bc/'BComp.exe'),'/qc=binary /solo /silent',True,'diff',adapter='beyond_compare',use_supported=True)
        invoke('installed-bc-native-missing-file',str(bc/'BComp.exe'),'"$LOCAL" "missing-anonymous-file.txt" /qc=binary /solo /silent',False,'diff',adapter='beyond_compare')
        from qa_tool_window import close_owned_comparison
        (bc/'.treegit-owned-tool.json').write_text(json.dumps({'purpose':'TreeGit disposable external comparison','executables':{'BCompare.exe':hashlib.sha256((bc/'BCompare.exe').read_bytes()).hexdigest()}}),'utf-8')
        with close_owned_comparison(bc/'BCompare.exe',filename) as closed:
            invoke('installed-bc-normal-close',str(bc/'BComp.exe'),'/solo /readonly',True,'diff',adapter='beyond_compare',use_supported=True)
        check('closing the actual Beyond Compare window completes normally',bool(closed))
        check('real comparison does not alter the users tool settings',settings_before=={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in installed.parent.glob('*.xml')})
        check('comparison preserves the installed application file metadata',metadata_before=={name:(p.stat().st_mtime_ns,p.stat().st_mode,p.stat().st_size) for name in names if (p:=installed.parent/name).is_file()})
    if os.environ.get('TREEGIT_TEST_WINMERGE_EXECUTABLE'):
        installed=Path(os.environ['TREEGIT_TEST_WINMERGE_EXECUTABLE']).resolve()
        assert os.name=='nt' and installed.name=='WinMergeU.exe' and installed.is_file()
        winmerge=root/'isolated WinMerge'
        shutil.copytree(installed.parent,winmerge,copy_function=os.link)
        ini=root/'isolated-winmerge.ini';ini.write_text('','utf-8')
        arguments='/g TreeGit-isolated-adapter-qa /noprefs /inifile "'+ini.as_posix()+'" /s- /noninteractive /enableexitcode '
        invoke('real-winmerge-differences',str(winmerge/'WinMergeU.exe'),arguments,True,'diff',adapter='win_merge',use_supported=True)
        invoke('real-winmerge-identical',str(winmerge/'WinMergeU.exe'),arguments+'"$LOCAL" "$LOCAL"',True,'diff',adapter='win_merge')
        invoke('real-winmerge-missing-file',str(winmerge/'WinMergeU.exe'),arguments+'"$LOCAL" "missing-anonymous-file.txt"',False,'diff',adapter='win_merge')
        from qa_tool_window import close_owned_comparison
        gui_arguments='/g TreeGit-isolated-adapter-qa /noprefs /inifile "'+ini.as_posix()+'"'
        with close_owned_comparison(winmerge/'WinMergeU.exe',filename) as closed:
            invoke('real-winmerge-normal-close',str(winmerge/'WinMergeU.exe'),gui_arguments,True,'diff',adapter='win_merge',use_supported=True)
        check('closing a real WinMerge comparison completes normally',bool(closed))
        check('WinMerge uses an isolated settings file',ini.exists())
    check('diff does not mutate Git or files', before == [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')])
    added=filename+'.added';(repo/added).write_bytes(b'new-side\n');git('add','--',added);git('commit','-m','Added-file fixture')
    added_before=[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')]
    invoke('native-added-file',sys.executable,'"'+native_diff_script.as_posix()+'" "$LOCAL" "$REMOTE" /readonly',True,'diff',path=added)
    check('added-file comparison supplies a real empty old side',json.loads(native_diff_arguments.read_text('utf-8'))=={'switch':'/readonly','old':'','new':b'new-side\n'.hex()})
    check('added-file comparison is read-only',added_before==[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')])
    git('rm','--',added);git('commit','-m','Deleted-file fixture')
    deleted_before=[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')]
    invoke('native-deleted-file',sys.executable,'"'+native_diff_script.as_posix()+'" "$LOCAL" "$REMOTE" /readonly',True,'diff',path=added)
    check('deleted-file comparison supplies a real empty new side',json.loads(native_diff_arguments.read_text('utf-8'))=={'switch':'/readonly','old':b'new-side\n'.hex(),'new':''})
    check('deleted-file comparison is read-only',deleted_before==[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')])
    git('reset','--hard',original_head)
    if verify_state_change:
        conflict()
        previous_bytes = (repo / filename).read_bytes()
        invoke('state-changed-after-edit', tool_program('cp'), '"$LOCAL" "$MERGED"; git add -- "$MERGED"; false',
               False, keep_recovery=True)
        check('recovery does not overwrite the file after the index changed', (repo / filename).read_bytes() == previous_bytes)
        check('recovery respects the external operation on the real index', not git('ls-files', '--unmerged') and git('show', ':' + filename) == 'local change')
        drafts = list(repo.glob('.treegit-merge-draft-*'))
        check('the complete edited draft is available for manual recovery', len(drafts) == 1 and drafts[0].read_bytes() == b'local change\n')
        records_on_disk = list(temporary.glob('TreeGit-external-merge-*/draft-*.json'))
        check('manual recovery keeps its original file mapping', len(records_on_disk) == 1)
        mapping = {key.lower(): value for key, value in json.loads(records_on_disk[0].read_text(encoding='utf-8')).items()}
        check('the recovery mapping identifies both real files', Path(mapping['file']).resolve() == (repo / filename).resolve() and Path(mapping['recovery']).resolve() == drafts[0])
        check('recovery has not created a commit', git('rev-parse', 'HEAD') == original_head)
except BaseException:
    failure = traceback.format_exc()
    raise
finally:
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'records': records, 'failure': failure}, ensure_ascii=False, indent=2), encoding='utf-8')
    remove_fixture(root, out, 'TreeGit external tools ')
    remove_fixture(temporary, temporary.parent, 'TreeGit ext tmp ')
    if ci_profile: ci_profile.close()
print(json.dumps({'passed': len(checks), 'failure': failure}))

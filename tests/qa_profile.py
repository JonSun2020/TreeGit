"""Reuse one owned CI application while isolating and restoring its portable data."""
import atexit
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
from qa_cleanup import remove_fixture


class IsolatedProfile:
    def __init__(self, executable, output):
        assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows', 'Application profile tests require an owned CI runner'
        self.exe = Path(executable).resolve()
        self.out = Path(output).resolve()
        allowed = [Path(os.environ[k]).resolve() for k in ('RUNNER_TEMP', 'GITHUB_WORKSPACE') if os.environ.get(k)]
        assert self.exe.is_file() and any(self.exe.is_relative_to(p) for p in allowed), 'Application must belong to the CI workspace'
        assert any(self.out.is_relative_to(p) for p in allowed), 'QA output must belong to the CI workspace'
        self.out.mkdir(parents=True, exist_ok=True)
        self.app = self.exe.parent
        self.data = self.app / 'data'
        assert not self.out.is_relative_to(self.data), 'QA reports must remain outside the disposable profile'
        self.closed = False
        self._idle()
        assert not self.data.is_symlink() and not self.data.is_junction(), 'Portable profile must not be a link'
        self.original = self._digest(self.data / 'preference.json')
        # Keep the rename on the same volume; only profile data moves.
        self.backup = Path(tempfile.mkdtemp(prefix='TreeGit saved profile ', dir=self.app)).resolve()
        assert self.backup.parent == self.app and self.data.parent == self.app
        if self.data.exists(): self.data.rename(self.backup / 'data')
        atexit.register(self.close)
        self.data.mkdir()

    def _idle(self):
        literal = str(self.exe).replace("'", "''")
        code = "$target='" + literal + "';if(Get-Process | Where-Object {$_.Path -eq $target}){throw 'Owned application is still running; portable profile left unchanged'}"
        subprocess.run(['powershell', '-NoProfile', '-Command', code], check=True, capture_output=True)

    @staticmethod
    def _digest(path):
        return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None

    def reset(self):
        assert not self.closed
        self._idle()
        remove_fixture(self.data, self.app, 'data')
        self.data.mkdir()
        return self.app

    def close(self):
        if self.closed: return
        self._idle()
        remove_fixture(self.data, self.app, 'data')
        saved = self.backup / 'data'
        if saved.exists(): saved.rename(self.data)
        assert self._digest(self.data / 'preference.json') == self.original, 'Original portable preferences were not restored'
        remove_fixture(self.backup, self.app, 'TreeGit saved profile ')
        self.closed = True
        (self.out / 'profile-reuse.json').write_text(json.dumps({
            'application': str(self.exe), 'programCopiesCreated': 0,
            'originalPreferencesRestored': True, 'temporaryProfileRemoved': True,
        }, indent=2), encoding='utf-8')
        print('PASS original portable profile restored; zero application copies created', flush=True)

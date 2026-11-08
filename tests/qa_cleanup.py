"""Remove a positively identified, owned QA fixture without traversing links."""
import os
import atexit
import tempfile
from pathlib import Path
import stat


def new_fixture(parent, prefix):
    parent = Path(parent).resolve()
    assert prefix.startswith('TreeGit '), 'QA fixture requires an explicit owned prefix'
    parent.mkdir(parents=True, exist_ok=True)
    path = Path(tempfile.mkdtemp(prefix=prefix, dir=parent)).resolve()
    atexit.register(remove_fixture, path, parent, prefix)
    return path


def remove_fixture(path, parent, prefix):
    path = Path(path).absolute(); parent = Path(parent).resolve()
    assert path.parent.resolve() == parent and path.name.startswith(prefix), 'Unexpected QA cleanup target'
    assert not path.is_symlink() and not path.is_junction(), 'QA root is a link'
    if not path.exists(): return
    root = path.resolve()
    assert root.parent == parent and root.name.startswith(prefix)

    def remove(directory):
        with os.scandir(directory) as entries:
            children = list(entries)
        for entry in children:
            item = Path(entry.path).absolute()
            assert item.is_relative_to(root)
            if entry.is_symlink():
                item.unlink()
            elif item.is_junction():
                # Removing the link itself never enumerates or deletes its target.
                os.rmdir(item)
            elif entry.is_dir(follow_symlinks=False):
                remove(item)
            else:
                item.chmod(item.stat().st_mode | stat.S_IWRITE)
                item.unlink()
        os.rmdir(directory)

    remove(root)

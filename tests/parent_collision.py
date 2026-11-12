"""Create two valid Git commits with the same ten hexadecimal prefix characters.

Only three commit objects and an empty tree are written. The colliding nonces
were determined once in cloud QA; no repeated search or large dictionary is used.
"""
import hashlib
import os
import subprocess


def create_fixture(repo, object_format, env):
    assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
    tree = hashlib.new(object_format, b'tree 0\0').hexdigest()
    prefix = (f'tree {tree}\nauthor Prefix QA <qa@example.invalid> 1700000000 +0000\n'
              'committer Prefix QA <qa@example.invalid> 1700000000 +0000\n\nParent prefix collision ').encode('ascii')

    def content(nonce): return prefix + str(nonce).encode('ascii') + b'\n'
    pair = {'sha1': (1693600, 1830326), 'sha256': (682213, 1248445)}[object_format]

    def write(kind, data):
        result = subprocess.run(['git', '-C', str(repo), 'hash-object', '-w', '-t', kind, '--stdin'],
                                input=data, capture_output=True, env=env, timeout=30)
        assert result.returncode == 0, result.stderr
        return result.stdout.decode('ascii').strip()

    assert write('tree', b'') == tree
    first = write('commit', content(pair[0])); second = write('commit', content(pair[1]))
    assert first != second and first[:10] == second[:10]
    child = write('commit', (f'tree {tree}\nparent {first}\nauthor Prefix QA <qa@example.invalid> 1700000001 +0000\n'
                              'committer Prefix QA <qa@example.invalid> 1700000001 +0000\n\nCollision child\n').encode('ascii'))
    result = subprocess.run(['git', '-C', str(repo), 'branch', 'collision-proof', child], capture_output=True, env=env, timeout=30)
    assert result.returncode == 0, result.stderr
    return {'first': first, 'unreachable': second, 'child': child, 'nonces': pair}

"""Publish an existing, successful CI artifact without rebuilding it."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
import zipfile


def gh(*args):
    return subprocess.run(["gh", *args], check=True, capture_output=True,
                          text=True, encoding="utf-8").stdout


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def inspect_package(path, includes_git, version, source):
    with zipfile.ZipFile(path) as package:
        assert package.testzip() is None
        entries = package.namelist()
        assert len(entries) == len(set(entries))
        assert not any(n.startswith("TreeGit/data/") and n != "TreeGit/data/" for n in entries)
        assert not any(n.startswith(("TreeGit/log/", "TreeGit/cache/")) for n in entries)
        git_entries = {n for n in entries if n.startswith("TreeGit/git/")}
        if includes_git:
            assert {"TreeGit/git/runtime-manifest.json", "TreeGit/git/runtime.zip"}.issubset(git_entries)
        else:
            assert not git_entries
        build = json.loads(package.read("TreeGit/build-info.json").decode("utf-8-sig"))
        assert build["product"] == "TreeGit" and build["version"] == version and build["sourceCommit"] == source
        assert not build["integrationTests"]
        assert "TreeGit/TreeGit.exe" in entries
        return {entry.filename: (entry.file_size, entry.CRC) for entry in package.infolist()
                if entry.filename not in git_entries}


version = os.environ["TREEGIT_VERSION"]
source = os.environ["TREEGIT_SOURCE"]
run_id = os.environ["TREEGIT_RUN_ID"]
assert re.fullmatch(r"\d+\.\d+\.\d+", version)
assert re.fullmatch(r"[0-9a-f]{40}", source)
assert re.fullmatch(r"\d+", run_id)
run = json.loads(gh("run", "view", run_id, "--json", "headSha,conclusion,jobs,url"))
assert run["headSha"] == source and run["conclusion"] == "success"
assert len(run["jobs"]) == 3 and all(j["conclusion"] == "success" for j in run["jobs"])

with tempfile.TemporaryDirectory(prefix="TreeGit-publish-") as temporary:
    folder = Path(temporary)
    gh("run", "download", run_id, "-n", "TreeGit-Windows-x64", "-D", str(folder))
    archive_name = f"TreeGit-{version}-Windows-x64.zip"
    checksum_file = folder / "SHA256SUMS.txt"
    rows = checksum_file.read_text("utf-8-sig").splitlines()
    assert len(rows) == 2, "Expected checksums for the full and app-only packages"
    checksums = {}
    for row in rows:
        digest, name = row.split()
        assert re.fullmatch(r"[0-9a-f]{64}", digest) and name not in checksums
        checksums[name] = digest
    assert archive_name in checksums
    app_name = next(name for name in checksums if name != archive_name)
    assert re.fullmatch(rf"TreeGit-{re.escape(version)}-Windows-x64-App-[0-9a-f]{{64}}\.zip", app_name)
    assert {p.name for p in folder.iterdir()} == {*checksums, checksum_file.name}
    for name, digest in checksums.items():
        assert sha(folder / name) == digest, f"Checksum mismatch: {name}"
    full_program = inspect_package(folder / archive_name, True, version, source)
    app_program = inspect_package(folder / app_name, False, version, source)
    assert full_program == app_program, "Full and app-only packages contain different program files"
    notes = Path(f"docs/RELEASE-{version}.md")
    for original, name in [(Path("docs/PORTABLE.md"), "README-Windows.md"),
                           (Path("docs/MCP.md"), "MCP.md"), (notes, "RELEASE-NOTES.md")]:
        shutil.copyfile(original, folder / name)
    (folder / "VALIDATION.md").write_text(
        f"# TreeGit {version} 验证记录\n\n源码提交：`{source}`。\n\n"
        f"[同源 CI]({run['url']}) 的三个作业全部通过，包含便携包、Git、MCP、界面及更新回归检查。\n\n"
        "发布使用该次 CI 生成的完整包和轻量更新包。已核对两个 ZIP 的完整性、SHA-256、程序版本、源码提交和程序文件一致性。\n\n"
        f"完整包 SHA-256：`{checksums[archive_name]}`。\n\n"
        f"轻量更新包 SHA-256：`{checksums[app_name]}`。\n", encoding="utf-8")
    assets = {p.name: p for p in folder.iterdir()}
    assert len(assets) == 7
    tag = "treegit-v" + version
    existing = subprocess.run(["gh", "release", "view", tag, "--json", "isDraft,targetCommitish,assets"],
                              capture_output=True, text=True, encoding="utf-8")
    if existing.returncode == 0:
        release = json.loads(existing.stdout)
        assert release["targetCommitish"] == source
    else:
        gh("release", "create", tag, "--draft", "--target", source,
           "--title", "TreeGit " + version, "--notes-file", str(notes))
        release = json.loads(gh("release", "view", tag, "--json", "isDraft,targetCommitish,assets"))
    assert {a["name"] for a in release["assets"]}.issubset(assets)
    present = {a["name"] for a in release["assets"]}
    for asset in release["assets"]:
        assert asset["digest"] == "sha256:" + sha(assets[asset["name"]])
    for name in sorted(assets.keys() - present):
        assert release["isDraft"], "A public release cannot be amended by this publisher"
        gh("release", "upload", tag, str(assets[name]))
    ready = json.loads(gh("release", "view", tag, "--json", "isDraft,targetCommitish,assets"))
    assert ready["targetCommitish"] == source and {a["name"] for a in ready["assets"]} == set(assets)
    for asset in ready["assets"]:
        assert asset["digest"] == "sha256:" + sha(assets[asset["name"]])
        assert asset["size"] == assets[asset["name"]].stat().st_size
    if ready["isDraft"]:
        gh("release", "edit", tag, "--draft=false", "--latest")
    for attempt in range(10):
        public = json.loads(gh("api", "repos/{owner}/{repo}/releases/latest"))
        if public["tag_name"] == tag:
            break
        time.sleep(3)
    assert public["tag_name"] == tag and not public["draft"] and not public["prerelease"]
    assert public["target_commitish"] == source and {a["name"] for a in public["assets"]} == set(assets)
    print("Verified and published", public["html_url"])

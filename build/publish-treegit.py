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
    archive = folder / f"TreeGit-{version}-Windows-x64.zip"
    checksum = (folder / "SHA256SUMS.txt").read_text("utf-8-sig").split()
    assert checksum == [sha(archive), archive.name]
    with zipfile.ZipFile(archive) as package:
        assert package.testzip() is None
        build = json.loads(package.read("TreeGit/build-info.json").decode("utf-8-sig"))
        assert build["version"] == version and build["sourceCommit"] == source
        assert not build["integrationTests"]
        assert not any(n.startswith("TreeGit/data/") and not n.endswith("/") for n in package.namelist())
    notes = Path(f"docs/RELEASE-{version}.md")
    for original, name in [(Path("docs/PORTABLE.md"), "README-Windows.md"),
                           (Path("docs/MCP.md"), "MCP.md"), (notes, "RELEASE-NOTES.md")]:
        shutil.copyfile(original, folder / name)
    (folder / "VALIDATION.md").write_text(
        f"# TreeGit {version} 验证记录\n\n源码提交：`{source}`。\n\n"
        f"[同源 CI]({run['url']}) 的三个作业全部通过，包含便携包、Git、MCP、界面及更新回归检查。\n\n"
        "发布直接使用该次 CI 的便携包，不重新编译。已核对 ZIP 完整性、SHA-256、程序版本及源码提交；包内不含个人配置。\n\n"
        f"ZIP SHA-256：`{sha(archive)}`。\n", encoding="utf-8")
    assets = {p.name: p for p in folder.iterdir()}
    assert len(assets) == 6
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
    assert ready["targetCommitish"] == source and len(ready["assets"]) == 6
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
    assert public["target_commitish"] == source and len(public["assets"]) == 6
    print("Verified and published", public["html_url"])

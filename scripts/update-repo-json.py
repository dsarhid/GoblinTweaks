"""Writes repo.json (a Dalamud custom plugin repository) from the manifest DalamudPackager produced.

Usage (called by the release workflow):
    python scripts/update-repo-json.py <manifest.json> <owner/repo> <tag>
"""
import json
import sys
import time
from pathlib import Path

manifest_path, repository, tag = sys.argv[1:4]
manifest = json.loads(Path(manifest_path).read_text(encoding="utf-8-sig"))

download = f"https://github.com/{repository}/releases/download/{tag}/latest.zip"
manifest.update(
    DownloadLinkInstall=download,
    DownloadLinkUpdate=download,
    DownloadLinkTesting=download,
    IsHide=False,
    IsTestingExclusive=False,
    LastUpdate=int(time.time()),
)

Path("repo.json").write_text(json.dumps([manifest], indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"repo.json -> {manifest.get('AssemblyVersion')} ({tag})")

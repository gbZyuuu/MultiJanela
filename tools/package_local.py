"""Curate an already compiled portable package; performs no network or publication."""
import argparse
import hashlib
import re
import shutil
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FILES = {"RobloxMultiManager.exe", "README.md", "LICENSE", "NOTICE.md", "THIRD-PARTY-NOTICES.txt", "SHA256SUMS.txt"}
DOCS = {"Microsoft.Web.WebView2.Core.xml", "Microsoft.Web.WebView2.WinForms.xml", "Microsoft.Web.WebView2.Wpf.xml"}

def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    target = args.directory.resolve(strict=True)
    if not target.is_relative_to((ROOT / "dist").resolve()) or not re.fullmatch(r"MultiJanela-\d+\.\d+\.\d+(?:-preview)?-win-(?:x64|x86|arm64)", target.name):
        raise ValueError("Select a generated MultiJanela package inside dist.")
    for item in target.iterdir():
        if not item.is_file() or item.name not in FILES | DOCS or getattr(item.stat(follow_symlinks=False), "st_file_attributes", 0) & 0x400:
            raise ValueError("Unexpected package entry: " + item.name)
    if (target / "RobloxMultiManager.exe").open("rb").read(2) != b"MZ":
        raise ValueError("Missing executable.")
    for name in DOCS:
        path = target / name
        if path.exists(): path.unlink()
    shutil.copyfile(ROOT / "PUBLIC_README.md", target / "README.md")
    for name in ("LICENSE", "NOTICE.md", "THIRD-PARTY-NOTICES.txt"):
        shutil.copyfile(ROOT / name, target / name)
    if "-preview-" in target.name:
        readme = target / "README.md"
        readme.write_text("Local preview / Prévia local — for review before release.\n\n" + readme.read_text(encoding="utf-8"), encoding="utf-8")
    manifest = "".join(digest(target / name) + "  " + name + "\n" for name in sorted(FILES - {"SHA256SUMS.txt"}))
    (target / "SHA256SUMS.txt").write_text(manifest, encoding="utf-8")
    if {p.name for p in target.iterdir()} != FILES: raise ValueError("Incorrect package inventory.")
    archive = target.parent / (target.name + ".zip")
    temporary = archive.with_name(archive.name + ".tmp")
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as output:
            for name in sorted(FILES): output.write(target / name, target.name + "/" + name)
        temporary.replace(archive)
    finally:
        if temporary.exists(): temporary.unlink()
    print("Executable:", target / "RobloxMultiManager.exe")
    print("Archive:", archive)
    print("Archive SHA256:", digest(archive))
    print("Verified inventory:", len(FILES), "files")

if __name__ == "__main__": main()

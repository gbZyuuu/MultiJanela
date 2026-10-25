"""Validate the exact standalone binary delivery before archiving or publishing."""
import hashlib
import re
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve(strict=True)
expected = {"RobloxMultiManager.exe", "README.md", "LICENSE", "NOTICE.md", "THIRD-PARTY-NOTICES.txt", "SHA256SUMS.txt"}
entries = list(root.iterdir())
assert {p.name for p in entries} == expected, "Unexpected package contents"
assert all(p.is_file() and not p.is_symlink() for p in entries), "Directories and links are forbidden"
manifest = {}
for line in (root / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines():
    match = re.fullmatch(r"([a-fA-F0-9]{64})  ([^/\\]+)", line)
    assert match and match[2] not in manifest, "Invalid checksum manifest"
    manifest[match[2]] = match[1].lower()
assert set(manifest) == expected - {"SHA256SUMS.txt"}
for name, digest in manifest.items():
    with (root / name).open("rb") as stream:
        assert hashlib.file_digest(stream, "sha256").hexdigest() == digest, name
with (root / "RobloxMultiManager.exe").open("rb") as stream:
    assert stream.read(2) == b"MZ", "Not a Windows executable"
print("Verified standalone delivery: exact inventory, PE executable and SHA256 checksums.")

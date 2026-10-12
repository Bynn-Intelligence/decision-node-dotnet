"""Checks the packages the release workflow built, before anything is published.

Usage: check_package.py <dist directory> <expected version>

Fails (exit 1) unless the directory holds exactly Bynn.DecisionNode.<version>.nupkg and
.snupkg, the metadata is what the package page shows (id, version, licence, readme, repository,
description), every file is on the allowlist, and no file names anything internal.
"""

import fnmatch
import hashlib
import io
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

PACKAGE_ID = "Bynn.DecisionNode"
REPOSITORY = "https://github.com/Bynn-Intelligence/decision-node-dotnet"
LICENSE = "MIT"

ALLOWED = {
    ".nupkg": [
        "_rels/.rels",
        "[Content_Types].xml",
        f"{PACKAGE_ID}.nuspec",
        "package/services/metadata/core-properties/*.psmdcp",
        "LICENSE",
        "README.md",
        f"lib/net8.0/{PACKAGE_ID}.dll",
        f"lib/net8.0/{PACKAGE_ID}.xml",
    ],
    ".snupkg": [
        "_rels/.rels",
        "[Content_Types].xml",
        f"{PACKAGE_ID}.nuspec",
        "package/services/metadata/core-properties/*.psmdcp",
        f"lib/net8.0/{PACKAGE_ID}.pdb",
    ],
}

# Nothing internal in any file of a package: document paths, machine paths, attribution...
FORBIDDEN = re.compile(
    r"docs/[0-9]{2}|backend/[0-9]{2}|products/|/Users/|co-authored-by|generated with",
    re.IGNORECASE,
)
# ...and no internal name. The names are kept out of this public file as the SHA-256 digests of
# the lowercase word: found anywhere inside a run of letters, or (WORDS) only as a whole run.
INSIDE = {
    "9e9b2efac0b62e4cf6f08cc6b7095e6ad7e18c447458c5849accb698f7149ef3",
    "934a11e602682e862b4b4d1f881ae8da1e097e3f6964bd780e5bb1fb798a6fdd",
    "b7933ed6180d9e2895a9d2df885e2a8b92e998a3c16297581f5b8698daacf492",
    "31d1b3d540ecf85228db7797efbe7d198ec5406abd3f6e64f27f684e9a312d29",
}
INSIDE_LENGTHS = (6, 7)
WORDS = {"2e0b8d61fa2a6959d254b6ff5d0fb512249329097336a35568089933b49abdde"}


def digest(word: str) -> str:
    return hashlib.sha256(word.encode()).hexdigest()


def internal_name(text: str) -> str | None:
    """The first internal name in the text, by its digest, or None."""
    for run in set(re.findall(r"[a-z]+", text.lower())):
        if digest(run) in WORDS:
            return run
        for length in INSIDE_LENGTHS:
            for i in range(len(run) - length + 1):
                if digest(run[i : i + length]) in INSIDE:
                    return run
    return None
# Boosters the package page does not describe until the API serves them.
UNLISTED = re.compile(r"face_detection|wanted_person", re.IGNORECASE)

errors: list[str] = []


def fail(message: str) -> None:
    errors.append(message)
    print(f"::error::{message}")


def texts(data: bytes) -> list[str]:
    """The file as text, read both ways a .NET assembly holds strings (UTF-8 and UTF-16)."""
    return [data.decode("utf-8", "ignore"), data.decode("utf-16-le", "ignore"), data[1:].decode("utf-16-le", "ignore")]


def allowed(name: str, pattern: str) -> bool:
    """An exact name, or a pattern with `*` (the brackets of `[Content_Types].xml` are literal)."""
    return name == pattern or ("*" in pattern and fnmatch.fnmatchcase(name, pattern))


def check_files(path: Path, kind: str) -> dict[str, bytes]:
    with zipfile.ZipFile(path) as archive:
        files = {name: archive.read(name) for name in archive.namelist() if not name.endswith("/")}
    print(f"{path.name}:")
    for name in sorted(files):
        print(f"  {len(files[name]):>8}  {name}")
        if not any(allowed(name, pattern) for pattern in ALLOWED[kind]):
            fail(f"{path.name}: {name} is not on the allowlist")
    for name, data in files.items():
        for text in texts(data):
            match = FORBIDDEN.search(text)
            if match:
                fail(f"{path.name}: {name} contains {match.group(0)!r}")
                break
            found = internal_name(text)
            if found:
                fail(f"{path.name}: {name} contains an internal name (in {found!r})")
                break
    return files


def check_metadata(path: Path, files: dict[str, bytes], version: str) -> None:
    nuspec = ET.parse(io.BytesIO(files[f"{PACKAGE_ID}.nuspec"])).getroot()
    ns = {"n": nuspec.tag.split("}")[0].strip("{")}

    def field(name: str) -> ET.Element | None:
        return nuspec.find(f"n:metadata/n:{name}", ns)

    def text(name: str) -> str:
        element = field(name)
        return (element.text or "").strip() if element is not None else ""

    if text("id") != PACKAGE_ID:
        fail(f"{path.name}: id is {text('id')!r}, not {PACKAGE_ID!r}")
    if text("version") != version:
        fail(f"{path.name}: version is {text('version')!r}, not {version!r}")
    if path.suffix == ".snupkg":
        return
    license_ = field("license")
    if license_ is None or license_.get("type") != "expression" or (license_.text or "").strip() != LICENSE:
        fail(f"{path.name}: the licence is not the expression {LICENSE}")
    if text("readme") != "README.md" or "README.md" not in files:
        fail(f"{path.name}: README.md is not the package readme")
    repository = field("repository")
    if repository is None or repository.get("url") != REPOSITORY:
        fail(f"{path.name}: the repository URL is not {REPOSITORY}")
    description = text("description")
    if not description:
        fail(f"{path.name}: no description")
    for what, value in (("README.md", files.get("README.md", b"").decode("utf-8", "ignore")), ("description", description)):
        match = UNLISTED.search(value)
        if match:
            fail(f"{path.name}: the {what} names {match.group(0)!r}")


def main() -> int:
    dist, version = Path(sys.argv[1]), sys.argv[2]
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?", version):
        fail(f"{version!r} is not a version")
        return 1
    found = sorted(p.name for p in dist.iterdir())
    wanted = sorted(f"{PACKAGE_ID}.{version}{kind}" for kind in ALLOWED)
    if found != wanted:
        fail(f"{dist} holds {found}, not {wanted}")
        return 1
    for kind in ALLOWED:
        path = dist / f"{PACKAGE_ID}.{version}{kind}"
        check_metadata(path, check_files(path, kind), version)
    if errors:
        return 1
    print(f"{PACKAGE_ID} {version}: metadata, contents and wording checked")
    return 0


if __name__ == "__main__":
    sys.exit(main())

"""Reads CHANGELOG.md for the release workflow.

  changelog.py top            the version of the top entry (`## [0.3.0] - Unreleased` -> 0.3.0)
  changelog.py notes VERSION  that version's section, without its heading, for the release notes

Exits 1 when the changelog has no such entry.
"""

import re
import sys
from pathlib import Path

HEADING = re.compile(r"^## \[([^\]]+)\]", re.MULTILINE)


def sections(text: str) -> list[tuple[str, str]]:
    found = list(HEADING.finditer(text))
    out = []
    for i, match in enumerate(found):
        end = found[i + 1].start() if i + 1 < len(found) else len(text)
        body = text[match.end() : end].split("\n", 1)[1] if "\n" in text[match.end() : end] else ""
        out.append((match.group(1), body.strip() + "\n"))
    return out


def main() -> int:
    entries = sections(Path("CHANGELOG.md").read_text(encoding="utf-8"))
    if not entries:
        print("::error::CHANGELOG.md has no version entry", file=sys.stderr)
        return 1
    if sys.argv[1:] == ["top"]:
        print(entries[0][0])
        return 0
    if len(sys.argv) == 3 and sys.argv[1] == "notes":
        for version, body in entries:
            if version == sys.argv[2]:
                sys.stdout.write(body)
                return 0
        print(f"::error::CHANGELOG.md has no entry for {sys.argv[2]}", file=sys.stderr)
        return 1
    print(__doc__, file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())

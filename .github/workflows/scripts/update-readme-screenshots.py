#!/usr/bin/env python3
from pathlib import Path

START = "<!-- branch-screenshots:start -->"
END = "<!-- branch-screenshots:end -->"
ANCHOR = "\n## Vision\n"

BLOCK = f"""{START}
## UI snapshots

These screenshots are generated from the current branch by the real WPF application on every non-main push. They are committed back to the branch so the README shows the UI that branch actually builds, rather than a manually curated image from some older revision.

| Archive browser | Binary analysis | Maintenance |
| :--: | :--: | :--: |
| [![Archive browser](docs/screenshots/archive-browser.png)](docs/screenshots/archive-browser.png) | [![Binary analysis](docs/screenshots/analysis.png)](docs/screenshots/analysis.png) | [![Maintenance](docs/screenshots/maintenance.png)](docs/screenshots/maintenance.png) |

{END}"""


def update_readme(path: Path) -> bool:
    text = path.read_text(encoding="utf-8")

    start_count = text.count(START)
    end_count = text.count(END)
    if start_count != end_count or start_count > 1:
        raise RuntimeError(
            f"Malformed screenshot markers: {START}={start_count}, {END}={end_count}"
        )

    if start_count == 1:
        start = text.index(START)
        end = text.index(END, start) + len(END)
        updated = text[:start] + BLOCK + text[end:]
    else:
        if ANCHOR not in text:
            raise RuntimeError("README anchor '## Vision' was not found")
        updated = text.replace(ANCHOR, f"\n{BLOCK}\n\n## Vision\n", 1)

    if updated == text:
        return False

    path.write_text(updated, encoding="utf-8", newline="\n")
    return True


def main() -> None:
    changed = update_readme(Path("README.md"))
    print("README screenshot section updated." if changed else "README screenshot section already current.")


if __name__ == "__main__":
    main()

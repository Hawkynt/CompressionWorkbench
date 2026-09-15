from pathlib import Path
import shutil

artifact_root = Path('/tmp/package-readmes')
packages = [
    path for path in artifact_root.rglob('Hawkynt.FileFormats.FileSystems')
    if path.is_dir() and (path / 'README.md').is_file() and (path / 'REFERENCE.md').is_file()
]
if len(packages) != 1:
    raise SystemExit(f'Expected exactly one generated FileSystems package directory, found {len(packages)}')

source = packages[0]
destination = Path('Hawkynt.FileFormats.FileSystems')
shutil.copyfile(source / 'README.md', destination / 'README.md')
shutil.copyfile(source / 'REFERENCE.md', destination / 'REFERENCE.md')

readme_path = destination / 'README.md'
text = readme_path.read_text(encoding='utf-8')
old_legend = '| **R** | Open, list and extract only. For network, distributed and encrypted formats this is detection of the on-disk signature plus whatever metadata the object carries. |'
new_legend = '| **N/A** | Registered filesystem domain that is not represented by a standalone image/container descriptor; specialized multi-source drivers may expose mounted capabilities separately. |\n| **R** | Open, list and extract an existing standalone image/container. |'
if text.count(old_legend) != 1:
    raise SystemExit(f'Expected one stale R legend, found {text.count(old_legend)}')
text = text.replace(old_legend, new_legend, 1)

old_row = '| [BeeGFS](https://en.wikipedia.org/wiki/BeeGFS) | `BeeGfs` | N/A | — | — | — | — | — | — | detection of the on-disk signature | Server-side objects only; no self-contained image exists | [BeeGFS](https://www.beegfs.io/) |'
new_row = '| [BeeGFS](https://en.wikipedia.org/wiki/BeeGFS) | `BeeGfs` | N/A | — | — | — | — | — | — | multi-stream V3/V6 decoder + native ext/XFS xattr/backing-image tests | Read-only offline metadata/storage target sets; V3 dentries + V6 inline/separate regular-file inodes; non-mirrored non-sparse RAID0 | [BeeGFS](https://www.beegfs.io/) |'
if text.count(old_row) != 1:
    raise SystemExit(f'Expected one stale BeeGFS support row, found {text.count(old_row)}')
text = text.replace(old_row, new_row, 1)
readme_path.write_text(text, encoding='utf-8')

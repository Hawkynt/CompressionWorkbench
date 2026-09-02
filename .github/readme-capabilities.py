from pathlib import Path
import re


def patch_states():
    p = Path('Hawkynt.FileFormats.FileSystems/README.md')
    t = p.read_text()
    replacements = {
      '| [Apple DMG](https://en.wikipedia.org/wiki/Apple_Disk_Image) | WORM | Apple disk-image container |':
        '| [Apple DMG](https://en.wikipedia.org/wiki/Apple_Disk_Image) | R/W | Apple disk-image container; verified rebuild-backed modification |',
      '| [Expert Witness Format](https://en.wikipedia.org/wiki/EnCase#Expert_Witness_File_Format) | R | EWF/EnCase forensic images |':
        '| [Expert Witness Format](https://en.wikipedia.org/wiki/EnCase#Expert_Witness_File_Format) | R/W ⚠️ | EWF/EnCase forensic images; logical `media.raw` mutation/repack profile |',
      '| [UEFI Firmware Volume](https://en.wikipedia.org/wiki/UEFI) | R | Firmware volume / FFS-oriented inspection |':
        '| [UEFI Firmware Volume](https://en.wikipedia.org/wiki/UEFI) | R/W ⚠️ | Fixed-size firmware-volume create/add/replace/remove profile |',
      '| [Device Tree Blob](https://en.wikipedia.org/wiki/Devicetree) | R | Flattened Device Tree property traversal |':
        '| [Device Tree Blob](https://en.wikipedia.org/wiki/Devicetree) | R/W | Flattened Device Tree hierarchy-preserving rebuild/edit |',
      '| [ReFS](https://en.wikipedia.org/wiki/ReFS) | R ⚠️ | Header/boot-sector oriented subset |':
        '| [ReFS](https://en.wikipedia.org/wiki/ReFS) | R/W ⚠️ | Native metadata read plus offline existing-file replace/remove and empty-directory removal; new-name insertion remains gated |',
      '| [EROFS](https://en.wikipedia.org/wiki/EROFS) | WORM | Enhanced read-only filesystem images |':
        '| [EROFS](https://en.wikipedia.org/wiki/EROFS) | R/W ⚠️ | Native-mounted read-only format; supported FLAT_PLAIN/FLAT_INLINE images are mutable offline by verified rebuild |',
    }
    for old, new in replacements.items():
        if old in t:
            t = t.replace(old, new, 1)
    p.write_text(t)


def descriptors(root):
    rows = []
    for path in sorted(Path(root).glob('**/*FormatDescriptor.cs')):
        text = path.read_text(errors='ignore')
        m = re.search(r'public\s+(?:sealed\s+)?class\s+\w+FormatDescriptor\s*:\s*(.*?)(?:\{|\n\s*public\s)', text, re.S)
        if not m:
            continue
        decl = m.group(1)
        mid = re.search(r'public\s+string\s+Id\s*=>\s*"([^"]+)"', text)
        if not mid:
            continue
        id_ = mid.group(1)
        interfaces = set(re.findall(r'\bI[A-Za-z0-9_]+\b', decl))
        # The class declaration may span until a later member in unusual formatting;
        # source-level interface tokens are still the authoritative advertised markers.
        rows.append((id_, interfaces, text))
    return rows


def matrix(rows, archive=False):
    out = [
      '<!-- maintenance-capabilities:start -->',
      '## Maintenance capability matrix',
      '',
      'A checkmark means the descriptor explicitly exposes the corresponding runtime capability. '
      'Profile-specific limitations still apply and unsafe/unknown layouts must fail closed. '
      'For immutable-on-mount filesystems, a checkmark may represent verified offline rebuild-backed maintenance.',
      '',
      '| Format | Optimize | Wipe / clean | Purge | Defrag |',
      '| --- | :---: | :---: | :---: | :---: |',
    ]
    for id_, interfaces, text in sorted(rows, key=lambda r: r[0].lower()):
        optimize = 'ILayoutOptimizable' in interfaces
        if archive and 'IStreamFormatOperations' in interfaces and 'IFormatOptionsSchema' in interfaces:
            optimize = True
        wipe = 'IWipeEmpty' in interfaces
        purge = 'IArchivePurgeable' in interfaces or 'IArchiveModifiable' in interfaces
        defrag = 'IArchiveDefragmentable' in interfaces
        mark = lambda v: '✅' if v else '—'
        out.append(f'| `{id_}` | {mark(optimize)} | {mark(wipe)} | {mark(purge)} | {mark(defrag)} |')
    out += [
      '',
      'The matrix is generated from descriptor interfaces during the merge-readiness audit; '
      'do not add a checkmark without an executable capability and round-trip/fail-closed coverage.',
      '<!-- maintenance-capabilities:end -->',
    ]
    return '\n'.join(out)


def install_matrix(readme, rows, archive=False):
    p = Path(readme)
    t = p.read_text()
    block = matrix(rows, archive)
    pattern = re.compile(r'<!-- maintenance-capabilities:start -->.*?<!-- maintenance-capabilities:end -->', re.S)
    if pattern.search(t):
        t = pattern.sub(block, t, count=1)
    else:
        anchor = '\n## 🚀 Quick start\n'
        if anchor not in t:
            raise SystemExit(f'quick-start anchor not found in {readme}')
        t = t.replace(anchor, '\n' + block + '\n' + anchor, 1)
    p.write_text(t)


patch_states()
install_matrix('Hawkynt.FileFormats.FileSystems/README.md', descriptors('FileSystems'))
install_matrix('Hawkynt.FileFormats.Archives/README.md', descriptors('FileFormats'), archive=True)
Path('.github/readme-capabilities.py').unlink()

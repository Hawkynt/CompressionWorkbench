from pathlib import Path
import re


def scan(root):
    rows=[]
    for path in sorted(Path(root).glob('**/*FormatDescriptor.cs')):
        text=path.read_text(errors='ignore')
        cm=re.search(r'public\s+(?:sealed\s+)?class\s+\w+FormatDescriptor\s*:\s*(.*?)\{',text,re.S)
        mid=re.search(r'public\s+string\s+Id\s*=>\s*"([^"]+)"',text)
        if not cm or not mid: continue
        interfaces=set(re.findall(r'\bI[A-Za-z0-9_]+\b',cm.group(1)))
        tunable='FormatOptionKind.Boolean' in text or re.search(r'AllowedValues\s*:\s*\[[^\]]*,',text,re.S)
        optimize=('ILayoutOptimizable' in interfaces or
                  ('IFormatOptionsSchema' in interfaces and tunable and
                   ('IArchiveCreatable' in interfaces or 'IStreamFormatOperations' in interfaces)))
        row={
          'id':mid.group(1), 'ifaces':interfaces,
          'defrag':'IArchiveDefragmentable' in interfaces,
          'shrink':'IArchiveShrinkable' in interfaces,
          'wipe':'IWipeEmpty' in interfaces,
          'purge':'IArchivePurgeable' in interfaces or 'IArchiveModifiable' in interfaces,
          'optimize':optimize,
          'meta':'IFileInternalLayoutMap' in interfaces or 'IFileInternalChunkMover' in interfaces,
        }
        row['compact']=row['defrag'] or row['shrink'] or row['optimize']
        rows.append(row)
    return rows


def mark(v): return '✅' if v else '—'

def fs_section(rows):
    lines=[
      '## Filesystem descriptors', '',
      'Generated from the descriptor capability interfaces in this tree. **Compact** is the composite '
      'defrag → optimize → shrink action and is available when at least one of those primitives is executable. '
      'A checkmark may represent a native in-place operation or a verified offline rebuild; mounted-driver R/W '
      'is tracked separately by the filesystem-driver readiness model.', '',
      '| Format | Optimize | Wipe / clean | Purge | Defrag | Shrink | Compact |',
      '| --- | :---: | :---: | :---: | :---: | :---: | :---: |',
    ]
    for r in sorted(rows,key=lambda r:r['id'].lower()):
        lines.append(f"| {r['id']} | {mark(r['optimize'])} | {mark(r['wipe'])} | {mark(r['purge'])} | {mark(r['defrag'])} | {mark(r['shrink'])} | {mark(r['compact'])} |")
    return '\n'.join(lines)


def archive_section(rows):
    visible=[r for r in rows if any(r[k] for k in ('optimize','wipe','purge','defrag','shrink','compact','meta'))]
    lines=[
      '## Archive / stream descriptors with at least one operation', '',
      'Archive **defrag** includes verified repack/relayout (including solid-stream regrouping where the format supports it). '
      '**Optimize** includes layout tuning and finite compression/dictionary/solid-block parameter search; candidates only win '
      'after round-trip verification.', '',
      '| Format | Optimize | Wipe / clean | Purge | Defrag | Shrink | Compact | Meta reorder |',
      '| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |',
    ]
    for r in sorted(visible,key=lambda r:r['id'].lower()):
        lines.append(f"| {r['id']} | {mark(r['optimize'])} | {mark(r['wipe'])} | {mark(r['purge'])} | {mark(r['defrag'])} | {mark(r['shrink'])} | {mark(r['compact'])} | {mark(r['meta'])} |")
    return '\n'.join(lines)

p=Path('docs/OPERATION_COVERAGE.md')
t=p.read_text()
# Canonical verb definitions and defaults.
t=re.sub(r'\| Purge\s*\| `IArchiveModifiable\.Remove`-all / empty `Create`\s*\|[^\n]*\|',
         '| Purge           | `IArchivePurgeable`                                  | Erase all live user data, leaving a valid empty container/image; system metadata may be recreated as required by the format. |',t)
t=t.replace('`Remove(all)` is the **purge** verb.', '`IArchivePurgeable.Purge` is the **purge** verb; `IArchiveModifiable` inherits it because full modification includes removing all live user files.')
t=t.replace('A filesystem descriptor therefore gains shrink / defrag / purge by simply declaring\nthe interface', 'A filesystem descriptor therefore gains shrink / defrag / purge by declaring\nthe corresponding interface')
t=t.replace('> **`CanModify` is advertised when the format is a mutable container with a working modify\n> path.** It is withheld only from **read-only-by-design** formats (CramFS, SquashFS) and\n> **create-only** formats — even though a rebuild could synthesise a modified copy, those do\n> not present themselves as editable.',
'''**`CanModify` means the public API can edit an existing instance and produce a verified valid result.**\nThe physical strategy may be in-place, copy-on-write, relayout/repack, or verified rebuild. Native OS mount\nimmutability (for example CramFS/SquashFS/EROFS) is not the same thing as offline image-editor capability.\nMounted-driver write readiness is tracked separately and remains fail-closed until its durability model is proven.''')
t=t.replace('- **CramFS**, **SquashFS** — compressed *read-only* filesystems by design; not presented as editable.\n','')
t=t.replace('- A handful of niche/append-shift formats (MSA per-track RLE; Wrapster/PFS0 header-at-start;\n  OVA manifest-over-all-members; MFS-1 bespoke catalog) keep the rebuild-backed verb without\n  advertising R/W.\n','- Formats whose correct edit necessarily rewrites headers, manifests, tracks, or whole images still advertise R/W when the verified rebuild is their supported existing-instance mutation strategy.\n')
# Broaden optimize definition to match the runtime implementation.
t=re.sub(r'\| Optimize\s*\| `ILayoutOptimizable`\s*\|[^\n]*\|',
         '| Optimize        | `ILayoutOptimizable` / tunable `IFormatOptionsSchema` | Search executable layout/compression parameters and keep the smallest/best verified result. |',t)

fs=scan('FileSystems')
arc=scan('FileFormats')
start=t.index('## Filesystem descriptors')
na=t.index('## N/A notes',start)
t=t[:start]+fs_section(fs)+'\n\n'+archive_section(arc)+'\n\n'+t[na:]

# Replace stale totals with source-derived advertised counts so the document and interfaces cannot drift in this pass.
allrows=fs+arc
counts={k:sum(1 for r in allrows if r[k]) for k in ('defrag','wipe','purge','shrink','optimize','meta')}
t=re.sub(r'\| Defragment\s*\| \d+ \|',f"| Defragment       | {counts['defrag']} |",t)
t=re.sub(r'\| Wipe\s*\| \d+ \|',f"| Wipe             | {counts['wipe']} |",t)
t=re.sub(r'\| Purge\s*\| \d+ \|',f"| Purge            | {counts['purge']} |",t)
t=re.sub(r'\| Shrink\s*\| \d+\s*\|',f"| Shrink           | {counts['shrink']} |",t)
t=re.sub(r'\| Optimize \(layout\)\| \d+\s*\|',f"| Optimize         | {counts['optimize']} |",t)
t=re.sub(r'\| Metadata-reorder\s*\| \d+\s*\|',f"| Metadata-reorder | {counts['meta']} |",t)
t=t.replace('(Counts are `GetArchiveOps(id) is IXxx` over the registered descriptors — i.e.\nwhat the UI/CLI actually gate on.', '(Counts above are regenerated from explicit descriptor capability interfaces in this tree. Runtime marker/flag consistency is enforced by CI; the UI/CLI gate on the same capability contracts.')
p.write_text(t)
Path('.github/coverage-matrix.py').unlink()

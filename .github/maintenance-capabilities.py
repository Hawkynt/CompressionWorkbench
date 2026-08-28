from __future__ import annotations

from pathlib import Path
import re

ROOT = Path('.')


def read(path: str | Path) -> str:
    return Path(path).read_text(encoding='utf-8')


def write(path: str | Path, value: str) -> None:
    Path(path).write_text(value, encoding='utf-8')


def replace_once(path: str | Path, old: str, new: str) -> None:
    value = read(path)
    if old not in value:
        raise SystemExit(f'{path}: required text not found:\n{old[:240]}')
    write(path, value.replace(old, new, 1))


def insert_before(path: str | Path, marker: str, insertion: str) -> None:
    value = read(path)
    if marker not in value:
        raise SystemExit(f'{path}: insertion marker not found: {marker[:160]}')
    write(path, value.replace(marker, insertion + marker, 1))


# ── Purge: explicit, transactional capability ───────────────────────────────
rebuild_path = Path('Compression.Registry/RebuildVerb.cs')
purge_helper = '''  /// <summary>\n  /// Transactional purge for a mutable container. The modifier operates only on\n  /// a staged copy; the caller's stream is replaced after the staged container\n  /// re-lists successfully and every original live entry name has disappeared.\n  /// </summary>\n  public static void PurgeViaModifier(\n      Stream archive, IArchiveFormatOperations ops, IArchiveModifiable modifier) {\n    ArgumentNullException.ThrowIfNull(archive);\n    ArgumentNullException.ThrowIfNull(ops);\n    ArgumentNullException.ThrowIfNull(modifier);\n    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)\n      throw new ArgumentException("Purge requires a readable, writable, seekable stream.", nameof(archive));\n\n    using var staged = CreateScratchStream();\n    archive.Position = 0;\n    archive.CopyTo(staged);\n    staged.Flush();\n\n    staged.Position = 0;\n    var sourceNames = ops.List(staged, null)\n      .Where(e => !e.IsDirectory)\n      .Select(e => e.Name)\n      .Distinct(StringComparer.OrdinalIgnoreCase)\n      .ToArray();\n    if (sourceNames.Length == 0) return;\n\n    staged.Position = 0;\n    modifier.Remove(staged, sourceNames);\n\n    staged.Position = 0;\n    var remaining = ops.List(staged, null)\n      .Where(e => !e.IsDirectory)\n      .Select(e => e.Name)\n      .ToHashSet(StringComparer.OrdinalIgnoreCase);\n    var survivors = sourceNames.Where(remaining.Contains).ToArray();\n    if (survivors.Length != 0)\n      throw new InvalidOperationException(\n        $"Purge left {survivors.Length} original live entr{(survivors.Length == 1 ? "y" : "ies")} behind; original container retained.");\n\n    archive.Position = 0;\n    archive.SetLength(0);\n    staged.Position = 0;\n    staged.CopyTo(archive);\n    archive.Flush();\n  }\n\n'''
if 'public static void PurgeViaModifier(' not in read(rebuild_path):
    insert_before(rebuild_path,
                  '  /// <summary>\n  /// A writable scratch stream that is not bounded by what a byte[] can hold.',
                  purge_helper)

# Purge default test now tests the explicit capability and requires an actual
# successful purge for any probe image a marked descriptor can create.
purge_test = Path('Compression.Tests/Operations/GenericPurgeRoundTripTests.cs')
v = read(purge_test)
v = v.replace('Safety net for the broad rollout of the default <see cref="IArchiveModifiable"/>',
              'Safety net for the explicit <see cref="IArchivePurgeable"/> capability')
v = v.replace('// Every format using the DEFAULT IArchiveModifiable.Remove (rebuild-via-WORM):',
              '// Every registered format explicitly exposing purge:')
v = v.replace('Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveModifiable))',
              'Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchivePurgeable))')
v = re.sub(r'\n\s*&& !Compression\.Tests\.Support\.CapabilityImplementers\.DeclaresOwn\(id, "Remove", typeof\(Stream\), typeof\(string\[\]\)\)', '', v)
v = v.replace('var modifiable = (IArchiveModifiable)fmtOps;', 'var purgeable = (IArchivePurgeable)fmtOps;')
v = v.replace('modifiable.Remove(ms, [.. before]);', 'purgeable.Purge(ms);')
v = v.replace('Assert.Pass($"{formatId}: purge cleanly NotSupported (no corruption).");\n        return;',
              'Assert.Fail($"{formatId}: advertises purge but rejected the probe container.");\n        return;')
v = v.replace('Assert.Ignore($"{formatId}: purge rebuild failed non-destructively ({ex.GetType().Name}).");\n        return;',
              'Assert.Fail($"{formatId}: advertises purge but failed the probe ({ex.GetType().Name}: {ex.Message}).");\n        return;')
write(purge_test, v)

marker_test = Path('Compression.Tests/Operations/MarkerInterfaceCoverageTests.cs')
v = read(marker_test)
v = v.replace('("purge/modify", typeof(IArchiveModifiable)),', '("purge", typeof(IArchivePurgeable)),')
write(marker_test, v)

# ── Wipe / clean: any exact extent/layout map gets a safe generic default ───
wipe_path = Path('Compression.Registry/IWipeEmpty.cs')
v = read(wipe_path)
old_decl = '  long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true);'
new_decl = '''  long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {\n    ArgumentNullException.ThrowIfNull(image);\n    if (!image.CanRead || !image.CanWrite || !image.CanSeek)\n      throw new ArgumentException("Wipe requires a readable, writable, seekable stream.", nameof(image));\n\n    List<DefragBlockInfo> extents = this switch {\n      IFilesystemExtentMap fs => fs.EnumerateExtents(image).ToList(),\n      IArchiveLayoutMap archive => archive.EnumerateLayout(image).ToList(),\n      _ => throw new NotSupportedException(\n        "The default wipe requires IFilesystemExtentMap or IArchiveLayoutMap."),\n    };\n\n    Func<string, long>? sizeLookup = null;\n    if (wipeClusterTips && this is IArchiveFormatOperations ops) {\n      image.Position = 0;\n      var sizes = ops.List(image, null)\n        .Where(e => !e.IsDirectory)\n        .GroupBy(e => e.Name, StringComparer.Ordinal)\n        .ToDictionary(g => g.Key, g => Math.Max(0, g.First().OriginalSize), StringComparer.Ordinal);\n      sizeLookup = name => sizes.TryGetValue(name, out var size) ? size : -1;\n    }\n\n    image.Position = 0;\n    return UnusedSpaceWiper.Wipe(image, extents, image.Length, wipeClusterTips, sizeLookup);\n  }'''
if old_decl in v:
    v = v.replace(old_decl, new_decl, 1)
write(wipe_path, v)

# ── Optimize: multi-entry option-space search is a real archive capability ──
archive_ops_path = Path('Compression.Lib/ArchiveOperations.cs')
v = read(archive_ops_path)
old_fallback = '''    // ── Unsupported: fall back to copy ───────────────────────────────\n    // Use temp+rename so a crash mid-copy doesn't leave a truncated target.\n    AtomicFileWriter.WriteAtomic(outputPath, outFs => {\n      using var inFs = File.OpenRead(inputPath);\n      inFs.CopyTo(outFs);\n    });\n    return (originalSize, originalSize, 0);'''
new_fallback = '''    // ── Generic multi-entry archive parameter search ─────────────────\n    // A creatable archive with a finite option schema can be optimized without\n    // format-specific code: try method/level/dictionary/solid-size combinations,\n    // verify every rebuild round-trips the exact live entry set, and keep only\n    // a strictly smaller result.\n    FormatRegistration.EnsureInitialized();\n    var archiveOps = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());\n    if (archiveOps is Compression.Registry.IArchiveCreatable creator\n        && archiveOps is Compression.Registry.IFormatOptionsSchema schema\n        && schema.OptionsSchema.Any(o =>\n          o.Kind == Compression.Registry.FormatOptionKind.Boolean\n          || o.AllowedValues is { Count: > 1 })) {\n      var best = ArchiveCompressionOptimizer.Optimize(inputPath, outputPath, archiveOps, creator, schema);\n      return (best.OriginalSize, best.OptimizedSize, best.EntriesOptimized);\n    }\n\n    // No searchable encoding/layout parameter exists. Preserve the old behavior:\n    // output is a byte-identical copy and EntriesOptimized=0, so callers do not\n    // mistake a no-op for an optimization.\n    AtomicFileWriter.WriteAtomic(outputPath, outFs => {\n      using var inFs = File.OpenRead(inputPath);\n      inFs.CopyTo(outFs);\n    });\n    return (originalSize, originalSize, 0);'''
if old_fallback in v:
    v = v.replace(old_fallback, new_fallback, 1)
elif 'Generic multi-entry archive parameter search' not in v:
    raise SystemExit('ArchiveOperations.cs: optimize fallback block not found')
write(archive_ops_path, v)

layout_path = Path('Compression.Registry/ILayoutOptimizable.cs')
v = read(layout_path)
v = v.replace('Opt-in capability: a filesystem descriptor can analyse its current on-disk layout',
              'Opt-in capability: a filesystem, archive, or container descriptor can analyse its current layout')
v = v.replace('The descriptor can be rebuilt with a different geometry / allocation-unit choice.',
              'The descriptor can be rebuilt with a different geometry, allocation-unit, compression, dictionary, solid-block, or other layout/encoding choice.')
write(layout_path, v)

# ── Bulk interface promotion, guarded by existing working prerequisites ─────
class_re = re.compile(
    r'(public\s+(?:(?:sealed|partial|abstract)\s+)*class\s+\w+FormatDescriptor\s*:\s*)([^\{]+)(\{)',
    re.S)


def add_interface(path: Path, interface: str, predicate) -> bool:
    value = read(path)
    match = class_re.search(value)
    if not match:
        return False
    interfaces = match.group(2)
    if interface in interfaces or not predicate(value, interfaces):
        return False
    replacement = match.group(1) + interfaces.rstrip() + ', ' + interface + ' ' + match.group(3)
    value = value[:match.start()] + replacement + value[match.end():]
    write(path, value)
    return True


def has(*names):
    return lambda _value, interfaces: all(name in interfaces for name in names)

promoted = {'defrag': 0, 'optimize': 0, 'wipe': 0}
for base in (Path('FileFormats'), Path('FileSystems')):
    for path in base.rglob('*FormatDescriptor.cs'):
        if add_interface(path, 'IArchiveDefragmentable', has('IArchiveFormatOperations', 'IArchiveCreatable')):
            promoted['defrag'] += 1
        if add_interface(path, 'ILayoutOptimizable',
                         lambda value, interfaces: 'IArchiveFormatOperations' in interfaces
                         and 'IArchiveCreatable' in interfaces
                         and 'IFormatOptionsSchema' in interfaces
                         and ('AllowedValues:' in value or 'FormatOptionKind.Boolean' in value)):
            promoted['optimize'] += 1
        if add_interface(path, 'IWipeEmpty',
                         lambda _value, interfaces: 'IFilesystemExtentMap' in interfaces or 'IArchiveLayoutMap' in interfaces):
            promoted['wipe'] += 1

print('Promoted:', promoted)

# ── UI gates now use the same explicit contracts as documentation/tests ─────
main_vm = Path('Compression.UI/ViewModels/MainViewModel.cs')
v = read(main_vm)
v = v.replace('Views.MaintenanceVerb.Optimize => ops is IArchiveCreatable or IFileInternalChunkMover,',
              'Views.MaintenanceVerb.Optimize => ops is ILayoutOptimizable or IFileInternalChunkMover,')
v = v.replace('Views.MaintenanceVerb.Purge => ops is IArchiveModifiable,',
              'Views.MaintenanceVerb.Purge => ops is IArchivePurgeable,')
v = v.replace('Views.MaintenanceVerb.WipeEmpty => ops is IWipeEmpty or IFilesystemExtentMap or IArchiveLayoutMap,',
              'Views.MaintenanceVerb.WipeEmpty => ops is IWipeEmpty,')
write(main_vm, v)

window = Path('Compression.UI/Views/DefragmentWindow.xaml.cs')
v = read(window)
v = v.replace('var isArchiveCreatable = ops is IArchiveCreatable;',
              'var isArchiveCreatable = ops is IArchiveCreatable;\n    var isArchiveOptimizable = ops is ILayoutOptimizable && isArchiveCreatable;')
# Make a specifically requested Optimize verb win over a descriptor that also
# exposes defrag (common for filesystems after this promotion).
old_if = '''    if (this._defragmentable != null) {\n      // FS defrag path (existing)'''
new_if = '''    if (this._requestedVerb == MaintenanceVerb.Optimize && isArchiveOptimizable) {\n      this._isArchiveMode = true;\n      this._isSevenZipFormat = format.ToString() == "SevenZip";\n      this._archiveOps = ops;\n      SupportLbl.Text = "Container optimization (verified parameter search / repack).";\n      SupportLbl.Foreground = System.Windows.Media.Brushes.DarkGreen;\n      RunBtn.Content = "Optimize";\n      RunBtn.IsEnabled = true;\n    } else if (this._defragmentable != null) {\n      // FS defrag path (existing)'''
if old_if in v:
    v = v.replace(old_if, new_if, 1)
v = v.replace('} else if (isArchiveLayout || isArchiveCreatable) {',
              '} else if (isArchiveLayout || isArchiveOptimizable) {')
v = v.replace('      if (isArchiveCreatable) {\n        SupportLbl.Text = "Archive optimization (extract + repack with optimal settings).";',
              '      if (isArchiveOptimizable) {\n        SupportLbl.Text = "Archive optimization (verified parameter search / optimal repack).";')
v = v.replace('var supportsWipe = ops is IWipeEmpty || ops is IFilesystemExtentMap || ops is IArchiveLayoutMap;',
              'var supportsWipe = ops is IWipeEmpty;')
v = v.replace('PurgeBtn.IsEnabled = ops is IArchiveModifiable;', 'PurgeBtn.IsEnabled = ops is IArchivePurgeable;')
v = v.replace('  /// <see cref="IArchiveModifiable.Remove"/> over every entry. Distinct from',
              '  /// <see cref="IArchivePurgeable.Purge"/>. Distinct from')
v = v.replace('    if (ops is not IArchiveModifiable) {\n      Append($"Purge not supported: {formatStr} is not modifiable.");',
              '    if (ops is not IArchivePurgeable purgeable) {\n      Append($"Purge not supported: {formatStr} does not expose IArchivePurgeable.");')
v = v.replace('        // Remove files first, then directories (deepest paths last avoids\n        // a modifier rejecting a non-empty directory removal).\n        Compression.Lib.ArchiveOperations.Remove(path, allNames);',
              '        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);\n        purgeable.Purge(stream);')
write(window, v)

# CLI wipe should likewise expose only the explicit marker; the map-backed
# default means no supported format loses the generic path.
cli = Path('Compression.CLI/Program.cs')
v = read(cli)
# Narrow the wipe fallback branches: they are now supplied by IWipeEmpty default.
start = v.find('var wipeCmd = new Command("wipe-empty"')
end = v.find('// ── compact', start)
if start >= 0 and end > start:
    block = v[start:end]
    block = block.replace('  For formats without a dedicated implementation but with an extent/layout\n  map, the generic wiper zeros all gaps between live extents.\n',
                          '  Layout-mapped formats inherit the generic IWipeEmpty implementation, which\n  zeros gaps between all live extents.\n')
    # Keep runtime fallbacks for backwards compatibility; interface promotion
    # makes them unreachable for shipped descriptors, so no behavior regresses.
    v = v[:start] + block + v[end:]
write(cli, v)

# ── Documentation: generate exhaustive checkmark matrices from source ───────
id_re = re.compile(r'public\s+string\s+Id\s*=>\s*"([^"]+)"')


def descriptor_rows(base: Path, archive_only: bool) -> list[dict[str, object]]:
    rows = []
    for path in base.rglob('*FormatDescriptor.cs'):
        value = read(path)
        m = class_re.search(value)
        mid = id_re.search(value)
        if not m or not mid:
            continue
        interfaces = m.group(2)
        if archive_only and 'IArchiveFormatOperations' not in interfaces and 'IStreamFormatOperations' not in interfaces:
            continue
        row = {
            'id': mid.group(1),
            'optimize': 'ILayoutOptimizable' in interfaces or ('IStreamFormatOperations' in interfaces and 'IFormatOptionsSchema' in interfaces),
            'wipe': 'IWipeEmpty' in interfaces,
            'purge': 'IArchivePurgeable' in interfaces or 'IArchiveModifiable' in interfaces,
            'defrag': 'IArchiveDefragmentable' in interfaces,
            'shrink': 'IArchiveShrinkable' in interfaces,
            'compact': ('IArchiveDefragmentable' in interfaces or 'IArchiveShrinkable' in interfaces or 'IArchiveCreatable' in interfaces),
        }
        rows.append(row)
    dedup = {}
    for row in rows:
        dedup[row['id']] = row
    return sorted(dedup.values(), key=lambda r: str(r['id']).lower())


def mark(value: bool) -> str:
    return '✅' if value else '—'


def matrix(rows: list[dict[str, object]], full: bool = False) -> str:
    if full:
        lines = [
            '| Format | Optimize | Wipe / clean | Purge | Defrag | Shrink | Compact |',
            '|---|:---:|:---:|:---:|:---:|:---:|:---:|',
        ]
        for r in rows:
            lines.append(f"| {r['id']} | {mark(r['optimize'])} | {mark(r['wipe'])} | {mark(r['purge'])} | {mark(r['defrag'])} | {mark(r['shrink'])} | {mark(r['compact'])} |")
        return '\n'.join(lines)
    lines = [
        '| Format | Optimize | Wipe / clean | Purge | Defrag |',
        '|---|:---:|:---:|:---:|:---:|',
    ]
    for r in rows:
        lines.append(f"| {r['id']} | {mark(r['optimize'])} | {mark(r['wipe'])} | {mark(r['purge'])} | {mark(r['defrag'])} |")
    return '\n'.join(lines)


def put_matrix(path: Path, rows: list[dict[str, object]]) -> None:
    value = read(path)
    start_marker = '<!-- maintenance-capabilities:start -->'
    end_marker = '<!-- maintenance-capabilities:end -->'
    section = f'''{start_marker}\n## Maintenance capability matrix\n\nThese columns are generated from the descriptor capability contracts. `Wipe / clean` means sanitizing unused/dead/reserved bytes while preserving the container size; removing reserve to make the file smaller is **Shrink/Compact**, not Wipe. Rebuild-backed Defrag/Optimize/Purge count only where the implementation is transactional/round-trip-verified.\n\n{matrix(rows)}\n\n{end_marker}'''
    if start_marker in value and end_marker in value:
        value = re.sub(re.escape(start_marker) + r'.*?' + re.escape(end_marker), section, value, flags=re.S)
    else:
        value = value.rstrip() + '\n\n' + section + '\n'
    write(path, value)

archive_rows = descriptor_rows(Path('FileFormats'), archive_only=True)
fs_rows = descriptor_rows(Path('FileSystems'), archive_only=False)
put_matrix(Path('Hawkynt.FileFormats.Archives/README.md'), archive_rows)
put_matrix(Path('Hawkynt.FileFormats.FileSystems/README.md'), fs_rows)

coverage = Path('docs/OPERATION_COVERAGE.md')
v = read(coverage)
fs_section = f'''## Filesystem descriptors\n\nGenerated from descriptor capability contracts. `Wipe / clean` preserves outer size; `Shrink`/`Compact` may remove reserved/free tail space.\n\n{matrix(fs_rows, full=True)}\n\n'''
archive_section = f'''## Archive / stream descriptors\n\nUnlike the old abbreviated table, this lists every archive/stream descriptor with the same maintenance columns. Solid-container Defrag means a verified re-layout/repack; Optimize includes compression/dictionary/solid-block parameter search when exposed by the format schema.\n\n{matrix(archive_rows, full=True)}\n\n'''
fs_start = v.find('## Filesystem descriptors')
archive_start = v.find('## Archive / stream descriptors', fs_start)
notes_start = v.find('## N/A notes', archive_start)
if fs_start < 0 or archive_start < 0 or notes_start < 0:
    raise SystemExit('docs/OPERATION_COVERAGE.md: expected section anchors not found')
v = v[:fs_start] + fs_section + archive_section + v[notes_start:]
# Reconcile the old no-interface wording now that purge is first-class.
v = v.replace('A dedicated **purge (empty-all)** verb has no interface yet; it is realised by\n  `IArchiveModifiable.Remove` over all entries (or a fresh empty `Create`). A\n  future `IArchivePurgeable` could formalise it.',
              '**purge (empty-all)** is explicitly exposed by `IArchivePurgeable`; fully\n  modifiable containers inherit that contract and the generic implementation stages and\n  verifies the purge before replacing the original.')
write(coverage, v)

model = Path('docs/ARCHIVE-MODEL.md')
v = read(model)
v = v.replace('`IArchiveModifiable.Remove` over all entries (or an empty `IArchiveCreatable.Create` *(no dedicated `IArchivePurgeable` yet — see Naming note)*',
              '`IArchivePurgeable.Purge` (transactional default for `IArchiveModifiable`)')
v = v.replace('A dedicated **purge (empty-all)** verb has no interface yet; it is realised by\n  `IArchiveModifiable.Remove` over all entries (or a fresh empty `Create`). A\n  future `IArchivePurgeable` could formalise it.',
              '**purge (empty-all)** is `IArchivePurgeable`. `IArchiveModifiable` inherits it;\n  the default purge mutates a staged copy, verifies that every original live entry is gone,\n  and only then replaces the source.')
v = v.replace('Find and apply the **best parameter set** for the data (cluster/block/inode size, geometry, alignment).',
              'Find and apply the **best parameter set** for the data (cluster/block/inode size, geometry, alignment, compression method/level, dictionary and solid-block grouping).')
write(model, v)

# Test source comments should no longer claim defrag is filesystem-only.
defrag_test = Path('Compression.Tests/Operations/GenericDefragRoundTripTests.cs')
v = read(defrag_test)
v = v.replace('filesystem descriptor', 'archive/filesystem descriptor')
write(defrag_test, v)

# Remove this one-shot machinery from the resulting product commit.
Path('.github/maintenance-capabilities.py').unlink(missing_ok=True)
Path('.github/workflows/maintenance-capabilities-once.yml').unlink(missing_ok=True)

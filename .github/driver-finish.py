from pathlib import Path


def replace(path: str, old: str, new: str, count: int = 1) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    found = text.count(old)
    if found != count:
        raise SystemExit(f"{path}: expected {count} occurrence(s), found {found}: {old[:100]!r}")
    p.write_text(text.replace(old, new, count), encoding="utf-8")


# Generic archive-backed filesystem sessions must never reintroduce the array
# ceiling. The snapshot is independently reopenable, so spool decoded bytes to
# the shared positional-handle helper rather than materialising a byte[].
replace(
    "Compression.Registry/FilesystemDriverDerivation.cs",
    '''    using var archive = _snapshot.OpenRead();
    using var entry = _operations.OpenEntry(archive, node.Entry.Name, _password);
    using var memory = new MemoryStream();
    entry.CopyTo(memory);
    return new DerivedReadOnlyFileHandle(node.Id, memory.ToArray());''',
    '''    var backing = node.Entry;
    return SpoolingReadOnlyFileHandle.Create(
      node.Id,
      Math.Max(0, backing.OriginalSize),
      output => {
        using var archive = _snapshot.OpenRead();
        using var entry = _operations.OpenEntry(archive, backing.Name, _password);
        entry.CopyTo(output);
      });''')

# A read-only mounted profile has no implemented publication/durability model.
# Do not infer one from the on-disk format or from offline archive mutators.
replace(
    "FileSystems/FileSystem.Fat/FatFilesystemDriverAdapter.cs",
    '''        FilesystemMutationModel.Direct,
        CanMount: true,
        CanMountWritable: false,''',
    '''        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,''')
replace(
    "FileSystems/FileSystem.Fat/FatFilesystemDriverAdapter.cs",
    '''    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.AllocationMap |
        FilesystemDriverReadinessLayer.DurabilityModel
      : FilesystemDriverReadinessLayer.None;''',
    '''    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.AllocationMap
      : FilesystemDriverReadinessLayer.None;''')

replace(
    "FileSystems/FileSystem.Ext/ExtFilesystemDriverAdapter.cs",
    '''      var hasJournal = (super.FeatureCompat & ExtDriverSuperblock.CompatHasJournal) != 0;
      return new FilesystemDriverProfile(''',
    '''      return new FilesystemDriverProfile(''')
replace(
    "FileSystems/FileSystem.Ext/ExtFilesystemDriverAdapter.cs",
    '''        hasJournal ? FilesystemMutationModel.Journaled : FilesystemMutationModel.Direct,
        CanMount: true,
        CanMountWritable: false,''',
    '''        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,''')
replace(
    "FileSystems/FileSystem.Ext/ExtFilesystemDriverAdapter.cs",
    '''    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.NativeStableNodeIds |
        FilesystemDriverReadinessLayer.AllocationMap |
        FilesystemDriverReadinessLayer.DurabilityModel
      : FilesystemDriverReadinessLayer.None;''',
    '''    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.NativeStableNodeIds |
        FilesystemDriverReadinessLayer.AllocationMap
      : FilesystemDriverReadinessLayer.None;''')

# This file and its workflow are deliberately one-shot repository surgery.
Path(".github/driver-finish.py").unlink(missing_ok=True)
Path(".github/workflows/driver-finish-once.yml").unlink(missing_ok=True)

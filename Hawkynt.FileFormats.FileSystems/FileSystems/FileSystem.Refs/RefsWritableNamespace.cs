#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

internal sealed record RefsWritableEntryLocation(
  string Path,
  ulong ParentDirectoryOid,
  RefsPageReference ParentDirectoryRoot,
  RefsBTreeRow EntryRow);

internal sealed record RefsWritableStorageLocation(
  RefsWritableEntryLocation Entry,
  ulong StorageObjectId,
  RefsPageReference StorageRoot,
  RefsBTreeRow StorageRow,
  bool UsesBackingRow);

/// <summary>
/// Resolves a slash-separated live path back to its type-0x30 row and, for
/// writable file data, to the exact object/root/row that owns the $DATA value.
/// Non-resident type-0x30 entries are followed to their type-0x40 backing row;
/// resident and upgraded-inline entries remain in the parent directory tree.
/// </summary>
internal sealed class RefsWritableNamespace {
  private readonly RefsMetadataReader _metadata;
  private readonly Dictionary<ulong, RefsPageReference> _objects = [];

  public RefsWritableNamespace(RefsMetadataReader metadata) {
    this._metadata = metadata;
    foreach (var row in metadata.WalkRoot(0)) {
      if (row.Key.Length < 16 || row.Value.Length < 0x40) continue;
      var oid = BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8));
      var reference = RefsPageReference.Parse(row.Value.AsSpan(0x20));
      if (reference.Lcns.Count > 0) this._objects[oid] = reference;
    }
  }

  public RefsBTreeRow FindDirectoryEntry(string path)
    => this.ResolveDirectoryEntry(path).EntryRow;

  public RefsWritableEntryLocation ResolveDirectoryEntry(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) throw new InvalidOperationException("ReFS path does not name an entry.");

    ulong directoryOid = 0x600;
    for (var partIndex = 0; partIndex < parts.Length; ++partIndex) {
      if (!this._objects.TryGetValue(directoryOid, out var root))
        throw new InvalidOperationException($"ReFS directory OID 0x{directoryOid:X} is not reachable.");

      RefsBTreeRow? match = null;
      foreach (var row in this._metadata.WalkTree(root, virtualAddresses: true)) {
        if (row.Key.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key) != 0x30) continue;
        var name = DecodeName(row.Key.AsSpan(4));
        if (name.Equals(parts[partIndex], StringComparison.OrdinalIgnoreCase)) {
          match = row;
          break;
        }
      }
      if (match == null)
        throw new InvalidOperationException($"ReFS path '{path}' could not be resolved at '{parts[partIndex]}'.");
      if (partIndex == parts.Length - 1)
        return new RefsWritableEntryLocation(path, directoryOid, root, match);
      if (match.Value.Length < 0x10)
        throw new InvalidOperationException($"ReFS path component '{parts[partIndex]}' has no child OID.");
      var attributes = match.Value.Length >= 0x44
        ? BinaryPrimitives.ReadUInt32LittleEndian(match.Value.AsSpan(0x40, 4))
        : 0U;
      if ((attributes & 0x10000000) == 0)
        throw new InvalidOperationException($"ReFS path component '{parts[partIndex]}' is not a directory.");
      directoryOid = BinaryPrimitives.ReadUInt64LittleEndian(match.Value.AsSpan(8, 8));
    }

    throw new InvalidOperationException($"ReFS path '{path}' could not be resolved.");
  }

  public RefsWritableStorageLocation ResolveStorage(string path) {
    var entry = this.ResolveDirectoryEntry(path);
    var row = entry.EntryRow;
    if (row.Key.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key.AsSpan(0, 2)) != 0x30)
      throw new InvalidDataException($"ReFS path '{path}' did not resolve to a filename row.");
    var keyFlags = BinaryPrimitives.ReadUInt16LittleEndian(row.Key.AsSpan(2, 2));

    // Long/resident and upgraded extent-holder values are physically owned by
    // the directory object's B+ row itself.
    if (keyFlags == 0x01)
      return new RefsWritableStorageLocation(
        entry,
        entry.ParentDirectoryOid,
        entry.ParentDirectoryRoot,
        row,
        UsesBackingRow: false);

    if (keyFlags != 0x02 || row.Value.Length is < 0x48 or > 0x54)
      throw new NotSupportedException(
        $"ReFS path '{path}' uses unsupported type-0x30 key flags 0x{keyFlags:X4}/value size {row.Value.Length}.");

    var attributes = BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x40, 4));
    if ((attributes & 0x10000000) != 0)
      throw new InvalidOperationException($"ReFS path '{path}' names a directory, not a writable data stream.");

    var fileId = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x00, 8));
    var homeOid = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x08, 8));
    var expectedSize = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x38, 8));
    if (!this._objects.TryGetValue(homeOid, out var homeRoot))
      throw new InvalidDataException($"ReFS backing object OID 0x{homeOid:X} for '{path}' is not reachable.");

    var candidates = new List<RefsBTreeRow>();
    foreach (var backing in this._metadata.WalkTree(homeRoot, virtualAddresses: true)) {
      if (backing.Key.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(backing.Key.AsSpan(0, 2)) != 0x40)
        continue;
      if (BinaryPrimitives.ReadUInt64LittleEndian(backing.Key.AsSpan(8, 8)) != fileId) continue;
      if (backing.Value.Length < 0x68) continue;
      candidates.Add(backing);
    }
    if (candidates.Count == 0)
      throw new InvalidDataException($"ReFS backing row for '{path}' / file ID 0x{fileId:X} is missing.");

    var exact = candidates.Where(candidate =>
      BinaryPrimitives.ReadUInt64LittleEndian(candidate.Value.AsSpan(0x58, 8)) == expectedSize).ToArray();
    var selected = exact.Length switch {
      1 => exact[0],
      > 1 => throw new InvalidDataException($"ReFS backing row for '{path}' is ambiguous even after size matching."),
      _ when candidates.Count == 1 => candidates[0],
      _ => throw new InvalidDataException($"ReFS backing row for '{path}' is ambiguous ({candidates.Count} candidates)."),
    };

    return new RefsWritableStorageLocation(entry, homeOid, homeRoot, selected, UsesBackingRow: true);
  }

  public RefsPageReference GetObjectRoot(ulong objectId)
    => this._objects.TryGetValue(objectId, out var root)
      ? root
      : throw new InvalidDataException($"ReFS object OID 0x{objectId:X} is not reachable from Object Table.");

  private static string DecodeName(ReadOnlySpan<byte> bytes) {
    if ((bytes.Length & 1) != 0) bytes = bytes[..^1];
    return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
  }
}

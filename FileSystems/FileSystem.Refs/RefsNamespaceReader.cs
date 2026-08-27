#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Walks the active ReFS namespace from the Object Table. Supports resident
/// files and ordinary extent-backed streams; malformed or unsupported stream
/// layouts are left unresolved instead of returning guessed data.
/// </summary>
internal sealed class RefsNamespaceReader {
  private const ulong RootDirectoryOid = 0x600;
  private readonly RefsMetadataReader _metadata;
  private readonly Dictionary<ulong, RefsPageReference> _objects = [];
  private readonly Dictionary<ulong, Dictionary<ulong, List<RefsBackingRecord>>> _backingCache = [];

  public RefsNamespaceReader(RefsMetadataReader metadata) {
    this._metadata = metadata;
    this.BuildObjectMap();
  }

  public IReadOnlyList<RefsFileRecord> ReadAll() {
    var result = new List<RefsFileRecord>();
    var visitedDirectories = new HashSet<ulong>();
    this.WalkDirectory(RootDirectoryOid, "", result, visitedDirectories, 0);
    return result;
  }

  private void BuildObjectMap() {
    foreach (var row in this._metadata.WalkRoot(0)) {
      if (row.Key.Length < 16 || row.Value.Length < 0x40) continue;
      var oid = BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8));
      var reference = RefsPageReference.Parse(row.Value.AsSpan(0x20));
      if (reference.Lcns is { Count: > 0 }) this._objects[oid] = reference;
    }
  }

  private void WalkDirectory(
      ulong oid,
      string parentPath,
      List<RefsFileRecord> output,
      HashSet<ulong> visited,
      int depth) {
    if (depth > 512 || !visited.Add(oid) || !this._objects.TryGetValue(oid, out var root)) return;

    List<RefsBTreeRow> rows;
    try {
      rows = this._metadata.WalkTree(root, virtualAddresses: true).ToList();
    } catch (InvalidDataException) {
      return;
    }

    var localBacking = ParseBackingRows(rows);
    this._backingCache[oid] = localBacking;

    foreach (var row in rows) {
      if (row.Key.Length < 4 || ReadU16(row.Key, 0) != 0x30) continue;
      var keyFlags = ReadU16(row.Key, 2);
      var name = DecodeName(row.Key.AsSpan(4));
      if (string.IsNullOrEmpty(name) || name is "." or "..") continue;
      var path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;

      if (keyFlags == 0x02 && row.Value.Length is >= 0x48 and <= 0x54) {
        var fileId = ReadU64(row.Value, 0x00);
        var homeOid = ReadU64(row.Value, 0x08);
        var modified = ToDateTime(ReadU64(row.Value, 0x18));
        var allocated = ReadU64(row.Value, 0x30);
        var size = ReadU64(row.Value, 0x38);
        var attributes = ReadU32(row.Value, 0x40);
        var isDirectory = (attributes & 0x10000000) != 0;

        if (isDirectory) {
          output.Add(new RefsFileRecord(path, true, false, 0, 0, modified, attributes, null, [], null));
          // For directory entries value+0x08 is the child directory's own OID.
          this.WalkDirectory(homeOid, path, output, visited, depth + 1);
          continue;
        }

        var backing = this.FindBacking(homeOid, fileId, size);
        var extents = backing?.Extents ?? [];
        output.Add(new RefsFileRecord(
          path,
          false,
          false,
          checked((long)Math.Min(size, long.MaxValue)),
          checked((long)Math.Min(allocated, long.MaxValue)),
          modified,
          attributes,
          null,
          extents,
          backing));
        continue;
      }

      if (keyFlags == 0x01 && row.Value.Length > 0x54) {
        var attributes = row.Value.Length >= 0x4C ? ReadU32(row.Value, 0x48) : 0;
        var modified = row.Value.Length >= 0x38 ? ToDateTime(ReadU64(row.Value, 0x30)) : null;
        var content = TryGetResidentContent(row.Value);
        var nominalSize = row.Value.Length >= 0x60 ? ReadU64(row.Value, 0x58) : 0;
        var nominalAllocated = row.Value.Length >= 0x68 ? ReadU64(row.Value, 0x60) : 0;

        // Older/upgraded ReFS can retain a long directory value after the data
        // has become extent-backed. Decode it with the same guarded extent
        // parser used by a type-0x40 backing record; only accept an exact VCN
        // cover of the advertised allocation.
        var holderExtents = content == null
          ? TryDecodeExtents(row.Value, this._metadata.ClusterSize, this._metadata)
          : [];
        var extentBacked = holderExtents.Count > 0;
        var size = content != null ? content.LongLength : checked((long)Math.Min(nominalSize, long.MaxValue));
        var allocated = extentBacked
          ? checked((long)Math.Min(nominalAllocated, long.MaxValue))
          : content?.LongLength ?? checked((long)Math.Min(nominalAllocated, long.MaxValue));

        output.Add(new RefsFileRecord(
          path,
          false,
          !extentBacked,
          size,
          allocated,
          modified,
          attributes,
          extentBacked ? null : content,
          holderExtents,
          null));
      }
    }
  }

  private RefsBackingRecord? FindBacking(ulong homeOid, ulong fileId, ulong expectedSize) {
    if (!this._backingCache.TryGetValue(homeOid, out var map)) {
      if (!this._objects.TryGetValue(homeOid, out var root)) return null;
      try {
        map = ParseBackingRows(this._metadata.WalkTree(root, virtualAddresses: true));
      } catch (InvalidDataException) {
        return null;
      }
      this._backingCache[homeOid] = map;
    }

    if (!map.TryGetValue(fileId, out var candidates) || candidates.Count == 0) return null;
    if (candidates.Count == 1) return candidates[0];
    return candidates.FirstOrDefault(c => (ulong)c.FileSize == expectedSize) ?? candidates[0];
  }

  private Dictionary<ulong, List<RefsBackingRecord>> ParseBackingRows(IEnumerable<RefsBTreeRow> rows) {
    var result = new Dictionary<ulong, List<RefsBackingRecord>>();
    foreach (var row in rows) {
      if (row.Key.Length < 16 || ReadU16(row.Key, 0) != 0x40 || row.Value.Length < 0x68) continue;
      var fileId = ReadU64(row.Key, 8);
      var fileSize = checked((long)Math.Min(ReadU64(row.Value, 0x58), long.MaxValue));
      var allocated = checked((long)Math.Min(ReadU64(row.Value, 0x60), long.MaxValue));
      var extents = TryDecodeExtents(row.Value, this._metadata.ClusterSize, this._metadata);
      var backing = new RefsBackingRecord(fileId, fileSize, allocated, row, extents);
      if (!result.TryGetValue(fileId, out var list)) result[fileId] = list = [];
      list.Add(backing);
    }
    return result;
  }

  private static IReadOnlyList<RefsDataExtent> TryDecodeExtents(
      byte[] value,
      int clusterSize,
      RefsMetadataReader metadata) {
    if (value.Length < 0x68 || clusterSize <= 0) return [];
    var allocated = ReadU64(value, 0x60);
    if (allocated == 0 || allocated % (ulong)clusterSize != 0) return [];
    var requiredClusters = allocated / (ulong)clusterSize;

    // Native 3.10+ holder/sub-record layout. The sub-records begin at +0xA8;
    // each candidate extent table is accepted only when its VCNs form exactly
    // [0, allocatedClusters), which prevents heuristic false positives from
    // being exposed as file content.
    for (var recordOffset = 0xA8; recordOffset + 4 <= value.Length;) {
      var recordSize = checked((int)ReadU32(value, recordOffset));
      if (recordSize <= 0 || recordSize > 4096 || recordOffset + recordSize > value.Length) break;
      var decoded = TryFindExtentTable(value, recordOffset, recordSize, requiredClusters, metadata);
      if (decoded.Count > 0) return decoded;
      recordOffset += recordSize;
    }

    // Upgraded v3.4-v3.10 can keep an embedded B+ holder. Its live extent
    // entries are named by 4-byte offset-directory entries ending in 0xffff.
    // Scan the bounded holder and then require the same exact VCN cover.
    for (var holder = 0x80; holder + 8 <= value.Length; holder += 4) {
      var marker = ReadU32(value, holder);
      if (marker is not (0x80000001 or 0x80000002) || ReadU32(value, holder + 4) != 0x00010028) continue;
      var node = holder + checked((int)ReadU32(value, holder));
      if (node <= holder || node >= value.Length) continue;
      var extents = new Dictionary<(uint Vcn, ulong Vlcn, uint Run, uint Flags), RefsDataExtent>();
      for (var p = node; p + 4 <= value.Length; p += 2) {
        if (ReadU16(value, p + 2) != 0xFFFF) continue;
        var rel = ReadU16(value, p);
        if (rel < 0x20 || rel >= 0x200) continue;
        var e = node + rel;
        if (e + 24 > value.Length) continue;
        var flags = ReadU32(value, e + 8);
        if ((flags & 0x04) != 0) continue;
        var run = ReadU32(value, e + 0x14);
        var vcn = ReadU32(value, e + 0x0C);
        var vlcn = ReadU64(value, e);
        if (run == 0 || (vlcn == 0 && (flags & 0x20) == 0)) continue;
        if (!TryMakeExtent(metadata, vcn, vlcn, run, flags, e, out var extent)) continue;
        extents[(vcn, vlcn, run, flags)] = extent;
      }
      var checkedExtents = ExactCover(extents.Values, requiredClusters);
      if (checkedExtents.Count > 0) return checkedExtents;
    }

    return [];
  }

  private static IReadOnlyList<RefsDataExtent> TryFindExtentTable(
      byte[] value,
      int recordOffset,
      int recordSize,
      ulong requiredClusters,
      RefsMetadataReader metadata) {
    var recordEnd = recordOffset + recordSize;
    for (var p = recordOffset + 4; p + 24 <= recordEnd; p += 4) {
      var start = ReadU32(value, p);
      var end = ReadU32(value, p + 4);
      var capacity = ReadU32(value, p + 12);
      var count = ReadU32(value, p + 20);
      if (start is < 0x10 or > 0x200 || end <= start || count is 0 or > 100000 || capacity < 0x100) continue;
      if ((ulong)(end - start) < (ulong)count * 24) continue;
      var entriesOffset = p + checked((int)start);
      if (entriesOffset < 0 || entriesOffset >= value.Length) continue;

      var extents = new List<RefsDataExtent>(checked((int)Math.Min(count, 4096u)));
      var cursor = entriesOffset;
      for (var i = 0u; i < count && cursor + 24 <= recordEnd; ++i) {
        var vlcn = ReadU64(value, cursor);
        var flags = ReadU32(value, cursor + 8);
        var vcn = ReadU32(value, cursor + 0x0C);
        var run = ReadU32(value, cursor + 0x14);
        if (run == 0) { extents.Clear(); break; }
        if (!TryMakeExtent(metadata, vcn, vlcn, run, flags, cursor, out var extent)) { extents.Clear(); break; }
        extents.Add(extent);
        cursor += flags == 0x1C00D0 ? 32 : 24;
      }
      var exact = ExactCover(extents, requiredClusters);
      if (exact.Count > 0) return exact;
    }
    return [];
  }

  private static bool TryMakeExtent(
      RefsMetadataReader metadata,
      uint fileVcn,
      ulong virtualLcn,
      uint runLength,
      uint flags,
      int valueRelativeOffset,
      out RefsDataExtent extent) {
    if ((flags & 0x20) != 0 && virtualLcn == 0) {
      extent = new RefsDataExtent(fileVcn, 0, 0, runLength, flags, true, valueRelativeOffset);
      return true;
    }
    try {
      var physical = metadata.TranslateVirtualLcn(virtualLcn);
      extent = new RefsDataExtent(fileVcn, virtualLcn, physical, runLength, flags, false, valueRelativeOffset);
      return true;
    } catch (InvalidDataException) {
      extent = default!;
      return false;
    }
  }

  private static IReadOnlyList<RefsDataExtent> ExactCover(IEnumerable<RefsDataExtent> source, ulong requiredClusters) {
    var list = source.OrderBy(e => e.FileVcn).ToList();
    if (list.Count == 0) return [];
    ulong cursor = 0;
    foreach (var extent in list) {
      if (extent.FileVcn != cursor) return [];
      cursor += extent.ClusterCount;
      if (cursor > requiredClusters) return [];
    }
    return cursor == requiredClusters ? list : [];
  }

  private static byte[]? TryGetResidentContent(byte[] value) {
    foreach (var (key, rowValue) in ParseEmbeddedRows(value)) {
      if (key.Length < 14) continue;
      if (ReadU32(key, 8) != 0x80000001 || key[12] != 0x80 || key[13] != 0) continue;
      for (var scan = 0; scan + 8 <= rowValue.Length; ++scan) {
        if (ReadU32(rowValue, scan) != 0x0C || ReadU32(rowValue, scan + 4) != 0x30) continue;
        var header = scan - 4;
        if (header < 0 || header + 0x38 > rowValue.Length) return null;
        if (ReadU32(rowValue, header + 0x0C) != 0) return null;
        var size = ReadU64(rowValue, header + 0x1C);
        if (size > int.MaxValue) return null;
        var contentOffset = header + 0x38;
        if (contentOffset + (long)size > rowValue.Length) return null;
        return rowValue.AsSpan(contentOffset, checked((int)size)).ToArray();
      }
    }
    return null;
  }

  private static IEnumerable<(byte[] Key, byte[] Value)> ParseEmbeddedRows(byte[] value) {
    if (value.Length < 0xC0) yield break;
    var @base = checked((int)ReadU32(value, 0));
    if (@base < 0x28 || @base >= value.Length - 0x28) yield break;

    for (var index = value.Length - 4; index >= @base; index -= 4) {
      if (ReadU16(value, index + 2) != 0xFFFF) break;
      var rowOffset = @base + ReadU16(value, index);
      if (rowOffset + 16 > value.Length) continue;
      var rowSize = checked((int)ReadU32(value, rowOffset));
      var keyOffset = ReadU16(value, rowOffset + 4);
      var keyLength = ReadU16(value, rowOffset + 6);
      var valueOffset = ReadU16(value, rowOffset + 10);
      var valueLength = ReadU16(value, rowOffset + 12);
      if (rowSize < 16 || rowOffset + rowSize > value.Length) continue;
      if (keyOffset + keyLength > rowSize || valueOffset + valueLength > rowSize) continue;
      yield return (
        value.AsSpan(rowOffset + keyOffset, keyLength).ToArray(),
        value.AsSpan(rowOffset + valueOffset, valueLength).ToArray());
    }
  }

  private static string DecodeName(ReadOnlySpan<byte> bytes) {
    try { return Encoding.Unicode.GetString(bytes).TrimEnd('\0'); }
    catch { return Convert.ToHexString(bytes); }
  }

  private static DateTime? ToDateTime(ulong fileTime) {
    if (fileTime == 0 || fileTime > long.MaxValue) return null;
    try { return DateTime.FromFileTimeUtc((long)fileTime); }
    catch (ArgumentOutOfRangeException) { return null; }
  }

  private static ushort ReadU16(byte[] value, int offset)
    => offset >= 0 && offset + 2 <= value.Length ? BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(offset, 2)) : (ushort)0;
  private static uint ReadU32(byte[] value, int offset)
    => offset >= 0 && offset + 4 <= value.Length ? BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(offset, 4)) : 0;
  private static ulong ReadU64(byte[] value, int offset)
    => offset >= 0 && offset + 8 <= value.Length ? BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(offset, 8)) : 0;
}

internal sealed record RefsFileRecord(
  string Path,
  bool IsDirectory,
  bool IsResident,
  long Size,
  long AllocatedSize,
  DateTime? Modified,
  uint Attributes,
  byte[]? ResidentContent,
  IReadOnlyList<RefsDataExtent> Extents,
  RefsBackingRecord? Backing);

internal sealed record RefsBackingRecord(
  ulong FileId,
  long FileSize,
  long AllocatedSize,
  RefsBTreeRow Row,
  IReadOnlyList<RefsDataExtent> Extents);

internal sealed record RefsDataExtent(
  uint FileVcn,
  ulong VirtualLcn,
  ulong PhysicalLcn,
  uint ClusterCount,
  uint Flags,
  bool IsSparse,
  int ValueRelativeOffset);

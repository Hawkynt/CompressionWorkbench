#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal readonly record struct RefsExtentSpec(uint FileVcn, ulong VirtualLcn, uint ClusterCount);

/// <summary>
/// Rewrites the live $DATA allocation metadata while keeping the owning outer
/// B+ row/key stable.  Resident files are promoted by shrinking their SI $DATA
/// value to its stream summary and populating the already-present live MI $DATA
/// allocation row.  Ordinary non-resident holders grow their existing extent
/// table.  No outer-row insertion is required.
/// </summary>
internal static class RefsStreamLayoutEditor {
  private sealed record MiniRow(uint EncodedIndex, ushort Flags, ushort Reserved, byte[] Key, byte[] Value);

  public static IReadOnlyList<RefsExtentSpec> BuildExtents(
      RefsMetadataReader metadata,
      IReadOnlyList<long> physicalBlockOffsets) {
    var clusterSize = metadata.ClusterSize;
    var result = new List<RefsExtentSpec>();
    uint fileVcn = 0;
    ulong? runStart = null;
    ulong previous = 0;
    uint runLength = 0;

    void Flush() {
      if (runStart is null || runLength == 0) return;
      result.Add(new RefsExtentSpec(fileVcn - runLength, runStart.Value, runLength));
      runStart = null;
      runLength = 0;
    }

    foreach (var offset in physicalBlockOffsets) {
      if (offset < 0 || offset % clusterSize != 0)
        throw new InvalidOperationException("ReFS relocation targets must be allocation-cluster aligned.");
      var physical = checked((ulong)(offset / clusterSize));
      if (!metadata.TryPhysicalToVirtualLcn(physical, out var virtualLcn))
        throw new InvalidOperationException($"ReFS physical LCN 0x{physical:X} has no virtual-container mapping.");

      if (runStart is null) {
        runStart = previous = virtualLcn;
        runLength = 1;
      } else if (virtualLcn == previous + 1 && runLength < uint.MaxValue) {
        previous = virtualLcn;
        ++runLength;
      } else {
        Flush();
        runStart = previous = virtualLcn;
        runLength = 1;
      }
      ++fileVcn;
    }
    Flush();
    return result;
  }

  public static byte[] BuildUpdatedValue(
      RefsFileRecord file,
      RefsBTreeRow storageRow,
      IReadOnlyList<RefsExtentSpec> extents,
      int clusterSize) {
    if (file.IsDirectory) throw new InvalidOperationException("ReFS directory rows are not file-data allocations.");
    if (file.Extents.Any(e => e.IsSparse || e.Flags == 0x1C00D0 || (e.Flags & 0x04) != 0))
      throw new NotSupportedException($"ReFS: '{file.Path}' uses sparse/integrity/shared allocation metadata that cannot be relaid safely.");
    if (extents.Count == 0 && file.Size != 0)
      throw new InvalidOperationException("A non-empty ReFS file cannot be relinked to an empty allocation.");

    var allocated = checked((ulong)extents.Sum(e => (long)e.ClusterCount) * (ulong)clusterSize);
    if ((ulong)Math.Max(0, file.Size) > allocated)
      throw new InvalidOperationException("ReFS replacement allocation is shorter than the file's logical size.");

    var original = storageRow.Value;
    if (TryRewriteEmbeddedData(original, file, extents, allocated, out var embedded))
      return embedded;
    if (TryRewriteNativeHolder(original, file, extents, allocated, out var holder))
      return holder;

    throw new NotSupportedException(
      $"ReFS: '{file.Path}' uses an extent-holder layout that this offline writer cannot rewrite without an outer B+ row insertion.");
  }

  private static bool TryRewriteEmbeddedData(
      byte[] original,
      RefsFileRecord file,
      IReadOnlyList<RefsExtentSpec> extents,
      ulong allocated,
      out byte[] updated) {
    updated = [];
    if (!TryParseMiniTree(original, out var @base, out var rows)) return false;

    var siIndex = -1;
    var miIndex = -1;
    for (var i = 0; i < rows.Count; ++i) {
      var key = rows[i].Key;
      if (key.Length < 16 || ReadU16(key, 0x0C) != 0x0080) continue;
      var marker = ReadU32(key, 8);
      if (marker == 0x80000001) siIndex = i;
      if (marker == 0x80000002 && key.Length >= 20) {
        var subId = ReadU32(key, 0x10);
        if (subId == 0x1000 || miIndex < 0) miIndex = i;
      }
    }
    if (miIndex < 0) return false;

    var mutable = rows.ToArray();
    var mi = mutable[miIndex];
    var newMiValue = BuildMiValue(mi.Value, file.Size, allocated, extents);
    var newMiKey = mi.Key.ToArray();
    if (newMiKey.Length >= 8)
      BinaryPrimitives.WriteUInt64LittleEndian(newMiKey.AsSpan(0, 8), checked((ulong)newMiValue.Length));
    mutable[miIndex] = mi with { Key = newMiKey, Value = newMiValue };

    if (file.IsResident) {
      if (siIndex < 0) return false;
      var si = mutable[siIndex];
      var newSiValue = BuildSiSummary(si.Value, file.Size, allocated);
      var newSiKey = si.Key.ToArray();
      if (newSiKey.Length >= 8)
        BinaryPrimitives.WriteUInt64LittleEndian(newSiKey.AsSpan(0, 8), checked((ulong)newSiValue.Length));
      mutable[siIndex] = si with { Key = newSiKey, Value = newSiValue };
    }

    updated = RepackMiniTree(original, @base, mutable);
    if (updated.Length >= 0x68) {
      BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(0x58, 8), checked((ulong)Math.Max(0, file.Size)));
      BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(0x60, 8), allocated);
    }
    return true;
  }

  private static byte[] BuildSiSummary(byte[] oldValue, long fileSize, ulong allocated) {
    const int length = 0x3C;
    var result = new byte[length];
    oldValue.AsSpan(0, Math.Min(oldValue.Length, length)).CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), length - 12);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), 0x0C);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x0C, 4), 0x30);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x18, 8), allocated);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x20, 8), checked((ulong)Math.Max(0, fileSize)));
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x28, 8), checked((ulong)Math.Max(0, fileSize)));
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x30, 8), allocated);
    return result;
  }

  private static byte[] BuildMiValue(
      byte[] oldValue,
      long fileSize,
      ulong allocated,
      IReadOnlyList<RefsExtentSpec> extents) {
    const int innerHeader = 0x88;
    const int extentHeader = 0x28;
    var length = checked(innerHeader + extentHeader + extents.Count * 24);
    var result = new byte[length];
    oldValue.AsSpan(0, Math.Min(oldValue.Length, innerHeader)).CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x00, 4), innerHeader);
    if (result.Length >= 8) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x04, 4), 0x00010028);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x0C, 4), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x2C, 4), extentHeader);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x30, 8), allocated);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x38, 8), checked((ulong)Math.Max(0, fileSize)));
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x40, 8), checked((ulong)Math.Max(0, fileSize)));
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x48, 8), allocated);
    var version = oldValue.Length >= 0x54 ? BinaryPrimitives.ReadUInt32LittleEndian(oldValue.AsSpan(0x50, 4)) : 1U;
    if ((version & 0x7FFFFFFF) > 1)
      throw new NotSupportedException("ReFS stream snapshots/CoW versions are not relaid in place.");
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x50, 4), 1U);

    var header = result.AsSpan(innerHeader, extentHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(header[0x00..0x04], extentHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..0x08], checked((uint)(extentHeader + extents.Count * 24)));
    BinaryPrimitives.WriteUInt32LittleEndian(header[0x0C..0x10], 0xE00);
    BinaryPrimitives.WriteUInt32LittleEndian(header[0x14..0x18], checked((uint)extents.Count));
    WriteExtents(result.AsSpan(innerHeader + extentHeader), extents);
    return result;
  }

  private static bool TryRewriteNativeHolder(
      byte[] original,
      RefsFileRecord file,
      IReadOnlyList<RefsExtentSpec> extents,
      ulong allocated,
      out byte[] updated) {
    updated = [];
    if (file.Extents.Count == 0) return false;
    if (file.Extents.Any(e => e.ValueRelativeOffset < 0 || e.Flags == 0x1C00D0)) return false;
    var firstEntry = file.Extents.Min(e => e.ValueRelativeOffset);

    for (var recordOffset = 0xA8; recordOffset + 4 <= original.Length;) {
      var recordSize = checked((int)ReadU32(original, recordOffset));
      if (recordSize <= 0 || recordOffset + recordSize > original.Length) break;
      var recordEnd = recordOffset + recordSize;
      for (var p = recordOffset + 4; p + 24 <= recordEnd; p += 4) {
        var start = checked((int)ReadU32(original, p));
        var end = checked((int)ReadU32(original, p + 4));
        var count = checked((int)ReadU32(original, p + 20));
        if (start < 0x10 || count <= 0) continue;
        var entriesOffset = p + start;
        if (entriesOffset != firstEntry || entriesOffset + count * 24 > recordEnd) continue;
        if (end - start < count * 24) continue;

        var oldEntriesEnd = entriesOffset + count * 24;
        var delta = checked((extents.Count - count) * 24);
        var newLength = checked(original.Length + delta);
        if (newLength <= 0) return false;
        updated = new byte[newLength];
        original.AsSpan(0, entriesOffset).CopyTo(updated);
        WriteExtents(updated.AsSpan(entriesOffset, extents.Count * 24), extents);
        original.AsSpan(oldEntriesEnd).CopyTo(updated.AsSpan(entriesOffset + extents.Count * 24));

        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(recordOffset, 4), checked((uint)(recordSize + delta)));
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(p + 4, 4), checked((uint)(start + extents.Count * 24)));
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(p + 20, 4), checked((uint)extents.Count));
        if (updated.Length >= 0x68) {
          BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(0x58, 8), checked((ulong)Math.Max(0, file.Size)));
          BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(0x60, 8), allocated);
        }
        return true;
      }
      recordOffset += recordSize;
    }
    return false;
  }

  private static void WriteExtents(Span<byte> destination, IReadOnlyList<RefsExtentSpec> extents) {
    if (destination.Length < extents.Count * 24)
      throw new ArgumentException("ReFS extent destination is too short.", nameof(destination));
    for (var i = 0; i < extents.Count; ++i) {
      var e = destination.Slice(i * 24, 24);
      e.Clear();
      BinaryPrimitives.WriteUInt64LittleEndian(e[0x00..0x08], extents[i].VirtualLcn);
      BinaryPrimitives.WriteUInt32LittleEndian(e[0x08..0x0C], 0x180040);
      BinaryPrimitives.WriteUInt32LittleEndian(e[0x0C..0x10], extents[i].FileVcn);
      BinaryPrimitives.WriteUInt32LittleEndian(e[0x14..0x18], extents[i].ClusterCount);
    }
  }

  private static bool TryParseMiniTree(byte[] value, out int @base, out List<MiniRow> rows) {
    @base = 0;
    rows = [];
    if (value.Length < 0xC0) return false;
    @base = checked((int)ReadU32(value, 0));
    if (@base < 0x28 || @base >= value.Length - 0x28) return false;

    var indexStart = value.Length;
    for (var p = value.Length - 4; p >= @base; p -= 4) {
      if (ReadU16(value, p + 2) != 0xFFFF) break;
      indexStart = p;
    }
    if (indexStart == value.Length) return false;

    for (var p = indexStart; p < value.Length; p += 4) {
      var encoded = ReadU32(value, p);
      if ((encoded >> 16) != 0xFFFF) return false;
      var rowOffset = @base + (int)(encoded & 0xFFFF);
      if (rowOffset + 16 > indexStart) return false;
      var rowSize = checked((int)ReadU32(value, rowOffset));
      if (rowSize < 16 || rowOffset + rowSize > indexStart) return false;
      var keyOffset = ReadU16(value, rowOffset + 4);
      var keyLength = ReadU16(value, rowOffset + 6);
      var flags = ReadU16(value, rowOffset + 8);
      var valueOffset = ReadU16(value, rowOffset + 10);
      var valueLength = ReadU16(value, rowOffset + 12);
      var reserved = ReadU16(value, rowOffset + 14);
      if (keyOffset + keyLength > rowSize || valueOffset + valueLength > rowSize) return false;
      rows.Add(new MiniRow(
        encoded,
        flags,
        reserved,
        value.AsSpan(rowOffset + keyOffset, keyLength).ToArray(),
        value.AsSpan(rowOffset + valueOffset, valueLength).ToArray()));
    }
    return rows.Count > 0;
  }

  private static byte[] RepackMiniTree(byte[] original, int @base, IReadOnlyList<MiniRow> rows) {
    var serialized = rows.Select(SerializeMiniRow).ToArray();
    var rowBytes = serialized.Sum(x => x.Length);
    var newLength = checked(@base + rowBytes + rows.Count * 4);
    var result = new byte[newLength];
    original.AsSpan(0, Math.Min(@base, original.Length)).CopyTo(result);
    var cursor = @base;
    for (var i = 0; i < rows.Count; ++i) {
      serialized[i].CopyTo(result, cursor);
      var rel = cursor - @base;
      if ((uint)rel > ushort.MaxValue) throw new InvalidOperationException("ReFS embedded row offset exceeds 16 bits.");
      var encoded = (rows[i].EncodedIndex & 0xFFFF0000U) | (uint)rel;
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(@base + rowBytes + i * 4, 4), encoded);
      cursor += serialized[i].Length;
    }
    return result;
  }

  private static byte[] SerializeMiniRow(MiniRow row) {
    var keyOffset = 16;
    var valueOffset = Align8(keyOffset + row.Key.Length);
    var rowSize = Align8(checked(valueOffset + row.Value.Length));
    if (row.Key.Length > ushort.MaxValue || row.Value.Length > ushort.MaxValue || valueOffset > ushort.MaxValue)
      throw new InvalidOperationException("ReFS embedded attribute row exceeds on-disk limits.");
    var result = new byte[rowSize];
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), checked((uint)rowSize));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), checked((ushort)keyOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6, 2), checked((ushort)row.Key.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8, 2), row.Flags);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10, 2), checked((ushort)valueOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12, 2), checked((ushort)row.Value.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14, 2), row.Reserved);
    row.Key.CopyTo(result, keyOffset);
    row.Value.CopyTo(result, valueOffset);
    return result;
  }

  private static int Align8(int value) => checked((value + 7) & ~7);
  private static ushort ReadU16(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 2 <= bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)) : (ushort)0;
  private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 4 <= bytes.Length ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)) : 0U;
}

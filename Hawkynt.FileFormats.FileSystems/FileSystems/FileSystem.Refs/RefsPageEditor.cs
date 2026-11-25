#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Offline editor for one live MSB+ leaf page. ReFS rows are variable sized and
/// the sorted key-index grows downward from its fixed end. Mutations therefore
/// repack only rows named by the live key-index array, preserve their row
/// metadata, and rebuild the index keeping each row's upper index word as found
/// (0xFFFF on most tables; on ReFS 3.14 allocator pages, node flags 0x0E00, the
/// row's start LCN — measured on Windows-written volumes). A row whose key lies
/// inside its value (key and value offsets equal, as allocator rows are stored)
/// keeps that form. Stale row bodies outside the live index are never resurrected.
/// </summary>
internal static class RefsPageEditor {
  internal sealed record LiveRow(
    uint EncodedIndex,
    ushort Flags,
    ushort Reserved,
    byte[] Key,
    byte[] Value,
    bool KeyInValue = false);

  private sealed record LeafState(
    byte[] Page,
    int NodeOffset,
    int DataStart,
    int IndexStart,
    int IndexEnd,
    List<LiveRow> Rows);

  public static bool CanReplaceValue(RefsMetadataGraph graph, RefsBTreeRow target, int newValueLength) {
    try {
      var state = ReadLeaf(graph, target.PhysicalPageLcn);
      var index = FindRow(state.Rows, target.Key);
      if (index < 0) return false;
      var rows = state.Rows.ToArray();
      rows[index] = rows[index] with { Value = new byte[newValueLength] };
      _ = Repack(state, rows, dryRun: true);
      return true;
    } catch (InvalidOperationException) {
      return false;
    }
  }

  public static bool CanInsertRow(
      RefsMetadataGraph graph,
      ulong physicalPageLcn,
      int sortedIndex,
      int keyLength,
      int valueLength) {
    if (keyLength < 0) throw new ArgumentOutOfRangeException(nameof(keyLength));
    if (valueLength < 0) throw new ArgumentOutOfRangeException(nameof(valueLength));
    try {
      var state = ReadLeaf(graph, physicalPageLcn);
      if ((uint)sortedIndex > (uint)state.Rows.Count) return false;
      var rows = state.Rows.ToList();
      rows.Insert(sortedIndex, new LiveRow(0xFFFF0000U, 0, 0, new byte[keyLength], new byte[valueLength]));
      _ = Repack(state, rows, dryRun: true);
      return true;
    } catch (InvalidOperationException) {
      return false;
    }
  }

  /// <summary>Replaces a leaf-row value and returns the changed page head.</summary>
  public static ulong ReplaceValue(RefsMetadataGraph graph, RefsBTreeRow target, ReadOnlySpan<byte> newValue) {
    var state = ReadLeaf(graph, target.PhysicalPageLcn);
    var index = FindRow(state.Rows, target.Key);
    if (index < 0)
      throw new InvalidOperationException("ReFS live row moved or disappeared before it could be rewritten.");

    var rows = state.Rows.ToArray();
    rows[index] = rows[index] with { Value = newValue.ToArray() };
    graph.WritePage(target.PhysicalPageLcn, Repack(state, rows, dryRun: false));
    return target.PhysicalPageLcn;
  }

  /// <summary>
  /// Inserts a row at a caller-selected position in the sorted key index. Key
  /// comparison is schema-specific and deliberately belongs to the table layer;
  /// this page primitive only performs structurally correct storage mutation.
  /// </summary>
  public static ulong InsertRow(
      RefsMetadataGraph graph,
      ulong physicalPageLcn,
      int sortedIndex,
      ReadOnlySpan<byte> key,
      ReadOnlySpan<byte> value,
      ushort flags = 0,
      ushort reserved = 0) {
    var state = ReadLeaf(graph, physicalPageLcn);
    if ((uint)sortedIndex > (uint)state.Rows.Count)
      throw new ArgumentOutOfRangeException(nameof(sortedIndex));

    var rows = state.Rows.ToList();
    rows.Insert(sortedIndex, new LiveRow(0xFFFF0000U, flags, reserved, key.ToArray(), value.ToArray()));
    graph.WritePage(physicalPageLcn, Repack(state, rows, dryRun: false));
    return physicalPageLcn;
  }

  /// <summary>Deletes the live row with the exact key and returns the changed page head.</summary>
  public static ulong DeleteRow(RefsMetadataGraph graph, RefsBTreeRow target) {
    var state = ReadLeaf(graph, target.PhysicalPageLcn);
    var index = FindRow(state.Rows, target.Key);
    if (index < 0)
      throw new InvalidOperationException("ReFS live row moved or disappeared before it could be deleted.");

    var rows = state.Rows.ToList();
    rows.RemoveAt(index);
    graph.WritePage(target.PhysicalPageLcn, Repack(state, rows, dryRun: false));
    return target.PhysicalPageLcn;
  }

  /// <summary>The live rows of a page in index order, and the bytes its node holds for rows and index.</summary>
  public static (IReadOnlyList<LiveRow> Rows, int Capacity) ReadRows(byte[] page, bool allowInner = false) {
    var state = ReadLeaf(page, allowInner);
    return (state.Rows, state.IndexEnd - state.DataStart);
  }

  /// <summary>The bytes a row of these lengths takes in a node, its index entry included.</summary>
  public static int RowCost(int keyLength, int valueLength) => Align8(Align8(16 + keyLength) + valueLength) + 4;

  /// <summary>The bytes an existing row takes in a node in the form it is stored, its index entry included.</summary>
  public static int RowCost(LiveRow row) => Serialize(row).Length + 4;

  /// <summary><paramref name="page"/> holding exactly <paramref name="rows"/>, in that order; throws when they do not fit.</summary>
  public static byte[] PackRows(byte[] page, IReadOnlyList<LiveRow> rows, bool allowInner = false)
    => Repack(ReadLeaf(page, allowInner), rows, dryRun: false);

  private static LeafState ReadLeaf(RefsMetadataGraph graph, ulong physicalPageLcn) => ReadLeaf(graph.ReadPage(physicalPageLcn), false);

  private static LeafState ReadLeaf(byte[] page, bool allowInner) {
    if (page.Length < 0x80 || !page.AsSpan(0, 4).SequenceEqual("MSB+"u8))
      throw new InvalidDataException("ReFS row is not stored in an MSB+ page.");

    var nodeOffset = 0x50 + checked((int)ReadU32(page, 0x50));
    if (nodeOffset < 0x50 || nodeOffset + 40 > page.Length)
      throw new InvalidDataException("ReFS node header is malformed.");
    var nodeFlags = ReadU32(page, nodeOffset + 0x0C);
    if (!allowInner && (nodeFlags & 0x100) != 0)
      throw new NotSupportedException("ReFS row mutation through the leaf editor is only valid on a B+ leaf page.");

    var dataStart = nodeOffset + checked((int)ReadU32(page, nodeOffset + 0x00));
    var indexStart = nodeOffset + checked((int)ReadU32(page, nodeOffset + 0x10));
    var indexEnd = nodeOffset + checked((int)ReadU32(page, nodeOffset + 0x20));
    if (dataStart < nodeOffset || indexStart < dataStart || indexEnd < indexStart || indexEnd > page.Length
        || ((indexEnd - indexStart) & 3) != 0)
      throw new InvalidDataException("ReFS node data/index bounds are malformed.");

    var rows = new List<LiveRow>((indexEnd - indexStart) / 4);
    for (var p = indexStart; p < indexEnd; p += 4) {
      var encoded = ReadU32(page, p);
      var rowOffset = nodeOffset + (int)(encoded & 0xFFFF);
      if (rowOffset < nodeOffset || rowOffset + 16 > page.Length)
        throw new InvalidDataException("ReFS live row index points outside its MSB+ page.");
      var rowSize = checked((int)ReadU32(page, rowOffset));
      if (rowSize < 16 || rowOffset + rowSize > page.Length)
        throw new InvalidDataException("ReFS live row is malformed.");
      var keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 4, 2));
      var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 6, 2));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 8, 2));
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 10, 2));
      var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 12, 2));
      var reserved = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 14, 2));
      if (keyOffset + keyLength > rowSize || valueOffset + valueLength > rowSize)
        throw new InvalidDataException("ReFS live row key/value bounds are malformed.");
      rows.Add(new LiveRow(
        encoded,
        flags,
        reserved,
        page.AsSpan(rowOffset + keyOffset, keyLength).ToArray(),
        page.AsSpan(rowOffset + valueOffset, valueLength).ToArray(),
        KeyInValue: keyOffset == valueOffset && keyLength <= valueLength));
    }

    return new LeafState(page, nodeOffset, dataStart, indexStart, indexEnd, rows);
  }

  private static byte[] Repack(LeafState state, IReadOnlyList<LiveRow> rows, bool dryRun) {
    var serialized = new byte[rows.Count][];
    var total = 0;
    for (var i = 0; i < rows.Count; ++i) {
      serialized[i] = Serialize(rows[i]);
      total = checked(total + serialized[i].Length);
    }

    var newIndexStart = checked(state.IndexEnd - rows.Count * 4);
    if (newIndexStart < state.DataStart || state.DataStart + total > newIndexStart)
      throw new InvalidOperationException(
        $"ReFS B+ leaf has insufficient space for {rows.Count:N0} live row(s); a page split is required.");
    if (dryRun) return state.Page;

    var page = state.Page.ToArray();
    page.AsSpan(state.DataStart, state.IndexEnd - state.DataStart).Clear();

    var cursor = state.DataStart;
    for (var i = 0; i < rows.Count; ++i) {
      var rowBytes = serialized[i];
      rowBytes.CopyTo(page, cursor);
      var rowRelative = cursor - state.NodeOffset;
      if ((uint)rowRelative > ushort.MaxValue)
        throw new InvalidOperationException("ReFS row offset exceeds the 16-bit B+ key-index field.");
      var encoded = (rows[i].EncodedIndex & 0xFFFF0000U) | (uint)rowRelative;
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(newIndexStart + i * 4, 4), encoded);
      cursor += rowBytes.Length;
    }

    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(state.NodeOffset + 0x04, 4), checked((uint)(cursor - state.NodeOffset)));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(state.NodeOffset + 0x08, 4), checked((uint)(newIndexStart - cursor)));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(state.NodeOffset + 0x10, 4), checked((uint)(newIndexStart - state.NodeOffset)));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(state.NodeOffset + 0x14, 4), checked((uint)rows.Count));
    UpdateTableRowCount(page, state.Rows.Count, rows.Count);
    return page;
  }

  /// <summary>
  /// A table whose root is a single leaf keeps its row count in the root page: the descriptor
  /// at 0x54 (u16 0x28) holds the leaf-page count at 0x68 (0 while the root is the only leaf)
  /// and the table's row count at 0x70 — measured on every table Windows wrote, and Windows
  /// keeps it current on each insert and delete. It is only rewritten when it held the old
  /// count, so a page of a larger tree is left alone.
  /// </summary>
  internal static void UpdateTableRowCount(Span<byte> page, int oldCount, int newCount) {
    if (page.Length < 0x78 || BinaryPrimitives.ReadUInt16LittleEndian(page[0x54..]) != 0x28) return;
    if (BinaryPrimitives.ReadUInt64LittleEndian(page[0x68..]) != 0) return;
    if (BinaryPrimitives.ReadUInt64LittleEndian(page[0x70..]) != (ulong)oldCount) return;
    BinaryPrimitives.WriteUInt64LittleEndian(page[0x70..], (ulong)newCount);
  }

  private static int FindRow(IReadOnlyList<LiveRow> rows, ReadOnlySpan<byte> key) {
    for (var i = 0; i < rows.Count; ++i)
      if (rows[i].Key.AsSpan().SequenceEqual(key)) return i;
    return -1;
  }

  private static byte[] Serialize(LiveRow row) {
    const int headerSize = 16;
    var keyOffset = headerSize;
    var keyInValue = row.KeyInValue && row.Key.Length <= row.Value.Length && row.Value.AsSpan(0, row.Key.Length).SequenceEqual(row.Key);
    var valueOffset = keyInValue ? keyOffset : Align8(keyOffset + row.Key.Length);
    var rowSize = Align8(checked(valueOffset + row.Value.Length));
    if (row.Key.Length > ushort.MaxValue || row.Value.Length > ushort.MaxValue || valueOffset > ushort.MaxValue)
      throw new InvalidOperationException("ReFS row key/value exceeds the on-disk 16-bit length fields.");

    var result = new byte[rowSize];
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), checked((uint)rowSize));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), checked((ushort)keyOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6, 2), checked((ushort)row.Key.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8, 2), row.Flags);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10, 2), checked((ushort)valueOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12, 2), checked((ushort)row.Value.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14, 2), row.Reserved);
    if (!keyInValue) row.Key.CopyTo(result, keyOffset);
    row.Value.CopyTo(result, valueOffset);
    return result;
  }

  private static int Align8(int value) => checked((value + 7) & ~7);

  private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 4 <= bytes.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4))
      : throw new InvalidDataException("ReFS metadata field lies outside its page.");
}

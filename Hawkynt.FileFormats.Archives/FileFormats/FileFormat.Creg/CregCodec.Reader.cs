#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Creg;

/// <summary>Clean-room reader for the Windows 95/98/Me CREG hive layout.</summary>
internal static class CregCodec {
  private const int FileHeaderSize = 32;
  private const int NavigationHeaderSize = 32;
  private const int HierarchyEntrySize = 28;
  private const int DataBlockHeaderSize = 32;

  // CREG marks "there is nothing here" with an all-ones field rather than with a zero, and the
  // width of the sentinel follows the width of the reference. Offsets (parent / first child /
  // next sibling, all relative to the RGKN record) are 32 bit, so their sentinel is 0xffffffff;
  // the RGDB block number and the key-name entry index are 16 bit, so theirs is 0xffff. A key
  // whose RGKN entry carries the 16-bit sentinel simply has no RGDB key-name record -- the root
  // key of every real hive is exactly that, because it has no name -- and an RGDB record that
  // carries it in its own identifier is a free slot left behind by a deletion.
  private const uint NoOffset = 0xffffffff;
  private const ushort NoEntry = 0xffff;

  public static StructuredNode Read(Stream stream) {
    var data = StructuredArchive.ReadAll(stream);
    if (data.Length < FileHeaderSize + NavigationHeaderSize || !data.AsSpan(0, 4).SequenceEqual("CREG"u8))
      throw new InvalidDataException("Not a Windows 9x CREG registry hive.");

    var minor = U16(data, 4);
    var major = U16(data, 6);
    if (major != 1 || minor != 0)
      throw new InvalidDataException($"Unsupported CREG version {major}.{minor}.");

    var dataBlocksOffset = CheckedOffset(U32(data, 8), data.Length, "CREG data-block list");
    var declaredDataBlocks = U16(data, 16);

    const int navigationOffset = FileHeaderSize;
    if (!data.AsSpan(navigationOffset, 4).SequenceEqual("RGKN"u8))
      throw new InvalidDataException("CREG key-navigation signature is missing.");

    var navigationSize = CheckedSize(U32(data, navigationOffset + 4), navigationOffset, data.Length, "CREG key-navigation record");
    if (navigationSize < NavigationHeaderSize + HierarchyEntrySize)
      throw new InvalidDataException("CREG key-navigation record is too small.");

    var hierarchyDataRelative = U32(data, navigationOffset + 8);
    var rootHierarchyOffset = CheckedRelative(navigationOffset, hierarchyDataRelative, navigationOffset + navigationSize, "CREG root hierarchy entry");
    EnsureRange(rootHierarchyOffset, HierarchyEntrySize, navigationOffset + navigationSize, "CREG root hierarchy entry");

    var blocks = ReadDataBlocks(data, dataBlocksOffset, declaredDataBlocks);
    var state = new ReaderState(data, blocks, navigationOffset, navigationOffset + navigationSize);
    return state.ReadRoot(rootHierarchyOffset);
  }

  /// <summary>
  /// Indexes every RGDB block by the key-name identifier each record stores in its own header,
  /// NOT by the record's position in the block. Real hives reuse freed slots, so the records in
  /// a block are in allocation order while their identifiers are not: in the Windows 9x
  /// <c>USER.DAT</c> published by log2timeline/dfwinreg, 776 of 801 records sit at a position
  /// that differs from their identifier. Position-based lookup therefore resolves keys to the
  /// wrong names instead of failing loudly.
  /// </summary>
  private static List<Dictionary<ushort, KeyNameEntry>> ReadDataBlocks(byte[] data, int start, int declaredCount) {
    var result = new List<Dictionary<ushort, KeyNameEntry>>(declaredCount);
    var offset = start;
    for (var blockIndex = 0; blockIndex < declaredCount; ++blockIndex) {
      EnsureRange(offset, DataBlockHeaderSize, data.Length, $"CREG data block {blockIndex}");
      if (!data.AsSpan(offset, 4).SequenceEqual("RGDB"u8))
        throw new InvalidDataException($"CREG data block {blockIndex} has no RGDB signature.");

      var size = CheckedSize(U32(data, offset + 4), offset, data.Length, $"CREG data block {blockIndex}");
      if (size < DataBlockHeaderSize)
        throw new InvalidDataException($"CREG data block {blockIndex} is too small.");

      var entries = new Dictionary<ushort, KeyNameEntry>();
      var cursor = offset + DataBlockHeaderSize;
      var end = offset + size;
      while (cursor + 20 <= end) {
        var rawSize = U32(data, cursor);
        if (rawSize is 0 or NoOffset)
          break;
        if (rawSize > int.MaxValue || rawSize < 20 || cursor + (long)rawSize > end)
          break;

        // A slot freed by a deletion keeps its size, so the block stays walkable, but blanks its
        // identifier to the same 0xffff sentinel. Indexing those would give every freed slot in a
        // block the same key; skipping them leaves one entry per live identifier.
        var recordIndex = U16(data, cursor + 4);
        if (recordIndex != NoEntry)
          _ = entries.TryAdd(recordIndex, new(cursor, checked((int)rawSize)));

        cursor += checked((int)rawSize);
      }

      result.Add(entries);
      offset = end;
    }

    return result;
  }

  private sealed class ReaderState(
    byte[] data,
    IReadOnlyList<Dictionary<ushort, KeyNameEntry>> blocks,
    int navigationOffset,
    int navigationEnd
  ) {
    private readonly HashSet<int> _activeHierarchyEntries = [];
    private int _keyCount;

    public StructuredNode ReadRoot(int hierarchyOffset) {
      var (_, root) = this.ReadKey(hierarchyOffset, 0);
      return root;
    }

    private (string Name, StructuredNode Node) ReadKey(int hierarchyOffset, int depth) {
      if (depth > 256)
        throw new InvalidDataException("CREG key nesting exceeds the 256-level safety limit.");
      if (++this._keyCount > 1_000_000)
        throw new InvalidDataException("CREG hive exceeds the one-million-key safety limit.");
      if (!this._activeHierarchyEntries.Add(hierarchyOffset))
        throw new InvalidDataException("CREG key hierarchy contains a cycle.");

      try {
        EnsureRange(hierarchyOffset, HierarchyEntrySize, navigationEnd, "CREG hierarchy entry");
        var entryNumber = U16(data, hierarchyOffset + 24);
        var blockNumber = U16(data, hierarchyOffset + 26);
        var keyEntry = this.GetKeyNameEntry(blockNumber, entryNumber);
        var (name, node) = keyEntry is { } named
          ? this.ReadKeyNameEntry(named)
          : (string.Empty, StructuredNode.Object("registry-key"));

        var nextChild = U32(data, hierarchyOffset + 16);
        var siblingGuard = new HashSet<uint>();
        while (nextChild != NoOffset && nextChild != 0) {
          if (!siblingGuard.Add(nextChild))
            throw new InvalidDataException("CREG sibling chain contains a cycle.");
          var childOffset = CheckedRelative(navigationOffset, nextChild, navigationEnd, "CREG child hierarchy entry");
          EnsureRange(childOffset, HierarchyEntrySize, navigationEnd, "CREG child hierarchy entry");
          var (childName, child) = this.ReadKey(childOffset, depth + 1);
          node.Add(childName, child);
          nextChild = U32(data, childOffset + 20);
        }

        return (name, node);
      } finally {
        this._activeHierarchyEntries.Remove(hierarchyOffset);
      }
    }

    /// <summary>Resolves a key's RGDB key-name record, or <see langword="null"/> when it has none.</summary>
    private KeyNameEntry? GetKeyNameEntry(ushort blockNumber, ushort entryNumber) {
      if (blockNumber == NoEntry || entryNumber == NoEntry)
        return null; // an absence, not a corruption -- the hive root is always this shape

      if (blockNumber >= blocks.Count)
        throw new InvalidDataException($"CREG key references invalid data block {blockNumber}.");
      if (!blocks[blockNumber].TryGetValue(entryNumber, out var entry))
        throw new InvalidDataException($"CREG key references invalid key-name entry {entryNumber} in block {blockNumber}.");

      return entry;
    }

    private (string Name, StructuredNode Node) ReadKeyNameEntry(KeyNameEntry entry) {
      var offset = entry.Offset;
      var end = offset + entry.Size;
      EnsureRange(offset, 20, end, "CREG key-name entry");

      var nameLength = U16(data, offset + 12);
      var valueCount = U16(data, offset + 14);
      EnsureRange(offset + 20, nameLength, end, "CREG key name");
      var name = DecodeAnsi(data.AsSpan(offset + 20, nameLength));

      var node = StructuredNode.Object("registry-key");
      var cursor = offset + 20 + nameLength;
      for (var i = 0; i < valueCount; ++i) {
        EnsureRange(cursor, 12, end, "CREG value entry");
        var type = U32(data, cursor);
        var valueNameLength = U16(data, cursor + 8);
        var valueDataLength = U16(data, cursor + 10);
        var payloadLength = checked(valueNameLength + valueDataLength);
        EnsureRange(cursor + 12, payloadLength, end, "CREG value payload");

        var valueName = valueNameLength == 0 ? "@" : DecodeAnsi(data.AsSpan(cursor + 12, valueNameLength));
        var valueData = data.AsSpan(cursor + 12 + valueNameLength, valueDataLength);
        node.Add(valueName, RegistryValue(type, valueData));
        cursor += 12 + payloadLength;
      }

      return (name, node);
    }
  }

  private static StructuredNode RegistryValue(uint type, ReadOnlySpan<byte> data) => type switch {
    1 => StructuredNode.Text(StructuredNodeKind.String, DecodeAnsi(data).TrimEnd('\0'), "REG_SZ"),
    3 => StructuredNode.Binary(data, "REG_BINARY"),
    4 when data.Length == 4 => StructuredNode.Text(
      StructuredNodeKind.Number,
      $"0x{BinaryPrimitives.ReadUInt32LittleEndian(data):X8}",
      "REG_DWORD"),
    _ => StructuredNode.Binary(data, RegistryTypeName(type)),
  };

  private static string RegistryTypeName(uint type) => type switch {
    0 => "REG_NONE",
    1 => "REG_SZ",
    2 => "REG_EXPAND_SZ",
    3 => "REG_BINARY",
    4 => "REG_DWORD",
    5 => "REG_DWORD_BIG_ENDIAN",
    6 => "REG_LINK",
    7 => "REG_MULTI_SZ",
    8 => "REG_RESOURCE_LIST",
    9 => "REG_FULL_RESOURCE_DESCRIPTOR",
    10 => "REG_RESOURCE_REQUIREMENTS_LIST",
    11 => "REG_QWORD",
    _ => $"REG_TYPE_{type}",
  };

  private static string DecodeAnsi(ReadOnlySpan<byte> value) => Encoding.Latin1.GetString(value);

  private static ushort U16(byte[] data, int offset) {
    EnsureRange(offset, 2, data.Length, "CREG UInt16");
    return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
  }

  private static uint U32(byte[] data, int offset) {
    EnsureRange(offset, 4, data.Length, "CREG UInt32");
    return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
  }

  private static int CheckedOffset(uint value, int limit, string what) {
    if (value > int.MaxValue || value > limit)
      throw new InvalidDataException($"{what} offset is outside the file.");
    return (int)value;
  }

  private static int CheckedSize(uint value, int offset, int limit, string what) {
    if (value > int.MaxValue || value < 8 || offset + (long)value > limit)
      throw new InvalidDataException($"{what} size is outside the file.");
    return (int)value;
  }

  private static int CheckedRelative(int origin, uint relative, int limit, string what) {
    var absolute = origin + (long)relative;
    if (absolute < origin || absolute > limit)
      throw new InvalidDataException($"{what} offset is outside the containing record.");
    return (int)absolute;
  }

  private static void EnsureRange(int offset, int length, int limit, string what) {
    if (offset < 0 || length < 0 || offset > limit - length)
      throw new InvalidDataException($"{what} is truncated or out of bounds.");
  }

  private readonly record struct KeyNameEntry(int Offset, int Size);
}

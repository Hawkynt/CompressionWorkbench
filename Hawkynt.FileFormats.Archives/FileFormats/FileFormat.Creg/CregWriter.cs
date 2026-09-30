#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Creg;

/// <summary>Writes a minimal CREG hive from a directory tree of binary registry values.</summary>
internal static class CregWriter {
  private const int FileHeaderSize = 32;
  private const int NavigationHeaderSize = 32;
  private const int HierarchyEntrySize = 28;
  private const int DataBlockHeaderSize = 32;
  private const int PreferredDataBlockSize = 60 * 1024;
  private const int MaximumSerializedSize = 256 * 1024 * 1024;
  private const uint NoOffset = uint.MaxValue;
  private const ushort NoEntry = ushort.MaxValue;
  private const int MaximumKeys = 1_000_000;
  private const int FreeRecordMinimum = 20;

  public static void Write(Stream output, StructuredNode root) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(root);
    if (!output.CanWrite) throw new ArgumentException("Output stream is not writable.", nameof(output));
    if (root.Kind != StructuredNodeKind.Object)
      throw new InvalidDataException("CREG root must be a registry key.");

    var keys = new List<KeyRecord>();
    var rootKey = new KeyRecord(string.Empty, null, []);
    AddChildren(rootKey, root, keys, 0);
    if (rootKey.Values.Count != 0)
      throw new NotSupportedException("CREG cannot represent value entries directly on the unnamed root key: its hierarchy entry has no key-name record to hold them. Put files under a key (a folder).");
    if (keys.Count > MaximumKeys)
      throw new InvalidDataException($"CREG hive exceeds the {MaximumKeys:N0}-key safety limit.");

    var blocks = BuildDataBlocks(keys);
    var hierarchyCount = checked(keys.Count + 1);
    var navigationUsed = checked(NavigationHeaderSize + hierarchyCount * HierarchyEntrySize);
    // Windows sizes the RGKN area in whole 4 KiB pages, as it does every RGDB block, and
    // libcreg reads hierarchy entries only from inside a page-sized area.
    var navigationSize = checked((navigationUsed + 4095) & ~4095);
    var dataOffset = checked(FileHeaderSize + navigationSize);
    long fileSize = dataOffset;
    foreach (var block in blocks) fileSize += block.Length;
    if (fileSize > MaximumSerializedSize)
      throw new InvalidDataException($"CREG hive exceeds the {MaximumSerializedSize:N0}-byte safety limit.");
    var hierarchy = new Dictionary<KeyRecord, int>(hierarchyCount) { [rootKey] = NavigationHeaderSize };
    for (var i = 0; i < keys.Count; ++i)
      hierarchy.Add(keys[i], checked(NavigationHeaderSize + (i + 1) * HierarchyEntrySize));

    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
    writer.Write("CREG"u8);
    writer.Write((ushort)0); // minor version
    writer.Write((ushort)1); // major version
    writer.Write(checked((uint)dataOffset));
    writer.Write(0u); // reserved/unknown header field
    writer.Write(checked((ushort)blocks.Count));
    writer.Write((ushort)0); // reserved/unknown header field
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write(0ul);

    writer.Write("RGKN"u8);
    writer.Write(checked((uint)navigationSize));
    writer.Write((uint)NavigationHeaderSize);
    writer.Write(checked((uint)(hierarchyCount * HierarchyEntrySize)));
    writer.Write(0u);
    writer.Write(0u);
    writer.Write(0ul);

    WriteHierarchy(writer, rootKey, hierarchy);
    foreach (var key in keys)
      WriteHierarchy(writer, key, hierarchy);
    writer.Write(new byte[navigationSize - navigationUsed]);

    foreach (var block in blocks)
      writer.Write(block);
  }

  private static void AddChildren(KeyRecord parent, StructuredNode node, List<KeyRecord> keys, int depth) {
    if (depth >= 256)
      throw new InvalidDataException("CREG key nesting exceeds the 256-level safety limit.");

    var keyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var valueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, child) in node.Members) {
      var nameBytes = EncodeAnsi(name, "CREG key or value name");
      if (child.Kind == StructuredNodeKind.Object) {
        if (!keyNames.Add(name))
          throw new InvalidDataException($"CREG key '{name}' duplicates a sibling key without regard to case.");
        var key = new KeyRecord(name, parent, nameBytes);
        parent.Children.Add(key);
        keys.Add(key);
        if (keys.Count > MaximumKeys)
          throw new InvalidDataException($"CREG hive exceeds the {MaximumKeys:N0}-key safety limit.");
        AddChildren(key, child, keys, depth + 1);
        continue;
      }

      if (child.Kind != StructuredNodeKind.Binary)
        throw new InvalidDataException($"CREG value '{name}' is not binary data.");
      if (nameBytes.Length > ushort.MaxValue)
        throw new InvalidDataException($"CREG value name '{name}' exceeds the 16-bit ANSI name limit.");
      if (child.Data.Length > ushort.MaxValue)
        throw new InvalidDataException($"CREG binary value '{name}' exceeds the 16-bit data limit.");
      if (!valueNames.Add(name))
        throw new InvalidDataException($"CREG value '{name}' duplicates a sibling value without regard to case.");

      parent.Values.Add(new(nameBytes, child.Data));
    }

    for (var i = 0; i + 1 < parent.Children.Count; ++i)
      parent.Children[i].NextSibling = parent.Children[i + 1];

    if (parent.Values.Count > ushort.MaxValue)
      throw new InvalidDataException($"CREG key '{parent.Name}' has too many values.");
  }

  private static List<byte[]> BuildDataBlocks(List<KeyRecord> keys) {
    var blocks = new List<byte[]>();
    var current = new List<byte[]>();
    var used = DataBlockHeaderSize;
    var maximumRecordBytes = MaximumSerializedSize - FileHeaderSize - NavigationHeaderSize - checked((keys.Count + 1) * HierarchyEntrySize);
    long recordBytes = 0;

    foreach (var key in keys) {
      var record = BuildKeyRecord(key);
      recordBytes += record.Length;
      if (recordBytes > maximumRecordBytes)
        throw new InvalidDataException($"CREG hive exceeds the {MaximumSerializedSize:N0}-byte safety limit.");
      if (current.Count == ushort.MaxValue || current.Count > 0 && (long)used + record.Length > PreferredDataBlockSize) {
        blocks.Add(BuildDataBlock(current, blocks.Count));
        current.Clear();
        used = DataBlockHeaderSize;
      }

      key.EntryIndex = checked((ushort)current.Count);
      key.BlockIndex = checked((ushort)blocks.Count);
      WriteU16(record, 4, key.EntryIndex);
      // Real hives carry the owning block's number beside the entry index.
      WriteU16(record, 6, key.BlockIndex);
      current.Add(record);
      used = checked(used + record.Length);
    }

    if (current.Count > 0)
      blocks.Add(BuildDataBlock(current, blocks.Count));
    if (blocks.Count > ushort.MaxValue)
      throw new InvalidDataException("CREG hive exceeds the 16-bit data-block count limit.");
    return blocks;
  }

  private static byte[] BuildKeyRecord(KeyRecord key) {
    if (key.Values.Count > ushort.MaxValue)
      throw new InvalidDataException($"CREG key '{key.Name}' has too many values.");

    long recordSize = 20L + key.NameBytes.Length;
    foreach (var value in key.Values)
      recordSize += 12L + value.Name.Length + value.Data.Length;
    if (recordSize > MaximumSerializedSize)
      throw new InvalidDataException($"CREG key '{key.Name}' exceeds the writer's size limit.");
    var size = checked((int)recordSize);
    var record = new byte[size];
    WriteU32(record, 0, checked((uint)size));
    WriteU16(record, 6, 0);
    WriteU32(record, 8, checked((uint)size));
    WriteU16(record, 12, checked((ushort)key.NameBytes.Length));
    WriteU16(record, 14, checked((ushort)key.Values.Count));
    key.NameBytes.CopyTo(record, 20);

    var cursor = 20 + key.NameBytes.Length;
    foreach (var value in key.Values) {
      WriteU32(record, cursor, 3); // REG_BINARY
      WriteU32(record, cursor + 4, 0);
      WriteU16(record, cursor + 8, checked((ushort)value.Name.Length));
      WriteU16(record, cursor + 10, checked((ushort)value.Data.Length));
      value.Name.CopyTo(record, cursor + 12);
      value.Data.CopyTo(record, cursor + 12 + value.Name.Length);
      cursor += 12 + value.Name.Length + value.Data.Length;
    }

    return record;
  }

  private static byte[] BuildDataBlock(List<byte[]> records, int index) {
    var usedSize = DataBlockHeaderSize;
    foreach (var record in records) usedSize = checked(usedSize + record.Length);
    // The slack after the last key is not left as zeros: a reader walks records
    // until the block ends, so Windows closes the block with one free record
    // (index and block 0xffff, used size 0xffffffff) spanning the rest. That
    // record needs its 20-byte header, so a tail shorter than that costs a page.
    var blockSize = checked((usedSize + 4095) & ~4095);
    if (blockSize != usedSize && blockSize - usedSize < FreeRecordMinimum)
      blockSize = checked(blockSize + 4096);
    var block = new byte[blockSize];
    "RGDB"u8.CopyTo(block);
    WriteU32(block, 4, checked((uint)blockSize));
    WriteU32(block, 8, checked((uint)(blockSize - usedSize)));
    WriteU16(block, 12, 8);
    WriteU16(block, 14, checked((ushort)index));
    WriteU32(block, 16, checked((uint)usedSize));

    var cursor = DataBlockHeaderSize;
    foreach (var record in records) {
      record.CopyTo(block, cursor);
      cursor += record.Length;
    }
    if (cursor < blockSize) {
      WriteU32(block, cursor, checked((uint)(blockSize - cursor)));
      WriteU16(block, cursor + 4, NoEntry);
      WriteU16(block, cursor + 6, NoEntry);
      WriteU32(block, cursor + 8, NoOffset);
    }
    return block;
  }

  private static void WriteHierarchy(BinaryWriter writer, KeyRecord key, Dictionary<KeyRecord, int> offsets) {
    writer.Write(0u);
    writer.Write(key.Parent is null ? 0u : HashName(key.NameBytes));
    writer.Write(NoOffset);
    writer.Write(key.Parent is null ? NoOffset : checked((uint)offsets[key.Parent]));
    writer.Write(key.Children.Count == 0 ? NoOffset : checked((uint)offsets[key.Children[0]]));
    writer.Write(key.NextSibling is null ? NoOffset : checked((uint)offsets[key.NextSibling]));
    writer.Write(key.Parent is null ? NoEntry : key.EntryIndex);
    writer.Write(key.Parent is null ? NoEntry : key.BlockIndex);
  }

  private static uint HashName(byte[] name) {
    uint hash = 0;
    foreach (var value in name) {
      var upper = value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;
      if (upper < 0x80) hash += upper;
    }
    return hash;
  }

  private static byte[] EncodeAnsi(string value, string description) {
    if (value.Contains('\0'))
      throw new InvalidDataException($"{description} contains a null character.");
    var encoded = Encoding.Latin1.GetBytes(value);
    if (!Encoding.Latin1.GetString(encoded).Equals(value, StringComparison.Ordinal))
      throw new InvalidDataException($"{description} cannot be represented by the CREG ANSI code page used by this writer.");
    if (encoded.Length > ushort.MaxValue)
      throw new InvalidDataException($"{description} exceeds the 16-bit ANSI string limit.");
    return encoded;
  }

  private static void WriteU16(byte[] destination, int offset, ushort value)
    => BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset), value);

  private static void WriteU32(byte[] destination, int offset, uint value)
    => BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset), value);

  private sealed class KeyRecord(string name, KeyRecord? parent, byte[] nameBytes) {
    public string Name { get; } = name;
    public KeyRecord? Parent { get; } = parent;
    public List<KeyRecord> Children { get; } = [];
    public List<ValueRecord> Values { get; } = [];
    public byte[] NameBytes { get; } = nameBytes;
    public ushort EntryIndex { get; set; } = NoEntry;
    public ushort BlockIndex { get; set; } = NoEntry;
    public KeyRecord? NextSibling { get; set; }
  }

  private sealed record ValueRecord(byte[] Name, byte[] Data);
}

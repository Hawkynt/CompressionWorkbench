using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// The rows a ReFS directory table keeps besides its name rows, measured by having Windows
/// Server 2022 (ReFS 3.7) add a file:
/// <list type="bullet">
/// <item>type 0x10, the directory's own header; value+0x80 (u64) is the last file number handed
/// out in this directory.</item>
/// <item>type 0x20, one per file held in a name row: key [0x20 00 00 80, u32 0, file number u64,
/// u64 0], value [u64 0, u16 0x000C, u16 name bytes, UTF-16 name] padded to 8 bytes; the name row
/// (type 0x30, kind 1) carries the same file number at value+0x80.</item>
/// </list>
/// </summary>
internal static class RefsDirectoryRows {
  public const int FileNumberOffset = 0x80;

  /// <summary>
  /// The directory's object-table row (root 0, key [u64 0, oid u64]) and where in its value the
  /// last file number handed out sits: +0xC8 on ReFS 3.7 (248-byte value), +0x48 on ReFS 3.14
  /// (80-byte value) — measured equal to the directory header's counter and to the highest
  /// file-number row on Windows-formatted volumes.
  /// </summary>
  public static (RefsBTreeRow Row, int CounterOffset)? ObjectRow(RefsMetadataReader metadata, ulong oid) {
    var row = metadata.WalkRoot(0).FirstOrDefault(r => r.Key.Length >= 16 && BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8)) == oid);
    if (row is null) return null;
    var offset = metadata.Header.MinorVersion >= 14 ? 0x48 : 0xC8;
    return row.Value.Length >= offset + 8 ? (row, offset) : null;
  }

  public static ulong FileNumber(byte[] nameRowValue)
    => nameRowValue.Length >= FileNumberOffset + 8 ? BinaryPrimitives.ReadUInt64LittleEndian(nameRowValue.AsSpan(FileNumberOffset)) : 0;

  public static byte[] IdKey(ulong fileNumber) {
    var key = new byte[24];
    BinaryPrimitives.WriteUInt32LittleEndian(key, 0x80000020);
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), fileNumber);
    return key;
  }

  public static byte[] IdValue(string name) {
    var bytes = Encoding.Unicode.GetBytes(name);
    var value = new byte[(12 + bytes.Length + 7) & ~7];
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(8), 0x000C);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(10), checked((ushort)bytes.Length));
    bytes.CopyTo(value, 12);
    return value;
  }

  /// <summary>The type-0x20 row of <paramref name="fileNumber"/> among <paramref name="rows"/>, checked to name <paramref name="name"/>.</summary>
  public static RefsBTreeRow? FindIdRow(IEnumerable<RefsBTreeRow> rows, ulong fileNumber, string name) {
    var key = IdKey(fileNumber);
    var row = rows.FirstOrDefault(r => r.Key.AsSpan().SequenceEqual(key));
    if (row is null || row.Value.Length < 12) return null;
    var length = BinaryPrimitives.ReadUInt16LittleEndian(row.Value.AsSpan(10));
    if (BinaryPrimitives.ReadUInt16LittleEndian(row.Value.AsSpan(8)) != 0x000C || 12 + length > row.Value.Length) return null;
    return string.Equals(Encoding.Unicode.GetString(row.Value, 12, length), name, StringComparison.OrdinalIgnoreCase) ? row : null;
  }
}

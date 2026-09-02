#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Sdat;

/// <summary>
/// Parses a Nintendo DS sound archive (<c>.sdat</c>, magic <c>SDAT</c>, little-endian) into the
/// list of files it carries. The NDS binary header is followed by four (u32 offset, u32 size)
/// block references — SYMB (optional, may be absent → offset 0), INFO, FAT and FILE. The FAT block
/// (<c>"FAT "</c> + size + count + count × (offset, size, u64 pad)) gives the absolute offset and
/// length of every embedded file inside the FILE block. Each carried file is detected by its own
/// 4-byte magic (<c>SWAV</c>, <c>SWAR</c>, <c>SSEQ</c>, <c>SBNK</c>, <c>STRM</c>, …).
/// </summary>
public sealed class SdatReader {

  /// <summary>
  /// Represents a file entry.
  /// </summary>
  public sealed record FileEntry(int Index, int Offset, int Size, string Magic, byte[] Data);

  /// <summary>
  /// Represents a parsed sdat.
  /// </summary>
  public sealed record ParsedSdat(int Version, IReadOnlyList<FileEntry> Files);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedSdat Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x40)
      throw new InvalidDataException("SDAT too short for header.");
    if (data[0] != 'S' || data[1] != 'D' || data[2] != 'A' || data[3] != 'T')
      throw new InvalidDataException("Missing SDAT magic.");
    var bom = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
    if (bom != 0xFEFF)
      throw new InvalidDataException("Only little-endian SDAT (BOM 0xFEFF) is supported.");

    var version = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
    // magic(4) bom(2) version(2) fileSize(4) headerSize(2) numBlocks(2) then 4 (offset,size) pairs.
    const int blockTable = 0x10;
    // SYMB[0], INFO[1], FAT[2], FILE[3].
    var fatOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(blockTable + 2 * 8)..]);
    if (fatOffset <= 0 || fatOffset + 12 > data.Length)
      throw new InvalidDataException("SDAT FAT block missing.");
    if (data[fatOffset] != 'F' || data[fatOffset + 1] != 'A' || data[fatOffset + 2] != 'T' || data[fatOffset + 3] != ' ')
      throw new InvalidDataException("SDAT FAT block has wrong magic.");

    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(fatOffset + 8)..]);
    if (count is < 0 or > 1_000_000)
      throw new InvalidDataException($"Implausible SDAT FAT count {count}.");

    var files = new List<FileEntry>(count);
    var recordBase = fatOffset + 12;
    for (var i = 0; i < count; ++i) {
      var r = recordBase + i * 16; // (u32 offset, u32 size, u64 pad)
      if (r + 16 > data.Length)
        break;
      var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[r..]);
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(r + 4)..]);
      if (off <= 0 || size <= 0 || off + size > data.Length)
        continue;

      var magic = ReadMagic(data, off);
      var blob = data.Slice(off, size).ToArray();
      files.Add(new FileEntry(i, off, size, magic, blob));
    }

    return new ParsedSdat(version, files);
  }

  private static string ReadMagic(ReadOnlySpan<byte> data, int offset) {
    if (offset + 4 > data.Length)
      return "";
    Span<char> chars = stackalloc char[4];
    for (var i = 0; i < 4; ++i) {
      var b = data[offset + i];
      chars[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
    }
    return new string(chars);
  }
}

using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Lzh;

/// <summary>One parsed LHA member header and where its data lies.</summary>
/// <param name="HeaderOffset">Offset of the header's first byte.</param>
/// <param name="DataOffset">Offset of the member's compressed data (just past every header byte).</param>
/// <param name="CompressedSize">Size of the compressed data, extended headers excluded.</param>
/// <param name="OriginalSize">Uncompressed size.</param>
/// <param name="Method">The five-character method id, e.g. <c>-lh5-</c>.</param>
/// <param name="Path">Directory and file name joined with '/', or empty.</param>
/// <param name="Crc16">CRC-16 of the uncompressed data.</param>
/// <param name="Level">Header level, 0-3.</param>
/// <param name="OsId">Operating-system id byte (0 for level 0, which has none).</param>
/// <param name="LastModified">Modification time.</param>
internal sealed record LhaHeader(
  long HeaderOffset, long DataOffset, long CompressedSize, long OriginalSize,
  string Method, string Path, ushort Crc16, byte Level, byte OsId, DateTime LastModified) {
  /// <summary>Offset just past the member's data, where the next header starts.</summary>
  public long End => this.DataOffset + this.CompressedSize;
}

/// <summary>
/// Parses LHA member headers of every level.
/// </summary>
/// <remarks>
/// <para>Layouts (all little-endian):</para>
/// <list type="bullet">
///   <item><description>Level 0 and 1: [0] header size N (bytes after the first two), [1] byte-sum of
///   those N bytes, [2..7] method, [7..11] compressed size, [11..15] original size, [15..19] MS-DOS time,
///   [19] attribute, [20] level, [21] name length L, name, CRC-16, then for level 0 an optional extended
///   area and for level 1 the OS id and the size of the first extended header. A level-1 member's
///   compressed size <em>includes</em> its extended headers, which follow the base header.</description></item>
///   <item><description>Level 2: [0..2] total header size, fields as above but with a Unix time, [21..23]
///   CRC-16, [23] OS id, then extended headers each led by a 16-bit size. LHA for OS-9/68k ('K') writes
///   a total two bytes short.</description></item>
///   <item><description>Level 3: [0..2] word size (4), the same fields, [24..28] total header size, then
///   extended headers each led by a 32-bit size.</description></item>
/// </list>
/// <para>Extended header 0x01 carries the file name, 0x02 the directory name with 0xFF as separator.</para>
/// <para>Sourcing: converted from lhasa's <c>lha_file_header.c</c> (ISC, Simon Howard); see
/// THIRD-PARTY-NOTICE.lhasa.txt in this directory.</para>
/// </remarks>
internal static class LhaHeaderParser {
  private const int CommonHeaderLength = 22;
  private const int Level0MinLength = 22;
  private const int Level1MinLength = 25;
  private const int Level2Length = 26;
  private const int Level3Length = 32;
  private const int Level3MaxLength = 1024 * 1024;
  private const byte OsTypeOs9_68k = (byte)'K';

  /// <summary>
  /// Reads the header at the stream's position. Returns <see langword="null"/> at the end-of-archive
  /// marker or end of stream.
  /// </summary>
  /// <exception cref="InvalidDataException">The header is damaged or of an unknown level.</exception>
  public static LhaHeader? Read(Stream stream) {
    var start = stream.Position;
    if (stream.Length - start < 1)
      return null;
    var first = stream.ReadByte();
    if (first <= 0)
      return null;  // a zero size byte ends the archive
    if (stream.Length - start < CommonHeaderLength)
      throw new InvalidDataException("Truncated LHA header.");

    var raw = new List<byte>(256) { (byte)first };
    Append(stream, raw, CommonHeaderLength - 1);
    var level = raw[20];

    return level switch {
      0 or 1 => ReadLevel01(stream, raw, start, level),
      2 => ReadLevel2(stream, raw, start),
      3 => ReadLevel3(stream, raw, start),
      _ => throw new InvalidDataException($"Unsupported LHA header level: {level}"),
    };
  }

  private static LhaHeader ReadLevel01(Stream stream, List<byte> raw, long start, byte level) {
    var headerLength = raw[0];
    if (headerLength < (level == 0 ? Level0MinLength : Level1MinLength))
      throw new InvalidDataException("LHA level-0/1 header is shorter than its fixed fields.");
    Append(stream, raw, headerLength + 2 - raw.Count);

    byte sum = 0;
    for (var i = 2; i < raw.Count; ++i)
      sum += raw[i];
    if (sum != raw[1])
      throw new InvalidDataException("LHA header checksum mismatch.");

    var pathLength = raw[21];
    if ((level == 0 ? Level0MinLength : Level1MinLength) + pathLength > headerLength)
      throw new InvalidDataException("LHA header name runs past the header.");

    long compressed = U32(raw, 7);
    var name = Latin1(raw, 22, pathLength).Replace('\\', '/');
    var crc = U16(raw, 22 + pathLength);
    byte os = level == 0 ? (byte)0 : raw[24 + pathLength];
    string? directory = null;

    if (level == 1) {
      // The size of the first extended header closes the base header; each extended header ends with the next one's.
      var offset = raw.Count - 2;
      while (true) {
        var size = U16(raw, raw.Count - 2);
        if (size == 0)
          break;
        if (size < 3)
          throw new InvalidDataException("LHA level-1 extended header is shorter than its own fields.");
        if (compressed < size)
          throw new InvalidDataException("LHA level-1 extended headers are larger than the member.");
        compressed -= size;  // the compressed size counts the extended headers too
        Append(stream, raw, size);
      }
      ParseExtended(raw, offset, 2, ref name, ref directory);
    }

    return Build(start, stream.Position, compressed, raw, name, directory, crc, level, os,
      DosTime(U32(raw, 15)));
  }

  private static LhaHeader ReadLevel2(Stream stream, List<byte> raw, long start) {
    var headerLength = U16(raw, 0);
    if (headerLength < Level2Length)
      throw new InvalidDataException("LHA level-2 header is shorter than its fixed fields.");
    Append(stream, raw, headerLength - raw.Count);
    var os = raw[23];
    if (os == OsTypeOs9_68k)
      Append(stream, raw, 2);  // OS-9/68k LHA counts the header two bytes short

    var name = string.Empty;
    string? directory = null;
    ParseExtended(raw, 24, 2, ref name, ref directory);
    return Build(start, stream.Position, U32(raw, 7), raw, name, directory, U16(raw, 21), 2, os,
      DateTimeOffset.FromUnixTimeSeconds(U32(raw, 15)).UtcDateTime);
  }

  private static LhaHeader ReadLevel3(Stream stream, List<byte> raw, long start) {
    if (U16(raw, 0) != 4)
      throw new InvalidDataException("LHA level-3 header has an unsupported word size.");
    Append(stream, raw, Level3Length - raw.Count);
    var headerLength = U32(raw, 24);
    if (headerLength > Level3MaxLength || headerLength < raw.Count)
      throw new InvalidDataException("LHA level-3 header size is out of range.");
    Append(stream, raw, (int)headerLength - raw.Count);

    var name = string.Empty;
    string? directory = null;
    ParseExtended(raw, 28, 4, ref name, ref directory);
    return Build(start, stream.Position, U32(raw, 7), raw, name, directory, U16(raw, 21), 3, raw[23],
      DateTimeOffset.FromUnixTimeSeconds(U32(raw, 15)).UtcDateTime);
  }

  /// <summary>Walks the extended-header chain starting at the size field at <paramref name="offset"/>.</summary>
  private static void ParseExtended(List<byte> raw, int offset, int fieldSize, ref string name, ref string? directory) {
    while (offset + fieldSize <= raw.Count) {
      long size = fieldSize == 4 ? U32(raw, offset) : U16(raw, offset);
      if (size == 0)
        break;
      if (size < fieldSize + 1 || offset + size > raw.Count)
        throw new InvalidDataException("LHA extended header size is out of range.");
      var type = raw[offset + fieldSize];
      var dataStart = offset + fieldSize + 1;
      var dataLength = (int)size - fieldSize - 1;
      if (dataStart + dataLength > raw.Count)
        throw new InvalidDataException("LHA extended header runs past the header.");
      switch (type) {
        case 0x01:
          name = Latin1(raw, dataStart, dataLength);
          break;
        case 0x02:
          var dir = new StringBuilder(dataLength);
          for (var i = 0; i < dataLength; ++i) {
            var b = raw[dataStart + i];
            dir.Append(b == 0xFF ? '/' : (char)b);
          }
          directory = dir.ToString();
          break;
      }
      offset += (int)size;
    }
  }

  private static LhaHeader Build(long start, long dataOffset, long compressed, List<byte> raw, string name,
      string? directory, ushort crc, byte level, byte os, DateTime modified) {
    var method = Encoding.ASCII.GetString([raw[2], raw[3], raw[4], raw[5], raw[6]]);
    var path = name;
    if (!string.IsNullOrEmpty(directory))
      path = directory.EndsWith('/') ? directory + name : directory + "/" + name;
    return new LhaHeader(start, dataOffset, compressed, U32(raw, 11), method, path.TrimEnd('/'), crc, level, os, modified);
  }

  private static void Append(Stream stream, List<byte> raw, int count) {
    if (count <= 0)
      return;
    var buffer = new byte[count];
    var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
    if (read < count)
      throw new InvalidDataException("Truncated LHA header.");
    raw.AddRange(buffer);
  }

  private static ushort U16(List<byte> raw, int at) => (ushort)(raw[at] | (raw[at + 1] << 8));

  private static uint U32(List<byte> raw, int at) =>
    (uint)(raw[at] | (raw[at + 1] << 8) | (raw[at + 2] << 16) | (raw[at + 3] << 24));

  private static string Latin1(List<byte> raw, int at, int count) {
    var bytes = new byte[count];
    raw.CopyTo(at, bytes, 0, count);
    return Encoding.Latin1.GetString(bytes);
  }

  private static DateTime DosTime(uint timestamp) {
    var time = (int)(timestamp & 0xFFFF);
    var date = (int)(timestamp >> 16);
    try {
      return new DateTime(((date >> 9) & 0x7F) + 1980, (date >> 5) & 0x0F, date & 0x1F,
        (time >> 11) & 0x1F, (time >> 5) & 0x3F, (time & 0x1F) * 2);
    } catch (ArgumentOutOfRangeException) {
      return DateTime.MinValue;
    }
  }
}

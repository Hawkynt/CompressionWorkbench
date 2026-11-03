using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.InnoSetup;

/// <summary>
/// Where the setup headers (setup-0) and file data (setup-1) sit inside a setup executable.
/// </summary>
/// <param name="HeaderOffset">File offset of the setup-0 version tag.</param>
/// <param name="DataOffset">File offset of embedded setup-1 data, or 0 when the data lives in external setup-N.bin slices.</param>
/// <param name="TableFound">Whether a setup loader offset table was found (false: the input is a bare setup-0).</param>
internal sealed record InnoOffsets(long HeaderOffset, long DataOffset, bool TableFound);

/// <summary>
/// Locates the setup loader's offset table: at 0x30 ("Inno" magic, before 5.1.5) or in the
/// RCDATA resource #11111 (5.1.5+). Converted from innoextract's src/loader/offsets.cpp
/// (zlib licence); the 64-bit table (revision 2) follows TSetupLdrOffsetTable in Inno Setup's
/// Shared.Struct.pas.
/// </summary>
internal static class InnoLoader {
  private const uint ExeHeaderMagic = 0x6F6E6E49; // "Inno"
  private const int ResourceId = 11111;

  private static readonly (byte[] Magic, uint Version)[] LoaderMagics = [
    ([.. "rDlPtS02"u8, 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.V(1, 2, 10)),
    ([.. "rDlPtS04"u8, 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.V(4, 0, 0)),
    ([.. "rDlPtS05"u8, 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.V(4, 0, 3)),
    ([.. "rDlPtS06"u8, 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.V(4, 0, 10)),
    ([.. "rDlPtS07"u8, 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.V(4, 1, 6)),
    ([.. "rDlPtS"u8, 0xCD, 0xE6, 0xD7, (byte)'{', 0x0B, (byte)'*'], InnoVersion.V(5, 1, 5)),
    ([.. "nS5W7dT"u8, 0x83, 0xAA, 0x1B, 0x0F, (byte)'j'], InnoVersion.V(5, 1, 5)),
  ];

  public static InnoOffsets Locate(Stream stream) {
    if (TryExeHeader(stream) is { } fromHeader)
      return fromHeader;
    if (FindRcData(stream, ResourceId) is { } resourceOffset && TryTable(stream, resourceOffset) is { } fromResource)
      return fromResource;
    return new(0, 0, false);
  }

  private static InnoOffsets? TryExeHeader(Stream stream) {
    if (stream.Length < 0x3C)
      return null;
    Span<byte> b = stackalloc byte[12];
    stream.Position = 0x30;
    if (stream.ReadAtLeast(b, 12, throwOnEndOfStream: false) < 12)
      return null;
    if (BinaryPrimitives.ReadUInt32LittleEndian(b) != ExeHeaderMagic)
      return null;
    var table = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
    if (table != ~BinaryPrimitives.ReadUInt32LittleEndian(b[8..]))
      return null;
    return TryTable(stream, table);
  }

  private static InnoOffsets? TryTable(Stream stream, long position) {
    if (position < 0 || position + 64 > stream.Length)
      return null;
    var raw = new byte[64];
    stream.Position = position;
    var n = stream.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false);
    var t = raw.AsSpan(0, n);

    uint version = uint.MaxValue;
    foreach (var (magic, v) in LoaderMagics)
      if (t[..12].SequenceEqual(magic)) {
        version = v;
        break;
      }

    var p = 12;
    uint revision = 0;
    if (version >= InnoVersion.V(5, 1, 5)) {
      revision = BinaryPrimitives.ReadUInt32LittleEndian(t[p..]);
      p += 4;
    }

    if (revision >= 2) {
      // TSetupLdrOffsetTable v2: TotalSize i64, OffsetEXE i64, UncompressedSizeEXE u32, CRCEXE i32,
      // Offset0 i64, Offset1 i64, ReservedPadding u32, TableCRC i32.
      var offset0 = BinaryPrimitives.ReadInt64LittleEndian(t[(p + 24)..]);
      var offset1 = BinaryPrimitives.ReadInt64LittleEndian(t[(p + 32)..]);
      var crc = BinaryPrimitives.ReadUInt32LittleEndian(t[(p + 44)..]);
      if (Crc32.Compute(t[..(p + 44)]) != crc)
        return null;
      return new(offset0, offset1, true);
    }

    p += 4; // total size
    p += 4; // exe offset
    if (version < InnoVersion.V(4, 1, 6)) p += 4; // exe compressed size
    p += 4; // exe uncompressed size
    p += 4; // exe checksum
    if (version < InnoVersion.V(4, 0, 0)) p += 4; // message offset (not covered by the CRC)
    var header = BinaryPrimitives.ReadUInt32LittleEndian(t[p..]);
    var data = BinaryPrimitives.ReadUInt32LittleEndian(t[(p + 4)..]);
    return new(header, data, true);
  }

  /// <summary>Finds the file offset of an RT_RCDATA resource with the given numeric id.</summary>
  internal static long? FindRcData(Stream stream, int id) {
    try {
      var head = new byte[4096];
      stream.Position = 0;
      var n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
      if (n < 0x40 || head[0] != 'M' || head[1] != 'Z')
        return null;
      var pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C));
      if (pe < 0 || pe + 24 > n || BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(pe)) != 0x00004550)
        return null;
      var sections = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 6));
      var optSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 20));
      var opt = pe + 24;
      var magic = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(opt));
      var dirBase = opt + (magic == 0x20B ? 112 : 96);
      if (dirBase + 24 > n)
        return null;
      var resRva = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(dirBase + 16));
      if (resRva == 0)
        return null;

      var sectionTable = opt + optSize;
      long RvaToOffset(uint rva) {
        for (var i = 0; i < sections; ++i) {
          var s = sectionTable + 40 * i;
          if (s + 40 > n) break;
          var va = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(s + 12));
          var vsize = Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(s + 8)), BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(s + 16)));
          var raw = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(s + 20));
          if (rva >= va && rva < va + vsize)
            return raw + (rva - va);
        }
        return -1;
      }

      var root = RvaToOffset(resRva);
      if (root < 0)
        return null;
      var typeDir = FindEntry(stream, root, root, 10);
      if (typeDir is not { } td || !td.IsDirectory) return null;
      var nameDir = FindEntry(stream, root, td.Offset, id);
      if (nameDir is not { } nd || !nd.IsDirectory) return null;
      var lang = FirstEntry(stream, root, nd.Offset);
      if (lang is not { } ld || ld.IsDirectory) return null;
      Span<byte> dataEntry = stackalloc byte[8];
      stream.Position = ld.Offset;
      stream.ReadExactly(dataEntry);
      var off = RvaToOffset(BinaryPrimitives.ReadUInt32LittleEndian(dataEntry));
      return off < 0 ? null : off;
    } catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException) {
      return null;
    }
  }

  private static (long Offset, bool IsDirectory)? FindEntry(Stream stream, long root, long dir, int id) {
    Span<byte> h = stackalloc byte[16];
    stream.Position = dir;
    if (stream.ReadAtLeast(h, 16, throwOnEndOfStream: false) < 16) return null;
    var named = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
    var ids = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
    Span<byte> e = stackalloc byte[8];
    for (var i = 0; i < named + ids; ++i) {
      stream.Position = dir + 16 + 8 * i;
      stream.ReadExactly(e);
      var name = BinaryPrimitives.ReadUInt32LittleEndian(e);
      var target = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
      if ((name & 0x80000000) == 0 && name == id)
        return (root + (target & 0x7FFFFFFF), (target & 0x80000000) != 0);
    }
    return null;
  }

  private static (long Offset, bool IsDirectory)? FirstEntry(Stream stream, long root, long dir) {
    Span<byte> h = stackalloc byte[24];
    stream.Position = dir;
    if (stream.ReadAtLeast(h, 24, throwOnEndOfStream: false) < 24) return null;
    var count = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]) + BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
    if (count == 0) return null;
    var target = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
    return (root + (target & 0x7FFFFFFF), (target & 0x80000000) != 0);
  }
}

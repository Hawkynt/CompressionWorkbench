using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using Compression.Core.DiskImage;

namespace FileSystem.Iso;

/// <summary>What a directory record's System Use area says under Rock Ridge.</summary>
internal sealed class RockRidgeInfo {
  /// <summary>NM: the POSIX name (continuation parts joined).</summary>
  public string? Name;
  /// <summary>SL: the symbolic link's target.</summary>
  public string? SymlinkTarget;
  /// <summary>PX: st_mode.</summary>
  public uint? Mode;
  /// <summary>RE: this directory was relocated here; its real place holds a CL entry.</summary>
  public bool Relocated;
  /// <summary>CL: this placeholder file is really the directory at that logical block.</summary>
  public uint? ChildLink;
  /// <summary>ZF: zisofs-compressed file data.</summary>
  public ZisofsInfo? Zisofs;
  /// <summary>ZF with an algorithm other than zisofs' "pz".</summary>
  public string? UnsupportedCompression;
}

/// <summary>ZF entry: header size in bytes, block size exponent, uncompressed length.</summary>
internal readonly record struct ZisofsInfo(int HeaderSize, int BlockLog2, long UncompressedSize);

/// <summary>
/// System Use Sharing Protocol (IEEE P1281) and Rock Ridge (IEEE P1282) entries:
/// CE continuation areas, NM names, SL symbolic links, PX modes, RE/CL directory
/// relocation, and the zisofs ZF entry with its compressed file format.
/// </summary>
/// <remarks>
/// <para>Entry layouts are from the Rock Ridge Interchange Protocol 1.12 (P1282) and
/// SUSP 1.12 (P1281). Both-endian numbers are read from their little-endian half.</para>
/// <para>zisofs (ZF algorithm "pz"): the ZF entry carries the header size / 4, log2 of
/// the block size and the uncompressed length; the file data starts with that header —
/// magic <c>37 E4 53 96 C9 DB D6 07</c>, the uncompressed length (u32 LE), header
/// size / 4, block size log2, two reserved bytes — followed by <c>blocks + 1</c> u32 LE
/// offsets into the file data. Block <c>i</c> is the zlib stream between offsets
/// <c>i</c> and <c>i + 1</c>; an empty block is all zeros. Format per zisofs-tools'
/// documentation and the Linux isofs driver's description of it.</para>
/// </remarks>
internal static class IsoRockRidge {
  private static readonly byte[] ZisofsMagic = [0x37, 0xE4, 0x53, 0x96, 0xC9, 0xDB, 0xD6, 0x07];
  private const int MaxContinuations = 32;

  /// <summary>Parses one System Use area, following CE continuation areas.</summary>
  public static RockRidgeInfo? Parse(ImageAccessor data, long start, long end, int sectorSize) {
    RockRidgeInfo? info = null;
    var name = new StringBuilder();
    var nameSeen = false;
    var link = new StringBuilder();
    var linkSeen = false;
    var linkComponentOpen = false;

    for (var hop = 0; hop <= MaxContinuations && start < end; hop++) {
      long nextStart = -1, nextLength = 0;
      var pos = start;
      while (pos + 4 <= end) {
        var s0 = data.ReadByte(pos);
        var s1 = data.ReadByte(pos + 1);
        var len = data.ReadByte(pos + 2);
        if (len < 4 || pos + len > end) break;
        info ??= new RockRidgeInfo();
        switch ((char)s0, (char)s1) {
          case ('S', 'T'):
            return Finish();
          case ('C', 'E') when len >= 28:
            nextStart = (long)data.ReadUInt32(pos + 4) * sectorSize + data.ReadUInt32(pos + 12);
            nextLength = data.ReadUInt32(pos + 20);
            break;
          case ('N', 'M') when len >= 5: {
            var flags = data.ReadByte(pos + 4);
            nameSeen = true;
            if ((flags & 0x02) != 0) name.Append('.');
            else if ((flags & 0x04) != 0) name.Append("..");
            else if (len > 5) name.Append(Encoding.UTF8.GetString(data.Read(pos + 5, len - 5)));
            break;
          }
          case ('S', 'L') when len >= 5: {
            linkSeen = true;
            var c = pos + 5;
            while (c + 2 <= pos + len) {
              var cflags = data.ReadByte(c);
              var clen = data.ReadByte(c + 1);
              if (c + 2 + clen > pos + len) break;
              string part = (cflags & 0x02) != 0 ? "."
                : (cflags & 0x04) != 0 ? ".."
                : (cflags & 0x08) != 0 ? ""
                : Encoding.UTF8.GetString(data.Read(c + 2, clen));
              if (!linkComponentOpen && link.Length > 0 && !(link.Length == 1 && link[0] == '/'))
                link.Append('/');
              if ((cflags & 0x08) != 0) {
                link.Clear();
                link.Append('/');
              } else {
                link.Append(part);
              }
              linkComponentOpen = (cflags & 0x01) != 0;
              c += 2 + clen;
            }
            break;
          }
          case ('P', 'X') when len >= 12:
            info.Mode = data.ReadUInt32(pos + 4);
            break;
          case ('R', 'E'):
            info.Relocated = true;
            break;
          case ('C', 'L') when len >= 12:
            info.ChildLink = data.ReadUInt32(pos + 4);
            break;
          case ('Z', 'F') when len >= 16: {
            var a0 = (char)data.ReadByte(pos + 4);
            var a1 = (char)data.ReadByte(pos + 5);
            if (a0 == 'p' && a1 == 'z')
              info.Zisofs = new ZisofsInfo(data.ReadByte(pos + 6) * 4, data.ReadByte(pos + 7), data.ReadUInt32(pos + 8));
            else
              info.UnsupportedCompression = $"{a0}{a1}";
            break;
          }
        }
        pos += len;
      }
      if (nextStart < 0 || nextLength <= 0 || nextStart + nextLength > data.Length) break;
      start = nextStart;
      end = nextStart + nextLength;
    }
    return Finish();

    RockRidgeInfo? Finish() {
      if (info is null) return null;
      if (nameSeen && name.Length > 0) info.Name = name.ToString();
      if (linkSeen) info.SymlinkTarget = link.ToString();
      return info;
    }
  }

  /// <summary>Decodes a zisofs file's stored bytes to its uncompressed content.</summary>
  public static byte[] DecodeZisofs(ReadOnlySpan<byte> stored, ZisofsInfo zf, string name) {
    if (zf.BlockLog2 is < 15 or > 17)
      throw new InvalidDataException($"ISO9660: zisofs file '{name}' has block size 2^{zf.BlockLog2}.");
    if (zf.HeaderSize < 16 || stored.Length < zf.HeaderSize || !stored[..8].SequenceEqual(ZisofsMagic))
      throw new InvalidDataException($"ISO9660: zisofs file '{name}' lacks the zisofs header.");
    var size = BinaryPrimitives.ReadUInt32LittleEndian(stored[8..]);
    if (size != zf.UncompressedSize)
      throw new InvalidDataException($"ISO9660: zisofs file '{name}' header says {size} bytes, the ZF entry {zf.UncompressedSize}.");
    if (size > Array.MaxLength)
      throw new NotSupportedException($"ISO9660: zisofs file '{name}' is too large to buffer.");
    var block = 1 << zf.BlockLog2;
    var blocks = (int)((size + block - 1) / block);
    var table = zf.HeaderSize;
    if (table + (blocks + 1) * 4L > stored.Length)
      throw new InvalidDataException($"ISO9660: zisofs file '{name}' block table is truncated.");

    var output = new byte[size];
    for (var i = 0; i < blocks; i++) {
      var from = BinaryPrimitives.ReadUInt32LittleEndian(stored[(table + i * 4)..]);
      var to = BinaryPrimitives.ReadUInt32LittleEndian(stored[(table + i * 4 + 4)..]);
      var expected = (int)Math.Min(block, size - (long)i * block);
      if (to < from || to > stored.Length)
        throw new InvalidDataException($"ISO9660: zisofs file '{name}' block {i} lies outside the file.");
      if (to == from) continue; // an all-zero block
      var z = stored[(int)from..(int)to];
      if (z.Length < 2 || (z[0] & 0x0F) != 8 || ((z[0] << 8) | z[1]) % 31 != 0)
        throw new InvalidDataException($"ISO9660: zisofs file '{name}' block {i} is not a zlib stream.");
      var inflated = DeflateDecompressor.Decompress(z[2..]);
      if (inflated.Length != expected)
        throw new InvalidDataException($"ISO9660: zisofs file '{name}' block {i} inflated to {inflated.Length} bytes, expected {expected}.");
      inflated.CopyTo(output, i * block);
    }
    return output;
  }
}

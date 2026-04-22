#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jffs2;

/// <summary>
/// Scans a JFFS2 image for node magic bytes (0x1985) and emits per-type
/// counts and flat dirent / inode tables. Does not decompress inode bodies —
/// that's deliberately out of scope for a triage tool.
/// </summary>
internal static class Jffs2Scanner {
  public const ushort Magic = 0x1985;
  public const ushort NodeTypeDirent = 0xE001;
  public const ushort NodeTypeInode = 0xE002;
  public const ushort NodeTypeCleanmarker = 0x2003;
  public const ushort NodeTypePadding = 0x2004;
  public const ushort NodeTypeSummary = 0x2006;

  internal sealed record DirentInfo(long ParentInode, long Inode, string Name, byte Type);
  internal sealed record InodeInfo(long Inode, uint Version, uint Uid, uint Gid, uint Mode, long Size, uint Mtime);

  internal sealed class ScanResult {
    public int DirentCount { get; set; }
    public int InodeCount { get; set; }
    public int CleanmarkerCount { get; set; }
    public int PaddingCount { get; set; }
    public int SummaryCount { get; set; }
    public int TotalNodes { get; set; }
    public int EraseSizeIfDetectable { get; set; }
    public List<DirentInfo> Dirents { get; } = [];
    public List<InodeInfo> Inodes { get; } = [];
    public bool ParseOk { get; set; }
  }

  public static ScanResult Scan(ReadOnlySpan<byte> image) {
    var result = new ScanResult();
    try {
      result.EraseSizeIfDetectable = DetectEraseSize(image);
      ScanLinear(image, result);
      result.ParseOk = true;
    } catch {
      result.ParseOk = false;
    }
    return result;
  }

  /// <summary>
  /// Erase size is typically a power of two (64 KiB, 128 KiB, 256 KiB, 4 MiB)
  /// that divides the image length AND where the magic appears at the start
  /// of every erase block. We pick the largest candidate whose start offsets
  /// all show magic.
  /// </summary>
  private static int DetectEraseSize(ReadOnlySpan<byte> image) {
    foreach (var candidate in (int[])[0x1000, 0x4000, 0x10000, 0x20000, 0x40000, 0x100000, 0x400000]) {
      if (candidate > image.Length) break;
      if (image.Length % candidate != 0) continue;
      var hits = 0;
      var count = image.Length / candidate;
      for (var i = 0; i < count; ++i) {
        var off = i * candidate;
        if (off + 2 > image.Length) break;
        if (BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(off, 2)) == Magic) ++hits;
      }
      // Require at least half the blocks to begin with magic for a confident match.
      if (count > 0 && hits * 2 >= count) return candidate;
    }
    return 0;
  }

  private static void ScanLinear(ReadOnlySpan<byte> image, ScanResult result) {
    var off = 0;
    while (off + 12 <= image.Length) {
      var magic = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(off, 2));
      if (magic != Magic) {
        off += 4; // JFFS2 nodes are 4-byte aligned; skip 4 bytes when out of sync.
        continue;
      }
      var nodeType = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(off + 2, 2));
      var totLen = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 4, 4));

      result.TotalNodes++;
      switch (nodeType) {
        case NodeTypeDirent:
          result.DirentCount++;
          TryParseDirent(image, off, totLen, result);
          break;
        case NodeTypeInode:
          result.InodeCount++;
          TryParseInode(image, off, totLen, result);
          break;
        case NodeTypeCleanmarker: result.CleanmarkerCount++; break;
        case NodeTypePadding: result.PaddingCount++; break;
        case NodeTypeSummary: result.SummaryCount++; break;
      }

      // Advance to next node (align totLen to 4).
      if (totLen < 12 || totLen > image.Length || off + (int)totLen > image.Length) {
        off += 4;
        continue;
      }
      var aligned = ((int)totLen + 3) & ~3;
      off += aligned;
    }
  }

  // Dirent layout (LE):
  //  0  magic    u16
  //  2  nodetype u16
  //  4  totlen   u32
  //  8  hdr_crc  u32
  // 12  pino     u32 (parent inode)
  // 16  version  u32
  // 20  ino      u32 (0 = unlink)
  // 24  mctime   u32
  // 28  nsize    u8
  // 29  type     u8
  // 30  unused[2]
  // 32  node_crc u32
  // 36  name_crc u32
  // 40  name[nsize]
  private static void TryParseDirent(ReadOnlySpan<byte> image, int off, uint totLen, ScanResult result) {
    try {
      if (off + 40 > image.Length) return;
      var parent = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 12, 4));
      var inode = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 20, 4));
      var nsize = image[off + 28];
      var type = image[off + 29];
      if (nsize == 0 || nsize > 128) return;
      if (off + 40 + nsize > image.Length) return;
      if (off + 40 + nsize > off + totLen) return;
      var name = Encoding.UTF8.GetString(image.Slice(off + 40, nsize));
      result.Dirents.Add(new DirentInfo(parent, inode, name, type));
    } catch {
      // swallow
    }
  }

  // Inode layout (LE), first 68 bytes:
  //  0  magic    u16
  //  2  nodetype u16
  //  4  totlen   u32
  //  8  hdr_crc  u32
  // 12  ino      u32
  // 16  version  u32
  // 20  mode     u32
  // 24  uid      u16
  // 26  gid      u16
  // 28  isize    u32 (file size)
  // 32  atime    u32
  // 36  mtime    u32
  // 40  ctime    u32
  // 44  offset   u32
  // 48  csize    u32
  // 52  dsize    u32
  // 56  compr    u8
  // 57  usercompr u8
  // 58  flags    u16
  // 60  data_crc u32
  // 64  node_crc u32
  private static void TryParseInode(ReadOnlySpan<byte> image, int off, uint totLen, ScanResult result) {
    try {
      if (off + 44 > image.Length) return;
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 12, 4));
      var version = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 16, 4));
      var mode = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 20, 4));
      var uid = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(off + 24, 2));
      var gid = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(off + 26, 2));
      var isize = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 28, 4));
      var mtime = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 36, 4));
      result.Inodes.Add(new InodeInfo(ino, version, uid, gid, mode, isize, mtime));
    } catch {
      // swallow
    }
    _ = totLen;
  }
}

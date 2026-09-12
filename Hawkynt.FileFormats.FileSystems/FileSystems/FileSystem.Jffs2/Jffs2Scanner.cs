#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

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

    /// <summary>Set when the node tables stopped at <see cref="MaxTableRows" /> and more nodes followed.</summary>
    public bool TablesTruncated { get; set; }
  }

  /// <summary>
  /// Cap on rows in the dirent / inode triage tables. A multi-gigabyte volume holds
  /// hundreds of thousands of data nodes; the tables exist to eyeball a suspect image,
  /// not to transcribe it, so past this point the scan records that it stopped.
  /// </summary>
  public const int MaxTableRows = 10_000;

  public static ScanResult Scan(ReadOnlySpan<byte> image) {
    using var accessor = ImageAccessor.FromBytes(image.ToArray());
    return Scan(accessor);
  }

  /// <summary>Scans a volume through random access, so the image never has to be resident.</summary>
  public static ScanResult Scan(ImageAccessor image) {
    ArgumentNullException.ThrowIfNull(image);
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
  private static int DetectEraseSize(ImageAccessor image) {
    foreach (var candidate in (int[])[0x1000, 0x4000, 0x10000, 0x20000, 0x40000, 0x100000, 0x400000]) {
      if (candidate > image.Length) break;
      if (image.Length % candidate != 0) continue;
      var hits = 0L;
      var count = image.Length / candidate;
      for (var i = 0L; i < count; ++i) {
        var off = i * candidate;
        if (off + 2 > image.Length) break;
        if (image.ReadUInt16(off) == Magic) ++hits;
      }
      // Require at least half the blocks to begin with magic for a confident match.
      if (count > 0 && hits * 2 >= count) return candidate;
    }
    return 0;
  }

  private static void ScanLinear(ImageAccessor image, ScanResult result) {
    var length = image.Length;
    var header = new byte[MaxNodeProbe];
    long off = 0;
    while (off + 12 <= length) {
      var want = (int)Math.Min(MaxNodeProbe, length - off);
      var read = image.Read(off, header.AsSpan(0, want));
      if (read < 12) break;
      var node = header.AsSpan(0, read);

      var magic = BinaryPrimitives.ReadUInt16LittleEndian(node[..2]);
      if (magic != Magic) {
        off += 4; // JFFS2 nodes are 4-byte aligned; skip 4 bytes when out of sync.
        continue;
      }
      var nodeType = BinaryPrimitives.ReadUInt16LittleEndian(node.Slice(2, 2));
      var totLen = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(4, 4));

      result.TotalNodes++;
      switch (nodeType) {
        case NodeTypeDirent:
          result.DirentCount++;
          TryParseDirent(node, totLen, result);
          break;
        case NodeTypeInode:
          result.InodeCount++;
          TryParseInode(node, result);
          break;
        case NodeTypeCleanmarker: result.CleanmarkerCount++; break;
        case NodeTypePadding: result.PaddingCount++; break;
        case NodeTypeSummary: result.SummaryCount++; break;
      }

      // Advance to next node (align totLen to 4).
      if (totLen < 12 || off + totLen > length) {
        off += 4;
        continue;
      }
      off += (totLen + 3) & ~3u;
    }
  }

  /// <summary>Bytes read per node probe: the dirent header plus the longest name it can carry.</summary>
  private const int MaxNodeProbe = 40 + 128;

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
  private static void TryParseDirent(ReadOnlySpan<byte> node, uint totLen, ScanResult result) {
    try {
      if (node.Length < 40) return;
      var parent = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(12, 4));
      var inode = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(20, 4));
      var nsize = node[28];
      var type = node[29];
      if (nsize == 0 || nsize > 128) return;
      if (40 + nsize > node.Length) return;
      if (40 + nsize > totLen) return;
      if (result.Dirents.Count >= MaxTableRows) { result.TablesTruncated = true; return; }
      var name = Encoding.UTF8.GetString(node.Slice(40, nsize));
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
  private static void TryParseInode(ReadOnlySpan<byte> node, ScanResult result) {
    try {
      if (node.Length < 44) return;
      if (result.Inodes.Count >= MaxTableRows) { result.TablesTruncated = true; return; }
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(12, 4));
      var version = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(16, 4));
      var mode = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(20, 4));
      var uid = BinaryPrimitives.ReadUInt16LittleEndian(node.Slice(24, 2));
      var gid = BinaryPrimitives.ReadUInt16LittleEndian(node.Slice(26, 2));
      var isize = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(28, 4));
      var mtime = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(36, 4));
      result.Inodes.Add(new InodeInfo(ino, version, uid, gid, mode, isize, mtime));
    } catch {
      // swallow
    }
  }
}

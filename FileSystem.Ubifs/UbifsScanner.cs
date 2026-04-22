#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ubifs;

/// <summary>
/// Scans a UBIFS image for node magic bytes (0x06101831) and emits
/// per-node summaries. Does NOT decode the LPT/TNC B+trees — the goal is
/// triage output only.
/// </summary>
internal static class UbifsScanner {
  public const uint NodeMagic = 0x06101831;
  public static readonly int[] CandidateLebSizes = [32768, 65536, 131072, 262144, 524288];

  internal sealed record InodeInfo(long InodeNum, long Size, uint Flags);
  internal sealed record DentryInfo(long ParentInode, string Name, byte Type);

  internal sealed class ScanResult {
    public Dictionary<byte, int> NodeCountsByType { get; } = new();
    public int TotalNodes { get; set; }
    public bool SuperblockFound { get; set; }
    public int LebSizeIfKnown { get; set; }
    public List<InodeInfo> Inodes { get; } = [];
    public List<DentryInfo> Dentries { get; } = [];
    public bool ParseOk { get; set; }
  }

  /// <summary>Scans the image and returns counts and extracted inode/dentry tables. Never throws.</summary>
  public static ScanResult Scan(ReadOnlySpan<byte> image) {
    var result = new ScanResult();
    try {
      // Pick best LEB size: the one that yields the most magic hits at LEB boundaries.
      var bestLeb = 0;
      var bestHits = 0;
      foreach (var leb in CandidateLebSizes) {
        var hits = CountMagicAtLebBoundaries(image, leb);
        if (hits > bestHits) { bestHits = hits; bestLeb = leb; }
      }
      result.LebSizeIfKnown = bestLeb;

      // Perform a linear magic scan so we can enumerate *all* nodes, not just
      // LEB-aligned ones (nodes can sit at arbitrary offsets inside an LEB).
      ScanLinear(image, result);
      result.ParseOk = true;
    } catch {
      result.ParseOk = false;
    }
    return result;
  }

  private static int CountMagicAtLebBoundaries(ReadOnlySpan<byte> image, int leb) {
    if (leb <= 0) return 0;
    var hits = 0;
    for (var off = 0; off + 4 <= image.Length; off += leb) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off, 4)) == NodeMagic)
        ++hits;
    }
    return hits;
  }

  private static void ScanLinear(ReadOnlySpan<byte> image, ScanResult result) {
    for (var off = 0; off + 24 <= image.Length; ++off) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off, 4)) != NodeMagic)
        continue;

      // Common header: magic(4) crc(4) sqnum(8) len(4) type(1) group_type(1) pad(2)
      var nodeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 16, 4));
      var nodeType = image[off + 20];

      result.TotalNodes++;
      result.NodeCountsByType.TryGetValue(nodeType, out var c);
      result.NodeCountsByType[nodeType] = c + 1;

      if (nodeType == 6) result.SuperblockFound = true;

      // Try best-effort inode/dentry decode.
      switch (nodeType) {
        case 0: TryParseInode(image, off, nodeLen, result); break;
        case 2: TryParseDentry(image, off, nodeLen, result); break;
      }

      // Advance past the node length if it looks sane to reduce redundant work.
      if (nodeLen >= 24 && nodeLen < 1 << 22 && off + nodeLen <= image.Length)
        off += nodeLen - 1; // -1 because the outer loop increments by 1
    }
  }

  // UBIFS inode node layout (after 24-byte common header):
  //   key[16] creat_sqnum(8) size(8) atime_sec(8) ctime_sec(8) mtime_sec(8) ...
  //   atime_nsec(4) ctime_nsec(4) mtime_nsec(4) nlink(4) uid(4) gid(4) mode(4)
  //   flags(4) data_len(4) xattr_cnt(4) xattr_size(4) pad(4) xattr_names(4)
  //   compr_type(2) pad2(2+24) data[]
  // inode number is parsed out of the first 8 bytes of the key (LE).
  private static void TryParseInode(ReadOnlySpan<byte> image, int off, int nodeLen, ScanResult result) {
    try {
      var payload = off + 24;
      if (payload + 16 + 8 + 8 + 3 * 8 + 5 * 4 + 7 * 4 > image.Length) return;
      var inodeNum = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(payload, 8));
      var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(payload + 16 + 8, 8));
      // flags at offset payload + 16 + 8 + 8 + 3*8 + 3*4 + 4*4 = payload + 80
      var flagsOff = payload + 80;
      if (flagsOff + 4 > image.Length) return;
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(flagsOff, 4));
      result.Inodes.Add(new InodeInfo(inodeNum, size, flags));
    } catch {
      // swallow
    }
    _ = nodeLen;
  }

  // UBIFS dentry node layout (after 24-byte common header):
  //   key[16] inum(8) padding(1) type(1) nlen(2) name[nlen + 1]
  private static void TryParseDentry(ReadOnlySpan<byte> image, int off, int nodeLen, ScanResult result) {
    try {
      var payload = off + 24;
      if (payload + 16 + 8 + 4 > image.Length) return;
      // parent inode is the first 8 bytes of the key
      var parent = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(payload, 8));
      var inum = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(payload + 16, 8));
      var type = image[payload + 16 + 8 + 1];
      var nlen = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(payload + 16 + 8 + 2, 2));
      if (nlen == 0 || nlen > 255) return;
      var nameOff = payload + 16 + 8 + 4;
      if (nameOff + nlen > image.Length) return;
      var name = Encoding.UTF8.GetString(image.Slice(nameOff, nlen));
      result.Dentries.Add(new DentryInfo(parent, name, type));
      _ = inum;
    } catch {
      // swallow
    }
    _ = nodeLen;
  }
}

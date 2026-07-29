#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Htfs;

/// <summary>
/// Walks an HTFS image written by <see cref="HtfsWriter"/> and yields its
/// on-disk extents: superblock + inode array become
/// <see cref="DefragBlockKind.MetadataReserved"/>; each directory body is
/// metadata too; each file extent is <see cref="DefragBlockKind.Used"/>.
/// </summary>
internal static class HtfsExtentMap {
  internal static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    // The head carries the superblock and inode table; the walk itself goes
    // through the reader, which streams. Buffering the whole volume capped the
    // wipe at the array limit and, worse, returned nothing when it threw -- and
    // an empty extent list reads as "the volume is entirely free".
    var head = new byte[(int)Math.Min(image.Length, 4 * 1024 * 1024)];
    image.ReadExactly(head, 0, head.Length);
    image.Position = 0;
    return EnumerateBytes(head, image);
  }

  private static List<DefragBlockInfo> EnumerateBytes(byte[] bytes, Stream image) {
    var result = new List<DefragBlockInfo>();
    var sb = HtfsSuperblock.TryParse(bytes);
    if (!sb.Valid) return result;

    int blockSize = HtfsWriter.DefaultBlockSize;
    foreach (var bs in new[] { 512, 1024, 2048 }) {
      var implied = (long)sb.Fsize * bs;
      if (implied >= image.Length - bs && implied <= image.Length + bs) {
        blockSize = bs;
        break;
      }
    }

    // SB + reserved tail through end of SB block.
    result.Add(new DefragBlockInfo(0, HtfsWriter.SuperblockOffset, DefragBlockKind.MetadataReserved, "boot"));
    result.Add(new DefragBlockInfo(HtfsWriter.SuperblockOffset, blockSize, DefragBlockKind.MetadataReserved, "superblock"));
    var inodeStart = (HtfsWriter.SuperblockOffset / blockSize + 1) * blockSize;
    var inodeBytes = (long)sb.Isize * blockSize;
    if (inodeBytes > 0)
      result.Add(new DefragBlockInfo(inodeStart, inodeBytes, DefragBlockKind.MetadataReserved, "inode_table"));

    // Root directory (inode 2) + every reader entry.
    try {
      image.Position = 0;
      var reader = new HtfsReader(image);
      // First data block hosts root dir; surface it explicitly.
      var rootStart = inodeStart + inodeBytes;
      if (rootStart < image.Length)
        result.Add(new DefragBlockInfo(rootStart, blockSize, DefragBlockKind.MetadataReserved, "root_dir"));
      foreach (var e in reader.Entries) {
        if (e.FirstBlock == 0) continue;
        var blocks = e.IsDirectory ? 1 : (e.Size + blockSize - 1) / blockSize;
        var len = (long)blocks * blockSize;
        result.Add(new DefragBlockInfo(e.FirstBlock * blockSize, len,
          e.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
          e.IsDirectory ? null : e.Name));
      }
    } catch { /* tolerate malformed */ }
    return result;
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Jfs1;

/// <summary>
/// Walks a JFS1 image written by <see cref="Jfs1Writer"/> and yields its
/// on-disk extents for purge + defrag.
/// </summary>
internal static class Jfs1ExtentMap {
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
    var sb = Jfs1Superblock.TryParse(bytes);
    if (!sb.Valid) return result;
    var bsize = (int)sb.BlockSize;
    if (bsize <= 0) bsize = Jfs1Writer.DefaultBlockSize;
    // Block 0 = superblock; block 1.. = inode table; then data.
    result.Add(new DefragBlockInfo(0, bsize, DefragBlockKind.MetadataReserved, "superblock"));
    try {
      image.Position = 0;
      var reader = new Jfs1Reader(image);
      // The inode table is as many blocks as the inode count needs, not one: the
      // writer records that count in s_inostamp. Assuming a single block put the
      // root directory a block early, so a wipe zeroed the real one and the
      // volume came back empty.
      var inodeBlocks = (int)Math.Max(1, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x18)));
      result.Add(new DefragBlockInfo(bsize, (long)inodeBlocks * bsize,
        DefragBlockKind.MetadataReserved, "inode_table"));
      // Root dir block follows the inode table.
      var rootDirOffset = (long)(1 + inodeBlocks) * bsize;
      if (rootDirOffset < image.Length)
        result.Add(new DefragBlockInfo(rootDirOffset, bsize, DefragBlockKind.MetadataReserved, "root_dir"));
      foreach (var e in reader.Entries) {
        if (e.FirstBlock == 0) continue;
        var blocks = e.IsDirectory ? 1 : (int)((e.Size + bsize - 1) / bsize);
        var len = (long)blocks * bsize;
        result.Add(new DefragBlockInfo((long)e.FirstBlock * bsize, len,
          e.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
          e.IsDirectory ? null : e.Name));
      }
    } catch { /* tolerate */ }
    return result;
  }
}

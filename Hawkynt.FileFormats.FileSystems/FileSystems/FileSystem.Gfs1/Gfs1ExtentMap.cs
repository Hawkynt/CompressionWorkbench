#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Gfs1;

/// <summary>
/// Walks a Sistina GFS1 image written by <see cref="Gfs1Writer"/> and yields
/// its on-disk extents.
/// </summary>
internal static class Gfs1ExtentMap {
  internal static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    // The head carries the superblock and inode table; the walk goes through the
    // reader, which streams. Buffering the whole volume capped the wipe at the
    // array limit and returned nothing when it threw -- and an empty extent list
    // reads as "the volume is entirely free".
    var head = new byte[(int)Math.Min(image.Length, 4 * 1024 * 1024)];
    image.ReadExactly(head, 0, head.Length);
    image.Position = 0;
    return EnumerateBytes(head, image);
  }

  private static List<DefragBlockInfo> EnumerateBytes(byte[] bytes, Stream image) {
    var result = new List<DefragBlockInfo>();
    var sb = Gfs1Superblock.TryParse(bytes);
    if (!sb.Valid) return result;
    // 0..65536 = boot/reserved; 65536..65536+BlockSize = superblock.
    result.Add(new DefragBlockInfo(0, Gfs1Writer.SuperblockOffset, DefragBlockKind.MetadataReserved, "boot"));
    result.Add(new DefragBlockInfo(Gfs1Writer.SuperblockOffset, Gfs1Writer.BlockSize, DefragBlockKind.MetadataReserved, "superblock"));
    var inodeStart = Gfs1Writer.SuperblockOffset + Gfs1Writer.BlockSize;
    try {
      image.Position = 0;
      var reader = new Gfs1Reader(image);
      // Everything between the superblock and the first block any file occupies
      // is the volume's own: the inode table and the root directory, whose sizes
      // are not recorded anywhere a reader can consult. Claiming a fixed one
      // block for the table and one for the root left the rest looking free, and
      // a wipe zeroed it — on a volume of twenty-eight files that cost every
      // file at once while the file data itself was never touched.
      //
      // Counting the inodes currently present does not work either: the table is
      // sized when the volume is written and does not shrink when files are
      // removed, so a count taken afterwards puts the root directory a block
      // short of where it is. The first file block is a fact about the volume;
      // an inode count is a fact about its history.
      var firstFileBlock = long.MaxValue;
      foreach (var e in reader.Entries)
        if (e.FirstBlock > 0 && e.FirstBlock < firstFileBlock) firstFileBlock = e.FirstBlock;

      var ownEnd = firstFileBlock == long.MaxValue
        ? inodeStart + Gfs1Writer.BlockSize          // nothing stored; the table is all there is
        : firstFileBlock * Gfs1Writer.BlockSize;
      if (ownEnd > inodeStart && ownEnd <= image.Length)
        result.Add(new DefragBlockInfo(inodeStart, ownEnd - inodeStart,
          DefragBlockKind.MetadataReserved, "inode_table"));

      foreach (var e in reader.Entries) {
        if (e.FirstBlock == 0) continue;
        var blocks = e.IsDirectory ? 1
          : (int)((e.Size + Gfs1Writer.BlockSize - 1) / Gfs1Writer.BlockSize);
        var len = (long)blocks * Gfs1Writer.BlockSize;
        result.Add(new DefragBlockInfo((long)e.FirstBlock * Gfs1Writer.BlockSize, len,
          e.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
          e.IsDirectory ? null : e.Name));
      }
    } catch { /* tolerate */ }
    return result;
  }
}
